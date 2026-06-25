# Scripts

Installation and configuration scripts for the OPC UA Knowledge Base MCP server.

## Install Scripts

### `install-mcp.ps1` (PowerShell)

Configures MCP client applications (GitHub Copilot CLI, Claude Desktop) to use the OPC UA KB MCP server.

```powershell
# Hosted mode — uses the cloud-hosted MCP server (recommended)
.\scripts\install-mcp.ps1 -Mode hosted -ApiKey <your-search-api-key>

# Local mode — uses the locally installed dotnet tool
.\scripts\install-mcp.ps1 -Mode local -ApiKey <your-search-api-key>
```

**What it does:**
1. Detects installed MCP clients (Copilot CLI, Claude Desktop)
2. Adds/updates the `opcua-kb-tools` MCP server entry (single endpoint for all 15 tools including RAG Q&A)
3. In local mode, verifies the `opcua-kb-mcp` dotnet tool is installed

**Configuration files modified:**
- GitHub Copilot CLI: `~/.copilot/mcp-config.json`
- Claude Desktop: `~/AppData/Roaming/Claude/claude_desktop_config.json` (Windows) or `~/Library/Application Support/Claude/claude_desktop_config.json` (macOS)

### `install-mcp.sh` (Bash)

Bash equivalent of the PowerShell script:

```bash
SEARCH_API_KEY=<your-search-api-key> ./scripts/install-mcp.sh hosted
SEARCH_API_KEY=<your-search-api-key> ./scripts/install-mcp.sh local
```

## NodeSet upload

For NodeSets too large to inline in a tool call (anything over ~30 KB), use the `/upload-nodeset` endpoint and pass the returned `nodeset_ref` to `validate_nodeset` or `check_compliance`:

```bash
# Raw XML body
curl -X POST \
  -H "api-key: $MCP_API_KEY" \
  -H "Content-Type: application/xml" \
  --data-binary @MyNodeSet.xml \
  https://<mcp-server-fqdn>/upload-nodeset

# Or multipart/form-data with a `file` part
curl -X POST \
  -H "api-key: $MCP_API_KEY" \
  -F "file=@MyNodeSet.xml" \
  https://<mcp-server-fqdn>/upload-nodeset

# Response: { "nodeset_ref": "blob:uploads/{sha256}.xml", "size_bytes": N, "sha256": "..." }
```

The endpoint requires the `api-key` header (no anonymous writes — even when `MCP_REQUIRE_AUTH=false`). Uploads are content-addressed by sha256 and auto-deleted by the storage account's lifecycle policy after 1 day.

## Agent Deployment Scripts

### `install-toolbox-and-agent.ps1` (PowerShell) — *legacy*

> ⚠️ **This script is now legacy.** The Hosted Agent no longer uses a Foundry Toolbox — it connects directly to the MCP server via `ModelContextProtocol.Client`. The script still works (the Toolbox it provisions is harmless but unused). For new deployments, prefer the simpler `azd provision` + `azd deploy` flow described in [`src/OpcUaKb.HostedAgent/README.md`](../src/OpcUaKb.HostedAgent/README.md).

End-to-end automation for deploying the OPC UA Hosted Agent to **Azure AI Foundry** as a managed Hosted Agent using the **Responses protocol**. Replaces the older Bot Framework / Microsoft 365 Agents SDK deployment.

```powershell
.\scripts\install-toolbox-and-agent.ps1 `
    [-ResourceGroup rg-opcua-kb] `
    [-FoundryAccountName opcua-kb-foundry] `
    [-FoundryProjectName opcua-kb-project] `
    [-McpServerUrl https://opcua-kb-mcp-server.<env>.azurecontainerapps.io/] `
    [-Location westus3] `
    [-AgentDir ..\src\OpcUaKb.HostedAgent] `
    [-SkipAzdInit] [-SkipProvision] `
    [-PublishAsApp] [-BindToTeams]
```

**What it does (idempotent — safe to re-run):**

1. Pre-flight: verifies `az`, `azd`, `azd ai agents` extension, agent dir + manifest, resolves MCP server URL.
2. Auth: `az account set`, soft RBAC check (Azure AI Project Manager on the Foundry project), `azd auth login` for the Microsoft tenant.
3. `azd ai agent init`: writes `azure.yaml` if missing.
4. `azd provision`: creates the Foundry account/project/ACR/gpt-4o (and the legacy Foundry Toolbox the agent no longer uses).
5. `azd deploy`: builds the container in ACR remotely and creates a new agent version. Captures the agent endpoint.
6. **Optional** `-PublishAsApp`: wraps the agent version in a Foundry Agent Application with a stable endpoint and dedicated Entra agent identity.
7. **Optional** `-BindToTeams`: binds the Agent Application to a Teams channel via Foundry's Activity-bridge.
8. Smoke test: invokes the agent with `azd ai agent invoke --verbose` and looks for tool-call traces.

**Architecture:**
```
Teams / Web Chat → Foundry Agent Application (Activity bridge)
  → OpcUaKb.HostedAgent (Responses protocol container)
    → ModelContextProtocol.Client.McpClient
      → OpcUaKb.McpServer (Container App, 15 tools)
        → Azure AI Search + Azure AI Foundry (RAG)
```

**Prerequisites:**
- `az` CLI (logged in to the right tenant)
- `azd` CLI v1.24.0+ with the `azure.ai.agents` extension
- An existing resource group with the MCP server Container App and a `gpt-4o` model deployment (run `infra/deploy.sh` first)
- A Hosted-Agent-supported region for the Foundry project (westus3, westus, norwayeast, francecentral, japaneast)

**Note:** The script uses **Python-direct invocation** of the Azure CLI on Windows to avoid `cmd.exe` truncating secrets containing special characters like `&`.

## Manual Configuration

### GitHub Copilot CLI

Add to `~/.copilot/mcp-config.json`:

```json
{
  "mcpServers": {
    "opcua-kb-tools": {
      "type": "http",
      "url": "https://<mcp-server-fqdn>/",
      "headers": { "api-key": "<your-search-api-key>" }
    }
  }
}
```

This single endpoint provides all 15 tools: structured search, RAG Q&A (`search_docs_rag`), compliance validation, version comparison, model design suggestions, and the profile graph (`list_profile_groups`, `get_profile`, `query_profiles`, `check_profile_conformance`).

### Claude Desktop

Add to `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "opcua-kb-tools": {
      "command": "opcua-kb-mcp",
      "args": ["--stdio"],
      "env": {
        "SEARCH_ENDPOINT": "https://<prefix>-search.search.windows.net",
        "SEARCH_API_KEY": "<your-search-api-key>",
        "AOAI_ENDPOINT": "https://<prefix>-foundry.openai.azure.com"
      }
    }
  }
}
```

> **Note:** `AOAI_ENDPOINT` enables the `search_docs_rag` tool (RAG Q&A). Omit it if you only need the structured search tools. When running locally, AOAI auth uses `DefaultAzureCredential` (`az login`) or set `AOAI_API_KEY` for key-based auth.

### Local stdio mode (any MCP client)

```bash
# Install the dotnet tool globally
dotnet tool install -g OpcUaKb.McpServer

# Run with stdio transport (all 15 tools)
SEARCH_ENDPOINT=https://<prefix>-search.search.windows.net \
SEARCH_API_KEY=<key> \
AOAI_ENDPOINT=https://<prefix>-foundry.openai.azure.com \
opcua-kb-mcp --stdio
```

Or from source:

```bash
dotnet run --project src/OpcUaKb.McpServer -- --stdio
```

### Environment Variables

| Variable | Required | Description |
|----------|----------|-------------|
| `SEARCH_ENDPOINT` | ✓ | Azure AI Search endpoint |
| `SEARCH_API_KEY` | ✓ | Azure AI Search admin key |
| `AOAI_ENDPOINT` | | Azure OpenAI / Foundry endpoint — enables `search_docs_rag` tool |
| `AOAI_API_KEY` | | AOAI key auth (if not using Managed Identity / `az login`) |
| `KB_NAME` | | Knowledge base name (default: `opcua-kb`) |
| `GPT_DEPLOYMENT` | | GPT model deployment name (default: `gpt-4o`) |
| `MCP_API_KEY` | | API key for authenticated access (defaults to `SEARCH_API_KEY` for read tools; `/upload-nodeset` uses this key explicitly and does not fall back to `SEARCH_API_KEY`) |
| `MCP_UPLOAD_KEY` | | Separate api-key for `POST /upload-nodeset`. Defaults to `MCP_API_KEY`. |
| `MCP_REQUIRE_AUTH` | | Set `true` to reject anonymous requests |
| `MCP_ANON_RATE_LIMIT` | | Max requests/min for anonymous callers (default: 10) |
| `MCP_AUTH_RATE_LIMIT` | | Max requests/min for authenticated callers (default: 0 = unlimited) |
