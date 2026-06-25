// ═══════════════════════════════════════════════════════════════════════
// ProfileGraph — serializable model for the profiles.opcfoundation.org
// graph. Written to blob storage as `profiles/graph.json` (camelCase) and
// read back by OpcUaKb.Core/ProfileGraphService. The JSON shape is the
// contract between the pipeline (writer) and the MCP server (reader); keep
// the two models in sync.
//
// Node key convention: "{guid}|{profileGroupId}" — guids can repeat across
// profile-group versions, so the group id disambiguates. Relationship
// payloads carry both guid and profile-group id, so cross-group edges
// resolve cleanly.
// ═══════════════════════════════════════════════════════════════════════

sealed class ProfileGraph
{
    public string GeneratedUtc { get; set; } = "";
    public Dictionary<string, string> StatusEnum { get; set; } = new()
    {
        ["1"] = "Draft",
        ["2"] = "ReleaseCandidate",
        ["3"] = "Released",
        ["4"] = "Deprecated",
        ["5"] = "Archived",
    };
    public List<GraphGroup> Groups { get; set; } = [];
    public List<GraphCategory> Categories { get; set; } = [];
    public List<GraphFolder> Folders { get; set; } = [];
    public List<GraphConformanceGroup> ConformanceGroups { get; set; } = [];
    public List<GraphConformanceUnit> ConformanceUnits { get; set; } = [];
    public List<GraphProfile> Profiles { get; set; } = [];
}

sealed class GraphGroup
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? FullName { get; set; }
    public int WorkingGroupId { get; set; }
    public int Sort { get; set; }
    public int? ReplacedById { get; set; }
    public List<int> Requires { get; set; } = [];
}

sealed class GraphCategory
{
    public string Key { get; set; } = "";
    public string? Guid { get; set; }
    public int Pg { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public int Status { get; set; }
}

sealed class GraphFolder
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

sealed class GraphConformanceGroup
{
    public string Key { get; set; } = "";
    public string? Guid { get; set; }
    public int Pg { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public int Status { get; set; }
}

sealed class GraphConformanceUnit
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

sealed class GraphProfile
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
    public List<GraphInclude> Includes { get; set; } = [];
    public List<GraphCuRef> ConformanceUnits { get; set; } = [];
}

sealed class GraphInclude
{
    public string? Guid { get; set; }
    public int Pg { get; set; }
    public bool IsOptional { get; set; }
    public bool ComponentOnly { get; set; }
}

sealed class GraphCuRef
{
    public string? Guid { get; set; }
    public int Pg { get; set; }
    public bool IsOptional { get; set; }
}
