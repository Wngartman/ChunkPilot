<p align="center">
  <img src="assets/brand/ChunkPilot-256.png" width="112" height="112" alt="ChunkPilot logo">
</p>

# ChunkPilot

ChunkPilot is a local-first Windows app that creates and manages Minecraft servers without making you learn Java, JAR layouts, launch scripts, or router terminology.

![ChunkPilot dashboard](docs/images/dashboard.png)

## Download

**Recommended: [Windows Installer](https://github.com/Wngartman/ChunkPilot/releases/download/v1.3.0-alpha.4/ChunkPilot-Setup-v1.3.0-alpha.4.exe)**

The per-user installer includes the required .NET runtime and can install Microsoft WebView2 when it is missing.

**Portable: [Windows x64 ZIP](https://github.com/Wngartman/ChunkPilot/releases/download/v1.3.0-alpha.4/ChunkPilot-Portable-v1.3.0-alpha.4-win-x64.zip)**

Extract the whole ZIP and run `ChunkPilot.exe`; no installation or developer tools are required.

ChunkPilot is prerelease software. The binaries are currently unsigned, so Windows SmartScreen may show a warning. Published SHA-256 hashes verify file integrity but do not create publisher reputation.

## What ChunkPilot does

- **Create servers:** guided Vanilla, Paper, Fabric, Quilt, Forge, NeoForge, and modpack setup with managed Java and exact version selection.
- **Play with friends:** choose local-only, home-network, or Internet hosting while ChunkPilot keeps local, LAN, router-reported, and verified public addresses distinct.
- **Install content:** browse Modrinth, paste a supported provider link, or import a server ZIP, `.mrpack`, JAR, or existing folder without manual extraction.
- **Protect worlds:** transactional installs and updates, verified backups, rollback snapshots, safe restore, and recovery-first removal.
- **Diagnose problems:** bounded console history, lifecycle progress, connectivity evidence, crash analysis, and local diagnostic bundles.

## Quick start

1. Install ChunkPilot.
2. Select **Create server** or **Add existing**.
3. Choose who should be able to join: this PC, your home network, or the Internet.
4. Start the server.
5. Use **Share** to copy the authoritative join information.

## Supported platforms

| Platform | Current support |
| --- | --- |
| Vanilla | Guided installation from official Mojang server artifacts, including certified Minecraft 1.2.5. |
| Paper | Exact official PaperMC builds, plugins, updates, backups, and lifecycle management. |
| Fabric and Quilt | Official loader metadata and managed installation for supported versions. |
| Forge and NeoForge | Official loader metadata and managed installation for supported versions. |
| Modpacks | Modrinth browsing, exact provider links, `.mrpack`, server-pack ZIP, and generic archive import. |
| Historical Minecraft | Automatic installation where an official server artifact exists; otherwise ChunkPilot explains that an original user-supplied ZIP or JAR is required. |

CurseForge project links and provider contracts are implemented, but live production access remains disabled while ChunkPilot's application-level credential approval is completed. Ordinary users are never asked for an API key. Terraria remains an engineering foundation and is not offered as a supported server type.

## Data and privacy

ChunkPilot is local-first: there is no required ChunkPilot account, no ads, and no telemetry. Server metadata, settings, recovery records, and logs remain on the PC. External requests occur only for the provider or reachability operation the user selects.

Managed servers default to `%USERPROFILE%\ChunkPilot\Servers`; application state defaults to `%LOCALAPPDATA%\ChunkPilot`. Imported folders remain in place and are treated as external data unless the user deliberately creates a managed copy. Uninstall preserves server data by default.

## Requirements

- 64-bit Windows 10 or Windows 11
- Internet access for provider metadata, downloads, and optional outside-in reachability checks

## Known limitations

- This is an alpha prerelease and is not code-signed.
- Some historical Minecraft versions have no current official server download and require a legitimately obtained user-supplied artifact.
- CurseForge live browsing is not enabled in the public build yet.
- Real router, firewall, CGNAT, and outside-in behavior varies by network and requires user acceptance on the actual machine.

## Help

- [Releases](https://github.com/Wngartman/ChunkPilot/releases)
- [Report a problem](https://github.com/Wngartman/ChunkPilot/issues/new?template=bug_report.yml)
- [Troubleshooting and known issues](docs/troubleshooting/README.md)
- [Security policy](SECURITY.md)

## Development

See [Contributing](CONTRIBUTING.md) and the [developer documentation](docs/development/README.md). The repository is publicly readable for inspection and reproducible builds, but no open-source source-code license is granted. Third-party components retain the licenses listed in [legal/THIRD-PARTY-NOTICES.md](legal/THIRD-PARTY-NOTICES.md).
