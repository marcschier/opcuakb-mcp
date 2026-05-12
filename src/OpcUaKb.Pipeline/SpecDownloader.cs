using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;

// ═══════════════════════════════════════════════════════════════════════
// Spec Downloader — for each (spec, version) discovered by SpecCatalog,
// pulls the three canonical artifacts into blob storage:
//
//   • Single Page HTML  → html/{SpecId}-{Version}.html
//   • STS XML           → sts-xml/{SpecId}-{Version}.xml   (if available)
//   • Markdown          → markdown/{SpecId}-{Version}.md   (if available)
//
// JSONL is intentionally skipped — upstream titles are broken (user-verified).
//
// Idempotent: blobs are tagged with an `upstream_url` metadata field and
// re-downloaded only if the URL changed or the cached copy is older than
// 7 days. Concurrency is throttled at the VERSION level (5 versions in
// flight); within a version, the three files are fetched sequentially.
// ═══════════════════════════════════════════════════════════════════════

sealed class SpecDownloader
{
    const string ContainerName = "opcua-content";
    const int MaxConcurrency = 5;
    static readonly TimeSpan FreshnessWindow = TimeSpan.FromDays(7);

    readonly HttpClient _http;
    readonly BlobContainerClient _container;
    readonly ILogger _log;
    readonly SemaphoreSlim _throttle = new(MaxConcurrency);

    int _downloaded;
    int _skipped;
    int _errors;
    int _totalFiles;

    public SpecDownloader(HttpClient http, BlobServiceClient blobs, ILogger log)
    {
        _http = http;
        _container = blobs.GetBlobContainerClient(ContainerName);
        _log = log;
    }

    public async Task<(int Downloaded, int Skipped, int Errors)> DownloadAsync(
        IEnumerable<VersionRef> versions, CancellationToken ct = default)
    {
        await _container.CreateIfNotExistsAsync(cancellationToken: ct);

        var list = versions as IReadOnlyCollection<VersionRef> ?? versions.ToList();
        _totalFiles = list.Count * 3;
        _log.LogInformation(
            "[SPEC_DOWNLOAD] Phase=start Versions={N} TotalFiles={Total}",
            list.Count, _totalFiles);

        var tasks = list.Select(v => ProcessVersionAsync(v, ct)).ToArray();
        await Task.WhenAll(tasks);

        _log.LogInformation(
            "[SPEC_DOWNLOAD] Phase=complete Downloaded={D} Skipped={S} Errors={E} TotalFiles={Total}",
            _downloaded, _skipped, _errors, _totalFiles);

        return (_downloaded, _skipped, _errors);
    }

    async Task ProcessVersionAsync(VersionRef v, CancellationToken ct)
    {
        await _throttle.WaitAsync(ct);
        try
        {
            await DownloadOneAsync(
                v, v.FullViewUrl,
                $"html/{v.SpecId}-{v.Version}.html",
                "text/html; charset=utf-8", ct);

            if (!string.IsNullOrEmpty(v.StsXmlUrl))
            {
                await DownloadOneAsync(
                    v, v.StsXmlUrl,
                    $"sts-xml/{v.SpecId}-{v.Version}.xml",
                    "application/xml; charset=utf-8", ct);
            }
            else
            {
                Interlocked.Increment(ref _skipped);
            }

            if (!string.IsNullOrEmpty(v.MarkdownUrl))
            {
                await DownloadOneAsync(
                    v, v.MarkdownUrl,
                    $"markdown/{v.SpecId}-{v.Version}.md",
                    "text/markdown; charset=utf-8", ct);
            }
            else
            {
                Interlocked.Increment(ref _skipped);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                "[SPEC_DOWNLOAD] Spec={Spec} Version={Version} Error={Error}",
                v.SpecId, v.Version, ex.Message);
            Interlocked.Increment(ref _errors);
        }
        finally
        {
            _throttle.Release();
        }
    }

    async Task DownloadOneAsync(
        VersionRef v, string url, string blobName, string contentType, CancellationToken ct)
    {
        // Defensive absolutization. SpecCatalog should already deliver absolute
        // URLs but historical regressions (and any future caller) have shipped
        // relative hrefs straight from anchor tags; HttpClient.GetAsync rejects
        // those with "An invalid request URI was provided." Wrap once here so
        // a single broken extractor can't take out the entire SPEC_DOWNLOAD phase.
        if (!string.IsNullOrEmpty(url) && url.StartsWith('/'))
            url = "https://reference.opcfoundation.org" + url;

        try
        {
            var blobClient = _container.GetBlobClient(blobName);

            if (await IsFreshAsync(blobClient, url, ct))
            {
                Interlocked.Increment(ref _skipped);
                LogProgress();
                return;
            }

            using var response = await RetryHelper.RetryAsync(
                () => _http.GetAsync(url, ct), _log);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _log.LogWarning(
                    "[SPEC_DOWNLOAD] HTTP=404 Spec={Spec} Version={Version} Url={Url}",
                    v.SpecId, v.Version, url);
                Interlocked.Increment(ref _errors);
                LogProgress();
                return;
            }

            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning(
                    "[SPEC_DOWNLOAD] HTTP={Code} Spec={Spec} Version={Version} Url={Url}",
                    (int)response.StatusCode, v.SpecId, v.Version, url);
                Interlocked.Increment(ref _errors);
                LogProgress();
                return;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            using var ms = new MemoryStream(bytes);

            var options = new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = contentType },
                Metadata = new Dictionary<string, string>
                {
                    ["upstream_url"] = url,
                    ["spec_id"] = v.SpecId,
                    ["version"] = v.Version,
                },
            };

            await blobClient.UploadAsync(ms, options, ct);
            Interlocked.Increment(ref _downloaded);
            LogProgress();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                "[SPEC_DOWNLOAD] Spec={Spec} Version={Version} Url={Url} Error={Error}",
                v.SpecId, v.Version, url, ex.Message);
            Interlocked.Increment(ref _errors);
            LogProgress();
        }
    }

    static async Task<bool> IsFreshAsync(BlobClient blob, string url, CancellationToken ct)
    {
        var existsResp = await blob.ExistsAsync(ct);
        if (!existsResp.Value) return false;

        try
        {
            var props = (await blob.GetPropertiesAsync(cancellationToken: ct)).Value;
            if (!props.Metadata.TryGetValue("upstream_url", out var cachedUrl)) return false;
            if (!string.Equals(cachedUrl, url, StringComparison.Ordinal)) return false;
            return DateTimeOffset.UtcNow - props.LastModified < FreshnessWindow;
        }
        catch
        {
            return false;
        }
    }

    void LogProgress()
    {
        var total = Interlocked.CompareExchange(ref _downloaded, 0, 0)
                  + Interlocked.CompareExchange(ref _skipped, 0, 0)
                  + Interlocked.CompareExchange(ref _errors, 0, 0);
        if (total > 0 && total % 50 == 0)
        {
            _log.LogInformation(
                "[SPEC_DOWNLOAD] Downloaded={D} Skipped={S} Errors={E} TotalFiles={Total}",
                _downloaded, _skipped, _errors, _totalFiles);
        }
    }
}
