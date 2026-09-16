# Architecture

ChunkPilot keeps the original native authority layers, with a locally bundled WebUI:

- `ChunkPilot.App` is the native WPF/WebView2 host and typed bridge. The bundled `ChunkPilot.WebUi` React interface renders snapshots and operation state; native dialogs collect sensitive or explicit decisions. Neither UI layer owns Minecraft processes or provider implementations.
- `ChunkPilot.Agent` is the hidden per-user control process. It owns process trees, stdin/stdout/stderr, lifecycle serialization, installation/update coordination, data-operation locks, schedules, backups, recovery, and live commands.
- `ChunkPilot.Core` contains protocol records, immutable domain/update models, launch and update policy, validation, scheduling, redaction, quoting, and bounded console state.
- `ChunkPilot.Infrastructure` contains SQLite, official install/update providers, transactional installation and server-pack activation, source detection, snapshot/migration services, safe files, backups, world/icon/whitelist/RAM services, statistics, status queries, jar metadata, and diagnostics.

## Local process boundary

The UI and agent exchange length-bounded newline JSON over a current-user-only Windows named pipe. Production pipe and mutex names remain stable for in-place upgrades. Tests use temporary app-data and server roots and never connect to an installed real server.

There is no inbound TCP control listener, embedded web server, account system, or telemetry. Enabled checks make bounded outbound HTTPS requests only to each server's linked provider and respect the configured interval. The agent is a normal per-user executable rather than a Windows service.

Normal WM_CLOSE is never cancelled: the UI writes `SafeApplicationExit` over a short one-way pipe handoff and exits. Independently, the Agent holds the UI's exact Windows process identity (PID plus raw creation `FILETIME`) and observes `Alive`, `Gone`, or `Unknown`. A pipe disconnect or missing heartbeat is not a death policy. Definite process loss acts promptly; one monotonic deadline bounds an unprovable identity; a failed observation pass cannot permanently stop monitoring.

Both deliberate close and proven UI-process death atomically seal that Agent lifetime against replacement registration, revoke an exact snapshot of in-memory per-server public-connectivity generations, invalidate external verification, begin generation-fenced exact-owned public-route cleanup, and run the authoritative server-native save/graceful-stop/bounded exact-process escalation pipeline for every managed server. Exit cancellation is offered to the active transactional operation; the lifecycle gate has a bounded acquisition deadline and is retried while renewal remains prohibited. The Agent exits only after no exact managed process remains and router cleanup is complete or truthfully bounded as pending. Minimize/tray keeps the UI process and hosting alive. This contract does not claim cleanup after simultaneous App-and-Agent termination or machine power loss.

The App-to-Agent session capability is random, Agent-minted, memory-only, and bound to the exact UI process. Public exposure authority is a separate generation per server and Agent lifecycle epoch. The exact identity is carried through router serialization and revalidated immediately before persistence and wire mutation; old cleanup cannot touch a different generation. Named-pipe possession, persisted intent, an old router record, or a durable firewall rule cannot manufacture or inherit a lease after Agent restart. Startup loads restoration intent but starts nothing until stale persisted public state has been treated as cleanup-only and its associated managed listener has reached a proven safe terminal decision. Storage failure leaves restoration inert.

## Process and operation ownership

Each `ManagedServer` has a lifecycle state machine and a single operation semaphore. Start, save, stop, restart, backup, restore, world switch/import/export, jar mutation, pack update, and version rollback pass through coordinated agent operations. An update cannot overlap another lifecycle or data operation.

Supported Minecraft ecosystems receive exactly one `nogui` token before launch. The final process uses `UseShellExecute=false`, redirected standard streams, and `CreateNoWindow=true`. Newly launched process records include PID plus raw Windows creation `FILETIME`, executable and existing provenance. Automatic detached termination requires an exact no-tolerance match; a legacy record without raw creation identity remains truthful recovery evidence but never kill authority. Detached `start` scripts and `javaw` are diagnosed instead of hidden after launch.

Every new managed server attempt is born atomically inside its own Windows Job through
`OwnedServerProcess`. Job membership retains descendants after a launcher or intermediate parent
exits; stop, force recovery, restart admission, and data-operation guards use its live-process count
and verified empty state. User-approved raw Windows arguments and configured stream encodings are
preserved. Provider staging still requires structured arguments through its separate strict entry
point. Normal application close keeps the Job open through the Agent's save and graceful-stop path.
Forced Agent termination closes its Jobs and terminates remaining descendants; this crash path
cannot promise a graceful save. Legacy detached records retain their existing exact-handle recovery
path; they do not acquire retrospective Job membership or authority from a PID alone.

Minecraft status queries have one three-second deadline covering connection, request, response reads,
and protocol fallbacks. A silent socket reports unavailable status. Caller cancellation remains
cancellation, and starting a new attempt waits at most five seconds for the old console and monitor
tasks to finish; a timeout leaves recovery required and starts no second process.

Cancellation is honored before activation. After the directory switch begins, ChunkPilot finishes transaction finalization, startup validation, or rollback before returning control. Raw process output enters a bounded sequence buffer and rolling ISO-timestamped local log.

## Managed installation transaction

An installation operation is recorded in SQLite before file mutation. Inputs are copied or downloaded into a same-volume `.chunkpilot-staging-<operation>` directory. ZIP entries are normalized and rejected if they escape staging. Official hashes are verified when supplied. Launch detection and required files are validated before `Directory.Move` atomically promotes the directory into the managed root.

Only after a deliberate EULA interaction does the installer write `eula=true` and record the timestamp/reference in SQLite. Registration happens after promotion. A local managed package records a local-package source identity and installed hash baseline.

Fabric, Quilt, Forge, and NeoForge use `LoaderMetadataService` and `LoaderInstallationService`. Official metadata/checksum sidecars are resolved outside ViewModels. Installers run in operation staging with an absolute Java executable and `ArgumentList`; output is captured, time-bounded, and retained. Generated `win_args.txt` or direct launcher JARs become non-detaching profiles; downloaded batch files are never executed.

Managed Java uses an `IManagedJavaPackageProvider`. The Temurin adapter requires official SHA-256, extracts through traversal-safe code, health-checks x64/version evidence, and persists per-server absolute paths under app data without changing the Windows Java environment.

CurseForge transport authentication stays inside Infrastructure. Optional personal setup validates a key
entered in the native password dialog and protects it with Windows DPAPI. Development provisioning reads
a local file only when `CHUNKPILOT_CURSEFORGE_KEY_FILE` explicitly supplies its path; normal startup never
searches for a developer credential. Service-enabled builds instead call the configured private backend,
whose application key stays in its host secret binding. No credential is returned in Agent protocol
models or React state. `CurseForgeApiClient` is the single
official API/CDN HTTP boundary: HTTPS/host allowlists, no cookies or redirects, connect/total timeouts,
bounded JSON, typed status mapping, cancellation, and in-flight request coalescing are enforced there.
Persistent CurseForge API-response caching is disabled under the currently reviewed provider terms.
The provisioner decodes only the populated span of one bounded byte allocation and zeroes that same complete
allocation in `finally`. The App captures the optional path-only bootstrap override once, removes it from its
process environment immediately, and places it only on the exact non-shell Agent start. The captured reference
is discarded when an existing Agent answers or after that child starts, so later browser, shell, and elevated
helper launches cannot inherit the source path. Processes that execute managed server content, downloaded
Java/loader code, approved automation programs, staged candidates, or runtime certification start from a bounded
Windows/Java environment allowlist; explicit server variables are applied afterward and the CurseForge
source-path variable is removed.

Official CurseForge server packs and generated candidates reuse the managed installation transaction. The
generated path accepts only an exact CurseForge manifest, recursively resolved required file relationships,
provider size/SHA-1, locally recorded SHA-256, the narrow `config`/`defaultconfigs` override set, and an exact
official loader install. Native preflight seals the full ordered required-dependency graph and optional-review
evidence into an Agent-lifetime plan. A short-lived authorization binds that plan and the official/client
artifact evidence to one exact creation request; it expires, is consumed once, and rejects caller-injected plan
data or replay. Cancellation and selection replacement revoke the exact review, including cancellation that
wins before a slow preflight registers. Both paths must pass a loopback-only staged launch, readiness/status
check and clean stop before promotion. Arbitrary pack scripts are never executed.

Creation acceptance is separate from transport acknowledgement. The App generates the operation ID before the
named-pipe request. If the begin response is lost, times out, is cancelled locally, or cannot be decoded, it asks
the Agent whether that exact ID was accepted before deciding to replay. An explicit miss may replay only the same
request and ID. `InstallationCoordinator` serializes acceptance, records the complete client-controlled plan
identity, consumes the one-use authorization at most once, and returns the existing operation for an exact
replay; any changed field or collision fails closed. Cancellation discovered after acceptance is sent to that
authoritative operation instead of being mistaken for proof that creation never started. Cancellation itself is
serialized under the same Begin gate with the complete request: it either cancels accepted work or reserves the
operation ID as a terminal pre-start tombstone before a delayed Begin can consume authority. These externally
controlled exact-operation paths reject `Guid.Empty` instead of silently substituting another identity.

CurseForge mod/dependency installation follows the same ownership rule through a separate registry. The Agent
deep-copies and digests the ordered exact plan, binds it to one server/provider/root-project/root-file tuple, and
returns a short-lived public authorization identity with the review. Installation consumes the stored plan once
before operation state is created, revalidates compatibility and inventory, and downloads only those exact
releases without resolving provider metadata a second time. Wrong-server, replaced, expired, altered-digest and
replayed requests fail closed. Other provider paths keep their existing contracts.

Managed-content begin uses the same exact-operation reconciliation boundary. Agent acceptance is serialized with
one-use plan consumption and retains the original server, provider, project, release, dependency mode, restart
choice, and authorization identity. A lost acknowledgement is resolved through a non-throwing exact-ID lookup;
only an explicit miss permits replay of that same request. The App validates every identity field in a returned
snapshot, and the Agent rejects a reused operation ID whose request differs. Managed-content cancellation uses
the same atomic cancel-or-tombstone contract as creation.

Each coordinator retains at most 4,096 pre-start tombstones. Reaching that bound atomically seals previously
unseen Begin registrations for the remainder of that Agent lifetime instead of returning a permanently
unestablishable fence. Exact identities retained before the seal still require their original request; an unseen
Begin is rejected before authorization consumption, while cancellation can revoke that request's unconsumed
authorization and return synthetic terminal-cancelled evidence without retaining another operation object. If the
Agent itself remains permanently unavailable after an ambiguous dispatch, the App deliberately leaves
cancellation pending and continues reconciliation because no terminal fence can be proven.

Windows destination identity is case-insensitive. A reviewed plan cannot assign the same destination JAR name to
multiple releases under different casing. Inventory and every add-on mutation walk the complete active
`mods`/`plugins`, disabled, Recovery, provenance, and configuration path chain without following reparse points;
junctions, directory substitutions, and ownership-uncertain components fail closed. Before download and again
under the content lock immediately before activation, installation refuses an existing path unless its unchanged
authoritative provenance and content baseline belong to the same provider project. A newly appeared file or
changed same-project baseline is preserved in place, no foreign provenance is written, and no refused file is
moved to Recovery. The same rule applies to exact multi-release plans and ordinary single-release installs.
ChunkPilot's lock serializes its own operations, but unrelated processes remain able to race the final check and
filesystem mutation: this user-mode path has no single atomic conditional replace that also proves same-file
identity and a no-reparse chain.

Crossplay packages use `ICrossplayPackageProvider`. Installation is stopped-server-only, capability-gated, backed up, hash-verified, and serialized under the canonical server-root lock. ChunkPilot records relative ownership paths and versions so removal can move only its own Geyser, Floodgate, and ViaVersion JARs into Recovery while preserving generated configuration.

Datapack installation is bound to a selected world containing `level.dat`, validates `pack.mcmeta`, backs up before mutation, stages files uniquely, and records the final content hash. Resource-pack settings require HTTPS and a valid SHA-1, then update `server.properties` atomically with recovery.

## Server-pack update transaction

Provider code is behind `IUpdateProviderAdapter`; ViewModels use only typed agent operations. `UpdateSourceDetector` trusts explicit ChunkPilot manifests and recognized launcher/provider IDs, never folder names or mod similarity.

`ServerPackUpdateService` writes an operation journal, checks snapshot/cache/server-candidate storage by exact
volume, creates a full compressed snapshot with per-file SHA-256 data, downloads or reuses a content-addressed
cache file, verifies the strongest provider digest, and extracts through traversal-safe code. Requirements that
share a volume are added before comparison; distinct volumes are evaluated separately and arithmetic saturates
instead of overflowing. The initial forecast reserves the exact package and download margin. Once the exact
package is locally available and hash-verified, a bounded central-directory inspection sums its declared expanded
file sizes and rejects encrypted, unsupported, unsafe, link-like, colliding, excessive, or oversized entries.
Before snapshot or active-server mutation, the final forecast combines that verified archive expansion with the
current-server recovery snapshot and every required generated-candidate payload and staging copy on their real
volumes. The former compressed-size multiplier is not an authorization for bounded archive staging. Volume
identity is resolved from the nearest existing target through its final Windows mount path to the authoritative
volume GUID; a drive letter alone is not used, and unresolved or network-backed targets fail closed. A
download-only update also reserves the exact package size before network transfer. `PackMigrationPlanner` copies
worlds, player data, server properties, access lists, JVM settings, icons, user files outside pack-managed
locations, and explicitly marked persistent paths. The new pack remains authoritative for mods, libraries,
scripts, defaults, and pack configuration; removed JARs become explicit conflicts and remain in the rollback
snapshot. An unresolved plan returns before activation. The user can select the old file, the new baseline, or
supply complete merged text for a bounded text configuration file; the second staging pass records and applies
those choices.

Forecast, version snapshot, and migration share one bounded metadata inventory of the complete current server
tree. The inventory is cancellation-aware, caps the tree at 500,000 entries, compares relative paths with Windows
case-insensitive semantics, and never follows a reparse point. Unsafe, inaccessible, missing, redirected, or
case-colliding components fail before snapshot/migration output. Each inventoried file's size, last-write identity,
and no-reparse ancestor chain are checked again before it is read; snapshot also bounds streamed length and
revalidates the entry after reading. This is bounded observation rather than a filesystem freeze; unrelated
external mutation outside those checks remains possible.

For CurseForge, both download-only and install paths perform operation-time native client-manifest preflight
before free-space evaluation, snapshot, reviewed-download reuse, ordinary cache lookup, or network download. The
linked numeric project and selected client/provider file IDs define the expected official/generated relationship.
After exact tuple validation, preflight replaces the request URL, size, SHA-1, applicable local client SHA-256,
safe filename, package type, platform and Java identity; unrelated hashes and caller-declared file lists are
cleared. Generated updates additionally carry a validated native dependency plan that is excluded from JSON in
both directions and used directly during materialization. Renderer-supplied operational fields or a generated
plan are therefore never update download authority.

CurseForge installations add `.chunkpilot/provider-owned-files.json`, a local SHA-256 baseline for files
materialized from the exact provider release. During updates, an unchanged obsolete provider file may leave
with the retired pack, while a locally modified provider file is preserved and surfaced for review. Explicit
`NewBaseline` is required to replace/remove that modified file. The exact project ID/slug, client file ID,
official server-pack file ID when present, loader and Minecraft version remain distinct persisted identities.

The candidate launch profile must be controllable and non-detaching. The existing root is renamed to a same-volume retained sibling and the candidate is renamed into place. The retained sibling and operation journal stay until the agent observes console readiness and completes a local Minecraft status handshake.

If validation or transaction finalization fails, the failed process tree is stopped, the verified snapshot is extracted and rehashed, the old definition/source is restored, and the old version is restarted when appropriate. A failed candidate is never left active in version metadata.

On agent startup, an incomplete `ServerPackUpdate` journal is recovered before server definitions are loaded. A retained pre-update sibling is restored; a pre-switch candidate is removed. Missing active and previous directories are reported as a hard recovery error rather than guessed.

## Router port mapping

Router control is layered like every other provider. `ChunkPilot.Core` holds only provider-neutral
models and rules — transport, mechanism, phase, failure, address classification, ownership proof and
lease timing — and knows nothing about SOAP or datagram layouts. `ChunkPilot.Infrastructure` holds the
three protocol implementations behind `IRouterMappingProvider`, their transports
(`IGatewayDatagramChannel`, `ISsdpSearchChannel`, `IUpnpControlChannel`) and the deterministic,
sequential selection in `RouterMappingService`. The Agent's `RouterMappingCoordinator` owns intent,
consent, persistence, ownership evidence and every mutation; `RouterMappingWorker` renews and
reconciles on a bounded periodic pass. ViewModels contain no protocol code and consume the Agent's
authoritative `RouterMappingState`.

Every mutation for one server runs under that server's own gate with an operation id, so start,
stop, enable, disable, renewal and reconciliation serialize. Production router operations additionally
carry exact lease/generation/Agent-epoch authority and revalidate it under this gate before every
durable or wire mutation. A cancelled or revoked operation's result is discarded rather than written,
so stale completion and old cleanup cannot overwrite or remove newer state.

Gateway selection reuses `LanAddressSelector`'s interface rule, so the fix that stopped a VPN adapter
being labelled the local network also stops it being offered to a router as a forwarding target; on
top of that the gateway must sit in the same IPv4 subnet as the chosen address.

`docs/operations/NETWORKING.md` is the authority on which protocol features are implemented, what is deliberately
not implemented, and what ChunkPilot is allowed to claim about reachability.

## Data and migrations

Application state defaults to `%LOCALAPPDATA%\ChunkPilot`; managed Minecraft data defaults to `%USERPROFILE%\ChunkPilot\Servers`. Imported folders remain authoritative and are only referenced by database records.

Schema v4 migrates v1-v3 databases in place. It retains every existing table and adds the v3 update/version tables plus:

- `update_sources` for provider/project IDs and the installed comparison baseline.
- `update_checks` for timestamped provider results.
- `update_downloads` for source URLs, file/build IDs, provider digests, local SHA-256 values, sizes, and outcomes.
- `version_snapshots` for active/rollback identity, health, retention, manifest paths, and launch definitions.
- `migration_decisions`, `rollback_history`, and `update_preferences`.
- capability, preset, catalog history/favorite, and managed-Java tables;
- network, tunnel, crossplay, access, gamerule, datapack, resource-pack, automation, sharing, and diagnostic tables;
- `router_mappings` for per-server Direct internet intent and the minimum evidence needed to prove a router mapping is ChunkPilot's own. It is a table of its own rather than a field on the network configuration, because the App replaces that record wholesale when the user saves a connection method, which would discard Agent-owned ownership evidence while a real mapping was still open. Persisted intent is not authority: only the current in-memory public-connectivity lease may create or renew exposure, and a new Agent treats old records as cleanup-only evidence.
- process identity, UI session/close intent, previous running state, and file-operation events.

The migration uses `CREATE TABLE IF NOT EXISTS`; it does not rebuild or delete existing servers, backups, schedules, settings, or history. Configuration writes use atomic replacement and recovery copies. Backups, version snapshots, and world shares carry SHA-256 manifests. Snapshot cleanup operates only on inactive owned archives and is gated by healthy-active, verified-backup, retention, and last-usable-version checks.

Provider secrets are kept outside SQLite in `%LOCALAPPDATA%\ChunkPilot\secrets.dat`; values are encrypted with DPAPI `CurrentUser` scope and never returned through the agent API.

## UI and performance

WPF resources are split into Colors, Typography, and Controls dictionaries. The Overview has one compact update card, global status uses one Update Center, and per-server history/rollback uses one Version Manager. Advanced provider and unattended controls use expanders. The most recently selected server and tab are restored.

Update progress is polled from the agent without blocking its process. Normal timestamps use 12-hour AM/PM formatting; raw server logs retain their original/ISO timestamps. Console collections are virtualized and bounded; host static data is cached, storage is sampled on a slower interval, and network throughput uses lightweight counters.

`ServerCapabilityProfile` is the single adapter boundary for Java/Bedrock, plugin/mod, content, gamerule, world, update, and crossplay applicability. Views bind to capabilities rather than repeating loader-name checks. Quick-start presets are immutable, reviewable policy output; Advanced still exposes the effective launch profile.

## Development certification boundary

A development package is eligible for headless runtime certification only when the package-affecting `HEAD`,
index, and actual worktree blob digests match exactly. The proof includes staged, untracked and unexpected ignored
source plus files hidden with `skip-worktree` or `assume-unchanged`. Eligible output is built from an isolated
`git archive` of that commit. The manifest binds the commit and input digests to the size and SHA-256 of every
packaged file, including the controller under `artifacts\dev-current\Certification`; certification never executes
a persistent `bin` or `obj` controller.

Each certification invocation creates one fresh marked run containing exactly `data`, `servers`, and `temp`.
`TEMP`, `TMP`, and .NET bundle extraction are scoped to that temp tree for the packaged controller. Final cleanup
refuses unexpected entries and reparse points, inventories the bounded full run, and moves only the exact run root
to the Windows Recycle Bin with no permanent-delete fallback, unless `--retain-stopped-run` explicitly retains
the task-owned run for recovery/acceptance. Exact Job exit and selected-port absence are separate terminal facts.

While a task server is running, the certifier takes two complete owner-PID inventories of TCP states/endpoints and
UDP endpoints in IPv4 and IPv6 across three stable Job snapshots taken before, between, and after them. The Job
accounting
and the exact PID/creation set must remain unchanged at all three boundaries, and the exact listener/UDP binding
multiset must match across both inventories. Every accepted owner is revalidated live against its creation
identity; inbound candidates also require the task-server subtree. The task root, inbound endpoint owner, and every intermediate ancestor belong to
that stable Job PID/creation set, must still match its live raw creation identity, and each parent must have been
created strictly before its child; missing, exited, cyclic, reordered, or PID-reused ancestry fails closed.
`StartupNetworkPolicy` is shared with production: require the expected exact local game listener, block
non-loopback TCP listeners, and return cannot-verify for unresolved non-loopback UDP or ownership. Non-listening
TCP observations are separate; Established alone proves no direction or purpose. Additional loopback bindings
are recorded, not mistaken for public exposure. This is an observable inbound-startup check, not an air gap or
security certification. Validation uses an exact-owned disposable world, restores configuration only after
Job-empty proof, and preserves failure evidence outside mutable staging. Each complete inventory still
consists of four sequential Windows tables; repeated stable observation detects mutations that persist into an
inventory, but cannot exclude a socket opened and closed entirely between observations or after the final one.

## Packaging

The repository produces framework-dependent, self-contained win-x64, and portable-test layouts. Inno Setup consumes the self-contained layout and reuses the stable AppId for upgrades. Default uninstall removes application binaries and registration while preserving settings, provider links, encrypted keys, version snapshots, backups, managed instances, and every imported external server.
