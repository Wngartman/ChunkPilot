# Modpack platform

ChunkPilot's pack path is provider-neutral at the Core/Agent and WebUI boundaries. It supports exact
official Modrinth `.mrpack` releases, exact CurseForge official server packs, supportable generated
CurseForge server candidates, local CurseForge manifest ZIPs, and local `.mrpack` files.
It does not scrape provider pages, execute pack scripts, or infer identity from a manifest that does
not contain it.

## Discovery and identity

- Search, explicit submit, pagination, compatible Minecraft/loader/category/channel filters and project
  details use the selected provider's official API. Modrinth uses v2. CurseForge uses the official REST
  API through either the configured ChunkPilot application service or an approved native personal key.
  Service-enabled builds need no end-user key; 1.0.0 public builds use personal setup. The service must be
  separately deployed and validated before a new release enables it.
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

Remote CurseForge preflight seals the complete required-file graph, its exact inclusion reasons, and explicit
optional exclusions before creation. The Agent retains a deep-copied authoritative plan under a bounded,
short-lived authorization. Creation must match the exact reviewed project/client/server file, artifact evidence,
platform and Java identity and consumes that authorization once. A renderer cannot inject a generated plan or
replay an old review. Cancellation, selection replacement, failed preflight response delivery, and a begin
explicitly proven unaccepted revoke the exact review; cancellation may pre-revoke an operation before a slow
preflight completes.

The later creation begin has its own exact-operation acceptance protocol. A missing named-pipe response does not
mean the Agent rejected the work: the App looks up the client-generated operation ID, reattaches when it exists,
and replays the unchanged request only after an explicit miss. The Agent serializes concurrent begin attempts,
consumes the authorization once, returns the existing operation for an exact replay, and rejects any changed
destination or plan field. Cancellation after acceptance is attached to that operation; cancellation before
acceptance performs bounded lookups without starting work on a changed selection.

Each creation/managed-content coordinator retains at most 4,096 pre-start cancellation identities. At that bound
it seals all previously unseen Begin requests for the rest of the Agent lifetime: known exact requests still
replay, unseen Begin is rejected before authorization consumption, and cancellation revokes unconsumed authority
and returns synthetic terminal-cancelled evidence without retaining another operation. A permanently unavailable
Agent remains a truthful pending-cancellation limitation because the App cannot prove whether an ambiguous Begin
crossed the pipe.

Creation uses the existing journaled stage/verify/activate transaction and explicit EULA path. Before
promotion, official and generated CurseForge candidates receive a bounded loopback-only Agent-owned launch,
console-readiness check, status ping, clean stop, and process-tree cleanup. Failure or timeout prevents
registration and preserves no partial managed server. Pack scripts are never executed during inspection.
Certification's endpoint proof also requires the task root, listener owner, and every intermediate parent to be
the same live stable Job PID/creation generations in strict parent-before-child creation order; missing, exited, or
PID-reused ancestry fails closed. Repeated native endpoint observation does not claim to eliminate a socket that
exists wholly between observations or a mutation after the final observation.

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

Before any CurseForge cache reuse, download, free-space decision or recovery snapshot, native preflight verifies
the exact linked project/client/server-file tuple and replaces every operational artifact field with current
approved provider evidence. URL, bounded size, SHA-1, applicable local client SHA-256, safe filename, package
type, platform and Java identity come from that preflight rather than the renderer request. Generated updates
also require the sealed dependency plan; that trusted property is excluded from JSON and is used directly rather
than resolving provider metadata again during materialization.

Storage approval follows that canonical preflight. Client-manifest inspection reserves the exact archive size
plus a download margin before its CDN request. After an exact package is downloaded or reused and hash-verified, a
bounded central-directory inspection calculates its declared expansion and rejects encrypted, unsupported,
unsafe, link-like, path-colliding, excessive, or oversized entries. Before recovery snapshot or active-server
mutation, update forecasting groups requirements by real volume and sums that verified expansion with the
current-server snapshot and every generated resolved-payload/staging copy that shares the volume; separate volumes
are checked independently. The former compressed-size multiplier does not authorize archive staging. Saturating
arithmetic prevents extreme declared sizes from wrapping into a smaller requirement. Windows volume identity
follows the target's resolved mount path and volume GUID rather than its apparent drive root. A failed or
unsupported resolution returns before the archive download, recovery snapshot, or server switch.

The current-server snapshot requirement is measured from the same bounded no-follow full-tree inventory consumed
by rollback snapshot and migration. It is cancellation-aware, caps the tree at 500,000 entries, rejects reparse,
inaccessible, redirected, missing, and duplicate/case-colliding components before output, and revalidates each
file's size, last-write identity, and ancestor chain before reading it. Snapshot also bounds and revalidates the
streamed entry afterward. This observation does not freeze unrelated external writers or later free-space changes.

Individual CurseForge mod/dependency installation is similarly review-bound. The Agent stores and digests the
exact ordered plan for one server and root release. The App exposes only the current matching authorization with
the review; switching server/provider/project/file or cancelling revokes it. Install consumes the stored plan
once before operation state, rejects wrong-server/tampered/replayed requests, and downloads no release outside
that reviewed plan.

If install acceptance becomes ambiguous, the App resolves the exact operation ID before replay. A returned
snapshot must match server, provider, root project/file, and operation kind. The Agent returns the original
operation only for an identical request; it never re-consumes the plan or downloads a second copy for an exact
replay, and it rejects an ID paired with another request.

Reviewed destination JAR names must also be unique under Windows case-insensitive comparison. Inventory and every
mutation walk the complete active `mods`/`plugins`, disabled, Recovery, provenance, and configuration path chains
without following reparse points; junctions, directory substitutions, and uncertain components fail closed.
Installation refuses a user-owned, ownership-uncertain, or different-project destination before download and,
under the content lock immediately before activation, revalidates that the same-project file and baseline are
unchanged. A late or replaced local file is preserved, no unrelated provenance is written, and no refused foreign
file is moved to Recovery. Ordinary single-release provider installs use the same destination-ownership rule.
ChunkPilot's lock cannot serialize unrelated processes, and this user-mode path has no single atomic conditional
replace that also proves same-file identity and a no-reparse chain; that external-writer TOCTOU remains disclosed.

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
