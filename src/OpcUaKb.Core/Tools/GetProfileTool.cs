using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

[McpServerToolType]
static class GetProfileTool
{
    [McpServerTool(Name = "get_profile"),
     Description("Get a single OPC UA Profile (Facet) from profiles.opcfoundation.org with its graph " +
        "neighborhood: description, release status, profile group, the conformance units it requires " +
        "(mandatory vs optional), the profiles it directly includes, and the profiles that include it " +
        "(reverse edges). Resolve by profile name (e.g. 'Standard UA Server Profile'), profileUri " +
        "(http://opcfoundation.org/UA-Profile/...), or graph key 'guid|pg'. For transitive expansion " +
        "across included profiles use check_profile_conformance mode=expand.")]
    public static async Task<string> GetProfile(
        ProfileGraphService graph,
        [Description("Profile name, profileUri, or graph key 'guid|pg'.")] string profile,
        [Description("Status scope when resolving by name/uri: released (default), rc, draft, all.")]
        string status = "released")
    {
        if (!graph.Available)
            return "Profile graph is not configured on this server (STORAGE_ACCOUNT_NAME not set).";

        GraphIndex idx;
        try { idx = await graph.GetAsync(); }
        catch (Exception ex) { return $"Could not load the profile graph: {ex.Message}"; }

        var p = idx.ResolveProfile(profile, status) ?? idx.ResolveProfile(profile, "all");
        if (p == null)
            return $"No profile found matching '{profile}'. Try query_profiles to search by fragment.";

        var sb = new StringBuilder();
        sb.AppendLine($"Profile: {p.Name}");
        sb.AppendLine($"  Group:   {p.GroupName ?? idx.GroupName(p.Pg)}");
        sb.AppendLine($"  Status:  {ProfileStatus.Label(p.Status)}  (version {p.Version})");
        if (!string.IsNullOrWhiteSpace(p.ProfileUri)) sb.AppendLine($"  URI:     {p.ProfileUri}");
        sb.AppendLine($"  Key:     {p.Key}");
        if (!string.IsNullOrWhiteSpace(p.Description))
            sb.AppendLine($"  Description: {p.Description}");
        sb.AppendLine();

        // Conformance units (direct)
        sb.AppendLine($"Conformance units ({p.ConformanceUnits.Count}):");
        foreach (var cu in p.ConformanceUnits.OrderBy(c => c.IsOptional).ThenBy(c => CuName(idx, c.Guid, c.Pg)))
        {
            var name = CuName(idx, cu.Guid, cu.Pg);
            sb.AppendLine($"  {(cu.IsOptional ? "[optional] " : "[mandatory]")} {name}");
        }
        sb.AppendLine();

        // Directly included profiles
        sb.AppendLine($"Includes ({p.Includes.Count} profiles):");
        foreach (var inc in p.Includes.OrderBy(i => i.IsOptional).ThenBy(i => ProfName(idx, i.Guid, i.Pg)))
        {
            var name = ProfName(idx, inc.Guid, inc.Pg);
            var flags = (inc.IsOptional ? "[optional] " : "[mandatory]") + (inc.ComponentOnly ? " (component-only)" : "");
            sb.AppendLine($"  {flags} {name}");
        }
        sb.AppendLine();

        // Reverse edges — profiles that include this one
        idx.IncludingByKey.TryGetValue(p.Key, out var including);
        var inc2 = including ?? [];
        sb.AppendLine($"Included by ({inc2.Count} profiles):");
        foreach (var e in inc2.OrderBy(e => e.Profile.Name))
            sb.AppendLine($"  {(e.IsOptional ? "[optional] " : "[mandatory]")} {e.Profile.Name} ({e.Profile.GroupName ?? idx.GroupName(e.Profile.Pg)})");

        return sb.ToString();
    }

    static string CuName(GraphIndex idx, string? guid, int pg) =>
        idx.CusByKey.TryGetValue($"{guid}|{pg}", out var cu) ? cu.Name ?? $"{guid}" : $"{guid} (pg{pg})";

    static string ProfName(GraphIndex idx, string? guid, int pg) =>
        idx.ProfilesByKey.TryGetValue($"{guid}|{pg}", out var p)
            ? $"{p.Name} ({p.GroupName ?? idx.GroupName(p.Pg)})"
            : $"{guid} (pg{pg})";
}
