[CmdletBinding()]
param(
    [string]$KeyFile = ''
)

$ErrorActionPreference = 'Stop'
$worktreeRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))

function Invoke-GitSingleLine {
    param([Parameter(Mandatory)][string[]]$Arguments)
    $lines = @(& git -C $worktreeRoot @Arguments 2>$null)
    if ($LASTEXITCODE -ne 0 -or $lines.Count -ne 1 -or [string]::IsNullOrWhiteSpace($lines[0])) {
        throw "Git could not resolve the current ChunkPilot worktree."
    }
    return $lines[0].Trim()
}

$bare = Invoke-GitSingleLine @('rev-parse', '--is-bare-repository')
if ($bare -ne 'false') { throw 'The current ChunkPilot worktree must belong to a non-bare repository.' }
$commonDirectory = [IO.Path]::GetFullPath((Invoke-GitSingleLine @(
    'rev-parse', '--path-format=absolute', '--git-common-dir')))
if ((Split-Path -Leaf $commonDirectory) -ne '.git' -or -not (Test-Path -LiteralPath $commonDirectory -PathType Container)) {
    throw 'Git did not resolve one ordinary primary ChunkPilot repository.'
}
$primaryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $commonDirectory))
$worktreeLines = @(& git -C $worktreeRoot worktree list --porcelain)
if ($LASTEXITCODE -ne 0) { throw 'Git worktree inventory is unavailable.' }
$registeredRoots = @($worktreeLines | Where-Object { $_ -match '^worktree\s+' } | ForEach-Object {
    [IO.Path]::GetFullPath(($_ -replace '^worktree\s+', '').Trim())
})
if (@($registeredRoots | Where-Object { $_.Equals($primaryRoot, [StringComparison]::OrdinalIgnoreCase) }).Count -ne 1) {
    throw 'Git did not identify one registered primary ChunkPilot worktree.'
}

$approvedSource = if ([string]::IsNullOrWhiteSpace($KeyFile)) {
    Join-Path $primaryRoot '.secrets\curseforge-api-key.txt'
} elseif ([IO.Path]::IsPathFullyQualified($KeyFile)) {
    [IO.Path]::GetFullPath($KeyFile)
} else {
    throw '-KeyFile must be one absolute file path.'
}
if (-not (Test-Path -LiteralPath $approvedSource -PathType Leaf)) {
    throw "Approved key file missing. Create '<primary repository>\.secrets\curseforge-api-key.txt', then rerun .\scripts\start-curseforge-dev.ps1."
}
$sourceItem = Get-Item -LiteralPath $approvedSource -Force
if (($sourceItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'The approved key file must be an ordinary local file, not a link.'
}

$appPath = Join-Path $worktreeRoot 'artifacts\dev-current\ChunkPilot.exe'
$agentPath = Join-Path $worktreeRoot 'artifacts\dev-current\Agent\ChunkPilot.Agent.exe'
if (-not (Test-Path -LiteralPath $appPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $agentPath -PathType Leaf)) {
    throw 'Development package missing. Run .\scripts\dev-build.ps1 -Tier Quick, then retry.'
}
$appPath = [IO.Path]::GetFullPath($appPath)
$agentPath = [IO.Path]::GetFullPath($agentPath)
$running = @(Get-CimInstance Win32_Process | Where-Object {
    $_.ExecutablePath -and (
        [IO.Path]::GetFullPath($_.ExecutablePath).Equals($appPath, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFullPath($_.ExecutablePath).Equals($agentPath, [StringComparison]::OrdinalIgnoreCase))
})
if ($running.Count -gt 0) {
    throw 'This worktree development App or Agent is already running. Close it normally, then retry.'
}

$repoBytes = [Text.Encoding]::UTF8.GetBytes($worktreeRoot.ToUpperInvariant())
try {
    $instanceHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($repoBytes)).Substring(0, 12).ToLowerInvariant()
}
finally {
    [Security.Cryptography.CryptographicOperations]::ZeroMemory($repoBytes)
}
$instanceId = "curseforge-dev-$instanceHash"
$pipeName = "ChunkPilot.Agent.v1.$instanceId"

$artifactsDirectory = [IO.Path]::GetFullPath((Join-Path $worktreeRoot 'artifacts'))
$runtimeRoot = [IO.Path]::GetFullPath((Join-Path $artifactsDirectory 'curseforge-dev-runtime'))
$artifactsPrefix = $artifactsDirectory + [IO.Path]::DirectorySeparatorChar
if (-not ($runtimeRoot + [IO.Path]::DirectorySeparatorChar).StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The isolated development runtime root did not resolve under this worktree artifacts directory.'
}
$artifactsItem = Get-Item -LiteralPath $artifactsDirectory -Force
if (($artifactsItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'The worktree artifacts directory must be an ordinary local directory, not a link.'
}

$runtimeMarkerPath = Join-Path $runtimeRoot '.chunkpilot-curseforge-dev-runtime.json'
$runtimeMarker = [ordered]@{
    schemaVersion = 1
    purpose = 'ChunkPilot CurseForge development runtime'
    worktreeRoot = $worktreeRoot
    runtimeRoot = $runtimeRoot
    instanceId = $instanceId
}
$runtimeMarkerJson = $runtimeMarker | ConvertTo-Json -Compress
if (Test-Path -LiteralPath $runtimeRoot) {
    if (-not (Test-Path -LiteralPath $runtimeRoot -PathType Container)) {
        throw 'The isolated development runtime root exists but is not a directory.'
    }
    $runtimeItem = Get-Item -LiteralPath $runtimeRoot -Force
    if (($runtimeItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'The isolated development runtime root must be an ordinary local directory, not a link.'
    }
    if (-not (Test-Path -LiteralPath $runtimeMarkerPath -PathType Leaf)) {
        throw 'The isolated development runtime root already exists without its ownership marker. Move it aside or recycle it after confirming no development session is using it.'
    }
    $runtimeMarkerItem = Get-Item -LiteralPath $runtimeMarkerPath -Force
    if (($runtimeMarkerItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'The isolated development runtime ownership marker must be an ordinary local file, not a link.'
    }
    $markerStream = [IO.File]::Open($runtimeMarkerPath, [IO.FileMode]::Open,
        [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $markerReader = [IO.StreamReader]::new($markerStream, [Text.UTF8Encoding]::new($false), $true)
        try { $storedMarker = $markerReader.ReadToEnd() | ConvertFrom-Json }
        finally { $markerReader.Dispose() }
    }
    finally {
        if ($null -ne $markerStream) { $markerStream.Dispose() }
    }
    if ($storedMarker.schemaVersion -ne 1 -or
        $storedMarker.purpose -ne $runtimeMarker.purpose -or
        -not [string]::Equals($storedMarker.worktreeRoot, $worktreeRoot, [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals($storedMarker.runtimeRoot, $runtimeRoot, [StringComparison]::OrdinalIgnoreCase) -or
        $storedMarker.instanceId -ne $instanceId) {
        throw 'The isolated development runtime ownership marker does not match this worktree.'
    }
}
else {
    New-Item -ItemType Directory -Path $runtimeRoot | Out-Null
    $runtimeItem = Get-Item -LiteralPath $runtimeRoot -Force
    if (($runtimeItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'The isolated development runtime root must be an ordinary local directory, not a link.'
    }
    $markerStream = [IO.File]::Open($runtimeMarkerPath, [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $markerWriter = [IO.StreamWriter]::new($markerStream, [Text.UTF8Encoding]::new($false))
        try { $markerWriter.Write($runtimeMarkerJson) }
        finally { $markerWriter.Dispose() }
    }
    finally {
        if ($null -ne $markerStream) { $markerStream.Dispose() }
    }
}

function Ensure-OwnedRuntimeDirectory {
    param([Parameter(Mandatory)][string]$Path)
    if (Test-Path -LiteralPath $Path) {
        if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
            throw "The isolated runtime path '$Path' exists but is not a directory."
        }
    }
    else {
        New-Item -ItemType Directory -Path $Path | Out-Null
    }
    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The isolated runtime path '$Path' must be an ordinary local directory, not a link."
    }
}

$dataRoot = Join-Path $runtimeRoot 'data'
$managedRoot = Join-Path $runtimeRoot 'servers'
Ensure-OwnedRuntimeDirectory $dataRoot
Ensure-OwnedRuntimeDirectory $managedRoot
$disabledSource = Join-Path $runtimeRoot 'credential-source-disabled.txt'
if (Test-Path -LiteralPath $disabledSource) {
    throw 'The disabled App credential source sentinel path must not exist.'
}

function Set-IsolatedEnvironment {
    param([Parameter(Mandatory)][Diagnostics.ProcessStartInfo]$StartInfo)
    $StartInfo.Environment['CHUNKPILOT_DATA_ROOT'] = $dataRoot
    $StartInfo.Environment['CHUNKPILOT_MANAGED_SERVERS_ROOT'] = $managedRoot
    $StartInfo.Environment['CHUNKPILOT_INSTANCE_ID'] = $instanceId
    $StartInfo.Environment.Remove('CHUNKPILOT_CURSEFORGE_KEY_FILE') | Out-Null
    $StartInfo.Environment.Remove('CHUNKPILOT_REACHABILITY_PROBE_URL') | Out-Null
}

$agentInfo = [Diagnostics.ProcessStartInfo]::new()
$agentInfo.FileName = $agentPath
$agentInfo.WorkingDirectory = Split-Path -Parent $agentPath
$agentInfo.UseShellExecute = $false
$agentInfo.CreateNoWindow = $true
Set-IsolatedEnvironment $agentInfo
$agentInfo.Environment['CHUNKPILOT_CURSEFORGE_KEY_FILE'] = $approvedSource
$agent = [Diagnostics.Process]::Start($agentInfo)
if ($null -eq $agent) { throw 'The isolated ChunkPilot Agent could not start.' }

try {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    do {
        if ($agent.HasExited) { throw 'The isolated ChunkPilot Agent exited before its local pipe became ready.' }
        $ready = Test-Path -LiteralPath "\\.\pipe\$pipeName"
        if (-not $ready) { Start-Sleep -Milliseconds 100 }
    } while (-not $ready -and [DateTimeOffset]::UtcNow -lt $deadline)
    if (-not $ready) { throw 'The isolated ChunkPilot Agent did not become ready within 30 seconds.' }

    $appInfo = [Diagnostics.ProcessStartInfo]::new()
    $appInfo.FileName = $appPath
    $appInfo.WorkingDirectory = Split-Path -Parent $appPath
    $appInfo.UseShellExecute = $false
    Set-IsolatedEnvironment $appInfo
    # An explicit nonexistent source prevents the App's fallback Agent path from consulting the
    # developer-machine default. The prestarted Agent already imported or reused the DPAPI value.
    $appInfo.Environment['CHUNKPILOT_CURSEFORGE_KEY_FILE'] = $disabledSource
    $app = [Diagnostics.Process]::Start($appInfo)
    if ($null -eq $app) { throw 'The isolated ChunkPilot App could not start.' }
}
catch {
    if (-not $agent.HasExited) { $agent.Kill($true) }
    throw
}

Write-Host 'ChunkPilot CurseForge development session started with isolated data and managed-server roots.'
