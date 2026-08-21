[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^v?1\.3\.0-alpha\.[1-9][0-9]*$')]
    [string]$Version,

    [ValidatePattern('^$|^v?1\.3\.0-alpha\.[1-9][0-9]*$')]
    [string]$Supersedes = '',

    [string]$Repository = 'Wngartman/ChunkPilot'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$tag = if ($Version.StartsWith('v', [StringComparison]::Ordinal)) { $Version } else { "v$Version" }
$supersedesTag = if (-not $Supersedes) { '' }
    elseif ($Supersedes.StartsWith('v', [StringComparison]::Ordinal)) { $Supersedes }
    else { "v$Supersedes" }

function Invoke-Git([Parameter(ValueFromRemainingArguments)][string[]]$Arguments) {
    $output = @(& git -C $repoRoot @Arguments)
    if ($LASTEXITCODE -ne 0) { throw "git $($Arguments -join ' ') failed." }
    return $output
}

function Assert-GitHubReleaseUnused([string]$ReleaseTag) {
    $result = @(& gh api "repos/$Repository/releases/tags/$ReleaseTag" 2>&1)
    $exitCode = $LASTEXITCODE
    if ($exitCode -eq 0) { throw "$ReleaseTag already has a GitHub release." }
    if (($result -join "`n") -notmatch '\(HTTP 404\)') {
        throw "Could not prove that $ReleaseTag is unused: $($result -join ' ')"
    }
    $global:LASTEXITCODE = 0
}

if ($tag -eq $supersedesTag) { throw 'A hotfix cannot supersede itself.' }
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'GitHub CLI (gh) is required.' }

Push-Location $repoRoot
try {
    & gh auth status
    if ($LASTEXITCODE -ne 0) { throw 'GitHub CLI is not authenticated.' }

    $changed = @(Invoke-Git status --porcelain)
    if ($changed.Count -ne 0) { throw 'The release worktree must be clean.' }
    $branch = (Invoke-Git branch --show-current)[0].Trim()
    if ($branch -ne 'main') { throw "Run hotfix publication from public main, not $branch." }

    Invoke-Git fetch origin main --tags | Out-Null
    $head = (Invoke-Git rev-parse HEAD)[0].Trim()
    $remoteMainRows = @(Invoke-Git ls-remote origin refs/heads/main)
    if ($remoteMainRows.Count -ne 1) { throw 'Could not resolve the exact public main commit.' }
    $remoteMain = ($remoteMainRows[0] -split '\s+')[0]
    if ($head -ne $remoteMain) {
        throw "Local main $head is not exact public main $remoteMain. Pull or fast-forward before release."
    }

    $existingTag = @(Invoke-Git ls-remote --tags origin "refs/tags/$tag" "refs/tags/$tag^{}")
    if ($existingTag.Count -ne 0) { throw "$tag already exists remotely; tags are immutable." }
    Assert-GitHubReleaseUnused $tag

    if ($supersedesTag) {
        & gh release view $supersedesTag --repo $Repository --json tagName,isDraft,isPrerelease | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Superseded release $supersedesTag does not exist." }
    }

    $template = Get-Content -LiteralPath (Join-Path $repoRoot 'release\RELEASE_NOTES.template.md') -Raw
    foreach ($placeholder in '{{RELEASE_TITLE}}', '{{RELEASE_TAG}}', '{{INSTALLER_NAME}}', '{{HOTFIX_NOTES}}') {
        if (-not $template.Contains($placeholder, [StringComparison]::Ordinal)) {
            throw "Release-note template is missing $placeholder."
        }
    }
    $hotfixNotes = Get-Content -LiteralPath (Join-Path $repoRoot 'release\HOTFIX_NOTES.md') -Raw
    if ([string]::IsNullOrWhiteSpace($hotfixNotes)) { throw 'release/HOTFIX_NOTES.md is empty.' }

    $dispatchStarted = [DateTimeOffset]::UtcNow.AddSeconds(-5)
    $dispatch = @(& gh workflow run release.yml --repo $Repository --ref main `
        -f "tag=$tag" -f "release_commit=$head" -f "supersedes=$supersedesTag")
    if ($LASTEXITCODE -ne 0) { throw 'Hotfix release workflow dispatch failed.' }
    $dispatchText = $dispatch -join "`n"
    $runId = if ($dispatchText -match '/actions/runs/(?<id>[0-9]+)') { $matches.id } else { $null }
    if (-not $runId) {
        for ($attempt = 0; $attempt -lt 10 -and -not $runId; $attempt++) {
            Start-Sleep -Seconds 2
            $runs = @(& gh run list --repo $Repository --workflow release.yml --event workflow_dispatch `
                --branch main --limit 10 --json databaseId,headSha,createdAt | ConvertFrom-Json)
            if ($LASTEXITCODE -ne 0) { throw 'Could not locate the dispatched release run.' }
            $match = $runs | Where-Object {
                $_.headSha -eq $head -and [DateTimeOffset]$_.createdAt -ge $dispatchStarted
            } | Sort-Object { [DateTimeOffset]$_.createdAt } -Descending | Select-Object -First 1
            if ($match) { $runId = [string]$match.databaseId }
        }
    }
    if (-not $runId) { throw 'The release workflow was dispatched, but its exact run ID was not found.' }

    $runUrl = "https://github.com/$Repository/actions/runs/$runId"
    Write-Host "Watching exact hotfix release run: $runUrl"
    & gh run watch $runId --repo $Repository --exit-status
    if ($LASTEXITCODE -ne 0) { throw "Hotfix release workflow failed: $runUrl" }

    $run = & gh run view $runId --repo $Repository --json headSha,status,conclusion,url | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $run.headSha -ne $head -or $run.conclusion -ne 'success') {
        throw 'The completed workflow is not a successful run for the exact public main commit.'
    }

    Invoke-Git fetch origin "refs/tags/$tag:refs/tags/$tag" | Out-Null
    if ((Invoke-Git cat-file -t "refs/tags/$tag")[0].Trim() -ne 'tag') {
        throw 'Published release tag is not annotated.'
    }
    $tagCommit = (Invoke-Git rev-list -n 1 $tag)[0].Trim()
    if ($tagCommit -ne $head) { throw "Published tag targets $tagCommit instead of $head." }

    $release = & gh release view $tag --repo $Repository `
        --json name,url,isDraft,isPrerelease,tagName,assets | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $release.isDraft -or -not $release.isPrerelease) {
        throw 'The GitHub release is not a visible prerelease.'
    }

    $verificationRoot = Join-Path $repoRoot "artifacts\hotfix-verification\$tag-$runId"
    if (Test-Path -LiteralPath $verificationRoot) {
        throw "Verification directory already exists: $verificationRoot"
    }
    New-Item -ItemType Directory -Path $verificationRoot -Force | Out-Null
    & gh release download $tag --repo $Repository --dir $verificationRoot
    if ($LASTEXITCODE -ne 0) { throw 'Independent public asset download failed.' }
    foreach ($line in Get-Content -LiteralPath (Join-Path $verificationRoot 'SHA256SUMS.txt')) {
        if ($line -notmatch '^([0-9a-f]{64})  (.+)$') { throw "Invalid SHA256SUMS line: $line" }
        $path = Join-Path $verificationRoot $matches[2]
        if (-not (Test-Path -LiteralPath $path)) { throw "Missing public checksum target: $($matches[2])" }
        $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $matches[1]) { throw "Public asset hash mismatch: $($matches[2])" }
    }
    $manifest = Get-Content -LiteralPath (Join-Path $verificationRoot 'release-manifest.json') -Raw |
        ConvertFrom-Json
    if ($manifest.Tag -ne $tag -or $manifest.Commit -ne $head) {
        throw 'Public release manifest is not bound to the exact release request.'
    }

    [PSCustomObject]@{
        Tag = $tag
        Commit = $head
        Release = $release.url
        Workflow = $runUrl
        Superseded = $supersedesTag
        Installer = "ChunkPilot-Setup-$tag.exe"
        ProductVersion = $manifest.ProductVersion
    }
}
finally {
    Pop-Location
}
