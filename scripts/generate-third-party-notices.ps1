[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\release-support\THIRD-PARTY-NOTICES.txt')
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$records = [Collections.Generic.List[object]]::new()

function Find-LicenseFile([string]$PackageRoot) {
    if (-not (Test-Path -LiteralPath $PackageRoot)) { return $null }
    Get-ChildItem -LiteralPath $PackageRoot -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^(licen[cs]e|copying|notice)(\.|$)' } |
        Sort-Object Name | Select-Object -First 1
}

# JavaScript packages whose code is included in the compiled WebUI bundle.
$webUi = Join-Path $repoRoot 'src\ChunkPilot.WebUi'
$lock = Get-Content -LiteralPath (Join-Path $webUi 'package-lock.json') -Raw | ConvertFrom-Json
foreach ($property in $lock.packages.PSObject.Properties) {
    if (-not $property.Name.StartsWith('node_modules/', [StringComparison]::Ordinal) -or
        $property.Value.dev -eq $true) { continue }
    $packageRoot = Join-Path $webUi ($property.Name -replace '/', [IO.Path]::DirectorySeparatorChar)
    $licenseFile = Find-LicenseFile $packageRoot
    $records.Add([PSCustomObject]@{
        Ecosystem = 'npm'
        Name = if ($property.Value.name) { [string]$property.Value.name } else { $property.Name.Substring(13) }
        Version = [string]$property.Value.version
        License = if ($property.Value.license) { [string]$property.Value.license } else { 'See package metadata' }
        LicenseFile = if ($licenseFile) { $licenseFile.FullName } else { $null }
    })
}

# NuGet packages that contribute to the native App, Agent, helper, Core, or Infrastructure payload.
$projects = @('ChunkPilot.App', 'ChunkPilot.Agent', 'ChunkPilot.FirewallHelper', 'ChunkPilot.Core', 'ChunkPilot.Infrastructure')
foreach ($project in $projects) {
    $assetsPath = Join-Path $repoRoot "src\$project\obj\project.assets.json"
    if (-not (Test-Path -LiteralPath $assetsPath)) {
        throw "NuGet restore metadata is missing: $assetsPath"
    }
    $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
    $packageFolders = @($assets.packageFolders.PSObject.Properties.Name)
    foreach ($library in $assets.libraries.PSObject.Properties) {
        if ($library.Value.type -ne 'package') { continue }
        $separator = $library.Name.LastIndexOf('/')
        $name = $library.Name.Substring(0, $separator)
        $version = $library.Name.Substring($separator + 1)
        $packageRoot = $null
        foreach ($folder in $packageFolders) {
            $candidate = Join-Path $folder ($library.Value.path -replace '/', [IO.Path]::DirectorySeparatorChar)
            if (Test-Path -LiteralPath $candidate) { $packageRoot = $candidate; break }
        }
        $license = 'See NuGet package metadata'
        $licenseFile = $null
        if ($packageRoot) {
            $nuspec = Get-ChildItem -LiteralPath $packageRoot -Filter '*.nuspec' -File | Select-Object -First 1
            if ($nuspec) {
                [xml]$xml = Get-Content -LiteralPath $nuspec.FullName -Raw
                $metadata = $xml.package.metadata
                if ($metadata.license) {
                    $license = [string]$metadata.license.InnerText
                    if ($metadata.license.type -eq 'file') {
                        $declared = Join-Path $packageRoot $license
                        if (Test-Path -LiteralPath $declared) { $licenseFile = Get-Item -LiteralPath $declared }
                    }
                }
            }
            if (-not $licenseFile) { $licenseFile = Find-LicenseFile $packageRoot }
        }
        $records.Add([PSCustomObject]@{
            Ecosystem = 'NuGet'
            Name = $name
            Version = $version
            License = $license
            LicenseFile = if ($licenseFile) { $licenseFile.FullName } else { $null }
        })
    }
}

$unique = @($records | Sort-Object Ecosystem, Name, Version -Unique)
$builder = [Text.StringBuilder]::new()
[void]$builder.AppendLine('CHUNKPILOT THIRD-PARTY NOTICES')
[void]$builder.AppendLine('Generated from the exact locked npm and restored NuGet dependency metadata used by this build.')
[void]$builder.AppendLine()
[void]$builder.AppendLine('PACKAGE INVENTORY')
foreach ($record in $unique) {
    [void]$builder.AppendLine("- $($record.Ecosystem): $($record.Name) $($record.Version) — $($record.License)")
}

$licenseGroups = @{}
foreach ($record in $unique | Where-Object LicenseFile) {
    $hash = (Get-FileHash -LiteralPath $record.LicenseFile -Algorithm SHA256).Hash
    if (-not $licenseGroups.ContainsKey($hash)) {
        $licenseGroups[$hash] = [PSCustomObject]@{ Packages = [Collections.Generic.List[string]]::new(); Path = $record.LicenseFile }
    }
    $licenseGroups[$hash].Packages.Add("$($record.Ecosystem):$($record.Name)@$($record.Version)")
}

[void]$builder.AppendLine()
[void]$builder.AppendLine('LICENSE TEXTS')
foreach ($group in $licenseGroups.GetEnumerator() | Sort-Object { $_.Value.Packages[0] }) {
    [void]$builder.AppendLine()
    [void]$builder.AppendLine(('=' * 78))
    [void]$builder.AppendLine(($group.Value.Packages | Sort-Object) -join ', ')
    [void]$builder.AppendLine(('=' * 78))
    [void]$builder.AppendLine((Get-Content -LiteralPath $group.Value.Path -Raw).Trim())
    [void]$builder.AppendLine()
}

$manual = Get-Content -LiteralPath (Join-Path $repoRoot 'THIRD-PARTY-NOTICES.md') -Raw
[void]$builder.AppendLine()
[void]$builder.AppendLine('PROJECT-SPECIFIC NOTICE')
[void]$builder.AppendLine($manual.Trim())

$outputFull = [IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Path (Split-Path -Parent $outputFull) -Force | Out-Null
[IO.File]::WriteAllText($outputFull, $builder.ToString(), [Text.UTF8Encoding]::new($false))
[PSCustomObject]@{ Output = $outputFull; Packages = $unique.Count; LicenseTexts = $licenseGroups.Count }
