# Infrastructure

All Azure resources are defined in [`main.bicep`](main.bicep) and deployed via [`deploy.sh`](deploy.sh).

## Azure Resources

| Resource | Derived Name | Purpose |
|----------|-------------|---------|
| AI Search (Standard) | `{prefix}-search` | Search index (`opcua-content-index-v2`) + knowledge base |
| Azure AI Foundry | `{prefix}-foundry` | AIServices account + default project; GPT-4o (30 TPM) + text-embedding-3-large (120 TPM). MI auth. |
| Blob Storage | `{prefix}storage` | Crawled content storage. **MI-only auth** (`allowSharedKeyAccess: false`), **publicNetworkAccess: Disabled** with `networkAcls.defaultAction: Deny` — reachable only through the VNet's private endpoint. |
| Container Registry | `{prefix}registry` | Pipeline + MCP server Docker images |
| Container Apps Environment | `{prefix}-env` | Workload-profile env (Consumption profile) with VNet integration via `apps-subnet`. |
| VNet | `{prefix}-vnet` | `10.20.0.0/24`. `apps-subnet` /26 (delegated to `Microsoft.App/environments`), `pe-subnet` /28 (privateEndpointNetworkPolicies Disabled). |
| Private DNS zone | `privatelink.blob.core.windows.net` | Linked to the VNet; resolves the storage account's blob endpoint to the PE private IP for any VNet client. |
| Private endpoint | `{prefix}-storage-pe` | For storage `blob` group. Auto-approved (same subscription). |
| Container Apps Job | `{prefix}-pipeline-job` | Weekly crawl + index (cron: `0 2 * * 0`, 24h timeout). Runs in the VNet-integrated workload-profile env; writes to storage via private endpoint. System MI + Cognitive Services OpenAI User + Storage Blob Data Contributor. |
| Container App | `{prefix}-mcp-server` | MCP server with 11 tools + RAG. **Workload-profile env, VNet-integrated.** Reads/writes storage exclusively via private endpoint. System MI + Cognitive Services OpenAI User + Storage Blob Data Contributor. |
| Log Analytics Workspace | `{prefix}-logs` | Pipeline and MCP server log collection |
| Azure Monitor Workbook | — | Pipeline dashboard (crawl/index/error tracking) |

> **Note**: The OPC UA Expert agent (`OpcUaKb.HostedAgent`) is **not** in this Bicep — it's a Foundry Hosted Agent provisioned by `azd provision` / `azd deploy` from `src/OpcUaKb.HostedAgent/`. The Hosted Agent connects to the MCP server here over HTTPS via `MCP_SERVER_URL`.
>
> **Region**: Foundry Hosted Agents are preview-only in select regions (westus3, westus, norwayeast, francecentral, japaneast). All KB resources colocate with the Hosted Agent in **westus3** (single region, single resource group `rg-opcua-kb`).

## Deployment

### Which script to run

- **Existing infra (Search, Foundry, Storage, ACR, Container Apps Env, pipeline job, MCP server, Log Analytics, Workbook)** — use `infra/deploy.sh`. Deploys `main.bicep`.
- **Foundry Hosted Agent** — `cd src/OpcUaKb.HostedAgent && azd provision && azd deploy`. The agent provisions its own Foundry account, project, ACR, and gpt-4o deployment in a Hosted-Agent-supported region (e.g., westus3), then deploys the container. The agent calls the MCP server (deployed by `infra/deploy.sh`) cross-region via `MCP_SERVER_URL`. See [`src/OpcUaKb.HostedAgent/README.md`](../src/OpcUaKb.HostedAgent/README.md).

> The legacy `scripts/install-toolbox-and-agent.ps1` is no longer the recommended path — the Hosted Agent no longer uses a Foundry Toolbox. Pure `azd provision` + `azd deploy` is sufficient.

### One-command deployment

```bash
./infra/deploy.sh \
  -s <subscription-id> \
  -g rg-opcua-kb \
  -p opcua-kb \
  -l westus3
```

| Flag | Description | Default |
|------|-------------|---------|
| `-s, --subscription` | Azure subscription ID | (required) |
| `-g, --resource-group` | Resource group name | `rg-opcua-kb` |
| `-p, --prefix` | Resource name prefix | `opcua-kb` |
| `-l, --location` | Azure region | `eastus` (override to `westus3` for production — colocates KB with Foundry Hosted Agent) |

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
9. **Role Assignments** — `Cognitive Services OpenAI User` for pipeline + MCP server managed identities; **`Storage Blob Data Contributor`** for the pipeline MI (storage is MI-only — shared-key auth disabled)
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
| `storageAccountName` | Storage account name | Used by Pipeline + Indexer with DefaultAzureCredential (shared-key auth disabled) |
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

Container image tags: `<version>` (e.g., `4.0.0`) + `latest` + `<sha>`
