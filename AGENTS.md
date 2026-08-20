# ChunkPilot — Codex Project Instructions

## Source of truth
Work in `D:\ChunkPilot`. The repository, current branch, migrations, tests, release notes, and generated artifacts are authoritative. This file defines durable product and engineering rules; task prompts define the current milestone. When instructions conflict, protect user data and follow the newest explicit task requirement.

At the time this file was written, ChunkPilot 1.2.0 was complete on `feature/chunkpilot-1.2-version-manager` at commit `0d52a30`, and 1.3 work was queued or in progress. Never assume that remains current: inspect Git and version metadata before every task.

## Mission
Build the best local-first Minecraft server launcher and manager for ordinary Windows users: easy to install, lightweight, dependable, understandable, and safe. A beginner should be able to choose Vanilla, a performance server, a modpack, plugins, or crossplay and have a working server quickly without learning Java, JARs, launch scripts, or router terminology. Advanced users must retain full control through progressive disclosure.

Product identity:

> Pick what you want to play. ChunkPilot handles the annoying server work, shows what changed, and makes dangerous operations reversible.

Do not turn ChunkPilot into a commercial hosting panel, browser dashboard, billing system, Docker manager, telemetry product, or cloud-dependent service.

## Non-negotiable principles
1. **It just works.** Prefer guided choices, validated defaults, managed dependencies, and plain-language errors.
2. **Worlds are irreplaceable.** Never risk, overwrite, delete, or silently migrate world data.
3. **Every major change is traceable and reversible.** Back up, stage, verify, activate, validate, and retain rollback.
4. **Truth over confidence theater.** Show confirmed, likely, possible, unknown, or unavailable. Never invent compatibility, player counts, TPS, public reachability, or update identity.
5. **Chameleon UI.** Show only features relevant to the selected server’s actual capabilities.
6. **Native and lightweight.** Keep WPF/.NET and the background agent. Avoid Electron, a local web dashboard, Docker, embedded Python, heavy frameworks, unlimited buffers, and constant remote polling.
7. **Local-first and private.** No accounts, telemetry, ads, or remote services unless explicitly optional and enabled by the user.
8. **Beginner first, expert complete.** Common actions stay obvious; advanced controls remain available but collapsed.
9. **No fake functionality.** Hide incomplete controls.
10. **Polish follows reliability.** Never sacrifice safety or correctness to ship visual features.

## Repository and concurrency safety
Before editing:
1. Inspect the current branch, `git status --porcelain`, recent commits, version metadata, running build or test processes, and active Codex work.
2. Do not modify a dirty repository owned by another task.
3. Do not start a new phase until the previous phase is committed, tested, packaged, and clean.
4. Create a new feature branch from the latest completed release commit unless the task explicitly says otherwise.
5. Create a recovery commit before broad or risky changes.
6. Never test against or modify a real user server folder.
7. Use temporary fixtures, copied databases, fake servers, and mocked provider responses.

Do not mass-format, rename, regenerate, or reorganize unrelated files. Preserve completed behavior unless integration, safety, or a proven defect requires a change.

## Architecture invariants
Preserve the existing layered native architecture:
- `ChunkPilot.App`: WPF UI only.
- `ChunkPilot.Agent`: hidden per-user process that owns server processes, schedules, backups, updates, and long-running operations.
- `ChunkPilot.Core`: models, state machines, interfaces, and rules.
- `ChunkPilot.Infrastructure`: providers, persistence, filesystem, process, network, and platform services.
- SQLite stores ChunkPilot metadata and history outside imported server folders.
- Named pipes are the default local App-to-Agent transport.
- Server lifecycle state and lifecycle intent are separate.
- One serialized operation queue exists per server.
- Long operations have operation IDs, cancellation, checkpoints, journals, timeouts, and recovery paths.
- Providers are behind interfaces. ViewModels must not contain provider-specific networking or loader detection.
- Capability detection is centralized in a strongly typed `ServerCapabilityProfile`.
- All mutable paths are canonicalized and coordinated by shared file-operation locks.
- Console pipelines are bounded, batched, virtualized, reconnectable, Unicode-safe, and independent from UI rendering.
- The agent must operate without the UI open.

Do not expose a local TCP API unless a dedicated, security-reviewed remote-management phase explicitly requires it.

## Data-safety rules
- Import is read-only and by reference unless the user explicitly chooses a managed copy.
- Managed instances live in persistent user data, never the application binary directory.
- Never automatically accept the Minecraft EULA. Require an unchecked explicit agreement control.
- Never automatically enable RCON, query, UPnP, firewall rules, port forwarding, or public access.
- Never run arbitrary downloaded scripts without review.
- Downloads and installations use unique staging directories, verification, transactional finalization, and cleanup or recovery.
- Writes to important text and configuration files are atomic and preserve comments, ordering, encoding, BOM, and line endings where practical.
- Backups are `.partial` until complete, verified, and atomically finalized.
- In-progress or failed backups cannot be restored, exported, or presented as complete.
- Restore, update, migration, world switch, or destructive content change requires a verified recovery point.
- Active worlds are separate from server-version snapshots.
- The active or only usable server version cannot be deleted.
- Version cleanup must never delete the current or separately managed world.
- Plugin or mod removal defaults to Recovery or Recycle Bin and lists exact paths. Never delete ownership-uncertain files.
- Use hashes and metadata IDs, not filenames alone, to reconcile add-ons.
- Never create duplicate active versions of the same mod or plugin ID without explicit approval.
- Never silently retain obsolete pack-managed JARs or delete user-added JARs.
- Secrets use Windows DPAPI and must be redacted from logs and diagnostic bundles.
- Uninstall preserves imported servers, managed servers, worlds, and backups by default.

## Process, close, and crash behavior
Normal UI close, Alt+F4, taskbar **Close window**, or Exit:
- Never block with a modal confirmation.
- Immediately close the UI and remove its tray icon.
- Send `SafeApplicationExit` to the agent.
- The agent performs save confirmation, stop, process-tree exit, and then exits.
- Do not leave an invisible `ChunkPilot.App` process.

Unexpected UI crash or forced UI termination:
- The agent proves the UI ended from its exact process id plus raw kernel creation identity, not from a
  pipe disconnect or heartbeat timeout.
- Immediately revoke every public-connectivity lease, stop renewal, and invalidate external verification.
- Begin exact-owned router/tunnel cleanup and safely save and stop every managed server through the
  normal per-server lifecycle queue.
- Exit the agent only after exact managed processes/listeners are gone and public cleanup has reached a
  truthful bounded state. If exact process termination cannot be proved, retain the agent and the failure
  evidence rather than declaring success.
- A live minimized or tray-resident UI keeps hosting. A replacement UI inherits no public lease.

Public connectivity is lease-owned and never inherited:

- One authenticated App session may create independent per-server leases. Every mutation proves the
  current capability, server, operation and lease generation; stale or replaced generations fail before
  persistent intent, router, firewall, renewal, or probe mutation.
- A restarted agent starts with no actionable lease. Persisted intent, consent, router evidence, and a
  firewall rule are cleanup/configuration evidence only; they never reopen public access.
- Exact-owned router/tunnel exposure is withdrawn through its ownership-proving removal path. Foreign or
  uncertain exposure is never deleted; failed removal remains pending and is never renewed or recreated.
- Windows Firewall access is durable exact server configuration, not a lease and not public-route proof.
  It may remain after shutdown and is inert for a stopped managed listener. There is no window guardian,
  WFP lifetime session, privileged service, or post-death UAC path.

Permanent invariant:

> When the actual UI process ends, ChunkPilot revokes public leases, safely stops all managed servers,
> and exits the Agent after bounded cleanup. Minimize/tray keeps hosting. Durable exact firewall
> configuration may remain; it is not proof of an active listener or public route.

See `docs/NETWORKING.md` and `docs/WINDOWS-FIREWALL-ACCESS.md`.

Manual server stop must suppress crash recovery and stale scheduled restart. Safe Restart permits exactly one intentional restart. Process reattachment must verify PID, start time, executable, working directory, and command signature; PID alone is never sufficient.

## UX rules
Use the existing dark WPF design system unless a dedicated design milestone says otherwise.
- Professional purple and blue-black appearance derived from the ChunkPilot icon.
- Consistent themed controls, popups, context menus, tooltips, icons, spacing, focus states, and accessibility.
- No white default WPF dropdowns.
- Plain language first; technical details under **Advanced** or **More details**.
- Common actions use clear icon-and-text labels.
- Preserve the user’s place, most recently selected server and tab, and active operation.
- Use live progress for downloads, installs, updates, backups, exports, and diagnostics.
- Console follows output only while the user is at the bottom; scrolling up pauses and shows unseen lines plus **Jump to latest**.
- Localhost, LAN, public, tunnel, Java, and Bedrock addresses are distinct and copied from distinct actions.
- Never present a local port check as proof of public accessibility.
- Use 12-hour times in normal UI and preserve raw timestamps in logs.
- Support Windows scaling, Windows 10 and 11, keyboard use, Reduced Motion, broad Unicode, and high-contrast states.
- Avoid duplicated controls and redundant navigation.

## Server support and integrations
Support capability-driven handling for:
- Vanilla, Paper, Purpur, Spigot or Bukkit, Fabric, Quilt, Forge, NeoForge, supported hybrids, Bedrock Dedicated Server, and custom scripts.
- Managed Java runtimes without changing system `PATH` or uninstalling system Java.
- Official provider metadata and APIs only; never scrape websites.
- Modrinth, CurseForge with a user key, GitHub Releases, Mojang, Paper, Purpur, official loader metadata, direct manifests, local packages, and future provider adapters.
- Exact version selection, release channels, hashes, dependency, loader, game-version, Java, and client/server environment checks.
- Dynmap and BlueMap integration rather than building a costly map renderer. An optional lazy-loaded WebView2 map surface is allowed only for local detected map URLs, with browser fallback.
- Connection methods as interchangeable adapters: LAN, direct port forwarding, public tunnel, private network, and future providers. ChunkPilot must remain useful without them.
- Crossplay only when compatibility is verified, with correct Java TCP and Bedrock UDP distinctions.

Never copy competitor source, assets, wording, or branding. Study public documentation and recurring GitHub issues to learn workflows and prevent failures.

## Competitive roadmap
The durable sequence after the guided 1.3 platform is:

### 1.4 — Connectivity and maps
Connection Manager, optional public-tunnel and private-network integrations, port-forwarding assistant, firewall and CGNAT diagnostics, Dynmap and BlueMap detection and in-app or browser view, upgraded friend sharing.

### 1.5 — Safety Lab and recovery
Isolated test deployments, safe test updates, add-ons and configuration changes, change timeline, last-known-good fingerprints, change-aware crash diagnosis, one-click recovery, and offsite backup destinations.

### 1.6 — Distribution and support
GitHub release pipeline, safe ChunkPilot updater and rollback, signed-manifest and checksum readiness, contextual local help center, rich local MOTD editor, client handoff manifests, and graphical configuration helpers.

### 1.7 — Secure remote and mobile management
Optional and disabled-by-default remote module; device pairing, HTTPS, scoped permissions, audit trail, revocation, rate limits, private-network or tunnel-first access, and a mobile PWA.

### 1.8 — Networks and extensibility
Velocity and proxy topology, ordered lifecycle, server groups, shared access rules, out-of-process extension API, CLI, PowerShell, webhooks, and local analytics.

### 1.9 — Identity and accessibility
Themes, optional low-opacity backgrounds, mascot, restrained Easter eggs, accessibility, density modes, and layout customization. These must not harm performance or readability.

### 2.0 — Stabilization and public release
Real-world compatibility matrix, Windows 10 and 11 testing, old and new versions and loaders, large packs, provider outages, antivirus locks, full disks, sleep and resume, crash recovery, signed builds, SBOM, reproducible CI, security review, performance profiling, and complete documentation.

Do not blindly advance version numbers. The task prompt and repository determine the actual milestone.

## Standout capabilities to protect
ChunkPilot should become known for:
- Transactional server-pack updates and automatic rollback.
- A Safe Test Lab that tests risky changes on an isolated port before production.
- A complete change timeline.
- “What changed since the last healthy launch?” diagnostics.
- One-click recovery that preserves the failed state.
- Beginner server and modpack creation with managed Java.
- Capability-aware UI that removes irrelevant complexity.
- World-safe version management.
- Connection setup that offers easier alternatives to router configuration.
- Shareable exact client setup information.
- Native Windows speed and simplicity.

## Engineering standards
- Use C#, the current supported .NET and WPF version already selected by the repo, nullable enabled.
- Use MVVM, dependency injection, async and await, cancellation tokens, bounded concurrency, structured logging, and central exception handling.
- Never block the UI thread and do not use `Thread.Sleep` in production workflows.
- Never concatenate shell commands with untrusted input.
- Use correct Windows quoting and process-tree tracking.
- Cache static hardware data; dynamic sampling must be inexpensive and cancellable.
- Provider requests need timeouts, cancellation, rate-limit awareness, cache expiration, and offline fallback where safe.
- Stream large files, logs, archives, and downloads.
- Keep scans bounded, lazy, cancellable, and away from world-region traversal unless explicitly required.
- Database migrations are forward-only, versioned, transactional, and tested from every supported prior schema.
- Keep dependencies few, justified, centrally pinned, and license-compatible.
- No telemetry or analytics leave the PC without explicit consent.

## Testing requirements
Preserve all existing tests. Add tests for every bug fixed, safety rule, provider adapter, migration, state transition, and new workflow.

Use:
- Unit tests for rules, parsers, compatibility, path safety, state machines, retention, and migrations.
- Integration tests with fake console servers, temporary server packs, mocked providers, test runtimes, and copied database fixtures.
- Long-running or accelerated console stress tests with bounded-memory assertions.
- Installer upgrade tests that preserve servers, worlds, backups, settings, and history.
- Failure injection for interrupted downloads, locked files, bad hashes, low disk, provider outage, failed startup, stale operations, crash loops, and PID reuse.
- UI or ViewModel tests for command enablement, close behavior, progress, validation, accessibility states, and duplicate-operation prevention.

Release gates:
1. `dotnet restore`
2. Release build with zero errors; do not introduce warnings.
3. All unit, integration, and migration tests pass.
4. Framework-dependent publish succeeds.
5. Self-contained win-x64 publish succeeds.
6. Portable build succeeds.
7. Upgrade-capable installer builds.
8. Smoke tests use isolated temporary data.
9. Working tree is clean.
10. Limitations are reported honestly.

A feature is not complete because it compiles or renders. It is complete when the real workflow works, failure paths are safe, tests pass, documentation is updated, and installer artifacts exist.

## Git, versioning, and release discipline
- Use one branch per bounded milestone.
- Commit a known-good recovery point before broad work.
- Keep commits coherent and descriptive.
- Never rewrite or delete user history to hide failures.
- Maintain stable installer identity for in-place upgrades.
- Update assembly version, installer version, schema, README, CHANGELOG, architecture, provider docs, safety docs, and migration docs.
- Produce SHA-256 for the release installer.
- Preserve prior installers and known-good release commits.
- Never claim live provider validation unless it actually ran.

## Required completion report
Report:
- Branch and commits
- Working-tree state
- Version and schema
- Tests passed, failed, and skipped by category
- Build warnings and errors
- Migration results
- Provider and integration status
- Smoke-test results
- Release, publish, portable, and installer paths
- Installer SHA-256
- Upgrade steps
- Data-safety result
- Exact remaining limitations
- Manual real-server smoke-test steps when relevant

## Decision hierarchy
When uncertain, choose in this order:
1. Protect worlds and user data.
2. Preserve a known-good rollback path.
3. Keep lifecycle state truthful.
4. Prefer official metadata and verified artifacts.
5. Make the beginner path obvious.
6. Keep advanced control available.
7. Keep the app lightweight and local.
8. Add polish without weakening any rule above.

## UI design system foundation
The permanent design-system foundation lives in `src/ChunkPilot.App/Themes` (tokens, shared control
styles, accessibility overlays, temporary compatibility aliases) and `src/ChunkPilot.App/DesignSystem`
(semantic icons, layout/motion/accessibility attached properties, `AppTheme`, the lookless composite
components, and the development-only Design Gallery). It is documented by
`docs/UI-DESIGN-SYSTEM.md`, `docs/UI-COMPONENT-CATALOG.md`, and `docs/UI-RESPONSIVE-RULES.md`, and
enforced by `DesignSystemContractTests`.

All UI work extends this foundation. It is never worked around.

**Reuse first.** Search `docs/UI-COMPONENT-CATALOG.md` before creating anything. If a component
exists, or is one variant away, use or extend it. A genuinely new control is added to the shared
system, documented in the catalog with its anatomy and required states, and shown in the Design
Gallery **before** any page uses it. Adding a token means updating the token file, the high-contrast
overlay where applicable, and the design-system document in the same change.

Required:
- Semantic tokens only: `App…` brushes via `DynamicResource`, metrics and typography via
  `StaticResource`.
- `ds:AppIcon` and `ds:AppButton.Icon` with `AppIconKind`; `FluentIcons` appears in exactly one file.
- `ds:AppLayout.Mode` triggers for responsive behaviour, `ds:AppMotion.IsEnabled` for motion.
- Public resource keys prefixed `App…`, internal building blocks prefixed `Internal…`.
- The merged-dictionary list in `App.xaml` stays flat and in step with `AppTheme.ThemeDictionaries`;
  nesting theme dictionaries breaks deferred `StaticResource` and `Style.BasedOn` resolution.
- `Themes/Compatibility/LegacyAliases.xaml` only ever shrinks. Never add to it.

Prohibited in any page or view: page-local colours, hex literals, font families, font sizes, radii,
shadows or motion values; a second button, input or card template; unexplained one-off styling;
emoji, raw glyph strings, private-use characters or icon-font references; `TabControl` navigation; a
scroll region nested inside the primary page scroller; horizontal scrolling of primary content; and a
default `MessageBox` for a product error.

## Complete UI design system rules
The complete native WPF UI overhaul is presentation-only: preserve Agent ownership, named-pipe transport, lifecycle intent/state separation, operation queues, bounded console capture, provider boundaries, persistence compatibility, and every data-safety rule above.

- Use semantic destinations and commands, not numeric tab indexes or page-specific service calls.
- Keep all visual values in shared token dictionaries; page XAML may compose components but must not invent colors, typography, radii, shadows, icon glyphs, or motion.
- Use FluentIcons.Wpf through the app's semantic icon abstraction. Do not use emoji, raw Segoe glyph strings, or page-local icon mappings.
- The shell owns navigation, server switching, global status, toasts, command palette, responsive mode, focus restoration, and close/crash behavior. Pages own presentation and bind to existing ViewModel commands.
- Wide, Standard, and Compact layouts must remain usable at Windows scaling settings and must not introduce horizontal page scrolling or nested scroll viewers for the primary workspace.
- Empty, loading, unavailable, failed, and destructive states must be explicit and truthful. Never fill a view with invented metrics or fake actions.
- Reduced Motion and high-contrast behavior are first-class states. Motion is brief, centralized, cancellable, and never required to understand a state change.
- Every UI change requires resource/build validation and a synthetic fixture smoke path. Real server folders, alternate worktrees, AppData, ProgramData, registry, services, firewall, installed instances, and externally managed processes are out of scope.
- Review visual changes in the Design Gallery. `ChunkPilot.exe --design-gallery` opens it; `--design-gallery --render <directory>` rasterises it at the Wide, Standard and Compact widths. The gallery is development-only, uses invented hard-coded data, takes no single-instance lock, shows no tray icon, and never contacts the agent. No product control may open it.
- Review the Create Server v2 wizard with `ChunkPilot.exe --create-server-v2-preview`; `--create-server-v2-preview --render <directory>` rasterises every reviewable state. It runs on the same terms as the gallery — development-only, invented data, no single-instance lock, no tray icon, no agent, no database, no installation — and no product control may open it. The production **Create server** commands continue to open `InstallServerWindow` until a later milestone retires it.
- `ChunkPilot.exe --create-server-v2-live-vanilla` opens the same wizard connected to the real Agent, for Vanilla only. Because it is explicitly requested, it raises itself to the front once when first rendered — a topmost toggle that restores the previous value, never a permanent Topmost, never repeated. Windows may still refuse activation when another application owns the foreground; the taskbar button flashes in that case. It is the opposite kind of switch from the preview and must not be confused with it: normal startup runs first — single-instance lock, tray icon, agent, database, shell — and the wizard then opens over it, because a wizard that genuinely creates a server needs the agent that owns the work and the navigation the finished server is opened through. It downloads Mojang's server, obtains a managed Java runtime, writes `eula.txt` only after deliberate acceptance, and registers a real server. Development-gated: no button, menu item or command reaches it, and without the switch none of it runs. Review it against an isolated `CHUNKPILOT_DATA_ROOT` **and** `CHUNKPILOT_MANAGED_SERVERS_ROOT`; `scripts/review-create-server-v2-live-vanilla.ps1` does exactly that.
