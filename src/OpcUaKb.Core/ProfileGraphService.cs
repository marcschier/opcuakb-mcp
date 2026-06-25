using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Identity;
using Azure.Storage.Blobs;

// ═══════════════════════════════════════════════════════════════════════
// ProfileGraphService — loads the profiles.opcfoundation.org graph written
// by the pipeline (profiles/graph.json.gz in the opcua-content container)
// via the MCP server's managed identity, caches it in memory, and provides
// lookup, graph traversal, and conformance computation for the profile MCP
// tools. The JSON contract mirrors OpcUaKb.Pipeline/ProfileGraph.cs.
//
// Enabled only when STORAGE_ACCOUNT_NAME is set (same account/container the
// upload endpoint and NodeSetLoader use). When unset, Available is false and
// the tools return a clear "not configured" message.
// ═══════════════════════════════════════════════════════════════════════

public sealed class ProfileGraphService
{
    public const string GraphBlobName = "profiles/graph.json.gz";

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    readonly BlobContainerClient? _container;
    readonly SemaphoreSlim _gate = new(1, 1);
    GraphIndex? _index;

    public bool Available => _container != null;

    public ProfileGraphService()
    {
        var account = Environment.GetEnvironmentVariable("STORAGE_ACCOUNT_NAME");
        var container = Environment.GetEnvironmentVariable("MCP_NODESET_CONTAINER") ?? "opcua-content";
        if (!string.IsNullOrWhiteSpace(account))
        {
            _container = new BlobServiceClient(
                new Uri($"https://{account}.blob.core.windows.net"),
                new DefaultAzureCredential())
                .GetBlobContainerClient(container);
        }
    }

    // For tests / DI overrides.
    public ProfileGraphService(BlobContainerClient container) => _container = container;

    public async Task<GraphIndex> GetAsync(CancellationToken ct = default)
    {
        if (_index != null) return _index;
        if (_container == null)
            throw new InvalidOperationException(
                "Profile graph is not configured. Set STORAGE_ACCOUNT_NAME on the MCP server.");

        await _gate.WaitAsync(ct);
        try
        {
            if (_index != null) return _index;
            var blob = _container.GetBlobClient(GraphBlobName);
            using var raw = await blob.OpenReadAsync(cancellationToken: ct);
            using var gz = new GZipStream(raw, CompressionMode.Decompress);
            var graph = await JsonSerializer.DeserializeAsync<ProfileGraphDoc>(gz, JsonOpts, ct)
                ?? throw new InvalidOperationException("Profile graph blob was empty or unparsable.");
            _index = new GraphIndex(graph);
            return _index;
        }
        finally { _gate.Release(); }
    }
}

// ── Status filtering ───────────────────────────────────────────────────
// Maturity ladder Draft(1) < ReleaseCandidate(2) < Released(3). Lifecycle
// states Deprecated(4)/Archived(5) are retired. "released" (default) shows
// only Released; "rc"/"draft" widen down the ladder; "all" includes
// everything (incl. deprecated/archived).
public static class ProfileStatus
{
    public const int Draft = 1, ReleaseCandidate = 2, Released = 3, Deprecated = 4, Archived = 5;

    public static string Label(int s) => s switch
    {
        1 => "Draft", 2 => "ReleaseCandidate", 3 => "Released",
        4 => "Deprecated", 5 => "Archived", _ => s.ToString(),
    };

    public static bool Allows(int status, string mode) => (mode ?? "released").ToLowerInvariant() switch
    {
        "all" => true,
        "draft" => status is Draft or ReleaseCandidate or Released,
        "rc" => status is ReleaseCandidate or Released,
        _ => status == Released, // "released"
    };
}

// ── In-memory index over the deserialized graph ────────────────────────
public sealed class GraphIndex
{
    public ProfileGraphDoc Graph { get; }
    public Dictionary<int, GroupNodeDoc> GroupsById { get; }
    public Dictionary<string, ProfileNodeDoc> ProfilesByKey { get; }
    public Dictionary<string, CuNodeDoc> CusByKey { get; }
    public Dictionary<string, CgNodeDoc> ConformanceGroupsByKey { get; }
    public Dictionary<string, CatNodeDoc> CategoriesByKey { get; }
    // Reverse includes: includedKey -> list of profiles that include it.
    public Dictionary<string, List<IncludingEdge>> IncludingByKey { get; }

    public GraphIndex(ProfileGraphDoc g)
    {
        Graph = g;
        GroupsById = g.Groups.ToDictionary(x => x.Id);
        ProfilesByKey = g.Profiles
            .GroupBy(p => p.Key).ToDictionary(grp => grp.Key, grp => grp.First());
        CusByKey = g.ConformanceUnits
            .GroupBy(c => c.Key).ToDictionary(grp => grp.Key, grp => grp.First());
        ConformanceGroupsByKey = g.ConformanceGroups
            .GroupBy(c => c.Key).ToDictionary(grp => grp.Key, grp => grp.First());
        CategoriesByKey = g.Categories
            .GroupBy(c => c.Key).ToDictionary(grp => grp.Key, grp => grp.First());

        IncludingByKey = new();
        foreach (var p in g.Profiles)
            foreach (var inc in p.Includes)
            {
                var key = $"{inc.Guid}|{inc.Pg}";
                if (!IncludingByKey.TryGetValue(key, out var list))
                    IncludingByKey[key] = list = [];
                list.Add(new IncludingEdge(p, inc.IsOptional));
            }
    }

    public string GroupName(int pg) =>
        GroupsById.TryGetValue(pg, out var g) ? (g.FullName ?? g.Name ?? $"pg{pg}") : $"pg{pg}";

    // Resolve a profile by key ("guid|pg"), profileUri, or name (case-insensitive).
    public ProfileNodeDoc? ResolveProfile(string q, string statusMode = "all")
    {
        if (string.IsNullOrWhiteSpace(q)) return null;
        if (ProfilesByKey.TryGetValue(q, out var byKey)) return byKey;

        var byUri = Graph.Profiles.FirstOrDefault(p =>
            string.Equals(p.ProfileUri, q, StringComparison.OrdinalIgnoreCase)
            && ProfileStatus.Allows(p.Status, statusMode));
        if (byUri != null) return byUri;

        var byName = Graph.Profiles
            .Where(p => string.Equals(p.Name, q, StringComparison.OrdinalIgnoreCase)
                && ProfileStatus.Allows(p.Status, statusMode))
            .OrderByDescending(p => p.Status == ProfileStatus.Released)
            .ThenByDescending(p => p.Version)
            .FirstOrDefault();
        return byName;
    }

    public IEnumerable<ProfileNodeDoc> FindProfiles(string nameFragment, string statusMode) =>
        Graph.Profiles.Where(p =>
            ProfileStatus.Allows(p.Status, statusMode)
            && (p.Name?.Contains(nameFragment, StringComparison.OrdinalIgnoreCase) ?? false));

    // Transitive expansion of a profile: all included profiles + conformance
    // units, with mandatory/optional classification. A CU/profile is optional
    // if any edge along its inclusion path is optional. Cycle-safe.
    public ExpandResult Expand(ProfileNodeDoc root, string statusMode)
    {
        var profiles = new Dictionary<string, bool>();   // key -> isOptional(path)
        var cus = new Dictionary<string, bool>();         // cu key -> isOptional(path)
        var visited = new HashSet<string>();

        void Walk(ProfileNodeDoc p, bool optionalPath)
        {
            if (!visited.Add(p.Key)) return;

            foreach (var cu in p.ConformanceUnits)
            {
                var key = $"{cu.Guid}|{cu.Pg}";
                var opt = optionalPath || cu.IsOptional;
                if (cus.TryGetValue(key, out var existing))
                    cus[key] = existing && opt; // mandatory if any path is mandatory
                else
                    cus[key] = opt;
            }

            foreach (var inc in p.Includes)
            {
                var key = $"{inc.Guid}|{inc.Pg}";
                var opt = optionalPath || inc.IsOptional;
                if (profiles.TryGetValue(key, out var existing))
                    profiles[key] = existing && opt;
                else
                    profiles[key] = opt;

                if (ProfilesByKey.TryGetValue(key, out var child)
                    && ProfileStatus.Allows(child.Status, statusMode))
                    Walk(child, opt);
            }
        }

        Walk(root, false);
        visited.Remove(root.Key); // root itself isn't an "included" profile
        return new ExpandResult(root, profiles, cus);
    }
}

public readonly record struct IncludingEdge(ProfileNodeDoc Profile, bool IsOptional);

public sealed record ExpandResult(
    ProfileNodeDoc Root,
    Dictionary<string, bool> IncludedProfiles,
    Dictionary<string, bool> ConformanceUnits);

// ── Deserialized graph model (mirrors OpcUaKb.Pipeline/ProfileGraph.cs) ──
public sealed class ProfileGraphDoc
{
    public string GeneratedUtc { get; set; } = "";
    public Dictionary<string, string> StatusEnum { get; set; } = new();
    public List<GroupNodeDoc> Groups { get; set; } = [];
    public List<CatNodeDoc> Categories { get; set; } = [];
    public List<FolderNodeDoc> Folders { get; set; } = [];
    public List<CgNodeDoc> ConformanceGroups { get; set; } = [];
    public List<CuNodeDoc> ConformanceUnits { get; set; } = [];
    public List<ProfileNodeDoc> Profiles { get; set; } = [];
}

public sealed class GroupNodeDoc
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? FullName { get; set; }
    public int WorkingGroupId { get; set; }
    public int Sort { get; set; }
    public int? ReplacedById { get; set; }
    public List<int> Requires { get; set; } = [];
}

public sealed class CatNodeDoc
{
    public string Key { get; set; } = "";
    public string? Guid { get; set; }
    public int Pg { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public int Status { get; set; }
}

public sealed class FolderNodeDoc
{
    public string Key { get; set; } = "";
    public string? Guid { get; set; }
    public int Pg { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public int Status { get; set; }
    public string? CategoryGuid { get; set; }
    public string? ParentFolderGuid { get; set; }
}

public sealed class CgNodeDoc
{
    public string Key { get; set; } = "";
    public string? Guid { get; set; }
    public int Pg { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public int Status { get; set; }
}

public sealed class CuNodeDoc
{
    public string Key { get; set; } = "";
    public string? Guid { get; set; }
    public int Pg { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public int Status { get; set; }
    public string? ConformanceGroupGuid { get; set; }
    public string? CategoryGuid { get; set; }
}

public sealed class ProfileNodeDoc
{
    public string Key { get; set; } = "";
    public string? Guid { get; set; }
    public int Pg { get; set; }
    public string? GroupName { get; set; }
    public string? Name { get; set; }
    public string? ProfileUri { get; set; }
    public string? Description { get; set; }
    public int Status { get; set; }
    public int Version { get; set; }
    public List<IncludeEdgeDoc> Includes { get; set; } = [];
    public List<CuRefDoc> ConformanceUnits { get; set; } = [];
}

public sealed class IncludeEdgeDoc
{
    public string? Guid { get; set; }
    public int Pg { get; set; }
    public bool IsOptional { get; set; }
    public bool ComponentOnly { get; set; }
}

public sealed class CuRefDoc
{
    public string? Guid { get; set; }
    public int Pg { get; set; }
    public bool IsOptional { get; set; }
}
