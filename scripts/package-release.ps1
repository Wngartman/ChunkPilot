[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^v1\.3\.0-alpha\.[1-9][0-9]*$')]
    [string]$ReleaseTag
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactsRoot = Join-Path $repoRoot 'artifacts'
$selfContained = Join-Path $artifactsRoot 'self-contained-win-x64'
$portableSource = Join-Path $artifactsRoot 'portable-test'
$releaseDirectory = Join-Path $artifactsRoot ("release\$ReleaseTag")
$sbomWork = Join-Path $artifactsRoot ("sbom-work\$ReleaseTag")
$safeArtifacts = [IO.Path]::GetFullPath($artifactsRoot) + [IO.Path]::DirectorySeparatorChar

function Reset-ArtifactsDirectory([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($safeArtifacts, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to reset a path outside artifacts: $full"
    }
    if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
    New-Item -ItemType Directory -Path $full -Force | Out-Null
}

function New-DeterministicZip([string]$Source, [string]$Destination, [DateTimeOffset]$Timestamp) {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if (Test-Path -LiteralPath $Destination) { Remove-Item -LiteralPath $Destination -Force }
    $archive = [IO.Compression.ZipFile]::Open($Destination, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $sourceFull = [IO.Path]::GetFullPath($Source).TrimEnd('\') + '\'
        foreach ($file in Get-ChildItem -LiteralPath $Source -File -Recurse | Sort-Object FullName) {
            $relative = $file.FullName.Substring($sourceFull.Length).Replace('\', '/')
            $entry = $archive.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = $Timestamp
            $input = $file.OpenRead()
            $output = $entry.Open()
            try { $input.CopyTo($output) } finally { $output.Dispose(); $input.Dispose() }
        }
    }
    finally { $archive.Dispose() }
}

$required = @(
    (Join-Path $selfContained 'ChunkPilot.exe'),
    (Join-Path $selfContained 'Agent\ChunkPilot.Agent.exe'),
    (Join-Path $portableSource 'README.txt'),
    (Join-Path $repoRoot 'release\RELEASE_NOTES.template.md')
)
foreach ($path in $required) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Required release input is missing: $path" }
}

$installerName = "ChunkPilot-Setup-$ReleaseTag.exe"
$portableName = "ChunkPilot-Portable-$ReleaseTag-win-x64.zip"
$installerSource = Join-Path $repoRoot "installer\output\$installerName"
if (-not (Test-Path -LiteralPath $installerSource)) { throw "Installer input is missing: $installerSource" }

Reset-ArtifactsDirectory $releaseDirectory
Reset-ArtifactsDirectory $sbomWork
$installer = Join-Path $releaseDirectory $installerName
$portable = Join-Path $releaseDirectory $portableName
$notices = Join-Path $releaseDirectory 'THIRD-PARTY-NOTICES.txt'
$sbom = Join-Path $releaseDirectory 'ChunkPilot-SBOM.spdx.json'
$checksums = Join-Path $releaseDirectory 'SHA256SUMS.txt'
$releaseNotes = Join-Path $releaseDirectory 'RELEASE_NOTES.md'

Copy-Item -LiteralPath $installerSource -Destination $installer
Copy-Item -LiteralPath (Join-Path $artifactsRoot 'release-support\THIRD-PARTY-NOTICES.txt') -Destination $notices
$commit = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') { throw 'Could not resolve the release source commit.' }
$commitTime = [DateTimeOffset]::Parse((& git -C $repoRoot show -s --format=%cI HEAD).Trim())
if ($commitTime.Year -lt 1980) { $commitTime = [DateTimeOffset]'1980-01-01T00:00:00Z' }
New-DeterministicZip $portableSource $portable $commitTime

$sbomToolDirectory = Join-Path $repoRoot '.tools\sbom-tool'
$sbomTool = Join-Path $sbomToolDirectory 'sbom-tool.exe'
if (-not (Test-Path -LiteralPath $sbomTool)) {
    & dotnet tool install Microsoft.Sbom.DotNetTool --tool-path $sbomToolDirectory --version 4.1.5
    if ($LASTEXITCODE -ne 0) { throw 'Repository-local Microsoft SBOM tool installation failed.' }
}
$sbomRuntime = (& (Join-Path $repoRoot 'scripts\install-sbom-runtime.ps1')).FullName
$sbomAssembly = Get-ChildItem -LiteralPath $sbomToolDirectory -Filter 'Microsoft.Sbom.DotNetTool.dll' -File -Recurse |
    Select-Object -First 1
if (-not $sbomAssembly) { throw 'The repository-local Microsoft SBOM tool assembly was not found.' }

& $sbomRuntime $sbomAssembly.FullName Generate -b $selfContained -bc (Join-Path $repoRoot 'src') -m $sbomWork -pn ChunkPilot `
    -pv $ReleaseTag.TrimStart('v') -ps ChunkPilot -nsb 'https://github.com/Wngartman/ChunkPilot' `
    -nsu "$ReleaseTag-$commit" -D true -pm true -li false -t (Join-Path $sbomWork 'generation-telemetry.json') -V Warning
if ($LASTEXITCODE -ne 0) { throw "SBOM generation failed with exit code $LASTEXITCODE." }
$generated = Get-ChildItem -LiteralPath $sbomWork -Filter 'manifest.spdx.json' -File -Recurse | Select-Object -First 1
if (-not $generated) { throw 'Microsoft SBOM Tool did not produce manifest.spdx.json.' }
Copy-Item -LiteralPath $generated.FullName -Destination $sbom
$sbomDocument = Get-Content -LiteralPath $sbom -Raw | ConvertFrom-Json
if ($sbomDocument.spdxVersion -ne 'SPDX-2.2' -or @($sbomDocument.files).Count -eq 0 -or
    @($sbomDocument.packages).Count -le 1) {
    throw 'Generated SPDX 2.2 SBOM does not contain both shipped files and detected dependency packages.'
}
$validationPath = Join-Path $sbomWork 'validation.json'
& $sbomRuntime $sbomAssembly.FullName Validate -b $selfContained -m (Join-Path $sbomWork '_manifest') `
    -o $validationPath -mi 'SPDX:2.2' -n `
    -t (Join-Path $sbomWork 'validation-telemetry.json') -V Warning
if ($LASTEXITCODE -ne 0) { throw "SBOM validation failed with exit code $LASTEXITCODE." }
if (-not (Test-Path -LiteralPath $validationPath)) { throw 'SBOM validation did not produce its result document.' }
$validation = Get-Content -LiteralPath $validationPath -Raw | ConvertFrom-Json
if ($validation.Result -ne 'Success' -or $validation.ValidationErrors.Count -ne 0 -or
    $validation.Summary.ValidationTelemetery.FilesFailedCount -ne 0 -or
    $validation.Summary.ValidationTelemetery.TotalPackagesInManifest -le 1) {
    throw 'SBOM validator did not report a complete dependency-bearing success.'
}

$hashTargets = @($installer, $portable, $sbom, $notices)
$hashLines = foreach ($path in $hashTargets) {
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($path))"
}
[IO.File]::WriteAllLines($checksums, $hashLines, [Text.UTF8Encoding]::new($false))

$productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $selfContained 'ChunkPilot.exe')).ProductVersion
if (-not $productVersion.EndsWith($commit, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Packaged ProductVersion $productVersion is not bound to release commit $commit."
}
$notes = Get-Content -LiteralPath (Join-Path $repoRoot 'release\RELEASE_NOTES.template.md') -Raw
$hotfixNotes = Get-Content -LiteralPath (Join-Path $repoRoot 'release\HOTFIX_NOTES.md') -Raw
if ([string]::IsNullOrWhiteSpace($hotfixNotes)) { throw 'release/HOTFIX_NOTES.md is empty.' }
if ($ReleaseTag -notmatch '^v(?<version>\d+\.\d+\.\d+)-alpha\.(?<alpha>[1-9][0-9]*)$') {
    throw "Could not derive release metadata from $ReleaseTag."
}
$releaseTitle = "ChunkPilot $($matches.version) Alpha $($matches.alpha)"
$notes = $notes.Replace('{{RELEASE_COMMIT}}', $commit)
$notes = $notes.Replace('{{PRODUCT_VERSION}}', $productVersion)
$notes = $notes.Replace('{{BUILD_TIME_UTC}}', [DateTimeOffset]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ'))
$notes = $notes.Replace('{{SHA256_SUMS}}', ($hashLines -join "`n"))
$notes = $notes.Replace('{{RELEASE_TITLE}}', $releaseTitle)
$notes = $notes.Replace('{{RELEASE_TAG}}', $ReleaseTag)
$notes = $notes.Replace('{{INSTALLER_NAME}}', $installerName)
$notes = $notes.Replace('{{HOTFIX_NOTES}}', $hotfixNotes.Trim())
if ($notes -match '\{\{[A-Z0-9_]+\}\}') { throw "Release notes contain an unresolved placeholder: $($matches[0])" }
[IO.File]::WriteAllText($releaseNotes, $notes, [Text.UTF8Encoding]::new($false))

$assetReport = foreach ($path in @($installer, $portable, $checksums, $sbom, $notices, $releaseNotes)) {
    $item = Get-Item -LiteralPath $path
    [PSCustomObject]@{
        Name = $item.Name
        Bytes = $item.Length
        Sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
$manifest = [PSCustomObject]@{
    Tag = $ReleaseTag
    Commit = $commit
    ProductVersion = $productVersion
    Assets = $assetReport
}
[IO.File]::WriteAllText((Join-Path $releaseDirectory 'release-manifest.json'), ($manifest | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
$manifest
