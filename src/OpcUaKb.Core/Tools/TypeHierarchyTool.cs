using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

[McpServerToolType]
static class TypeHierarchyTool
{
    [McpServerTool(Name = "get_type_hierarchy"),
     Description("Get the type hierarchy for an OPC UA ObjectType, VariableType, or DataType " +
        "including supertype chain, declared members, inherited members, and total member counts " +
        "(member counts are not reported for DataTypes). " +
        "Use this to answer questions like 'what is the largest ObjectType', " +
        "'show the inheritance chain for ServerType', or " +
        "'show the supertype chain for AnalogUnitType'.")]
    public static async Task<string> GetTypeHierarchy(
        SearchService search,
        [Description("Type browse name to look up — ObjectType, VariableType, or DataType (e.g., DesignType, ServerType, AnalogUnitType, Int32)")] string type_name,
        [Description("Optional companion spec to narrow results (e.g., DI, Pumps)")] string? spec = null)
    {
        var filters = new List<string> { "(content_type eq 'nodeset_hierarchy' or content_type eq 'cloudlib_hierarchy')" };
        if (!string.IsNullOrWhiteSpace(spec))
            filters.Add($"spec_part eq '{spec}'");

        // Search hierarchy docs by type name
        var results = await search.SearchAsync(
            type_name,
            string.Join(" and ", filters),
            ["browse_name", "spec_part", "parent_type", "page_chunk"],
            10);

        // Also try exact match on browse_name
        if (results.Count == 0 || !results.Any(r => r.Document.GetString("browse_name") == type_name))
        {
            var exactFilter = string.Join(" and ", filters.Append($"browse_name eq '{type_name}'"));
            var exact = await search.SearchAsync("*", exactFilter,
                ["browse_name", "spec_part", "parent_type", "page_chunk"], 5);
            if (exact.Count > 0)
                results = exact;
        }

        if (results.Count == 0)
            return $"No hierarchy data found for type '{type_name}'. " +
                   "The type may not exist in the indexed NodeSets, or hierarchy data hasn't been computed yet.";

        var sb = new StringBuilder();
        foreach (var r in results)
        {
            var chunk = r.Document.GetString("page_chunk");
            if (!string.IsNullOrEmpty(chunk))
            {
                sb.AppendLine(chunk);
                sb.AppendLine("---");
            }
        }

        return sb.ToString();
    }
}
