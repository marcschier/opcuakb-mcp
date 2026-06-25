using System.Text;
using Azure.Search.Documents.Models;

// ═══════════════════════════════════════════════════════════════════════
// ProfileIndexer — turns a ProfileGraph into Azure AI Search documents for
// full-text / semantic discovery in opcua-content-index-v2. Mirrors the
// nodeset-doc approach: text only, no embeddings (the index's semantic
// config + the knowledge-base index source make these retrievable).
//
// content_type values: profile, conformance_unit, conformance_group,
// profile_category. The graph blob remains the source of truth for the
// full relationship structure; these docs are for search/discovery.
// ═══════════════════════════════════════════════════════════════════════

static class ProfileIndexer
{
    public static List<SearchDocument> BuildDocuments(ProfileGraph graph)
    {
        var docs = new List<SearchDocument>();
        var groupName = graph.Groups.ToDictionary(g => g.Id, g => g.FullName ?? g.Name ?? $"pg{g.Id}");

        string Status(int s) => graph.StatusEnum.TryGetValue(s.ToString(), out var label) ? label : s.ToString();
        string Grp(int pg) => groupName.TryGetValue(pg, out var n) ? n : $"pg{pg}";

        foreach (var p in graph.Profiles)
        {
            var grp = p.GroupName ?? Grp(p.Pg);
            var status = Status(p.Status);
            var sb = new StringBuilder();
            sb.Append(p.Name).Append(" — OPC UA Profile (").Append(grp).Append("). Status: ").Append(status).Append('.');
            if (!string.IsNullOrWhiteSpace(p.ProfileUri)) sb.Append(" URI: ").Append(p.ProfileUri).Append('.');
            sb.Append(" Conformance units: ").Append(p.ConformanceUnits.Count).Append("; included profiles: ").Append(p.Includes.Count).Append('.');
            if (!string.IsNullOrWhiteSpace(p.Description)) sb.Append(' ').Append(p.Description);

            docs.Add(new SearchDocument
            {
                ["id"] = Key("profile", p.Key),
                ["content_type"] = "profile",
                ["source"] = "profiles",
                ["source_url"] = p.ProfileUri,
                ["spec_id"] = grp,
                ["spec_title"] = grp,
                ["spec_version"] = p.Version.ToString(),
                ["title"] = p.Name,
                ["description"] = p.Description,
                ["page_chunk"] = sb.ToString(),
                ["release_status"] = status,
                ["profile_group"] = grp,
            });
        }

        foreach (var cu in graph.ConformanceUnits)
        {
            var grp = Grp(cu.Pg);
            var status = Status(cu.Status);
            var sb = new StringBuilder();
            sb.Append(cu.Name).Append(" — OPC UA Conformance Unit (").Append(grp).Append("). Status: ").Append(status).Append('.');
            if (!string.IsNullOrWhiteSpace(cu.Description)) sb.Append(' ').Append(cu.Description);

            docs.Add(new SearchDocument
            {
                ["id"] = Key("cu", cu.Key),
                ["content_type"] = "conformance_unit",
                ["source"] = "profiles",
                ["spec_id"] = grp,
                ["spec_title"] = grp,
                ["title"] = cu.Name,
                ["description"] = cu.Description,
                ["page_chunk"] = sb.ToString(),
                ["release_status"] = status,
                ["profile_group"] = grp,
            });
        }

        foreach (var cg in graph.ConformanceGroups)
        {
            var grp = Grp(cg.Pg);
            var status = Status(cg.Status);
            docs.Add(new SearchDocument
            {
                ["id"] = Key("cg", cg.Key),
                ["content_type"] = "conformance_group",
                ["source"] = "profiles",
                ["spec_id"] = grp,
                ["spec_title"] = grp,
                ["title"] = cg.Name,
                ["description"] = cg.Description,
                ["page_chunk"] = $"{cg.Name} — OPC UA Conformance Group ({grp}). Status: {status}. {cg.Description}",
                ["release_status"] = status,
                ["profile_group"] = grp,
            });
        }

        foreach (var c in graph.Categories)
        {
            var grp = Grp(c.Pg);
            var status = Status(c.Status);
            docs.Add(new SearchDocument
            {
                ["id"] = Key("cat", c.Key),
                ["content_type"] = "profile_category",
                ["source"] = "profiles",
                ["spec_id"] = grp,
                ["spec_title"] = grp,
                ["title"] = c.Name,
                ["description"] = c.Description,
                ["page_chunk"] = $"{c.Name} — OPC UA Profile Category ({grp}). Status: {status}. {c.Description}",
                ["release_status"] = status,
                ["profile_group"] = grp,
            });
        }

        return docs;
    }

    // Azure Search keys may not contain '/', '.', etc. — Base64Url the natural key.
    static string Key(string prefix, string naturalKey) =>
        prefix + "_" + Convert.ToBase64String(Encoding.UTF8.GetBytes(naturalKey))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
