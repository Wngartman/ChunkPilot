[CmdletBinding()]
param(
    [ValidateSet('Quick', 'Feature', 'HighRisk')]
    [string]$Tier = 'Feature',
    [string]$FrontendTest = '',
    [string]$DotNetFilter = '',
    [switch]$PackageInputProofOnly
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$packagePaths = @(
    'src',
    'assets',
    '.editorconfig',
    '.gitattributes',
    'ChunkPilot.sln',
    'Directory.Build.props',
    'Directory.Build.targets',
    'Directory.Packages.props',
    'global.json',
    'NuGet.Config',
    'nuget.config',
    'scripts/build-webui.ps1',
    'scripts/dev-build.ps1',
    'scripts/certify-curseforge-runtime.ps1'
)

function Invoke-PackageProofGit([string[]]$Arguments) {
    $output = @(& git -C $repoRoot @Arguments)
    if ($LASTEXITCODE -ne 0) {
        throw 'Git could not compute the exact package-input proof.'
    }
    return $output
}

function Assert-SafeGitPath([string]$RelativePath) {
    if ([string]::IsNullOrWhiteSpace($RelativePath) -or
        [IO.Path]::IsPathRooted($RelativePath) -or
        $RelativePath.Contains('\') -or
        $RelativePath.Contains("`0") -or $RelativePath.Contains("`r") -or
        $RelativePath.Contains("`n") -or
        @($RelativePath.Split('/')) -contains '' -or
        @($RelativePath.Split('/')) -contains '.' -or
        @($RelativePath.Split('/')) -contains '..') {
        throw 'Git returned an unsafe package-input path.'
    }
}

function Get-PackageEntryDigest(
    [Collections.Generic.Dictionary[string, string]]$Entries) {
    $canonical = [Text.StringBuilder]::new()
    $paths = [string[]]@($Entries.Keys)
    [Array]::Sort($paths, [StringComparer]::Ordinal)
    foreach ($path in $paths) {
        [void]$canonical.Append($path).Append("`0").Append(
            $Entries[$path].ToLowerInvariant()).Append("`n")
    }
    return [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData(
            [Text.Encoding]::UTF8.GetBytes($canonical.ToString()))).ToLowerInvariant()
}

function Test-AllowedGeneratedPackageArtifact([string]$RelativePath) {
    if (-not $RelativePath.StartsWith('src/', [StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }
    foreach ($segment in $RelativePath.Split('/')) {
        if ($segment.Equals('bin', [StringComparison]::OrdinalIgnoreCase) -or
            $segment.Equals('obj', [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }
    if ($RelativePath.StartsWith(
            'src/ChunkPilot.WebUi/node_modules/', [StringComparison]::OrdinalIgnoreCase) -or
        $RelativePath.StartsWith(
            'src/ChunkPilot.WebUi/dist/', [StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }
    return $RelativePath.StartsWith(
        'src/ChunkPilot.WebUi/', [StringComparison]::OrdinalIgnoreCase) -and
        $RelativePath.EndsWith('.tsbuildinfo', [StringComparison]::OrdinalIgnoreCase)
}

function Get-PackageInputProof([string]$ExpectedCommit = 'HEAD') {
    $head = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    foreach ($line in @(Invoke-PackageProofGit (
            @('ls-tree', '-r', '--full-tree', $ExpectedCommit, '--') + $packagePaths))) {
        $tab = $line.IndexOf("`t")
        if ($tab -le 0) { throw 'Git returned a malformed HEAD package-input entry.' }
        $metadata = @($line.Substring(0, $tab).Split(
            ' ', [StringSplitOptions]::RemoveEmptyEntries))
        $path = $line.Substring($tab + 1)
        Assert-SafeGitPath $path
        if ($metadata.Count -ne 3 -or $metadata[1] -cne 'blob' -or
            $metadata[2] -notmatch '^[0-9a-fA-F]{40,64}$' -or
            -not $head.TryAdd($path, $metadata[2].ToLowerInvariant())) {
            throw 'Git returned a duplicate or unsupported HEAD package input.'
        }
    }
    if ($head.Count -lt 1) { throw 'The current HEAD contains no package-affecting inputs.' }

    $index = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    foreach ($line in @(Invoke-PackageProofGit (
            @('ls-files', '--stage', '--') + $packagePaths))) {
        $tab = $line.IndexOf("`t")
        if ($tab -le 0) { throw 'Git returned a malformed index package-input entry.' }
        $metadata = @($line.Substring(0, $tab).Split(
            ' ', [StringSplitOptions]::RemoveEmptyEntries))
        $path = $line.Substring($tab + 1)
        Assert-SafeGitPath $path
        if ($metadata.Count -ne 3 -or $metadata[2] -cne '0' -or
            $metadata[1] -notmatch '^[0-9a-fA-F]{40,64}$' -or
            -not $index.TryAdd($path, $metadata[1].ToLowerInvariant())) {
            throw 'The package-affecting index is unmerged, duplicated, or unsupported.'
        }
    }

    $untracked = @(Invoke-PackageProofGit (
        @('ls-files', '--others', '--exclude-standard', '--') + $packagePaths))
    foreach ($path in $untracked) { Assert-SafeGitPath $path }
    $unexpectedIgnored = [Collections.Generic.List[string]]::new()
    foreach ($path in @(Invoke-PackageProofGit (
            @('ls-files', '--others', '--ignored', '--exclude-standard', '--') + $packagePaths))) {
        Assert-SafeGitPath $path
        if (-not (Test-AllowedGeneratedPackageArtifact $path)) {
            $unexpectedIgnored.Add($path)
        }
    }
    $pathSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($path in $head.Keys) { [void]$pathSet.Add($path) }
    foreach ($path in $index.Keys) { [void]$pathSet.Add($path) }
    foreach ($path in $untracked) { [void]$pathSet.Add($path) }
    foreach ($path in $unexpectedIgnored) { [void]$pathSet.Add($path) }
    $worktreePaths = [string[]]@($pathSet)
    [Array]::Sort($worktreePaths, [StringComparer]::Ordinal)
    $existingPaths = [Collections.Generic.List[string]]::new()
    foreach ($path in $worktreePaths) {
        $candidate = [IO.Path]::GetFullPath((Join-Path $repoRoot $path))
        $repoPrefix = [IO.Path]::TrimEndingDirectorySeparator(
            [IO.Path]::GetFullPath($repoRoot)) + [IO.Path]::DirectorySeparatorChar
        if (-not $candidate.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'A package input escaped the current repository.'
        }
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            $existingPaths.Add($path)
        }
    }
    $worktree = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    if ($existingPaths.Count -gt 0) {
        $hashes = @($existingPaths.ToArray() | & git -C $repoRoot hash-object --stdin-paths)
        if ($LASTEXITCODE -ne 0 -or $hashes.Count -ne $existingPaths.Count) {
            throw 'Git returned a contradictory package-input worktree hash set.'
        }
        for ($indexValue = 0; $indexValue -lt $existingPaths.Count; $indexValue++) {
            if ([string]$hashes[$indexValue] -notmatch '^[0-9a-fA-F]{40,64}$') {
                throw 'Git returned an invalid package-input worktree identity.'
            }
            $worktree.Add(
                $existingPaths[$indexValue], ([string]$hashes[$indexValue]).ToLowerInvariant())
        }
    }
    foreach ($path in $worktreePaths) {
        if (-not $worktree.ContainsKey($path)) { $worktree.Add($path, 'missing') }
    }
    $headDigest = Get-PackageEntryDigest $head
    $worktreeDigest = Get-PackageEntryDigest $worktree
    $indexDigest = Get-PackageEntryDigest $index
    return [pscustomobject]@{
        headSha256 = $headDigest
        worktreeSha256 = $worktreeDigest
        indexSha256 = $indexDigest
        fileCount = $head.Count
        matchesHead = $headDigest -ceq $worktreeDigest -and $headDigest -ceq $indexDigest
    }
}

function Get-BoundedNoFollowFiles([string]$Root, [int]$MaximumEntries) {
    $pending = [Collections.Generic.Stack[string]]::new()
    $files = [Collections.Generic.List[IO.FileInfo]]::new()
    $rootFull = [IO.Path]::GetFullPath($Root)
    $rootItem = Get-Item -LiteralPath $rootFull -Force
    if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Development package inventory refuses a redirected root.'
    }
    $pending.Push($rootFull)
    $count = 0
    while ($pending.Count -gt 0) {
        $current = $pending.Pop()
        foreach ($item in Get-ChildItem -LiteralPath $current -Force) {
            $count++
            if ($count -gt $MaximumEntries) {
                throw 'The bounded development package inventory exceeded its safe entry limit.'
            }
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Development package inventory refuses reparse points.'
            }
            if ($item.PSIsContainer) { $pending.Push($item.FullName) }
            else { $files.Add([IO.FileInfo]$item) }
        }
    }
    return $files.ToArray()
}

$commit = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') {
    throw 'Could not determine the current commit.'
}
$packageInputProofBefore = Get-PackageInputProof $commit
$headAfterInitialProof = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $headAfterInitialProof -cne $commit) {
    $packageInputProofBefore.matchesHead = $false
}
if ($PackageInputProofOnly) {
    $packageInputProofBefore | ConvertTo-Json -Compress
    return
}
$output = Join-Path $repoRoot 'artifacts\dev-current'
$recovery = Join-Path $repoRoot ('artifacts\recovery\dev-build-' + [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss'))
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

$buildRoot = $repoRoot
$isolatedSourceSession = $null
$packageSourceKind = 'worktree'
try {
if ($packageInputProofBefore.matchesHead) {
    $isolatedSourceSession = Join-Path $repoRoot (
        'artifacts\temp\dev-build-source-' + [Guid]::NewGuid().ToString('N'))
    $isolatedSourceRoot = Join-Path $isolatedSourceSession 'source'
    $isolatedSourceArchive = Join-Path $isolatedSourceSession 'head.zip'
    New-Item -ItemType Directory -Path $isolatedSourceSession | Out-Null
    & git -C $repoRoot archive --format=zip "--output=$isolatedSourceArchive" $commit
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $isolatedSourceArchive -PathType Leaf)) {
        throw 'Could not materialize the exact HEAD source archive.'
    }
    [IO.Compression.ZipFile]::ExtractToDirectory($isolatedSourceArchive, $isolatedSourceRoot)
    Remove-Item -LiteralPath $isolatedSourceArchive -Force
    $buildRoot = $isolatedSourceRoot
    $packageSourceKind = 'isolated-head-archive'
}

$webUi = Join-Path $buildRoot 'src\ChunkPilot.WebUi'
Push-Location $webUi
try {
    if ($packageSourceKind -eq 'isolated-head-archive' -or
        -not (Test-Path -LiteralPath (Join-Path $webUi 'node_modules'))) {
        npm ci --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { throw 'WebUI dependency restore failed.' }
        $lockHash = (Get-FileHash -LiteralPath (Join-Path $webUi 'package-lock.json') -Algorithm SHA256).Hash.ToLowerInvariant()
        [IO.File]::WriteAllText(
            (Join-Path $webUi 'node_modules\.chunkpilot-lock-sha256'),
            "$lockHash`n",
            [Text.UTF8Encoding]::new($false))
    }
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
    dotnet restore (Join-Path $buildRoot 'tests\ChunkPilot.UnitTests\ChunkPilot.UnitTests.csproj')
    if ($LASTEXITCODE -ne 0) { throw 'Feature-test restore failed.' }
    dotnet test (Join-Path $buildRoot 'tests\ChunkPilot.UnitTests\ChunkPilot.UnitTests.csproj') -c Release --no-restore --filter $DotNetFilter
    if ($LASTEXITCODE -ne 0) { throw 'Targeted feature tests failed.' }
} elseif ($Tier -eq 'HighRisk') {
    dotnet restore (Join-Path $buildRoot 'ChunkPilot.sln')
    if ($LASTEXITCODE -ne 0) { throw 'High-risk test restore failed.' }
    $testArguments = @('test', (Join-Path $buildRoot 'ChunkPilot.sln'), '-c', 'Release', '--no-restore', '-m:1')
    if ($DotNetFilter) {
        Write-Host "High-risk test selection (excluded tests are not certified): $DotNetFilter"
        $testArguments += @('--filter', $DotNetFilter)
    }
    dotnet @testArguments
    if ($LASTEXITCODE -ne 0) { throw 'High-risk test suite failed.' }
}

# A test-project restore resolves App without a runtime identifier and rewrites its assets file. Restore
# packaged targets after all test restores so the win-x64 publish can never consume that narrower graph.
dotnet restore (Join-Path $buildRoot 'src\ChunkPilot.App\ChunkPilot.App.csproj') -r win-x64
if ($LASTEXITCODE -ne 0) { throw 'App runtime restore failed.' }
dotnet restore (Join-Path $buildRoot 'src\ChunkPilot.Agent\ChunkPilot.Agent.csproj') -r win-x64
if ($LASTEXITCODE -ne 0) { throw 'Agent runtime restore failed.' }
dotnet restore (Join-Path $buildRoot 'src\ChunkPilot.Certification\ChunkPilot.Certification.csproj') -r win-x64
if ($LASTEXITCODE -ne 0) { throw 'Certification runtime restore failed.' }

$outputFull = [IO.Path]::GetFullPath($output)
$artifactsFull = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts')) + [IO.Path]::DirectorySeparatorChar
if (-not $outputFull.StartsWith($artifactsFull, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe development output path.' }
if (Test-Path -LiteralPath $outputFull) {
    [void](Get-BoundedNoFollowFiles $outputFull 20000)
    Remove-Item -LiteralPath $outputFull -Recurse -Force
}
New-Item -ItemType Directory -Path (Join-Path $outputFull 'Agent') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $outputFull 'Certification') -Force | Out-Null
$timestamp = [DateTimeOffset]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
$identity = @("-p:ChunkPilotGitSha=$commit", '-p:ChunkPilotReleaseTag=v1.3.0-alpha.5-dev', "-p:ChunkPilotBuildTimestampUtc=$timestamp")
$single = @('-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=None', '-p:DebugSymbols=false')
$multi = @('-p:PublishSingleFile=false', '-p:DebugType=None', '-p:DebugSymbols=false')
dotnet publish (Join-Path $buildRoot 'src\ChunkPilot.App\ChunkPilot.App.csproj') -c Release -r win-x64 --self-contained true --no-restore -o $outputFull @identity @single
if ($LASTEXITCODE -ne 0) { throw 'App development publish failed.' }
dotnet publish (Join-Path $buildRoot 'src\ChunkPilot.FirewallHelper\ChunkPilot.FirewallHelper.csproj') -c Release -r win-x64 --self-contained true -o $outputFull @identity @single
if ($LASTEXITCODE -ne 0) { throw 'Firewall helper development publish failed.' }
dotnet publish (Join-Path $buildRoot 'src\ChunkPilot.Agent\ChunkPilot.Agent.csproj') -c Release -r win-x64 --self-contained true --no-restore -o (Join-Path $outputFull 'Agent') @identity @single
if ($LASTEXITCODE -ne 0) { throw 'Agent development publish failed.' }
dotnet publish (Join-Path $buildRoot 'src\ChunkPilot.Certification\ChunkPilot.Certification.csproj') -c Release -r win-x64 --self-contained true --no-restore -o (Join-Path $outputFull 'Certification') @identity @multi
if ($LASTEXITCODE -ne 0) { throw 'Certification controller development publish failed.' }
$publishedFiles = @(Get-BoundedNoFollowFiles $outputFull 20000)
$publishedFiles | Where-Object {
    $_.Extension -ieq '.pdb' -or $_.Name -like 'Microsoft.Web.WebView2.*.xml'
} | Remove-Item -Force
$integrityManifestPath = Join-Path $outputFull '.chunkpilot-build-manifest.json'
$integrityFiles = @(Get-BoundedNoFollowFiles $outputFull 20000 |
    Where-Object { $_.FullName -ne $integrityManifestPath } |
    Sort-Object FullName |
    ForEach-Object {
        [ordered]@{
            relativePath = [IO.Path]::GetRelativePath($outputFull, $_.FullName).Replace('\', '/')
            sizeBytes = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    })
$packageInputProofAfter = Get-PackageInputProof $commit
$headAfterBuild = (& git -C $repoRoot rev-parse HEAD).Trim()
$packageInputsMatchHead = $packageInputProofBefore.matchesHead -and
    $packageInputProofAfter.matchesHead -and
    $packageSourceKind -ceq 'isolated-head-archive' -and
    $LASTEXITCODE -eq 0 -and $headAfterBuild -ceq $commit -and
    $packageInputProofBefore.headSha256 -ceq $packageInputProofAfter.headSha256 -and
    $packageInputProofBefore.worktreeSha256 -ceq $packageInputProofAfter.worktreeSha256 -and
    $packageInputProofBefore.indexSha256 -ceq $packageInputProofAfter.indexSha256 -and
    $packageInputProofBefore.fileCount -eq $packageInputProofAfter.fileCount
$integrityManifest = [ordered]@{
    schemaVersion = 3
    gitSha = $commit.ToLowerInvariant()
    packageSourceKind = $packageSourceKind
    packageInputsMatchHead = $packageInputsMatchHead
    packageInputFileCount = $packageInputProofBefore.fileCount
    packageInputHeadSha256 = $packageInputProofBefore.headSha256
    packageInputWorktreeSha256 = $packageInputProofBefore.worktreeSha256
    packageInputIndexSha256 = $packageInputProofBefore.indexSha256
    files = $integrityFiles
} | ConvertTo-Json -Depth 5
$integrityTemporary = $integrityManifestPath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
try {
    [IO.File]::WriteAllText($integrityTemporary, $integrityManifest, [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $integrityTemporary -Destination $integrityManifestPath
}
finally {
    if (Test-Path -LiteralPath $integrityTemporary) {
        Remove-Item -LiteralPath $integrityTemporary -Force
    }
}

Write-Host "Development build ready ($Tier). Recovery: $recovery"
if (-not $packageInputsMatchHead) {
    Write-Warning 'This development package was built from inputs that did not exactly match HEAD and cannot be used for live certification.'
}
Write-Host "Set-Location '$repoRoot'"
Write-Host "& '$outputFull\ChunkPilot.exe'"
}
finally {
    if ($null -ne $isolatedSourceSession -and
        (Test-Path -LiteralPath $isolatedSourceSession)) {
        [void](Get-BoundedNoFollowFiles $isolatedSourceSession 50000)
        Remove-Item -LiteralPath $isolatedSourceSession -Recurse -Force
    }
}
