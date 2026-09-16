## Download ChunkPilot for Windows

**Recommended:** download and run [`{{INSTALLER_NAME}}`](https://github.com/Wngartman/ChunkPilot/releases/download/{{RELEASE_TAG}}/{{INSTALLER_NAME}}).

**Portable:** use [`ChunkPilot-Portable-{{RELEASE_TAG}}-win-x64.zip`](https://github.com/Wngartman/ChunkPilot/releases/download/{{RELEASE_TAG}}/ChunkPilot-Portable-{{RELEASE_TAG}}-win-x64.zip) when you do not want an installed Start Menu entry.

ChunkPilot is a local-first Windows x64 launcher and manager for Minecraft servers. These artifacts were built, tested, installed, upgraded, and packaged from one exact public commit by the release workflow. A tag containing `alpha`, `beta`, or `rc` is a prerelease, not stable certification.

- Release tag: `{{RELEASE_TAG}}`
- Release commit: `{{RELEASE_COMMIT}}`
- Product version: `{{PRODUCT_VERSION}}`
- Build completed: `{{BUILD_TIME_UTC}}`

## Changes in this release

{{HOTFIX_NOTES}}

The default uninstall preserves settings, servers, worlds, backups, provider credentials, and history. Imported servers remain in their original folders.

## Upgrading

Close ChunkPilot and let its servers finish saving and stopping, then run the installer normally over
the existing installation. Do not uninstall or move your server folders first. The visible product
version is **1.0.1**; Windows uses numeric installer version **1.3.2** to keep upgrades from the earlier
1.3.0-alpha installers ordered correctly. The database remains at schema **6**.

## Current limitations

- Windows x64 only. Automated Windows checks do not certify every Windows version, router, or hardware configuration.
- The installer and executables are unsigned, so SmartScreen may warn.
- CurseForge requires each user to configure their own approved API key through native setup. No shared developer key is shipped, and author download restrictions remain enforced.
- Some historical Minecraft versions have no current official server artifact and require an original user-supplied ZIP or JAR.
- Router, firewall, CGNAT, and outside-in behavior varies by network and still requires acceptance on the actual machine.

## SHA-256

Verify the installer, portable ZIP, and release metadata with `SHA256SUMS.txt`.

```text
{{SHA256_SUMS}}
```

Report non-sensitive problems through [GitHub Issues](https://github.com/Wngartman/ChunkPilot/issues). Follow [SECURITY.md](https://github.com/Wngartman/ChunkPilot/blob/main/SECURITY.md) for vulnerabilities or sensitive reports.
