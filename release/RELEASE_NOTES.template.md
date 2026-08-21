# ChunkPilot 1.3.0 Alpha 3 Lifecycle Hotfix

ChunkPilot is a local-first Windows x64 launcher and manager for Minecraft servers. This alpha hotfix corrects startup intent and manual Stop ownership without changing the application schema or making the WebUI preview the default.

- Release tag: `v1.3.0-alpha.3`
- Release commit: `{{RELEASE_COMMIT}}`
- Product version: `{{PRODUCT_VERSION}}`
- Build completed: `{{BUILD_TIME_UTC}}`

## Install

Download `ChunkPilot-Setup-v1.3.0-alpha.3.exe`. It is a per-user, self-contained Windows x64 installer, so a separate .NET installation is not required. The installer adds **ChunkPilot** and **ChunkPilot WebUI Preview** shortcuts. The preview remains explicitly opt-in.

## Alpha 3 hotfix

- A stale prior-running, crash-recovery, or restart-journal observation can no longer start a server
  after Windows login, App startup, or Agent startup. Only explicit autostart policy or a due
  user-created schedule authorizes startup.
- Manual **Stop server** now suppresses a pending automatic restart immediately and cancels the owned
  operation before waiting for the server's serialized operation queue.
- Stop reconciles the exact owned process and persisted running state before reporting completion. A
  non-cooperative operation produces an actionable bounded failure instead of an indefinite
  `Stopping` state.

If Microsoft Edge WebView2 Evergreen Runtime is missing, setup runs the Microsoft-signed Evergreen bootstrapper included in the installer. The portable package can run the default WPF interface without Node.js or an installer; its WebUI preview still requires WebView2.

## Current capabilities

- Guided creation for Vanilla, Paper, Purpur, Fabric, NeoForge, Forge, Quilt, and exact server-capable Modrinth releases.
- Managed Java without changing system `PATH` or uninstalling system Java.
- Safe lifecycle control, bounded console capture, backups, restore, worlds, schedules, plugins/mods, and version rollback.
- Consent-first router and Windows Firewall workflows that never claim public reachability from a local port check.
- Local-only application state with no accounts, ads, or telemetry.

## Data preservation

The default uninstall removes application binaries and shortcuts while preserving settings, servers, worlds, backups, credentials, and history. Imported servers remain in their original folders. Optional removal controls are separate and unchecked by default.

## Known limitations

- This is a pre-alpha snapshot, not a stable or beta release.
- The WebUI remains a preview; normal no-argument launch uses the accepted WPF interface.
- Windows x64 is the only packaged platform.
- The installer and executables are unsigned, so SmartScreen may warn.
- Some historical Minecraft releases have no redistributable official server artifact and require a user-supplied artifact or remain unavailable.
- CurseForge integration requires the user's own API key.
- Terraria support is an engineering-only experimental foundation and is not presented as supported product functionality.
- Router, firewall, CGNAT, and outside-in behavior varies by real network; deterministic tests do not prove every environment.

## SHA-256

Verify downloaded assets with `SHA256SUMS.txt`.

```text
{{SHA256_SUMS}}
```

Report non-sensitive problems through [GitHub Issues](https://github.com/Wngartman/ChunkPilot/issues). Follow [SECURITY.md](https://github.com/Wngartman/ChunkPilot/blob/main/SECURITY.md) for vulnerabilities or sensitive reports.
