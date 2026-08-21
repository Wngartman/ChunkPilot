[CmdletBinding()]
param(
    [ValidateSet('Quick', 'Feature', 'HighRisk')]
    [string]$Tier = 'Feature',
    [string]$FrontendTest = '',
    [string]$DotNetFilter = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$output = Join-Path $repoRoot 'artifacts\dev-current'
$recovery = Join-Path $repoRoot ('artifacts\recovery\dev-build-' + [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss'))
$commit = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') { throw 'Could not determine the current commit.' }
$branch = (& git -C $repoRoot branch --show-current).Trim()
if ([string]::IsNullOrWhiteSpace($branch)) { throw 'Development builds require a named branch.' }
$status = @(& git -C $repoRoot status --porcelain)
New-Item -ItemType Directory -Path $recovery -Force | Out-Null
$recoveryPatch = Join-Path $recovery 'working-tree.patch'
& git -C $repoRoot diff --binary HEAD "--output=$recoveryPatch"
if ($LASTEXITCODE -ne 0) { throw 'Could not create the recovery patch.' }
$untracked = @(& git -C $repoRoot ls-files --others --exclude-standard)
[IO.File]::WriteAllLines((Join-Path $recovery 'untracked-files.txt'), $untracked, [Text.UTF8Encoding]::new($false))
foreach ($relative in $untracked) {
    $source = Join-Path $repoRoot $relative
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { continue }
    $destination = Join-Path (Join-Path $recovery 'untracked') $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $destination
}

$webUi = Join-Path $repoRoot 'src\ChunkPilot.WebUi'
Push-Location $webUi
try {
    if (-not (Test-Path -LiteralPath (Join-Path $webUi 'node_modules'))) { npm ci }
    npm run typecheck
    if ($LASTEXITCODE -ne 0) { throw 'WebUI typecheck failed.' }
    npm run lint
    if ($LASTEXITCODE -ne 0) { throw 'WebUI lint failed.' }
    if ($FrontendTest) { npm test -- --run $FrontendTest } elseif ($Tier -ne 'Quick') { npm test -- --run }
    if ($LASTEXITCODE -ne 0) { throw 'WebUI tests failed.' }
    npm run build
    if ($LASTEXITCODE -ne 0) { throw 'WebUI build failed.' }
}
finally { Pop-Location }

if ($Tier -eq 'Feature' -and $DotNetFilter) {
    dotnet restore (Join-Path $repoRoot 'tests\ChunkPilot.UnitTests\ChunkPilot.UnitTests.csproj')
    if ($LASTEXITCODE -ne 0) { throw 'Feature-test restore failed.' }
    dotnet test (Join-Path $repoRoot 'tests\ChunkPilot.UnitTests\ChunkPilot.UnitTests.csproj') -c Release --no-restore --filter $DotNetFilter
    if ($LASTEXITCODE -ne 0) { throw 'Targeted feature tests failed.' }
} elseif ($Tier -eq 'HighRisk') {
    dotnet restore (Join-Path $repoRoot 'ChunkPilot.sln')
    if ($LASTEXITCODE -ne 0) { throw 'High-risk test restore failed.' }
    dotnet test (Join-Path $repoRoot 'ChunkPilot.sln') -c Release --no-restore -m:1
    if ($LASTEXITCODE -ne 0) { throw 'High-risk test suite failed.' }
}

# A test-project restore resolves App without a runtime identifier and rewrites its assets file. Restore
# packaged targets after all test restores so the win-x64 publish can never consume that narrower graph.
dotnet restore (Join-Path $repoRoot 'src\ChunkPilot.App\ChunkPilot.App.csproj') -r win-x64
if ($LASTEXITCODE -ne 0) { throw 'App runtime restore failed.' }
dotnet restore (Join-Path $repoRoot 'src\ChunkPilot.Agent\ChunkPilot.Agent.csproj') -r win-x64
if ($LASTEXITCODE -ne 0) { throw 'Agent runtime restore failed.' }

$outputFull = [IO.Path]::GetFullPath($output)
$artifactsFull = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts')) + [IO.Path]::DirectorySeparatorChar
if (-not $outputFull.StartsWith($artifactsFull, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe development output path.' }
if (Test-Path -LiteralPath $outputFull) { Remove-Item -LiteralPath $outputFull -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $outputFull 'Agent') -Force | Out-Null
$timestamp = [DateTimeOffset]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
$identity = @("-p:ChunkPilotGitSha=$commit", '-p:ChunkPilotReleaseTag=v1.3.0-alpha.5-dev', "-p:ChunkPilotBuildTimestampUtc=$timestamp")
$single = @('-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=None', '-p:DebugSymbols=false')
dotnet publish (Join-Path $repoRoot 'src\ChunkPilot.App\ChunkPilot.App.csproj') -c Release -r win-x64 --self-contained true --no-restore -o $outputFull @identity @single
if ($LASTEXITCODE -ne 0) { throw 'App development publish failed.' }
dotnet publish (Join-Path $repoRoot 'src\ChunkPilot.FirewallHelper\ChunkPilot.FirewallHelper.csproj') -c Release -r win-x64 --self-contained true -o $outputFull @identity @single
if ($LASTEXITCODE -ne 0) { throw 'Firewall helper development publish failed.' }
dotnet publish (Join-Path $repoRoot 'src\ChunkPilot.Agent\ChunkPilot.Agent.csproj') -c Release -r win-x64 --self-contained true --no-restore -o (Join-Path $outputFull 'Agent') @identity @single
if ($LASTEXITCODE -ne 0) { throw 'Agent development publish failed.' }
Get-ChildItem -LiteralPath $outputFull -Filter '*.pdb' -File -Recurse | Remove-Item -Force
Get-ChildItem -LiteralPath $outputFull -Filter 'Microsoft.Web.WebView2.*.xml' -File -Recurse | Remove-Item -Force

Write-Host "Development build ready ($Tier). Recovery: $recovery"
Write-Host "Set-Location '$repoRoot'"
Write-Host "& '$outputFull\ChunkPilot.exe'"
