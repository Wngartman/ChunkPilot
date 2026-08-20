[CmdletBinding()]
param(
    [string]$Destination = (Join-Path (Split-Path -Parent $PSScriptRoot) 'installer\prerequisites\MicrosoftEdgeWebview2Setup.exe')
)

$ErrorActionPreference = 'Stop'
$downloadUrl = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703'
$expectedSha256 = 'BE695EB3732A94E181F008AB5CF6EE650F8644676E87F9E02B6AB0D02F2EA08E'
$destinationFull = [IO.Path]::GetFullPath($Destination)
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$allowedRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'installer\prerequisites')) + [IO.Path]::DirectorySeparatorChar
if (-not ($destinationFull + [IO.Path]::DirectorySeparatorChar).StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase) -and
    -not $destinationFull.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to place the WebView2 bootstrapper outside installer/prerequisites: $destinationFull"
}

function Assert-OfficialBootstrapper([string]$Path) {
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    if ($actual -ne $expectedSha256) {
        throw "The WebView2 bootstrapper SHA-256 did not match the pinned release input. Expected $expectedSha256; got $actual."
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
        $signature.SignerCertificate.Subject -notmatch '(^|, )O=Microsoft Corporation(,|$)') {
        throw "The WebView2 bootstrapper does not have a valid Microsoft Corporation Authenticode signature."
    }
}

if (Test-Path -LiteralPath $destinationFull) {
    Assert-OfficialBootstrapper $destinationFull
    return Get-Item -LiteralPath $destinationFull
}

$parent = Split-Path -Parent $destinationFull
New-Item -ItemType Directory -Path $parent -Force | Out-Null
$temporary = $destinationFull + '.download'
try {
    Invoke-WebRequest -Uri $downloadUrl -OutFile $temporary
    Assert-OfficialBootstrapper $temporary
    Move-Item -LiteralPath $temporary -Destination $destinationFull
}
finally {
    if (Test-Path -LiteralPath $temporary) {
        Remove-Item -LiteralPath $temporary -Force
    }
}

Get-Item -LiteralPath $destinationFull
