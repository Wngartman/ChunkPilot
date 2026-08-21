## Download ChunkPilot for Windows

**Recommended:** download and run [`{{INSTALLER_NAME}}`](https://github.com/Wngartman/ChunkPilot/releases/download/{{RELEASE_TAG}}/{{INSTALLER_NAME}}).

**Portable:** use [`ChunkPilot-Portable-{{RELEASE_TAG}}-win-x64.zip`](https://github.com/Wngartman/ChunkPilot/releases/download/{{RELEASE_TAG}}/ChunkPilot-Portable-{{RELEASE_TAG}}-win-x64.zip) when you do not want an installed Start Menu entry.

ChunkPilot is a local-first Windows x64 launcher and manager for Minecraft servers. This prerelease was built, tested, installed, upgraded, and packaged from one exact public commit.

- Release tag: `{{RELEASE_TAG}}`
- Release commit: `{{RELEASE_COMMIT}}`
- Product version: `{{PRODUCT_VERSION}}`
- Build completed: `{{BUILD_TIME_UTC}}`

## What's in Alpha 5

{{HOTFIX_NOTES}}

The default uninstall preserves settings, servers, worlds, backups, provider credentials, and history. Imported servers remain in their original folders.

## Current limitations

- This is alpha software for 64-bit Windows 10 and Windows 11.
- The installer and executables are unsigned, so SmartScreen may warn.
- CurseForge production access remains disabled while the application credential model is approved; users are never asked for an API key.
- Some historical Minecraft versions have no current official server artifact and require an original user-supplied ZIP or JAR.
- Router, firewall, CGNAT, and outside-in behavior varies by network and still requires acceptance on the actual machine.

## SHA-256

Verify the installer, portable ZIP, and release metadata with `SHA256SUMS.txt`.

```text
{{SHA256_SUMS}}
```

Report non-sensitive problems through [GitHub Issues](https://github.com/Wngartman/ChunkPilot/issues). Follow [SECURITY.md](https://github.com/Wngartman/ChunkPilot/blob/main/SECURITY.md) for vulnerabilities or sensitive reports.
