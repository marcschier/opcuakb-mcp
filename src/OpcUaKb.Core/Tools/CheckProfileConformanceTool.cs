using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

[McpServerToolType]
static class CheckProfileConformanceTool
{
    [McpServerTool(Name = "check_profile_conformance"),
     Description("Conformance analysis over the OPC UA profile graph (profiles.opcfoundation.org). " +
        "Three modes:\n" +
        "• mode=expand — given one profile, transitively expand every required and optional conformance " +
        "unit and included profile (following included profiles, cycle-safe, across profile groups).\n" +
        "• mode=satisfy — given a set of supported conformance units, report which profiles are fully or " +
        "partially satisfied (all mandatory conformance units present).\n" +
        "• mode=diff — given two profiles, list conformance units / included profiles added, removed, or " +
        "whose optionality changed (e.g. comparing two versions).")]
    public static async Task<string> CheckProfileConformance(
        ProfileGraphService graph,
        [Description("Mode: expand | satisfy | diff.")] string mode,
        [Description("Primary profile (name, profileUri, or 'guid|pg'). Required for expand and diff.")]
        string? profile = null,
        [Description("Second profile for mode=diff (name, profileUri, or 'guid|pg').")]
        string? profile_b = null,
        [Description("For mode=satisfy: comma- or newline-separated supported conformance unit names " +
            "(or guids).")] string? supported_units = null,
        [Description("Optional profile group filter (substring) to scope mode=satisfy.")] string? group = null,
        [Description("Release status scope: released (default), rc, draft, all.")] string status = "released",
        [Description("Max rows to return (1-200, default 60).")] int top = 60)
    {
        if (!graph.Available)
            return "Profile graph is not configured on this server (STORAGE_ACCOUNT_NAME not set).";

        top = Math.Clamp(top, 1, 200);
        GraphIndex idx;
        try { idx = await graph.GetAsync(); }
        catch (Exception ex) { return $"Could not load the profile graph: {ex.Message}"; }

        return (mode ?? "").ToLowerInvariant() switch
        {
            "expand" => Expand(idx, profile, status),
            "satisfy" => Satisfy(idx, supported_units, group, status, top),
            "diff" => Diff(idx, profile, profile_b, status),
            _ => "Invalid mode. Use one of: expand, satisfy, diff.",
        };
    }

    static string Expand(GraphIndex idx, string? profile, string status)
    {
        if (string.IsNullOrWhiteSpace(profile)) return "mode=expand requires `profile`.";
        var p = idx.ResolveProfile(profile, status) ?? idx.ResolveProfile(profile, "all");
        if (p == null) return $"No profile found matching '{profile}'.";

        var result = idx.Expand(p, status);
        var cuRows = result.ConformanceUnits
            .Select(kv => (Name: CuName(idx, kv.Key), Optional: kv.Value))
            .OrderBy(r => r.Optional).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var mandatory = cuRows.Count(r => !r.Optional);

        var sb = new StringBuilder();
        sb.AppendLine($"Transitive conformance for: {p.Name}  ({p.GroupName ?? idx.GroupName(p.Pg)}, {ProfileStatus.Label(p.Status)})");
        sb.AppendLine($"Status scope: {status}");
        sb.AppendLine();
        sb.AppendLine($"Conformance units: {cuRows.Count} total — {mandatory} mandatory, {cuRows.Count - mandatory} optional");
        foreach (var r in cuRows)
            sb.AppendLine($"  {(r.Optional ? "[optional] " : "[mandatory]")} {r.Name}");
        sb.AppendLine();
        sb.AppendLine($"Included profiles (transitive): {result.IncludedProfiles.Count}");
        foreach (var kv in result.IncludedProfiles
            .Select(kv => (Name: ProfName(idx, kv.Key), Optional: kv.Value))
            .OrderBy(r => r.Optional).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            sb.AppendLine($"  {(kv.Optional ? "[optional] " : "[mandatory]")} {kv.Name}");
        return sb.ToString();
    }

    static string Satisfy(GraphIndex idx, string? supported, string? group, string status, int top)
    {
        if (string.IsNullOrWhiteSpace(supported))
            return "mode=satisfy requires `supported_units` (comma/newline separated names or guids).";

        var supportedSet = supported
            .Split([',', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant())
            .ToHashSet();

        var scope = idx.Graph.Profiles
            .Where(p => ProfileStatus.Allows(p.Status, status))
            .Where(p => string.IsNullOrWhiteSpace(group)
                || ((p.GroupName ?? idx.GroupName(p.Pg)).Contains(group, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var full = new List<string>();
        var partial = new List<(string Name, int Missing, int Required)>();

        foreach (var p in scope)
        {
            var expanded = idx.Expand(p, status);
            var required = expanded.ConformanceUnits
                .Where(kv => !kv.Value)               // mandatory only
                .Select(kv => CuName(idx, kv.Key))
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (required.Count == 0) continue;

            var missing = required.Count(n => !Supports(supportedSet, n, idx));
            var label = $"{p.Name} ({p.GroupName ?? idx.GroupName(p.Pg)})";
            if (missing == 0) full.Add(label);
            else partial.Add((label, missing, required.Count));
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Conformance satisfaction — {supportedSet.Count} supported unit token(s), scope={(string.IsNullOrWhiteSpace(group) ? "all groups" : group)}, status={status}");
        sb.AppendLine($"Evaluated {scope.Count} profiles.");
        sb.AppendLine();
        sb.AppendLine($"Fully satisfied ({full.Count}):");
        foreach (var f in full.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(top))
            sb.AppendLine($"  ✓ {f}");
        if (full.Count > top) sb.AppendLine($"  … {full.Count - top} more.");
        sb.AppendLine();
        sb.AppendLine($"Closest partial matches ({partial.Count}):");
        foreach (var pm in partial.OrderBy(x => x.Missing).ThenBy(x => x.Name).Take(top))
            sb.AppendLine($"  ✗ {pm.Name} — missing {pm.Missing}/{pm.Required} mandatory units");
        if (partial.Count > top) sb.AppendLine($"  … {partial.Count - top} more.");
        return sb.ToString();
    }

    static string Diff(GraphIndex idx, string? a, string? b, string status)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return "mode=diff requires both `profile` and `profile_b`.";
        var pa = idx.ResolveProfile(a, status) ?? idx.ResolveProfile(a, "all");
        var pb = idx.ResolveProfile(b, status) ?? idx.ResolveProfile(b, "all");
        if (pa == null) return $"No profile found matching '{a}'.";
        if (pb == null) return $"No profile found matching '{b}'.";

        var ea = idx.Expand(pa, status);
        var eb = idx.Expand(pb, status);

        // Compare conformance units by name (semantic identity across groups/versions).
        var cuA = ea.ConformanceUnits.ToDictionary(kv => CuName(idx, kv.Key), kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        var cuB = eb.ConformanceUnits.ToDictionary(kv => CuName(idx, kv.Key), kv => kv.Value, StringComparer.OrdinalIgnoreCase);

        var sb = new StringBuilder();
        sb.AppendLine($"Diff (conformance units, transitive):");
        sb.AppendLine($"  A = {pa.Name} ({pa.GroupName ?? idx.GroupName(pa.Pg)})");
        sb.AppendLine($"  B = {pb.Name} ({pb.GroupName ?? idx.GroupName(pb.Pg)})");
        sb.AppendLine();

        var added = cuB.Keys.Where(k => !cuA.ContainsKey(k)).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        var removed = cuA.Keys.Where(k => !cuB.ContainsKey(k)).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        var changed = cuA.Keys.Where(k => cuB.ContainsKey(k) && cuA[k] != cuB[k])
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

        sb.AppendLine($"Added in B ({added.Count}):");
        foreach (var k in added) sb.AppendLine($"  + {k} {(cuB[k] ? "[optional]" : "[mandatory]")}");
        sb.AppendLine($"Removed from A ({removed.Count}):");
        foreach (var k in removed) sb.AppendLine($"  - {k} {(cuA[k] ? "[optional]" : "[mandatory]")}");
        sb.AppendLine($"Optionality changed ({changed.Count}):");
        foreach (var k in changed)
            sb.AppendLine($"  ~ {k}: {(cuA[k] ? "optional" : "mandatory")} → {(cuB[k] ? "optional" : "mandatory")}");
        return sb.ToString();
    }

    static bool Supports(HashSet<string> supported, string cuName, GraphIndex idx)
    {
        if (supported.Contains(cuName.ToLowerInvariant())) return true;
        // Also allow matching by guid token.
        var cu = idx.Graph.ConformanceUnits.FirstOrDefault(c =>
            string.Equals(c.Name, cuName, StringComparison.OrdinalIgnoreCase));
        return cu?.Guid != null && supported.Contains(cu.Guid.ToLowerInvariant());
    }

    static string CuName(GraphIndex idx, string key) =>
        idx.CusByKey.TryGetValue(key, out var cu) ? cu.Name ?? key : key;

    static string ProfName(GraphIndex idx, string key) =>
        idx.ProfilesByKey.TryGetValue(key, out var p)
            ? $"{p.Name} ({p.GroupName ?? idx.GroupName(p.Pg)})" : key;
}
