# Source Projects

All projects target .NET 10 with nullable enabled and implicit usings.

## Projects

| Project | Type | Description |
|---------|------|-------------|
| [`OpcUaKb.Pipeline`](OpcUaKb.Pipeline/) | Console (Container Apps Job) | Combined crawl + index + NodeSet parse + CloudLib pipeline |
| [`OpcUaKb.McpServer`](OpcUaKb.McpServer/) | Web (Container App) | MCP server with 11 tools — search, RAG Q&A, compliance, modelling (HTTP/SSE + stdio) |
| [`OpcUaKb.HostedAgent`](OpcUaKb.HostedAgent/) | Web (Foundry Hosted Agent) | Microsoft Agent Framework agent using the Responses protocol; consumes a Foundry Toolbox that wraps `OpcUaKb.McpServer`. Replaces the legacy Bot Framework agent. |
| [`OpcUaKb.Core`](OpcUaKb.Core/) | Library | Shared `KbService` (KB retrieve + GPT-4o synthesis) and tool implementations used by McpServer |
| [`OpcUaKb.Setup`](OpcUaKb.Setup/) | Console | Creates Web Knowledge Source, Knowledge Base, verifies MCP endpoint |
| [`OpcUaKb.Crawler`](OpcUaKb.Crawler/) | Console | Standalone BFS web crawler for `*.opcfoundation.org` |
| [`OpcUaKb.Indexer`](OpcUaKb.Indexer/) | Console | Standalone HTML chunker + embedder + search indexer |
| [`OpcUaKb.Test`](OpcUaKb.Test/) | Console | Verification queries against the live knowledge base |

## Build

```bash
# Build everything
dotnet build

# Build a specific project
dotnet build src/OpcUaKb.Pipeline
```

There are no unit tests — `OpcUaKb.Test` is a console app requiring live Azure credentials. CI validates compilation only.

## Key Dependencies

| Package | Version | Used By |
|---------|---------|---------|
| `Azure.Search.Documents` | 11.8.0-beta.1 | Pipeline, Indexer, McpServer |
| `Azure.AI.OpenAI` | 2.9.0-beta.1 | Pipeline, Indexer |
| `Azure.Identity` | 1.14.2 | Core (DefaultAzureCredential for AOAI) |
| `ModelContextProtocol.AspNetCore` | 1.2.0 | McpServer |
| `Microsoft.Agents.AI.Foundry.Hosting` | 1.3.0-preview.260423.1 | HostedAgent (Agent Framework + Responses) |
| `Azure.AI.Projects` | 2.1.0-beta.1 | HostedAgent (Toolbox + project client) |

## Conventions

- **Top-level statements** for all console apps (Pipeline, Setup, Test)
- **Sealed classes** preferred
- **No explicit namespaces** in Pipeline project
- **Structured logging**: `[PHASE] Key=Value` format for KQL dashboard queries
- **HttpClient**: shared instances only — never `new HttpClient()` in loops (causes socket exhaustion)
- **Error handling**: `RetryHelper.RetrySearchAsync()` for Azure Search SDK calls, `RetryHelper.RetryAsync()` for raw HTTP with `Retry-After` support
- **Shared logic**: business code reusable across McpServer + Agent lives in `OpcUaKb.Core` (see `KbService`)

## Pipeline Phases

| Phase | Duration | Description |
|-------|----------|-------------|
| **1. Crawl** | ~5 min | BFS crawl of `*.opcfoundation.org`. Incremental — skips recently downloaded pages. |
| **2. Index** | ~3 hours | Parse HTML → chunks, generate embeddings via `text-embedding-3-large` (120K TPM), upload to Azure AI Search. Version catalog built from crawled main page. |
| **3. NodeSet** | ~30 min | Parse NodeSet XMLs from blob storage, build cross-file type hierarchy with alias/namespace normalization, generate per-ObjectType hierarchy docs + per-spec/cross-spec summary docs. |
| **4. CloudLib** *(optional)* | ~1 hour | Download all NodeSets from [UA-CloudLibrary](https://uacloudlibrary.opcfoundation.org) REST API (`/infomodel/find2` with cursor pagination), parse, generate summaries/hierarchies, upload with `cloudlib_*` content types. |

### Running Locally

```bash
# Required
export STORAGE_CONNECTION_STRING="$(az storage account show-connection-string --name <prefix>storage -g <rg> -o tsv)"
export SEARCH_ENDPOINT="https://<prefix>-search.search.windows.net"
export SEARCH_API_KEY="$(az search admin-key show --service-name <prefix>-search -g <rg> --query primaryKey -o tsv)"
export AOAI_ENDPOINT="https://<prefix>-foundry.openai.azure.com"

# Optional: UA-CloudLibrary integration
export CLOUDLIB_USERNAME="your-email@example.com"
export CLOUDLIB_PASSWORD="your-password"

# Auth to AOAI is keyless via DefaultAzureCredential
az login

dotnet run --project src/OpcUaKb.Pipeline
```

### Triggering in Azure

```bash
az containerapp job start --name <prefix>-pipeline-job --resource-group <rg>
```

## MCP Server

The MCP server is the single endpoint for all 11 tools including RAG Q&A. It connects to Azure AI Search for structured queries and to Azure AI Foundry (GPT-4o) for natural language answer synthesis.

### Transports

| Mode | Command | Use Case |
|------|---------|----------|
| HTTP/SSE (default) | `dotnet run --project src/OpcUaKb.McpServer` | Hosted on Azure Container Apps |
| stdio | `dotnet run --project src/OpcUaKb.McpServer -- --stdio` | Local use with Claude Desktop, etc. |

### Environment Variables

| Variable | Required | Default | Description |
|----------|----------|---------|-------------|
| `SEARCH_ENDPOINT` | ✓ | | Azure AI Search endpoint |
| `SEARCH_API_KEY` | ✓ | | Azure AI Search admin key |
| `AOAI_ENDPOINT` | | | Azure OpenAI / Foundry endpoint — enables `search_docs_rag` tool |
| `AOAI_API_KEY` | | | AOAI key auth (falls back to Managed Identity) |
| `KB_NAME` | | `opcua-kb` | Knowledge base name for RAG retrieval |
| `GPT_DEPLOYMENT` | | `gpt-4o` | GPT model deployment name |
| `MCP_API_KEY` | | from `SEARCH_API_KEY` | API key for authenticated access |
| `MCP_REQUIRE_AUTH` | | `false` | Set `true` to block all anonymous requests |
| `MCP_ANON_RATE_LIMIT` | | `10` | Max requests/min for anonymous callers (per IP) |
| `MCP_AUTH_RATE_LIMIT` | | `0` | Max requests/min for authenticated callers (0 = unlimited) |

### Tool Implementation

Tools are implemented as static classes with `[McpServerToolType]` and `[McpServerTool]` attributes in `src/OpcUaKb.McpServer/Tools/`:

| File | Tools |
|------|-------|
| `SearchNodesTool.cs` | `search_nodes` |
| `SearchDocsTool.cs` | `search_docs` |
| `SearchDocsRagTool.cs` | `search_docs_rag` (RAG Q&A via KB retrieve + GPT-4o) |
| `TypeHierarchyTool.cs` | `get_type_hierarchy` |
| `SpecSummaryTool.cs` | `get_spec_summary` |
| `CountNodesTool.cs` | `count_nodes` |
| `ListSpecsTool.cs` | `list_specs` |
| `ValidateNodeSetTool.cs` | `validate_nodeset` |
| `CompareVersionsTool.cs` | `compare_versions` |
| `ComplianceTool.cs` | `check_compliance` |
| `SuggestModelTool.cs` | `suggest_model` |

### Services

| Class | Purpose |
|-------|---------|
| `SearchService` | Shared Azure AI Search client for structured queries |
| `KbService` | KB retrieve API + GPT-4o chat completion for RAG. Lives in `OpcUaKb.Core`, shared with Agent. |

## Foundry Hosted Agent

The Hosted Agent (`OpcUaKb.HostedAgent`) is a Foundry Hosted Agent using the **Responses protocol** and **Microsoft Agent Framework**. It exposes the OPC UA Knowledge Base as a conversational bot in Microsoft Teams and Microsoft 365 Copilot via Foundry's Activity-bridge.

### Architecture

```
User in Teams/M365 Copilot → Foundry Agent Application (Activity bridge)
   → POST /responses → OpcUaKb.HostedAgent (Foundry-managed container)
       → projectClient.AsAIAgent(model, instructions, tools)
       → Foundry Responses API runs the tool loop server-side
           → Foundry Toolbox "opcua-kb-tools" (MCP-compatible endpoint)
               → OpcUaKb.McpServer (Container App, hosts the 11 tools)
                   → Azure AI Search
```

The hosted agent itself is ~60 lines of code — the entire tool-using loop is handled by the Foundry Responses API. No manual chat completions, no manual history hydration, no manual tool dispatch.

### Environment Variables

| Variable | Required | Default | Description |
|----------|----------|---------|-------------|
| `FOUNDRY_PROJECT_ENDPOINT` | ✓ | | Foundry project endpoint (auto-injected in hosted containers) |
| `AZURE_AI_MODEL_DEPLOYMENT_NAME` | ✓ | | Model deployment name (e.g. `gpt-4o`) |
| `TOOLBOX_NAME` | ✓ | `opcua-kb-tools` | Foundry Toolbox to load tools from |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | | | Auto-injected in hosted containers; enables tracing |

### Local Testing

```bash
cd src/OpcUaKb.HostedAgent
azd auth login
azd ai agent run                                   # runs locally on port 8088
azd ai agent invoke --local "What is Part 9?"     # sends a test query
```

The agent uses `DefaultAzureCredential` so a fresh `az login` (or `azd auth login`) is sufficient.

### Deployment

Use `scripts/install-toolbox-and-agent.ps1` — it provisions the Toolbox (declared in `agent.manifest.yaml`), builds the container in ACR, deploys via `azd deploy`, optionally publishes as an Agent Application, and binds to Teams via the Activity bridge. See [`scripts/README.md`](../scripts/README.md).

## Search Index Schema

The index schema is duplicated in two places and **must be kept in sync**:
- `OpcUaKb.Pipeline/Program.cs` → `EnsureIndexAsync()`
- `OpcUaKb.Indexer/Program.cs` → `CreateIndexAsync()`

Azure Search cannot change existing field attributes (e.g., adding `IsFacetable = true` to an existing field). Only truly new fields can be added to an existing index.

### Content Types

| Type | Source | Description |
|------|--------|-------------|
| `text` | Crawler | HTML spec page text chunks |
| `table` | Crawler | HTML tables extracted from spec pages |
| `diagram` | Crawler | Diagram descriptions from spec pages |
| `nodeset` | Pipeline | Individual NodeSet nodes (ObjectType, Variable, Method, DataType) |
| `nodeset_summary` | Pipeline | Per-spec + cross-spec aggregation statistics |
| `nodeset_hierarchy` | Pipeline | Per-ObjectType docs with supertype chain and member counts |
| `cloudlib_nodeset` | Pipeline | CloudLibrary NodeSet nodes |
| `cloudlib_summary` | Pipeline | CloudLibrary per-spec aggregation docs |
| `cloudlib_hierarchy` | Pipeline | CloudLibrary per-ObjectType hierarchy docs |

### Index Fields

| Field | Type | Filterable | Facetable | Description |
|-------|------|-----------|-----------|-------------|
| `browse_name` | String | ✓ | | Node browse name |
| `node_class` | String | ✓ | ✓ | ObjectType, Variable, Method, DataType, etc. |
| `spec_part` | String | ✓ | ✓ | Companion spec name (DI, Pumps, Part3, etc.) |
| `spec_version` | String | ✓ | | Version path segment (v104, v105, v200) |
| `parent_type` | String | ✓ | | Parent ObjectType browse name |
| `modelling_rule` | String | ✓ | ✓ | Mandatory, Optional, MandatoryPlaceholder, etc. |
| `data_type` | String | ✓ | ✓ | OPC UA data type |
| `content_type` | String | ✓ | | See content types above |
| `is_latest` | Boolean | ✓ | | `true` for the latest version of each spec |
| `version_rank` | Int32 | ✓ | | 1 = latest, 2 = previous, 3 = older |
| `source` | String | ✓ | ✓ | `opcfoundation` or `cloudlib` |
| `namespace_uri` | String | ✓ | | OPC UA namespace URI |
| `publication_date` | DateTimeOffset | ✓ | | CloudLib publication date |
| `popularity` | Int64 | ✓ | | Download count; drives scoring profile |
| `in_opcfoundation_index` | Boolean | ✓ | ✓ | Namespace overlap flag for CloudLib-diff queries |
| `title`, `description` | String | | | CloudLib metadata |
