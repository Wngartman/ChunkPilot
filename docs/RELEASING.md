# Public hotfix releases

ChunkPilot hotfix releases are Windows-only, prerelease-only, and deliberately published from an exact
clean public `main` commit. Ordinary pull requests run source validation. The release workflow performs
the one authoritative consumer build and then passes that immutable payload to its write-scoped publish
job; it never rebuilds after testing.

## Prepare the release commit

1. Put only the intended public change on a focused branch and merge it through a green pull request.
2. Update `release/HOTFIX_NOTES.md` with the release-specific changes. The surrounding release text and
   tag/title/filename fields are generated from `release/RELEASE_NOTES.template.md`.
3. Confirm public `main` is clean and exactly matches `origin/main`.
4. Do not create the tag. The workflow creates the annotated tag only after every consumer gate passes.

## Publish with one command

From the clean public `main` worktree:

```powershell
.\scripts\publish-hotfix.ps1 `
    -Version 1.3.0-alpha.4 `
    -Supersedes v1.3.0-alpha.3
```

`-Supersedes` is optional. It is applied only after the new release is public and independently
redownloaded. The script refuses a dirty tree, a non-`main` branch, stale local main, an existing tag or
release, and an unknown previous release. It dispatches `.github/workflows/release.yml` with the exact
40-character public-main commit, watches that one run, and independently verifies the resulting
annotated tag, prerelease, release manifest, and public checksums.

The equivalent manual **Publish hotfix prerelease** workflow dispatch requires:

- `tag`: the new `v1.3.0-alpha.N` tag;
- `release_commit`: the full current public `main` SHA;
- `supersedes`: an optional prior prerelease tag.

## Exact-artifact release gate

The workflow:

1. proves the request still identifies exact public `main` and an unused release identity;
2. builds and tests the WebUI and .NET solution once;
3. creates framework-dependent, self-contained win-x64, and portable layouts;
4. compiles the per-user installer;
5. tests the packaged Agent, accepted UI, and WebUI Preview;
6. creates checksums, notices, an SPDX SBOM, release notes, and `release-manifest.json`;
7. tests a clean portable extraction;
8. tests clean install, same-version reinstall, uninstall, and fixture-data preservation on the
   disposable GitHub-hosted Windows runner;
9. audits source/history and checks whitespace;
10. uploads one internal workflow artifact bound to tag and commit;
11. rechecks public `main`, creates or proves the exact annotated tag, and publishes those same bytes;
12. redownloads public assets, verifies SHA-256, optionally marks the prior prerelease superseded, and
    writes a concise immutable baseline to the workflow summary.

The public `release-manifest.json` records the tag, commit, embedded product version, sizes, and hashes.
`docs/RELEASE-BASELINES.md` retains the alpha.3 baseline created before manifests became a public asset.

## Trust and safety

- `.NET` is pinned by `global.json`; npm packages are locked by `package-lock.json`.
- Official GitHub actions are pinned to full commits.
- Inno Setup 7.0.2 is checked against its pinned SHA-256 and valid Pyrsys B.V. Authenticode
  signature. The changing WebView2 Evergreen bootstrapper is accepted only with the expected Microsoft
  Authenticode and product identity, and its exact downloaded hash is recorded.
- Microsoft SBOM Tool 4.1.5 runs repository-locally on the pinned, SHA-512-verified .NET 8.0.30
  runtime. Gitleaks 8.30.1 is verified against its official release checksum before source-history
  scanning.
- Only the dependent publish job receives `contents: write`.
- Workflow concurrency prevents two runs from publishing the same tag.
- A pre-existing release is never replaced. A matching annotated tag left by a publish-stage
  interruption may be reused only when it peels to the requested exact commit and no release exists.
- Installer lifecycle tests are runner-only and use disposable fixture data.
- The app is unsigned; SHA-256 provides integrity, not SmartScreen reputation.
