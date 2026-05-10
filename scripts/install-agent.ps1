# ═══════════════════════════════════════════════════════════════════════
# OPC UA KB Custom Engine Agent — End-to-End Install (PowerShell)
#
# Idempotent, end-to-end deployment of the OPC UA KB Custom Engine Agent
# (Microsoft 365 Agents SDK / Bot Framework) to Azure + Microsoft 365.
#
# What it does:
#   1. Validates prerequisites (az CLI, Azure login)
#   2. Ensures an Entra app registration exists for the bot
#   3. Rotates / creates a client secret for that app
#   4. Builds & pushes the agent container image to ACR
#   5. Runs the main Bicep deployment with botAppId / botAppPassword
#   6. Forces a Container App revision update to pick up the new image
#   7. Updates the Bot Service messaging endpoint with the agent FQDN
#   8. Generates a Teams app package (.zip) for sideloading
#
# This script uses az CLI's Python directly to avoid PowerShell munging
# special characters in client secrets (a recurring pain on Windows).
#
# Usage:
#   .\scripts\install-agent.ps1
#   .\scripts\install-agent.ps1 -ResourceGroup rg-opcua-kb -Prefix opcua-kb -Location eastus
#   .\scripts\install-agent.ps1 -SkipImageBuild
# ═══════════════════════════════════════════════════════════════════════

[CmdletBinding()]
param(
    [string]$ResourceGroup = "rg-opcua-kb",
    [string]$Prefix = "opcua-kb",
    [string]$Location = "eastus",
    [string]$AppDisplayName = "OPC UA KB Agent",
    [switch]$SkipImageBuild
)

$ErrorActionPreference = "Stop"

# ── Console helpers ─────────────────────────────────────────────────────
function Write-Info { param([string]$Msg) Write-Host "[INFO]  $Msg" -ForegroundColor Cyan }
function Write-Ok   { param([string]$Msg) Write-Host "[OK]    $Msg" -ForegroundColor Green }
function Write-Warn { param([string]$Msg) Write-Host "[WARN]  $Msg" -ForegroundColor Yellow }
function Write-Fail { param([string]$Msg) Write-Host "[FAIL]  $Msg" -ForegroundColor Red; throw $Msg }

# ── Resolve repo root ───────────────────────────────────────────────────
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot  = Resolve-Path (Join-Path $ScriptDir "..")
Set-Location $RepoRoot

# ── Derived names ───────────────────────────────────────────────────────
$AcrName     = ($Prefix -replace '-','') + 'registry'
$AgentApp    = "$Prefix-agent"
$BotName     = "$Prefix-agent"
$ImageRepo   = "opcua-kb-agent"
$ImageTag    = "latest"
$ImageRef    = "$AcrName.azurecr.io/${ImageRepo}:$ImageTag"
$AgentDir    = Join-Path $RepoRoot "agents\m365-agent"
$ManifestTpl = Join-Path $AgentDir "appPackage\manifest.template.json"
$ColorIcon   = Join-Path $AgentDir "appPackage\color.png"
$OutlineIcon = Join-Path $AgentDir "appPackage\outline.png"
$BuildDir    = Join-Path $AgentDir "appPackage\build"
$ZipPath     = Join-Path $BuildDir "opcua-kb-agent.zip"

# ── Resolve az CLI Python (avoid PowerShell quoting issues) ────────────
$PyPathCandidates = @(
    "C:\Program Files\Microsoft SDKs\Azure\CLI2\python.exe",
    "C:\Program Files (x86)\Microsoft SDKs\Azure\CLI2\python.exe",
    "$env:LOCALAPPDATA\Programs\Microsoft SDKs\Azure\CLI2\python.exe"
)
$PyPath = $PyPathCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1

# Invoke-Az: call az via its embedded python directly when possible.
# This bypasses the az.cmd shim which mangles quoted args containing $, !, etc.
function Invoke-Az {
    param([Parameter(ValueFromRemainingArguments = $true)] [string[]]$Args)
    if ($PyPath) {
        & $PyPath -IBm azure.cli @Args
    } else {
        & az @Args
    }
    if ($LASTEXITCODE -ne 0) {
        throw "az CLI failed (exit $LASTEXITCODE): az $($Args -join ' ')"
    }
}

# Invoke-AzText: same as Invoke-Az but captures stdout as a single string,
# stripped of trailing whitespace. Returns $null on empty.
function Invoke-AzText {
    param([Parameter(ValueFromRemainingArguments = $true)] [string[]]$Args)
    $out = if ($PyPath) {
        & $PyPath -IBm azure.cli @Args 2>$null
    } else {
        & az @Args 2>$null
    }
    if ($LASTEXITCODE -ne 0) { return $null }
    if ($null -eq $out) { return $null }
    $joined = ($out -join "`n").Trim()
    if ([string]::IsNullOrWhiteSpace($joined)) { return $null }
    return $joined
}

# ════════════════════════════════════════════════════════════════════════
# Step 1: Validate prerequisites
# ════════════════════════════════════════════════════════════════════════
Write-Info "Validating prerequisites..."

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    Write-Fail "Azure CLI ('az') not found. Install from https://aka.ms/install-azure-cli"
}

if ($PyPath) {
    Write-Ok "Using az python: $PyPath"
} else {
    Write-Warn "Azure CLI Python not found at standard paths — falling back to 'az'. Secrets with special characters may misbehave."
}

$account = Invoke-AzText account show -o json
if (-not $account) {
    Write-Fail "Not logged in to Azure. Run 'az login' first."
}
$acctObj = $account | ConvertFrom-Json
$Subscription = $acctObj.id
$TenantId     = $acctObj.tenantId
Write-Ok "Subscription : $($acctObj.name) ($Subscription)"
Write-Ok "Tenant       : $TenantId"

if (-not (Test-Path $ManifestTpl)) {
    Write-Fail "Manifest template not found: $ManifestTpl"
}

# ════════════════════════════════════════════════════════════════════════
# Step 2: Ensure resource group exists (idempotent)
# ════════════════════════════════════════════════════════════════════════
Write-Info "Ensuring resource group '$ResourceGroup' exists in $Location..."
Invoke-Az group create -n $ResourceGroup -l $Location -o none
Write-Ok "Resource group ready."

# ════════════════════════════════════════════════════════════════════════
# Step 3: Create or get Entra app registration
# ════════════════════════════════════════════════════════════════════════
Write-Info "Looking up Entra app registration: $AppDisplayName"

$existingApps = Invoke-AzText ad app list --display-name $AppDisplayName --query "[].{appId:appId,id:id}" -o json
$AppId = $null
$AppObjectId = $null

if ($existingApps) {
    $apps = $existingApps | ConvertFrom-Json
    if ($apps.Count -gt 0) {
        $AppId       = $apps[0].appId
        $AppObjectId = $apps[0].id
        Write-Ok "Found existing app registration: $AppId"
    }
}

if (-not $AppId) {
    Write-Info "Creating new app registration..."
    # Bot Framework requires:
    #   - sign-in audience: AzureADMultipleOrgs (multi-tenant) so the bot can
    #     receive messages from any Teams/Copilot tenant.
    #   - web reply URL https://token.botframework.com/.auth/web/redirect
    #     for OAuth handoff via Bot Framework Token Service.
    $createOut = Invoke-AzText ad app create `
        --display-name $AppDisplayName `
        --sign-in-audience AzureADMultipleOrgs `
        --web-redirect-uris "https://token.botframework.com/.auth/web/redirect" `
        -o json
    if (-not $createOut) { Write-Fail "Failed to create Entra app registration." }
    $appObj = $createOut | ConvertFrom-Json
    $AppId       = $appObj.appId
    $AppObjectId = $appObj.id
    Write-Ok "Created app registration: $AppId"

    # Eventual consistency — give Graph a moment before we touch the app again.
    Start-Sleep -Seconds 5
}

# Make sure the redirect URI is present even on existing apps (idempotent).
Write-Info "Ensuring Bot Framework redirect URI is registered..."
Invoke-Az ad app update --id $AppId --web-redirect-uris "https://token.botframework.com/.auth/web/redirect" -o none
Write-Ok "Redirect URI ensured."

# ════════════════════════════════════════════════════════════════════════
# Step 4: Set identifierUris (api://botid-{appId})
# ════════════════════════════════════════════════════════════════════════
$BotIdentifierUri = "api://botid-$AppId"
Write-Info "Setting identifierUris to $BotIdentifierUri ..."
Invoke-Az ad app update --id $AppId --identifier-uris $BotIdentifierUri -o none
Write-Ok "identifierUris set."

# ════════════════════════════════════════════════════════════════════════
# Step 5: Create / rotate client secret
# ════════════════════════════════════════════════════════════════════════
Write-Info "Rotating client secret 'agent-deploy-secret' (2 year lifetime)..."
$credOut = Invoke-AzText ad app credential reset `
    --id $AppId `
    --display-name "agent-deploy-secret" `
    --years 2 `
    --append `
    -o json
if (-not $credOut) { Write-Fail "Failed to create client secret." }
$cred = $credOut | ConvertFrom-Json
$AppPassword = $cred.password
if ([string]::IsNullOrWhiteSpace($AppPassword)) { Write-Fail "Empty client secret returned." }
Write-Ok "Client secret rotated (will be passed to Bicep as a secure parameter)."

# ════════════════════════════════════════════════════════════════════════
# Step 6: Ensure a service principal exists for the app (Bot Service requires it)
# ════════════════════════════════════════════════════════════════════════
Write-Info "Ensuring service principal exists for app $AppId..."
$spExists = Invoke-AzText ad sp show --id $AppId --query "id" -o tsv
if (-not $spExists) {
    Invoke-Az ad sp create --id $AppId -o none
    Write-Ok "Service principal created."
} else {
    Write-Ok "Service principal already exists."
}

# ════════════════════════════════════════════════════════════════════════
# Step 7: Resolve existing pipelineImage so we don't accidentally reset it
# ════════════════════════════════════════════════════════════════════════
Write-Info "Detecting existing pipeline image (to preserve across deploys)..."
$ExistingPipelineImage = Invoke-AzText containerapp job show `
    -n "$Prefix-pipeline-job" `
    -g $ResourceGroup `
    --query "properties.template.containers[0].image" `
    -o tsv
if ($ExistingPipelineImage -and $ExistingPipelineImage -notlike "mcr.microsoft.com/*") {
    Write-Ok "Reusing existing pipeline image: $ExistingPipelineImage"
} else {
    $ExistingPipelineImage = ""
    Write-Warn "No existing pipeline image found — Bicep will use the default placeholder."
}

# ════════════════════════════════════════════════════════════════════════
# Step 8: Build & push agent image to ACR
# ════════════════════════════════════════════════════════════════════════
if ($SkipImageBuild) {
    Write-Warn "Skipping agent image build (-SkipImageBuild)."
} else {
    # Verify ACR exists; if it doesn't, the Bicep deploy will create it but
    # we won't be able to push beforehand. In that case we let Bicep deploy
    # first with a placeholder, then come back to push.
    $acrExists = Invoke-AzText acr show -n $AcrName -g $ResourceGroup --query "name" -o tsv
    if (-not $acrExists) {
        Write-Warn "ACR '$AcrName' does not exist yet. Will run Bicep first, then build."
    } else {
        Write-Info "Building and pushing agent image to ACR ($AcrName)..."
        Invoke-Az acr build `
            --registry $AcrName `
            --resource-group $ResourceGroup `
            --image "${ImageRepo}:$ImageTag" `
            --file "Dockerfile.agent" `
            "."
        Write-Ok "Image pushed: $ImageRef"
    }
}

# ════════════════════════════════════════════════════════════════════════
# Step 9: Run the main Bicep deployment with botAppId / botAppPassword
# ════════════════════════════════════════════════════════════════════════
Write-Info "Running Bicep deployment (this may take several minutes)..."

$bicepArgs = @(
    "deployment", "group", "create",
    "-g", $ResourceGroup,
    "--template-file", "infra/main.bicep",
    "--parameters",
    "prefix=$Prefix",
    "location=$Location",
    "botAppId=$AppId",
    "botAppPassword=$AppPassword"
)
if ($ExistingPipelineImage) {
    $bicepArgs += "pipelineImage=$ExistingPipelineImage"
}
$bicepArgs += @("-o", "none")

Invoke-Az @bicepArgs
Write-Ok "Bicep deployment complete."

# If we couldn't push the image before Bicep (because ACR didn't exist),
# do it now and continue.
if (-not $SkipImageBuild) {
    $acrExists = Invoke-AzText acr show -n $AcrName -g $ResourceGroup --query "name" -o tsv
    if ($acrExists) {
        $imgPushed = Invoke-AzText acr repository show -n $AcrName --image "${ImageRepo}:$ImageTag" --query "name" -o tsv
        if (-not $imgPushed) {
            Write-Info "ACR exists now — building agent image post-deploy..."
            Invoke-Az acr build `
                --registry $AcrName `
                --resource-group $ResourceGroup `
                --image "${ImageRepo}:$ImageTag" `
                --file "Dockerfile.agent" `
                "."
            Write-Ok "Image pushed: $ImageRef"
        }
    }
}

# ════════════════════════════════════════════════════════════════════════
# Step 10: Force Container App revision update with new image
#
# Bicep won't trigger a new revision when the image tag is the same
# ("latest"), so we explicitly bump a revision-suffix.
# ════════════════════════════════════════════════════════════════════════
$RevisionSuffix = "deploy-$(Get-Date -Format 'yyyyMMddHHmmss')"
Write-Info "Updating Container App $AgentApp to revision $RevisionSuffix..."
Invoke-Az containerapp update `
    -n $AgentApp `
    -g $ResourceGroup `
    --image $ImageRef `
    --revision-suffix $RevisionSuffix `
    -o none
Write-Ok "Container App updated."

# ════════════════════════════════════════════════════════════════════════
# Step 11: Resolve agent FQDN and (re)point the Bot Service endpoint
# ════════════════════════════════════════════════════════════════════════
Write-Info "Resolving agent FQDN..."
$Fqdn = Invoke-AzText containerapp show `
    -n $AgentApp `
    -g $ResourceGroup `
    --query "properties.configuration.ingress.fqdn" `
    -o tsv
if (-not $Fqdn) { Write-Fail "Could not resolve agent FQDN." }
Write-Ok "Agent FQDN: $Fqdn"

$BotEndpoint = "https://$Fqdn/api/messages"
Write-Info "Updating Bot Service '$BotName' messaging endpoint -> $BotEndpoint"
Invoke-Az bot update `
    -n $BotName `
    -g $ResourceGroup `
    --endpoint $BotEndpoint `
    -o none
Write-Ok "Bot endpoint updated."

# ════════════════════════════════════════════════════════════════════════
# Step 12: Generate Teams app package (manifest + icons -> .zip)
# ════════════════════════════════════════════════════════════════════════
Write-Info "Generating Teams app package..."

if (-not (Test-Path $BuildDir)) {
    New-Item -ItemType Directory -Path $BuildDir -Force | Out-Null
}

# Reuse a stable Teams app id once one has been generated. Each Teams
# tenant treats this id as the unique identity for the side-loaded app,
# so regenerating it on every run would create duplicate app entries.
$TeamsAppIdFile = Join-Path $BuildDir ".teams-app-id"
if (Test-Path $TeamsAppIdFile) {
    $TeamsAppId = (Get-Content $TeamsAppIdFile -Raw).Trim()
    Write-Ok "Reusing Teams app id: $TeamsAppId"
} else {
    $TeamsAppId = [guid]::NewGuid().ToString()
    Set-Content -Path $TeamsAppIdFile -Value $TeamsAppId -Encoding ASCII
    Write-Ok "Generated new Teams app id: $TeamsAppId"
}

$ManifestRaw = Get-Content $ManifestTpl -Raw
$Manifest = $ManifestRaw `
    -replace '\$\{\{BOT_ID\}\}',       $AppId `
    -replace '\$\{\{TEAMS_APP_ID\}\}', $TeamsAppId

$BuiltManifest = Join-Path $BuildDir "manifest.json"
Set-Content -Path $BuiltManifest -Value $Manifest -Encoding UTF8

Copy-Item -Path $ColorIcon   -Destination (Join-Path $BuildDir "color.png")   -Force
Copy-Item -Path $OutlineIcon -Destination (Join-Path $BuildDir "outline.png") -Force

if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
# Zip just the three required files (NOT .teams-app-id) so Teams accepts it.
$ZipInputs = @(
    (Join-Path $BuildDir "manifest.json"),
    (Join-Path $BuildDir "color.png"),
    (Join-Path $BuildDir "outline.png")
)
Compress-Archive -Path $ZipInputs -DestinationPath $ZipPath -Force
Write-Ok "Teams app package: $ZipPath"

# ════════════════════════════════════════════════════════════════════════
# Step 13: Summary
# ════════════════════════════════════════════════════════════════════════
$WebChatUrl = "https://dev.botframework.com/bots/channels?id=$BotName"

Write-Host ""
Write-Host "════════════════════════════════════════════════════════════════════" -ForegroundColor Green
Write-Host " OPC UA KB Custom Engine Agent — Deployment Complete" -ForegroundColor Green
Write-Host "════════════════════════════════════════════════════════════════════" -ForegroundColor Green
Write-Host ""

$summary = [pscustomobject]@{
    "Entra App ID"        = $AppId
    "Tenant"              = $TenantId
    "Bot Service"         = $BotName
    "Container App"       = $AgentApp
    "FQDN"                = $Fqdn
    "Bot Endpoint"        = $BotEndpoint
    "Teams App ID"        = $TeamsAppId
    "Teams Package"       = $ZipPath
    "Web Chat (test)"     = $WebChatUrl
}
$summary | Format-List

Write-Host ""
Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host "  1. Test the bot with Bot Framework Web Chat:"
Write-Host "       $WebChatUrl"
Write-Host ""
Write-Host "  2. Sideload the agent into Microsoft Teams. Either:"
Write-Host "     a) Teams Admin Center -> Manage apps -> Upload custom app:"
Write-Host "          $ZipPath"
Write-Host "     b) Teams Developer Portal (https://dev.teams.microsoft.com/) ->"
Write-Host "        Apps -> Import an existing app -> select the .zip above"
Write-Host ""
Write-Host "  3. (Optional) Publish to Microsoft 365 Copilot via the same package -"
Write-Host "     the manifest already declares it as a custom engine agent."
Write-Host ""
Write-Host "════════════════════════════════════════════════════════════════════" -ForegroundColor Green
