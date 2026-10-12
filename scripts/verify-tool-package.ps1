param(
    [string]$PackageDirectory = (Join-Path $PSScriptRoot '..\nupkg'),
    [string]$ExpectedPackageVersion,
    [string]$ExpectedCommit
)

$ErrorActionPreference = 'Stop'
$sourceVersion = (Get-Content (Join-Path $PSScriptRoot '..\version.json') -Raw | ConvertFrom-Json).version
$repository = 'https://github.com/marcschier/opcuakb-mcp'
$packages = @(Get-ChildItem $PackageDirectory -Filter '*.nupkg')
if ($packages.Count -ne 1) {
    throw "Expected exactly one tool package in '$PackageDirectory', found $($packages.Count)."
}
if (-not $ExpectedCommit) {
    $ExpectedCommit = git -C (Join-Path $PSScriptRoot '..') rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Cannot determine the package source commit.' }
}

$archive = [System.IO.Compression.ZipFile]::OpenRead($packages[0].FullName)
try {
    $nuspec = @($archive.Entries | Where-Object FullName -like '*.nuspec')
    if ($nuspec.Count -ne 1) { throw 'Expected exactly one package nuspec.' }
    $reader = [System.IO.StreamReader]::new($nuspec[0].Open())
    try { [xml]$metadata = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $manifest = $archive.GetEntry('.mcp/server.json')
    if (-not $manifest) { throw 'The package is missing its MCP server manifest.' }
    $reader = [System.IO.StreamReader]::new($manifest.Open())
    try { $server = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }

    $package = $metadata.package.metadata
    if ($package.id -ne 'OpcUaKb.McpServer' -or
        $package.packageTypes.packageType.name -ne 'DotnetTool') {
        throw 'Package identity or dotnet tool type is incorrect.'
    }
    if ($package.repository.url -ne $repository -or
        $package.repository.commit -ne $ExpectedCommit) {
        throw "Package repository provenance does not match '$repository' at '$ExpectedCommit'."
    }
    if ($server.version -ne $sourceVersion -or $server.repository -ne $repository) {
        throw 'Packaged MCP manifest does not match the release source version and repository.'
    }
    if ($server.name -ne 'opcua-kb-mcp' -or $server.publisher -ne 'marcschier' -or
        $server.transport.stdio.command -ne 'opcua-kb-mcp') {
        throw 'Packaged MCP manifest tool identity is incorrect.'
    }
    $settings = @($archive.Entries | Where-Object FullName -like 'tools/*/DotnetToolSettings.xml')
    if ($settings.Count -ne 1) { throw 'Expected exactly one dotnet tool settings file.' }
    $reader = [System.IO.StreamReader]::new($settings[0].Open())
    try { [xml]$toolSettings = $reader.ReadToEnd() } finally { $reader.Dispose() }
    if ($toolSettings.DotNetCliTool.Commands.Command.Name -ne $server.transport.stdio.command) {
        throw 'The dotnet tool command and packaged MCP stdio command do not match.'
    }
    if ($ExpectedPackageVersion) {
        if ($package.version -ne $ExpectedPackageVersion) {
            throw "Expected package version '$ExpectedPackageVersion', received '$($package.version)'."
        }
    } elseif ($package.version -ne $sourceVersion -and $package.version -notlike "$sourceVersion-g*") {
        throw "Package version '$($package.version)' does not match source version '$sourceVersion'."
    }
    Write-Host "Verified tool $($package.version), MCP manifest $($server.version), repository $repository, source $ExpectedCommit."
} finally {
    $archive.Dispose()
}
