using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;

// ═══════════════════════════════════════════════════════════════════════
// OPC UA Knowledge Base Pipeline — Crawl + Index
// Designed to run as an Azure Container Apps scheduled job.
// Emits structured JSON telemetry for Log Analytics dashboard.
// ═══════════════════════════════════════════════════════════════════════

using var loggerFactory = LoggerFactory.Create(b =>
{
    b.AddJsonConsole(o =>
    {
        o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
        o.UseUtcTimestamp = true;
    });
    b.SetMinimumLevel(LogLevel.Information);
});
var log = loggerFactory.CreateLogger("Pipeline");

var storageConnStr = Require("STORAGE_CONNECTION_STRING");
var searchEndpoint = Require("SEARCH_ENDPOINT");
var searchApiKey   = Require("SEARCH_API_KEY");
var aoaiEndpoint   = Require("AOAI_ENDPOINT");
var credential     = new DefaultAzureCredential();

var statusTracker = new PipelineStatusTracker(
    new BlobContainerClient(storageConnStr, "opcua-content"),
    loggerFactory.CreateLogger<PipelineStatusTracker>());

var sw = Stopwatch.StartNew();
var exitCode = 0;

try
{
    // ── Phase 1: Crawl ─────────────────────────────────────────────────
    log.LogInformation("[PIPELINE] Phase={Phase} Status={Status}", "crawl", "started");
    await statusTracker.UpdateAsync("crawl", "running");

    var crawler = new OpcUaCrawler(storageConnStr, loggerFactory.CreateLogger<OpcUaCrawler>(), statusTracker);
    await crawler.RunAsync();

    log.LogInformation("[PIPELINE] Phase={Phase} Status={Status} ElapsedSec={Elapsed}",
        "crawl", "completed", (int)sw.Elapsed.TotalSeconds);
    await statusTracker.UpdateAsync("crawl", "completed", elapsedSec: (int)sw.Elapsed.TotalSeconds);

    // ── Phase 2: Index ─────────────────────────────────────────────────
    log.LogInformation("[PIPELINE] Phase={Phase} Status={Status}", "index", "started");
    await statusTracker.UpdateAsync("index", "running");

    var indexSw = Stopwatch.StartNew();
    var indexer = new OpcUaIndexer(
        storageConnStr, searchEndpoint, searchApiKey,
        aoaiEndpoint, credential,
        loggerFactory.CreateLogger<OpcUaIndexer>(), statusTracker);
    await indexer.RunAsync();

    log.LogInformation("[PIPELINE] Phase={Phase} Status={Status} ElapsedSec={Elapsed}",
        "index", "completed", (int)indexSw.Elapsed.TotalSeconds);
    await statusTracker.UpdateAsync("index", "completed",
        elapsedSec: (int)indexSw.Elapsed.TotalSeconds);

    // ── Phase 3: Parse NodeSets ─────────────────────────────────────────
    log.LogInformation("[PIPELINE] Phase={Phase} Status={Status}", "nodeset", "started");
    await statusTracker.UpdateAsync("nodeset", "running");

    var nodesetSw = Stopwatch.StartNew();
    var nodesetParser = new OpcUaNodeSetParser(storageConnStr, loggerFactory.CreateLogger<OpcUaNodeSetParser>());
    var nodesetDocs = await nodesetParser.ParseAllAsync();
    log.LogInformation("[PIPELINE] Phase={Phase} Docs={Count}", "nodeset", nodesetDocs.Count);

    // Shared search client for uploading nodeset + cloudlib docs
    var searchClient = new SearchIndexClient(new Uri(searchEndpoint), new AzureKeyCredential(searchApiKey))
        .GetSearchClient("opcua-content-index");

    if (nodesetDocs.Count > 0)
    {
        // Generate summary documents for aggregation queries
        var summaryDocs = nodesetParser.GenerateSummaries(nodesetDocs);
        log.LogInformation("[PIPELINE] Phase={Phase} Summaries={Count}", "nodeset-summary", summaryDocs.Count);

        // Combine nodeset + summary docs for upload
        var allNodesetDocs = nodesetDocs.Concat(summaryDocs).ToList();

        // Tag all opcfoundation docs with source and top popularity
        foreach (var doc in allNodesetDocs)
        {
            doc["source"] = "opcfoundation";
            doc["popularity"] = 1_000_000_000L;
            doc["in_opcfoundation_index"] = true;
        }

        // Upload nodeset docs to search index WITHOUT pre-computing embeddings.
        log.LogInformation("[PIPELINE] Phase={Phase} Status={Status} Docs={Count}", "nodeset-upload", "started", allNodesetDocs.Count);
        int nodesetUploaded = 0;
        for (int i = 0; i < allNodesetDocs.Count; i += 100)
        {
            var batch = allNodesetDocs.Skip(i).Take(100).ToList();
            try
            {
                await RetryHelper.RetrySearchAsync(async () =>
                {
                    await searchClient.IndexDocumentsAsync(IndexDocumentsBatch.Upload(batch));
                    return true;
                }, log);
                nodesetUploaded += batch.Count;
                if (nodesetUploaded % 1000 == 0)
                    log.LogInformation("[NODESET] Uploaded={N} Total={T}", nodesetUploaded, allNodesetDocs.Count);
            }
            catch (Exception ex)
            {
                log.LogWarning("[NODESET] Phase=upload Error={Error} BatchStart={I}", ex.Message, i);
            }
        }

        log.LogInformation("[PIPELINE] Phase={Phase} Status={Status} Uploaded={U}",
            "nodeset", "completed", nodesetUploaded);
    }

    // Collect opcfoundation namespace URIs (used by cloudlib phase to detect duplicates).
    // Case-insensitive and normalized (trim trailing slash) so cloudlib comparisons are reliable.
    var opcfNamespaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var d in nodesetDocs)
    {
        if (d.TryGetValue("namespace_uri", out var n) && n is string s && !string.IsNullOrEmpty(s))
            opcfNamespaces.Add(s.TrimEnd('/'));
    }
    log.LogInformation("[PIPELINE] OpcFoundation namespaces collected={Count}", opcfNamespaces.Count);

    // Phase 3b: CloudLibrary NodeSets (optional — only if credentials provided)
    var cloudLib = CloudLibraryClient.TryCreate(storageConnStr, log);
    if (cloudLib != null)
    {
        log.LogInformation("[PIPELINE] Phase={Phase} Status={Status}", "cloudlib", "started");
        await statusTracker.UpdateAsync("cloudlib", "running");

        var cloudLibEntries = await cloudLib.DownloadAllNodeSetsAsync();
        log.LogInformation("[CLOUDLIB] Downloaded {Count} NodeSet entries", cloudLibEntries.Count);

        if (cloudLibEntries.Count > 0)
        {
            var cloudLibBlobNames = cloudLibEntries.Select(e => e.BlobName).ToList();

            // Index entries by blob name for quick overlay of metadata onto parsed docs
            var metaByNs = cloudLibEntries
                .Where(e => !string.IsNullOrEmpty(e.NamespaceUri))
                .GroupBy(e => e.NamespaceUri)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            // Parse CloudLibrary NodeSets using the same parser
            var cloudParser = new OpcUaNodeSetParser(storageConnStr, loggerFactory.CreateLogger<OpcUaNodeSetParser>());
            var cloudDocs = await cloudParser.ParseBlobsAsync(cloudLibBlobNames);
            log.LogInformation("[CLOUDLIB] Parsed {Count} NodeSet documents", cloudDocs.Count);

            // Generate summaries BEFORE mutating content_type — GenerateSummaries filters by
            // content_type == "nodeset", so it must run while docs still have the original tag.
            var cloudSummaries = cloudParser.GenerateSummaries(cloudDocs);
            log.LogInformation("[CLOUDLIB] Generated {Count} summary documents", cloudSummaries.Count);

            // Tag all cloudlib docs with distinct content_type + overlay API metadata
            foreach (var doc in cloudDocs)
            {
                var ct = doc.TryGetValue("content_type", out var v) ? v?.ToString() ?? "" : "";
                doc["content_type"] = ct switch
                {
                    "nodeset" => "cloudlib_nodeset",
                    "nodeset_summary" => "cloudlib_summary",
                    "nodeset_hierarchy" => "cloudlib_hierarchy",
                    _ => $"cloudlib_{ct}",
                };

                // Look up metadata by namespace_uri (emitted by NodeSetParser on every doc)
                var nsUri = doc.TryGetValue("namespace_uri", out var n) ? n?.ToString() ?? "" : "";

                // Flag whether this namespace already exists in the crawled opcfoundation index
                var inOpcfoundation = !string.IsNullOrEmpty(nsUri)
                    && opcfNamespaces.Contains(nsUri.TrimEnd('/'));
                doc["in_opcfoundation_index"] = inOpcfoundation;
                // Keep source=opcfoundation for specs that are also in the crawled index;
                // only tag as cloudlib for specs that exist ONLY in the UA CloudLibrary.
                doc["source"] = inOpcfoundation ? "opcfoundation" : "cloudlib";
                doc["popularity"] = inOpcfoundation ? 1_000_000_000L : 0L; // default; overwritten below if metadata matches

                if (string.IsNullOrEmpty(nsUri) || !metaByNs.TryGetValue(nsUri, out var meta))
                    continue;

                doc["title"] = meta.Title;
                doc["description"] = meta.Description;
                doc["popularity"] = meta.NumberOfDownloads;
                if (!string.IsNullOrEmpty(meta.Version))
                    doc["spec_version"] = meta.Version;
                if (meta.PublicationDate.HasValue)
                    doc["publication_date"] = meta.PublicationDate.Value;
            }

            // Compute is_latest / version_rank across CloudLib versions of the same namespace
            // Group by namespace_uri, order by publication_date desc, rank 1 = latest
            var cloudByNs = cloudDocs
                .Where(d => d.TryGetValue("namespace_uri", out var n) && !string.IsNullOrEmpty(n?.ToString()))
                .GroupBy(d => d["namespace_uri"]?.ToString() ?? "");

            foreach (var group in cloudByNs)
            {
                // Order by publication_date desc (null last)
                var ordered = group
                    .OrderByDescending(d => d.TryGetValue("publication_date", out var p) && p is DateTimeOffset dto ? dto : DateTimeOffset.MinValue)
                    .ThenByDescending(d => d.TryGetValue("spec_version", out var v) ? v?.ToString() ?? "" : "")
                    .ToList();

                // Assign rank per distinct spec_version within the namespace
                int rank = 0;
                string? prevVersion = null;
                foreach (var d in ordered)
                {
                    var ver = d.TryGetValue("spec_version", out var v) ? v?.ToString() ?? "" : "";
                    if (ver != prevVersion) { rank++; prevVersion = ver; }
                    d["version_rank"] = rank;
                    d["is_latest"] = rank == 1;
                }
            }

            // Generate summaries for CloudLibrary nodesets (already generated above before mutation)
            foreach (var doc in cloudSummaries)
            {
                var ct = doc.TryGetValue("content_type", out var v) ? v?.ToString() ?? "" : "";
                doc["content_type"] = ct switch
                {
                    "nodeset" => "cloudlib_nodeset",
                    "nodeset_summary" => "cloudlib_summary",
                    "nodeset_hierarchy" => "cloudlib_hierarchy",
                    var x when x.StartsWith("cloudlib_") => x,
                    _ => $"cloudlib_{ct}",
                };

                // Enrich summary page_chunk with the CloudLib title + description if we have matching metadata
                var spec = doc.TryGetValue("spec_part", out var sp) ? sp?.ToString() ?? "" : "";
                var firstForSpec = cloudDocs
                    .FirstOrDefault(d => d.TryGetValue("spec_part", out var p) && p?.ToString() == spec
                                      && d.TryGetValue("title", out _));
                if (firstForSpec != null)
                {
                    var title = firstForSpec.TryGetValue("title", out var t) ? t?.ToString() ?? "" : "";
                    var desc = firstForSpec.TryGetValue("description", out var de) ? de?.ToString() ?? "" : "";
                    var nsUri = firstForSpec.TryGetValue("namespace_uri", out var n) ? n?.ToString() ?? "" : "";
                    var version = firstForSpec.TryGetValue("spec_version", out var vv) ? vv?.ToString() ?? "" : "";
                    var pubDate = firstForSpec.TryGetValue("publication_date", out var pd) && pd is DateTimeOffset dto
                        ? dto.ToString("yyyy-MM-dd") : "";

                    doc["title"] = title;
                    doc["description"] = desc;
                    doc["namespace_uri"] = nsUri;
                    if (!string.IsNullOrEmpty(version)) doc["spec_version"] = version;
                    if (firstForSpec.TryGetValue("publication_date", out var pdv) && pdv is DateTimeOffset d2)
                        doc["publication_date"] = d2;
                    if (firstForSpec.TryGetValue("popularity", out var pop)) doc["popularity"] = pop!;
                    if (firstForSpec.TryGetValue("in_opcfoundation_index", out var inIdx)) doc["in_opcfoundation_index"] = inIdx!;

                    // Prefix page_chunk with human-readable metadata so text search can find it
                    var existing = doc.TryGetValue("page_chunk", out var pc) ? pc?.ToString() ?? "" : "";
                    var header = new System.Text.StringBuilder();
                    if (!string.IsNullOrEmpty(title)) header.AppendLine($"Title: {title}");
                    if (!string.IsNullOrEmpty(nsUri)) header.AppendLine($"Namespace: {nsUri}");
                    if (!string.IsNullOrEmpty(version)) header.AppendLine($"Version: {version}");
                    if (!string.IsNullOrEmpty(pubDate)) header.AppendLine($"Published: {pubDate}");
                    header.AppendLine("Source: UA-CloudLibrary");
                    if (!string.IsNullOrEmpty(desc))
                    {
                        header.AppendLine();
                        header.AppendLine("Description:");
                        header.AppendLine(desc);
                        header.AppendLine();
                    }
                    doc["page_chunk"] = header.ToString() + existing;
                    // Mirror is_latest / version_rank from the representative doc
                    if (firstForSpec.TryGetValue("is_latest", out var il)) doc["is_latest"] = il!;
                    if (firstForSpec.TryGetValue("version_rank", out var vr)) doc["version_rank"] = vr!;
                }

                // Resolve source/popularity from the representative doc's namespace.
                // If the namespace was also crawled from reference.opcfoundation.org, surface
                // the summary as an opcfoundation spec so list_specs source=opcfoundation
                // returns it. Falls back to cloudlib when firstForSpec is null or has no namespace.
                var summaryNsUri = firstForSpec != null
                    && firstForSpec.TryGetValue("namespace_uri", out var nu)
                    ? nu?.ToString() ?? "" : "";
                var inOpcfoundation = !string.IsNullOrEmpty(summaryNsUri)
                    && opcfNamespaces.Contains(summaryNsUri.TrimEnd('/'));
                doc["in_opcfoundation_index"] = inOpcfoundation;
                doc["source"] = inOpcfoundation ? "opcfoundation" : "cloudlib";
                doc["popularity"] = inOpcfoundation ? 1_000_000_000L : 0L;
            }

            var allCloudDocs = cloudDocs.Concat(cloudSummaries).ToList();
            log.LogInformation("[CLOUDLIB] Uploading {Count} docs to index", allCloudDocs.Count);

            int cloudUploaded = 0;
            for (int i = 0; i < allCloudDocs.Count; i += 100)
            {
                var batch = allCloudDocs.Skip(i).Take(100).ToList();
                try
                {
                    await RetryHelper.RetrySearchAsync(async () =>
                    {
                        await searchClient.IndexDocumentsAsync(IndexDocumentsBatch.Upload(batch));
                        return true;
                    }, log);
                    cloudUploaded += batch.Count;
                    if (cloudUploaded % 1000 == 0)
                        log.LogInformation("[CLOUDLIB] Uploaded={N} Total={T}", cloudUploaded, allCloudDocs.Count);
                }
                catch (Exception ex)
                {
                    log.LogWarning("[CLOUDLIB] Phase=upload Error={Error} BatchStart={I}", ex.Message, i);
                }
            }

            log.LogInformation("[PIPELINE] Phase={Phase} Status={Status} Uploaded={U} Expected={E} Entries={N}",
                "cloudlib", "completed", cloudUploaded, allCloudDocs.Count, cloudLibEntries.Count);
            if (cloudUploaded < allCloudDocs.Count)
                log.LogWarning("[CLOUDLIB] Upload shortfall: uploaded {Uploaded} of {Expected} docs",
                    cloudUploaded, allCloudDocs.Count);
        }
        else
        {
            log.LogWarning("[CLOUDLIB] No entries returned from CloudLibrary API — check credentials or API availability");
        }
        await statusTracker.UpdateAsync("cloudlib", "completed");
    }

    await statusTracker.UpdateAsync("nodeset", "completed",
        elapsedSec: (int)nodesetSw.Elapsed.TotalSeconds);

    log.LogInformation("[PIPELINE] Phase={Phase} Status={Status} TotalElapsedSec={Elapsed}",
        "pipeline", "completed", (int)sw.Elapsed.TotalSeconds);
    await statusTracker.UpdateAsync("pipeline", "completed",
        elapsedSec: (int)sw.Elapsed.TotalSeconds);
}
catch (Exception ex)
{
    log.LogError(ex, "[PIPELINE] Phase={Phase} Status={Status} Error={Error}",
        statusTracker.CurrentPhase, "failed", ex.Message);
    await statusTracker.UpdateAsync(statusTracker.CurrentPhase, "failed", error: ex.Message);
    exitCode = 1;
}

return exitCode;

string Require(string name) =>
    Environment.GetEnvironmentVariable(name)
    ?? throw new InvalidOperationException($"Missing environment variable: {name}");

// ═══════════════════════════════════════════════════════════════════════
// Pipeline Status Tracker — writes _pipeline-status.json to blob storage
// ═══════════════════════════════════════════════════════════════════════

sealed class PipelineStatusTracker
{
    static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };
    readonly BlobContainerClient _container;
    readonly ILogger _log;
    PipelineStatus _status = new();

    public string CurrentPhase => _status.CurrentPhase;

    public PipelineStatusTracker(BlobContainerClient container, ILogger logger)
    {
        _container = container;
        _log = logger;
    }

    public async Task UpdateAsync(string phase, string status,
        int? downloaded = null, int? skipped = null, int? errors = null,
        int? queued = null, int? htmlBlobs = null, int? chunks = null,
        int? embedded = null, int? indexed = null,
        int? elapsedSec = null, string? error = null)
    {
        _status.CurrentPhase = phase;
        _status.Status = status;
        _status.LastUpdated = DateTimeOffset.UtcNow;
        if (downloaded.HasValue) _status.CrawlDownloaded = downloaded.Value;
        if (skipped.HasValue) _status.CrawlSkipped = skipped.Value;
        if (errors.HasValue) _status.CrawlErrors = errors.Value;
        if (queued.HasValue) _status.CrawlQueued = queued.Value;
        if (htmlBlobs.HasValue) _status.IndexHtmlBlobs = htmlBlobs.Value;
        if (chunks.HasValue) _status.IndexChunks = chunks.Value;
        if (embedded.HasValue) _status.IndexEmbedded = embedded.Value;
        if (indexed.HasValue) _status.IndexUploaded = indexed.Value;
        if (elapsedSec.HasValue) _status.ElapsedSeconds = elapsedSec.Value;
        if (error != null) _status.LastError = error;

        try
        {
            await _container.CreateIfNotExistsAsync();
            var blob = _container.GetBlobClient("_pipeline-status.json");
            var json = JsonSerializer.SerializeToUtf8Bytes(_status, s_json);
            using var ms = new MemoryStream(json);
            await blob.UploadAsync(ms, overwrite: true);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Could not write status: {Msg}", ex.Message);
        }
    }
}

sealed class PipelineStatus
{
    public string CurrentPhase { get; set; } = "init";
    public string Status { get; set; } = "pending";
    public DateTimeOffset LastUpdated { get; set; } = DateTimeOffset.UtcNow;
    public int ElapsedSeconds { get; set; }
    public int CrawlDownloaded { get; set; }
    public int CrawlSkipped { get; set; }
    public int CrawlErrors { get; set; }
    public int CrawlQueued { get; set; }
    public int IndexHtmlBlobs { get; set; }
    public int IndexChunks { get; set; }
    public int IndexEmbedded { get; set; }
    public int IndexUploaded { get; set; }
    public string? LastError { get; set; }
}

// ═══════════════════════════════════════════════════════════════════════
// Crawler
// ═══════════════════════════════════════════════════════════════════════

sealed class OpcUaCrawler : IDisposable
{
    const string BaseUrl = "https://reference.opcfoundation.org/";
    const string PrimaryHost = "reference.opcfoundation.org";
    const string FullCrawlHost = "profiles.opcfoundation.org";
    const string AllowedDomain = ".opcfoundation.org";
    const string ContainerName = "opcua-content";
    const string CrawlStateBlob = "_crawl-state.json";
    const int MaxConcurrency = 5;
    const int DelayMs = 200;
    const int RecrawlHours = 24;

    static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };
    static readonly HashSet<string> s_imgExts = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp", ".ico" };

    readonly BlobContainerClient _container;
    readonly HttpClient _http;
    readonly SemaphoreSlim _throttle = new(MaxConcurrency);
    readonly ConcurrentDictionary<string, byte> _queued = new(StringComparer.OrdinalIgnoreCase);
    readonly ILogger _log;
    readonly PipelineStatusTracker _tracker;
    Dictionary<string, DateTimeOffset> _crawled = new(StringComparer.OrdinalIgnoreCase);
    int _downloaded, _skipped, _errors;

    public OpcUaCrawler(string connectionString, ILogger logger, PipelineStatusTracker tracker)
    {
        _log = logger;
        _tracker = tracker;
        _container = new BlobContainerClient(connectionString, ContainerName);
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = MaxConcurrency,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AutomaticDecompression = System.Net.DecompressionMethods.All
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("OpcUaKb-Crawler/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,*/*;q=0.8");
    }

    public async Task RunAsync()
    {
        await _container.CreateIfNotExistsAsync();
        await LoadStateAsync();
        _log.LogInformation("Crawl state: {Count} previously crawled URLs", _crawled.Count);

        var queue = new ConcurrentQueue<(string url, bool isPage, bool followLinks)>();
        Enqueue(queue, BaseUrl, true, true);

        // Process the main page synchronously up front so that ExtractLinks has
        // populated the queue (and _queued) with per-spec version URLs before we
        // probe older versions below.
        if (queue.TryDequeue(out var seed))
            await ProcessAsync(seed.url, seed.isPage, seed.followLinks, queue);

        // The main page only links each spec's latest few versions, so older
        // versions (e.g., DI v101) are unreachable via normal link-following.
        // HEAD-probe them explicitly and enqueue any that exist.
        await ProbeOlderVersionsAsync(queue);

        while (!queue.IsEmpty)
        {
            var batch = new List<(string url, bool isPage, bool followLinks)>();
            while (batch.Count < MaxConcurrency * 2 && queue.TryDequeue(out var item))
                batch.Add(item);

            await Task.WhenAll(batch.Select(item => ProcessAsync(item.url, item.isPage, item.followLinks, queue)));

            if (_downloaded > 0 && _downloaded % 50 == 0)
                await SaveStateAsync();
        }

        await SaveStateAsync();
        _log.LogInformation("[CRAWL] Status=completed Downloaded={D} Skipped={S} Errors={E}",
            _downloaded, _skipped, _errors);
        await _tracker.UpdateAsync("crawl", "completed",
            downloaded: _downloaded, skipped: _skipped, errors: _errors, queued: 0);
    }

    // Per-spec version probing: derive (spec, latestVersion) tuples from URLs
    // already discovered on the main page, then HEAD-probe v100..(latest-1) for
    // each spec. Capped at 20 versions per spec to keep this bounded. Successful
    // probes are enqueued for normal page-crawling so they get indexed.
    async Task ProbeOlderVersionsAsync(ConcurrentQueue<(string, bool, bool)> queue)
    {
        var rx = new Regex(
            @"https?://reference\.opcfoundation\.org/(?<spec>[^/]+(?:/[^/]+)?)/v(?<ver>\d{3,4})[a-z]?/",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var bySpec = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var url in _queued.Keys)
        {
            var m = rx.Match(url);
            if (!m.Success) continue;
            var spec = m.Groups["spec"].Value;
            if (int.TryParse(m.Groups["ver"].Value, out var v))
            {
                if (!bySpec.TryGetValue(spec, out var cur) || v > cur)
                    bySpec[spec] = v;
            }
        }

        _log.LogInformation("[CRAWL] Status=probe_older_start Specs={N}", bySpec.Count);
        var discovered = 0;
        var probeTasks = new List<Task>();
        foreach (var (spec, latest) in bySpec)
        {
            // Cap at 20 attempts per spec, never probe below v100.
            var floor = Math.Max(100, latest - 20);
            for (var v = floor; v < latest; v++)
            {
                var probeUrl = $"https://reference.opcfoundation.org/{spec}/v{v:D3}/docs/";
                probeTasks.Add(ProbeOneAsync(probeUrl, queue, () => Interlocked.Increment(ref discovered)));
            }
        }
        await Task.WhenAll(probeTasks);
        _log.LogInformation("[CRAWL] Status=probe_older_done Discovered={N}", discovered);
    }

    async Task ProbeOneAsync(string url, ConcurrentQueue<(string, bool, bool)> queue, Action onSuccess)
    {
        await _throttle.WaitAsync();
        try
        {
            await Task.Delay(50);
            using var head = new HttpRequestMessage(HttpMethod.Head, url);
            using var resp = await _http.SendAsync(head);
            if (resp.IsSuccessStatusCode)
            {
                _log.LogInformation("[CRAWL] Discovered older version: {Url}", url);
                Enqueue(queue, url, true, true);
                onSuccess();
            }
        }
        catch { /* probe-only, ignore */ }
        finally { _throttle.Release(); }
    }

    async Task ProcessAsync(string url, bool isPage, bool followLinks, ConcurrentQueue<(string, bool, bool)> queue)
    {
        await _throttle.WaitAsync();
        try
        {
            if (_crawled.TryGetValue(url, out var last) &&
                DateTimeOffset.UtcNow - last < TimeSpan.FromHours(RecrawlHours))
            {
                Interlocked.Increment(ref _skipped);
                return;
            }

            await Task.Delay(DelayMs);
            var response = await RetryHelper.RetryAsync(() => _http.GetAsync(url), _log);
            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning("HTTP {Code} for {Url}", (int)response.StatusCode, url);
                Interlocked.Increment(ref _errors);
                return;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync();
            var blobName = UrlToBlobName(url, response.Content.Headers.ContentType?.MediaType);
            var blobClient = _container.GetBlobClient(blobName);
            using var ms = new MemoryStream(bytes);
            await blobClient.UploadAsync(ms, new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders
                {
                    ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream"
                }
            });

            _crawled[url] = DateTimeOffset.UtcNow;
            Interlocked.Increment(ref _downloaded);

            if (isPage && followLinks && (response.Content.Headers.ContentType?.MediaType?.Contains("html") == true))
            {
                var html = Encoding.UTF8.GetString(bytes);
                ExtractLinks(html, url, queue);
            }

            if (_downloaded % 25 == 0)
            {
                _log.LogInformation("[CRAWL] Downloaded={D} Skipped={S} Errors={E} Queued={Q}",
                    _downloaded, _skipped, _errors, queue.Count);
                if (_downloaded % 100 == 0)
                    await _tracker.UpdateAsync("crawl", "running",
                        downloaded: _downloaded, skipped: _skipped,
                        errors: _errors, queued: queue.Count);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning("[CRAWL] Error={Error} Url={Url}", ex.Message, url);
            Interlocked.Increment(ref _errors);
        }
        finally
        {
            _throttle.Release();
        }
    }

    void ExtractLinks(string html, string baseUrl, ConcurrentQueue<(string, bool, bool)> queue)
    {
        try
        {
            var parser = new HtmlParser();
            var doc = parser.ParseDocument(html);
            var baseUri = new Uri(baseUrl);

            foreach (var a in doc.QuerySelectorAll("a[href]"))
            {
                var href = a.GetAttribute("href");
                if (string.IsNullOrWhiteSpace(href) || href.StartsWith('#') || href.StartsWith("javascript:"))
                    continue;
                if (Uri.TryCreate(baseUri, href, out var resolved) &&
                    IsAllowedDomain(resolved.Host))
                    Enqueue(queue, resolved.GetLeftPart(UriPartial.Path), true, ShouldFollowLinks(resolved.Host));
            }

            foreach (var img in doc.QuerySelectorAll("img[src], link[href], script[src]"))
            {
                var src = img.GetAttribute("src") ?? img.GetAttribute("href");
                if (string.IsNullOrWhiteSpace(src)) continue;
                if (Uri.TryCreate(baseUri, src, out var resolved) &&
                    IsAllowedDomain(resolved.Host))
                    Enqueue(queue, resolved.GetLeftPart(UriPartial.Query), false, false);
            }
        }
        catch { /* non-fatal */ }
    }

    static bool IsAllowedDomain(string host) =>
        host.EndsWith(AllowedDomain, StringComparison.OrdinalIgnoreCase) ||
        host.Equals("opcfoundation.org", StringComparison.OrdinalIgnoreCase);

    // Full crawl (follow links) for primary + profiles; depth-1 only for other subdomains
    static bool ShouldFollowLinks(string host) =>
        host.Equals(PrimaryHost, StringComparison.OrdinalIgnoreCase) ||
        host.Equals(FullCrawlHost, StringComparison.OrdinalIgnoreCase);

    void Enqueue(ConcurrentQueue<(string, bool, bool)> queue, string url, bool isPage, bool followLinks)
    {
        if (_queued.TryAdd(url, 0))
            queue.Enqueue((url, isPage, followLinks));
    }

    static string UrlToBlobName(string url, string? contentType)
    {
        var uri = new Uri(url);
        // Prefix with host for non-reference domains to avoid path collisions
        var prefix = uri.Host.Equals("reference.opcfoundation.org", StringComparison.OrdinalIgnoreCase)
            ? "" : uri.Host.Replace(".", "_") + "/";
        var path = uri.AbsolutePath.TrimStart('/');
        if (string.IsNullOrEmpty(path) || path.EndsWith('/'))
            path += "index.html";
        // Append .xml for API nodeset endpoints that serve XML
        if (contentType?.Contains("xml") == true && !path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            path += ".xml";
        // Append .html for HTML responses whose URL has no file extension
        // (e.g., /DI/v102/docs/1). Without this, the indexer's name-based .html
        // filter silently drops these blobs and older spec versions go unindexed.
        if (contentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true
            && !HasFileExtension(path))
            path += ".html";
        return prefix + path;
    }

    static bool HasFileExtension(string path)
    {
        var lastSlash = path.LastIndexOf('/');
        var fileName = lastSlash >= 0 ? path[(lastSlash + 1)..] : path;
        return fileName.Contains('.');
    }

    async Task LoadStateAsync()
    {
        try
        {
            var blob = _container.GetBlobClient(CrawlStateBlob);
            if (await blob.ExistsAsync())
            {
                var dl = await blob.DownloadContentAsync();
                var state = dl.Value.Content.ToObjectFromJson<Dictionary<string, DateTimeOffset>>(s_json);
                if (state != null) _crawled = new Dictionary<string, DateTimeOffset>(state, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) { _log.LogWarning("Could not load crawl state: {Msg}", ex.Message); }
    }

    async Task SaveStateAsync()
    {
        try
        {
            var blob = _container.GetBlobClient(CrawlStateBlob);
            var json = JsonSerializer.SerializeToUtf8Bytes(_crawled, s_json);
            using var ms = new MemoryStream(json);
            await blob.UploadAsync(ms, overwrite: true);
        }
        catch (Exception ex) { _log.LogWarning("Could not save crawl state: {Msg}", ex.Message); }
    }

    public void Dispose() => _http.Dispose();
}

// ═══════════════════════════════════════════════════════════════════════
// Indexer
// ═══════════════════════════════════════════════════════════════════════

sealed class OpcUaIndexer
{
    const string IndexName = "opcua-content-index";
    const string ContainerName = "opcua-content";
    const string EmbeddingDeployment = "text-embedding-3-large";
    const int EmbeddingDimensions = 3072;
    const int ChunkSize = 512;
    const int ChunkOverlap = 50;
    const int EmbeddingBatchSize = 16;
    const int UploadBatchSize = 100;

    readonly SearchIndexClient _indexClient;
    readonly SearchClient _searchClient;
    readonly BlobContainerClient _container;
    readonly HttpClient _http;
    readonly TokenCredential _credential;
    readonly string _aoaiEndpoint;
    readonly string _storageConn;
    readonly ILogger _log;
    readonly PipelineStatusTracker _tracker;
    VersionCatalog _versionCatalog = null!;

    public OpcUaIndexer(string storageConn, string searchEndpoint, string searchApiKey,
        string aoaiEndpoint, TokenCredential credential, ILogger logger, PipelineStatusTracker tracker)
    {
        _log = logger;
        _tracker = tracker;
        _aoaiEndpoint = aoaiEndpoint;
        _storageConn = storageConn;
        _credential = credential;
        _indexClient = new SearchIndexClient(new Uri(searchEndpoint), new AzureKeyCredential(searchApiKey));
        _searchClient = _indexClient.GetSearchClient(IndexName);
        _container = new BlobContainerClient(storageConn, ContainerName);
        _http = new HttpClient();
    }

    public async Task RunAsync()
    {
        await EnsureIndexAsync();
        _log.LogInformation("[INDEX] Status=index_ready Index={Name}", IndexName);

        // Build version catalog from crawled main page
        _versionCatalog = await VersionCatalog.BuildFromCrawledPageAsync(_storageConn, _log);

        var htmlBlobs = new List<string>();
        await foreach (var item in _container.GetBlobsAsync())
        {
            // Defense-in-depth pairing with the crawler's UrlToBlobName fix:
            // accept blobs by either the .html name suffix or a text/html
            // Content-Type. This ensures previously crawled blobs that were
            // saved without an .html extension (e.g., /DI/v102/docs/1) still
            // get indexed.
            var ct = item.Properties?.ContentType ?? "";
            if (item.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                || ct.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
            {
                htmlBlobs.Add(item.Name);
            }
        }
        _log.LogInformation("[INDEX] HtmlBlobs={Count}", htmlBlobs.Count);
        await _tracker.UpdateAsync("index", "running", htmlBlobs: htmlBlobs.Count);
        if (htmlBlobs.Count == 0) return;

        var allDocs = new List<SearchDocument>();
        var parser = new HtmlParser();
        int processed = 0;

        foreach (var blobName in htmlBlobs)
        {
            processed++;
            try
            {
                var dl = await _container.GetBlobClient(blobName).DownloadContentAsync();
                var html = dl.Value.Content.ToString();
                var sourceUrl = $"https://reference.opcfoundation.org/{blobName.Replace('\\', '/')}";
                var (part, ver) = ExtractSpecInfo(blobName);
                var versionEntry = _versionCatalog.Lookup(blobName);
                var doc = await parser.ParseDocumentAsync(html);
                allDocs.AddRange(ChunkDocument(doc, part, ver, sourceUrl,
                    versionEntry?.IsLatest ?? true, versionEntry?.Rank ?? 1));

                if (processed % 50 == 0)
                {
                    _log.LogInformation("[INDEX] Phase=chunking Parsed={N} Total={T} Chunks={C}",
                        processed, htmlBlobs.Count, allDocs.Count);
                    await _tracker.UpdateAsync("index", "running",
                        htmlBlobs: htmlBlobs.Count, chunks: allDocs.Count);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning("[INDEX] Phase=chunking Error={Error} Blob={Blob}", ex.Message, blobName);
            }
        }
        _log.LogInformation("[INDEX] Phase=chunking Status=completed Chunks={Count}", allDocs.Count);
        await _tracker.UpdateAsync("index", "running", chunks: allDocs.Count);

        // Embeddings
        _log.LogInformation("[INDEX] Phase=embedding Total={Count}", allDocs.Count);
        var sem = new SemaphoreSlim(2);
        int embDone = 0;
        for (int i = 0; i < allDocs.Count; i += EmbeddingBatchSize)
        {
            var batch = allDocs.Skip(i).Take(EmbeddingBatchSize).ToList();
            await sem.WaitAsync();
            try
            {
                var texts = batch.Select(d => (string)d["page_chunk"]).ToList();
                var vectors = await GetEmbeddingsAsync(texts);
                for (int j = 0; j < batch.Count && j < vectors.Count; j++)
                    batch[j]["page_chunk_vector"] = vectors[j];
                embDone += batch.Count;
                if (embDone % 100 == 0)
                {
                    _log.LogInformation("[INDEX] Phase=embedding Embedded={N} Total={T}", embDone, allDocs.Count);
                    await _tracker.UpdateAsync("index", "running", embedded: embDone);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning("[INDEX] Phase=embedding Error={Error} BatchStart={I}", ex.Message, i);
            }
            finally { sem.Release(); }
        }
        _log.LogInformation("[INDEX] Phase=embedding Status=completed Embedded={N}", embDone);
        await _tracker.UpdateAsync("index", "running", embedded: embDone);

        // Upload
        _log.LogInformation("[INDEX] Phase=upload Total={Count}", allDocs.Count);
        int uploaded = 0;
        for (int i = 0; i < allDocs.Count; i += UploadBatchSize)
        {
            var batch = allDocs.Skip(i).Take(UploadBatchSize).ToList();
            try
            {
                await RetryHelper.RetrySearchAsync(
                    () => _searchClient.IndexDocumentsAsync(IndexDocumentsBatch.Upload(batch)), _log);
                uploaded += batch.Count;
                if (uploaded % 500 == 0)
                {
                    _log.LogInformation("[INDEX] Phase=upload Uploaded={N} Total={T}", uploaded, allDocs.Count);
                    await _tracker.UpdateAsync("index", "running", indexed: uploaded);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning("[INDEX] Phase=upload Error={Error} BatchStart={I}", ex.Message, i);
            }
        }
        _log.LogInformation("[INDEX] Phase=upload Status=completed Indexed={N}", uploaded);
        await _tracker.UpdateAsync("index", "completed", indexed: uploaded);
    }

    async Task EnsureIndexAsync()
    {
        var index = new SearchIndex(IndexName)
        {
            Description = "OPC UA reference specification content chunks with vector embeddings.",
            Fields =
            {
                new SimpleField("id", SearchFieldDataType.String) { IsKey = true, IsFilterable = true },
                new SearchableField("page_chunk") { AnalyzerName = LexicalAnalyzerName.EnMicrosoft },
                new SearchField("page_chunk_vector", SearchFieldDataType.Collection(SearchFieldDataType.Single))
                {
                    IsSearchable = true, VectorSearchDimensions = EmbeddingDimensions, VectorSearchProfileName = "hnsw-embedding"
                },
                new SimpleField("source_url", SearchFieldDataType.String) { IsFilterable = true },
                new SimpleField("spec_part", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
                new SimpleField("spec_version", SearchFieldDataType.String) { IsFilterable = true },
                new SearchableField("section_title"),
                new SimpleField("content_type", SearchFieldDataType.String) { IsFilterable = true },
                new SimpleField("chunk_index", SearchFieldDataType.Int32) { IsSortable = true },
                new SimpleField("node_class", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
                new SimpleField("modelling_rule", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
                new SimpleField("browse_name", SearchFieldDataType.String) { IsFilterable = true },
                new SimpleField("parent_type", SearchFieldDataType.String) { IsFilterable = true },
                new SimpleField("data_type", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
                new SimpleField("is_latest", SearchFieldDataType.Boolean) { IsFilterable = true },
                new SimpleField("version_rank", SearchFieldDataType.Int32) { IsFilterable = true, IsSortable = true },
                new SimpleField("source", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
                new SimpleField("namespace_uri", SearchFieldDataType.String) { IsFilterable = true },
                new SimpleField("publication_date", SearchFieldDataType.DateTimeOffset) { IsFilterable = true, IsSortable = true },
                new SearchableField("title"),
                new SearchableField("description") { AnalyzerName = LexicalAnalyzerName.EnMicrosoft },
                new SimpleField("popularity", SearchFieldDataType.Int64) { IsFilterable = true, IsSortable = true },
                new SimpleField("in_opcfoundation_index", SearchFieldDataType.Boolean) { IsFilterable = true, IsFacetable = true },
            },
            ScoringProfiles =
            {
                new ScoringProfile("popularity_boost")
                {
                    Functions =
                    {
                        new MagnitudeScoringFunction("popularity", 5.0,
                            new MagnitudeScoringParameters(1, 1_000_000) { ShouldBoostBeyondRangeByConstant = true })
                        { Interpolation = ScoringFunctionInterpolation.Logarithmic }
                    }
                }
            },
            DefaultScoringProfile = "popularity_boost",
            SemanticSearch = new SemanticSearch
            {
                DefaultConfigurationName = "semantic_config",
                Configurations =
                {
                    new SemanticConfiguration("semantic_config", new SemanticPrioritizedFields
                    {
                        TitleField = new SemanticField("section_title"),
                        ContentFields = { new SemanticField("page_chunk"), new SemanticField("description") },
                        KeywordsFields = { new SemanticField("title") }
                    })
                }
            },
            VectorSearch = new VectorSearch
            {
                Algorithms = { new HnswAlgorithmConfiguration("alg") { Parameters = new HnswParameters { Metric = VectorSearchAlgorithmMetric.Cosine } } },
                Profiles = { new VectorSearchProfile("hnsw-embedding", "alg") { VectorizerName = "aoai-vectorizer" } },
                Vectorizers =
                {
                    new AzureOpenAIVectorizer("aoai-vectorizer")
                    {
                        Parameters = new AzureOpenAIVectorizerParameters
                        {
                            ResourceUri = new Uri(_aoaiEndpoint),
                            DeploymentName = EmbeddingDeployment,
                            ModelName = EmbeddingDeployment
                        }
                    }
                }
            }
        };
        await _indexClient.CreateOrUpdateIndexAsync(index);
    }

    List<SearchDocument> ChunkDocument(AngleSharp.Dom.IDocument doc, string specPart, string specVersion,
        string sourceUrl, bool isLatest, int versionRank)
    {
        var results = new List<SearchDocument>();
        var body = doc.Body;
        if (body == null) return results;

        string currentHeading = doc.Title ?? specPart;
        var blocks = new List<(string heading, string text, string type)>();

        foreach (var el in body.QuerySelectorAll("h1,h2,h3,h4,h5,h6,p,pre,code,li,td,th,figcaption,dt,dd"))
        {
            var tag = el.TagName.ToLowerInvariant();
            if (tag.StartsWith('h') && tag.Length == 2) { currentHeading = el.TextContent.Trim(); continue; }
            var text = el.TextContent.Trim();
            if (text.Length < 10) continue;
            blocks.Add((currentHeading, text, tag is "td" or "th" ? "table" : "text"));
        }

        foreach (var table in body.QuerySelectorAll("table"))
        {
            var heading = table.PreviousElementSibling?.TagName.StartsWith("H") == true
                ? table.PreviousElementSibling.TextContent.Trim() : currentHeading;
            var md = TableToMarkdown(table);
            if (md.Length > 20) blocks.Add((heading, md, "table"));
        }

        foreach (var img in body.QuerySelectorAll("img[src]"))
        {
            var alt = img.GetAttribute("alt") ?? "";
            var src = img.GetAttribute("src") ?? "";
            var cap = img.Closest("figure")?.QuerySelector("figcaption")?.TextContent ?? "";
            var ctx = $"[Image: {alt}] {cap} (src: {src})";
            if (ctx.Length > 20) blocks.Add((currentHeading, ctx, "image"));
        }

        int chunkIdx = 0;
        var buf = new StringBuilder();
        string bufHeading = currentHeading, bufType = "text";

        foreach (var (heading, text, type) in blocks)
        {
            if (buf.Length / 4 + text.Length / 4 > ChunkSize && buf.Length > 0)
            {
                results.Add(MakeDoc(buf.ToString(), bufHeading, bufType, sourceUrl, specPart, specVersion, chunkIdx++, isLatest, versionRank));
                var words = buf.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                buf.Clear();
                if (words.Length > ChunkOverlap)
                    buf.Append(string.Join(' ', words[^ChunkOverlap..]));
            }
            bufHeading = heading; bufType = type;
            if (buf.Length > 0) buf.Append('\n');
            buf.Append(text);
        }
        if (buf.Length > 0)
            results.Add(MakeDoc(buf.ToString(), bufHeading, bufType, sourceUrl, specPart, specVersion, chunkIdx, isLatest, versionRank));

        return results;
    }

    static SearchDocument MakeDoc(string text, string heading, string contentType,
        string sourceUrl, string specPart, string specVersion, int chunkIdx,
        bool isLatest = true, int versionRank = 1)
    {
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{sourceUrl}:{chunkIdx}")))[..32].ToLowerInvariant();
        return new SearchDocument(new Dictionary<string, object>
        {
            ["id"] = id, ["page_chunk"] = text, ["source_url"] = sourceUrl,
            ["spec_part"] = specPart, ["spec_version"] = specVersion,
            ["section_title"] = heading, ["content_type"] = contentType, ["chunk_index"] = chunkIdx,
            ["is_latest"] = isLatest, ["version_rank"] = versionRank,
            ["source"] = "opcfoundation",
            ["popularity"] = 1_000_000_000L,
            ["in_opcfoundation_index"] = true,
        });
    }

    async Task<List<float[]>> GetEmbeddingsAsync(List<string> texts)
    {
        var body = JsonSerializer.Serialize(new { input = texts, model = EmbeddingDeployment });
        var resp = await RetryHelper.RetryAsync(
            async () =>
            {
                var token = await _credential.GetTokenAsync(
                    new TokenRequestContext(new[] { "https://cognitiveservices.azure.com/.default" }),
                    default);
                var clone = new HttpRequestMessage(HttpMethod.Post,
                    $"{_aoaiEndpoint}/openai/deployments/{EmbeddingDeployment}/embeddings?api-version=2024-06-01")
                { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                clone.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);
                return await _http.SendAsync(clone);
            }, _log);
        resp.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
        return json["data"]!.AsArray()
            .Select(d => d!["embedding"]!.AsArray().Select(v => v!.GetValue<float>()).ToArray())
            .ToList();
    }

    static (string part, string version) ExtractSpecInfo(string blobName) =>
        VersionCatalog.ExtractSpecInfoFromPath(blobName);

    static string TableToMarkdown(IElement table)
    {
        var sb = new StringBuilder();
        foreach (var row in table.QuerySelectorAll("tr"))
        {
            var cells = row.QuerySelectorAll("th, td");
            sb.AppendLine("| " + string.Join(" | ", cells.Select(c => c.TextContent.Trim().Replace("|", "\\|"))) + " |");
        }
        return sb.ToString();
    }
}

// ═══════════════════════════════════════════════════════════════════════
// Retry Helper — exponential backoff for HTTP 429 / 503
// ═══════════════════════════════════════════════════════════════════════

static class RetryHelper
{
    const int MaxRetries = 5;

    static TimeSpan ComputeDelay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.TryGetValues("Retry-After", out var values))
        {
            var raw = values.FirstOrDefault();
            if (int.TryParse(raw, out var secs))
                return TimeSpan.FromSeconds(secs);
            if (DateTimeOffset.TryParse(raw, out var date))
            {
                var delta = date - DateTimeOffset.UtcNow;
                if (delta > TimeSpan.Zero) return delta;
            }
        }
        var baseSec = Math.Pow(2, attempt); // 1, 2, 4, 8, 16
        var jitter = baseSec * (0.75 + Random.Shared.NextDouble() * 0.5); // ±25%
        return TimeSpan.FromSeconds(jitter);
    }

    public static async Task<HttpResponseMessage> RetryAsync(
        Func<Task<HttpResponseMessage>> action, ILogger log)
    {
        for (int attempt = 0; ; attempt++)
        {
            var response = await action();
            if (response.StatusCode is not ((System.Net.HttpStatusCode)429 or System.Net.HttpStatusCode.ServiceUnavailable))
                return response;

            if (attempt >= MaxRetries)
                return response;

            var delay = ComputeDelay(response, attempt);
            log.LogWarning("[RETRY] StatusCode={Code} Attempt={Attempt}/{Max} DelayMs={DelayMs}",
                (int)response.StatusCode, attempt + 1, MaxRetries, (int)delay.TotalMilliseconds);
            response.Dispose();
            await Task.Delay(delay);
        }
    }

    public static async Task<T> RetrySearchAsync<T>(
        Func<Task<T>> action, ILogger log)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (RequestFailedException ex) when (
                ex.Status is 429 or 503 && attempt < MaxRetries)
            {
                var baseSec = Math.Pow(2, attempt);
                var jitter = baseSec * (0.75 + Random.Shared.NextDouble() * 0.5);
                var delay = TimeSpan.FromSeconds(jitter);
                log.LogWarning("[RETRY] SearchRequestFailed StatusCode={Code} Attempt={Attempt}/{Max} DelayMs={DelayMs}",
                    ex.Status, attempt + 1, MaxRetries, (int)delay.TotalMilliseconds);
                await Task.Delay(delay);
            }
        }
    }
}
