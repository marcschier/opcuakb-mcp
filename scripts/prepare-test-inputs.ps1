param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\src\OpcUaKb.Test\bin\Release\net10.0\testdata')
)

$ErrorActionPreference = 'Stop'
$inputs = @(
    @{
        Name = 'opc-10000-3-v1.05.06-sts.xml'
        Url = 'https://reference.opcfoundation.org/specs/OPC-10000-3/v1.05.06/t63925672496/download/sts-xml'
        Sha256 = '8653CD08E11521289F4FFDA7A019C9DD832949F92A9CB0D97C1B7537AEAA392B'
    },
    @{
        Name = 'opc-10000-3-v1.05.06-full.html'
        Url = 'https://reference.opcfoundation.org/specs/OPC-10000-3/v1.05.06/full'
        Sha256 = '6B804DF8BBB9D415BE064746DEA0AC36254DC81F4D7F280B48AF5D2F1D3FE2B9'
    }
)

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
foreach ($inputFile in $inputs) {
    $path = Join-Path $OutputDirectory $inputFile.Name
    Invoke-WebRequest -Uri $inputFile.Url -OutFile $path
    $hash = (Get-FileHash -Path $path -Algorithm SHA256).Hash
    if ($hash -ne $inputFile.Sha256) {
        throw "Publisher input '$($inputFile.Name)' changed: expected $($inputFile.Sha256), received $hash."
    }
    Write-Host "Verified $($inputFile.Name): $hash"
}
