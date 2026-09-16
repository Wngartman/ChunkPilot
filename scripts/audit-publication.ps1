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
if ($gitleaksExit -notin 0, 1) {
    throw "Gitleaks could not audit $Revision (exit code $gitleaksExit)."
}

$findings = if ((Test-Path -LiteralPath $reportPath) -and (Get-Item -LiteralPath $reportPath).Length -gt 0) {
    @(Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json)
} else { @() }
$knownFalsePositiveFingerprints = @(
    # An immutable historical test fixture: a generated 64-hex certification token, never a provider key.
    'e6f0acb36ffb3e2ee6874d5debc87f0078187913:tests/ChunkPilot.UnitTests/CertificationUpdateFaultInjectorTests.cs:generic-api-key:8',
    # An immutable CURRENT-GATE line containing the prior live-API Git commit, not credential material.
    'bb43bea6b0af3d96b1f5f82acc0dbdacc72e73e7:docs/CURRENT-GATE.md:generic-api-key:11',
    # Immutable fake-router tests use the literal 0123456789ABCDEF01234567 as a 12-byte PCP nonce.
    # These exact test lines are not application credentials or live mapping authority.
    '3c9b9c8f49a97c41cfd21e9927e9eb86e1312f86:tests/ChunkPilot.IntegrationTests/RouterMappingRealRouterSequenceIntegrationTests.cs:generic-api-key:569',
    '3c9b9c8f49a97c41cfd21e9927e9eb86e1312f86:tests/ChunkPilot.UnitTests/RouterMapping/PcpMappingProviderTests.cs:generic-api-key:278'
)
$knownFalsePositives = @($findings | Where-Object Fingerprint -In $knownFalsePositiveFingerprints)
$unexpectedFindings = @($findings | Where-Object Fingerprint -NotIn $knownFalsePositiveFingerprints)
if ($unexpectedFindings.Count -ne 0) {
    throw "Gitleaks rejected $Revision with $($unexpectedFindings.Count) unexpected redacted finding(s)."
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
    $_.Path -match '(^|/)(node_modules|bin|obj|artifacts|worlds?|backups?|logs?|dumps?|cache|\.secrets?|secrets?)(/|$)' -or
    $_.Path -match '(^|/)(secrets\.dat|chunkpilot\.db(?:-wal|-shm)?)(/|$)' -or
    $_.Path -match '(^|/)(WebView2|CurrentProfile)(/|$)' -or
    $_.Path -match '(^|/)(\.wrangler(/|$)|\.dev\.vars(?:\.[^/]*)?$)' -or
    $_.Path -match '(^|/)curseforge-api-key[^/]*\.txt$' -or
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
$ignored = @(git -C $repoRoot status --ignored=matching --short)
$result = [PSCustomObject]@{
    Revision = (& git -C $repoRoot rev-parse $Revision).Trim()
    GitleaksVersion = (& $gitleaks version).Trim()
    GitleaksFindings = $unexpectedFindings.Count
    GitleaksKnownFalsePositives = $knownFalsePositives.Count
    ReachableBlobCount = $blobs.Count
    ReachableProhibitedPaths = 0
    LargeBlobs = $large
    IgnoredStatus = $ignored
    SourceLicensePresent = [bool](Get-ChildItem -LiteralPath $repoRoot -File | Where-Object Name -match '^LICENSE($|\.)')
}
[IO.File]::WriteAllText((Join-Path $auditRoot 'publication-audit.json'), ($result | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
$result
