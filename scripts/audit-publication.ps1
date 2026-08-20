[CmdletBinding()]
param(
    [string]$Revision = 'HEAD'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$auditRoot = Join-Path $repoRoot 'artifacts\publication-audit'
New-Item -ItemType Directory -Path $auditRoot -Force | Out-Null

$toolRoot = Join-Path $repoRoot '.tools\gitleaks-8.30.1'
$gitleaks = Join-Path $toolRoot 'gitleaks.exe'
if (-not (Test-Path -LiteralPath $gitleaks)) {
    New-Item -ItemType Directory -Path $toolRoot -Force | Out-Null
    $zip = Join-Path $toolRoot 'gitleaks_8.30.1_windows_x64.zip'
    Invoke-WebRequest -Uri 'https://github.com/gitleaks/gitleaks/releases/download/v8.30.1/gitleaks_8.30.1_windows_x64.zip' -OutFile $zip
    $actual = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
    $expected = 'D29144DEFF3A68AA93CED33DDDF84B7FDC26070ADD4AA0F4513094C8332AFC4E'
    if ($actual -ne $expected) { throw "Gitleaks archive checksum mismatch. Expected $expected; got $actual." }
    Expand-Archive -LiteralPath $zip -DestinationPath $toolRoot
}

$reportPath = Join-Path $auditRoot 'gitleaks.json'
& $gitleaks git --log-opts=$Revision --redact=100 --no-banner --report-format json --report-path $reportPath $repoRoot
$gitleaksExit = $LASTEXITCODE
if ($gitleaksExit -ne 0) {
    $findingCount = if ((Test-Path -LiteralPath $reportPath) -and (Get-Item -LiteralPath $reportPath).Length -gt 0) {
        @(Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json).Count
    } else { 0 }
    throw "Gitleaks rejected $Revision with $findingCount redacted finding(s)."
}

$objects = @(git -C $repoRoot rev-list --objects $Revision)
if ($LASTEXITCODE -ne 0) { throw "Could not enumerate Git objects reachable from $Revision." }
$metadata = @($objects | git -C $repoRoot cat-file '--batch-check=%(objecttype) %(objectname) %(objectsize) %(rest)')
$blobs = @($metadata | Where-Object { $_ -like 'blob *' } | ForEach-Object {
    if ($_ -match '^blob ([0-9a-f]+) ([0-9]+) ?(.*)$') {
        [PSCustomObject]@{ Oid = $matches[1]; Bytes = [int64]$matches[2]; Path = $matches[3] }
    }
})

$prohibited = @($blobs | Where-Object {
    $_.Path -match '(^|/)(node_modules|bin|obj|artifacts|worlds?|backups?|logs?|dumps?|cache)(/|$)' -or
    $_.Path -match '\.(jar|mrpack|zip|7z|rar|db|sqlite|sqlite3|dmp|pfx|p12|pem|key|exe|dll|msi|msix|nupkg)$'
})
if ($prohibited.Count -ne 0) {
    throw "Prohibited generated, user-data, secret, or binary paths are reachable from ${Revision}: $($prohibited.Path -join ', ')"
}

$privateMarkers = @(
    ('Sta' + 'Tech2-Server'),
    ('ChunkPilot' + '-Local'),
    ('C:\Users\' + 'wngar')
)
$grepArguments = @('grep', '-I', '-l', '-F')
foreach ($marker in $privateMarkers) { $grepArguments += @('-e', $marker) }
$grepArguments += @($Revision, '--')
$trackedText = @(& git -C $repoRoot @grepArguments 2>$null)
if ($LASTEXITCODE -gt 1) { throw 'Private-marker Git scan failed.' }
if ($trackedText.Count -ne 0) { throw "Private or user-specific marker remains in the publication tree: $($trackedText -join ', ')" }

$large = @($blobs | Where-Object Bytes -ge 1MB | Sort-Object Bytes -Descending |
    Select-Object Bytes, Oid, Path)
$ignored = @(git -C $repoRoot status --ignored --short)
$result = [PSCustomObject]@{
    Revision = (& git -C $repoRoot rev-parse $Revision).Trim()
    GitleaksVersion = (& $gitleaks version).Trim()
    GitleaksFindings = 0
    ReachableBlobCount = $blobs.Count
    ReachableProhibitedPaths = 0
    LargeBlobs = $large
    IgnoredStatus = $ignored
    SourceLicensePresent = [bool](Get-ChildItem -LiteralPath $repoRoot -File | Where-Object Name -match '^LICENSE($|\.)')
}
[IO.File]::WriteAllText((Join-Path $auditRoot 'publication-audit.json'), ($result | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
$result
