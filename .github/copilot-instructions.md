# Copilot Instructions for OPC UA Knowledge Base

## Build & Run

```bash
# Build everything
dotnet build

# Run the pipeline locally (requires env vars — see README.md "Manual Pipeline Run")
dotnet run --project src/OpcUaKb.Pipeline

# Run the custom MCP server (requires SEARCH_ENDPOINT and SEARCH_API_KEY)
dotnet run --project src/OpcUaKb.McpServer

# Run the hosted agent locally (requires Azure CLI login + an existing Foundry Toolbox)
# See src/OpcUaKb.HostedAgent/README.md and scripts/install-toolbox-and-agent.ps1.
azd ai agent run --cwd src/OpcUaKb.HostedAgent

# Validate Bicep infrastructure
az bicep build --file infra/main.bicep

# Deploy everything (idempotent)
./infra/deploy.sh -s <subscription-id> -g rg-opcua-kb -p opcua-kb -l eastus
```

There are no unit tests — `OpcUaKb.Test` is a console app requiring live Azure credentials. CI validates compilation only.

## Architecture

- **Single solution** (`OpcUaKnowledgeBase.slnx`) with 8 projects under `src/`
- **Pipeline** (`OpcUaKb.Pipeline`): Top-level statements, sealed classes, no explicit namespaces. Three phases: crawl → index → nodeset. Runs as Azure Container Apps Job on a weekly cron.
- **MCP Server** (`OpcUaKb.McpServer`): Custom MCP server with 11 tools (search_nodes, get_type_hierarchy, get_spec_summary, search_docs, search_docs_rag, count_nodes, list_specs, compare_versions, suggest_model, check_compliance, validate_nodeset). Uses `ModelContextProtocol` SDK with HTTP SSE (default) + stdio (`--stdio`) transports.
- **Hosted Agent** (`OpcUaKb.HostedAgent`): Foundry Hosted Agent using Microsoft Agent Framework + Responses protocol. Reads tools from a Foundry Toolbox that wraps `OpcUaKb.McpServer`. Deployed via `azd deploy` (after `azd ai agent init` + `azd provision`); replaces the legacy Bot Framework custom engine agent.
- **Infrastructure**: `infra/main.bicep` (Azure resources) + `infra/deploy.sh`. The Foundry-side agent + toolbox are NOT in Bicep; they're provisioned by `azd provision` / `azd deploy` from `src/OpcUaKb.HostedAgent/agent.manifest.yaml` (after `azd ai agent init` scaffolds `azure.yaml`).
- **Index**: Azure AI Search `opcua-content-index` with `content_type` field distinguishing `text`, `table`, `diagram`, `nodeset`, `nodeset_summary`, and `nodeset_hierarchy` docs
- **Structured fields**: NodeSet docs have `node_class`, `modelling_rule`, `browse_name`, `parent_type`, and `data_type` as filterable fields for structured queries
- **Summary docs**: Pre-computed per-spec and cross-spec statistics (content_type=`nodeset_summary`) enable the KB to answer aggregation questions
- **Hierarchy docs**: Per-ObjectType documents (content_type=`nodeset_hierarchy`) with supertype chain, declared/inherited member counts
- **Type hierarchy**: Cross-file ObjectType inheritance resolution with alias/namespace normalization, memoized supertype chain traversal, and completeness tracking
- **API version**: Azure AI Search agentic retrieval uses `2025-11-01-preview`. Knowledge sources use `kind: "web"` with `webParameters.domains.allowedDomains`.

## Azure Resource Configuration

These are the **production values** — do not revert to lower defaults:

| Parameter | Value | Notes |
|-----------|-------|-------|
| AI Search SKU | `standard` | Required for semantic ranker + knowledge bases |
| Embedding model capacity | `120` (TPM in thousands) | Upgraded from 30 to avoid 429 throttling |
| GPT-4o capacity | `30` | |
| Container Apps Job timeout | `86400` (24 hours) | Full crawl + index takes ~17 hours |
| Cron schedule | `0 2 * * 0` | Weekly Sunday 2am UTC |
| Resource group | `rg-opcua-kb` | Region: eastus |
| KB retrieval reasoning | `medium` | Upgraded from low for better query planning |

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

The index schema is duplicated in two places and **must be kept in sync**:
- `OpcUaKb.Pipeline/Program.cs` → `EnsureIndexAsync()`
- `OpcUaKb.Indexer/Program.cs` → `CreateIndexAsync()`

**Important**: Azure Search cannot change existing field attributes (e.g., adding `IsFacetable = true` to an already-created field). Only truly new fields can be added to an existing index.

## Azure AI Search Agentic Retrieval API

The deploy script uses `az rest` for preview API operations. Key schema patterns:

```bash
# Knowledge source (web type)
az rest --method PUT \
  --url "https://{search}.search.windows.net/knowledgebases/{kb}/knowledgesources/{ks}?api-version=2025-11-01-preview" \
  --body '{
    "kind": "web",
    "webParameters": { "domains": { "allowedDomains": ["*.opcfoundation.org"] } }
  }'

# Knowledge base with models
az rest --method PUT \
  --url "https://{search}.search.windows.net/knowledgebases/{kb}?api-version=2025-11-01-preview" \
  --body '{
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
- NuGet: `Azure.Search.Documents` 11.8.0-beta.1, `Azure.AI.OpenAI` 2.9.0-beta.1, `ModelContextProtocol` 1.2.0, `Microsoft.Agents.AI.Foundry.Hosting` 1.3.0-preview.260423.1 (HostedAgent only), `Azure.AI.Projects` 2.1.0-beta.1 (HostedAgent only)
- Structured logging: `[PHASE] Key=Value` format for KQL dashboard queries
- Pipeline status tracked in `_pipeline-status.json` blob
- MCP server uses static tool classes with `[McpServerToolType]` / `[McpServerTool]` attributes
- HostedAgent uses Agent Framework: `projectClient.AsAIAgent(model, instructions, tools)` with tools fetched via `projectClient.GetToolboxToolsAsync(toolboxName)` — Foundry executes tool calls server-side, no manual loop
- Foundry Toolbox is declared as a `kind: toolbox` resource in `agent.manifest.yaml` and auto-provisioned by `azd provision`
- Tool implementations are shared between `OpcUaKb.McpServer` (the actual host) and `OpcUaKb.Core` (definitions). The Toolbox proxies to the MCP server on Container Apps.
