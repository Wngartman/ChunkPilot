[CmdletBinding()]
param(
    [string]$InstallDirectory
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if ([string]::IsNullOrWhiteSpace($InstallDirectory)) {
    $InstallDirectory = Join-Path $repoRoot '.tools\InnoSetup'
}
$installFull = [IO.Path]::GetFullPath($InstallDirectory)
$toolsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot '.tools')) + [IO.Path]::DirectorySeparatorChar
if (-not $installFull.StartsWith($toolsRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to install build tooling outside the repository-local .tools directory: $installFull"
}

$compiler = Join-Path $installFull 'ISCC.exe'
if (Test-Path -LiteralPath $compiler) {
    return Get-Item -LiteralPath $compiler
}

$version = '7.0.2'
$installerUrl = 'https://github.com/jrsoftware/issrc/releases/download/is-7_0_2/innosetup-7.0.2-x64.exe'
$expectedSha256 = '5AD54CA3DEF786F8F4212552E54CC6D8D61329E2D24A1CFEE0571D42C2684FF1'
$downloadDirectory = Join-Path $repoRoot '.tools\inno-setup-installer'
$installerPath = Join-Path $downloadDirectory "innosetup-$version-x64.exe"
New-Item -ItemType Directory -Path $downloadDirectory -Force | Out-Null

if (-not (Test-Path -LiteralPath $installerPath)) {
    Invoke-WebRequest -Uri $installerUrl -OutFile $installerPath
}
$actualSha256 = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash
if ($actualSha256 -ne $expectedSha256) {
    throw "The Inno Setup installer SHA-256 did not match the pinned official release. Expected $expectedSha256; got $actualSha256."
}
$signature = Get-AuthenticodeSignature -LiteralPath $installerPath
if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
    $signature.SignerCertificate.Subject -notmatch '^CN=Pyrsys B\.V\.') {
    throw 'The Inno Setup installer does not have the expected valid Pyrsys B.V. Authenticode signature.'
}

$arguments = @(
    '/CURRENTUSER', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
    "/DIR=$installFull"
)
$process = Start-Process -FilePath $installerPath -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $compiler)) {
    throw "Inno Setup $version installation failed with exit code $($process.ExitCode)."
}

$previousErrorActionPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
$help = & $compiler /? 2>&1 | Out-String
$helpExitCode = $LASTEXITCODE
$ErrorActionPreference = $previousErrorActionPreference
if ($helpExitCode -notin @(0, 1)) {
    throw "Inno Setup compiler help failed with exit code $helpExitCode."
}
if ($help -notmatch 'Inno Setup 7 Command-Line Compiler') {
    throw 'The repository-local Inno Setup compiler did not identify itself as Inno Setup 7.'
}
# ISCC deliberately returns 1 for its help banner. PowerShell otherwise carries
# that native exit code out of this successful script invocation on a fresh CI
# runner, even after a subsequent cmdlet succeeds.
$global:LASTEXITCODE = 0
Get-Item -LiteralPath $compiler
