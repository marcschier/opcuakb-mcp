using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

[McpServerToolType]
static class QueryProfilesTool
{
    [McpServerTool(Name = "query_profiles"),
     Description("Graph query over OPC UA Profiles from profiles.opcfoundation.org. Filter by name " +
        "fragment, profile group (e.g. 'UACore 1.05'), and release status, then optionally traverse the " +
        "inclusion graph. Use this to answer questions like 'which Server facets are in UACore 1.05?', " +
        "'what profiles include the Base Condition facet?', or 'list alarming-related profiles'. For a " +
        "single profile's full neighborhood use get_profile; for transitive conformance use " +
        "check_profile_conformance.")]
    public static async Task<string> QueryProfiles(
        ProfileGraphService graph,
        [Description("Case-insensitive substring to match in the profile name. Omit to match all.")]
        string? query = null,
        [Description("Optional profile group filter (case-insensitive substring, e.g. 'UACore 1.05', 'DI').")]
        string? group = null,
        [Description("Release status scope: released (default), rc, draft, all.")] string status = "released",
        [Description("Relationship to expand for each match: none (default), includes (direct included " +
            "profiles), included_by (profiles that include the match).")] string relationship = "none",
        [Description("Max profiles to return (1-200, default 50).")] int top = 50)
    {
        if (!graph.Available)
            return "Profile graph is not configured on this server (STORAGE_ACCOUNT_NAME not set).";

        top = Math.Clamp(top, 1, 200);
        GraphIndex idx;
        try { idx = await graph.GetAsync(); }
        catch (Exception ex) { return $"Could not load the profile graph: {ex.Message}"; }

        var matches = idx.Graph.Profiles
            .Where(p => ProfileStatus.Allows(p.Status, status))
            .Where(p => string.IsNullOrWhiteSpace(query)
                || (p.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
            .Where(p => string.IsNullOrWhiteSpace(group)
                || ((p.GroupName ?? idx.GroupName(p.Pg)).Contains(group, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => p.GroupName ?? idx.GroupName(p.Pg), StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (matches.Count == 0)
            return $"No profiles match query='{query}' group='{group}' status='{status}'.";

        var rel = (relationship ?? "none").ToLowerInvariant();
        var sb = new StringBuilder();
        sb.AppendLine($"Profiles matching query='{query}' group='{group}' status='{status}' — {matches.Count} match(es):");
        sb.AppendLine();
        foreach (var p in matches.Take(top))
        {
            sb.AppendLine($"• {p.Name}  [{ProfileStatus.Label(p.Status)}]  ({p.GroupName ?? idx.GroupName(p.Pg)})");
            if (!string.IsNullOrWhiteSpace(p.ProfileUri)) sb.AppendLine($"    {p.ProfileUri}");
            sb.AppendLine($"    conformance units: {p.ConformanceUnits.Count}, includes: {p.Includes.Count}");

            if (rel == "includes")
            {
                foreach (var inc in p.Includes.OrderBy(i => i.IsOptional))
                    sb.AppendLine($"      → includes {(inc.IsOptional ? "[opt] " : "")}{ProfName(idx, inc.Guid, inc.Pg)}");
            }
            else if (rel == "included_by")
            {
                idx.IncludingByKey.TryGetValue(p.Key, out var by);
                foreach (var e in (by ?? []).OrderBy(e => e.Profile.Name))
                    sb.AppendLine($"      ← included by {(e.IsOptional ? "[opt] " : "")}{e.Profile.Name} ({e.Profile.GroupName ?? idx.GroupName(e.Profile.Pg)})");
            }
        }
        if (matches.Count > top)
            sb.AppendLine($"… {matches.Count - top} more (raise `top` or narrow the query).");
        return sb.ToString();
    }

    static string ProfName(GraphIndex idx, string? guid, int pg) =>
        idx.ProfilesByKey.TryGetValue($"{guid}|{pg}", out var p)
            ? $"{p.Name} ({p.GroupName ?? idx.GroupName(p.Pg)})"
            : $"{guid} (pg{pg})";
}
