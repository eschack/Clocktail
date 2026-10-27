param(
    [Parameter(Mandatory = $true)][ValidatePattern('^\d+\.\d+\.\d+([-.][A-Za-z0-9.-]+)?$')][string]$Version
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$outputPath = Join-Path $repoRoot "out\tray-$Version"
dotnet publish (Join-Path $repoRoot 'src\Clocktail.Tray') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none "-p:Version=$Version" -o $outputPath -nologo
if ($LASTEXITCODE -ne 0) { throw 'Tray publish failed.' }
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE'), (Join-Path $repoRoot 'THIRD_PARTY_NOTICES.md') -Destination $outputPath
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\TRAY.md') -Destination (Join-Path $outputPath 'README.md')
$validationSource = Join-Path $repoRoot 'docs\validation'
if (Test-Path -LiteralPath $validationSource) { Copy-Item -LiteralPath $validationSource -Destination $outputPath -Recurse -Force }
$zipPath = Join-Path $repoRoot "out\Clocktail-Tray-$Version-win-x64.zip"
Compress-Archive -Path (Join-Path $outputPath '*') -DestinationPath $zipPath -Force
Write-Output $zipPath
