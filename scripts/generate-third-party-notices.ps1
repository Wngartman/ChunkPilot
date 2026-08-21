[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\release-support\THIRD-PARTY-NOTICES.txt')
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$records = [Collections.Generic.List[object]]::new()
$strictUtf8 = [Text.UTF8Encoding]::new($false, $true)

function Find-LicenseFile([string]$PackageRoot) {
    if (-not (Test-Path -LiteralPath $PackageRoot)) { return $null }
    Get-ChildItem -LiteralPath $PackageRoot -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^(licen[cs]e|copying|notice)(\.|$)' } |
        Sort-Object Name | Select-Object -First 1
}

# JavaScript packages whose code is included in the compiled WebUI bundle.
$webUi = Join-Path $repoRoot 'src\ChunkPilot.WebUi'
$lockJson = [IO.File]::ReadAllText((Join-Path $webUi 'package-lock.json'), $strictUtf8)
# Windows PowerShell 5.1 rejects the empty-string root-package property emitted by npm lockfile v3.
# Give only that structural key a temporary parse name; it is excluded from the node_modules inventory.
$emptyRootPattern = '(?m)^    "": \{$'
if ([regex]::Matches($lockJson, $emptyRootPattern).Count -ne 1) {
    throw 'package-lock.json did not contain exactly one npm root-package entry.'
}
$lock = [regex]::Replace($lockJson, $emptyRootPattern, '    "__chunkpilot_root__": {') | ConvertFrom-Json
foreach ($entry in $lock.packages.PSObject.Properties) {
    $packagePath = [string]$entry.Name
    $metadata = $entry.Value
    if (-not $packagePath.StartsWith('node_modules/', [StringComparison]::Ordinal) -or
        $metadata.dev -eq $true) { continue }
    $packageRoot = Join-Path $webUi ($packagePath -replace '/', [IO.Path]::DirectorySeparatorChar)
    $licenseFile = Find-LicenseFile $packageRoot
    $records.Add([PSCustomObject]@{
        Ecosystem = 'npm'
        Name = if ($metadata.name) { [string]$metadata.name } else { $packagePath.Substring(13) }
        Version = [string]$metadata.version
        License = if ($metadata.license) { [string]$metadata.license } else { 'See package metadata' }
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
    $assets = [IO.File]::ReadAllText($assetsPath, $strictUtf8) | ConvertFrom-Json
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
                [xml]$xml = [IO.File]::ReadAllText($nuspec.FullName, $strictUtf8)
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

$recordsByIdentity = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
foreach ($record in $records) {
    $identity = "$($record.Ecosystem)`0$($record.Name)`0$($record.Version)"
    if (-not $recordsByIdentity.ContainsKey($identity)) {
        $recordsByIdentity.Add($identity, $record)
    }
}
[string[]]$recordIdentities = @($recordsByIdentity.Keys)
[Array]::Sort($recordIdentities, [StringComparer]::Ordinal)
$unique = @($recordIdentities | ForEach-Object { $recordsByIdentity[$_] })
$builder = [Text.StringBuilder]::new()
[void]$builder.AppendLine('CHUNKPILOT THIRD-PARTY NOTICES')
[void]$builder.AppendLine('Generated from the exact locked npm and restored NuGet dependency metadata used by this build.')
[void]$builder.AppendLine()
[void]$builder.AppendLine('PACKAGE INVENTORY')
foreach ($record in $unique) {
    [void]$builder.AppendLine("- $($record.Ecosystem): $($record.Name) $($record.Version) - $($record.License)")
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
[string[]]$licenseHashes = @($licenseGroups.Keys)
[Array]::Sort($licenseHashes, [StringComparer]::Ordinal)
foreach ($hash in $licenseHashes) {
    $group = $licenseGroups[$hash]
    [string[]]$groupPackages = @($group.Packages)
    [Array]::Sort($groupPackages, [StringComparer]::Ordinal)
    [void]$builder.AppendLine()
    [void]$builder.AppendLine(('=' * 78))
    [void]$builder.AppendLine($groupPackages -join ', ')
    [void]$builder.AppendLine(('=' * 78))
    [void]$builder.AppendLine(([IO.File]::ReadAllText($group.Path, $strictUtf8)).Trim())
    [void]$builder.AppendLine()
}

$manual = [IO.File]::ReadAllText((Join-Path $repoRoot 'legal\THIRD-PARTY-NOTICES.md'), $strictUtf8)
[void]$builder.AppendLine()
[void]$builder.AppendLine('PROJECT-SPECIFIC NOTICE')
[void]$builder.AppendLine($manual.Trim())

$outputFull = [IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Path (Split-Path -Parent $outputFull) -Force | Out-Null
[IO.File]::WriteAllText($outputFull, $builder.ToString(), [Text.UTF8Encoding]::new($false))
[PSCustomObject]@{ Output = $outputFull; Packages = $unique.Count; LicenseTexts = $licenseGroups.Count }
