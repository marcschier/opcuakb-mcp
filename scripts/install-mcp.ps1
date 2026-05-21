# ═══════════════════════════════════════════════════════════════════════
# OPC UA KB MCP Server — Install Script (PowerShell)
# Installs the dotnet tool and configures MCP clients.
# ═══════════════════════════════════════════════════════════════════════
param(
    [ValidateSet("hosted", "local")]
    [string]$Mode = "hosted",
    [string]$ApiKey = $env:SEARCH_API_KEY
)

$ErrorActionPreference = "Stop"

$McpServerUrl = "https://opcua-kb-sc-mcp-server-v2.ashyglacier-fe56ba7c.swedencentral.azurecontainerapps.io/"
$KbMcpUrl = "https://opcua-kb-search.search.windows.net/knowledgebases/opcua-kb/mcp?api-version=2025-11-01-preview"
$SearchEndpoint = "https://opcua-kb-search.search.windows.net"

if ([string]::IsNullOrEmpty($ApiKey)) {
    $ApiKey = Read-Host "Enter Search API key"
    if ([string]::IsNullOrEmpty($ApiKey)) { throw "API key is required." }
}

# ── Install dotnet tool (for local mode) ─────────────────────────────
if ($Mode -eq "local") {
    Write-Host "[INFO]  Installing opcua-kb-mcp dotnet tool..." -ForegroundColor Cyan
    try {
        dotnet tool install -g OpcUaKb.McpServer 2>$null
        Write-Host "[OK]    dotnet tool installed: opcua-kb-mcp" -ForegroundColor Green
    } catch {
        try { dotnet tool update -g OpcUaKb.McpServer } catch {}
        Write-Host "[OK]    dotnet tool updated: opcua-kb-mcp" -ForegroundColor Green
    }
}

# ── Helper: merge into JSON config ───────────────────────────────────
function Merge-McpConfig {
    param([string]$Path, [hashtable]$Servers)

    $dir = Split-Path $Path -Parent
    if (!(Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

    $cfg = if (Test-Path $Path) {
        Get-Content $Path -Raw | ConvertFrom-Json -AsHashtable
    } else {
        @{ mcpServers = @{} }
    }

    if (-not $cfg.ContainsKey("mcpServers")) { $cfg["mcpServers"] = @{} }
    foreach ($key in $Servers.Keys) {
        $cfg["mcpServers"][$key] = $Servers[$key]
    }

    $cfg | ConvertTo-Json -Depth 10 | Set-Content $Path -Encoding UTF8
}

# ── Configure Copilot CLI ────────────────────────────────────────────
$copilotPath = Join-Path $env:USERPROFILE ".copilot\mcp.json"
Write-Host "[INFO]  Configuring GitHub Copilot CLI..." -ForegroundColor Cyan

$servers = @{
    "opcua-kb" = @{
        type = "http"
        url = $KbMcpUrl
        headers = @{ "api-key" = $ApiKey }
    }
}

if ($Mode -eq "hosted") {
    $servers["opcua-kb-tools"] = @{
        type = "http"
        url = $McpServerUrl
        headers = @{ "api-key" = $ApiKey }
    }
} else {
    $servers["opcua-kb-tools"] = @{
        command = "opcua-kb-mcp"
        args = @("--stdio")
        env = @{
            SEARCH_ENDPOINT = $SearchEndpoint
            SEARCH_API_KEY = $ApiKey
        }
    }
}

Merge-McpConfig -Path $copilotPath -Servers $servers
Write-Host "[OK]    Copilot CLI configured ($Mode)" -ForegroundColor Green

# ── Configure Claude Desktop ─────────────────────────────────────────
$claudePath = Join-Path $env:APPDATA "Claude\claude_desktop_config.json"
if (Test-Path (Split-Path $claudePath -Parent)) {
    Write-Host "[INFO]  Configuring Claude Desktop..." -ForegroundColor Cyan
    $claudeServers = @{
        "opcua-kb-tools" = @{
            command = "opcua-kb-mcp"
            args = @("--stdio")
            env = @{
                SEARCH_ENDPOINT = $SearchEndpoint
                SEARCH_API_KEY = $ApiKey
            }
        }
    }
    Merge-McpConfig -Path $claudePath -Servers $claudeServers
    Write-Host "[OK]    Claude Desktop configured" -ForegroundColor Green
} else {
    Write-Host "[WARN]  Claude Desktop config dir not found — skipping" -ForegroundColor Yellow
}

# ── Summary ───────────────────────────────────────────────────────────
Write-Host ""
Write-Host "════════════════════════════════════════════════════════════" -ForegroundColor Green
Write-Host " OPC UA KB MCP Server — Installation Complete" -ForegroundColor Green
Write-Host "════════════════════════════════════════════════════════════" -ForegroundColor Green
Write-Host ""
Write-Host "  Mode:          $Mode" -ForegroundColor Cyan
Write-Host "  MCP Server:    $McpServerUrl" -ForegroundColor Cyan
Write-Host "  KB Endpoint:   $KbMcpUrl" -ForegroundColor Cyan
Write-Host ""
Write-Host "  Available tools: search_nodes, get_type_hierarchy, get_spec_summary,"
Write-Host "    search_docs, count_nodes, validate_nodeset, compare_versions,"
Write-Host "    check_compliance, suggest_model"
Write-Host ""
Write-Host "════════════════════════════════════════════════════════════" -ForegroundColor Green
