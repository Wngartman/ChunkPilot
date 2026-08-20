[CmdletBinding()]
param(
    [string]$InstallDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) '.tools\dotnet-runtime-8.0.30')
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$installFull = [IO.Path]::GetFullPath($InstallDirectory)
$toolsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot '.tools')) + [IO.Path]::DirectorySeparatorChar
if (-not $installFull.StartsWith($toolsRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to install the SBOM runtime outside the repository-local .tools directory: $installFull"
}

$version = '8.0.30'
$runtimeUrl = "https://builds.dotnet.microsoft.com/dotnet/Runtime/$version/dotnet-runtime-$version-win-x64.zip"
$expectedSha512 = '99E61C9A2D15DBB280DB98BFC3EE45DFEDA25FDB91E3D3C167789DD74328957A4F791C57AD13E8A3344DF64A27D6EF8332DD91A773072541789A1D11EE3B4439'
$archive = Join-Path $repoRoot ".tools\dotnet-runtime-$version-win-x64.zip"
$dotnet = Join-Path $installFull 'dotnet.exe'

function Assert-Runtime([string]$Path) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
        $signature.SignerCertificate.Subject -notmatch '(^|, )O=Microsoft Corporation(,|$)') {
        throw 'The repository-local .NET 8 runtime does not have the expected valid Microsoft Corporation signature.'
    }
    $runtimes = & $Path --list-runtimes | Out-String
    if ($LASTEXITCODE -ne 0 -or $runtimes -notmatch 'Microsoft\.NETCore\.App 8\.0\.30') {
        throw "The repository-local SBOM runtime is not Microsoft.NETCore.App $version."
    }
}

if (Test-Path -LiteralPath $dotnet) {
    Assert-Runtime $dotnet
    return Get-Item -LiteralPath $dotnet
}

if (-not (Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest -Uri $runtimeUrl -OutFile $archive
}
$actualSha512 = (Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash
if ($actualSha512 -ne $expectedSha512) {
    throw "The .NET $version runtime archive SHA-512 did not match the pinned Microsoft release metadata. Expected $expectedSha512; got $actualSha512."
}

New-Item -ItemType Directory -Path $installFull -Force | Out-Null
Expand-Archive -LiteralPath $archive -DestinationPath $installFull
Assert-Runtime $dotnet
Get-Item -LiteralPath $dotnet
