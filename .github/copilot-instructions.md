# Copilot Instructions for OPC UA Knowledge Base

## Build & Run

```bash
# Build everything
dotnet build

# Run the pipeline locally (requires env vars — see README.md "Manual Pipeline Run")
dotnet run --project src/OpcUaKb.Pipeline

# Run the custom MCP server (requires SEARCH_ENDPOINT and SEARCH_API_KEY)
dotnet run --project src/OpcUaKb.McpServer

# Run the hosted agent locally (requires Azure CLI login + MCP_SERVER_URL)
# See src/OpcUaKb.HostedAgent/README.md.
azd ai agent run --cwd src/OpcUaKb.HostedAgent

# Validate Bicep infrastructure
az bicep build --file infra/main.bicep

# Deploy KB infrastructure (idempotent)
./infra/deploy.sh -s <subscription-id> -g rg-opcua-kb -p opcua-kb -l westus3

# Deploy Hosted Agent (requires a Hosted-Agent-supported region, e.g. westus3)
cd src/OpcUaKb.HostedAgent && azd provision && azd deploy
```

There are no unit tests — `OpcUaKb.Test` is a console app requiring live Azure credentials. CI validates compilation only.

## Architecture

- **Single solution** (`OpcUaKnowledgeBase.slnx`) with 8 projects under `src/`
- **Pipeline** (`OpcUaKb.Pipeline`): Top-level statements, sealed classes, no explicit namespaces. Phases: spec_discovery → spec_download → image_fetch → github_fetch → nodeset_parse → spec_index → cloudlib → profiles. Runs as Azure Container Apps Job on a weekly cron. Crawls the per-spec `/specs/{id}` landing pages (Single Page HTML + STS XML + GitHub supplementary files). The `profiles` phase crawls the profiles.opcfoundation.org REST API (the SPA's backend) and emits a profile graph blob + searchable docs.
- **MCP Server** (`OpcUaKb.McpServer`): Custom MCP server with 15 tools (search_nodes, get_type_hierarchy, get_spec_summary, search_docs, search_docs_rag, count_nodes, list_specs, compare_versions, suggest_model, check_compliance, validate_nodeset, plus the profile-graph tools list_profile_groups, get_profile, query_profiles, check_profile_conformance). Uses `ModelContextProtocol` SDK with HTTP SSE (default) + stdio (`--stdio`) transports.
- **Hosted Agent** (`OpcUaKb.HostedAgent`): Foundry Hosted Agent using Microsoft Agent Framework + Responses protocol. Connects **directly** to `OpcUaKb.McpServer` via `ModelContextProtocol.Client` (`HttpClientTransport` + `McpClient.CreateAsync` + `ListToolsAsync`) so each of the 15 MCP tools is exposed as a distinct `McpClientTool` (`AIFunction`). Deployed via `azd deploy` (after `azd ai agent init` + `azd provision`); replaces the legacy Bot Framework custom engine agent. **Does NOT use a Foundry Toolbox** — earlier `GetToolboxToolsAsync` approach wrapped tools into one opaque `McpTool` invisible to GPT-4o.
- **Infrastructure**: `infra/main.bicep` (Azure resources) + `infra/deploy.sh`. The Foundry-side Hosted Agent is NOT in Bicep; it's provisioned by `azd provision` / `azd deploy` from `src/OpcUaKb.HostedAgent/agent.manifest.yaml`.
- **Region**: All resources colocated in **westus3** (the KB stack co-located with the Foundry Hosted Agent, which is preview-restricted to westus3, westus, norwayeast, francecentral, japaneast).
- **Index**: Azure AI Search `opcua-content-index-v2` (current). Legacy `opcua-content-index` (v1) is preserved for back-compat tools. `content_type` distinguishes `spec_section` (v2 per-section docs), `nodeset`, `nodeset_summary`, `nodeset_hierarchy`, `cloudlib_*`, the profiles content types `profile`/`conformance_unit`/`conformance_group`/`profile_category`, and legacy `text`/`table`/`diagram`.
- **Profiles graph**: The `profiles` phase fetches profiles.opcfoundation.org via its anonymous REST API (`/api/profilegroup`, `/api/profile?pg=`, `/api/conformanceunit?pg=`, per-profile `includedprofiles`/`includedconformanceunits`) for ALL profile groups and versions. It writes a normalized graph to `profiles/graph.json.gz` (+ `profiles/catalog.json`) in the `opcua-content` blob container. The MCP server's `ProfileGraphService` loads/caches that blob (via managed identity) to back the profile tools. `release_status` enum: Draft=1, ReleaseCandidate=2, Released=3, Deprecated=4, Archived=5; profile tools default to Released-only with a `status` widen param (rc/draft/all). New index fields: `release_status`, `profile_group`, `is_optional`.
- **v2 structured fields**: `spec_id`, `spec_title`, `section_id`, `section_number`, `section_path`, `breadcrumb` (Collection), `figures` (Collection), `publication_date`. Doc key = `Base64Url("{spec_id}|{version}|{section_slug or section_number}")` — Azure Search rejects `/` and `.` in keys.
- **NodeSet structured fields**: `node_class`, `modelling_rule`, `browse_name`, `parent_type`, `data_type` for structured queries
- **Summary docs**: Pre-computed per-spec and cross-spec statistics (`content_type=nodeset_summary`)
- **Hierarchy docs**: Per-ObjectType documents (`content_type=nodeset_hierarchy`) with supertype chain, declared/inherited member counts
- **Type hierarchy**: Cross-file ObjectType inheritance resolution with alias/namespace normalization, memoized supertype chain traversal, and completeness tracking
- **API version**: Azure AI Search agentic retrieval uses `2025-11-01-preview`. The knowledge base `{prefix}-kb` binds **two** knowledge sources: `{prefix}-web-ks` (`kind: "web"` with `webParameters.domains.allowedDomains`) for live opcfoundation.org lookups, and `{prefix}-index-ks` (`kind: "searchIndex"` targeting `opcua-content-index-v2`, `semanticConfigurationName: "semantic_config"`) for the structured spec/NodeSet content built by the pipeline. The index's built-in vectorizer authenticates to Foundry via the Search service MI (`Cognitive Services OpenAI User`), so it works under Foundry `disableLocalAuth=true`.
- **Storage**: MI-only (`allowSharedKeyAccess: false`). All Pipeline + Indexer code takes `BlobServiceClient` via `DefaultAzureCredential(new Uri($"https://{name}.blob.core.windows.net"))`. There is **no** `STORAGE_CONNECTION_STRING` env var.

## Azure Resource Configuration

These are the **production values** — do not revert to lower defaults:

| Parameter | Value | Notes |
|-----------|-------|-------|
| AI Search SKU | `standard` | Required for semantic ranker + knowledge bases |
| Embedding model capacity | `120` (TPM in thousands) | Upgraded from 30 to avoid 429 throttling |
| GPT-4o capacity | `30` | |
| Container Apps Job timeout | `86400` (24 hours) | Full crawl + index takes ~17 hours |
| Cron schedule | `0 2 * * 0` | Weekly Sunday 2am UTC |
| KB resource group | `rg-opcua-kb` | Current production region: **westus3** (KB + Hosted Agent colocated) |
| Storage auth | Managed Identity only | `allowSharedKeyAccess: false` |

## HttpClient Usage — Critical Pattern

**Never create `new HttpClient()` inside loops or retry lambdas.** This causes socket exhaustion and loses auth headers. Always use a shared instance:

```csharp
// CORRECT — shared client with default headers
var http = new HttpClient();
http.DefaultRequestHeaders.Add("api-key", apiKey);
// Pass http to methods, reuse across all calls

// WRONG — causes socket exhaustion, silent auth failures
for (var i = 0; i < batches.Count; i++)
{
    var client = new HttpClient(); // ← never do this
    await client.PostAsync(url, content);
}
```

This pattern caused 3 consecutive pipeline runs (each ~17 hours) to report success while producing 0 NodeSet documents.

## Error Handling in Batch Operations

**Never swallow HTTP errors in batch processing loops.** If `EnsureSuccessStatusCode()` throws inside a `try/catch` that only logs, the entire batch silently fails while the pipeline reports success.

Pattern to follow:
- Use `RetryHelper.RetrySearchAsync()` for Azure Search SDK calls
- Use `RetryHelper.RetryAsync()` for raw HTTP calls — handles 429/503 with `Retry-After`
- Log failures with `[PHASE] Phase=X Error=Y` structured format for dashboard visibility
- Track upload counts and compare against expected totals at phase end

## Azure AI Search Schema — Keep In Sync

The current production index is **`opcua-content-index-v2`**. The schema is duplicated in two places and **must be kept in sync**:
- `OpcUaKb.Pipeline/Program.cs` → `EnsureIndexAsync()`
- `OpcUaKb.Indexer/Program.cs` → `CreateIndexAsync()`

The v1 index (`opcua-content-index`) is no longer written to but is preserved in code (default for `SearchService.DefaultIndexName` was bumped to v2 in 4.0).

**Important**: Azure Search cannot change existing field attributes (e.g., adding `IsFacetable = true` to an already-created field). Only truly new fields can be added to an existing index.

## Azure AI Search Agentic Retrieval API

The deploy script uses `az rest` for preview API operations. Key schema patterns:

```bash
# Knowledge source (web type)
az rest --method PUT \
  --url "https://{search}.search.windows.net/knowledgesources/{prefix}-web-ks?api-version=2025-11-01-preview" \
  --body '{
    "kind": "web",
    "webParameters": { "domains": { "allowedDomains": ["*.opcfoundation.org"] } }
  }'

# Knowledge source (search index type) — grounds the KB in opcua-content-index-v2
az rest --method PUT \
  --url "https://{search}.search.windows.net/knowledgesources/{prefix}-index-ks?api-version=2025-11-01-preview" \
  --body '{
    "kind": "searchIndex",
    "searchIndexParameters": {
      "searchIndexName": "opcua-content-index-v2",
      "semanticConfigurationName": "semantic_config",
      "searchFields": [{ "name": "page_chunk" }],
      "sourceDataFields": [{ "name": "page_chunk" }]
    }
  }'

# Knowledge base binds BOTH sources + the GPT-4o model
az rest --method PUT \
  --url "https://{search}.search.windows.net/knowledgebases/{kb}?api-version=2025-11-01-preview" \
  --body '{
    "knowledgeSources": [{ "name": "{prefix}-web-ks" }, { "name": "{prefix}-index-ks" }],
    "models": [{ "kind": "azureOpenAI", "azureOpenAIParameters": { ... } }]
  }'
```

The MCP endpoint is automatically exposed at:
```
https://{search}.search.windows.net/knowledgebases/{kb}/mcp?api-version=2025-11-01-preview
```

## Conventions

- .NET 10, nullable enabled, implicit usings
- Top-level statements for all console apps (Pipeline, Setup, Test, McpServer, HostedAgent)
- Sealed classes preferred
- No explicit namespaces in Pipeline project
- NuGet: `Azure.Search.Documents` 11.8.0-beta.1, `Azure.AI.OpenAI` 2.9.0-beta.1, `ModelContextProtocol.AspNetCore` 1.2.0 (server), `ModelContextProtocol` 1.2.0 (client — HostedAgent), `Microsoft.Agents.AI.Foundry.Hosting` 1.3.0-preview.260423.1 (HostedAgent), `Azure.AI.Projects` 2.1.0-beta.1 (HostedAgent)
- Structured logging: `[PHASE] Key=Value` format for KQL dashboard queries
- Pipeline status tracked in `_pipeline-status.json` blob
- MCP server uses static tool classes with `[McpServerToolType]` / `[McpServerTool]` attributes
- HostedAgent uses Agent Framework with **direct MCP client**: `var mcpClient = await McpClient.CreateAsync(new HttpClientTransport(...))` → `var tools = await mcpClient.ListToolsAsync()` → `projectClient.AsAIAgent(model, instructions, tools: [.. tools])`. The hosting library runs the tool-call loop locally in the container. **Do NOT use `GetToolboxToolsAsync`** — it returns one opaque `McpTool` wrapper invisible to the model.
- Tool implementations live in `OpcUaKb.Core/Tools/` and are hosted by `OpcUaKb.McpServer`. The HostedAgent and any other MCP client consume them over HTTPS.
- Storage uses `DefaultAzureCredential` only — there is no `STORAGE_CONNECTION_STRING` env var. Pipeline MI must have `Storage Blob Data Contributor` on the storage account.
- Linux-vs-Windows gotcha: `Uri.TryCreate("/path", UriKind.Absolute, out _)` returns `true` on Linux (scheme `file://`) but `false` on Windows. Use `UrlHelper.Absolutize` for URL absolutization.
