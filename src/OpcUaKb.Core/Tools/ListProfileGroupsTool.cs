using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

[McpServerToolType]
static class ListProfileGroupsTool
{
    [McpServerTool(Name = "list_profile_groups"),
     Description("List OPC UA Profile Groups from profiles.opcfoundation.org (e.g. 'UACore 1.05', " +
        "'DI 1.05', 'PADIM 1.02'). Each group is a specification+version that owns a set of Profiles, " +
        "Categories, Conformance Groups and Conformance Units. Returns per-group counts and the release-" +
        "status mix. Use this to discover which profile groups exist before calling get_profile, " +
        "query_profiles or check_profile_conformance.")]
    public static async Task<string> ListProfileGroups(
        ProfileGraphService graph,
        [Description("Optional case-insensitive substring to filter group names (e.g. 'UACore', 'DI', 'PADIM').")]
        string? filter = null,
        [Description("Max groups to return (1-500, default 200).")] int top = 200)
    {
        if (!graph.Available)
            return "Profile graph is not configured on this server (STORAGE_ACCOUNT_NAME not set).";

        top = Math.Clamp(top, 1, 500);
        GraphIndex idx;
        try { idx = await graph.GetAsync(); }
        catch (Exception ex) { return $"Could not load the profile graph: {ex.Message}"; }

        var groups = idx.Graph.Groups
            .Where(g => string.IsNullOrWhiteSpace(filter)
                || (g.FullName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderBy(g => g.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (groups.Count == 0)
            return $"No profile groups match filter '{filter}'. Graph generated {idx.Graph.GeneratedUtc}.";

        var profilesByPg = idx.Graph.Profiles.GroupBy(p => p.Pg)
            .ToDictionary(g => g.Key, g => g.ToList());
        var cusByPg = idx.Graph.ConformanceUnits.GroupBy(c => c.Pg).ToDictionary(g => g.Key, g => g.Count());

        var sb = new StringBuilder();
        sb.AppendLine($"OPC UA Profile Groups — {groups.Count} of {idx.Graph.Groups.Count} (graph generated {idx.Graph.GeneratedUtc}):");
        sb.AppendLine();
        foreach (var g in groups.Take(top))
        {
            profilesByPg.TryGetValue(g.Id, out var ps);
            cusByPg.TryGetValue(g.Id, out var cuCount);
            var released = ps?.Count(p => p.Status == ProfileStatus.Released) ?? 0;
            var deprecated = ps?.Count(p => p.Status == ProfileStatus.Deprecated) ?? 0;
            var draftRc = ps?.Count(p => p.Status is ProfileStatus.Draft or ProfileStatus.ReleaseCandidate) ?? 0;
            sb.Append("• ").Append(g.FullName).Append("  (id=").Append(g.Id).Append(')');
            if (g.ReplacedById is int rb) sb.Append(" [replaced by id=").Append(rb).Append(']');
            sb.AppendLine();
            sb.Append("    profiles: ").Append(ps?.Count ?? 0)
              .Append(" (released ").Append(released)
              .Append(", draft/rc ").Append(draftRc)
              .Append(", deprecated ").Append(deprecated).Append(')')
              .Append("  conformance units: ").Append(cuCount)
              .AppendLine();
        }
        if (groups.Count > top)
            sb.AppendLine($"… {groups.Count - top} more (raise `top`).");
        return sb.ToString();
    }
}
