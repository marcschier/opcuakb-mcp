using System.IO.Compression;
using System.Text.Json;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;

// ═══════════════════════════════════════════════════════════════════════
// ProfileGraphStore — persists the ProfileGraph to the `opcua-content`
// blob container (the same container the MCP server reads via its managed
// identity).
//
//   profiles/graph.json.gz   — full gzip-compressed graph (read by the MCP
//                              server's ProfileGraphService, cached in memory)
//   profiles/catalog.json    — lightweight group catalog with per-group counts
//                              (cheap listing without loading the full graph)
//
// The JSON uses camelCase so it round-trips with the Core reader model.
// ═══════════════════════════════════════════════════════════════════════

static class ProfileGraphStore
{
    public const string GraphBlobName = "profiles/graph.json.gz";
    public const string CatalogBlobName = "profiles/catalog.json";

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task WriteAsync(BlobContainerClient container, ProfileGraph graph, ILogger log)
    {
        // Full graph — gzip compressed.
        var graphBytes = JsonSerializer.SerializeToUtf8Bytes(graph, JsonOpts);
        using (var compressed = new MemoryStream())
        {
            using (var gz = new GZipStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
                gz.Write(graphBytes, 0, graphBytes.Length);
            compressed.Position = 0;
            await container.GetBlobClient(GraphBlobName).UploadAsync(compressed, new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/gzip" },
            });
        }
        log.LogInformation(
            "[PROFILES] Phase=graph_written Blob={Blob} RawBytes={Raw} Profiles={P} ConformanceUnits={CU}",
            GraphBlobName, graphBytes.Length, graph.Profiles.Count, graph.ConformanceUnits.Count);

        // Lightweight catalog.
        var catalog = new
        {
            graph.GeneratedUtc,
            groups = graph.Groups
                .OrderBy(g => g.FullName)
                .Select(g => new
                {
                    g.Id, g.FullName, g.WorkingGroupId, g.ReplacedById,
                    profiles = graph.Profiles.Count(p => p.Pg == g.Id),
                    conformanceUnits = graph.ConformanceUnits.Count(c => c.Pg == g.Id),
                    categories = graph.Categories.Count(c => c.Pg == g.Id),
                })
                .ToList(),
        };
        var catalogBytes = JsonSerializer.SerializeToUtf8Bytes(catalog, JsonOpts);
        await container.GetBlobClient(CatalogBlobName).UploadAsync(
            new BinaryData(catalogBytes), overwrite: true);
        log.LogInformation("[PROFILES] Phase=catalog_written Blob={Blob} Groups={G}",
            CatalogBlobName, graph.Groups.Count);
    }
}
