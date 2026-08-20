[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $repoRoot ".tools\dotnet\dotnet.exe"
$systemDotnet = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnet = if ($systemDotnet) { $systemDotnet.Source } elseif (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { throw "A .NET SDK matching global.json is required." }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"

& (Join-Path $repoRoot "scripts\build-webui.ps1")
if ($LASTEXITCODE -ne 0) { throw "WebUI build failed with exit code $LASTEXITCODE." }

& $dotnet restore (Join-Path $repoRoot "ChunkPilot.sln")
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE." }
& $dotnet build (Join-Path $repoRoot "ChunkPilot.sln") -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE." }
