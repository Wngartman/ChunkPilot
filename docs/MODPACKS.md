# Modpack platform

ChunkPilot's pack path is provider-neutral at the Core/Agent and WebUI boundaries. It supports exact
official Modrinth `.mrpack` releases, licensed CurseForge server-pack releases when the user supplies
an API key, and local `.mrpack` files.
It does not scrape provider pages, execute pack scripts, or infer identity from a manifest that does
not contain it.

## Discovery and identity

- Search, explicit submit, compatible Minecraft/loader/category filters and project details use the
  selected provider's official API. Modrinth uses v2. CurseForge remains disconnected until the user
  enters a key, which is stored through Windows DPAPI and is never sent to React.
- Search is cancellation-aware and hydrates from bounded cache before refresh. Superseded responses
  cannot replace a newer query or leave a stale detail selection visible.
- Exact project and release identity comes from the outer API selection. `modrinth.index.json` is not
  treated as a Modrinth project-ID source.
- Remote archives must use the official CDN and provide size, SHA-1 and SHA-512.
- Local selection uses the native file picker and a five-minute single-use token. The Agent re-reads
  the archive and binds the operation to its size and SHA-512; React receives neither a native path nor
  persistence authority.
- Provider images are allowlisted to the Modrinth CDN, limited to 512 KiB and 4096 pixels per axis,
  resized to 160px, re-encoded as PNG, and cached in a bounded native cache.

## Materialization and creation

The reader rejects duplicate paths, absolute/traversal paths, links, special ZIP entries, excessive
file counts/sizes, ambiguous overrides, invalid hashes and unsupported schema. The downloader uses
HTTPS, bounded redirects, approved hosts, public-address validation and connection-time IP pinning.
Each indexed file is size/hash verified before activation.

Server materialization applies required server files, then common overrides, then server overrides.
Client-only files and client overrides remain excluded. Optional server files are reported but are not
silently selected. Exactly one declared Fabric, NeoForge, Forge or Quilt loader is installed through
the existing official loader service. Java is resolved by the existing managed-runtime policy.

Creation uses the existing journaled stage/verify/activate transaction and explicit EULA path. Pack
files are not executed during inspection. Public packs are not started during creation; the first
Agent-owned start supplies runtime readiness and load evidence.

Creation acceptance is detached from the long transaction. React assigns the operation ID before the
request, the Agent records progress durably, and renderer reload/reconnect discovers the same operation.
Progress includes stage, bytes/files and freshness evidence; a transient poll failure reschedules rather
than freezing the last visible state. Terminal completion cannot report `NotStarted — 100%`.

## Updates and ownership

The installed server stores exact provider/project/release, Minecraft, loader, Java-relevant build and
archive identity. Update checks compare pack releases. The update transaction creates recovery,
materializes the new whole-pack baseline, preserves persistent world/config state, reports drift and
conflicts, activates transactionally, validates, and rolls back through the existing version system.
It never silently applies ordinary per-mod updates to a linked pack.

Files from the selected release are pack-managed. Files added independently remain user-owned.
Ownership-uncertain content is not deleted automatically.

## Current limitations

- CurseForge requires a user-provided API key and provider availability still depends on its licensed
  API. A disconnected or rate-limited provider remains explicit instead of becoming an empty catalog.
- Local `.mrpack` files are not linked back to a provider automatically and therefore cannot receive
  provider updates until identity is separately proven.
- Modrinth supplies all-time/follows/newest/updated ordering. Period trends remain unavailable until
  ChunkPilot has sufficient local 7/30/365-day snapshots.
- No representative third-party public pack was executed during this milestone. Tests use generated
  fixture packs and official loader artifacts only, so public-pack runtime acceptance remains open.
- Client installers, arbitrary scripts, unsupported loaders and manually distributed private files
  are not executed or guessed.

Official specifications and APIs:

- <https://support.modrinth.com/en/articles/8802351-modrinth-modpack-format-mrpack>
- <https://docs.modrinth.com/api/>
- <https://docs.curseforge.com/rest-api/>

Focused development checks:

```powershell
dotnet test tests/ChunkPilot.UnitTests/ChunkPilot.UnitTests.csproj -c Release --filter "FullyQualifiedName~ModrinthPackFormatTests|FullyQualifiedName~Release12Tests"
Set-Location src/ChunkPilot.WebUi
npm test -- --run src/features/modpacks/ModpackPicker.test.tsx src/features/ServerWorkspace.modpack.test.tsx
```
