# Infrastructure

All Azure resources are defined in [`main.bicep`](main.bicep) and deployed via [`deploy.sh`](deploy.sh).

## Azure Resources

| Resource | Derived Name | Purpose |
|----------|-------------|---------|
| AI Search (Standard) | `{prefix}-search` | Search index + knowledge base |
| Azure AI Foundry | `{prefix}-foundry` | AIServices account + default project; GPT-4o (30 TPM) + text-embedding-3-large (120 TPM). MI auth. |
| Blob Storage | `{prefix}storage` | Crawled content storage |
| Container Registry | `{prefix}registry` | Pipeline + MCP server Docker images |
| Container Apps Environment | `{prefix}-env` | Shared environment for job + app |
| Container Apps Job | `{prefix}-pipeline-job` | Weekly crawl + index (cron: `0 2 * * 0`, 24h timeout). System MI + Cognitive Services OpenAI User role. |
| Container App | `{prefix}-mcp-server` | MCP server with 11 tools + RAG (scale 0–2, HTTP auto-scale). System MI + Cognitive Services OpenAI User role. |
| Log Analytics Workspace | `{prefix}-logs` | Pipeline and MCP server log collection |
| Azure Monitor Workbook | — | Pipeline dashboard (crawl/index/error tracking) |

> **Note**: The OPC UA Expert agent (`OpcUaKb.HostedAgent`) is **not** in Bicep — it's a Foundry Hosted Agent provisioned by `azd provision` / `azd deploy` (after `azd ai agent init`) from `src/OpcUaKb.HostedAgent/agent.manifest.yaml`. The Foundry Toolbox that wraps the MCP server is declared as a `kind: toolbox` resource in that manifest and auto-provisioned by `azd provision`. See `scripts/install-toolbox-and-agent.ps1`.

## Deployment

### Which script to run

- **Existing infra (Search, Foundry, Storage, ACR, Container Apps Env, pipeline job, MCP server, Log Analytics, Workbook)** — use `infra/deploy.sh`. Deploys `main.bicep`.
- **Foundry Hosted Agent + Toolbox + Teams binding** — use `scripts/install-toolbox-and-agent.ps1`. Uses `azd ai agent` to provision the Toolbox declared in `agent.manifest.yaml`, build the agent container in ACR, deploy it to Foundry's managed runtime, optionally publish as an Agent Application, and bind to Teams via Foundry's Activity-bridge.

### One-command deployment

```bash
./infra/deploy.sh \
  -s <subscription-id> \
  -g rg-opcua-kb \
  -p opcua-kb \
  -l eastus
```

| Flag | Description | Default |
|------|-------------|---------|
| `-s, --subscription` | Azure subscription ID | (required) |
| `-g, --resource-group` | Resource group name | `rg-opcua-kb` |
| `-p, --prefix` | Resource name prefix | `opcua-kb` |
| `-l, --location` | Azure region | `eastus` |

The script is **idempotent** — safe to run multiple times. It performs:
1. Resource group creation
2. Bicep deployment (`main.bicep`)
3. Docker image build + push (pipeline + MCP server)
4. Container Apps Job + App creation/update
5. Knowledge Base setup (web knowledge source, GPT-4o model binding)
6. MCP endpoint configuration

### Prerequisites

- [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) (logged in with `az login`)
- [Docker](https://docs.docker.com/get-docker/) (for container builds)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [nbgv](https://github.com/dotnet/Nerdbank.GitVersioning) (`dotnet tool install -g nbgv`)

### Bicep Structure

`main.bicep` defines all resources in a single file with numbered sections:

1. **Parameters** — prefix, location, container image, CloudLib credentials (optional)
2. **AI Search** — Standard SKU with semantic ranker
3. **Azure AI Foundry** — AIServices account (`kind: AIServices`) + default project + model deployments
4. **Storage** — Blob storage for crawled content
5. **Container Registry** — Basic SKU with admin credentials
6. **Container Apps Environment** — Log Analytics integration
7. **Pipeline Job** — Container Apps Job with cron schedule, MI + secrets
8. **MCP Server** — Container App with ingress, scale-to-zero, rate limiting env vars
9. **Role Assignments** — `Cognitive Services OpenAI User` for pipeline + MCP server managed identities
10. **Azure Monitor Workbook** — Pipeline dashboard
11. **Outputs** — endpoints + connection details (see *Bicep Outputs* below)

### Validate Bicep

```bash
az bicep build --file infra/main.bicep
```

### Azure AI Search Configuration

| Parameter | Value | Notes |
|-----------|-------|-------|
| SKU | `standard` | Required for semantic ranker + knowledge bases |
| KB reasoning | `medium` | Upgraded from `low` for better query planning |
| KB API version | `2025-11-01-preview` | Agentic retrieval preview |

### Container Configuration

| Parameter | Pipeline Job | MCP Server |
|-----------|-------------|------------|
| CPU | 2 cores | 0.5 cores |
| Memory | 4 GiB | 1 GiB |
| Timeout | 86400s (24h) | — |
| Scale | 1 (single execution) | 0–2 (HTTP auto-scale) |
| Cron | `0 2 * * 0` (Sunday 2am UTC) | — |

> **Foundry Hosted Agent**: The agent's container is managed by Foundry's hosted-agent runtime (not by this Bicep). CPU/memory are declared in `src/OpcUaKb.HostedAgent/agent.yaml` (0.25 cores / 0.5 GiB by default). Foundry's runtime applies scale-to-zero with a 15-minute idle timeout and predictable cold-starts. See [`src/OpcUaKb.HostedAgent/README.md`](../src/OpcUaKb.HostedAgent/README.md).

### Bicep Outputs

| Output | Value | Notes |
|--------|-------|-------|
| `searchEndpoint` | `https://{prefix}-search.search.windows.net` | |
| `searchApiKey` | (admin key) | Sensitive — emitted for use by `deploy.sh` |
| `foundryEndpoint` / `foundryProjectEndpoint` / `aoaiEndpoint` | Foundry account / project endpoints | |
| `storageConnectionString` | Storage connection string | Sensitive |
| `acrLoginServer` | `{prefix}registry.azurecr.io` | |
| `mcpEndpoint` | `https://{prefix}-search.search.windows.net/knowledgebases/{prefix}-kb/mcp?api-version=2025-11-01-preview` | Built-in KB-hosted MCP endpoint |
| `mcpServerEndpoint` | `https://{prefix}-mcp-server.<env>.azurecontainerapps.io` | Custom MCP server (11 tools + RAG); the Foundry Toolbox proxies to this |

### Monitoring

The Azure Monitor Workbook "OPC UA Pipeline Dashboard" provides:
- Pipeline phase transitions and execution history
- Crawl progress (downloaded/queued/errors over time)
- Index progress (chunks/embedded/uploaded)
- Errors and warnings table
- Execution duration bar chart

Access via: **Azure Portal → Monitor → Workbooks → "OPC UA Pipeline Dashboard"**

Dashboard KQL queries use the `[PHASE] Key=Value` structured log format emitted by the pipeline.

## CI/CD

GitHub Actions workflow (`.github/workflows/ci.yml`):
- **Push/PR to main** — build + compile all projects (full git history for NBGV)
- **Push to main** — build both Docker images (pipeline + MCP server), push to GHCR with SemVer tags

Container image tags: `<version>` (e.g., `3.0.0`) + `latest` + `<sha>`
