#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════════════
# OPC UA KB Custom Engine Agent — End-to-End Install (bash)
#
# Idempotent, end-to-end deployment of the OPC UA KB Custom Engine Agent
# (Microsoft 365 Agents SDK / Bot Framework) to Azure + Microsoft 365.
#
# What it does:
#   1. Validates prerequisites (az, jq, zip)
#   2. Ensures an Entra app registration exists for the bot
#   3. Rotates / creates a client secret for that app
#   4. Builds & pushes the agent container image to ACR
#   5. Runs the main Bicep deployment with botAppId / botAppPassword
#   6. Forces a Container App revision update to pick up the new image
#   7. Updates the Bot Service messaging endpoint with the agent FQDN
#   8. Generates a Teams app package (.zip) for sideloading
#
# Usage:
#   ./scripts/install-agent.sh
#   ./scripts/install-agent.sh -g rg-opcua-kb -p opcua-kb -l eastus
#   ./scripts/install-agent.sh --skip-image-build
# ═══════════════════════════════════════════════════════════════════════
set -euo pipefail

# ── Colors ────────────────────────────────────────────────────────────
RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; BLUE='\033[0;34m'; NC='\033[0m'
info()  { echo -e "${BLUE}[INFO]${NC}  $*"; }
ok()    { echo -e "${GREEN}[OK]${NC}    $*"; }
warn()  { echo -e "${YELLOW}[WARN]${NC}  $*"; }
fail()  { echo -e "${RED}[FAIL]${NC}  $*"; exit 1; }

# ── Defaults ──────────────────────────────────────────────────────────
RESOURCE_GROUP="rg-opcua-kb"
PREFIX="opcua-kb"
LOCATION="eastus"
APP_DISPLAY_NAME="OPC UA KB Agent"
SKIP_IMAGE_BUILD=0

usage() {
  cat <<EOF
Usage: $(basename "$0") [OPTIONS]

End-to-end install of the OPC UA KB Custom Engine Agent.

Options:
  -g, --resource-group   Resource group name (default: ${RESOURCE_GROUP})
  -p, --prefix           Resource name prefix (default: ${PREFIX})
  -l, --location         Azure region (default: ${LOCATION})
  -n, --app-name         Entra app display name (default: ${APP_DISPLAY_NAME})
      --skip-image-build Skip building/pushing the agent container image
  -h, --help             Show this help message
EOF
  exit 0
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    -g|--resource-group)   RESOURCE_GROUP="$2"; shift 2 ;;
    -p|--prefix)           PREFIX="$2"; shift 2 ;;
    -l|--location)         LOCATION="$2"; shift 2 ;;
    -n|--app-name)         APP_DISPLAY_NAME="$2"; shift 2 ;;
    --skip-image-build)    SKIP_IMAGE_BUILD=1; shift ;;
    -h|--help)             usage ;;
    *) fail "Unknown option: $1. Use --help for usage." ;;
  esac
done

# ── Resolve repo root ─────────────────────────────────────────────────
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
cd "${REPO_ROOT}"

# ── Derived names ─────────────────────────────────────────────────────
ACR_NAME="${PREFIX//-/}registry"
AGENT_APP="${PREFIX}-agent"
BOT_NAME="${PREFIX}-agent"
IMAGE_REPO="opcua-kb-agent"
IMAGE_TAG="latest"
IMAGE_REF="${ACR_NAME}.azurecr.io/${IMAGE_REPO}:${IMAGE_TAG}"

AGENT_DIR="${REPO_ROOT}/agents/m365-agent"
MANIFEST_TPL="${AGENT_DIR}/appPackage/manifest.template.json"
COLOR_ICON="${AGENT_DIR}/appPackage/color.png"
OUTLINE_ICON="${AGENT_DIR}/appPackage/outline.png"
BUILD_DIR="${AGENT_DIR}/appPackage/build"
ZIP_PATH="${BUILD_DIR}/opcua-kb-agent.zip"

# ════════════════════════════════════════════════════════════════════════
# Step 1: Validate prerequisites
# ════════════════════════════════════════════════════════════════════════
info "Validating prerequisites..."
command -v az  >/dev/null 2>&1 || fail "Azure CLI (az) not found. Install from https://aka.ms/install-azure-cli"
command -v jq  >/dev/null 2>&1 || fail "jq not found. Install from https://stedolan.github.io/jq/"
command -v zip >/dev/null 2>&1 || fail "zip not found. Install via your package manager (apt/brew/choco)."

ACCOUNT_JSON="$(az account show -o json 2>/dev/null || true)"
[[ -z "$ACCOUNT_JSON" ]] && fail "Not logged in to Azure. Run 'az login' first."
SUBSCRIPTION="$(echo "$ACCOUNT_JSON" | jq -r '.id')"
TENANT_ID="$(echo "$ACCOUNT_JSON" | jq -r '.tenantId')"
SUB_NAME="$(echo "$ACCOUNT_JSON" | jq -r '.name')"
ok "Subscription : ${SUB_NAME} (${SUBSCRIPTION})"
ok "Tenant       : ${TENANT_ID}"

[[ -f "$MANIFEST_TPL" ]] || fail "Manifest template not found: $MANIFEST_TPL"

# ════════════════════════════════════════════════════════════════════════
# Step 2: Ensure resource group exists
# ════════════════════════════════════════════════════════════════════════
info "Ensuring resource group '${RESOURCE_GROUP}' exists in ${LOCATION}..."
az group create -n "$RESOURCE_GROUP" -l "$LOCATION" -o none
ok "Resource group ready."

# ════════════════════════════════════════════════════════════════════════
# Step 3: Create or get Entra app registration
# ════════════════════════════════════════════════════════════════════════
info "Looking up Entra app registration: ${APP_DISPLAY_NAME}"

EXISTING_APPS="$(az ad app list --display-name "$APP_DISPLAY_NAME" --query '[].{appId:appId,id:id}' -o json 2>/dev/null || echo '[]')"
APP_ID="$(echo "$EXISTING_APPS" | jq -r '.[0].appId // empty')"
APP_OBJECT_ID="$(echo "$EXISTING_APPS" | jq -r '.[0].id // empty')"

if [[ -n "$APP_ID" ]]; then
  ok "Found existing app registration: ${APP_ID}"
else
  info "Creating new app registration..."
  # Bot Framework requires:
  #   - sign-in audience: AzureADMultipleOrgs (multi-tenant) so the bot can
  #     receive messages from any Teams / Copilot tenant.
  #   - web reply URL https://token.botframework.com/.auth/web/redirect
  #     for OAuth handoff via Bot Framework Token Service.
  CREATE_OUT="$(az ad app create \
    --display-name "$APP_DISPLAY_NAME" \
    --sign-in-audience AzureADMultipleOrgs \
    --web-redirect-uris "https://token.botframework.com/.auth/web/redirect" \
    -o json)"
  APP_ID="$(echo "$CREATE_OUT" | jq -r '.appId')"
  APP_OBJECT_ID="$(echo "$CREATE_OUT" | jq -r '.id')"
  [[ -z "$APP_ID" || "$APP_ID" == "null" ]] && fail "Failed to create Entra app registration."
  ok "Created app registration: ${APP_ID}"
  # Eventual consistency — give Graph a moment before we touch the app again.
  sleep 5
fi

# Make sure the redirect URI is present on existing apps too (idempotent).
info "Ensuring Bot Framework redirect URI is registered..."
az ad app update --id "$APP_ID" --web-redirect-uris "https://token.botframework.com/.auth/web/redirect" -o none
ok "Redirect URI ensured."

# ════════════════════════════════════════════════════════════════════════
# Step 4: Set identifierUris (api://botid-{appId})
# ════════════════════════════════════════════════════════════════════════
BOT_IDENTIFIER_URI="api://botid-${APP_ID}"
info "Setting identifierUris to ${BOT_IDENTIFIER_URI} ..."
az ad app update --id "$APP_ID" --identifier-uris "$BOT_IDENTIFIER_URI" -o none
ok "identifierUris set."

# ════════════════════════════════════════════════════════════════════════
# Step 5: Create / rotate client secret
# ════════════════════════════════════════════════════════════════════════
info "Rotating client secret 'agent-deploy-secret' (2 year lifetime)..."
CRED_OUT="$(az ad app credential reset \
    --id "$APP_ID" \
    --display-name "agent-deploy-secret" \
    --years 2 \
    --append \
    -o json)"
APP_PASSWORD="$(echo "$CRED_OUT" | jq -r '.password')"
[[ -z "$APP_PASSWORD" || "$APP_PASSWORD" == "null" ]] && fail "Empty client secret returned."
ok "Client secret rotated (will be passed to Bicep as a secure parameter)."

# ════════════════════════════════════════════════════════════════════════
# Step 6: Ensure a service principal exists for the app
# ════════════════════════════════════════════════════════════════════════
info "Ensuring service principal exists for app ${APP_ID}..."
SP_EXISTS="$(az ad sp show --id "$APP_ID" --query "id" -o tsv 2>/dev/null || true)"
if [[ -z "$SP_EXISTS" ]]; then
  az ad sp create --id "$APP_ID" -o none
  ok "Service principal created."
else
  ok "Service principal already exists."
fi

# ════════════════════════════════════════════════════════════════════════
# Step 7: Resolve existing pipelineImage so we don't accidentally reset it
# ════════════════════════════════════════════════════════════════════════
info "Detecting existing pipeline image (to preserve across deploys)..."
EXISTING_PIPELINE_IMAGE="$(az containerapp job show \
    -n "${PREFIX}-pipeline-job" \
    -g "$RESOURCE_GROUP" \
    --query "properties.template.containers[0].image" \
    -o tsv 2>/dev/null || true)"
if [[ -n "$EXISTING_PIPELINE_IMAGE" && "$EXISTING_PIPELINE_IMAGE" != mcr.microsoft.com/* ]]; then
  ok "Reusing existing pipeline image: $EXISTING_PIPELINE_IMAGE"
else
  EXISTING_PIPELINE_IMAGE=""
  warn "No existing pipeline image found — Bicep will use the default placeholder."
fi

# ════════════════════════════════════════════════════════════════════════
# Step 8: Build & push agent image to ACR (if ACR exists)
# ════════════════════════════════════════════════════════════════════════
if [[ "$SKIP_IMAGE_BUILD" -eq 1 ]]; then
  warn "Skipping agent image build (--skip-image-build)."
else
  ACR_EXISTS="$(az acr show -n "$ACR_NAME" -g "$RESOURCE_GROUP" --query "name" -o tsv 2>/dev/null || true)"
  if [[ -z "$ACR_EXISTS" ]]; then
    warn "ACR '${ACR_NAME}' does not exist yet. Will run Bicep first, then build."
  else
    info "Building and pushing agent image to ACR (${ACR_NAME})..."
    az acr build \
        --registry "$ACR_NAME" \
        --resource-group "$RESOURCE_GROUP" \
        --image "${IMAGE_REPO}:${IMAGE_TAG}" \
        --file "Dockerfile.agent" \
        "."
    ok "Image pushed: ${IMAGE_REF}"
  fi
fi

# ════════════════════════════════════════════════════════════════════════
# Step 9: Run the main Bicep deployment with botAppId / botAppPassword
# ════════════════════════════════════════════════════════════════════════
info "Running Bicep deployment (this may take several minutes)..."

BICEP_PARAMS=(
  "prefix=${PREFIX}"
  "location=${LOCATION}"
  "botAppId=${APP_ID}"
  "botAppPassword=${APP_PASSWORD}"
)
if [[ -n "$EXISTING_PIPELINE_IMAGE" ]]; then
  BICEP_PARAMS+=("pipelineImage=${EXISTING_PIPELINE_IMAGE}")
fi

az deployment group create \
    -g "$RESOURCE_GROUP" \
    --template-file "infra/main.bicep" \
    --parameters "${BICEP_PARAMS[@]}" \
    -o none
ok "Bicep deployment complete."

# If we couldn't push the image before Bicep (because ACR didn't exist),
# do it now and continue.
if [[ "$SKIP_IMAGE_BUILD" -ne 1 ]]; then
  ACR_EXISTS="$(az acr show -n "$ACR_NAME" -g "$RESOURCE_GROUP" --query "name" -o tsv 2>/dev/null || true)"
  if [[ -n "$ACR_EXISTS" ]]; then
    IMG_PUSHED="$(az acr repository show -n "$ACR_NAME" --image "${IMAGE_REPO}:${IMAGE_TAG}" --query "name" -o tsv 2>/dev/null || true)"
    if [[ -z "$IMG_PUSHED" ]]; then
      info "ACR exists now — building agent image post-deploy..."
      az acr build \
          --registry "$ACR_NAME" \
          --resource-group "$RESOURCE_GROUP" \
          --image "${IMAGE_REPO}:${IMAGE_TAG}" \
          --file "Dockerfile.agent" \
          "."
      ok "Image pushed: ${IMAGE_REF}"
    fi
  fi
fi

# ════════════════════════════════════════════════════════════════════════
# Step 10: Force Container App revision update with new image
#
# Bicep won't trigger a new revision when the image tag is the same
# ("latest"), so we explicitly bump a revision-suffix.
# ════════════════════════════════════════════════════════════════════════
REVISION_SUFFIX="deploy-$(date +%Y%m%d%H%M%S)"
info "Updating Container App ${AGENT_APP} to revision ${REVISION_SUFFIX}..."
az containerapp update \
    -n "$AGENT_APP" \
    -g "$RESOURCE_GROUP" \
    --image "$IMAGE_REF" \
    --revision-suffix "$REVISION_SUFFIX" \
    -o none
ok "Container App updated."

# ════════════════════════════════════════════════════════════════════════
# Step 11: Resolve agent FQDN and (re)point the Bot Service endpoint
# ════════════════════════════════════════════════════════════════════════
info "Resolving agent FQDN..."
FQDN="$(az containerapp show \
    -n "$AGENT_APP" \
    -g "$RESOURCE_GROUP" \
    --query "properties.configuration.ingress.fqdn" \
    -o tsv)"
[[ -z "$FQDN" ]] && fail "Could not resolve agent FQDN."
ok "Agent FQDN: ${FQDN}"

BOT_ENDPOINT="https://${FQDN}/api/messages"
info "Updating Bot Service '${BOT_NAME}' messaging endpoint -> ${BOT_ENDPOINT}"
az bot update \
    -n "$BOT_NAME" \
    -g "$RESOURCE_GROUP" \
    --endpoint "$BOT_ENDPOINT" \
    -o none
ok "Bot endpoint updated."

# ════════════════════════════════════════════════════════════════════════
# Step 12: Generate Teams app package
# ════════════════════════════════════════════════════════════════════════
info "Generating Teams app package..."
mkdir -p "$BUILD_DIR"

# Reuse a stable Teams app id once one has been generated. Each Teams
# tenant treats this id as the unique identity for the side-loaded app,
# so regenerating it on every run would create duplicate app entries.
TEAMS_APP_ID_FILE="${BUILD_DIR}/.teams-app-id"
if [[ -f "$TEAMS_APP_ID_FILE" ]]; then
  TEAMS_APP_ID="$(tr -d '[:space:]' < "$TEAMS_APP_ID_FILE")"
  ok "Reusing Teams app id: ${TEAMS_APP_ID}"
else
  if command -v uuidgen >/dev/null 2>&1; then
    TEAMS_APP_ID="$(uuidgen | tr 'A-Z' 'a-z')"
  else
    TEAMS_APP_ID="$(python3 -c 'import uuid; print(uuid.uuid4())')"
  fi
  echo -n "$TEAMS_APP_ID" > "$TEAMS_APP_ID_FILE"
  ok "Generated new Teams app id: ${TEAMS_APP_ID}"
fi

# Use jq for safe, escape-aware substitution of the placeholders in the
# manifest template (sed would mishandle slashes / special chars in ids).
BUILT_MANIFEST="${BUILD_DIR}/manifest.json"
jq --arg botId "$APP_ID" --arg teamsId "$TEAMS_APP_ID" \
   '(.. | strings) |= (gsub("\\$\\{\\{BOT_ID\\}\\}"; $botId) | gsub("\\$\\{\\{TEAMS_APP_ID\\}\\}"; $teamsId))' \
   "$MANIFEST_TPL" > "$BUILT_MANIFEST"

cp -f "$COLOR_ICON"   "${BUILD_DIR}/color.png"
cp -f "$OUTLINE_ICON" "${BUILD_DIR}/outline.png"

# Zip just the three required files (NOT .teams-app-id) so Teams accepts it.
rm -f "$ZIP_PATH"
( cd "$BUILD_DIR" && zip -q "$(basename "$ZIP_PATH")" manifest.json color.png outline.png )
ok "Teams app package: ${ZIP_PATH}"

# ════════════════════════════════════════════════════════════════════════
# Step 13: Summary
# ════════════════════════════════════════════════════════════════════════
WEB_CHAT_URL="https://dev.botframework.com/bots/channels?id=${BOT_NAME}"

echo ""
echo -e "${GREEN}════════════════════════════════════════════════════════════════════${NC}"
echo -e "${GREEN} OPC UA KB Custom Engine Agent — Deployment Complete${NC}"
echo -e "${GREEN}════════════════════════════════════════════════════════════════════${NC}"
echo ""
printf "  %-18s %s\n" "Entra App ID:"     "$APP_ID"
printf "  %-18s %s\n" "Tenant:"           "$TENANT_ID"
printf "  %-18s %s\n" "Bot Service:"      "$BOT_NAME"
printf "  %-18s %s\n" "Container App:"    "$AGENT_APP"
printf "  %-18s %s\n" "FQDN:"             "$FQDN"
printf "  %-18s %s\n" "Bot Endpoint:"     "$BOT_ENDPOINT"
printf "  %-18s %s\n" "Teams App ID:"     "$TEAMS_APP_ID"
printf "  %-18s %s\n" "Teams Package:"    "$ZIP_PATH"
printf "  %-18s %s\n" "Web Chat (test):"  "$WEB_CHAT_URL"
echo ""
echo -e "${BLUE}Next steps:${NC}"
echo "  1. Test the bot with Bot Framework Web Chat:"
echo "       ${WEB_CHAT_URL}"
echo ""
echo "  2. Sideload the agent into Microsoft Teams. Either:"
echo "     a) Teams Admin Center -> Manage apps -> Upload custom app:"
echo "          ${ZIP_PATH}"
echo "     b) Teams Developer Portal (https://dev.teams.microsoft.com/) ->"
echo "        Apps -> Import an existing app -> select the .zip above"
echo ""
echo "  3. (Optional) Publish to Microsoft 365 Copilot via the same package -"
echo "     the manifest already declares it as a custom engine agent."
echo ""
echo -e "${GREEN}════════════════════════════════════════════════════════════════════${NC}"
