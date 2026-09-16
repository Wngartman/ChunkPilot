<p align="center">
  <img src="assets/brand/ChunkPilot-256.png" width="112" height="112" alt="ChunkPilot logo">
</p>

# ChunkPilot

Create a Minecraft server, bring an existing world, and play with friends. ChunkPilot manages Java, server files, backups, updates, and connections from one Windows app.

![ChunkPilot dashboard with sample servers](docs/images/dashboard.png)

## Download ChunkPilot 1.0.1

**Recommended: [Windows Installer](https://github.com/Wngartman/ChunkPilot/releases/download/v1.0.1/ChunkPilot-Setup-v1.0.1.exe)**

The per-user installer includes the required .NET runtime and can install Microsoft WebView2 when it is missing.

**Portable: [Windows x64 ZIP](https://github.com/Wngartman/ChunkPilot/releases/download/v1.0.1/ChunkPilot-Portable-v1.0.1-win-x64.zip)**

Extract the whole ZIP and run `ChunkPilot.exe`; no installation or developer tools are required.

The binaries are currently unsigned, so Windows SmartScreen may show a warning. Published SHA-256 hashes verify file integrity but do not create publisher reputation. The release workflow verifies the exact source commit and installer upgrade path before publishing artifacts.

## What ChunkPilot does

- **Create servers:** guided Vanilla, Paper, Fabric, Quilt, Forge, NeoForge, and modpack setup with managed Java, exact version selection, and a safe choice between a new world or an existing world folder/ZIP.
- **Play with friends:** choose local-only, home-network, or Internet hosting, with separate addresses and connection checks for each.
- **Install content:** browse Modrinth or connect your own approved CurseForge API key, paste an exact provider link, or import a server ZIP, `.mrpack`, JAR, or existing folder without manual extraction.
- **Protect worlds:** transactional installs and updates, verified backups, rollback snapshots, safe restore, and recovery-first removal.
- **Stay in control:** live console, player access, schedules, configuration editing, crash diagnosis, and local diagnostic bundles.

## Quick start

1. Install ChunkPilot.
2. Select **Create server** or **Add existing**.
3. Choose a version or modpack, then create a new world or select your existing world folder/ZIP.
4. Review the settings, accept the Minecraft EULA, and create the server.
5. Start it and use **Share** for its join address. Internet access is a separate, optional setup.

## Supported platforms

| Platform | Current support |
| --- | --- |
| Vanilla | Guided installation from official Mojang server artifacts, including certified Minecraft 1.2.5. |
| Paper | Exact official PaperMC builds, plugins, updates, backups, and lifecycle management. |
| Fabric and Quilt | Official loader metadata and managed installation for supported versions. |
| Forge and NeoForge | Official loader metadata, exact installer verification, and managed installation for supported versions. |
| Modpacks | Modrinth and configured CurseForge browsing, exact provider links, `.mrpack`, server-pack ZIP, and generic archive import. |
| Historical Minecraft | Automatic setup requires an official server artifact, verifiable integrity, compatible Java, and a known managed launcher. Unsupported versions remain unavailable; supported original-file imports are offered separately. |

CurseForge in 1.0.1 uses an optional personal API key, entered through **Settings → Set up CurseForge**
and protected by Windows. The private application-service integration keeps ChunkPilot's key on the
backend and requires no key from users. Its small-pack install, restart, backup, restore, update, and
rollback checks passed, but large-download hosting capacity is not ready. That service is not activated
in this release.
Author download restrictions apply to both routes. See [CurseForge access](docs/architecture/CURSEFORGE-CREDENTIAL-DELIVERY.md).
Terraria remains an engineering foundation and is not offered as a supported server type.

## Data and privacy

No required ChunkPilot account, no ads, and no telemetry. Server metadata, settings, recovery records, and logs stay on
your PC. Provider searches, downloads, enabled update checks, and Mojang player-head requests use their
respective online services. Outside-in connection checks require an explicit action; this build has no
production probe endpoint. Details are in the [network egress inventory](docs/security/NETWORK-EGRESS.md).

Managed servers default to `%USERPROFILE%\ChunkPilot\Servers`; application state defaults to `%LOCALAPPDATA%\ChunkPilot`. Imported folders remain in place and are treated as external data unless the user deliberately creates a managed copy. Uninstall preserves server data by default.

## Requirements

- 64-bit Windows 10 (1809 or later) or Windows 11
- Internet access for provider metadata, downloads, and optional outside-in reachability checks

Use a maintained Windows release. Microsoft's [.NET support policy](https://learn.microsoft.com/en-us/dotnet/core/install/windows#supported-versions)
and WebView2 lifecycle still apply; compatibility is not a promise of security updates for an obsolete OS.

ChunkPilot uses the shared WebView2 runtime, loads workspaces on demand, and bounds console history and
background work. Server memory and CPU needs depend on the selected Minecraft version and pack; a large
modpack still needs substantially more hardware than a small Vanilla server.

## Known limitations

- This release is not code-signed.
- Some historical Minecraft versions have no current official server download and require a legitimately obtained user-supplied artifact.
- CurseForge browsing requires your own approved API key; author download restrictions remain enforced.
- Automatic router setup supports UPnP, PCP, and NAT-PMP where the router and network allow it. It cannot
  remove CGNAT. Starlink's router does not support port forwarding; direct inbound access needs a
  supported third-party router and a public IP, or an alternative tunnel/private network. See
  [Starlink's requirements](https://starlink.com/en-ps/support/article/e3e6bb65-2bbc-1c8a-0c42-74b4776a64f8).
  A router mapping alone is not proof that friends can join.
- Latest and historical version metadata is broader than the runtime-tested matrix. Missing official
  server artifacts are not rebuilt automatically from unofficial sources.

## Help

- [Releases](https://github.com/Wngartman/ChunkPilot/releases)
- [Report a problem](https://github.com/Wngartman/ChunkPilot/issues/new?template=bug_report.yml)
- [Troubleshooting and known issues](docs/troubleshooting/README.md)
- [Security policy](SECURITY.md)

## Development

See [Contributing](CONTRIBUTING.md) and the [developer documentation](docs/development/README.md). The repository is publicly readable for inspection and reproducible builds, but no open-source source-code license is granted. Third-party components retain the licenses listed in [legal/THIRD-PARTY-NOTICES.md](legal/THIRD-PARTY-NOTICES.md).
