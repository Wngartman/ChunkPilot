# Public prerelease process

ChunkPilot prereleases are published from one exact clean public `main` commit. Pull requests run path-aware feature validation; an explicit Release workflow performs the one authoritative consumer build and hands the same tested bytes to its write-scoped publish job.

## Prepare the release commit

1. Put only the intended public change on a focused branch and merge it through a green pull request.
2. Update `release/HOTFIX_NOTES.md` with release-specific changes.
3. Confirm public `main` is clean and exactly matches `origin/main`.
4. Do not create the tag. The workflow creates the annotated tag only after every consumer gate passes.

## Publish

From clean public `main`:

```powershell
.\scripts\publish-release.ps1 `
    -Version 1.3.0-alpha.4 `
    -Supersedes v1.3.0-alpha.3
```

The command rejects a dirty or non-main tree, stale public main, reused tag/release, or unknown superseded release. It dispatches `.github/workflows/release.yml` with the full public-main SHA, watches that exact run, then independently redownloads and verifies the public assets.

## Validation tiers

- **Development Validation** runs WebUI and public-document checks on relevant non-main pushes.
- **Feature Validation** is the authoritative pull-request workflow. It classifies paths, runs unit/migration checks for .NET changes, and adds the integration suite for lifecycle, ownership, backup, networking, schema, installer, and release changes.
- **Release** runs only through explicit workflow dispatch. It builds once and tests the exact artifacts that it publishes.

The Release workflow:

1. proves the request identifies exact public `main` and an unused immutable release identity;
2. builds/tests the WebUI and .NET solution once;
3. creates framework-dependent, self-contained win-x64, and organized portable layouts;
4. compiles the per-user installer;
5. tests the packaged Agent and default WebUI close path;
6. generates checksums and one metadata ZIP containing the SPDX SBOM, notices, build manifest, and provenance;
7. tests a clean portable extraction;
8. tests clean install, prior-version upgrade when configured, same-version reinstall, uninstall, and fixture-data preservation on a disposable GitHub-hosted runner;
9. audits public source/history, documentation links, and whitespace;
10. uploads one internal workflow payload bound to the tag and commit;
11. rechecks public `main`, creates the exact annotated tag, and publishes those same bytes;
12. redownloads all four public assets, verifies their hashes and embedded build identity, marks the previous prerelease superseded, and records a concise baseline.

## Public assets

- `ChunkPilot-Setup-v1.3.0-alpha.N.exe`
- `ChunkPilot-Portable-v1.3.0-alpha.N-win-x64.zip`
- `SHA256SUMS.txt`
- `ChunkPilot-Release-Metadata-v1.3.0-alpha.N.zip`

The metadata ZIP contains `ChunkPilot-SBOM.spdx.json`, `THIRD-PARTY-NOTICES.txt`, `build-manifest.json`, and `provenance.json`.

## Trust and signing

- .NET and npm dependencies are pinned; official GitHub actions use full commit SHAs.
- Inno Setup and the WebView2 bootstrapper are identity/integrity checked before use.
- The repository-local SBOM tool and runtime are pinned and validated.
- Only the dependent publish job receives `contents: write`; pull-request jobs never receive signing secrets.
- See [code signing](CODE-SIGNING.md). Until a trusted identity is enrolled, artifacts remain truthfully unsigned and checksums provide integrity rather than publisher reputation.
- Tags and release assets are immutable and never overwritten.
