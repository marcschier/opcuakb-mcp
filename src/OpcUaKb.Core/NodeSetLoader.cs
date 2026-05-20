using System.Net;
using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;

// ═══════════════════════════════════════════════════════════════════════
// NodeSetLoader — resolves a NodeSet XML payload from one of three input
// modes used by validate_nodeset / check_compliance (and any future tools).
//
//   1. Inline   `nodeset_xml`  — string, hard-capped at 30 KB so the LLM
//                                can actually emit it inside tool-call args.
//
//   2. Stored   `nodeset_ref`  — opaque reference resolved server-side.
//                                Currently supports `blob:{path}` pointing
//                                at the same blob container the pipeline
//                                writes NodeSets to (and that the upload
//                                endpoint stores user uploads in).
//                                The `opcfoundation:{spec}@{ver}` and
//                                `cloudlib:{ns}@{ver}` schemes are reserved
//                                for a later iteration.
//
//   3. Fetched  `nodeset_url`  — https URL with a host allow-list. Bounded
//                                read (default 50 MB), 60 s timeout, single
//                                retry on 429/503. Redirects are explicitly
//                                disabled (SSRF defense).
//
// Returns a `NodeSetSource` whose `OpenAsync()` delegate produces a fresh
// readable Stream each call. URL fetches buffer once into memory so the
// network only happens once; blob and inline modes open cheaply each time.
// Callers MUST dispose each returned Stream.
//
// Required env (HTTP transport) — only used when nodeset_ref is set:
//   STORAGE_ACCOUNT_NAME            — same account the pipeline uses.
//                                     The system MI must have
//                                     Storage Blob Data Contributor.
//
// Optional env:
//   MCP_NODESET_CONTAINER           — default `opcua-content`
//   MCP_NODESET_MAX_BYTES           — default 52428800 (50 MB)
//   MCP_NODESET_URL_ALLOWLIST       — comma-separated host patterns;
//                                     `*` is a wildcard prefix. Defaults
//                                     cover opcfoundation.org and GitHub
//                                     raw content hosts.
// ═══════════════════════════════════════════════════════════════════════

sealed public class NodeSetLoader
{
    public const int MaxInlineBytes = 30 * 1024;
    public const long DefaultMaxFetchBytes = 50L * 1024 * 1024;

    static readonly string[] DefaultAllowList =
    [
        "*.opcfoundation.org",
        "raw.githubusercontent.com",
        "objects.githubusercontent.com",
    ];

    readonly HttpClient _fetchClient;
    readonly BlobContainerClient? _container;
    readonly long _maxBytes;
    readonly string[] _allowList;

    public NodeSetLoader(HttpClient fetchClient, BlobContainerClient? container = null)
    {
        _fetchClient = fetchClient;
        _container = container;
        _maxBytes = long.TryParse(
            Environment.GetEnvironmentVariable("MCP_NODESET_MAX_BYTES"), out var m) && m > 0
                ? m : DefaultMaxFetchBytes;

        var raw = Environment.GetEnvironmentVariable("MCP_NODESET_URL_ALLOWLIST");
        _allowList = string.IsNullOrWhiteSpace(raw)
            ? DefaultAllowList
            : [.. raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    /// <summary>
    /// Default factory using <see cref="DefaultAzureCredential"/> against the
    /// storage account named by STORAGE_ACCOUNT_NAME. The HttpClient used for
    /// outbound URL fetches has automatic redirects disabled to prevent SSRF
    /// past the allow-list. When STORAGE_ACCOUNT_NAME isn't set, only inline
    /// and URL modes are available.
    /// </summary>
    public static NodeSetLoader CreateDefault()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        var fetchClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };

        var accountName = Environment.GetEnvironmentVariable("STORAGE_ACCOUNT_NAME");
        BlobContainerClient? container = null;
        if (!string.IsNullOrWhiteSpace(accountName))
        {
            var containerName = Environment.GetEnvironmentVariable("MCP_NODESET_CONTAINER") ?? "opcua-content";
            var serviceUri = new Uri($"https://{accountName}.blob.core.windows.net");
            container = new BlobServiceClient(serviceUri, new DefaultAzureCredential())
                .GetBlobContainerClient(containerName);
        }
        return new NodeSetLoader(fetchClient, container);
    }

    /// <summary>
    /// Resolves exactly one of the three inputs and returns a source whose
    /// <see cref="NodeSetSource.OpenAsync"/> delegate produces a fresh
    /// readable Stream each call. Throws <see cref="NodeSetLoadException"/>
    /// for any validation or fetch failure so tools can return it verbatim.
    /// </summary>
    public async Task<NodeSetSource> ResolveAsync(
        string? nodesetXml,
        string? nodesetRef,
        string? nodesetUrl,
        CancellationToken ct = default)
    {
        var present =
            (string.IsNullOrWhiteSpace(nodesetXml) ? 0 : 1) +
            (string.IsNullOrWhiteSpace(nodesetRef) ? 0 : 1) +
            (string.IsNullOrWhiteSpace(nodesetUrl) ? 0 : 1);

        if (present == 0)
            throw new NodeSetLoadException(
                "Provide exactly one of: nodeset_xml (inline, ≤30KB), nodeset_ref (e.g. 'blob:uploads/abc.xml'), or nodeset_url (https://).");
        if (present > 1)
            throw new NodeSetLoadException(
                "Provide only one of nodeset_xml / nodeset_ref / nodeset_url, not multiple.");

        if (!string.IsNullOrWhiteSpace(nodesetXml))
            return SourceFromInline(nodesetXml);

        if (!string.IsNullOrWhiteSpace(nodesetRef))
            return SourceFromRef(nodesetRef.Trim());

        return await SourceFromUrlAsync(nodesetUrl!.Trim(), ct);
    }

    static NodeSetSource SourceFromInline(string xml)
    {
        var bytes = Encoding.UTF8.GetByteCount(xml);
        if (bytes > MaxInlineBytes)
            throw new NodeSetLoadException(
                $"Inline nodeset_xml is {bytes / 1024} KB which exceeds the {MaxInlineBytes / 1024} KB limit. " +
                "Pass a 'nodeset_url' or upload via POST /upload-nodeset and pass the returned 'nodeset_ref' instead.");

        var buffer = Encoding.UTF8.GetBytes(xml);
        return new NodeSetSource(
            opener: () => Task.FromResult<Stream>(new MemoryStream(buffer, writable: false)),
            description: "inline nodeset_xml");
    }

    NodeSetSource SourceFromRef(string @ref)
    {
        if (!@ref.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
            throw new NodeSetLoadException(
                $"Unsupported nodeset_ref scheme '{@ref.Split(':', 2)[0]}'. " +
                "Supported: 'blob:{path}' (path within the configured blob container).");

        if (_container == null)
            throw new NodeSetLoadException(
                "nodeset_ref support is disabled because STORAGE_ACCOUNT_NAME is not configured on this MCP server.");

        var path = @ref["blob:".Length..].TrimStart('/');
        if (string.IsNullOrWhiteSpace(path))
            throw new NodeSetLoadException("nodeset_ref 'blob:' scheme requires a non-empty path.");

        // Defense in depth — `..` segments must never escape the container root.
        if (path.Contains("..", StringComparison.Ordinal))
            throw new NodeSetLoadException("nodeset_ref path must not contain '..' segments.");

        var blob = _container.GetBlobClient(path);
        var maxBytes = _maxBytes;

        return new NodeSetSource(
            opener: async () =>
            {
                try
                {
                    var props = await blob.GetPropertiesAsync();
                    if (props.Value.ContentLength > maxBytes)
                        throw new NodeSetLoadException(
                            $"Blob '{path}' is {props.Value.ContentLength / 1024} KB which exceeds the {maxBytes / 1024} KB limit.");
                    var stream = await blob.OpenReadAsync();
                    return new SizeBoundedStream(stream, maxBytes);
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    throw new NodeSetLoadException($"Blob '{path}' was not found.");
                }
                catch (RequestFailedException ex)
                {
                    throw new NodeSetLoadException($"Blob '{path}' could not be read: {ex.Status} {ex.ErrorCode}.");
                }
            },
            description: $"blob:{path}");
    }

    async Task<NodeSetSource> SourceFromUrlAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new NodeSetLoadException(
                $"nodeset_url must be an absolute https:// URL. Got: {url}");
        }

        if (!IsHostAllowed(uri.Host))
            throw new NodeSetLoadException(
                $"Host '{uri.Host}' is not in the allow-list. " +
                "Configure MCP_NODESET_URL_ALLOWLIST or use the upload endpoint.");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(60));

        // SSRF defense: AllowAutoRedirect=false on the configured HttpClient.
        // 3xx is treated as failure — callers must pass the final URL.
        HttpResponseMessage response;
        try
        {
            response = await SendWithRetryAsync(uri, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new NodeSetLoadException($"Timed out fetching {uri.Host}.");
        }
        catch (HttpRequestException ex)
        {
            throw new NodeSetLoadException($"Could not fetch {uri.Host}: {ex.Message}");
        }

        try
        {
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                throw new NodeSetLoadException(
                    $"Upstream returned HTTP {(int)response.StatusCode} (redirect). " +
                    "Pass the final URL directly; redirects are not followed.");
            }

            if (!response.IsSuccessStatusCode)
                throw new NodeSetLoadException(
                    $"Upstream returned HTTP {(int)response.StatusCode} {response.ReasonPhrase} for {uri.Host}.");

            if (response.Content.Headers.ContentLength is long len && len > _maxBytes)
                throw new NodeSetLoadException(
                    $"Remote NodeSet is {len / 1024} KB which exceeds the {_maxBytes / 1024} KB limit.");

            // Buffer to memory bounded by max bytes. This kills three concerns
            // in one shot: (a) header pass + node pass share one fetch,
            // (b) XmlReader read time isn't bound by network latency,
            // (c) we don't hold a connection open between passes.
            var bounded = new SizeBoundedStream(await response.Content.ReadAsStreamAsync(cts.Token), _maxBytes);
            var buffer = new MemoryStream();
            await bounded.CopyToAsync(buffer, cts.Token);
            buffer.Position = 0;

            var bytes = buffer.ToArray();
            return new NodeSetSource(
                opener: () => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false)),
                description: $"url:{uri.Host}{uri.AbsolutePath}");
        }
        finally
        {
            response.Dispose();
        }
    }

    async Task<HttpResponseMessage> SendWithRetryAsync(Uri uri, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.UserAgent.ParseAdd("OpcUaKb.McpServer/4.0 (+https://github.com/marcschier/OpcUaKb)");
            req.Headers.Accept.ParseAdd("application/xml, text/xml, application/octet-stream;q=0.5");

            var response = await _fetchClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

            if ((response.StatusCode is HttpStatusCode.TooManyRequests
                or HttpStatusCode.ServiceUnavailable) && attempt == 0)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
                response.Dispose();
                await Task.Delay(retryAfter, ct);
                continue;
            }
            return response;
        }
    }

    bool IsHostAllowed(string host)
    {
        foreach (var pattern in _allowList)
        {
            if (pattern.StartsWith("*.", StringComparison.Ordinal))
            {
                var suffix = pattern[1..]; // ".opcfoundation.org"
                if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else if (string.Equals(host, pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}

/// <summary>
/// A resolved NodeSet payload exposed as a re-openable stream source.
/// Callers may invoke <see cref="OpenAsync"/> multiple times to perform
/// multiple parsing passes; for URL inputs the underlying bytes are
/// fetched only once and buffered in memory.
/// </summary>
public sealed class NodeSetSource
{
    readonly Func<Task<Stream>> _opener;
    public string Description { get; }

    internal NodeSetSource(Func<Task<Stream>> opener, string description)
    {
        _opener = opener;
        Description = description;
    }

    public Task<Stream> OpenAsync() => _opener();
}

public sealed class NodeSetLoadException(string message) : Exception(message);

/// <summary>
/// Wraps a stream and aborts the read past <c>maxBytes</c>. When
/// <paramref name="leaveInnerOpen"/> is true the inner stream isn't disposed.
/// Exposes <see cref="BytesRead"/> so callers can report exact size after
/// a streaming copy without buffering.
/// </summary>
public sealed class SizeBoundedStream : Stream
{
    readonly Stream _inner;
    readonly long _max;
    readonly bool _leaveInnerOpen;
    long _read;

    public SizeBoundedStream(Stream inner, long maxBytes, bool leaveInnerOpen = false)
    {
        _inner = inner;
        _max = maxBytes;
        _leaveInnerOpen = leaveInnerOpen;
    }

    /// <summary>Bytes successfully delivered to the caller so far.</summary>
    public long BytesRead => _read;

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _read; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        // Same async-bridge rationale as HashingStream — Kestrel forbids
        // sync IO on the request body, but Azure SDK calls Read sync from
        // its internal buffering path.
        return ReadAsync(buffer.AsMemory(offset, count), default).AsTask().GetAwaiter().GetResult();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        EnforceLimit();
        var allowed = (int)Math.Min(buffer.Length, _max - _read);
        var read = await _inner.ReadAsync(buffer[..Math.Min(allowed + 1, buffer.Length)], ct);
        _read += read;
        EnforceLimit();
        return read;
    }

    void EnforceLimit()
    {
        if (_read > _max)
            throw new NodeSetLoadException($"NodeSet exceeds the {_max / 1024} KB limit.");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveInnerOpen) _inner.Dispose();
        base.Dispose(disposing);
    }
}

