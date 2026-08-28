# Modpack platform

ChunkPilot's pack path is provider-neutral at the Core/Agent and WebUI boundaries. It supports exact
official Modrinth `.mrpack` releases, exact CurseForge official server packs, supportable generated
CurseForge server candidates, local CurseForge manifest ZIPs, and local `.mrpack` files.
It does not scrape provider pages, execute pack scripts, or infer identity from a manifest that does
not contain it.

## Discovery and identity

- Search, explicit submit, pagination, compatible Minecraft/loader/category/channel filters and project
  details use the selected provider's official API. Modrinth uses v2. CurseForge uses the official REST
  API only when the native approved application credential is available; users are never asked for a key.
- Search is cancellation-aware and superseded responses cannot replace a newer query or stale detail.
  Modrinth hydrates from bounded cache before refresh. CurseForge API responses are not persistently cached
  under the currently reviewed third-party terms; identical in-flight requests are still deduplicated.
- Exact project and release identity comes from the outer API selection. `modrinth.index.json` is not
  treated as a Modrinth project-ID source.
- Remote archives must use the provider's official CDN and exact size/hash evidence. Modrinth requires
  SHA-1 and SHA-512; CurseForge requires its published SHA-1 and records a local SHA-256 after download.
- Local selection uses the native file picker and a five-minute single-use token. The Agent re-reads
  the archive and binds the operation to its size and SHA-512; React receives neither a native path nor
  persistence authority.
- Provider images are allowlisted to the selected provider's official CDN, limited to 512 KiB and 4096 pixels per axis,
  resized to 160px, re-encoded as PNG, and cached in a bounded native cache.

## Materialization and creation

The reader rejects duplicate paths, absolute/traversal paths, links, special ZIP entries, excessive
file counts/sizes, ambiguous overrides, invalid hashes and unsupported schema. The downloader uses
HTTPS, bounded redirects, approved hosts, public-address validation and connection-time IP pinning.
Each indexed file is size/hash verified before activation.

Modrinth materialization applies required server files, then common overrides, then server overrides.
CurseForge official server packs are preferred when the selected client file declares an exact relationship.
Otherwise the exact client manifest may generate a candidate only when every required project/file is
available, distributable, size/hash verified, and compatible with exactly one declared Fabric, NeoForge,
Forge, or Quilt loader. Required dependencies are resolved recursively with cycle/count limits; optional
dependencies are skipped and incompatible relations block creation. Only `config` and `defaultconfigs`
overrides are copied. Scripts, KubeJS roots, arbitrary launch scripts, and client-only content are not run
or guessed. Java and the exact loader are installed through the existing native services.

Creation uses the existing journaled stage/verify/activate transaction and explicit EULA path. Before
promotion, official and generated CurseForge candidates receive a bounded loopback-only Agent-owned launch,
console-readiness check, status ping, clean stop, and process-tree cleanup. Failure or timeout prevents
registration and preserves no partial managed server. Pack scripts are never executed during inspection.

Creation acceptance is detached from the long transaction. React assigns the operation ID before the
request, the Agent records progress durably, and renderer reload/reconnect discovers the same operation.
Progress includes stage, bytes/files and freshness evidence; a transient poll failure reschedules rather
than freezing the last visible state. Terminal completion cannot report `NotStarted — 100%`.

## Updates and ownership

The installed server stores exact provider/project/slug/client-release/server-file, Minecraft, loader,
Java-relevant build and archive identity. Update checks compare whole pack releases. The update transaction creates recovery,
materializes the new whole-pack baseline, preserves persistent world/config state, reports drift and
conflicts, activates transactionally, validates, and rolls back through the existing version system.
It never silently applies ordinary per-mod updates to a linked pack.

Files from the selected release are recorded in a local SHA-256 ownership baseline. Unchanged obsolete
provider files can move out with the retired candidate; locally modified provider files are preserved and
surfaced as conflicts unless the user explicitly selects the new baseline. Files added independently remain
user-owned. Worlds, player data, server properties, access lists, JVM settings, icons, and explicitly marked
persistent paths survive whole-pack updates. Minecraft/loader family changes remain manual-review updates and
should use an updated managed copy when in-place compatibility is not established.

## Current limitations

- Public CurseForge activation remains gated. The local native path is implemented, but current written terms
  do not establish that ChunkPilot may distribute a shared application credential directly in desktop builds.
- The authorized local credential file was absent during the final automated campaign, so live authentication,
  exact public project/file resolution, and representative public-pack runtime validation remain unavailable.
- Local `.mrpack` files are not linked back to a provider automatically and therefore cannot receive
  provider updates until identity is separately proven.
- Modrinth supplies all-time/follows/newest/updated ordering. Period trends remain unavailable until
  ChunkPilot has sufficient local 7/30/365-day snapshots.
- No representative third-party public pack was executed during this milestone. Deterministic fixtures cover
  official server packs, generated manifests, dependencies, staged validation, ownership conflicts, and
  rollback boundaries; public-pack runtime acceptance remains open.
- Client installers, arbitrary scripts, unsupported loaders and manually distributed private files
  are not executed or guessed.

Official specifications and APIs:

- <https://support.modrinth.com/en/articles/8802351-modrinth-modpack-format-mrpack>
- <https://docs.modrinth.com/api/>
- <https://docs.curseforge.com/rest-api/>

Focused development checks:

```powershell
dotnet test tests/ChunkPilot.UnitTests/ChunkPilot.UnitTests.csproj -c Release --filter "FullyQualifiedName~ModrinthPackFormatTests|FullyQualifiedName~CurseForge|FullyQualifiedName~ProviderOwnershipManifest|FullyQualifiedName~Release12Tests"
Set-Location src/ChunkPilot.WebUi
npm test -- --run src/features/modpacks/ModpackPicker.test.tsx src/features/ServerWorkspace.modpack.test.tsx
```
