# Public release process

ChunkPilot public releases are intentionally manual and Windows-only. Ordinary pushes and pull requests run `.github/workflows/ci.yml`; they never publish a release.

## Local gates

From a clean release branch:

```powershell
.\scripts\audit-publication.ps1
.\scripts\publish.ps1 -BuildInstaller -ReleaseTag v1.3.0-alpha.2
.\scripts\smoke-portable.ps1 -PortableRoot .\artifacts\portable-test
.\scripts\test-packaged-ui-close.ps1 -PortableRoot .\artifacts\portable-test
.\scripts\test-packaged-ui-close.ps1 -PortableRoot .\artifacts\portable-test -WebUiPreview
.\scripts\package-release.ps1 -ReleaseTag v1.3.0-alpha.2
.\scripts\test-portable-package.ps1 -PortableZip .\artifacts\release\v1.3.0-alpha.2\ChunkPilot-Portable-v1.3.0-alpha.2-win-x64.zip
git diff --check
```

`test-installer.ps1` refuses to run outside GitHub Actions because it intentionally installs, registers, reinstalls, and uninstalls the app. That gate belongs on a disposable GitHub-hosted Windows runner, never on a development profile or real ChunkPilot data.

## Toolchain trust

- `.NET` is pinned by `global.json`; npm packages are locked by `package-lock.json`.
- The release workflow uses full commit pins for official GitHub actions.
- Inno Setup 7.0.2 is downloaded from the publisher's GitHub release and checked against a pinned SHA-256 plus its valid Pyrsys B.V. Authenticode signature.
- The Microsoft Edge WebView2 Evergreen bootstrapper is downloaded from Microsoft's official programmatic link. Because Microsoft intentionally updates the bytes behind that Evergreen URL, acquisition requires a valid Microsoft Corporation Authenticode signature and the expected Microsoft Edge Update product identity, then records the exact SHA-256 and file version embedded in that build.
- Microsoft SBOM Tool 4.1.5 is installed repository-locally from its verified NuGet package and run on a pinned, SHA-512-verified Microsoft .NET 8.0.30 runtime. Its telemetry output is written only to the ignored build-artifact directory; the tool states that no telemetry is submitted to Microsoft.
- Gitleaks 8.30.1 is downloaded from its official GitHub release and verified with the release checksum before scanning the exact public history.

## Publication

1. Push the exact clean snapshot to public `main` and wait for the ordinary CI run to pass.
2. Manually dispatch **CI** with `distribution: true` and wait for its dependent disposable-runner
   installer/portable job to pass.
3. Create and push a new annotated tag. Never move or reuse a published tag.
4. Manually dispatch **Publish prerelease** with that tag.
5. The first job checks out and proves the annotated tag, reruns all release gates, builds the installer and portable package, tests install/reinstall/uninstall data preservation, generates checksums/notices/SBOM/release notes, and uploads one internal workflow artifact.
6. Only the dependent publish job receives `contents: write`. It refuses an existing release, creates a prerelease, downloads the public assets again, and verifies every entry in `SHA256SUMS.txt`.

No code-signing step exists until a trusted Windows certificate is deliberately configured. SHA-256 is an integrity check, not a SmartScreen reputation mechanism.
