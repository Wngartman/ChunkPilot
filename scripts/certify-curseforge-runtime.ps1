[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$KeyFile,
    [Parameter(Mandatory)]
    [string]$Project,
    [Parameter(Mandatory)]
    [string]$ClientFileId,
    [ValidateSet('Metadata', 'Official', 'Full')]
    [string]$Phase = 'Metadata',
    [string]$OfficialNewProject,
    [string]$OfficialNewClientFileId,
    [string]$GeneratedProject,
    [string]$GeneratedClientFileId,
    [string]$GeneratedFallbackProject,
    [string]$GeneratedFallbackClientFileId,
    [string]$ForgeMinecraftVersion,
    [string]$ForgeLoaderVersion,
    [string]$ModProjectId,
    [string]$ModFileId,
    [switch]$AcceptMinecraftEulaForCertification,
    [switch]$RetainStoppedRun,
    [string]$ServerName = 'ChunkPilot CurseForge Certification',
    [ValidateRange(1, 65535)]
    [int]$Port = 25585,
    [string]$RuntimeRoot,
    [string]$Report
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$head = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $head -notmatch '^[0-9a-f]{40}$') {
    throw 'Could not determine the current repository HEAD.'
}

function Assert-TrackedHeadBlob([string]$RelativePath) {
    & git -C $repoRoot ls-files --error-unmatch -- $RelativePath *> $null
    if ($LASTEXITCODE -ne 0) {
        throw 'A credential or package-bearing certification script must be tracked at the current HEAD.'
    }
    $headBlob = (& git -C $repoRoot rev-parse ("HEAD:" + $RelativePath)).Trim()
    $worktreeBlob = (& git -C $repoRoot hash-object ("--path=" + $RelativePath) -- $RelativePath).Trim()
    if ($LASTEXITCODE -ne 0 -or
        $headBlob -notmatch '^[0-9a-fA-F]{40,64}$' -or
        $worktreeBlob -notmatch '^[0-9a-fA-F]{40,64}$' -or
        $headBlob -ine $worktreeBlob) {
        throw 'A credential or package-bearing certification script does not exactly match its tracked HEAD blob.'
    }
}

$wrapperRelative = 'scripts/certify-curseforge-runtime.ps1'
$devBuildRelative = 'scripts/dev-build.ps1'
Assert-TrackedHeadBlob $wrapperRelative
Assert-TrackedHeadBlob $devBuildRelative
$devBuildScript = Join-Path $repoRoot $devBuildRelative
$packageInputProofJson = (& $devBuildScript -PackageInputProofOnly | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($packageInputProofJson)) {
    throw 'The exact package-input proof could not be computed.'
}
$packageInputProof = $packageInputProofJson | ConvertFrom-Json
if (-not $packageInputProof.matchesHead -or
    [int]$packageInputProof.fileCount -lt 1 -or
    [string]$packageInputProof.headSha256 -notmatch '^[0-9a-fA-F]{64}$' -or
    [string]$packageInputProof.worktreeSha256 -ine [string]$packageInputProof.headSha256 -or
    [string]$packageInputProof.indexSha256 -ine [string]$packageInputProof.headSha256) {
    throw 'Package-affecting HEAD, index, and actual worktree inputs do not match exactly. Commit the exact candidate and run .\scripts\dev-build.ps1 -Tier Quick first.'
}
if ([string]::IsNullOrWhiteSpace($RuntimeRoot)) {
    $RuntimeRoot = Join-Path $artifactsRoot 'curseforge-runtime-headless'
}
$runtimeFull = [IO.Path]::GetFullPath($RuntimeRoot)
$artifactsPrefix = $artifactsRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $runtimeFull.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "RuntimeRoot must be a scoped directory under this worktree's artifacts directory."
}

function Assert-NoReparsePoint([string]$Parent, [string]$Candidate) {
    $relative = [IO.Path]::GetRelativePath($Parent, $Candidate)
    $current = $Parent
    $parts = @()
    if ($relative -ne '.') {
        $parts = $relative.Split(
            @([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar),
            [StringSplitOptions]::RemoveEmptyEntries)
    }
    foreach ($part in @('') + $parts) {
        if (-not [string]::IsNullOrEmpty($part)) {
            $current = Join-Path $current $part
        }
        if (-not (Test-Path -LiteralPath $current)) {
            break
        }
        $item = Get-Item -LiteralPath $current -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Certification runtime paths cannot traverse reparse points.'
        }
    }
}

function Get-BoundedNoFollowFiles([string]$Root, [int]$MaximumEntries) {
    $pending = [Collections.Generic.Stack[string]]::new()
    $files = [Collections.Generic.List[IO.FileInfo]]::new()
    $pending.Push([IO.Path]::GetFullPath($Root))
    $count = 0
    while ($pending.Count -gt 0) {
        $current = $pending.Pop()
        foreach ($item in Get-ChildItem -LiteralPath $current -Force) {
            $count++
            if ($count -gt $MaximumEntries) {
                throw 'The bounded package inventory exceeded its safe entry limit.'
            }
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Development package integrity validation refuses reparse points.'
            }
            if ($item.PSIsContainer) {
                $pending.Push($item.FullName)
            }
            else {
                $files.Add([IO.FileInfo]$item)
            }
        }
    }
    return $files.ToArray()
}

Assert-NoReparsePoint $repoRoot $artifactsRoot
Assert-NoReparsePoint $artifactsRoot $runtimeFull
$markerPath = Join-Path $runtimeFull '.chunkpilot-curseforge-certification-root.json'
if ((Test-Path -LiteralPath $runtimeFull -PathType Container) -and
    -not (Test-Path -LiteralPath $markerPath -PathType Leaf) -and
    @(Get-ChildItem -LiteralPath $runtimeFull -Force).Count -gt 0) {
    throw 'A nonempty unmarked RuntimeRoot is not owned and was refused.'
}
[IO.Directory]::CreateDirectory($runtimeFull) | Out-Null
Assert-NoReparsePoint $artifactsRoot $runtimeFull
$canonicalRepo = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($repoRoot)).ToLowerInvariant()
$sha = [Security.Cryptography.SHA256]::Create()
try {
    $fingerprint = [Convert]::ToHexString(
        $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($canonicalRepo))).ToLowerInvariant()
}
finally {
    $sha.Dispose()
}
if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
    $marker = [ordered]@{
        schemaVersion = 1
        purpose = 'ChunkPilot CurseForge runtime certification'
        repositoryFingerprint = $fingerprint
        createdAtUtc = [DateTimeOffset]::UtcNow
    } | ConvertTo-Json
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($marker)
    try {
        $stream = [IO.FileStream]::new(
            $markerPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write,
            [IO.FileShare]::None, 4096, [IO.FileOptions]::WriteThrough)
        try {
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Flush($true)
        }
        finally {
            $stream.Dispose()
        }
    }
    catch [IO.IOException] {
        if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) { throw }
    }
}
$markerItem = Get-Item -LiteralPath $markerPath -Force
if (($markerItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'The certification ownership marker cannot be a reparse point.'
}
$existingMarker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
if ($existingMarker.schemaVersion -ne 1 -or
    $existingMarker.purpose -cne 'ChunkPilot CurseForge runtime certification' -or
    $existingMarker.repositoryFingerprint -ine $fingerprint) {
    throw 'RuntimeRoot is not owned by this exact ChunkPilot worktree.'
}

$agent = Join-Path $repoRoot 'artifacts\dev-current\Agent\ChunkPilot.Agent.exe'
$app = Join-Path $repoRoot 'artifacts\dev-current\ChunkPilot.exe'
$controller = Join-Path $repoRoot 'artifacts\dev-current\Certification\ChunkPilot.Certification.exe'
foreach ($candidate in @($app, $agent, $controller)) {
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw 'The self-contained candidate is missing. Run .\scripts\dev-build.ps1 -Tier Quick first.'
    }
    $productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($candidate).ProductVersion
    if ([string]::IsNullOrWhiteSpace($productVersion) -or
        $productVersion.IndexOf($head, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw 'The packaged App/Agent candidate is stale. Run .\scripts\dev-build.ps1 -Tier Quick first.'
    }
}
$firewallHelper = Join-Path $repoRoot 'artifacts\dev-current\ChunkPilot.FirewallHelper.exe'
if (-not (Test-Path -LiteralPath $firewallHelper -PathType Leaf)) {
    throw 'The packaged FirewallHelper candidate is missing. Run .\scripts\dev-build.ps1 -Tier Quick first.'
}
$firewallProductVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($firewallHelper).ProductVersion
if ([string]::IsNullOrWhiteSpace($firewallProductVersion) -or
    $firewallProductVersion.IndexOf($head, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
    throw 'The packaged FirewallHelper candidate is stale. Run .\scripts\dev-build.ps1 -Tier Quick first.'
}
$packageRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts\dev-current'))
$packageRootItem = Get-Item -LiteralPath $packageRoot -Force
if (($packageRootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'Development package integrity validation refuses a redirected package root.'
}
$integrityManifestPath = Join-Path $packageRoot '.chunkpilot-build-manifest.json'
if (-not (Test-Path -LiteralPath $integrityManifestPath -PathType Leaf)) {
    throw 'The HEAD-bound development package integrity manifest is missing. Run .\scripts\dev-build.ps1 -Tier Quick first.'
}
$integrityManifestInfo = Get-Item -LiteralPath $integrityManifestPath -Force
if ($integrityManifestInfo.Length -lt 1 -or $integrityManifestInfo.Length -gt 4MB -or
    ($integrityManifestInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'The HEAD-bound development package integrity manifest is invalid.'
}
$integrityManifest = Get-Content -LiteralPath $integrityManifestPath -Raw | ConvertFrom-Json
$integrityEntries = @($integrityManifest.files)
if ($integrityManifest.schemaVersion -ne 3 -or
    $integrityManifest.gitSha -ine $head -or
    [string]$integrityManifest.packageSourceKind -cne 'isolated-head-archive' -or
    -not [bool]$integrityManifest.packageInputsMatchHead -or
    [int]$integrityManifest.packageInputFileCount -ne [int]$packageInputProof.fileCount -or
    [string]$integrityManifest.packageInputHeadSha256 -ine [string]$packageInputProof.headSha256 -or
    [string]$integrityManifest.packageInputWorktreeSha256 -ine [string]$packageInputProof.worktreeSha256 -or
    [string]$integrityManifest.packageInputIndexSha256 -ine [string]$packageInputProof.indexSha256 -or
    $integrityEntries.Count -lt 3 -or $integrityEntries.Count -gt 10000) {
    throw 'The development package integrity manifest is not bound to the current HEAD.'
}
$manifestByPath = @{}
foreach ($entry in $integrityEntries) {
    $relative = [string]$entry.relativePath
    $segments = @($relative.Split('/'))
    if ([string]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathRooted($relative) -or
        $relative.Contains('\') -or $segments -contains '' -or $segments -contains '.' -or
        $segments -contains '..') {
        throw 'The development package integrity manifest contains an unsafe relative path.'
    }
    $identity = $relative.ToLowerInvariant()
    if ($manifestByPath.ContainsKey($identity)) {
        throw 'The development package integrity manifest contains a duplicate path.'
    }
    $manifestByPath[$identity] = $entry
}
$actualPackageFiles = @(Get-BoundedNoFollowFiles $packageRoot 20000 |
    Where-Object { $_.FullName -ne $integrityManifestPath })
if ($actualPackageFiles.Count -ne $manifestByPath.Count) {
    throw 'The development package file set does not match its HEAD-bound integrity manifest.'
}
foreach ($file in $actualPackageFiles) {
    $relative = [IO.Path]::GetRelativePath($packageRoot, $file.FullName).Replace('\', '/')
    $identity = $relative.ToLowerInvariant()
    if (-not $manifestByPath.ContainsKey($identity)) {
        throw 'The development package contains a file absent from its integrity manifest.'
    }
    $expected = $manifestByPath[$identity]
    $actualHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    if ([long]$expected.sizeBytes -lt 0 -or
        [string]$expected.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or
        $file.Length -ne [long]$expected.sizeBytes -or
        $actualHash -ine [string]$expected.sha256) {
        throw 'A development package file does not match its HEAD-bound SHA-256 manifest.'
    }
}
$keyPath = [IO.Path]::GetFullPath($KeyFile)
if (-not (Test-Path -LiteralPath $keyPath -PathType Leaf)) {
    throw 'The approved CurseForge key file is unavailable.'
}
$keyItem = Get-Item -LiteralPath $keyPath -Force
if (($keyItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'The approved CurseForge key source cannot be a reparse point.'
}
if ($Phase -in @('Official', 'Full') -and -not $AcceptMinecraftEulaForCertification) {
    throw 'Official and Full certification require -AcceptMinecraftEulaForCertification.'
}
if ($Phase -eq 'Full') {
    $requiredFullValues = [ordered]@{
        OfficialNewProject = $OfficialNewProject
        OfficialNewClientFileId = $OfficialNewClientFileId
        GeneratedProject = $GeneratedProject
        GeneratedClientFileId = $GeneratedClientFileId
        ForgeMinecraftVersion = $ForgeMinecraftVersion
        ForgeLoaderVersion = $ForgeLoaderVersion
        ModProjectId = $ModProjectId
        ModFileId = $ModFileId
    }
    foreach ($entry in $requiredFullValues.GetEnumerator()) {
        if ([string]::IsNullOrWhiteSpace([string]$entry.Value)) {
            throw ("Full certification requires -" + $entry.Key + '.')
        }
    }
    $hasGeneratedFallbackProject = -not [string]::IsNullOrWhiteSpace($GeneratedFallbackProject)
    $hasGeneratedFallbackFile = -not [string]::IsNullOrWhiteSpace($GeneratedFallbackClientFileId)
    if ($hasGeneratedFallbackProject -ne $hasGeneratedFallbackFile) {
        throw 'Full generated fallback project and client file ID must be supplied together.'
    }
}
$evidenceRoot = [IO.Path]::GetFullPath((Join-Path $runtimeFull 'evidence'))
[IO.Directory]::CreateDirectory($evidenceRoot) | Out-Null
Assert-NoReparsePoint $runtimeFull $evidenceRoot
$reportFull = $null
if (-not [string]::IsNullOrWhiteSpace($Report)) {
    $reportFull = [IO.Path]::GetFullPath($Report)
    $reportLeaf = [IO.Path]::GetFileName($reportFull)
    if ([IO.Path]::GetDirectoryName($reportFull) -ine $evidenceRoot -or
        $reportLeaf -notmatch '^certification-[A-Za-z0-9._-]+\.json$' -or
        (Test-Path -LiteralPath $reportFull)) {
        throw 'Report must be a new safe certification-*.json file directly under RuntimeRoot\evidence.'
    }
    Assert-NoReparsePoint $evidenceRoot $reportFull
}

$runsRoot = [IO.Path]::GetFullPath((Join-Path $runtimeFull 'runs'))
[IO.Directory]::CreateDirectory($runsRoot) | Out-Null
Assert-NoReparsePoint $runtimeFull $runsRoot
$runId = 'run-' + [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' +
    [Guid]::NewGuid().ToString('N')
$runRoot = [IO.Path]::GetFullPath((Join-Path $runsRoot $runId))
if (Test-Path -LiteralPath $runRoot) {
    throw 'The fresh certification run identity unexpectedly already exists.'
}
[IO.Directory]::CreateDirectory($runRoot) | Out-Null
Assert-NoReparsePoint $runsRoot $runRoot
$taskTemp = [IO.Path]::GetFullPath((Join-Path $runRoot 'temp'))
[IO.Directory]::CreateDirectory($taskTemp) | Out-Null
Assert-NoReparsePoint $runRoot $taskTemp
if (@(Get-ChildItem -LiteralPath $runRoot -Force).Count -ne 1) {
    throw 'The fresh certification run root contains an unexpected entry.'
}

$controllerArguments = @(
    'certify-curseforge-runtime',
    '--runtime-root', $runtimeFull,
    '--run-id', $runId,
    '--project', $Project,
    '--client-file-id', $ClientFileId,
    '--phase', $Phase,
    '--server-name', $ServerName,
    '--port', $Port.ToString([Globalization.CultureInfo]::InvariantCulture)
)
if ($null -ne $reportFull) {
    $controllerArguments += @('--report', $reportFull)
}
if ($AcceptMinecraftEulaForCertification) {
    $controllerArguments += '--accept-minecraft-eula-for-certification'
}
if ($RetainStoppedRun) {
    $controllerArguments += '--retain-stopped-run'
}
if ($Phase -eq 'Full') {
    $controllerArguments += @(
        '--official-new-project', $OfficialNewProject,
        '--official-new-client-file-id', $OfficialNewClientFileId,
        '--generated-project', $GeneratedProject,
        '--generated-client-file-id', $GeneratedClientFileId,
        '--forge-minecraft-version', $ForgeMinecraftVersion,
        '--forge-loader-version', $ForgeLoaderVersion,
        '--mod-project-id', $ModProjectId,
        '--mod-file-id', $ModFileId
    )
    if (-not [string]::IsNullOrWhiteSpace($GeneratedFallbackProject)) {
        $controllerArguments += @(
            '--generated-fallback-project', $GeneratedFallbackProject,
            '--generated-fallback-client-file-id', $GeneratedFallbackClientFileId
        )
    }
}

$hadPreviousKeySource = Test-Path Env:\CHUNKPILOT_CURSEFORGE_KEY_FILE
$previousKeySource = $env:CHUNKPILOT_CURSEFORGE_KEY_FILE
$previousTaskEnvironment = @{}
foreach ($name in @('TEMP', 'TMP', 'DOTNET_BUNDLE_EXTRACT_BASE_DIR')) {
    $exists = Test-Path ("Env:\" + $name)
    $previousTaskEnvironment[$name] = [pscustomobject]@{
        exists = $exists
        value = if ($exists) { [Environment]::GetEnvironmentVariable($name) } else { $null }
    }
}
$controllerExitCode = 2
try {
    $env:CHUNKPILOT_CURSEFORGE_KEY_FILE = $keyPath
    $env:TEMP = $taskTemp
    $env:TMP = $taskTemp
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $taskTemp
    Push-Location $repoRoot
    try {
        & $controller @controllerArguments
        $controllerExitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
}
finally {
    if ($hadPreviousKeySource) {
        $env:CHUNKPILOT_CURSEFORGE_KEY_FILE = $previousKeySource
    }
    else {
        Remove-Item Env:\CHUNKPILOT_CURSEFORGE_KEY_FILE -ErrorAction SilentlyContinue
    }
    foreach ($name in @('TEMP', 'TMP', 'DOTNET_BUNDLE_EXTRACT_BASE_DIR')) {
        $previous = $previousTaskEnvironment[$name]
        if ($previous.exists) {
            [Environment]::SetEnvironmentVariable($name, [string]$previous.value)
        }
        else {
            Remove-Item ("Env:\" + $name) -ErrorAction SilentlyContinue
        }
    }
}
exit $controllerExitCode
