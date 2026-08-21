[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string[]]$Path,
    [string]$CertificateThumbprint = $env:CHUNKPILOT_SIGNING_CERT_THUMBPRINT,
    [string]$TimestampUrl = 'https://timestamp.digicert.com',
    [switch]$Required
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    if ($Required) { throw 'A trusted Authenticode signing identity is required but was not configured.' }
    Write-Host 'Authenticode signing skipped: no trusted ChunkPilot signing identity is configured.'
    return
}

$thumbprint = $CertificateThumbprint.Replace(' ', '').ToUpperInvariant()
if ($thumbprint -notmatch '^[0-9A-F]{40,64}$') { throw 'The signing certificate thumbprint is invalid.' }
$certificate = Get-ChildItem -LiteralPath Cert:\CurrentUser\My |
    Where-Object { $_.Thumbprint -eq $thumbprint -and $_.HasPrivateKey } |
    Select-Object -First 1
if (-not $certificate) { throw 'The configured trusted signing certificate is not available with a private key.' }

$signTool = if ($env:SIGNTOOL_PATH) { $env:SIGNTOOL_PATH } else {
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($command) { $command.Source } else {
        Get-ChildItem -LiteralPath "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Filter signtool.exe -File -Recurse `
            -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
    }
}
if (-not $signTool -or -not (Test-Path -LiteralPath $signTool)) { throw 'signtool.exe was not found.' }

foreach ($itemPath in $Path) {
    $full = [IO.Path]::GetFullPath($itemPath)
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { throw "Signing input is missing: $full" }
    & $signTool sign /sha1 $thumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 $full
    if ($LASTEXITCODE -ne 0) { throw "Authenticode signing failed for $full." }
    $signature = Get-AuthenticodeSignature -LiteralPath $full
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $thumbprint -or
        -not $signature.TimeStamperCertificate) {
        throw "Signature verification failed for $full."
    }
    Write-Host "Signed and timestamped: $full"
}
