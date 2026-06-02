#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════════════
# OPC UA KB MCP Server — Install Script
# Installs the dotnet tool and configures MCP clients.
# ═══════════════════════════════════════════════════════════════════════
set -euo pipefail

RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; BLUE='\033[0;34m'; NC='\033[0m'
info()  { echo -e "${BLUE}[INFO]${NC}  $*"; }
ok()    { echo -e "${GREEN}[OK]${NC}    $*"; }
warn()  { echo -e "${YELLOW}[WARN]${NC}  $*"; }
fail()  { echo -e "${RED}[FAIL]${NC}  $*"; exit 1; }

# ── Configuration ─────────────────────────────────────────────────────
MCP_SERVER_URL="https://opcua-kb-mcp-server.lemoncliff-5369bab2.westus3.azurecontainerapps.io/"
KB_MCP_URL="https://opcua-kb-search.search.windows.net/knowledgebases/opcua-kb-kb/mcp?api-version=2025-11-01-preview"
SEARCH_ENDPOINT="https://opcua-kb-search.search.windows.net"
API_KEY="${SEARCH_API_KEY:-}"

MODE="${1:-hosted}"  # hosted or local

if [[ -z "$API_KEY" ]]; then
  echo -n "Enter Search API key: "
  read -r API_KEY
  [[ -z "$API_KEY" ]] && fail "API key is required."
fi

# ── Install dotnet tool (for local/stdio mode) ───────────────────────
if [[ "$MODE" == "local" ]]; then
  info "Installing opcua-kb-mcp dotnet tool..."
  if command -v dotnet &>/dev/null; then
    dotnet tool install -g OpcUaKb.McpServer 2>/dev/null || dotnet tool update -g OpcUaKb.McpServer
    ok "dotnet tool installed: opcua-kb-mcp"
  else
    warn "dotnet not found — skipping tool install. Use hosted mode instead."
    MODE="hosted"
  fi
fi

# ── Configure Copilot CLI ────────────────────────────────────────────
COPILOT_CONFIG="$HOME/.copilot/mcp.json"
if [[ -d "$HOME/.copilot" ]] || [[ "$MODE" == "hosted" ]]; then
  info "Configuring GitHub Copilot CLI..."
  mkdir -p "$HOME/.copilot"

  if [[ "$MODE" == "hosted" ]]; then
    # Hosted HTTP endpoint
    python3 -c "
import json, os
path = '$COPILOT_CONFIG'
cfg = json.load(open(path)) if os.path.exists(path) else {'mcpServers': {}}
cfg.setdefault('mcpServers', {})
cfg['mcpServers']['opcua-kb'] = {'type': 'http', 'url': '$KB_MCP_URL', 'headers': {'api-key': '$API_KEY'}}
cfg['mcpServers']['opcua-kb-tools'] = {'type': 'http', 'url': '$MCP_SERVER_URL', 'headers': {'api-key': '$API_KEY'}}
json.dump(cfg, open(path, 'w'), indent=2)
" 2>/dev/null && ok "Copilot CLI configured (hosted)" || warn "Could not update Copilot config (python3 needed)"
  else
    # Local stdio
    python3 -c "
import json, os
path = '$COPILOT_CONFIG'
cfg = json.load(open(path)) if os.path.exists(path) else {'mcpServers': {}}
cfg.setdefault('mcpServers', {})
cfg['mcpServers']['opcua-kb'] = {'type': 'http', 'url': '$KB_MCP_URL', 'headers': {'api-key': '$API_KEY'}}
cfg['mcpServers']['opcua-kb-tools'] = {'command': 'opcua-kb-mcp', 'args': ['--stdio'], 'env': {'SEARCH_ENDPOINT': '$SEARCH_ENDPOINT', 'SEARCH_API_KEY': '$API_KEY'}}
json.dump(cfg, open(path, 'w'), indent=2)
" 2>/dev/null && ok "Copilot CLI configured (local stdio)" || warn "Could not update Copilot config (python3 needed)"
  fi
fi

# ── Configure Claude Desktop ─────────────────────────────────────────
if [[ "$(uname)" == "Darwin" ]]; then
  CLAUDE_CONFIG="$HOME/Library/Application Support/Claude/claude_desktop_config.json"
elif [[ -n "${APPDATA:-}" ]]; then
  CLAUDE_CONFIG="$APPDATA/Claude/claude_desktop_config.json"
else
  CLAUDE_CONFIG="$HOME/.config/Claude/claude_desktop_config.json"
fi

if [[ -d "$(dirname "$CLAUDE_CONFIG")" ]]; then
  info "Configuring Claude Desktop..."
  python3 -c "
import json, os
path = '''$CLAUDE_CONFIG'''
cfg = json.load(open(path)) if os.path.exists(path) else {'mcpServers': {}}
cfg.setdefault('mcpServers', {})
cfg['mcpServers']['opcua-kb-tools'] = {'command': 'opcua-kb-mcp', 'args': ['--stdio'], 'env': {'SEARCH_ENDPOINT': '$SEARCH_ENDPOINT', 'SEARCH_API_KEY': '$API_KEY'}}
json.dump(cfg, open(path, 'w'), indent=2)
" 2>/dev/null && ok "Claude Desktop configured" || warn "Could not update Claude config (python3 needed)"
fi

# ── Summary ───────────────────────────────────────────────────────────
echo ""
echo -e "${GREEN}════════════════════════════════════════════════════════════${NC}"
echo -e "${GREEN} OPC UA KB MCP Server — Installation Complete${NC}"
echo -e "${GREEN}════════════════════════════════════════════════════════════${NC}"
echo ""
echo -e "  Mode:          ${BLUE}${MODE}${NC}"
echo -e "  MCP Server:    ${BLUE}${MCP_SERVER_URL}${NC}"
echo -e "  KB Endpoint:   ${BLUE}${KB_MCP_URL}${NC}"
echo ""
echo -e "  Available tools: search_nodes, get_type_hierarchy, get_spec_summary,"
echo -e "    search_docs, count_nodes, validate_nodeset, compare_versions,"
echo -e "    check_compliance, suggest_model"
echo ""
echo -e "${GREEN}════════════════════════════════════════════════════════════${NC}"
