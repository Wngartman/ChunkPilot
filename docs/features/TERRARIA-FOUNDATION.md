# Experimental Terraria foundation

Terraria support is an engineering preview behind `--experimental-terraria-preview`. It is not shown by ordinary Create Server and is not a supported public platform yet.

## Official source and integrity

ChunkPilot uses the exact Re-Logic Windows dedicated-server package for Terraria 1.4.5.6:

`https://terraria.org/api/download/pc-dedicated-server/terraria-server-1456.zip`

The reviewed response is 45,635,619 bytes and the Windows subtree is `1456/Windows/`. Re-Logic does not publish a cryptographic checksum for this package. ChunkPilot therefore records its own streamed SHA-256 as **local integrity evidence**, never as an official signature.

The downloader only accepts the allowlisted HTTPS origin and exact release path. Its bounded archive reader rejects traversal, rooted and Windows-invalid paths, links/reparse entries, case-equivalent duplicates, excessive expansion, and suspicious compression. It extracts only the reviewed Windows subtree into unique staging.

## Architecture and safety

`ServerGameKind` distinguishes Minecraft from Terraria while retaining one Agent-owned lifecycle, operation queue, exact-process ownership model, console pipeline, creation journal, backup path, recovery path, and connectivity architecture. Game-specific runtime profiles provide readiness, status, save, and stop behavior without scattering Terraria checks through the supervisor.

The experimental installer:

- reuses `ServerCreationTransaction`;
- keeps the world inside the managed server root;
- writes an explicit managed config;
- uses `playing`, `save`, and `exit` commands;
- disables UPnP;
- currently requires a loopback bind;
- never creates `eula.txt` because Terraria is not governed by the Minecraft EULA flow.

The preview runs before the normal single-instance/Agent startup and uses no production data or network mutation.

## Certification

Run the isolated certifier from the repository:

```powershell
dotnet run --project .\src\ChunkPilot.Certification\ChunkPilot.Certification.csproj -c Release --no-restore -- certify-terraria --cache .\artifacts\terraria-certification --timeout-seconds 900
```

The certifier downloads or reuses the official cache, creates a unique disposable root, binds only to `127.0.0.1`, disables UPnP, owns one exact process, verifies listener/console/save/stop/world/cleanup evidence, and removes its disposable root. It does not touch Windows Firewall or a router.

On the current test machine the official artifact validated with local SHA-256 `d75c455ac217fd3434448c8f8251c1347f0875a85c438589dc71b557777e9155`, but the official executable exited before readiness because Microsoft XNA Framework 4.0 is missing. ChunkPilot did not install that system prerequisite. Exact Terraria runtime support therefore remains blocked rather than certified.

Before this platform can become ordinary product UI, the product needs a reviewed prerequisite acquisition policy, exact real-runtime readiness/save/stop evidence, game-aware diagnostics and connectivity presentation, and public-network acceptance testing by the user.
