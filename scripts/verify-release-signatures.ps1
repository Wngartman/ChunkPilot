[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string[]]$Path,
    [switch]$RequireSigned
)

$ErrorActionPreference = 'Stop'
function Assert-ReleaseSignaturePolicy(
    [string]$Status, [bool]$HasSigner, [bool]$HasTimestamp, [bool]$RequireSigned) {
    # An intentionally unsigned release is not permission to ship a broken or untrusted signature.
    if ($Status -cnotin @('Valid', 'NotSigned') -or ($Status -ceq 'Valid' -and -not $HasSigner)) {
        throw "The release signature is invalid or untrusted; status was $Status."
    }
    if ($RequireSigned -and ($Status -cne 'Valid' -or -not $HasSigner -or -not $HasTimestamp)) {
        throw "A valid trusted and timestamped release signature was required; status was $Status."
    }
}

$results = foreach ($itemPath in $Path) {
    $full = [IO.Path]::GetFullPath($itemPath)
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { throw "Signature input is missing: $full" }
    $signature = Get-AuthenticodeSignature -LiteralPath $full
    Assert-ReleaseSignaturePolicy -Status ([string]$signature.Status) `
        -HasSigner ($null -ne $signature.SignerCertificate) `
        -HasTimestamp ($null -ne $signature.TimeStamperCertificate) -RequireSigned ([bool]$RequireSigned)
    $signed = $signature.Status -eq 'Valid' -and $null -ne $signature.SignerCertificate
    [PSCustomObject]@{
        Path = $full
        Status = [string]$signature.Status
        Signed = $signed
        Timestamped = $null -ne $signature.TimeStamperCertificate
        Subject = if ($signature.SignerCertificate) { $signature.SignerCertificate.Subject } else { $null }
    }
}

$results
