# Changelog

## Unreleased

## 1.3.0-alpha.4 - 2026-08-20

### Current product synchronization

- Synchronized the public snapshot with the current React/WebView2 product while retaining the Alpha
  3 lifecycle ownership fix: no inferred reboot autostart, bounded manual Stop, exact process identity,
  and authoritative reconnect state.
- Made the WebUI the only shipped interface. Normal startup needs no preview argument, the obsolete WPF
  product shell and shortcut are excluded, and WebView2 failure uses a minimal native recovery window.
- Replaced end-user CurseForge key onboarding with a developer/application credential gate. Public
  CurseForge access remains disabled until the production credential model is approved.
- Added direct Modrinth provider-link resolution and native-token import for server ZIPs, `.mrpack`,
  JARs, and folders with bounded archive validation, TOCTOU reinspection, staging, journaling, and rollback.
- Reorganized public documentation and release assets, added build identity and signing-ready scripts,
  reduced portable loose-file clutter with single-file first-party binaries, and introduced Quick,
  Feature, HighRisk, and immutable Release validation tiers.

### Alpha 3 lifecycle ownership hotfix

- Stopped treating a server's last observed running state, crash-recovery state, or stale restart
  journal as permission to start it when Windows, ChunkPilot, or the Agent starts. Startup now requires
  an explicit persisted autostart setting or a due user-created schedule.
- Manual Stop now records stop intent and cancels a pending Start, Restart, or restartable data
  operation before waiting for the per-server operation queue. An operation that ignores cancellation
  reaches a bounded actionable failure instead of leaving the UI in `Stopping` indefinitely.
- Reconciled stopped/process state before completing Stop and made WebUI lifecycle completion report
  the authoritative Agent result rather than an optimistic command return.

### First public GitHub alpha snapshot

- Added reproducible GitHub-hosted Windows CI and an intentionally manual prerelease workflow with
  minimum permissions, exact annotated-tag verification, installer/portable lifecycle smoke tests,
  SHA-256 generation, SPDX SBOM validation, and public asset redownload verification.
- Hardened the per-user Inno Setup package around a self-contained win-x64 payload, an explicit WebUI
  Preview shortcut, official Microsoft WebView2 Evergreen prerequisite handling, and default uninstall
  preservation of settings, servers, worlds, backups, credentials, and history.
- Added a public README, deterministic fixture screenshots, security and issue-reporting paths,
  generated third-party notices, release instructions, and a frozen `v1.3.0-alpha.2` baseline record.
- Removed a development-machine-specific import-folder probe and private local path references from the
  public snapshot. No product capability milestone or application version bump was introduced.

### Managed-loader stable certification

- Added an explicit-EULA, resumable exact-runtime campaign for official Fabric, NeoForge, Forge, and
  Quilt stable catalogs. The embedded identity-bound registry now contains 162 exact passes: 47
  Fabric, 13 NeoForge, 59 Forge, and 43 Quilt. Twenty-three provider/catalog blockers and two exact
  Forge failures remain truthfully unverified.
- Separated loader-installer Java from the resulting server runtime. Quilt installer 0.15.1 uses a
  private Java 17 toolchain while old Quilt servers retain their required Java 16 or Java 8 runtime;
  the same boundary applies to creation, certification, and staged loader updates.
- Added legacy Minecraft status-ping fallback for exact old-server readiness checks, bounded
  installer diagnostics, atomic per-version ledgers, retry/resume behavior, free-space preflight,
  process-tree cleanup, and exact blocked reasons.
- Recorded that Ornithe 1.0, Beta 1.8, and Beta 1.8.1 cannot be automatically certified because
  Mojang publishes no official server artifact; no archive mirror is substituted.

### Friend Connectivity v2 — consent-first Windows Firewall access

- Direct internet now presents router mapping and Windows Firewall as independent layers. A firewall
  rule is never created by selecting Direct internet, starting a server, opening the App, or running a
  read-only check. The user must request the change, confirm the exact scope, and accept the normal
  Windows administrator prompt.
- The one-shot elevated helper accepts only fixed create, update, and remove operations. It creates one
  enabled inbound allow rule for the server's exact managed Java executable, exact TCP port, and one
  authorized active profile. The App and Agent remain non-elevated.
- Rule ownership is backed by a stable ID, ChunkPilot group and description plus schema-v6 persistence.
  Foreign allow and block rules are reported but never changed. Port, Java, or profile drift makes an
  owned rule stale until the user explicitly updates it.
- Every helper result is verified by re-reading Windows Firewall. Helper exit code alone is not success.
  Stop, Restart, App close, and Agent restart do not churn a valid persistent rule; server deletion is
  refused until an owned rule is verified absent.
- Public networks require an additional explicit confirmation. Managed policy, disabled firewall,
  ownership collisions, and block rules are shown truthfully. Even when both layers are configured,
  ChunkPilot says external reachability has not been verified.

### Fix — a stopped server kept reporting "Router port is open"

Found on a real router with a clean ChunkPilot-owned mapping for TCP 25566. The owner pressed Stop,
Minecraft released the port, and the card went on showing the mapping, its lease and its original
AddPortMapping result as current.

- The exposure had in fact been withdrawn; the screen never asked again. Router-mapping state was only
  read when the selected server changed, so the card kept whatever it was handed when the port was
  opened. It is now re-read after every lifecycle command and on the periodic refresh while Direct
  internet is the selected method, with a sequence guard so a slower earlier read cannot put a removed
  mapping back.
- Wanting Direct internet and having a port open are now separate states. A stopped server reads as
  set up with nothing open, and only a mapping that exists may show a lease, an internal endpoint or an
  address with a port on it.
- A stop and a restart are told apart by the lifecycle operation instead of by a ninety-second timer:
  a restart keeps its mapping only while that restart is actually running. A stopped or crashed server
  now loses its exposure on the first reconciliation rather than up to a minute and a half later, and a
  restart that fails can no longer hold a port open indefinitely.
- A cleanup the router would not confirm reports itself, keeps the evidence a retry needs, and offers
  Try again alongside Turn off instead of claiming the port was closed.
- Cancelling setup now really cancels: the recorded intent is put back, so the next reconciliation no
  longer opens the port the owner had just backed out of.

### Fix — Direct internet returned to "Not set up" after the router answered

Found on a real home router. UPnP discovery, the device description and `GetExternalIPAddress` all
succeeded, and the card still went back to **Not set up** with no explanation.

- The Agent decided a server's state from the user's intent before looking at the operation that had
  just run, so a capability check that succeeded projected as "off". Because the confirmation only
  opens on a supported result, and intent is only recorded once the exposure is confirmed, the feature
  could not be reached on any router — and a check that *failed* projected as "off" too, so no router
  failure could ever be explained. A settled state now comes from durable evidence and a failure stays
  visible until the user retries, turns Direct internet off, or something authoritative changes.
- Two technical rows — gateway and external port — were computed from the state but never told it had
  changed, so the diagnostic screen kept claiming no gateway had been identified while quoting the
  gateway's own reply. A test now fails if any property changes with the state without announcing it.
- The router-reported address is shown as soon as the router states one, instead of rendering an empty
  row beside a copy button until a mapping supplied a port.
- Technical details now distinguish a mechanism that answered from one that owns a mapping, name the
  private address a mapping would use, and record when the last check ran.
- An attempt that creates nothing no longer writes any ownership evidence, and cancelling an attempt is
  no longer recorded as a failure.
- Failure copy gained the two cases it was missing: a router that turned the request down, and no safe
  local address to forward to.

No change was made to the UPnP request format: `GetExternalIPAddress` succeeding on the real router
through the same channel is direct evidence that the envelope, SOAPACTION, content type and fault
parsing are accepted by it.

### Friend Connectivity v1 — consent-first router port mapping

A beginner can choose **Direct internet** and have ChunkPilot ask the router to forward the Minecraft
port, without meeting the words UPnP, PCP, NAT-PMP, NAT or gateway. Nothing is ever opened
automatically.

- Added router port mapping behind `IRouterMappingProvider`, with three implementations verified
  against primary sources: PCP version 2 (RFC 6887), NAT-PMP (RFC 6886) and UPnP IGD (UPnP Forum
  *WANIPConnection:1* plus the *UPnP Device Architecture* SSDP and SOAP rules). Capability checks are
  read-only by construction — PCP ANNOUNCE, the NAT-PMP external address request, and UPnP
  `GetExternalIPAddress`.
- Mechanisms are probed strictly in sequence (PCP, NAT-PMP, UPnP IGD), never in parallel, and the
  mechanism that establishes a mapping owns it until recovery requires rediscovery.
- Java Edition maps TCP only. Transport is modelled explicitly so future Bedrock UDP support cannot be
  confused with it. The external port always equals the server's own authoritative port; a substitute
  offered by the router is withdrawn and reported as a conflict rather than presented as the user's
  address.
- Mapping ownership requires persisted evidence, a matching internal endpoint and a matching
  description. Anything short of that is a conflict and is left untouched. PCP deletes carry
  ChunkPilot's own 96-bit mapping nonce.
- Added the `router_mappings` table for per-server intent and ownership evidence. Every existing
  server defaults to off; consent is per server and is forgotten when Direct internet is turned off.
- The Agent renews finite leases at half the lease, withdraws the mapping on a deliberate stop,
  deliberately preserves it across a safe restart, reconciles after an agent restart or power loss, and
  retains a failed removal for retry instead of hiding it.
- Overview gained a Direct internet block with an inline, single-step confirmation and progressive
  disclosure of technical details. An accepted mapping is reported as the router port being open,
  never as publicly reachable, and a router-reported WAN address is labelled as exactly that.
  `100.64.0.0/10` and RFC 1918 WAN addresses raise *your router appears to be behind another network
  layer*, never a certain CGNAT claim.
- Windows Firewall is not modified by this milestone, and no external reachability probe is performed.

### UI rebuild - Phase 1: design-system foundation

Presentation only. Agent ownership, named-pipe transport, lifecycle intent/state separation,
operation queues, console capture, provider boundaries, persistence and every data-safety rule are
unchanged.

- Rebuilt the WPF design system as a token layer (`Themes/Tokens`: palette, semantic colour,
  typography, metrics, elevation, motion) plus twelve shared component dictionaries
  (`Themes/Controls`). Surfaces are now neutral dark rather than purple-tinted, and purple is
  reserved for brand, selection, focus and the single primary action.
- Added typed metric tokens (`CornerRadius`, `Thickness`, `Duration`) in place of doubles coerced at
  each use site, plus elevation tokens restricted to genuinely floating layers.
- Added `AppTheme` centralised theme loading with runtime high-contrast and Reduced-Motion overlay
  dictionaries, and per-window accessibility state. Reduced Motion is enforced both by zeroed
  duration tokens and by an inherited `AppMotion.IsEnabled` flag that stops storyboards starting.
- Added `AppLayout` responsive attached properties, so Wide/Standard/Compact comes from documented
  breakpoint tokens instead of per-view size handlers.
- Replaced the icon abstraction with `AppIconKind`/`AppIconMap`/`AppIcon` and `AppButton.Icon`;
  `FluentIcons` is now referenced from exactly one XAML file and two code files, and the map throws
  rather than falling back to a placeholder glyph.
- Added thirteen lookless composite components: page header, section card, status badge, alert,
  toast, progress panel, loading state, empty state, info row, server row, busy indicator, number
  box and search box.
- Added a development-only Design Gallery (`ChunkPilot.exe --design-gallery`) with invented,
  hard-coded preview data, plus deterministic Wide/Standard/Compact rasterisation via
  `--design-gallery --render <directory>`. It takes no single-instance lock, shows no tray icon and
  never contacts the agent.
- Removed design-system violations from the not-yet-rebuilt shell: icon-font glyph literals,
  private-use characters, raw `FluentIcon` elements and the hard-coded colours in the state-brush
  converter and sparkline.
- Added `DesignSystemContractTests`: the real dictionaries are loaded through a WPF application on
  an STA thread so every `StaticResource` and `Style BasedOn` must resolve, plus checks for token
  types, overlay coverage, duplicate keys, catalogue/theme/gallery agreement, icon uniqueness, and
  bans on page-local colours, typography literals, glyph literals, tab navigation, nested scroll
  regions and stray message boxes.
- Rewrote `docs/architecture/UI-DESIGN-SYSTEM.md`, `docs/architecture/UI-COMPONENT-CATALOG.md` and
  `docs/architecture/UI-RESPONSIVE-RULES.md`, and added an always-loaded reuse-first UI steering rule.

Product pages, dialogs and workflows are unchanged in this phase; they still use a temporary
compatibility alias layer that later phases delete.

## 1.3.0 - 2026-07-24

- Replaced blocking/modal window shutdown with immediate `SafeApplicationExit`; the agent performs background save/stop and exits afterward. Unexpected UI loss now defaults to keeping servers running and provides reconnect recovery.
- Added persisted lifecycle intent, bounded crash recovery, manual-stop suppression, one intended safe-restart allowance, prior-running-state restoration, and PID/start/executable/working-directory/command-signature identity records.
- Added centralized evidence-based capability profiles and capability-gated Java/Bedrock, mod/plugin, world, gameplay, and update UI sections.
- Added beginner Create Server presets, exact version selection, provider-neutral catalog policy, server-only filtering, Modrinth browsing, DPAPI-gated CurseForge, and honest unavailable FTB status.
- Added transactional Fabric, Quilt, Forge, and NeoForge loader installation with official metadata, hash sidecars, isolated staging, captured output, cancellation, and non-detaching launch detection.
- Added checksum-verified Eclipse Temurin x64 managed Java, per-server absolute paths, multi-major selection, highest class-file inspection, health/usage records, and Recovery-based owned runtime removal without PATH/JAVA_HOME changes.
- Added explicit networking modes and LAN/public separation, Share With Friends guides, official/hash-verified owned Geyser/Floodgate/ViaVersion crossplay installation, UDP conflict checks, and reversible removal.
- Added unified whitelist/operator/player-ban/IP-ban evidence, version-aware gameplay presets/live-or-queued gamerules, transactional world-specific datapack installation, atomic server resource-pack configuration, content ID/hash reconciliation, and no-code automation.
- Beginner presets now apply their reviewed `server.properties` values and create the promised daily verified-backup schedule; the Vanilla fixture installs exact Mojang metadata through private managed Java.
- Added file-operation canonical locks and external-edit hash detection.
- Added additive SQLite schema v4 while retaining 1.0-1.2 server, world, backup, schedule, settings, update, and rollback data.
- Completed the native WPF UI overhaul: centralized FluentIcons semantic icon usage, tokenized dark design system, shared controls, responsive shell/sidebar, semantic destinations, dashboard, guided create/import, server workspace, console, management, access, protection, settings, global pages, and transactional update surfaces. Existing Agent lifecycle, named-pipe reconnect, world/backup/update safety, and command bindings remain intact.
- Added high-contrast and Reduced Motion resource behavior, truthful empty/loading/unavailable copy, focused keyboard/accessibility resource contracts, and deterministic packaged WM_CLOSE coverage.

## 1.2.0 - 2026-07-24

- Added conservative update-source detection and explicit source/baseline linking for Modrinth, CurseForge, GitHub Releases, direct HTTPS manifests, local package history, ChunkPilot manifests, Prism metadata, and normalized ATLauncher metadata.
- Added official provider adapters with release-channel filtering, server-package selection, CurseForge DPAPI key storage, provider digest verification, and local SHA-256 recording.
- Added manual, startup, interval, daily/weekly-equivalent, beta, alpha, download-only, and advanced unattended update controls.
- Added transactional server-pack updates with player warnings, save/stop coordination, verified full snapshots, isolated staging, persistent-data migration, launch validation, atomic activation, readiness monitoring, local Minecraft status validation, and automatic failed-startup rollback.
- Added crash recovery for interrupted activation journals and one-attempt unattended update loop prevention.
- Added a per-server Version Manager, persistent post-update validation, snapshot verification/export/retention/recovery deletion, manual safe rollback, update history, compact Overview status, and a global Update Center.
- Added SQLite schema v3 tables for sources, checks, downloads and hashes, snapshots, migration decisions, rollback history, and update preferences while retaining 1.0/1.1 records.
- Added daily safe cleanup gates requiring a healthy active version, a verified conventional backup, an expired non-permanent snapshot, and last-usable-version protection.
- Expanded self-test coverage for provider/source configuration, update storage permissions, disk capacity, SHA-256, schema migration, and rollback readiness.
- Added fake-pack integration coverage for download-only staging, valid update/start/status validation, invalid hashes, world/settings migration, verified rollback, failed-startup auto-recovery, interrupted-switch recovery, and safe snapshot deletion.

## 1.1.0 - 2026-07-24

- Added transactional managed server installation for Vanilla, stable Paper, Purpur, local packages, and direct HTTPS packages, with official metadata, hash verification, cancellation, operation journaling, safe staging, and deliberate EULA acceptance.
- Prevented the Minecraft Java GUI for supported launch profiles by applying one `nogui` argument while retaining redirected console streams and detecting detached or `javaw` launchers.
- Reworked the WPF shell around centralized dark color, typography, and control resources, including themed ComboBox popups, tabs, context menus, numeric controls, clearer navigation, server headers, tooltips, and responsive host cards.
- Added real host hardware, memory, storage, LAN, and network-throughput data with cached static sampling.
- Added reliable console follow/pause/unseen-line behavior, filtering, search, virtualization, clear-view, copy actions, and jump-to-latest.
- Added constrained basic configuration controls and validation.
- Added server icon conversion with 64x64 PNG output and recovery.
- Added world discovery, safe import/switch/export, verified manifests, live save-off/save-on coordination, and Windows clipboard file-drop sharing.
- Added ecosystem-aware Mods/Plugins inventory, compatibility states, duplicate checks, local install recovery, and disabled storage outside active loader directories.
- Added live/stopped whitelist management, safe RAM argument updates and recommendations, automatic restart countdown/backup/retry controls, address copy actions, and honest layered connection testing.
- Added SQLite schema v2 migrations for operation journal, EULA records, instance source history, and plugin manifests without replacing existing server rows.
- Updated Inno Setup to 1.1.0 with upgrade support and data-preserving uninstall choices.
- Expanded automated coverage for managed installs, migrations, background process control, console behavior, worlds, whitelist, RAM, icon conversion, theme resources, and high-volume output.

## 1.0.0 - 2026-07-24

- Initial ChunkPilot release with WPF dashboard, reconnectable background agent, existing-server import, lifecycle management, integrated console, statistics, backups, schedules, files/configuration, local jar management, diagnostics, tests, publishing, and a per-user installer.
