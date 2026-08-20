# Architecture

ChunkPilot 1.3 keeps the original four-project architecture:

- `ChunkPilot.App` is the WPF MVVM shell. It renders snapshots and streamed operation state, collects explicit user decisions, and sends typed requests. It never owns Minecraft processes or provider implementations.
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

Cancellation is honored before activation. After the directory switch begins, ChunkPilot finishes transaction finalization, startup validation, or rollback before returning control. Raw process output enters a bounded sequence buffer and rolling ISO-timestamped local log.

## Managed installation transaction

An installation operation is recorded in SQLite before file mutation. Inputs are copied or downloaded into a same-volume `.chunkpilot-staging-<operation>` directory. ZIP entries are normalized and rejected if they escape staging. Official hashes are verified when supplied. Launch detection and required files are validated before `Directory.Move` atomically promotes the directory into the managed root.

Only after a deliberate EULA interaction does the installer write `eula=true` and record the timestamp/reference in SQLite. Registration happens after promotion. A local managed package records a local-package source identity and installed hash baseline.

Fabric, Quilt, Forge, and NeoForge use `LoaderMetadataService` and `LoaderInstallationService`. Official metadata/checksum sidecars are resolved outside ViewModels. Installers run in operation staging with an absolute Java executable and `ArgumentList`; output is captured, time-bounded, and retained. Generated `win_args.txt` or direct launcher JARs become non-detaching profiles; downloaded batch files are never executed.

Managed Java uses an `IManagedJavaPackageProvider`. The Temurin adapter requires official SHA-256, extracts through traversal-safe code, health-checks x64/version evidence, and persists per-server absolute paths under app data without changing the Windows Java environment.

Crossplay packages use `ICrossplayPackageProvider`. Installation is stopped-server-only, capability-gated, backed up, hash-verified, and serialized under the canonical server-root lock. ChunkPilot records relative ownership paths and versions so removal can move only its own Geyser, Floodgate, and ViaVersion JARs into Recovery while preserving generated configuration.

Datapack installation is bound to a selected world containing `level.dat`, validates `pack.mcmeta`, backs up before mutation, stages files uniquely, and records the final content hash. Resource-pack settings require HTTPS and a valid SHA-1, then update `server.properties` atomically with recovery.

## Server-pack update transaction

Provider code is behind `IUpdateProviderAdapter`; ViewModels use only typed agent operations. `UpdateSourceDetector` trusts explicit ChunkPilot manifests and recognized launcher/provider IDs, never folder names or mod similarity.

`ServerPackUpdateService` writes an operation journal, checks both snapshot/cache and server-volume free space, creates a full compressed snapshot with per-file SHA-256 data, downloads or reuses a content-addressed cache file, verifies the strongest provider digest, and extracts through traversal-safe code. `PackMigrationPlanner` copies worlds, player data, server properties, access lists, JVM settings, icons, user files outside pack-managed locations, and explicitly marked persistent paths. The new pack remains authoritative for mods, libraries, scripts, defaults, and pack configuration; removed JARs become explicit conflicts and remain in the rollback snapshot. An unresolved plan returns before activation. The user can select the old file, the new baseline, or supply complete merged text for a bounded text configuration file; the second staging pass records and applies those choices.

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

`docs/NETWORKING.md` is the authority on which protocol features are implemented, what is deliberately
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

## Packaging

The repository produces framework-dependent, self-contained win-x64, and portable-test layouts. Inno Setup consumes the self-contained layout and reuses the stable AppId for upgrades. Default uninstall removes application binaries and registration while preserving settings, provider links, encrypted keys, version snapshots, backups, managed instances, and every imported external server.
