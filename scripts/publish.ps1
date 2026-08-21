[CmdletBinding()]
param(
    [ValidateSet("Release")]
    [string]$Configuration = "Release",
    [switch]$BuildInstaller,
    [ValidatePattern('^$|^v1\.3\.0-alpha\.[1-9][0-9]*$')]
    [string]$ReleaseTag = ''
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $repoRoot ".tools\dotnet\dotnet.exe"
$systemDotnet = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnet = if ($systemDotnet) { $systemDotnet.Source } elseif (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { throw "A .NET SDK matching global.json is required." }
$artifactsRoot = Join-Path $repoRoot "artifacts"
$frameworkOutput = Join-Path $artifactsRoot "framework-dependent"
$selfContainedOutput = Join-Path $artifactsRoot "self-contained-win-x64"
$portableTestOutput = Join-Path $artifactsRoot "portable-test"
$releaseSupportOutput = Join-Path $artifactsRoot "release-support"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"

& (Join-Path $repoRoot "scripts\build-webui.ps1")
if ($LASTEXITCODE -ne 0) { throw "WebUI build failed with exit code $LASTEXITCODE." }

function Reset-OutputDirectory([string]$Path) {
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullArtifacts = [System.IO.Path]::GetFullPath($artifactsRoot) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($fullArtifacts, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean output outside the artifacts directory: $fullPath"
    }
    if (Test-Path -LiteralPath $fullPath) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
    New-Item -ItemType Directory -Path $fullPath -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $fullPath "Agent") -Force | Out-Null
}

& $dotnet restore (Join-Path $repoRoot "ChunkPilot.sln")
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE." }
# Keep the unit and integration projects sequential here. Running both test hosts concurrently can
# exhaust the integration fixtures' short-lived loopback-port pool and makes the publish gate flaky.
& $dotnet test (Join-Path $repoRoot "ChunkPilot.sln") -c $Configuration --no-restore -m:1 --logger "console;verbosity=minimal"
if ($LASTEXITCODE -ne 0) { throw "Tests failed; publish was stopped." }

Reset-OutputDirectory $frameworkOutput
& $dotnet publish (Join-Path $repoRoot "src\ChunkPilot.App\ChunkPilot.App.csproj") -c $Configuration --no-restore --self-contained false -o $frameworkOutput
if ($LASTEXITCODE -ne 0) { throw "Framework-dependent app publish failed." }
& $dotnet publish (Join-Path $repoRoot "src\ChunkPilot.FirewallHelper\ChunkPilot.FirewallHelper.csproj") -c $Configuration --no-restore --self-contained false -o $frameworkOutput
if ($LASTEXITCODE -ne 0) { throw "Framework-dependent firewall helper publish failed." }
& $dotnet publish (Join-Path $repoRoot "src\ChunkPilot.Agent\ChunkPilot.Agent.csproj") -c $Configuration --no-restore --self-contained false -o (Join-Path $frameworkOutput "Agent")
if ($LASTEXITCODE -ne 0) { throw "Framework-dependent agent publish failed." }

Reset-OutputDirectory $selfContainedOutput
& $dotnet publish (Join-Path $repoRoot "src\ChunkPilot.App\ChunkPilot.App.csproj") -c $Configuration -r win-x64 --self-contained true -o $selfContainedOutput
if ($LASTEXITCODE -ne 0) { throw "Self-contained app publish failed." }
& $dotnet publish (Join-Path $repoRoot "src\ChunkPilot.FirewallHelper\ChunkPilot.FirewallHelper.csproj") -c $Configuration -r win-x64 --self-contained true -o $selfContainedOutput
if ($LASTEXITCODE -ne 0) { throw "Self-contained firewall helper publish failed." }
& $dotnet publish (Join-Path $repoRoot "src\ChunkPilot.Agent\ChunkPilot.Agent.csproj") -c $Configuration -r win-x64 --self-contained true -o (Join-Path $selfContainedOutput "Agent")
if ($LASTEXITCODE -ne 0) { throw "Self-contained agent publish failed." }

# Consumer packages intentionally exclude symbols. Keep any developer symbols in intermediate
# build output rather than placing them beside the binaries installed by ordinary users.
Get-ChildItem -LiteralPath $selfContainedOutput -Filter '*.pdb' -File -Recurse |
    Remove-Item -Force

& (Join-Path $repoRoot 'scripts\generate-third-party-notices.ps1') -OutputPath (Join-Path $releaseSupportOutput 'THIRD-PARTY-NOTICES.txt') | Out-Host
Copy-Item -LiteralPath (Join-Path $releaseSupportOutput 'THIRD-PARTY-NOTICES.txt') -Destination (Join-Path $selfContainedOutput 'THIRD-PARTY-NOTICES.txt') -Force

Reset-OutputDirectory $portableTestOutput
Copy-Item -Path (Join-Path $selfContainedOutput "*") -Destination $portableTestOutput -Recurse -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'release\PORTABLE-README.txt') -Destination (Join-Path $portableTestOutput 'README.txt') -Force

if ($BuildInstaller) {
    if (-not $ReleaseTag) { throw '-ReleaseTag is required when -BuildInstaller is used.' }
    & (Join-Path $repoRoot 'scripts\acquire-webview2-bootstrapper.ps1') | Out-Host
    $candidates = @(
        (Join-Path $repoRoot ".tools\InnoSetup\ISCC.exe"),
        (Join-Path ${env:ProgramFiles} "Inno Setup 7\ISCC.exe"),
        (Join-Path ${env:LOCALAPPDATA} "Programs\Inno Setup 7\ISCC.exe")
    )
    $iscc = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
    if (-not $iscc) {
        throw "Inno Setup 7 ISCC.exe was not found. Publish outputs were created, but the installer was not compiled."
    }
    & $iscc "/DMyReleaseTag=$ReleaseTag" (Join-Path $repoRoot "installer\ChunkPilot.iss")
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup compiler failed with exit code $LASTEXITCODE." }
}

Write-Host "Framework-dependent publish: $frameworkOutput"
Write-Host "Self-contained win-x64 publish: $selfContainedOutput"
Write-Host "Portable test build: $portableTestOutput"
