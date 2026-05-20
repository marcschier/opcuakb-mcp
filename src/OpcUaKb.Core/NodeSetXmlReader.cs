using System.Xml;
using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;

// ═══════════════════════════════════════════════════════════════════════
// NodeSetXmlReader — streaming OPC UA NodeSet parser used by
// validate_nodeset and check_compliance. Replaces XDocument.Parse which
// would load the entire file (5× heap blow-up) for what amounts to a
// single forward walk over the top-level node elements.
//
// Two enumeration shapes:
//   • EnumerateNodes(stream, ...) — full node payload (References,
//     attributes). Used by ValidateNodeSet.
//   • EnumerateCompliance(stream)  — only the fields CheckCompliance
//     needs (BrowseName, NodeClass, ParentType, DataType).
//
// Memory profile: O(1) per node — we only retain whatever the caller
// pulls off the IEnumerable + the few in-flight strings the parser
// needs. NodeSet XML files containing megabytes of NodeIds parse in
// ~10 MB working set instead of 100s of MB.
//
// The parent-NodeId → BrowseName lookup is the only thing that needs
// the whole file in memory; it's stored as a Dictionary<string, string>
// (typically ~50 bytes per entry × ~50K entries = 2.5 MB).
// ═══════════════════════════════════════════════════════════════════════

static class NodeSetXmlReader
{
    public const string Ns = "http://opcfoundation.org/UA/2011/03/UANodeSet.xsd";

    static readonly HashSet<string> NodeElementNames = new(StringComparer.Ordinal)
    {
        "UAObjectType", "UAVariableType", "UAObject", "UAVariable",
        "UAMethod", "UADataType", "UAReferenceType", "UAView",
    };

    static XmlReaderSettings ReaderSettings(bool async) => new()
    {
        Async = async,
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreWhitespace = true,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
    };

    /// <summary>
    /// First pass over the NodeSet — collects namespaces, aliases and
    /// a NodeId → BrowseName lookup so subsequent rule passes can
    /// resolve inverse references without backtracking the stream.
    /// </summary>
    public static async Task<NodeSetHeader> ReadHeaderAsync(Stream stream, CancellationToken ct = default)
    {
        var header = new NodeSetHeader();
        using var reader = XmlReader.Create(stream, ReaderSettings(async: true));

        while (await reader.ReadAsync())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;

            switch (reader.LocalName)
            {
                case "UANodeSet" when reader.NamespaceURI == Ns:
                    header.RootSeen = true;
                    break;
                case "Uri" when reader.NamespaceURI == Ns:
                    var uri = (await reader.ReadElementContentAsStringAsync()).Trim();
                    if (!string.IsNullOrEmpty(uri)) header.NamespaceUris.Add(uri);
                    break;
                case "Alias" when reader.NamespaceURI == Ns:
                    var aliasName = reader.GetAttribute("Alias");
                    var aliasValue = (await reader.ReadElementContentAsStringAsync()).Trim();
                    if (!string.IsNullOrEmpty(aliasName)) header.Aliases[aliasName] = aliasValue;
                    break;
                default:
                    if (NodeElementNames.Contains(reader.LocalName) && reader.NamespaceURI == Ns)
                    {
                        var id = reader.GetAttribute("NodeId");
                        var bn = reader.GetAttribute("BrowseName");
                        if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(bn))
                            header.BrowseNameByNodeId[id] = StripNsPrefix(bn);
                    }
                    break;
            }
        }
        return header;
    }

    /// <summary>
    /// Second pass over the NodeSet — yields one record per top-level
    /// node element with its References subtree decoded.
    /// </summary>
    public static async IAsyncEnumerable<NodeRecord> EnumerateNodesAsync(
        Stream stream,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = XmlReader.Create(stream, ReaderSettings(async: true));
        while (await reader.ReadAsync())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (reader.NamespaceURI != Ns || !NodeElementNames.Contains(reader.LocalName)) continue;

            var localName = reader.LocalName;
            var nodeId = reader.GetAttribute("NodeId") ?? "";
            var browseName = reader.GetAttribute("BrowseName") ?? "";
            var dataType = reader.GetAttribute("DataType") ?? "";
            var parentNodeId = reader.GetAttribute("ParentNodeId");

            var refs = new List<ReferenceRecord>();
            if (!reader.IsEmptyElement)
            {
                using var sub = reader.ReadSubtree();
                while (await sub.ReadAsync())
                {
                    if (sub.NodeType != XmlNodeType.Element) continue;
                    if (sub.LocalName != "Reference" || sub.NamespaceURI != Ns) continue;

                    var refType = sub.GetAttribute("ReferenceType") ?? "";
                    var isForward = sub.GetAttribute("IsForward");
                    var target = (await sub.ReadElementContentAsStringAsync()).Trim();
                    refs.Add(new ReferenceRecord(refType, isForward, target));
                }
            }

            yield return new NodeRecord
            {
                NodeClass = localName,
                NodeId = nodeId,
                BrowseName = browseName,
                DataType = dataType,
                ParentNodeId = parentNodeId,
                References = refs,
            };
        }
    }

    public static string StripNsPrefix(string browseName)
    {
        var idx = browseName.IndexOf(':');
        return idx >= 0 && idx < 4 && int.TryParse(browseName[..idx], out _)
            ? browseName[(idx + 1)..] : browseName;
    }

    public static bool IsNodeElement(string localName) => NodeElementNames.Contains(localName);
}

sealed class NodeSetHeader
{
    public bool RootSeen { get; set; }
    public List<string> NamespaceUris { get; } = [];
    public Dictionary<string, string> Aliases { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> BrowseNameByNodeId { get; } = new(StringComparer.Ordinal);
}

sealed class NodeRecord
{
    public required string NodeClass { get; init; }
    public required string NodeId { get; init; }
    public required string BrowseName { get; init; }
    public required string DataType { get; init; }
    public required string? ParentNodeId { get; init; }
    public required List<ReferenceRecord> References { get; init; }
}

readonly record struct ReferenceRecord(string ReferenceType, string? IsForward, string Target);
