using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;

[McpServerToolType]
static class ValidateNodeSetTool
{
    // OPC 11030 §2.1.2: BrowseNames should be PascalCase, no underscores
    static readonly Regex ContainsUnderscore = new(@"_", RegexOptions.Compiled);

    [McpServerTool(Name = "validate_nodeset"),
     Description("Validate an OPC UA NodeSet XML against the OPC UA standard and OPC 11030 " +
        "Modelling Best Practices. Checks naming conventions, modelling rules, type hierarchy, " +
        "reference types, and structural correctness. Returns a report with errors, warnings, " +
        "and informational findings with spec references. " +
        "Provide exactly ONE input source: " +
        "(a) `nodeset_xml` for tiny snippets (≤30 KB hard cap — the LLM tool-call args limit); " +
        "(b) `nodeset_ref` of the form `blob:uploads/{sha256}.xml` returned by POST /upload-nodeset, " +
        "or `blob:nodesets/...` for pipeline-indexed NodeSets; " +
        "(c) `nodeset_url` pointing at an https URL on the allow-list (UA-Nodeset on GitHub, " +
        "*.opcfoundation.org, etc.). Use (b) or (c) for any real NodeSet — they're virtually always " +
        "well over 30 KB.")]
    public static async Task<string> ValidateNodeSet(
        NodeSetLoader loader,
        [Description("Optional: inline NodeSet XML, ≤30 KB. Use only for hand-crafted snippets.")]
        string? nodeset_xml = null,
        [Description("Optional: server-side reference, e.g. `blob:uploads/{sha256}.xml` returned " +
            "by POST /upload-nodeset, or `blob:nodesets/UA-Nodeset/.../Opc.Ua.Di.NodeSet2.xml`.")]
        string? nodeset_ref = null,
        [Description("Optional: https URL to fetch. Must match the configured allow-list " +
            "(defaults: *.opcfoundation.org, raw.githubusercontent.com).")]
        string? nodeset_url = null)
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

        NodeSetHeader header;
        var report = new List<Finding>();
        try
        {
            using (var pass1 = await source.OpenAsync())
            {
                header = await NodeSetXmlReader.ReadHeaderAsync(pass1);
            }

            if (!header.RootSeen)
                return "❌ Root element must be <UANodeSet> with namespace " + NodeSetXmlReader.Ns;

            // §2.5 NamespaceUri conventions
            ValidateNamespaceUris(header.NamespaceUris, report);

            var nodeCount = 0;
            var seenNodeIds = new HashSet<string>(StringComparer.Ordinal);

            using var pass2 = await source.OpenAsync();
            await foreach (var node in NodeSetXmlReader.EnumerateNodesAsync(pass2))
            {
                nodeCount++;

                if (string.IsNullOrEmpty(node.NodeId))
                {
                    report.Add(Finding.Error(
                        $"Node with BrowseName='{node.BrowseName}' has no NodeId attribute",
                        "Part 3, §5.2.1"));
                    continue;
                }

                if (!seenNodeIds.Add(node.NodeId))
                {
                    report.Add(Finding.Error(
                        $"Duplicate NodeId: {node.NodeId} (BrowseName='{node.BrowseName}')",
                        "Part 3, §5.2.1"));
                }

                if (string.IsNullOrEmpty(node.BrowseName))
                {
                    report.Add(Finding.Error(
                        $"Node {node.NodeId} has no BrowseName attribute",
                        "Part 3, §5.2"));
                }

                var stripped = NodeSetXmlReader.StripNsPrefix(node.BrowseName);
                ValidateNaming(stripped, node.NodeClass, node.NodeId, report);
                ValidateReferences(node, stripped, header.Aliases, report);
                ValidateModellingRules(node, stripped, report);
                ValidateDataTypeUsage(node, stripped, report);
            }

            return RenderReport(report, nodeCount, header);
        }
        catch (NodeSetLoadException ex)
        {
            return $"❌ {ex.Message}";
        }
        catch (System.Xml.XmlException ex)
        {
            return $"❌ Failed to parse XML: {ex.Message}";
        }
    }

    static void ValidateNamespaceUris(List<string> uris, List<Finding> report)
    {
        foreach (var uri in uris)
        {
            // §2.5: NamespaceUri should end with /
            if (!uri.EndsWith('/'))
            {
                report.Add(Finding.Warning(
                    $"NamespaceUri '{uri}' does not end with '/'. Convention is to end with trailing slash.",
                    "OPC 11030, §2.5"));
            }

            // §2.5: Should use http://opcfoundation.org/UA/ prefix for OPC Foundation specs
            if (uri.Contains("opcfoundation.org") && !uri.StartsWith("http://opcfoundation.org/UA/"))
            {
                report.Add(Finding.Warning(
                    $"NamespaceUri '{uri}' uses opcfoundation.org but doesn't follow 'http://opcfoundation.org/UA/...' convention.",
                    "OPC 11030, §2.5"));
            }
        }
    }

    static void ValidateNaming(string browseName, string nodeClass, string nodeId, List<Finding> report)
    {
        if (string.IsNullOrEmpty(browseName)) return;

        // §2.1.2: BrowseNames should use PascalCase
        if (browseName.Length > 1 && char.IsLower(browseName[0]))
        {
            report.Add(Finding.Warning(
                $"BrowseName '{browseName}' ({nodeId}) starts with lowercase. OPC 11030 recommends PascalCase.",
                "OPC 11030, §2.1.2"));
        }

        // §2.1.2: Avoid underscores in BrowseNames
        if (ContainsUnderscore.IsMatch(browseName))
        {
            report.Add(Finding.Warning(
                $"BrowseName '{browseName}' ({nodeId}) contains underscore(s). OPC 11030 recommends avoiding underscores.",
                "OPC 11030, §2.1.2"));
        }

        // §2.1.3: ObjectTypes and VariableTypes should end with "Type"
        if (nodeClass is "UAObjectType" or "UAVariableType")
        {
            if (!browseName.EndsWith("Type"))
            {
                report.Add(Finding.Warning(
                    $"{nodeClass} BrowseName '{browseName}' ({nodeId}) does not end with 'Type'.",
                    "OPC 11030, §2.1.3"));
            }
        }

        // §2.1.3: DataTypes that are structures/enums: naming conventions
        if (nodeClass == "UADataType" && browseName.EndsWith("Type") &&
            !browseName.EndsWith("DataType"))
        {
            report.Add(Finding.Info(
                $"DataType BrowseName '{browseName}' ({nodeId}) ends with 'Type'. Consider if 'DataType' suffix is more appropriate.",
                "OPC 11030, §2.1.3"));
        }
    }

    static void ValidateReferences(
        NodeRecord node, string browseName, Dictionary<string, string> aliases, List<Finding> report)
    {
        bool hasSubtype = false;
        bool hasModellingRule = false;

        foreach (var r in node.References)
        {
            var refType = r.ReferenceType;
            if (aliases.TryGetValue(refType, out var resolved))
                refType = resolved;

            if (refType is "HasSubtype" or "i=45" or "ns=0;i=45" && r.IsForward != "true")
                hasSubtype = true;
            if (refType is "HasModellingRule" or "i=37" or "ns=0;i=37")
                hasModellingRule = true;
        }

        if (node.NodeClass is "UAObjectType" or "UAVariableType" or "UADataType" or "UAReferenceType"
            && !hasSubtype)
        {
            report.Add(Finding.Warning(
                $"{node.NodeClass} '{browseName}' ({node.NodeId}) has no HasSubtype inverse reference. It should derive from a base type.",
                "Part 3, §5.8"));
        }

        if (node.NodeClass is "UAObject" or "UAVariable" or "UAMethod"
            && node.ParentNodeId != null && !hasModellingRule)
        {
            report.Add(Finding.Warning(
                $"Instance '{browseName}' ({node.NodeId}) has ParentNodeId but no ModellingRule reference.",
                "Part 3, §6.2.6"));
        }
    }

    static void ValidateModellingRules(NodeRecord node, string browseName, List<Finding> report)
    {
        if (node.NodeClass != "UAObjectType") return;

        var forwardMembers = 0;
        foreach (var r in node.References)
        {
            if (r.IsForward == "true" &&
                r.ReferenceType is "HasComponent" or "HasProperty" or "HasOrderedComponent"
                    or "i=47" or "i=46" or "i=49")
            {
                forwardMembers++;
            }
        }

        if (forwardMembers == 0)
        {
            report.Add(Finding.Info(
                $"ObjectType '{browseName}' ({node.NodeId}) has no declared components/properties. Consider if this is intentional.",
                "OPC 11030, §7.2"));
        }
    }

    static void ValidateDataTypeUsage(NodeRecord node, string browseName, List<Finding> report)
    {
        if (node.NodeClass != "UAVariable") return;

        // Warn about using generic Structure/BaseDataType
        if (node.DataType is "i=22" or "ns=0;i=22")
        {
            report.Add(Finding.Warning(
                $"Variable '{browseName}' ({node.NodeId}) uses generic 'Structure' DataType. Consider using a more specific concrete type.",
                "OPC 11030, §7.5"));
        }

        if (node.DataType is "i=24" or "ns=0;i=24")
        {
            report.Add(Finding.Warning(
                $"Variable '{browseName}' ({node.NodeId}) uses 'BaseDataType'. Consider using a more specific type.",
                "Part 3, §5.8.3"));
        }
    }

    static string RenderReport(List<Finding> report, int nodeCount, NodeSetHeader header)
    {
        var sb = new StringBuilder();
        var errors = report.Count(f => f.Severity == "ERROR");
        var warnings = report.Count(f => f.Severity == "WARNING");
        var infos = report.Count(f => f.Severity == "INFO");

        sb.AppendLine($"## NodeSet Validation Report");
        sb.AppendLine();
        sb.AppendLine($"Nodes analyzed: {nodeCount}");
        sb.AppendLine($"Namespace URIs: {header.NamespaceUris.Count}");
        sb.AppendLine($"Aliases: {header.Aliases.Count}");
        sb.AppendLine();
        sb.AppendLine($"**{errors} errors, {warnings} warnings, {infos} info**");
        sb.AppendLine();

        if (errors == 0 && warnings == 0)
        {
            sb.AppendLine("✅ No issues found. The NodeSet appears well-formed and follows best practices.");
        }
        else
        {
            foreach (var group in report.GroupBy(f => f.Severity).OrderBy(g => g.Key))
            {
                sb.AppendLine($"### {group.Key}s ({group.Count()})");
                sb.AppendLine();
                foreach (var f in group.Take(100))
                {
                    sb.AppendLine($"- {f.Message}");
                    if (!string.IsNullOrEmpty(f.SpecRef))
                        sb.AppendLine($"  📖 {f.SpecRef}");
                }
                if (group.Count() > 100)
                    sb.AppendLine($"  ... and {group.Count() - 100} more {group.Key} findings");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    sealed class Finding
    {
        public required string Severity { get; init; }
        public required string Message { get; init; }
        public string? SpecRef { get; init; }

        public static Finding Error(string msg, string? specRef = null) =>
            new() { Severity = "ERROR", Message = msg, SpecRef = specRef };
        public static Finding Warning(string msg, string? specRef = null) =>
            new() { Severity = "WARNING", Message = msg, SpecRef = specRef };
        public static Finding Info(string msg, string? specRef = null) =>
            new() { Severity = "INFO", Message = msg, SpecRef = specRef };
    }
}
