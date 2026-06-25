using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Html.Parser;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Azure.Storage.Blobs;

// ── Configuration ──────────────────────────────────────────────────────
var searchEndpoint = Environment.GetEnvironmentVariable("SEARCH_ENDPOINT")
    ?? "https://opcua-kb-search.search.windows.net";
var searchApiKey = Environment.GetEnvironmentVariable("SEARCH_API_KEY")
    ?? throw new InvalidOperationException("Set SEARCH_API_KEY");
var storageAccountName = Environment.GetEnvironmentVariable("STORAGE_ACCOUNT_NAME")
    ?? throw new InvalidOperationException("Set STORAGE_ACCOUNT_NAME");
var aoaiEndpoint = Environment.GetEnvironmentVariable("AOAI_ENDPOINT")
    ?? "https://opcua-kb-foundry.openai.azure.com";
TokenCredential credential = new DefaultAzureCredential();

const string IndexName = "opcua-content-index";
const string ContainerName = "opcua-content";
const string EmbeddingDeployment = "text-embedding-3-large";
const int EmbeddingDimensions = 3072;
const int ChunkSize = 512;    // approximate tokens
const int ChunkOverlap = 50;  // approximate token overlap
const int EmbeddingBatchSize = 16;
const int SearchUploadBatchSize = 100;

var indexClient = new SearchIndexClient(new Uri(searchEndpoint), new AzureKeyCredential(searchApiKey));
var searchClient = indexClient.GetSearchClient(IndexName);
var blobServiceClient = new BlobServiceClient(
    new Uri($"https://{storageAccountName}.blob.core.windows.net"),
    credential);
var blobContainer = blobServiceClient.GetBlobContainerClient(ContainerName);
using var http = new HttpClient();

// ── Step 1: Create or update search index ──────────────────────────────
Console.WriteLine("Creating/updating search index...");
await CreateIndexAsync();
Console.WriteLine($"  ✓ Index '{IndexName}' ready.");

// ── Step 2: List HTML blobs ────────────────────────────────────────────
Console.WriteLine("Listing HTML blobs...");
var htmlBlobs = new List<string>();
await foreach (var item in blobContainer.GetBlobsAsync())
{
    if (item.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
        (item.Properties.ContentType?.Contains("html") == true))
        htmlBlobs.Add(item.Name);
}
Console.WriteLine($"  Found {htmlBlobs.Count} HTML blobs.");

if (htmlBlobs.Count == 0)
{
    Console.WriteLine("No HTML content found. Run the crawler first.");
    return 0;
}

// ── Step 3: Process each blob ──────────────────────────────────────────
var allDocs = new List<SearchDocument>();
var parser = new HtmlParser();
int processed = 0;

foreach (var blobName in htmlBlobs)
{
    processed++;
    Console.Write($"\r  Processing {processed}/{htmlBlobs.Count}: {blobName.TruncateTo(60)}");

    try
    {
        var blobClient = blobContainer.GetBlobClient(blobName);
        var download = await blobClient.DownloadContentAsync();
        var html = download.Value.Content.ToString();

        var sourceUrl = $"https://reference.opcfoundation.org/{blobName.Replace('\\', '/')}";
        var (specPart, specVersion) = ExtractSpecInfo(blobName);

        var doc = await parser.ParseDocumentAsync(html);

        // Extract sections with headings
        var chunks = ChunkDocument(doc, specPart, specVersion, sourceUrl, blobName);
        allDocs.AddRange(chunks);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\n  ⚠ Error processing {blobName}: {ex.Message}");
    }
}
Console.WriteLine($"\n  Generated {allDocs.Count} chunks from {htmlBlobs.Count} pages.");

// ── Step 4: Generate embeddings ────────────────────────────────────────
Console.WriteLine("Generating embeddings...");
var semaphore = new SemaphoreSlim(5);
int embeddingsDone = 0;

for (int i = 0; i < allDocs.Count; i += EmbeddingBatchSize)
{
    var batch = allDocs.Skip(i).Take(EmbeddingBatchSize).ToList();
    await semaphore.WaitAsync();
    try
    {
        var texts = batch.Select(d => (string)d["page_chunk"]).ToList();
        var vectors = await GetEmbeddingsAsync(texts);

        for (int j = 0; j < batch.Count && j < vectors.Count; j++)
        {
            batch[j]["page_chunk_vector"] = vectors[j];
        }

        embeddingsDone += batch.Count;
        Console.Write($"\r  Embedded {embeddingsDone}/{allDocs.Count} chunks");
    }
    finally
    {
        semaphore.Release();
    }
}
Console.WriteLine();

// ── Step 5: Upload to search index ─────────────────────────────────────
Console.WriteLine("Uploading to search index...");
int uploaded = 0;
for (int i = 0; i < allDocs.Count; i += SearchUploadBatchSize)
{
    var batch = allDocs.Skip(i).Take(SearchUploadBatchSize).ToList();
    var indexBatch = IndexDocumentsBatch.Upload(batch);
    try
    {
        await searchClient.IndexDocumentsAsync(indexBatch);
        uploaded += batch.Count;
        Console.Write($"\r  Uploaded {uploaded}/{allDocs.Count} documents");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\n  ⚠ Batch upload error at {i}: {ex.Message}");
    }
}
Console.WriteLine($"\n  ✓ Indexed {uploaded} documents.");
Console.WriteLine("── Indexing complete ─────────────────────────────────────────────");
return 0;

// ════════════════════════════════════════════════════════════════════════
// Helper methods
// ════════════════════════════════════════════════════════════════════════

async Task CreateIndexAsync()
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
                IsSearchable = true,
                VectorSearchDimensions = EmbeddingDimensions,
                VectorSearchProfileName = "hnsw-embedding"
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
            // Profiles (profiles.opcfoundation.org) fields:
            new SimpleField("release_status", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
            new SimpleField("profile_group", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
            new SimpleField("is_optional", SearchFieldDataType.Boolean) { IsFilterable = true },
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

    await indexClient.CreateOrUpdateIndexAsync(index);
}

List<SearchDocument> ChunkDocument(AngleSharp.Dom.IDocument doc, string specPart, string specVersion, string sourceUrl, string blobName)
{
    var results = new List<SearchDocument>();
    int chunkIdx = 0;

    // Extract text content grouped by headings
    var body = doc.Body;
    if (body == null) return results;

    string currentHeading = doc.Title ?? specPart;

    // Extract all text blocks
    var textBlocks = new List<(string heading, string text, string contentType)>();

    foreach (var element in body.QuerySelectorAll("h1, h2, h3, h4, h5, h6, p, pre, code, li, td, th, figcaption, dt, dd"))
    {
        var tag = element.TagName.ToLowerInvariant();
        if (tag.StartsWith('h') && tag.Length == 2)
        {
            currentHeading = element.TextContent.Trim();
            continue;
        }

        var text = element.TextContent.Trim();
        if (string.IsNullOrWhiteSpace(text) || text.Length < 10) continue;

        var contentType = tag is "td" or "th" ? "table" : "text";
        textBlocks.Add((currentHeading, text, contentType));
    }

    // Extract tables as markdown
    foreach (var table in body.QuerySelectorAll("table"))
    {
        var heading = table.PreviousElementSibling?.TagName.StartsWith("H") == true
            ? table.PreviousElementSibling.TextContent.Trim()
            : currentHeading;

        var md = TableToMarkdown(table);
        if (md.Length > 20)
            textBlocks.Add((heading, md, "table"));
    }

    // Extract image references
    foreach (var img in body.QuerySelectorAll("img[src]"))
    {
        var src = img.GetAttribute("src") ?? "";
        var alt = img.GetAttribute("alt") ?? "";
        var caption = img.Closest("figure")?.QuerySelector("figcaption")?.TextContent ?? "";
        var context = $"[Image: {alt}] {caption} (src: {src})";
        if (context.Length > 20)
            textBlocks.Add((currentHeading, context, "image"));
    }

    // Chunk the collected text
    var buffer = new StringBuilder();
    string bufferHeading = currentHeading;
    string bufferType = "text";

    foreach (var (heading, text, contentType) in textBlocks)
    {
        var estimatedTokens = EstimateTokens(buffer.ToString() + text);
        if (estimatedTokens > ChunkSize && buffer.Length > 0)
        {
            results.Add(CreateChunkDoc(buffer.ToString(), bufferHeading, bufferType, sourceUrl, specPart, specVersion, chunkIdx++));

            // Keep overlap
            var overlapText = GetOverlapText(buffer.ToString());
            buffer.Clear();
            buffer.Append(overlapText);
        }

        bufferHeading = heading;
        bufferType = contentType;
        if (buffer.Length > 0) buffer.Append('\n');
        buffer.Append(text);
    }

    if (buffer.Length > 0)
        results.Add(CreateChunkDoc(buffer.ToString(), bufferHeading, bufferType, sourceUrl, specPart, specVersion, chunkIdx++));

    return results;
}

SearchDocument CreateChunkDoc(string text, string heading, string contentType, string sourceUrl, string specPart, string specVersion, int chunkIdx)
{
    var id = ComputeHash($"{sourceUrl}:{chunkIdx}");
    return new SearchDocument(new Dictionary<string, object>
    {
        ["id"] = id,
        ["page_chunk"] = text,
        ["source_url"] = sourceUrl,
        ["spec_part"] = specPart,
        ["spec_version"] = specVersion,
        ["section_title"] = heading,
        ["content_type"] = contentType,
        ["chunk_index"] = chunkIdx,
        ["source"] = "opcfoundation",
        ["popularity"] = 1_000_000_000L,
        ["in_opcfoundation_index"] = true,
    });
}

async Task<List<float[]>> GetEmbeddingsAsync(List<string> texts)
{
    var body = new { input = texts, model = EmbeddingDeployment };
    var json = JsonSerializer.Serialize(body);
    var token = await credential.GetTokenAsync(
        new TokenRequestContext(new[] { "https://cognitiveservices.azure.com/.default" }),
        default);
    var request = new HttpRequestMessage(HttpMethod.Post,
        $"{aoaiEndpoint}/openai/deployments/{EmbeddingDeployment}/embeddings?api-version=2024-06-01")
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);

    var response = await http.SendAsync(request);
    response.EnsureSuccessStatusCode();

    var responseJson = await response.Content.ReadAsStringAsync();
    var doc = JsonNode.Parse(responseJson)!;
    var data = doc["data"]!.AsArray();

    return data.Select(d =>
        d!["embedding"]!.AsArray().Select(v => v!.GetValue<float>()).ToArray()
    ).ToList();
}

(string specPart, string specVersion) ExtractSpecInfo(string blobName)
{
    var partMatch = Regex.Match(blobName, @"Part(\d+)", RegexOptions.IgnoreCase);
    var versionMatch = Regex.Match(blobName, @"(v\d+)", RegexOptions.IgnoreCase);
    return (
        partMatch.Success ? $"Part{partMatch.Groups[1].Value}" : "Unknown",
        versionMatch.Success ? versionMatch.Groups[1].Value : "Unknown"
    );
}

string TableToMarkdown(AngleSharp.Dom.IElement table)
{
    var sb = new StringBuilder();
    var rows = table.QuerySelectorAll("tr");
    foreach (var row in rows)
    {
        var cells = row.QuerySelectorAll("th, td");
        sb.AppendLine("| " + string.Join(" | ", cells.Select(c => c.TextContent.Trim().Replace("|", "\\|"))) + " |");
    }
    return sb.ToString();
}

int EstimateTokens(string text) => text.Length / 4; // rough: ~4 chars per token

string GetOverlapText(string text)
{
    var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (words.Length <= ChunkOverlap) return text;
    return string.Join(' ', words[^ChunkOverlap..]);
}

string ComputeHash(string input)
{
    var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
    return Convert.ToHexString(bytes)[..32].ToLowerInvariant();
}

static class StringExtensions
{
    public static string TruncateTo(this string s, int maxLen)
        => s.Length <= maxLen ? s : s[..(maxLen - 3)] + "...";
}
