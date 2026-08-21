[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string[]]$Path,
    [switch]$RequireSigned
)

$ErrorActionPreference = 'Stop'
$results = foreach ($itemPath in $Path) {
    $full = [IO.Path]::GetFullPath($itemPath)
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { throw "Signature input is missing: $full" }
    $signature = Get-AuthenticodeSignature -LiteralPath $full
    $signed = $signature.Status -eq 'Valid' -and $null -ne $signature.SignerCertificate
    if ($RequireSigned -and (-not $signed -or -not $signature.TimeStamperCertificate)) {
        throw "A valid trusted and timestamped signature was required for $full; status was $($signature.Status)."
    }
    [PSCustomObject]@{
        Path = $full
        Status = [string]$signature.Status
        Signed = $signed
        Timestamped = $null -ne $signature.TimeStamperCertificate
        Subject = if ($signature.SignerCertificate) { $signature.SignerCertificate.Subject } else { $null }
    }
}

$results
