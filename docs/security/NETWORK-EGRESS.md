# Network egress inventory

ChunkPilot is local-first. It has no account requirement, telemetry, advertising, analytics, or automatic
crash upload. Network access is limited to the paths below. Ordinary server supervision and file editing do
not contact ChunkPilot-operated services.

Every internet request inherently exposes the computer's public IP address to the destination. ChunkPilot's
HTTP clients also send a product user-agent. The **Data sent** column lists the additional application data
placed in the URL or body.

| Destination or class | Trigger | Automatic? | Data sent | Bound and cache behavior |
|---|---|---:|---|---|
| `piston-meta.mojang.com` and manifest-selected official Mojang download hosts | Browse or create/update a Vanilla server, or resolve an official historical artifact | Only inside the selected workflow | Minecraft version and artifact URL from official metadata | Metadata calls are normally 30–45 seconds; large verified downloads are streamed with longer operation timeouts and local artifact reuse |
| `fill.papermc.io`, `api.purpurmc.org` | Browse, create, or update Paper/Purpur | Only inside the selected workflow | Minecraft version and build identity | 30–45 second metadata calls; catalogs use bounded local caches; artifacts are streamed and hash-checked |
| `meta.fabricmc.net`, `meta.quiltmc.org`, `quiltmc.org`, `maven.quiltmc.org`, `maven.minecraftforge.net`, `files.minecraftforge.net`, `maven.neoforged.net` | Browse, create, certify, or update a managed loader server | Only inside the selected workflow | Minecraft and loader versions | 30–45 second metadata calls, four-hour catalog cache where applicable; installers/libraries use bounded operation timeouts and integrity checks |
| `meta.legacyfabric.net`, `meta.ornithemc.net`, `maven.fabricmc.net`, `maven.quiltmc.org`, `maven.ornithemc.net` | Explicit historical-loader creation/certification | Only inside the selected workflow | Historical Minecraft, loader, and intermediary identities | Official-host allowlists; metadata calls are bounded; materialized artifacts require provider hashes or official sidecars |
| `api.adoptium.net` and the exact asset URL returned by Adoptium | A selected server needs a managed Java runtime not already present | Only inside creation/update that needs Java | Required Java major, Windows/x64/JRE selection | 30 second metadata call; runtime download is streamed, checksum-verified, and cached locally |
| `api.modrinth.com`, `cdn.modrinth.com` | Search/browse, resolve a link, install, or update a modpack/mod/plugin | Search occurs while the user uses provider browsing; no background polling | Search text, Minecraft version, loader, project/release identifiers | Catalog cache is fresh for six hours and usable offline for up to 30 days; plugin search cache is five minutes; downloads are bounded, streamed, and hash-checked |
| Allowlisted `.mrpack` origins: `cdn.modrinth.com`, GitHub download hosts, `gitlab.com`, and public Google storage redirects used by GitLab | Import a user-selected `.mrpack` whose manifest names one of these origins | No | Exact manifest-selected file paths | HTTPS only, at most five redirects, public DNS addresses only, 20-minute stream timeout, manifest size/hash verification |
| `api.curseforge.com` | Developer-gated CurseForge authentication, discovery, version/category/loader inventory, link resolution, exact project/file detail, dependency resolution, and update checks | No; only while the user requests authentication, browse, link, install, update, or refresh work. Scheduled/background CurseForge checks are disabled | Search text, selected Minecraft version/loader/category, project/file IDs, public IP inherent in HTTPS, and the approved application credential in the `x-api-key` header | Five-second connect and 20-second total request bounds; redirects/cookies disabled; the final response URI must remain exact HTTPS `api.curseforge.com`; JSON is capped at 8 MiB. One `429` retry is allowed only for a provider `Retry-After` no greater than five seconds. Identical reads coalesce only while the request is active; completed API responses are never persisted or reused as an offline cache |
| `forgecdn.net` and its HTTPS subdomains, including `media.forgecdn.net` and `mediafilez.forgecdn.net` | User-selected CurseForge image, exact mod/modpack download, client-manifest preflight, generated-candidate build, or update | No; only for visible provider images or an explicit review/download/install/update workflow | Exact provider-selected file/image path, public IP inherent in HTTPS, and for protected downloads the approved application credential in the `x-api-key` header | Redirects/cookies disabled and the final URI must remain an approved HTTPS CDN host. Archives are streamed with declared size and provider SHA-1 verification plus a locally computed SHA-256 baseline. Images are natively fetched, type/size/dimension checked, converted to PNG, and held only in a bounded memory cache |
| `api.github.com` and GitHub release asset hosts | Check or install a server pack explicitly linked to GitHub Releases | User action or an enabled update check/schedule | Repository, release, and asset identities | 45 second metadata timeout; up to 50 releases; downloaded target must pass the recorded size/hash policy |
| User-configured HTTPS direct-manifest and artifact hosts | Check or install an explicitly linked direct update manifest | User action or an enabled update check/schedule | The configured manifest URL, pack identity, and selected artifact URL | 45 second metadata timeout; HTTPS required; exact SHA-256 required before activation |
| `download.geysermc.org`, plus Modrinth for ViaVersion | Explicitly install/update Java/Bedrock crossplay packages | No | Selected project/platform and release identity | 30 second metadata and bounded streamed install; official provider SHA-256/SHA-512 required |
| `sessionserver.mojang.com`, `textures.minecraft.net` | Render a head for a player row that has an authoritative UUID | **Yes, while the Players workspace is visible** | Player UUID to Mojang; Mojang-selected texture identifier | Six-second HTTP timeout, one MiB per response, at most 128 successful/in-flight player entries in memory; failure falls back to a local generic head |
| `api.mcstatus.io` | User confirms the legacy one-time external status test | No; explicit consent for each test | Configured public hostname/address and port in the request path | Ten-second timeout; result is point-in-time and not treated as durable proof |
| Optional configured outside-in probe origin (`CHUNKPILOT_REACHABILITY_PROBE_URL`) | User presses **Check from outside** in Advanced connectivity | No; no production endpoint is compiled into this build | Router-reported public IPv4, server TCP port, and random correlation ID | HTTPS origin only, redirects disabled, 15-second total timeout, four KiB response limit, `no-store`; service contract is stateless |
| `terraria.org` | Development-only `--experimental-terraria-preview` certification | No; not reachable from ordinary Create Server | Fixed Terraria release package path | Exact host/path, redirects constrained to the same origin, 20-minute streamed download, expected size/hash and local cache verification |
| Allowlisted documentation and repair sites | User opens an EULA, Help source, issue/release link, or WebView2 repair page in the system browser | No | The selected documentation URL | External browser owns its network behavior; in-product Help uses a fixed HTTPS host allowlist |

## Local-network traffic

These paths do not contact an internet service, but they can send packets on the PC or home network:

- Minecraft status and port checks connect to the selected configured address/port. Normal lifecycle checks
  use loopback for a local managed server.
- Router discovery uses PCP and NAT-PMP to the selected default gateway on UDP 5351, then UPnP SSDP to
  `239.255.255.250:1900` and the exact local control URL announced by that router. Read-only detection can
  occur while evaluating connectivity. Creating or deleting mappings still requires the explicit Internet
  hosting workflow and exact ownership evidence.
- Minecraft itself opens the server's configured TCP listener, and Geyser may open its separately configured
  UDP listener. Those are server processes owned by the Agent, not analytics or ChunkPilot service calls.

## Logging, diagnostics, and credentials

Provider credentials are protected locally through the Windows secret store. The approved CurseForge source
file is bootstrap input only: the candidate value is authenticated against the official Minecraft game identity
before DPAPI storage is changed, a rejected/offline/cancelled rotation preserves the last accepted value, and the
source-path environment variable is removed from the Agent and from every managed child process. At App startup,
`AgentClient` first captures that optional path for only the exact non-shell Agent child and immediately removes
it from the long-lived App environment; the captured reference is discarded after an existing Agent answers or
the child starts. A later browser, shell-launched process, or elevated firewall helper therefore cannot inherit
the source path from the App. The credential, its source path, and authorization header never cross the named-pipe
or WebUI bridge.
The bounded source read does not resize away from its original allocation: only the populated span is decoded and
the complete original byte buffer is zeroed in `finally`. Processes that execute managed server content,
downloaded Java/loader code, approved automation, staged validation, or runtime certification inherit only a
bounded Windows/Java baseline plus explicit workflow variables, not the App/Agent's arbitrary parent environment.

Normal logs identify providers, stages, hosts, and artifact names where those fields are ordinary local evidence.
CurseForge is narrower: persistent creation/update logs, operation and creation journals, instance history, and
recovery descriptions use fixed local wording and do not retain API URLs, project/release labels, provider file
names, provider hashes, or response descriptions. Diagnostic bundle export redacts recognized authorization
headers, API keys, tokens, passwords, and credential fields from both text and structured JSON. It deliberately
retains useful non-provider context such as server/player names, UUIDs, local paths, timestamps, and local
operation evidence, so the UI tells the user to review a bundle before sharing.

No diagnostic bundle is uploaded by ChunkPilot. Creating a bundle writes it locally; sharing it is a separate
user action outside the application.

CurseForge receives only the metadata needed by the requested provider action; ChunkPilot never uploads a
managed server directory, world, configuration, log, or backup to CurseForge.

## CurseForge operation authorization boundary

Discovery output is not download authority. A ready remote-creation preflight is deep-copied into a bounded,
short-lived Agent registry and can start exactly one matching official-pack or generated-candidate creation. Its
identity covers the exact project/client/server-file relationship, approved artifacts and integrity, platform,
Java requirement, and generated required-dependency graph. Cancellation, selection replacement, failed preflight
response delivery, a begin explicitly proven unaccepted, expiry, mismatch and replay revoke or reject that exact
authorization; a pre-completion tombstone handles cancellation that arrives before a slow preflight registers.

CurseForge mod/dependency plans use a separate short-lived authorization bound to one server, provider, root
project/file and SHA-256 digest of the complete ordered plan. Installation consumes the Agent's stored copy once
and performs no second metadata resolution. Renderer state cannot retarget that plan to another server or replay
it after the selection changes.

Named-pipe response loss does not mint a second authorization or operation. Creation and managed-content clients
generate the operation identity before begin, query the Agent for that exact identity after ambiguous transport
failure, and replay only after an explicit miss. Agent-side serialized acceptance consumes one-use authority at
most once and returns existing work only when the complete request identity matches. Managed-content lookup is
validated against server, kind, provider, project and release; creation cancellation discovered after acceptance
is sent to the accepted operation.

Pre-start cancellation retention is bounded without converting capacity into an unfenced result. At 4,096
retained identities, each coordinator seals new registrations for the rest of that Agent lifetime. Known IDs
continue to require their exact stored requests; unseen Begin requests are rejected before consuming authority,
and a matching cancellation revokes any unconsumed authorization and returns synthetic terminal evidence without
allocating another retained operation. If the Agent itself is permanently unavailable after ambiguous dispatch,
the App keeps the cancellation pending: transport loss is not treated as proof that Begin was blocked.

Whole-pack updates do not use a UI-supplied authorization object. Instead, every CurseForge download-only or
install operation runs native exact preflight before snapshot, cache lookup/reuse, or download, then replaces all
wire-carried operational fields with the approved URL, size, SHA-1, applicable local SHA-256, filename, package
type and platform identity. A generated update's trusted dependency plan is native-only and excluded from JSON in
both directions. These rules constrain which already user-requested provider calls and downloads may proceed;
they do not add background polling or another destination.

Managed add-on inventory and mutation walk the complete active `mods`/`plugins`, disabled, Recovery, provenance,
and configuration path chains without following reparse points. Junctions, directory substitutions, and uncertain
components fail closed. Activation revalidates the exact destination and unchanged same-project baseline under the
content lock immediately before mutation; late or foreign files are preserved. That lock does not serialize an
unrelated process, and Windows provides no single user-mode conditional replace that simultaneously proves the
same file identity and a no-reparse chain, so the remaining external-writer TOCTOU is explicitly not claimed away.

Exact provider size is also a precondition for egress. Client-manifest preflight checks its staging volume before
the CDN request. After an exact package is downloaded or reused and hash-verified, bounded central-directory
inspection calculates its declared expanded size and rejects unsafe, encrypted, unsupported, link-like, colliding,
excessive, or oversized entries. Before recovery snapshot or active-server mutation, update preparation sums that
verified expansion with the current-server snapshot and every generated payload/staging copy on each real volume.
The former compressed-size multiplier does not authorize archive staging. The system resolves final mounted
targets to Windows volume GUIDs instead of trusting drive letters; network-backed or unresolved targets fail
closed. Overflow-safe arithmetic cannot reduce an extreme requirement.

The current-server snapshot term is not a recursive best-effort estimate. Forecast, rollback snapshot, and
migration use the same cancellation-aware full-tree inventory, bounded at 500,000 entries and prohibited from
following any reparse point. Unsafe, inaccessible, missing, redirected, duplicate/case-colliding, or changed files
fail closed before output or mutation; size, last-write identity, and the no-reparse ancestor chain are rechecked
before an inventoried file is consumed, and snapshot bounds the streamed length and validates again afterward.
This does not freeze unrelated external filesystem writers or free space after observation.

## Headless CurseForge certification isolation

Certification executes only the controller included in the exact-HEAD development package and covered by its
complete SHA-256 manifest. It creates a fresh owned `data`/`servers`/`temp` run root, scopes temporary and .NET
bundle extraction there, and permits no router, firewall, public probe, query, or RCON mutation. Cleanup first
proves every task Job and endpoint is gone, then moves only that exact bounded run root to the Windows Recycle Bin;
unexpected entries, reparse points, or Recycle Bin failure retain the run and fail the cleanup claim.

The running-server check takes two complete TCP4/TCP6/UDP4/UDP6 owner-PID inventories across three stable Job
snapshots before, between, and after them. Job process accounting and the exact PID/creation set must match at all
three boundaries; the exact owned endpoint multiset must also match across the inventories. Each accepted owner is
then revalidated live against its creation identity and the task-server process subtree. The root, owner, and every
intermediate parent must be the same live generation represented by the stable Job PID/creation set, and each
parent must have been created strictly before its child; missing, exited, cyclic, reordered, or PID-reused ancestry
fails closed. Only loopback TCP on the selected Minecraft port is approved; wildcard, non-loopback, UDP,
secondary-port, identity-raced, unreadable, or mutation-raced evidence fails closed. The report records only
sanitized counts and policy results. Each inventory still reads four Windows tables sequentially, so repeated
stable observation cannot exclude a socket opened and closed entirely between observations or a mutation after
the second inventory.

## CurseForge persistence boundary

CurseForge searches, project/file responses, pagination, categories, release labels, changelogs, download URLs,
provider hashes, and update-check results are session-only API data. They are neither written to the normal
catalog cache nor exposed as offline results. The following smallest installed-state evidence is retained because
it is needed to identify and recover the local installation:

- exact numeric project, client-file, and installed/server-file identities;
- a fixed origin value distinguishing API-derived operational identity, archive-manifest identity, and a
  user-entered reference;
- Minecraft/loader compatibility needed to launch the installed server;
- locally computed SHA-256 values, installed byte sizes, relative ownership paths, and local snapshot manifests;
- fixed local operation status and counts that do not reproduce provider response labels or descriptions.

Update downloads retain their local SHA-256, byte count, and local status, but not the CurseForge URL, filename,
version label/ID, or provider digest. CurseForge recovery snapshots retain exact numeric rollback identity and
local file hashes but not provider release labels, source URLs, changelogs, update notes, or provider-derived filenames.
Generated-pack evidence derives its durable size from the installed local file and records counts instead of
provider project lists or override-path response detail. Legacy snapshots lacking an exact installed file identity
fail closed instead of borrowing the currently installed identity.

This installed-state exception is a safety interpretation, not written provider approval. Public activation
remains gated until CurseForge confirms that retaining this minimum local rollback/ownership evidence is permitted.

## Live CurseForge verification checkpoint — 2026-08-29

**VERIFIED:** the authorized candidate was accepted by official Minecraft game identity `432` and imported into
isolated DPAPI storage. Live requests then covered categories, 5,007 modloader rows, 103 Minecraft versions,
modpack search, two provider pages, exact project/file detail, official server-pack relationship, project and
exact-file links, an approved exact-file CDN URL, a mod exact file with one required dependency, and bounded
not-found mapping. Five controlled searches measured p50 `27.8 ms` and max `229.4 ms`; exact detail measured
`206.6 ms`. These are one Internet session, not deterministic performance guarantees. No persistent provider
cache was used.

**UNAVAILABLE at this checkpoint:** no live `429` was induced, no naturally occurring distribution-disabled file
was selected, and the approved CDN URL was not used for a complete real archive transfer, server launch, mod
installation, update, or rollback. Those categories cannot be marked verified from deterministic fixtures. An
unrelated Windows Security dialog for a Prism Launcher/OpenJDK/Minecraft process covered the packaged UI; Codex
did not interact with that dialog or process, and the live runtime campaign stopped without downloading an
archive or mod payload.

**VERIFIED cleanup:** the exact candidate App and Agent were resolved by executable path and stopped; UI-death
cleanup completed with zero candidate-owned processes and zero listeners on the isolated port. Subsequent
packaged Agent and normal-close harnesses use isolated data and an explicitly missing fixture-local key path, so
they cannot authenticate with the approved source by inheritance.

Public key sharing and the minimum-installed-state permission described above remain unavailable without written
CurseForge authorization. No key is committed, packaged, placed in React state, or accepted through an ordinary
user field.

## Headless CurseForge runtime continuation — 2026-08-30

This continuation intentionally used no foreground UI, browser control, mouse, keyboard, or dialog automation.
The exact development-package Agent and controller authenticated through the native credential path,
recovered only isolated DPAPI state, resolved live metadata, downloaded real provider payloads beneath fresh
task-owned roots, materialized official Fabric server packs with exact managed Java, and observed Minecraft
readiness through the packaged named-pipe workflow.

More FPS project `531644` and Optimized Performance project `1172292` each supplied exact Minecraft 1.21.1 Fabric
client/server relationships with distribution allowed and Java 21. Full runs reached Minecraft `Done` but then
failed closed as `FailedNothingChanged`: stable Job-owned endpoint inventories found outbound HTTPS connections
from the staged Java process to public IPv4 addresses on remote port 443. They were not wildcard Minecraft
listeners. The current certification contract permits only the exact selected loopback Minecraft TCP port while
the server is running; outbound TCP, wildcard binding, UDP, secondary ports, unstable identity, and unreadable
evidence remain prohibited. The contract was not relaxed to force a positive result.

No failed run was registered or promoted. Every exact Agent exited with code `0`, exact root/process identity
checks passed, and the final Jobs contained zero processes. Exact run roots are absent. Cleanup reports remain
truthful when Windows removes a root without returning a recoverable Recycle Bin item, and aborted campaigns do
not claim final selected-port absence because that postcondition was not reached.

The bounded ledger records `990,582,328` bytes guarded, `218,797,028` completed, and seven conservative unknown
reservations totaling `771,785,300` bytes, leaving `1,156,901,320` bytes under the 2 GiB cap. Zero
observed-incomplete bytes does not convert unknown reservations into completed transfers.

One live unavailable-project lookup also proved that JSON `null` can be a valid `ResolveCatalogProject` result.
Both named-pipe clients now recognize JSON null only for the exact nullable catalog and plug-in release contracts;
all unrelated missing payloads still fail as malformed responses. This corrects error classification without
inventing provider availability. The exact-HEAD packaged metadata rerun returned truthful project/file not-found,
reserved no payload, exited both Agent Jobs cleanly, and recycled its fresh run.

Because strict official-pack validation stopped the campaign, generated-candidate, mod/dependency, update,
rollback, and controlled-recovery workflows remain unverified. Actual packaged WebUI and visual/keyboard checks
were also not exercised during this headless continuation.
