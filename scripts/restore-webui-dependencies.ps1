param(
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$Destination = ""
)

$ErrorActionPreference = 'Stop'
$arguments = @((Join-Path $PSScriptRoot 'restore-webui-dependencies.mjs'), $ProjectRoot)
if (-not [string]::IsNullOrWhiteSpace($Destination)) { $arguments += $Destination }
& node @arguments
if ($LASTEXITCODE -ne 0) { throw "WebUI dependency restore failed with exit code $LASTEXITCODE" }
