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
| Container App | `{prefix}-agent` | Custom engine agent (Bot Framework / Microsoft 365 Agents SDK). Scale 1–3, system MI + Cognitive Services OpenAI User role on Foundry. Receives messages from Teams/M365 Copilot via Azure Bot Service. |
| Azure Bot Service | `{prefix}-agent` | Bot channel registration (kind: `azurebot`, F0). Multi-tenant Entra app for JWT auth. Channels: `MsTeams`, `M365Extensions`. Endpoint points at agent Container App. |
| Log Analytics Workspace | `{prefix}-logs` | Pipeline and MCP server log collection |
| Azure Monitor Workbook | — | Pipeline dashboard (crawl/index/error tracking) |

## Deployment

### Which script to run

- **Existing infra (Search, Foundry, Storage, ACR, Container Apps Env, pipeline job, MCP server, Log Analytics, Workbook)** — use `infra/deploy.sh`. It deploys `main.bicep` with `botAppId=''`, so the Bot Service and its channels are skipped (the `agent` Container App still gets created with a placeholder image).
- **Custom engine agent (Bot Service + Teams/M365 channels)** — use `scripts/install-agent.ps1` instead. Bicep cannot create Entra apps, so the script:
  1. Creates (or reuses) a multi-tenant Entra app + client secret for the bot.
  2. Calls `az deployment group create` against `main.bicep` with the right `botAppId` / `botAppPassword` params so the `bot` + channel resources are materialized and the agent container gets `BOT_ID` / `BOT_PASSWORD` wired in.
  3. Builds and pushes the agent Docker image to ACR, then updates the Container App to that image.
  4. Packages the Teams app manifest (`appPackage/`) for sideloading into Teams or the M365 admin center.

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
10. **Custom Engine Agent** — `{prefix}-agent` Container App (external HTTP ingress on port 8080, scale 1–3), `Cognitive Services OpenAI User` role on Foundry for its system MI, plus an `azurebot` Bot Service + `MsTeamsChannel` + `M365Extensions` channel resources. The bot + channels are guarded by `if (!empty(botAppId))` so they only deploy when an Entra app id is supplied (via `scripts/install-agent.ps1`). When `botAppPassword` is set, it is stored as a secret and projected as the `BOT_PASSWORD` env var on the agent container.
11. **Azure Monitor Workbook** — Pipeline dashboard
12. **Outputs** — endpoints + connection details (see *Bicep Outputs* below)

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

| Parameter | Pipeline Job | MCP Server | Agent |
|-----------|-------------|------------|-------|
| CPU | 2 cores | 0.5 cores | 0.5 cores |
| Memory | 4 GiB | 1 GiB | 1 GiB |
| Timeout | 86400s (24h) | — | — |
| Scale | 1 (single execution) | 0–2 (HTTP auto-scale) | 1–3 (HTTP auto-scale) |
| Cron | `0 2 * * 0` (Sunday 2am UTC) | — | — (HTTP-triggered) |

> **Why the agent uses `minReplicas: 1` instead of scaling to zero:** Bot Framework / Microsoft 365 Copilot conversations expect sub-second turn responses. Scaling to zero would cause cold-start delays (image pull + container init) on the first message of every idle period, which users perceive as the bot being broken. Keeping one warm replica trades a small fixed cost for predictable latency.

### Azure Bot Service Configuration

| Parameter | Value | Notes |
|-----------|-------|-------|
| `kind` | `azurebot` | Channel registration only — no compute, no managed runtime |
| SKU | `F0` (free) | Sufficient for low-volume use; upgrade to `S1` for production SLAs |
| `msaAppType` | `MultiTenant` | Any organization can install the agent into Teams / M365 Copilot using the published Entra app id |
| Channels | `MsTeamsChannel`, `M365Extensions` | Teams chat + the Microsoft 365 Copilot extension channel |
| Endpoint | `https://{agent-fqdn}/api/messages` | Computed by Bicep from the agent Container App's ingress FQDN |
| Entra app | **Not** created by Bicep | ARM cannot create Entra app registrations — `scripts/install-agent.ps1` creates a multi-tenant app + client secret and passes them to Bicep as `botAppId` / `botAppPassword` |

The `bot`, `botTeamsChannel`, and `botM365Channel` resources in `main.bicep` are gated on `if (!empty(botAppId))`, so running plain `infra/deploy.sh` (which leaves `botAppId` blank) is safe and skips the bot entirely.

### Bicep Outputs

| Output | Value | Notes |
|--------|-------|-------|
| `searchEndpoint` | `https://{prefix}-search.search.windows.net` | |
| `searchApiKey` | (admin key) | Sensitive — emitted for use by `deploy.sh` |
| `foundryEndpoint` / `foundryProjectEndpoint` / `aoaiEndpoint` | Foundry account / project endpoints | |
| `storageConnectionString` | Storage connection string | Sensitive |
| `acrLoginServer` | `{prefix}registry.azurecr.io` | |
| `mcpEndpoint` | `https://{prefix}-search.search.windows.net/knowledgebases/{prefix}-kb/mcp?api-version=2025-11-01-preview` | Built-in KB-hosted MCP endpoint |
| `mcpServerEndpoint` | `https://{prefix}-mcp-server.<env>.azurecontainerapps.io` | Custom MCP server (11 tools + RAG) |
| `agentEndpoint` | `https://{prefix}-agent.<env>.azurecontainerapps.io` | Custom engine agent FQDN; `/api/messages` is the Bot Framework endpoint |
| `botName` | `{prefix}-agent` or `''` | Empty string until `botAppId` is provided (i.e., until `scripts/install-agent.ps1` has run) |

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
