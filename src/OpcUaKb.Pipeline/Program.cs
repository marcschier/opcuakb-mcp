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
// OPC UA Knowledge Base Pipeline — module-based orchestration.
//
// Phases (in order):
//   1. spec_discovery   — SpecCatalog: root listing + per-spec landings
//   2. spec_download    — SpecDownloader: html/, sts-xml/, markdown/ blobs
//   3. image_fetch      — ImageFetcher: figure binaries (/img/{sha}.png)
//   4. github_fetch     — GitHubFetcher: UA-Nodeset + companion spec repos
//   5. nodeset_parse    — OpcUaNodeSetParser over api/nodesets/ + nodesets/
//   6. spec_estimate    — SpecIndexer.EstimateAsync (preflight)
//   7. spec_index       — SpecIndexer.IndexAsync (HTML+STS → embed → upload)
//   8. cloudlib         — UA-CloudLibrary nodesets (LAST so opcf namespace
//                         coverage is complete for source tagging)
//
// All search uploads target opcua-content-index-v2 (blue-green migration).
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

var storageAccountName = Require("STORAGE_ACCOUNT_NAME");
var searchEndpoint = Require("SEARCH_ENDPOINT");
var searchApiKey   = Require("SEARCH_API_KEY");
var aoaiEndpoint   = Require("AOAI_ENDPOINT");
var githubToken    = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
var environment    = Environment.GetEnvironmentVariable("ENVIRONMENT") ?? "Development";

if (string.IsNullOrEmpty(githubToken))
{
    if (string.Equals(environment, "Production", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException(
            "GITHUB_TOKEN is required in Production but was not set.");
    log.LogWarning(
        "[PIPELINE] GITHUB_TOKEN not set — anonymous GitHub quota is 60 req/hr. "
        + "OK for local dev, NOT acceptable in production.");
}

var credential = new DefaultAzureCredential();

// Single BlobServiceClient using DefaultAzureCredential — the storage
// account has shared-key auth disabled, so all access flows through the
// pipeline job's system-assigned managed identity (Storage Blob Data
// Contributor role granted in main.bicep).
var blobs = new BlobServiceClient(
    new Uri($"https://{storageAccountName}.blob.core.windows.net"),
    credential);

const string IndexNameV2 = "opcua-content-index-v2";

// Shared HttpClient — never recreate inside loops (auth headers + sockets).
var sharedHandler = new SocketsHttpHandler
{
    MaxConnectionsPerServer = 10,
    PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    AutomaticDecompression = System.Net.DecompressionMethods.All,
};
using var http = new HttpClient(sharedHandler) { Timeout = TimeSpan.FromMinutes(2) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("OpcUaKb-Pipeline/2.0");
http.DefaultRequestHeaders.Accept.ParseAdd("*/*");

var statusTracker = new PipelineStatusTracker(
    blobs.GetBlobContainerClient("opcua-content"),
    loggerFactory.CreateLogger<PipelineStatusTracker>());

var sw = Stopwatch.StartNew();
var exitCode = 0;

try
{
    // ── Phase 1: Spec Discovery ───────────────────────────────────────
    var phaseSw = await BeginPhaseAsync("spec_discovery");
    var catalog = new SpecCatalog(http, loggerFactory.CreateLogger<SpecCatalog>());
    var specs = await catalog.DiscoverAllSpecsAsync();
    log.LogInformation("[PIPELINE] Phase=spec_discovery Specs={N}", specs.Count);

    var landingsSem = new SemaphoreSlim(5);
    var landingTasks = specs.Select(async spec =>
    {
        await landingsSem.WaitAsync();
        try { return await catalog.GetLandingAsync(spec.SpecId); }
        catch (Exception ex)
        {
            log.LogWarning("[PIPELINE] Phase=spec_discovery Spec={Spec} Error={Error}",
                spec.SpecId, ex.Message);
            return null;
        }
        finally { landingsSem.Release(); }
    }).ToArray();
    var landingResults = await Task.WhenAll(landingTasks);
    var landings = landingResults.Where(l => l != null).Cast<SpecLanding>().ToList();
    var allVersions = landings.SelectMany(l => l.Versions).ToList();
    log.LogInformation(
        "[PIPELINE] Phase=spec_discovery Landings={L} Versions={V} SupplementaryFiles={S}",
        landings.Count, allVersions.Count, landings.Sum(l => l.SupplementaryFiles.Count));
    await EndPhaseAsync("spec_discovery", phaseSw);

    // ── Phase 2: Spec Download ────────────────────────────────────────
    phaseSw = await BeginPhaseAsync("spec_download");
    var downloader = new SpecDownloader(http, blobs,
        loggerFactory.CreateLogger<SpecDownloader>());
    var (specDownloaded, specSkipped, specErrors) = await downloader.DownloadAsync(allVersions);
    log.LogInformation(
        "[PIPELINE] Phase=spec_download Downloaded={D} Skipped={S} Errors={E}",
        specDownloaded, specSkipped, specErrors);
    await statusTracker.UpdateAsync("spec_download", "running",
        downloaded: specDownloaded, skipped: specSkipped, errors: specErrors);
    await EndPhaseAsync("spec_download", phaseSw);

    // ── Phase 3: Image Fetch ──────────────────────────────────────────
    phaseSw = await BeginPhaseAsync("image_fetch");
    var imageShas = await ExtractImageShasAsync(blobs, log);
    log.LogInformation("[PIPELINE] Phase=image_fetch UniqueShasReferenced={N}", imageShas.Count);
    var imageFetcher = new ImageFetcher(http, blobs,
        loggerFactory.CreateLogger<ImageFetcher>());
    var (imgDownloaded, imgSkipped, imgErrors) = await imageFetcher.FetchAsync(imageShas);
    log.LogInformation(
        "[PIPELINE] Phase=image_fetch Downloaded={D} Skipped={S} Errors={E}",
        imgDownloaded, imgSkipped, imgErrors);
    await EndPhaseAsync("image_fetch", phaseSw);

    // ── Phase 4: GitHub Fetch ─────────────────────────────────────────
    phaseSw = await BeginPhaseAsync("github_fetch");
    var ghRefs = await BuildGitHubRefsAsync(landings, blobs,
        loggerFactory.CreateLogger<StsMetadataParser>(), log);
    log.LogInformation("[PIPELINE] Phase=github_fetch DistinctRefs={N}", ghRefs.Count);
    var ghFetcher = new GitHubFetcher(http, blobs, githubToken,
        loggerFactory.CreateLogger<GitHubFetcher>());
    var (ghDownloaded, ghSkipped, ghErrors) = await ghFetcher.FetchAsync(ghRefs);
    log.LogInformation(
        "[PIPELINE] Phase=github_fetch Downloaded={D} Skipped={S} Errors={E}",
        ghDownloaded, ghSkipped, ghErrors);
    await EndPhaseAsync("github_fetch", phaseSw);

    // ── Phase 5: NodeSet Parse ────────────────────────────────────────
    phaseSw = await BeginPhaseAsync("nodeset_parse");
    var nodesetParser = new OpcUaNodeSetParser(blobs,
        loggerFactory.CreateLogger<OpcUaNodeSetParser>());
    var nodesetDocs = await nodesetParser.ParseAllAsync();
    log.LogInformation("[PIPELINE] Phase=nodeset_parse Docs={N}", nodesetDocs.Count);

    var v2SearchClient = new SearchIndexClient(
        new Uri(searchEndpoint), new AzureKeyCredential(searchApiKey))
        .GetSearchClient(IndexNameV2);

    if (nodesetDocs.Count > 0)
    {
        var summaryDocs = nodesetParser.GenerateSummaries(nodesetDocs);
        log.LogInformation(
            "[PIPELINE] Phase=nodeset_parse Summaries={N}", summaryDocs.Count);

        var allNodesetDocs = nodesetDocs.Concat(summaryDocs).ToList();

        foreach (var doc in allNodesetDocs)
        {
            doc["source"] = "opcfoundation";
            doc["popularity"] = 1_000_000_000L;
            doc["in_opcfoundation_index"] = true;
        }

        var nodesetUploaded = await UploadBatchedAsync(
            v2SearchClient, allNodesetDocs, "NODESET", log);
        log.LogInformation(
            "[PIPELINE] Phase=nodeset_parse Uploaded={U} Expected={E}",
            nodesetUploaded, allNodesetDocs.Count);
    }

    // Collect opcfoundation namespaces for the CloudLib source-tagging step
    // below. Built from the freshly parsed nodeset docs so coverage reflects
    // the GitHubFetcher's output.
    var opcfNamespaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var d in nodesetDocs)
    {
        if (d.TryGetValue("namespace_uri", out var n) && n is string s && !string.IsNullOrEmpty(s))
            opcfNamespaces.Add(s.TrimEnd('/'));
    }
    log.LogInformation("[PIPELINE] OpcFoundation namespaces collected={Count}", opcfNamespaces.Count);
    await EndPhaseAsync("nodeset_parse", phaseSw);

    // ── Phase 6: Spec Estimate (preflight) ────────────────────────────
    phaseSw = await BeginPhaseAsync("spec_estimate");
    var specIndexer = new SpecIndexer(blobs, searchEndpoint, searchApiKey,
        aoaiEndpoint, credential, http, loggerFactory.CreateLogger<SpecIndexer>());
    var estimate = await specIndexer.EstimateAsync();
    log.LogInformation(
        "[PIPELINE] Phase=spec_estimate Versions={V} EstimatedChunks={C} EstimatedTokens={T}",
        estimate.VersionCount, estimate.EstimatedChunkCount, estimate.EstimatedTokens);
    await EndPhaseAsync("spec_estimate", phaseSw);

    // ── Phase 7: Spec Index (parse + embed + upload to v2) ────────────
    phaseSw = await BeginPhaseAsync("spec_index");
    var indexResult = await specIndexer.IndexAsync();
    log.LogInformation(
        "[PIPELINE] Phase=spec_index Chunks={C} Embedded={E} Uploaded={U} Errors={Err}",
        indexResult.Chunks, indexResult.Embedded, indexResult.Uploaded, indexResult.Errors);
    await statusTracker.UpdateAsync("spec_index", "running",
        chunks: indexResult.Chunks, embedded: indexResult.Embedded,
        indexed: indexResult.Uploaded);
    await EndPhaseAsync("spec_index", phaseSw);

    // ── Phase 8: CloudLibrary (LAST — uses opcfNamespaces from above) ─
    var cloudLib = CloudLibraryClient.TryCreate(blobs, log);
    if (cloudLib != null)
    {
        phaseSw = await BeginPhaseAsync("cloudlib");

        var cloudLibEntries = await cloudLib.DownloadAllNodeSetsAsync();
        log.LogInformation("[CLOUDLIB] Downloaded {Count} NodeSet entries", cloudLibEntries.Count);

        if (cloudLibEntries.Count > 0)
        {
            var cloudLibBlobNames = cloudLibEntries.Select(e => e.BlobName).ToList();

            var metaByNs = cloudLibEntries
                .Where(e => !string.IsNullOrEmpty(e.NamespaceUri))
                .GroupBy(e => e.NamespaceUri)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var cloudParser = new OpcUaNodeSetParser(blobs,
                loggerFactory.CreateLogger<OpcUaNodeSetParser>());
            var cloudDocs = await cloudParser.ParseBlobsAsync(cloudLibBlobNames);
            log.LogInformation("[CLOUDLIB] Parsed {Count} NodeSet documents", cloudDocs.Count);

            // Summaries must be computed BEFORE we rewrite content_type, since
            // GenerateSummaries filters on content_type == "nodeset".
            var cloudSummaries = cloudParser.GenerateSummaries(cloudDocs);
            log.LogInformation("[CLOUDLIB] Generated {Count} summary documents", cloudSummaries.Count);

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

                var nsUri = doc.TryGetValue("namespace_uri", out var n) ? n?.ToString() ?? "" : "";

                var inOpcfoundation = !string.IsNullOrEmpty(nsUri)
                    && opcfNamespaces.Contains(nsUri.TrimEnd('/'));
                doc["in_opcfoundation_index"] = inOpcfoundation;
                doc["source"] = inOpcfoundation ? "opcfoundation" : "cloudlib";
                doc["popularity"] = inOpcfoundation ? 1_000_000_000L : 0L;

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

            // Compute is_latest / version_rank across CloudLib versions per namespace.
            var cloudByNs = cloudDocs
                .Where(d => d.TryGetValue("namespace_uri", out var n) && !string.IsNullOrEmpty(n?.ToString()))
                .GroupBy(d => d["namespace_uri"]?.ToString() ?? "");

            foreach (var group in cloudByNs)
            {
                var ordered = group
                    .OrderByDescending(d => d.TryGetValue("publication_date", out var p) && p is DateTimeOffset dto ? dto : DateTimeOffset.MinValue)
                    .ThenByDescending(d => d.TryGetValue("spec_version", out var v) ? v?.ToString() ?? "" : "")
                    .ToList();

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

                    var existing = doc.TryGetValue("page_chunk", out var pc) ? pc?.ToString() ?? "" : "";
                    var header = new StringBuilder();
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
                    if (firstForSpec.TryGetValue("is_latest", out var il)) doc["is_latest"] = il!;
                    if (firstForSpec.TryGetValue("version_rank", out var vr)) doc["version_rank"] = vr!;
                }

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
            log.LogInformation("[CLOUDLIB] Uploading {Count} docs to {Index}",
                allCloudDocs.Count, IndexNameV2);

            var cloudUploaded = await UploadBatchedAsync(
                v2SearchClient, allCloudDocs, "CLOUDLIB", log);

            log.LogInformation(
                "[PIPELINE] Phase=cloudlib Uploaded={U} Expected={E} Entries={N}",
                cloudUploaded, allCloudDocs.Count, cloudLibEntries.Count);
            if (cloudUploaded < allCloudDocs.Count)
                log.LogWarning("[CLOUDLIB] Upload shortfall: uploaded {Uploaded} of {Expected} docs",
                    cloudUploaded, allCloudDocs.Count);
        }
        else
        {
            log.LogWarning("[CLOUDLIB] No entries returned from CloudLibrary API — check credentials or API availability");
        }

        await EndPhaseAsync("cloudlib", phaseSw);
    }

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

// ── Local helpers ────────────────────────────────────────────────────

async Task<Stopwatch> BeginPhaseAsync(string phaseName)
{
    log.LogInformation("[PIPELINE] Phase={Phase} Status={Status}", phaseName, "started");
    await statusTracker.UpdateAsync(phaseName, "running");
    return Stopwatch.StartNew();
}

async Task EndPhaseAsync(string phaseName, Stopwatch phaseSw)
{
    log.LogInformation("[PIPELINE] Phase={Phase} Status={Status} Duration={Sec}s",
        phaseName, "completed", (int)phaseSw.Elapsed.TotalSeconds);
    await statusTracker.UpdateAsync(phaseName, "completed",
        elapsedSec: (int)phaseSw.Elapsed.TotalSeconds);
}

static async Task<HashSet<string>> ExtractImageShasAsync(BlobServiceClient blobs, ILogger log)
{
    // Light regex sweep over html/*.html blobs — cheap and avoids loading
    // the full DOM just to enumerate figure references.
    var container = blobs.GetBlobContainerClient("opcua-content");
    var rx = new Regex(@"/img/([a-f0-9]{64})\.png",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    var shas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    var scanned = 0;
    await foreach (var item in container.GetBlobsAsync(
        BlobTraits.None, BlobStates.None, prefix: "html/", cancellationToken: default))
    {
        if (!item.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            continue;

        try
        {
            var dl = await container.GetBlobClient(item.Name).DownloadContentAsync();
            var html = dl.Value.Content.ToString();
            foreach (Match m in rx.Matches(html))
                shas.Add(m.Groups[1].Value.ToLowerInvariant());
        }
        catch (Exception ex)
        {
            log.LogWarning("[IMAGE_FETCH] Blob={Blob} ScanError={Error}",
                item.Name, ex.Message);
        }

        scanned++;
        if (scanned % 50 == 0)
            log.LogInformation("[IMAGE_FETCH] Scanned={N} ShasFound={S}", scanned, shas.Count);
    }

    log.LogInformation("[IMAGE_FETCH] Scan complete: HtmlBlobs={N} UniqueShas={S}",
        scanned, shas.Count);
    return shas;
}

static async Task<List<GitHubRef>> BuildGitHubRefsAsync(
    List<SpecLanding> landings, BlobServiceClient blobs,
    ILogger stsLog, ILogger pipelineLog)
{
    // Build the de-duped (owner, repo, tag, pathFilter) set from two signals:
    //   • Landing-page Supplementary Files — authoritative (owner/repo/tag/path)
    //   • STS XML opc:gitHubTag custom-meta — supplemental tag pinning, mapped
    //     to OPCFoundation/UA-Nodeset (the canonical home for OPC-published
    //     nodesets) with an empty path filter to mirror the whole tree.
    var seen = new HashSet<(string Owner, string Repo, string Tag, string Path)>();
    var refs = new List<GitHubRef>();

    void Add(string owner, string repo, string tag, string pathFilter)
    {
        if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo) || string.IsNullOrEmpty(tag))
            return;
        var key = (owner, repo, tag, pathFilter ?? "");
        if (seen.Add(key))
            refs.Add(new GitHubRef(owner, repo, tag, pathFilter ?? ""));
    }

    // 1. Supplementary Files from landings
    foreach (var landing in landings)
    {
        foreach (var sup in landing.SupplementaryFiles)
        {
            var slash = sup.Repo.IndexOf('/');
            if (slash <= 0 || slash >= sup.Repo.Length - 1) continue;
            var owner = sup.Repo[..slash];
            var repo = sup.Repo[(slash + 1)..];
            Add(owner, repo, sup.Tag, sup.Path);
        }
    }

    // 2. STS XML opc:gitHubTag — parse every sts-xml/*.xml blob downloaded
    //    in the spec_download phase. Map to OPCFoundation/UA-Nodeset.
    var container = blobs.GetBlobContainerClient("opcua-content");
    var parser = new StsMetadataParser(stsLog);
    var parsedStsCount = 0;
    var taggedCount = 0;
    await foreach (var item in container.GetBlobsAsync(
        BlobTraits.None, BlobStates.None, prefix: "sts-xml/", cancellationToken: default))
    {
        if (!item.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;
        try
        {
            var dl = await container.GetBlobClient(item.Name).DownloadContentAsync();
            var xml = dl.Value.Content.ToString();
            var meta = parser.Parse(xml);
            parsedStsCount++;
            if (!string.IsNullOrEmpty(meta.GitHubTag))
            {
                Add("OPCFoundation", "UA-Nodeset", meta.GitHubTag, "");
                taggedCount++;
            }
        }
        catch (Exception ex)
        {
            pipelineLog.LogWarning("[GITHUB_FETCH] StsParseError Blob={Blob} Error={Error}",
                item.Name, ex.Message);
        }
    }
    pipelineLog.LogInformation(
        "[GITHUB_FETCH] StsXmlParsed={P} StsWithGitHubTag={T} TotalRefs={R}",
        parsedStsCount, taggedCount, refs.Count);

    return refs;
}

static async Task<int> UploadBatchedAsync(
    SearchClient searchClient,
    List<SearchDocument> docs,
    string logTag,
    ILogger log)
{
    var uploaded = 0;
    const int batchSize = 100;
    for (var i = 0; i < docs.Count; i += batchSize)
    {
        var batch = docs.Skip(i).Take(batchSize).ToList();
        try
        {
            await RetryHelper.RetrySearchAsync(async () =>
            {
                await searchClient.IndexDocumentsAsync(IndexDocumentsBatch.Upload(batch));
                return true;
            }, log);
            uploaded += batch.Count;
            if (uploaded % 1000 == 0)
                log.LogInformation("[{Tag}] Uploaded={N} Total={T}",
                    logTag, uploaded, docs.Count);
        }
        catch (Exception ex)
        {
            log.LogWarning("[{Tag}] Phase=upload Error={Error} BatchStart={I}",
                logTag, ex.Message, i);
        }
    }
    return uploaded;
}

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

// ═══════════════════════════════════════════════════════════════════════
// SearchIndexFactory — central schema definition for the opcua-content
// indexes. Reused by SpecIndexer and the standalone OpcUaKb.Indexer tool;
// keep in sync with OpcUaKb.Indexer/Program.cs.
// ═══════════════════════════════════════════════════════════════════════

static class SearchIndexFactory
{
    public const string EmbeddingDeployment = "text-embedding-3-large";
    public const int EmbeddingDimensions = 3072;

    public static SearchIndex Build(string indexName, string aoaiEndpoint) => new(indexName)
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
            new SimpleField("spec_id", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
            new SearchableField("spec_title") { AnalyzerName = LexicalAnalyzerName.EnMicrosoft },
            new SimpleField("section_id", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("section_number", SearchFieldDataType.String) { IsFilterable = true, IsSortable = true },
            new SimpleField("section_path", SearchFieldDataType.String),
            new SimpleField("breadcrumb", SearchFieldDataType.Collection(SearchFieldDataType.String)) { IsFilterable = true, IsFacetable = true },
            new SimpleField("figures", SearchFieldDataType.Collection(SearchFieldDataType.String)) { IsFilterable = true },
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
                        ResourceUri = new Uri(aoaiEndpoint),
                        DeploymentName = EmbeddingDeployment,
                        ModelName = EmbeddingDeployment
                    }
                }
            }
        }
    };
}

// ═══════════════════════════════════════════════════════════════════════
// EmbeddingClient — shared Azure OpenAI embeddings helper. Wraps the
// /embeddings REST endpoint with bearer-token auth and retry on 429/503.
// Reused by SpecIndexer and the standalone OpcUaKb.Indexer tool.
// ═══════════════════════════════════════════════════════════════════════

static class EmbeddingClient
{
    public static async Task<List<float[]>> GetEmbeddingsAsync(
        HttpClient http, TokenCredential credential, string aoaiEndpoint,
        string deployment, List<string> texts, ILogger log)
    {
        var body = JsonSerializer.Serialize(new { input = texts, model = deployment });
        var resp = await RetryHelper.RetryAsync(
            async () =>
            {
                var token = await credential.GetTokenAsync(
                    new TokenRequestContext(new[] { "https://cognitiveservices.azure.com/.default" }),
                    default);
                var clone = new HttpRequestMessage(HttpMethod.Post,
                    $"{aoaiEndpoint}/openai/deployments/{deployment}/embeddings?api-version=2024-06-01")
                { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                clone.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);
                return await http.SendAsync(clone);
            }, log);
        if (!resp.IsSuccessStatusCode)
        {
            // Read the response body so callers can see WHICH input was rejected
            // (Azure OpenAI 400s typically name the offending element/field).
            var errorBody = await resp.Content.ReadAsStringAsync();
            // Truncate to keep KQL log lines manageable.
            if (errorBody.Length > 4000) errorBody = errorBody[..4000] + "…(truncated)";
            resp.Dispose();
            throw new HttpRequestException(
                $"Embeddings call failed with HTTP {(int)resp.StatusCode}. Body: {errorBody}");
        }
        var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
        return json["data"]!.AsArray()
            .Select(d => d!["embedding"]!.AsArray().Select(v => v!.GetValue<float>()).ToArray())
            .ToList();
    }
}
