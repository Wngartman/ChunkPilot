# Public deployment configuration only. Never accept credentials or arbitrary MSBuild properties.
function Get-CertificationBuildConfiguration([string]$CurseForgeServiceEndpoint = '') {
    if ($CurseForgeServiceEndpoint -cnotmatch '\A(?:|https://[a-z0-9-]+(?:\.[a-z0-9-]+)+(?::443)?/?)\z') {
        throw 'CurseForgeServiceEndpoint must be an empty value or a public HTTPS origin without credentials, paths, or query parameters.'
    }
    $canonicalEndpoint = ''
    if ($CurseForgeServiceEndpoint.Length -gt 0) {
        $serviceUri = [Uri]$CurseForgeServiceEndpoint
        if ($serviceUri.IsLoopback -or $serviceUri.HostNameType -ne [UriHostNameType]::Dns -or
            $serviceUri.IdnHost.EndsWith('.localhost', [StringComparison]::OrdinalIgnoreCase) -or
            $serviceUri.IdnHost.EndsWith('.local', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'CurseForgeServiceEndpoint must be a public HTTPS DNS origin, not a local service.'
        }
        $canonicalEndpoint = $serviceUri.AbsoluteUri.TrimEnd('/') + '/'
    }
    $configurationText = "ChunkPilot development build configuration v1`nCurseForgeServiceEndpoint=$canonicalEndpoint`n"
    return [pscustomobject]@{
        curseForgeServiceEndpoint = $canonicalEndpoint
        buildConfigurationSha256 = [Convert]::ToHexString(
            [Security.Cryptography.SHA256]::HashData(
                [Text.Encoding]::UTF8.GetBytes($configurationText))).ToLowerInvariant()
    }
}
