[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PortableZip
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$zip = [IO.Path]::GetFullPath($PortableZip)
if (-not (Test-Path -LiteralPath $zip)) { throw "Portable ZIP was not found: $zip" }
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("ChunkPilot-portable-release-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    Expand-Archive -LiteralPath $zip -DestinationPath $testRoot
    foreach ($required in @('ChunkPilot.exe', 'Agent\ChunkPilot.Agent.exe', 'README.txt', 'THIRD-PARTY-NOTICES.txt', 'WebUi\index.html')) {
        if (-not (Test-Path -LiteralPath (Join-Path $testRoot $required))) { throw "Portable package is missing $required." }
    }
    $prohibited = @(Get-ChildItem -LiteralPath $testRoot -File -Recurse | Where-Object {
        $_.Extension -in @('.pdb', '.cs', '.csproj', '.jar', '.mrpack') -or
        $_.FullName -match '[\\/]node_modules[\\/]'
    })
    if ($prohibited.Count -ne 0) { throw "Portable package contains prohibited development/server files: $($prohibited.Name -join ', ')" }

    $agent = & (Join-Path $repoRoot 'scripts\smoke-portable.ps1') -PortableRoot $testRoot
    if ($agent.AgentExitCode -ne 0 -or $agent.SelfTestErrors -ne 0 -or -not $agent.DatabaseCreated) {
        throw 'Portable Agent self-test failed.'
    }
    $webUi = (& (Join-Path $repoRoot 'scripts\test-packaged-ui-close.ps1') -PortableRoot $testRoot) | ConvertFrom-Json
    if (-not $webUi.OverallPass) { throw "Portable WebUI close smoke failed: $($webUi.Failures -join '; ')" }
    [PSCustomObject]@{ Zip = $zip; Agent = $agent; WebUi = $webUi; Pass = $true }
}
finally {
    $full = [IO.Path]::GetFullPath($testRoot)
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ($full.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($full).StartsWith('ChunkPilot-portable-release-', [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $full)) {
        Remove-Item -LiteralPath $full -Recurse -Force
    }
}
