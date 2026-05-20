using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

[McpServerToolType]
static class CheckComplianceTool
{
    [McpServerTool(Name = "check_compliance"),
     Description("Check whether a NodeSet XML implementation is compliant with a companion specification's " +
        "type definitions. Compares the implementation against the indexed spec to find: missing mandatory nodes, " +
        "missing optional nodes (info), data type mismatches, incorrect modelling rules, and extra nodes not in the spec. " +
        "Use this to verify that an OPC UA server correctly implements a companion specification. " +
        "Provide exactly ONE input source: " +
        "(a) `nodeset_xml` for tiny snippets (≤30 KB hard cap); " +
        "(b) `nodeset_ref` of the form `blob:uploads/{sha256}.xml` returned by POST /upload-nodeset, " +
        "or `blob:nodesets/...` for pipeline-indexed NodeSets; " +
        "(c) `nodeset_url` pointing at an allow-listed https URL. Use (b) or (c) for real NodeSets.")]
    public static async Task<string> CheckCompliance(
        SearchService search,
        NodeSetLoader loader,
        [Description("Companion spec name to check against (e.g., DI, Pumps, PlasticsRubber, Machinery)")] string spec,
        [Description("Optional: inline NodeSet XML, ≤30 KB. Use only for hand-crafted snippets.")]
        string? nodeset_xml = null,
        [Description("Optional: server-side reference, e.g. `blob:uploads/{sha256}.xml` returned " +
            "by POST /upload-nodeset, or `blob:nodesets/UA-Nodeset/...`.")]
        string? nodeset_ref = null,
        [Description("Optional: https URL to fetch. Must match the configured allow-list.")]
        string? nodeset_url = null,
        [Description("Specific ObjectType to check compliance for (optional — checks all types if omitted)")]
        string? object_type = null)
    {
        NodeSetSource source;
        try
        {
            source = await loader.ResolveAsync(nodeset_xml, nodeset_ref, nodeset_url);
        }
        catch (NodeSetLoadException ex)
        {
            return $"❌ {ex.Message}";
        }

        // First pass — build the NodeId → BrowseName lookup so we can
        // resolve `ParentType` from inverse HasComponent/HasProperty
        // refs in the second pass.
        NodeSetHeader header;
        try
        {
            using var pass1 = await source.OpenAsync();
            header = await NodeSetXmlReader.ReadHeaderAsync(pass1);
        }
        catch (System.Xml.XmlException ex)
        {
            return $"❌ Failed to parse XML: {ex.Message}";
        }
        catch (NodeSetLoadException ex)
        {
            return $"❌ {ex.Message}";
        }

        if (!header.RootSeen)
            return "❌ Invalid XML: root element must be <UANodeSet>.";

        var implNodes = new Dictionary<string, ImplNode>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var pass2 = await source.OpenAsync();
            await foreach (var node in NodeSetXmlReader.EnumerateNodesAsync(pass2))
            {
                var browseName = NodeSetXmlReader.StripNsPrefix(node.BrowseName);
                var nodeClass = MapNodeClass(node.NodeClass);

                // Resolve parent BrowseName from inverse HasComponent / HasProperty / HasOrderedComponent.
                string parentType = "";
                foreach (var r in node.References)
                {
                    var rt = r.ReferenceType;
                    if (rt is "HasComponent" or "HasProperty" or "HasOrderedComponent"
                        or "i=47" or "i=46" or "i=49"
                        && r.IsForward != "true")
                    {
                        if (header.BrowseNameByNodeId.TryGetValue(r.Target, out var pbn))
                            parentType = pbn;
                        break;
                    }
                }

                var key = $"{nodeClass}|{browseName}|{parentType}";
                implNodes.TryAdd(key, new ImplNode
                {
                    BrowseName = browseName,
                    NodeClass = nodeClass,
                    ParentType = parentType,
                    DataType = node.DataType,
                });
            }
        }
        catch (NodeSetLoadException ex)
        {
            return $"❌ {ex.Message}";
        }
        catch (System.Xml.XmlException ex)
        {
            return $"❌ Failed to parse XML: {ex.Message}";
        }

        // Fetch spec definitions from the index — try opcfoundation, then cloudlib
        var filters = new List<string>
        {
            "content_type eq 'nodeset'",
            SpecFilter.Match(spec),
        };
        if (!string.IsNullOrWhiteSpace(object_type))
            filters.Add($"parent_type eq '{object_type}'");

        var select = new[] { "browse_name", "node_class", "parent_type", "modelling_rule", "data_type" };
        var specNodes = await search.SearchAsync("*", string.Join(" and ", filters), select, 1000);

        // Fallback to cloudlib if opcfoundation has no data
        if (specNodes.Count == 0)
        {
            filters[0] = "content_type eq 'cloudlib_nodeset'";
            // For cloudlib, prefer latest version
            filters.Add("is_latest eq true");
            specNodes = await search.SearchAsync("*", string.Join(" and ", filters), select, 1000);
        }

        if (specNodes.Count == 0)
            return $"No nodes found in spec '{spec}'" +
                   (object_type != null ? $" for type '{object_type}'" : "") +
                   ". Check the spec name is correct.";

        // Compare
        var findings = new List<(string severity, string message)>();
        int mandatoryMissing = 0, optionalMissing = 0, matched = 0, extra = 0;
        var specKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var specNode in specNodes)
        {
            var d = specNode.Document;
            var name = d.GetString("browse_name") ?? "";
            var nc = d.GetString("node_class") ?? "";
            var parent = d.GetString("parent_type") ?? "";
            var mr = d.GetString("modelling_rule") ?? "";
            var dt = d.GetString("data_type") ?? "";

            var key = $"{nc}|{name}|{parent}";
            specKeys.Add(key);

            if (implNodes.TryGetValue(key, out var impl))
            {
                matched++;

                // Check data type mismatch
                if (!string.IsNullOrEmpty(dt) && !string.IsNullOrEmpty(impl.DataType)
                    && !dt.Equals(impl.DataType, StringComparison.OrdinalIgnoreCase)
                    && !impl.DataType.Contains(dt))
                {
                    findings.Add(("WARNING",
                        $"DataType mismatch: {name} [{nc}] in {parent} — spec expects '{dt}', implementation has '{impl.DataType}'"));
                }
            }
            else
            {
                // Missing from implementation
                if (mr == "Mandatory")
                {
                    mandatoryMissing++;
                    findings.Add(("ERROR",
                        $"Missing mandatory {nc}: {name} in {parent} (ModellingRule: Mandatory)"));
                }
                else if (mr == "Optional")
                {
                    optionalMissing++;
                    findings.Add(("INFO",
                        $"Optional {nc} not implemented: {name} in {parent}"));
                }
                else if (mr is "MandatoryPlaceholder" or "OptionalPlaceholder")
                {
                    findings.Add(("INFO",
                        $"Placeholder {nc} not implemented: {name} in {parent} ({mr})"));
                }
            }
        }

        // Check for extra nodes not in spec (only for the specific types we're checking)
        if (!string.IsNullOrWhiteSpace(object_type))
        {
            foreach (var (key, impl) in implNodes)
            {
                if (impl.ParentType.Equals(object_type, StringComparison.OrdinalIgnoreCase)
                    && !specKeys.Contains(key))
                {
                    extra++;
                    findings.Add(("INFO",
                        $"Extra {impl.NodeClass} not in spec: {impl.BrowseName} in {impl.ParentType}"));
                }
            }
        }

        // Build report
        var sb = new StringBuilder();
        sb.AppendLine($"## Compliance Report: {spec}");
        if (!string.IsNullOrWhiteSpace(object_type))
            sb.AppendLine($"ObjectType: {object_type}");
        sb.AppendLine();
        sb.AppendLine($"Spec nodes checked: {specNodes.Count}");
        sb.AppendLine($"Implementation nodes parsed: {implNodes.Count}");
        sb.AppendLine($"Matched: {matched} | Missing mandatory: {mandatoryMissing} | Missing optional: {optionalMissing} | Extra: {extra}");
        sb.AppendLine();

        if (mandatoryMissing == 0 && findings.All(f => f.severity != "WARNING"))
        {
            sb.AppendLine($"✅ Implementation is **compliant** with {spec}" +
                (object_type != null ? $" ({object_type})" : "") + ".");
            if (optionalMissing > 0)
                sb.AppendLine($"   ({optionalMissing} optional nodes not implemented — this is acceptable)");
        }
        else
        {
            sb.AppendLine($"❌ Implementation has **{mandatoryMissing} mandatory nodes missing**.");
        }
        sb.AppendLine();

        // Group findings
        foreach (var sev in new[] { "ERROR", "WARNING", "INFO" })
        {
            var group = findings.Where(f => f.severity == sev).ToList();
            if (group.Count == 0) continue;

            var label = sev switch { "ERROR" => "Errors", "WARNING" => "Warnings", _ => "Info" };
            sb.AppendLine($"### {label} ({group.Count})");
            foreach (var (_, msg) in group.Take(50))
                sb.AppendLine($"- {msg}");
            if (group.Count > 50)
                sb.AppendLine($"  ... and {group.Count - 50} more");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    static string MapNodeClass(string localName) => localName switch
    {
        "UAObjectType" => "ObjectType",
        "UAVariableType" => "VariableType",
        "UAObject" => "Object",
        "UAVariable" => "Variable",
        "UAMethod" => "Method",
        "UADataType" => "DataType",
        "UAReferenceType" => "ReferenceType",
        "UAView" => "View",
        _ => localName,
    };

    sealed class ImplNode
    {
        public required string BrowseName { get; init; }
        public required string NodeClass { get; init; }
        public required string ParentType { get; init; }
        public required string DataType { get; init; }
    }
}
