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
2. Adds/updates the `opcua-kb-tools` MCP server entry (single endpoint for all 11 tools including RAG Q&A)
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

## Agent Deployment Scripts

### `install-toolbox-and-agent.ps1` (PowerShell)

End-to-end automation for deploying the OPC UA Hosted Agent to **Azure AI Foundry** as a managed Hosted Agent using the **Responses protocol** and a **Foundry Toolbox** that wraps the MCP server. This replaces the legacy Bot Framework / Microsoft 365 Agents SDK deployment.

```powershell
.\scripts\install-toolbox-and-agent.ps1 `
    [-ResourceGroup rg-opcua-kb] `
    [-FoundryAccountName opcua-kb-foundry] `
    [-FoundryProjectName opcua-kb-project] `
    [-McpServerUrl https://opcua-kb-mcp-server.<env>.azurecontainerapps.io/] `
    [-Location eastus] `
    [-AgentDir ..\src\OpcUaKb.HostedAgent] `
    [-SkipAzdInit] [-SkipProvision] `
    [-PublishAsApp] [-BindToTeams]
```

**What it does (idempotent — safe to re-run):**

1. Pre-flight: verifies `az`, `azd`, `azd ai agents` extension, agent dir + manifest, resolves MCP server URL.
2. Auth: `az account set`, soft RBAC check (Azure AI Project Manager on the Foundry project), `azd auth login` for the Microsoft tenant.
3. `azd ai agent init`: writes `azure.yaml` if missing.
4. `azd provision`: creates the Foundry Toolbox declared in `agent.manifest.yaml` (the `kind: toolbox` resource) plus the agent identity + RBAC.
5. `azd deploy`: builds the container in ACR remotely and creates a new agent version. Captures the agent endpoint.
6. **Optional** `-PublishAsApp`: wraps the agent version in a Foundry Agent Application with a stable endpoint and dedicated Entra agent identity.
7. **Optional** `-BindToTeams`: binds the Agent Application to a Teams channel via Foundry's Activity-bridge.
8. Smoke test: invokes the agent with `azd ai agent invoke --verbose` and looks for tool-call traces.

**Architecture (new):**
```
Teams / Web Chat → Foundry Agent Application (Activity bridge)
  → OpcUaKb.HostedAgent (Responses protocol container)
    → Foundry Responses API (built-in tool loop)
      → Foundry Toolbox "opcua-kb-tools"
        → OpcUaKb.McpServer (Container App, still hosts the 11 tools)
          → Azure AI Search
```

**Prerequisites:**
- `az` CLI (logged in to the right tenant)
- `azd` CLI v1.24.0+ with the `azure.ai.agents` extension
- An existing `rg-opcua-kb` resource group with `opcua-kb-foundry` AIServices account, MCP server Container App, and a `gpt-4o` model deployment (run `infra/deploy.sh` first)

**Note:** The script uses **Python-direct invocation** of the Azure CLI on Windows to avoid `cmd.exe` truncating secrets containing special characters like `&` (carried over from `install-agent.ps1`).

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

This single endpoint provides all 11 tools: structured search, RAG Q&A (`search_docs_rag`), compliance validation, version comparison, and model design suggestions.

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

# Run with stdio transport (all 11 tools)
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
| `MCP_API_KEY` | | API key for authenticated access (defaults to `SEARCH_API_KEY`) |
| `MCP_REQUIRE_AUTH` | | Set `true` to reject anonymous requests |
| `MCP_ANON_RATE_LIMIT` | | Max requests/min for anonymous callers (default: 10) |
| `MCP_AUTH_RATE_LIMIT` | | Max requests/min for authenticated callers (default: 0 = unlimited) |
