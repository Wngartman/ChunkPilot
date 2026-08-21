[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$documents = @(
    (Get-Item -LiteralPath (Join-Path $repoRoot 'README.md')),
    (Get-Item -LiteralPath (Join-Path $repoRoot 'CONTRIBUTING.md')),
    (Get-Item -LiteralPath (Join-Path $repoRoot 'SECURITY.md'))
) + @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'docs') -Filter '*.md' -File -Recurse)
$errors = [Collections.Generic.List[string]]::new()
$linkPattern = '!?(?:\[[^\]]*\])\((?<target>[^)]+)\)'

foreach ($document in $documents) {
    $text = [IO.File]::ReadAllText($document.FullName)
    $relativeDocument = [IO.Path]::GetRelativePath($repoRoot, $document.FullName)
    foreach ($forbidden in @('ChatGPT', 'Codex conversation', 'the user said', 'D:\ChunkPilot')) {
        if ($text.Contains($forbidden, [StringComparison]::OrdinalIgnoreCase)) {
            $errors.Add("$relativeDocument contains private/internal wording: $forbidden")
        }
    }
    foreach ($match in [regex]::Matches($text, $linkPattern)) {
        $target = $match.Groups['target'].Value.Trim().Trim('<', '>')
        if ($target -match '^(https?://|mailto:|#)') { continue }
        $pathOnly = ($target -split '#', 2)[0]
        if ([string]::IsNullOrWhiteSpace($pathOnly)) { continue }
        $decoded = [Uri]::UnescapeDataString($pathOnly).Replace('/', [IO.Path]::DirectorySeparatorChar)
        $resolved = [IO.Path]::GetFullPath((Join-Path $document.DirectoryName $decoded))
        if (-not (Test-Path -LiteralPath $resolved)) {
            $errors.Add("$relativeDocument has a broken local link: $target")
        }
    }
}

if ($errors.Count -gt 0) { throw ($errors -join [Environment]::NewLine) }
Write-Host "Validated $($documents.Count) public Markdown documents and their local links."
