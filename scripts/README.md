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

### `install-agent.ps1` (PowerShell) / `install-agent.sh` (Bash)

End-to-end automation for deploying the OPC UA Custom Engine Agent (Microsoft 365 Agents SDK / Bot Framework) to Azure and Microsoft 365.

```powershell
.\scripts\install-agent.ps1 [-ResourceGroup rg-opcua-kb] [-Prefix opcua-kb] [-Location eastus] [-SkipImageBuild]
```

```bash
./scripts/install-agent.sh [-g rg-opcua-kb] [-p opcua-kb] [-l eastus] [--skip-image-build]
```

**What it does (idempotent — safe to re-run):**

1. Creates or finds Microsoft Entra ID multi-tenant app registration (`OPC UA KB Agent`) with the correct redirect URI (`https://token.botframework.com/.auth/web/redirect`)
2. Generates a 2-year client secret (appended — doesn't invalidate prior secrets in flight)
3. Sets the `identifierUris` to `api://botid-{appId}` (required by Teams manifest)
4. Builds the agent Docker image via ACR (`Dockerfile.agent`)
5. Deploys the Bicep template with `botAppId` and `botAppPassword` parameters
6. Updates the Container App revision to pick up the new image
7. Sets the Bot Service messaging endpoint to the agent's FQDN
8. Generates the Teams app package (substitutes `${{BOT_ID}}` and `${{TEAMS_APP_ID}}`, copies icons, zips)

**Output:** `agents/m365-agent/appPackage/build/opcua-kb-agent.zip` ready to upload via Teams Admin Center.

**Prerequisites:**
- `az` CLI (logged in)
- An existing `rg-opcua-kb` resource group (the script doesn't create infrastructure from scratch — run `infra/deploy.sh` first to create the base resources, then `install-agent.ps1` to add the agent)

**Note:** The script uses **Python-direct invocation** of the Azure CLI on Windows to avoid `cmd.exe` truncating client secrets containing special characters like `&`.

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
