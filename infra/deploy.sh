#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════════════
# OPC UA Knowledge Base — Azure Deployment Script
# Deploys all infrastructure, builds the pipeline image, and configures
# the knowledge base with MCP endpoint for Copilot CLI.
# ═══════════════════════════════════════════════════════════════════════
set -euo pipefail

# ── Colors ────────────────────────────────────────────────────────────
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

info()  { echo -e "${BLUE}[INFO]${NC}  $*"; }
ok()    { echo -e "${GREEN}[OK]${NC}    $*"; }
warn()  { echo -e "${YELLOW}[WARN]${NC}  $*"; }
fail()  { echo -e "${RED}[FAIL]${NC}  $*"; exit 1; }

# ── Defaults ──────────────────────────────────────────────────────────
SUBSCRIPTION=""
RESOURCE_GROUP="rg-opcua-kb"
PREFIX="opcua-kb"
LOCATION="eastus"

# ── Parse arguments ───────────────────────────────────────────────────
usage() {
  cat <<EOF
Usage: $(basename "$0") [OPTIONS]

Deploys the OPC UA Knowledge Base to Azure.

Options:
  -s, --subscription    Azure subscription ID (required)
  -g, --resource-group  Resource group name (default: ${RESOURCE_GROUP})
  -p, --prefix          Resource name prefix (default: ${PREFIX})
  -l, --location        Azure region (default: ${LOCATION})
  -h, --help            Show this help message
EOF
  exit 0
}

while [[ $# -gt 0 ]]; do
  case $1 in
    -s|--subscription)    SUBSCRIPTION="$2"; shift 2 ;;
    -g|--resource-group)  RESOURCE_GROUP="$2"; shift 2 ;;
    -p|--prefix)          PREFIX="$2"; shift 2 ;;
    -l|--location)        LOCATION="$2"; shift 2 ;;
    -h|--help)            usage ;;
    *) fail "Unknown option: $1. Use --help for usage." ;;
  esac
done

[[ -z "$SUBSCRIPTION" ]] && fail "Azure subscription ID is required. Use -s or --subscription."

# ── Derived names ─────────────────────────────────────────────────────
SEARCH_NAME="${PREFIX}-search"
ACR_NAME="${PREFIX//\-/}registry"
JOB_NAME="${PREFIX}-pipeline-job"
KB_NAME="${PREFIX}-kb"
INDEX_NAME="opcua-content-index-v2"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

# ── Step 0: Validate prerequisites ───────────────────────────────────
info "Checking prerequisites..."

command -v az     >/dev/null 2>&1 || fail "Azure CLI (az) is not installed. Install from https://aka.ms/install-azure-cli"
command -v docker >/dev/null 2>&1 || fail "Docker is not installed. Install from https://docs.docker.com/get-docker/"
command -v dotnet >/dev/null 2>&1 || fail ".NET SDK (dotnet) is not installed. Install from https://dot.net"

ok "All prerequisites found."

# ── Step 1: Set Azure subscription ───────────────────────────────────
info "Setting Azure subscription to ${SUBSCRIPTION}..."
az account set --subscription "$SUBSCRIPTION"
ok "Subscription set."

# ── Step 2: Create resource group ────────────────────────────────────
info "Ensuring resource group '${RESOURCE_GROUP}' exists in '${LOCATION}'..."
az group create \
  --name "$RESOURCE_GROUP" \
  --location "$LOCATION" \
  --output none 2>/dev/null || true
ok "Resource group ready."

# ── Step 3: Deploy Bicep template ────────────────────────────────────
info "Deploying Bicep template (this may take several minutes)..."
DEPLOY_OUTPUT=$(az deployment group create \
  --resource-group "$RESOURCE_GROUP" \
  --template-file "${SCRIPT_DIR}/main.bicep" \
  --parameters prefix="$PREFIX" location="$LOCATION" \
  --query 'properties.outputs' \
  --output json) || fail "Bicep deployment failed. Check the Azure portal for details."

SEARCH_ENDPOINT=$(echo "$DEPLOY_OUTPUT" | jq -r '.searchEndpoint.value')
SEARCH_API_KEY=$(echo "$DEPLOY_OUTPUT" | jq -r '.searchApiKey.value')
AOAI_ENDPOINT=$(echo "$DEPLOY_OUTPUT" | jq -r '.aoaiEndpoint.value')
FOUNDRY_ENDPOINT=$(echo "$DEPLOY_OUTPUT" | jq -r '.foundryEndpoint.value')
FOUNDRY_PROJECT_ENDPOINT=$(echo "$DEPLOY_OUTPUT" | jq -r '.foundryProjectEndpoint.value')
STORAGE_ACCOUNT_NAME=$(echo "$DEPLOY_OUTPUT" | jq -r '.storageAccountName.value')
ACR_LOGIN_SERVER=$(echo "$DEPLOY_OUTPUT" | jq -r '.acrLoginServer.value')
MCP_ENDPOINT=$(echo "$DEPLOY_OUTPUT" | jq -r '.mcpEndpoint.value')

ok "Infrastructure deployed."

# ── Step 4: Get version from Nerdbank.GitVersioning ──────────────────
VERSION=$(nbgv get-version -v NuGetPackageVersion 2>/dev/null || echo "0.0.0")
info "Building version ${VERSION}..."

# ── Step 4a: Build and push Docker image to ACR ──────────────────────
IMAGE_TAG="${ACR_LOGIN_SERVER}/${PREFIX}-pipeline:${VERSION}"
info "Building and pushing pipeline image to ACR..."
az acr build \
  --registry "$ACR_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --image "${PREFIX}-pipeline:${VERSION}" \
  --image "${PREFIX}-pipeline:latest" \
  --file "${REPO_ROOT}/Dockerfile" \
  "${REPO_ROOT}" \
  --no-logs || fail "ACR build failed. Ensure the Dockerfile is valid."
ok "Container image pushed: ${IMAGE_TAG}"

# ── Step 4b: Build and push MCP server image to ACR ──────────────────
MCP_IMAGE_TAG="${ACR_LOGIN_SERVER}/opcua-mcp-server:${VERSION}"
info "Building and pushing MCP server image to ACR..."
az acr build \
  --registry "$ACR_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --image "opcua-mcp-server:${VERSION}" \
  --image "opcua-mcp-server:latest" \
  --file "${REPO_ROOT}/Dockerfile.mcpserver" \
  "${REPO_ROOT}" \
  --no-logs || fail "MCP server ACR build failed."
ok "MCP server image pushed: ${MCP_IMAGE_TAG}"

# ── Step 5: Update Container Apps Job with the real image ────────────
info "Updating Container Apps Job with new image..."
az containerapp job update \
  --name "$JOB_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --image "$IMAGE_TAG" \
  --output none || fail "Failed to update Container Apps Job."
ok "Pipeline job updated."

# ── Step 5b: Update MCP server Container App ─────────────────────────
MCP_APP_NAME="${PREFIX}-mcp-server"
info "Updating MCP server Container App..."
az containerapp update \
  --name "$MCP_APP_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --image "$MCP_IMAGE_TAG" \
  --output none || fail "Failed to update MCP server Container App."
MCP_SERVER_FQDN=$(az containerapp show \
  --name "$MCP_APP_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --query "properties.configuration.ingress.fqdn" -o tsv)
ok "MCP server updated: https://${MCP_SERVER_FQDN}"

# ── Step 6: Create Web Knowledge Source ──────────────────────────────
info "Creating web knowledge source..."
SEARCH_BASE="https://${SEARCH_NAME}.search.windows.net"
API_VERSION="2025-11-01-preview"

# Idempotent: PUT is safe to call multiple times
HTTP_STATUS=$(curl -s -o /dev/null -w "%{http_code}" -X PUT \
  "${SEARCH_BASE}/knowledgesources/${PREFIX}-web-ks?api-version=${API_VERSION}" \
  -H "Content-Type: application/json" \
  -H "api-key: ${SEARCH_API_KEY}" \
  -d "{
    \"name\": \"${PREFIX}-web-ks\",
    \"kind\": \"web\",
    \"description\": \"OPC UA reference specifications from *.opcfoundation.org\",
    \"webParameters\": {
      \"domains\": {
        \"allowedDomains\": [
          { \"address\": \"reference.opcfoundation.org\", \"includeSubpages\": true },
          { \"address\": \"profiles.opcfoundation.org\", \"includeSubpages\": true },
          { \"address\": \"www.opcfoundation.org\", \"includeSubpages\": true },
          { \"address\": \"opcfoundation.org\", \"includeSubpages\": true }
        ]
      }
    }
  }")

[[ "$HTTP_STATUS" =~ ^2 ]] || warn "Web knowledge source creation returned HTTP ${HTTP_STATUS} (may already exist)."
ok "Web knowledge source configured."

# ── Step 6b: Create Index Knowledge Source ───────────────────────────
# Binds the agentic-retrieval KB to the structured search index built by
# the pipeline (per-section spec docs, NodeSet nodes, summaries, ObjectType
# hierarchies). The index's built-in vectorizer authenticates to the
# Foundry account via the search service's managed identity, so no key is
# needed here. semanticConfigurationName must match the index's config.
info "Creating index knowledge source..."
HTTP_STATUS=$(curl -s -o /dev/null -w "%{http_code}" -X PUT \
  "${SEARCH_BASE}/knowledgesources/${PREFIX}-index-ks?api-version=${API_VERSION}" \
  -H "Content-Type: application/json" \
  -H "api-key: ${SEARCH_API_KEY}" \
  -d "{
    \"name\": \"${PREFIX}-index-ks\",
    \"kind\": \"searchIndex\",
    \"description\": \"OPC UA indexed specification + NodeSet content (${INDEX_NAME}): per-section spec docs, NodeSet nodes, summaries, and ObjectType hierarchies.\",
    \"searchIndexParameters\": {
      \"searchIndexName\": \"${INDEX_NAME}\",
      \"semanticConfigurationName\": \"semantic_config\",
      \"searchFields\": [
        { \"name\": \"page_chunk\" },
        { \"name\": \"section_title\" },
        { \"name\": \"title\" },
        { \"name\": \"spec_title\" },
        { \"name\": \"description\" }
      ],
      \"sourceDataFields\": [
        { \"name\": \"page_chunk\" },
        { \"name\": \"spec_title\" },
        { \"name\": \"section_title\" },
        { \"name\": \"title\" }
      ]
    }
  }")

[[ "$HTTP_STATUS" =~ ^2 ]] || warn "Index knowledge source creation returned HTTP ${HTTP_STATUS} (may already exist)."
ok "Index knowledge source configured."

# ── Step 7: Create Knowledge Base ────────────────────────────────────
info "Creating knowledge base '${KB_NAME}'..."
HTTP_STATUS=$(curl -s -o /dev/null -w "%{http_code}" -X PUT \
  "${SEARCH_BASE}/knowledgebases/${KB_NAME}?api-version=${API_VERSION}" \
  -H "Content-Type: application/json" \
  -H "api-key: ${SEARCH_API_KEY}" \
  -d "{
    \"name\": \"${KB_NAME}\",
    \"description\": \"OPC UA knowledge base for answering questions about OPC UA specifications, generating test code, and looking up NodeSet definitions.\",
    \"knowledgeSources\": [
      { \"name\": \"${PREFIX}-web-ks\" },
      { \"name\": \"${PREFIX}-index-ks\" }
    ],
    \"models\": [
      {
        \"kind\": \"azureOpenAI\",
        \"azureOpenAIParameters\": {
          \"resourceUri\": \"${AOAI_ENDPOINT}\",
          \"deploymentId\": \"gpt-4o\",
          \"modelName\": \"gpt-4o\"
        }
      }
    ],
    \"retrievalReasoningEffort\": { \"kind\": \"medium\" },
    \"outputMode\": \"answerSynthesis\",
    \"retrievalInstructions\": \"Use the OPC UA indexed knowledge source for detailed specification content including tables, diagrams, and code examples. Use the OPC UA web knowledge source for real-time lookups and content that may not yet be indexed. Both sources cover OPC 10000 specification parts (1-26+), companion specifications, and NodeSet definitions. The index contains structured NodeSet data with filterable fields: node_class, modelling_rule, spec_part, browse_name, parent_type, data_type. Summary documents (content_type=nodeset_summary) provide pre-computed per-spec and cross-spec statistics. Hierarchy documents (content_type=nodeset_hierarchy) provide per-ObjectType inheritance chains and member counts.\",
    \"answerInstructions\": \"Provide technically precise answers grounded in the OPC UA specifications. Include specification part numbers and section references when available. When generating code, use the OPC UA .NET Standard SDK conventions. For protocol-level questions, cite the exact service names and parameter structures. For aggregation questions (counts, comparisons), use nodeset_summary documents. For type hierarchy questions, use nodeset_hierarchy documents. Format code blocks with C# syntax.\"
  }")

[[ "$HTTP_STATUS" =~ ^2 ]] || warn "Knowledge base creation returned HTTP ${HTTP_STATUS} (may already exist)."
ok "Knowledge base '${KB_NAME}' configured."

# ── Step 8: Test query ───────────────────────────────────────────────
info "Running test query against the knowledge base..."
QUERY_RESULT=$(curl -s -X POST \
  "${SEARCH_BASE}/knowledgebases/${KB_NAME}/retrieve?api-version=${API_VERSION}" \
  -H "Content-Type: application/json" \
  -H "api-key: ${SEARCH_API_KEY}" \
  -d '{
    "messages": [{ "role": "user", "content": [{ "type": "text", "text": "What is OPC UA?" }] }],
    "retrievalReasoningEffort": { "kind": "low" }
  }' 2>/dev/null) || true

if echo "$QUERY_RESULT" | jq -e '.response[0].content[0].text' >/dev/null 2>&1; then
  ok "Test query succeeded."
else
  warn "Test query did not return an answer (knowledge base may still be indexing)."
fi

# ── Summary ───────────────────────────────────────────────────────────
echo ""
echo -e "${GREEN}════════════════════════════════════════════════════════════${NC}"
echo -e "${GREEN} Deployment Complete!${NC}"
echo -e "${GREEN}════════════════════════════════════════════════════════════${NC}"
echo ""
echo -e "  Resource Group:   ${BLUE}${RESOURCE_GROUP}${NC}"
echo -e "  Search Endpoint:  ${BLUE}${SEARCH_ENDPOINT}${NC}"
echo -e "  Foundry Endpoint: ${BLUE}${FOUNDRY_ENDPOINT}${NC}"
echo -e "  Foundry Project:  ${BLUE}${FOUNDRY_PROJECT_ENDPOINT}${NC}"
echo -e "  ACR Server:       ${BLUE}${ACR_LOGIN_SERVER}${NC}"
echo -e "  KB MCP Endpoint:  ${BLUE}${MCP_ENDPOINT}${NC}"
echo -e "  MCP Server:       ${BLUE}https://${MCP_SERVER_FQDN}${NC}"
echo ""
echo -e "  ${YELLOW}To configure GitHub Copilot CLI:${NC}"
echo ""
echo -e "  Add to ~/.copilot/mcp.json:"
echo ""
echo -e "    {"
echo -e "      \"mcpServers\": {"
echo -e "        \"opcua-kb\": {"
echo -e "          \"type\": \"http\","
echo -e "          \"url\": \"${MCP_ENDPOINT}\","
echo -e "          \"headers\": {"
echo -e "            \"api-key\": \"<your-search-api-key>\""
echo -e "          }"
echo -e "        },"
echo -e "        \"opcua-kb-tools\": {"
echo -e "          \"type\": \"http\","
echo -e "          \"url\": \"https://${MCP_SERVER_FQDN}/mcp\""
echo -e "        }"
echo -e "      }"
echo -e "    }"
echo ""
echo -e "${GREEN}════════════════════════════════════════════════════════════${NC}"
