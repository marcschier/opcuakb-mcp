# ═══════════════════════════════════════════════════════════════════════
# OPC UA KB Foundry Hosted Agent — End-to-End Install (PowerShell)
#
# Idempotent, end-to-end deployment of the OPC UA KB Foundry Hosted Agent
# (Azure AI Foundry / Responses protocol) backed by the existing
# OpcUaKb.McpServer wrapped as a Foundry Toolbox.
#
# What it does:
#   1. Pre-flight  : validate az / azd CLIs, azd ai agents extension,
#                    AgentDir layout. Resolve McpServerUrl if omitted.
#   2. Auth        : az account set + azd auth login. Soft-check RBAC
#                    on the Foundry project.
#   3. azd init    : run `azd ai agent init` (skip if files pre-created).
#                    Falls back to a minimal azure.yaml if init fails.
#   4. Provision   : `azd provision` — creates Toolbox + agent identity.
#   5. Deploy      : `azd deploy` — builds image remotely in ACR,
#                    publishes a new agent version.
#   6. Publish     : optional `-PublishAsApp` — wrap the version in a
#                    Foundry Agent Application with a stable endpoint.
#   7. Teams bind  : optional `-BindToTeams` — bind Agent Application to
#                    a Teams channel via the Activity bridge.
#   8. Smoke test  : `azd ai agent invoke` with a representative prompt;
#                    asserts at least one tool call was made.
#
# This script uses az CLI's Python directly (via -IBm azure.cli) to avoid
# PowerShell munging special characters in secrets / connection strings,
# matching the pattern from the original install-agent.ps1.
#
# Usage:
#   .\scripts\install-toolbox-and-agent.ps1
#   .\scripts\install-toolbox-and-agent.ps1 -SkipAzdInit
#   .\scripts\install-toolbox-and-agent.ps1 -PublishAsApp -BindToTeams
# ═══════════════════════════════════════════════════════════════════════

[CmdletBinding()]
param(
    [string]$SubscriptionId = "",
    [string]$TenantId = "72f988bf-86f1-41af-91ab-2d7cd011db47",
    [string]$ResourceGroup = "rg-opcua-kb",
    [string]$FoundryAccountName = "opcua-kb-foundry",
    [string]$FoundryProjectName = "default",
    [string]$McpServerUrl = "",
    [string]$Location = "eastus",
    [string]$AgentDir = "",
    [switch]$SkipAzdInit,
    [switch]$SkipProvision,
    [switch]$PublishAsApp,
    [switch]$BindToTeams,
    [switch]$CleanupLegacyBot
)

$ErrorActionPreference = "Stop"

# ── Phase / log helpers ─────────────────────────────────────────────────
function Write-Phase {
    param([string]$Name, [string]$Status = "start")
    Write-Host ""
    Write-Host "[PHASE $Name] $Status" -ForegroundColor Cyan
}
function Write-Sub  { param([string]$Msg) Write-Host "  $Msg" }
function Write-Kv   { param([string]$Key, [string]$Value) Write-Host "  $Key=$Value" }
function Write-Warn { param([string]$Msg) Write-Host "  WARN: $Msg" -ForegroundColor Yellow }
function Write-Err  { param([string]$Msg) Write-Host "  ERROR: $Msg" -ForegroundColor Red }
function Write-Fail { param([string]$Msg) Write-Err $Msg; throw $Msg }
function Write-Ok   { param([string]$Msg) Write-Host "  OK: $Msg" -ForegroundColor Green }

# ── Resolve repo root + default AgentDir ────────────────────────────────
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot  = (Resolve-Path (Join-Path $ScriptDir "..")).Path
if (-not $AgentDir) {
    $AgentDir = Join-Path $RepoRoot "src\OpcUaKb.HostedAgent"
}
# Resolve to absolute path if it exists; otherwise normalise the string
if (Test-Path $AgentDir) {
    $AgentDir = (Resolve-Path $AgentDir).Path
} else {
    $AgentDir = [System.IO.Path]::GetFullPath($AgentDir)
}

# ── Resolve az CLI Python (avoid PowerShell quoting issues) ────────────
# Same pattern as install-agent.ps1: invoking azure.cli through its embedded
# python bypasses cmd.exe's quote mangling for args containing $, &, !, etc.
$PyPathCandidates = @(
    "C:\Program Files\Microsoft SDKs\Azure\CLI2\python.exe",
    "C:\Program Files (x86)\Microsoft SDKs\Azure\CLI2\python.exe",
    "$env:LOCALAPPDATA\Programs\Microsoft SDKs\Azure\CLI2\python.exe"
)
$script:PyPath = $PyPathCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1

# Invoke-Az: call az via its embedded python directly when possible.
# NOTE: must not be an advanced function (no [Parameter()] attribute) to
# avoid PowerShell's parameter binding consuming '-o tsv' style args.
function Invoke-Az {
    if ($script:PyPath) {
        & $script:PyPath -IBm azure.cli @args
    } else {
        & az @args
    }
    if ($LASTEXITCODE -ne 0) {
        throw "az CLI failed (exit $LASTEXITCODE): az $($args -join ' ')"
    }
}

# Invoke-AzText: like Invoke-Az but captures stdout as a single string,
# stripped of trailing whitespace. Returns $null on empty or error.
function Invoke-AzText {
    $out = if ($script:PyPath) {
        & $script:PyPath -IBm azure.cli @args 2>$null
    } else {
        & az @args 2>$null
    }
    if ($LASTEXITCODE -ne 0) { return $null }
    if ($null -eq $out) { return $null }
    $joined = ($out -join "`n").Trim()
    if ([string]::IsNullOrWhiteSpace($joined)) { return $null }
    return $joined
}

# Invoke-Azd: wrap azd calls so failures are surfaced as PowerShell errors
# with the exact command for forensic logs.
function Invoke-Azd {
    & azd @args
    if ($LASTEXITCODE -ne 0) {
        throw "azd failed (exit $LASTEXITCODE): azd $($args -join ' ')"
    }
}

# Invoke-AzdText: capture azd stdout (e.g., for parsing endpoint URLs).
# Note: many azd commands emit ANSI to stderr; we keep stderr visible.
function Invoke-AzdText {
    $out = & azd @args
    if ($LASTEXITCODE -ne 0) { return $null }
    if ($null -eq $out) { return $null }
    $joined = ($out -join "`n")
    if ([string]::IsNullOrWhiteSpace($joined)) { return $null }
    return $joined
}

# ════════════════════════════════════════════════════════════════════════
# PHASE 1: Pre-flight
# ════════════════════════════════════════════════════════════════════════
Write-Phase "preflight" "start"

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    Write-Fail "Azure CLI ('az') not found. Install from https://aka.ms/install-azure-cli"
}
$azVer = Invoke-AzText version --query '"azure-cli"' -o tsv
Write-Kv "az_cli_version" $azVer

if (-not (Get-Command azd -ErrorAction SilentlyContinue)) {
    Write-Fail "Azure Developer CLI ('azd') not found. Install from https://aka.ms/azd-install"
}
& azd version 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Fail "azd version check failed." }
$azdVerRaw = & azd version 2>$null
$azdVerLine = ($azdVerRaw | Select-Object -First 1).ToString().Trim()
Write-Kv "azd_version" $azdVerLine

# Ensure the Azure AI Agents extension is installed.
# `azd ext list` succeeds whether or not the extension is present; we parse
# its output. If absent, `azd ext install azure.ai.agents` adds it.
$extOut = & azd ext list 2>$null
$hasAgentsExt = $false
if ($extOut) {
    $hasAgentsExt = ($extOut -join "`n") -match "azure\.ai\.agents"
}
if (-not $hasAgentsExt) {
    Write-Sub "Installing azd extension: azure.ai.agents"
    Invoke-Azd ext install azure.ai.agents
    Write-Ok "azd ai agents extension installed."
} else {
    Write-Ok "azd ai agents extension already installed."
}

if ($script:PyPath) {
    Write-Kv "az_python" $script:PyPath
} else {
    Write-Warn "Azure CLI Python not found at standard paths — falling back to 'az' shim."
}

if (-not (Test-Path $AgentDir)) {
    Write-Fail "AgentDir not found: $AgentDir"
}
$manifestPath = Join-Path $AgentDir "agent.manifest.yaml"
if (-not (Test-Path $manifestPath)) {
    Write-Fail "agent.manifest.yaml not found in AgentDir: $manifestPath"
}
Write-Kv "agent_dir" $AgentDir

# Resolve SubscriptionId from current az context if not provided.
if (-not $SubscriptionId) {
    $SubscriptionId = Invoke-AzText account show --query id -o tsv
    if (-not $SubscriptionId) {
        Write-Fail "Could not resolve SubscriptionId. Run 'az login' first or pass -SubscriptionId."
    }
}
Write-Kv "subscription_id" $SubscriptionId
Write-Kv "tenant_id"       $TenantId
Write-Kv "resource_group"  $ResourceGroup
Write-Kv "location"        $Location
Write-Kv "foundry_account" $FoundryAccountName
Write-Kv "foundry_project" $FoundryProjectName

# Resolve McpServerUrl by querying the existing Container App if not supplied.
if (-not $McpServerUrl) {
    Write-Sub "Resolving McpServerUrl from Container App 'opcua-kb-mcp-server'..."
    $fqdn = Invoke-AzText containerapp show `
        -n "opcua-kb-mcp-server" `
        -g $ResourceGroup `
        --query "properties.configuration.ingress.fqdn" `
        -o tsv
    if (-not $fqdn) {
        Write-Warn "Could not resolve MCP server FQDN. Pass -McpServerUrl explicitly if Toolbox creation requires it."
        $McpServerUrl = ""
    } else {
        $McpServerUrl = "https://$fqdn"
    }
}
Write-Kv "mcp_server_url" ($(if ($McpServerUrl) { $McpServerUrl } else { "<unresolved>" }))

Write-Phase "preflight" "ok"

# ════════════════════════════════════════════════════════════════════════
# PHASE 2: Authentication
# ════════════════════════════════════════════════════════════════════════
Write-Phase "auth" "start"

Write-Sub "Setting active subscription..."
Invoke-Az account set --subscription $SubscriptionId
Write-Ok "Subscription set: $SubscriptionId"

# Soft-check RBAC on the Foundry project. We don't fail because the role
# could be inherited via a group; just warn so the operator can verify
# if a downstream step trips an authorization error.
$signedInUpn = Invoke-AzText account show --query "user.name" -o tsv
$signedInOid = Invoke-AzText ad signed-in-user show --query "id" -o tsv
$projScope   = "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroup/providers/Microsoft.CognitiveServices/accounts/$FoundryAccountName/projects/$FoundryProjectName"
Write-Kv "signed_in_user" $signedInUpn
$hasRequiredRole = $false
if ($signedInOid) {
    $roles = Invoke-AzText role assignment list --assignee $signedInOid --scope $projScope --query "[].roleDefinitionName" -o tsv
    if ($roles) {
        $rolesList = $roles -split "`r?`n"
        $required  = @("Owner", "Azure AI Project Manager", "Contributor")
        foreach ($r in $rolesList) {
            if ($required -contains $r.Trim()) { $hasRequiredRole = $true; break }
        }
        Write-Kv "project_roles" ($rolesList -join ",")
    }
}
if (-not $hasRequiredRole) {
    Write-Warn "Could not confirm Owner / Contributor / Azure AI Project Manager role on the Foundry project."
    Write-Warn "Project scope: $projScope"
    Write-Warn "If subsequent azd steps fail with 403, grant the role and retry."
} else {
    Write-Ok "Caller has a sufficient role on the Foundry project."
}

# azd auth login: skip if a valid token is already present.
# `azd auth login --check-status` exits 0 when authenticated, non-zero otherwise.
& azd auth login --check-status 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Sub "azd not authenticated — running 'azd auth login --tenant-id $TenantId'..."
    Invoke-Azd auth login --tenant-id $TenantId
    Write-Ok "azd authenticated."
} else {
    Write-Ok "azd already authenticated."
}

Write-Phase "auth" "ok"

# ════════════════════════════════════════════════════════════════════════
# PHASE 3: azd init (skippable)
# ════════════════════════════════════════════════════════════════════════
Write-Phase "init" "start"

Push-Location $AgentDir
try {
    $azureYamlPath = Join-Path $AgentDir "azure.yaml"
    if ($SkipAzdInit) {
        Write-Sub "-SkipAzdInit specified — skipping 'azd ai agent init'."
    } elseif (Test-Path $azureYamlPath) {
        Write-Sub "azure.yaml already exists — skipping 'azd ai agent init' (idempotent)."
    } else {
        Write-Sub "Running 'azd ai agent init' (non-interactive)..."
        # The exact non-interactive form of `azd ai agent init` is evolving;
        # try the documented form first and fall back to a manual scaffold
        # if it errors out (e.g. on older extension versions).
        $initOk = $false
        try {
            & azd ai agent init --no-prompt --language csharp --template existing 2>&1 | ForEach-Object { Write-Sub $_ }
            if ($LASTEXITCODE -eq 0) { $initOk = $true }
        } catch {
            Write-Warn "azd ai agent init (no-prompt form) failed: $($_.Exception.Message)"
        }
        if (-not $initOk) {
            # Fallback: synthesise a minimal azure.yaml pointing at the
            # existing agent.yaml / agent.manifest.yaml so that
            # `azd provision` and `azd deploy` can still discover the
            # service. This mirrors what the init command produces and is
            # safe to commit alongside the agent project.
            Write-Warn "Falling back to manual azure.yaml scaffold."
            $envName = "opcua-kb-hostedagent"
            $azureYaml = @"
# yaml-language-server: `$schema=https://raw.githubusercontent.com/Azure/azure-dev/main/schemas/v1.0/azure.yaml.json
name: $envName
metadata:
  template: opcua-kb-hosted-agent@0.0.1
services:
  agent:
    project: .
    language: csharp
    host: aiagent
"@
            Set-Content -Path $azureYamlPath -Value $azureYaml -Encoding UTF8
            Write-Ok "Wrote fallback azure.yaml: $azureYamlPath"
        } else {
            Write-Ok "azd ai agent init completed."
        }
    }

    # Ensure an azd environment is selected so provision/deploy don't prompt.
    # `azd env list` returns a table; first column has the env name and an
    # asterisk on the active one.
    $envList = & azd env list 2>$null
    $haveActiveEnv = $false
    if ($envList) {
        $haveActiveEnv = ($envList -join "`n") -match "\*\s+\S+"
    }
    if (-not $haveActiveEnv) {
        $defaultEnv = "opcua-kb-hostedagent"
        Write-Sub "No active azd env — creating '$defaultEnv'..."
        Invoke-Azd env new $defaultEnv --location $Location --subscription $SubscriptionId
        Write-Ok "azd env '$defaultEnv' created."
    } else {
        Write-Ok "azd environment already selected."
    }

    # Propagate parameters needed by Bicep / agent.manifest.yaml into azd env.
    # These are no-ops if already set to the same value.
    Invoke-Azd env set AZURE_LOCATION              $Location
    Invoke-Azd env set AZURE_SUBSCRIPTION_ID       $SubscriptionId
    Invoke-Azd env set AZURE_RESOURCE_GROUP        $ResourceGroup
    Invoke-Azd env set AZURE_TENANT_ID             $TenantId
    Invoke-Azd env set FOUNDRY_ACCOUNT_NAME        $FoundryAccountName
    Invoke-Azd env set FOUNDRY_PROJECT_NAME        $FoundryProjectName
    # Required by agent.manifest.yaml + agent.yaml substitutions and by the
    # container at runtime. Hard-fail in Program.cs if missing.
    Invoke-Azd env set AZURE_AI_MODEL_DEPLOYMENT_NAME "gpt-4o"
    Invoke-Azd env set TOOLBOX_NAME                "opcua-kb-tools"
    if ($McpServerUrl) {
        Invoke-Azd env set MCP_SERVER_URL $McpServerUrl
    }
    Write-Ok "azd env variables propagated."
}
finally {
    Pop-Location
}

Write-Phase "init" "ok"

# ════════════════════════════════════════════════════════════════════════
# PHASE 4: Provision (skippable)
# ════════════════════════════════════════════════════════════════════════
Write-Phase "provision" "start"

if ($SkipProvision) {
    Write-Sub "-SkipProvision specified — skipping 'azd provision'."
} else {
    Push-Location $AgentDir
    try {
        Write-Sub "Running 'azd provision' (creates Toolbox + agent identity + RBAC)..."
        Invoke-Azd provision --no-prompt
        Write-Ok "azd provision complete."
    }
    finally {
        Pop-Location
    }
}

Write-Phase "provision" "ok"

# ════════════════════════════════════════════════════════════════════════
# PHASE 5: Deploy
# ════════════════════════════════════════════════════════════════════════
Write-Phase "deploy" "start"

$agentEndpoint = ""
Push-Location $AgentDir
try {
    Write-Sub "Running 'azd deploy' (remote ACR build + agent version publish)..."
    $deployOut = & azd deploy --no-prompt 2>&1
    $deployExit = $LASTEXITCODE
    $deployOut | ForEach-Object { Write-Sub $_ }
    if ($deployExit -ne 0) {
        throw "azd deploy failed (exit $deployExit). Inspect output above for the failing step."
    }

    # Surface the agent endpoint. azd typically prints lines like:
    #   - Endpoint: https://...
    # Match conservatively and fall back to azd env values.
    foreach ($line in $deployOut) {
        if ($line -match "https?://[^\s'""]+") {
            $candidate = $Matches[0]
            if ($candidate -match "(foundry|cognitiveservices|inference|agents|azure\.com)") {
                $agentEndpoint = $candidate
                break
            }
        }
    }
    if (-not $agentEndpoint) {
        $envAgentEndpoint = & azd env get-value AGENT_ENDPOINT 2>$null
        if ($envAgentEndpoint -and $LASTEXITCODE -eq 0) {
            $agentEndpoint = $envAgentEndpoint.Trim()
        }
    }
    if ($agentEndpoint) {
        Write-Kv "agent_endpoint" $agentEndpoint
    } else {
        Write-Warn "Could not parse agent endpoint from azd deploy output — check 'azd env get-values'."
    }
    Write-Ok "azd deploy complete."
}
finally {
    Pop-Location
}

Write-Phase "deploy" "ok"

# ════════════════════════════════════════════════════════════════════════
# PHASE 6: Publish as Agent Application (optional)
# ════════════════════════════════════════════════════════════════════════
if ($PublishAsApp) {
    Write-Phase "publish" "start"

    Push-Location $AgentDir
    try {
        # `azd ai agent publish` is the documented future command but is not
        # yet GA across all extension builds. Try it first and fall back to
        # printing a manual portal step if it isn't available.
        $publishCmdOk = $false
        try {
            & azd ai agent publish --no-prompt 2>&1 | ForEach-Object { Write-Sub $_ }
            if ($LASTEXITCODE -eq 0) { $publishCmdOk = $true }
        } catch {
            Write-Warn "azd ai agent publish errored: $($_.Exception.Message)"
        }

        if ($publishCmdOk) {
            Write-Ok "Foundry Agent Application published."
            $appEndpoint = & azd env get-value AGENT_APP_ENDPOINT 2>$null
            if ($appEndpoint -and $LASTEXITCODE -eq 0) {
                Write-Kv "agent_app_endpoint" $appEndpoint.Trim()
            }
        } else {
            Write-Warn "azd ai agent publish not available in this extension build."
            Write-Warn "Manual step:"
            Write-Warn "  1. Open https://ai.azure.com → Project '$FoundryProjectName' → Agents → <your agent>"
            Write-Warn "  2. Click 'Publish as application' → choose 'Hosted Agent Application'"
            Write-Warn "  3. Note the stable endpoint URL and re-run with -PublishAsApp once the CLI catches up."
            Write-Warn "  See: samples/csharp/hosted-agents/agent-framework/foundry-toolbox-server-side/README.md"
        }
    }
    finally {
        Pop-Location
    }

    Write-Phase "publish" "ok"
} else {
    Write-Sub "Skipping publish (pass -PublishAsApp to enable)."
}

# ════════════════════════════════════════════════════════════════════════
# PHASE 7: Bind to Teams (optional)
# ════════════════════════════════════════════════════════════════════════
if ($BindToTeams) {
    Write-Phase "teams" "start"

    Push-Location $AgentDir
    try {
        # v1: the Foundry portal is the supported surface for binding an
        # Agent Application to a Teams channel via the Activity bridge.
        # If/when `azd ai agent channel add teams` lands we can replace
        # this with an actual command. For now print the manual procedure
        # with the exact resource identifiers so the operator can complete
        # it in a couple of clicks.
        $manualOk = $false
        try {
            & azd ai agent channel add teams --no-prompt 2>&1 | ForEach-Object { Write-Sub $_ }
            if ($LASTEXITCODE -eq 0) { $manualOk = $true }
        } catch {
            # Expected if the subcommand isn't available yet.
            Write-Warn "azd ai agent channel command not available: $($_.Exception.Message)"
        }

        if ($manualOk) {
            Write-Ok "Agent Application bound to Teams channel."
        } else {
            Write-Warn "Teams channel binding requires a manual portal step in v1:"
            Write-Warn "  1. Open https://ai.azure.com -> Project '$FoundryProjectName' -> Agents -> Applications"
            Write-Warn "  2. Select your Agent Application -> 'Channels' -> 'Add channel' -> 'Microsoft Teams'"
            Write-Warn "  3. Follow the Activity bridge wizard. Foundry manages the bridge end-to-end; no Bot Service"
            Write-Warn "     registration in this repo is required (the legacy 'opcua-kb-agent' bot was removed)."
            Write-Warn "  4. Sideload the generated Teams app package via Teams Admin Center."
        }
    }
    finally {
        Pop-Location
    }

    Write-Phase "teams" "ok"
} else {
    Write-Sub "Skipping Teams binding (pass -BindToTeams to enable)."
}

# ════════════════════════════════════════════════════════════════════════
# PHASE 8: Smoke test
# ════════════════════════════════════════════════════════════════════════
Write-Phase "smoke" "start"

$smokePrompt = "Can a server expose a DataType attribute with i=0 node id? Is that allowed?"
$toolCallObserved = $false
$smokeResponse = ""

Push-Location $AgentDir
try {
    Write-Sub "Invoking agent (cloud) with diagnostic prompt..."
    # Use --verbose so we can see tool invocations in the trace output; some
    # builds expose this as --debug. The canonical sample uses positional prompt
    # syntax (`azd ai agent invoke "<prompt>"`) rather than `--prompt`; that
    # form is more portable across azd ai agent extension versions.
    $invokeArgs = @("ai", "agent", "invoke", $smokePrompt, "--verbose")
    $invokeOut  = & azd @invokeArgs 2>&1
    $invokeExit = $LASTEXITCODE

    if ($invokeExit -ne 0) {
        Write-Warn "azd ai agent invoke exited with code $invokeExit. Output:"
        $invokeOut | ForEach-Object { Write-Sub $_ }
    } else {
        $invokeOut | ForEach-Object { Write-Sub $_ }
        $smokeResponse = ($invokeOut -join "`n")

        # Heuristic tool-call detection: the verbose trace prints lines
        # like "tool_call:" / "ToolCall(" / "invoking tool". Any one match
        # is enough to assert the agent did exercise the Toolbox.
        $toolPatterns = @("tool_call", "ToolCall", "invoking tool", "tool=", "function_call")
        foreach ($pat in $toolPatterns) {
            if ($smokeResponse -match [regex]::Escape($pat)) {
                $toolCallObserved = $true
                break
            }
        }
    }
}
finally {
    Pop-Location
}

if ($smokeResponse) {
    # Print first ~800 chars as a response excerpt for the operator.
    $excerpt = if ($smokeResponse.Length -gt 800) { $smokeResponse.Substring(0, 800) + " ..." } else { $smokeResponse }
    Write-Host ""
    Write-Sub "Response excerpt:"
    $excerpt -split "`r?`n" | ForEach-Object { Write-Sub "  $_" }
}

if ($toolCallObserved) {
    Write-Ok "Tool call observed in trace — Toolbox is wired correctly."
    Write-Phase "smoke" "ok"
} else {
    Write-Warn "No tool call detected in invoke trace. The agent may have answered from the model alone."
    Write-Warn "Re-run with: azd ai agent invoke '$smokePrompt' --verbose"
    Write-Phase "smoke" "warn"
}

# ════════════════════════════════════════════════════════════════════════
# 9. Cleanup legacy Bot Framework agent Container App (optional)
# ════════════════════════════════════════════════════════════════════════
# Bicep no longer declares the old `opcua-kb-agent` Container App, but
# `az deployment` uses incremental mode by default — the live resource
# stays unless explicitly deleted. Run only after the hosted agent smoke
# test succeeds and the user has validated the Foundry agent end-to-end.
if ($CleanupLegacyBot) {
    Write-Phase "cleanup_legacy" "start"
    if ($toolCallObserved) {
        $legacyExists = Invoke-AzText containerapp show -n opcua-kb-agent -g $ResourceGroup --query "name" -o tsv 2>$null
        if ($legacyExists -eq "opcua-kb-agent") {
            Write-Warn "Deleting legacy Container App 'opcua-kb-agent'..."
            Invoke-Az containerapp delete -n opcua-kb-agent -g $ResourceGroup --yes | Out-Null
            Write-Ok "Legacy Container App deleted."
        } else {
            Write-Ok "No legacy 'opcua-kb-agent' Container App found — nothing to clean up."
        }
        $legacyBot = Invoke-AzText resource show --name opcua-kb-agent --resource-group $ResourceGroup --resource-type "Microsoft.BotService/botServices" --query "name" -o tsv 2>$null
        if ($legacyBot -eq "opcua-kb-agent") {
            Write-Warn "Deleting legacy Bot Service 'opcua-kb-agent'..."
            Invoke-Az resource delete --name opcua-kb-agent --resource-group $ResourceGroup --resource-type "Microsoft.BotService/botServices" | Out-Null
            Write-Ok "Legacy Bot Service deleted."
        }
        Write-Phase "cleanup_legacy" "done"
    } else {
        Write-Warn "Skipping legacy cleanup — smoke test did not observe a tool call."
        Write-Warn "Re-run install with -CleanupLegacyBot only after confirming the new agent works end-to-end."
        Write-Phase "cleanup_legacy" "skipped"
    }
}

# ════════════════════════════════════════════════════════════════════════
# Summary
# ════════════════════════════════════════════════════════════════════════
Write-Host ""
Write-Host "════════════════════════════════════════════════════════════════════" -ForegroundColor Green
Write-Host " OPC UA KB Foundry Hosted Agent — Install Complete" -ForegroundColor Green
Write-Host "════════════════════════════════════════════════════════════════════" -ForegroundColor Green
Write-Host ""

$summary = [pscustomobject]@{
    "Subscription"     = $SubscriptionId
    "Tenant"           = $TenantId
    "Resource Group"   = $ResourceGroup
    "Foundry Account"  = $FoundryAccountName
    "Foundry Project"  = $FoundryProjectName
    "MCP Server URL"   = $McpServerUrl
    "Agent Dir"        = $AgentDir
    "Agent Endpoint"   = ($(if ($agentEndpoint) { $agentEndpoint } else { "<see azd env get-values>" }))
    "Published As App" = ($(if ($PublishAsApp) { "yes" } else { "no" }))
    "Bound To Teams"   = ($(if ($BindToTeams) { "yes (or manual portal step)" } else { "no" }))
    "Tool Call OK"     = ($(if ($toolCallObserved) { "yes" } else { "not observed" }))
}
$summary | Format-List

Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host "  - Inspect azd env values:  azd env get-values"
Write-Host "  - Re-run smoke test:       azd ai agent invoke 'your question' --verbose"
Write-Host "  - Local container test:    azd ai agent invoke --local 'your question'"
if (-not $PublishAsApp) {
    Write-Host "  - To publish as Agent Application: re-run with -PublishAsApp"
}
if (-not $BindToTeams) {
    Write-Host "  - To bind to Teams:                re-run with -BindToTeams"
}
if (-not $CleanupLegacyBot) {
    Write-Host "  - After smoke test succeeds: re-run with -CleanupLegacyBot to delete the old"
    Write-Host "    'opcua-kb-agent' Container App + Bot Service registration left behind by"
    Write-Host "    incremental Bicep deployments."
}
Write-Host ""
