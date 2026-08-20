# Paper platform

ChunkPilot's WebUI preview can create a Paper server from an exact stable build for any official
Minecraft version that PaperMC currently exposes. Paper is not the
default platform. Paper servers expose the separately documented, Agent-owned plugin-management
surface in [PAPER-PLUGINS.md](PAPER-PLUGINS.md).

## Authority and flow

1. The Agent reads the official PaperMC Fill v3 project inventory.
2. The user chooses an exact Minecraft release and exact Paper build. Stable is the default;
   PaperMC beta/alpha builds remain Experimental and require a separate acknowledgement. Identity-matched runtime
   evidence promotes a build to **Verified** or **Recommended**; metadata-only builds remain
   **Experimental** and require a separate risk acknowledgement.
3. The selected build ID, HTTPS object URL, size, and SHA-256 remain together as typed metadata.
4. The Agent selects or acquires the required managed Java runtime.
5. `ManagedServerInstaller` resolves the same exact build again and verifies PaperMC's SHA-256 plus
   the expected SHA-256 retained with the native selection. JavaScript never supplies a download URL
   or hash.
6. The existing journalled `ServerCreationTransaction` stages, verifies, activates, registers, and
   rolls back the server. Creation does not open the firewall or router.

The completed definition records `Paper` as its ecosystem, the Minecraft version separately, and
the exact Paper build as its loader version. Central capability mapping consequently presents the
server's Content destination as Plugins and connects it to bounded inventory, local import, permitted
provider browsing, compatible updates, enable/disable, and reversible removal operations.

## Provider and cache behavior

- Project inventory: `https://fill.papermc.io/v3/projects/paper`
- Exact builds: `https://fill.papermc.io/v3/projects/paper/versions/{version}/builds`
- Requests carry a product-identifying User-Agent as required by PaperMC.
- Version and per-version build catalogs are cached under ChunkPilot's catalog cache.
- A fresh cache prevents provider requests. A stale cache is labelled stale; a forced refresh that
  fails keeps the last good catalog rather than replacing it with an empty result.
- `STABLE`, `BETA`, and `ALPHA` builds with an HTTPS URL, positive size, and valid SHA-256 are
  selectable. Stable is preferred automatically; pre-stable builds require the explicit Experimental
  acknowledgement. Malformed and integrity-incomplete entries remain unavailable.

## Build updates

Managed Paper creation records an exact `PaperMC` update source containing the Minecraft version,
Paper build, and reviewed artifact identity. The update adapter refreshes the official Fill build
inventory for that same Minecraft version only. It does not silently convert a Paper build update
into a Minecraft-version upgrade. The existing update transaction provides recovery snapshot,
verified download, staged candidate, activation, validation, and rollback; beta/alpha channels are
included only when the server's update preferences explicitly allow them.

## Exact runtime certification

The dedicated command below resolves an exact official build, verifies PaperMC's SHA-256 and size,
uses an isolated managed Java runtime, writes `eula=true` only under the specified disposable cache,
binds the server to a temporary loopback port, checks readiness and status, sends safe console input,
requires the Paper `plugins` directory, requests a clean stop, and removes the run root:

```powershell
dotnet run --project .\src\ChunkPilot.Certification\ChunkPilot.Certification.csproj -c Release -- certify-paper --accept-minecraft-eula-for-certification --refresh --cache .\artifacts\paper-certification --export-evidence .\src\ChunkPilot.Infrastructure\Resources\paper-runtime-certification-v1.json
```

On 2026-08-17, the resumable campaign reached a terminal result for the latest integrity-complete
stable build of all 54 stable Minecraft lines returned by PaperMC: 31 passed exact runtime
certification, 18 launched but failed readiness, and 5 had no integrity-complete stable build.
Cleanup succeeded for every entry. The passing evidence spans Java 8, 16, 17, 21, and 25 and is
embedded in `paper-runtime-certification-v1.json`; metadata refresh cannot promote a different
artifact identity. Paper 26.2 build 112 is the single Recommended default. Other identity-matched
passes are Verified. Failed and metadata-only builds remain Experimental or Unavailable with their
exact reason.

Ordinary unit tests do not contact PaperMC. They use Fill-shaped response fixtures and isolated
temporary data roots. The transaction integration test uses a deterministic in-memory JAR response
and never starts Java or a Minecraft server. The explicit certification command is the separate,
user-authorized live gate.
