[CmdletBinding()]
param([switch]$SkipTests)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$webUi = Join-Path $repoRoot 'src\ChunkPilot.WebUi'
$lockPath = Join-Path $webUi 'package-lock.json'
$marker = Join-Path $webUi 'node_modules\.chunkpilot-lock-sha256'
$expected = (Get-FileHash -LiteralPath $lockPath -Algorithm SHA256).Hash.ToLowerInvariant()
$actual = if (Test-Path -LiteralPath $marker) { (Get-Content -LiteralPath $marker -Raw).Trim().ToLowerInvariant() } else { '' }
$npm = Join-Path (Split-Path -Parent (Get-Command node -ErrorAction Stop).Source) 'npm.cmd'

Push-Location $webUi
try {
    if ($actual -ne $expected) {
        & $npm ci --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { throw "WebUI dependency restore failed with exit code $LASTEXITCODE." }
        [IO.File]::WriteAllText($marker, "$expected`n", [Text.UTF8Encoding]::new($false))
    }
    & $npm run typecheck
    if ($LASTEXITCODE -ne 0) { throw 'WebUI typecheck failed.' }
    & $npm run lint
    if ($LASTEXITCODE -ne 0) { throw 'WebUI lint failed.' }
    if (-not $SkipTests) {
        & $npm test
        if ($LASTEXITCODE -ne 0) { throw 'WebUI tests failed.' }
    }
    & $npm run build
    if ($LASTEXITCODE -ne 0) { throw 'WebUI production build failed.' }
} finally {
    Pop-Location
}
