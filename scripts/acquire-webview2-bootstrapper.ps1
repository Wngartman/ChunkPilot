[CmdletBinding()]
param(
    [string]$Destination = (Join-Path (Split-Path -Parent $PSScriptRoot) 'installer\prerequisites\MicrosoftEdgeWebview2Setup.exe')
)

$ErrorActionPreference = 'Stop'
$downloadUrl = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703'
$destinationFull = [IO.Path]::GetFullPath($Destination)
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$allowedRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'installer\prerequisites')) + [IO.Path]::DirectorySeparatorChar
if (-not ($destinationFull + [IO.Path]::DirectorySeparatorChar).StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase) -and
    -not $destinationFull.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to place the WebView2 bootstrapper outside installer/prerequisites: $destinationFull"
}

function Assert-OfficialBootstrapper([string]$Path) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
        $signature.SignerCertificate.Subject -notmatch '(^|, )O=Microsoft Corporation(,|$)') {
        throw "The WebView2 bootstrapper does not have a valid Microsoft Corporation Authenticode signature."
    }

    $item = Get-Item -LiteralPath $Path
    if ($item.VersionInfo.ProductName -ne 'Microsoft Edge Update' -or
        $item.VersionInfo.OriginalFilename -ne 'MicrosoftEdgeUpdateSetup.exe') {
        throw "The Microsoft-signed download is not the expected WebView2 Evergreen bootstrapper product."
    }

    [pscustomobject]@{
        Path = $item.FullName
        SHA256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
        SignatureStatus = $signature.Status.ToString()
        SignerSubject = $signature.SignerCertificate.Subject
        ProductName = $item.VersionInfo.ProductName
        FileVersion = $item.VersionInfo.FileVersion
    }
}

if (Test-Path -LiteralPath $destinationFull) {
    Assert-OfficialBootstrapper $destinationFull
    return
}

$parent = Split-Path -Parent $destinationFull
New-Item -ItemType Directory -Path $parent -Force | Out-Null
$temporary = $destinationFull + '.download'
try {
    Invoke-WebRequest -Uri $downloadUrl -OutFile $temporary
    $null = Assert-OfficialBootstrapper $temporary
    Move-Item -LiteralPath $temporary -Destination $destinationFull
}
finally {
    if (Test-Path -LiteralPath $temporary) {
        Remove-Item -LiteralPath $temporary -Force
    }
}

Assert-OfficialBootstrapper $destinationFull
