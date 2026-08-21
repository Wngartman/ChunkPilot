# Product friction register

Product requirements the owner has identified from using ChunkPilot. Every entry is a durable product
commitment, not a suggestion and not necessarily a verified defect. Verified defects live in the
[Bug register](BUG-REGISTER.md).

> **All active items are equal High product priority. Implementation order reflects engineering
> dependency and safety only, not importance.**

IDs are stable and never reused. An item leaves **Open** only when its acceptance outcome is delivered,
tested, documented, independently reviewed where required, and accepted in the real product.

| ID | Item | Priority | Status | Bug |
|---|---|---|---|---|
| [CP-FRICTION-001](#cp-friction-001--precise-ram-allocation) | Precise RAM allocation | High (equal) | **Implemented** | — |
| [CP-FRICTION-002](#cp-friction-002--easy-server-renaming) | Easy server renaming | High (equal) | **Implemented** | — |
| [CP-FRICTION-003](#cp-friction-003--server-icon-workflow) | Server icon workflow | High (equal) | **Implemented** | [CP-2026-021](BUG-REGISTER.md#cp-2026-021--changing-a-server-icon-a-second-time-fails-with-an-access-error) |
| [CP-FRICTION-004](#cp-friction-004--open-server-folder) | Open server folder | High (equal) | **Implemented** | — |
| [CP-FRICTION-005](#cp-friction-005--versions--updates--backups-as-first-class-management) | Versions / Updates / Backups as first-class management | High (equal) | **Implemented** | — |
| [CP-FRICTION-006](#cp-friction-006--networking-discoverability) | Networking discoverability | High (equal) | **Implemented** | — |
| [CP-FRICTION-007](#cp-friction-007--creation-port--networking) | Creation port + networking | High (equal) | **Implemented** | — |
| [CP-FRICTION-008](#cp-friction-008--full-minecraft-version-history) | Full Minecraft version history | High (equal) | **Implemented** — 907-entry inventory with terminal exact certification evidence | — |
| [CP-FRICTION-009](#cp-friction-009--modpack-creation--discovery) | Modpack creation / discovery | High (equal) | **In progress** | — |
| [CP-FRICTION-010](#cp-friction-010--ui-exit-network-safety) | UI-exit network safety | High (equal) | **In progress** | [CP-2026-020](BUG-REGISTER.md#cp-2026-020--chunkpilot-managed-network-exposure-survives-ui-process-death) |
| [CP-FRICTION-011](#cp-friction-011--evidence-based-server-health) | Evidence-based server health | High (equal) | **Implemented locally; awaiting acceptance** | — |
| [CP-FRICTION-012](#cp-friction-012--offline-help-and-troubleshooting) | Offline help and troubleshooting | High (equal) | **Implemented locally; awaiting acceptance** | — |

---

## CP-FRICTION-001 — Precise RAM allocation

| Field | Value |
|---|---|
| Date | 2026-08-12 |
| Priority | High (equal) |
| Status | **Implemented** |
| Area | Server memory allocation control |

The memory control must accept an exact typed value as well as convenient presets. A value such as
`4.6 GB` must be representable whenever the runtime can safely express it. Presets are a convenience,
never a restriction.

**Acceptance outcome.** A typed allocation is validated against host memory and other configured
servers, normalized to exact Java-compatible MiB/bytes, and applied through `RamArgumentService`.

---

## CP-FRICTION-002 — Easy server renaming

| Field | Value |
|---|---|
| Date | 2026-08-12 |
| Priority | High (equal) |
| Status | **Implemented** |
| Area | Server identity / display name |

Changing ChunkPilot's display name must be easy and must never be conflated with moving or renaming the
server folder, world, or backups.

**Acceptance outcome.** Renaming changes only validated ChunkPilot metadata, preserves all paths, and
is distinct from any future reversible folder-level operation.

---

## CP-FRICTION-003 — Server icon workflow

| Field | Value |
|---|---|
| Date | 2026-08-12 |
| Priority | High (equal) |
| Status | **Implemented** |
| Area | Server icon selection, cropping and installation |
| Defect | [CP-2026-021](BUG-REGISTER.md#cp-2026-021--changing-a-server-icon-a-second-time-fails-with-an-access-error) |

The crop/change experience needs a proper design pass. Independently, changing an icon a second time
can fail with Access denied and requires a root-cause correction, not a retry-loop workaround.

**Acceptance outcome.** Choosing, cropping and replacing an icon is clear, and several consecutive
changes in one session succeed in an isolated deterministic regression.

Implemented with a square pan/zoom crop surface, Fit and Reset actions, a real 64 x 64 preview,
lock-free in-memory WPF images, content-addressed saved icons, and atomic same-directory finalization.
The A -> B -> C, reopen, saved-library reuse, same-source reuse, cancellation, invalid-finalization,
source-move and exact-output regressions are automated. A failed finalization leaves the previous icon
and preview in place and publishes no library record.

The WebUI reuses that authoritative installation path. Its Server appearance editor stages pan, zoom,
90-degree rotation and 64/32/16 px previews in the renderer, but does not mutate the live icon until
Save succeeds. React receives only bounded image data, never the selected path, and the snapshot icon
cache is explicitly invalidated after Agent success.

---

## CP-FRICTION-004 — Open server folder

| Field | Value |
|---|---|
| Date | 2026-08-12 |
| Priority | High (equal) |
| Status | **Implemented** |
| Area | Server Dashboard and Manage |

An obvious **Open server folder** action must be available from Dashboard and Manage.

**Acceptance outcome.** Both actions resolve and open the exact canonical recorded server root, and
fail safely without guessing when that root is missing or unreadable.

---

## CP-FRICTION-005 — Versions / Updates / Backups as first-class management

| Field | Value |
|---|---|
| Date | 2026-08-12 |
| Priority | High (equal) |
| Status | **Implemented** |
| Area | Navigation and information architecture |

**Versions & Updates** and **Backups** must be obvious first-class management destinations rather than
miscellaneous settings. This outcome does not freeze today's exact navigation layout.

**Acceptance outcome.** Each destination is found without hunting through settings, and the final
arrangement is intentional and documented in the UI architecture.

WebUI keeps **Backups** and **Versions** as persistent server-workspace tabs. Backups exposes create,
verify and protected restore; Versions exposes installed evidence, authoritative update state,
verified recovery snapshots, safe cancellation and rollback, plus the complete official inventory.

---

## CP-FRICTION-006 — Networking discoverability

| Field | Value |
|---|---|
| Date | 2026-08-12 |
| Priority | High (equal) |
| Status | **Implemented** |
| Area | Access / networking surface |

Primary networking mode, state, and actions are too buried. Mechanism, gateway, router lifetime,
interface, and policy evidence should remain progressively disclosed.

**Acceptance outcome.** Current networking state and its primary action are easy to find while every
truthfulness rule in [Networking](../operations/NETWORKING.md) remains intact.

The server hero keeps **Share** visible and states LAN, Internet setup, confirmed joinability, or
attention in plain language. Overview points directly to **Manage connectivity**. The WebUI
Connectivity category exposes only the ordinary **LAN** and **Internet** choices while retaining
private/internal migration states under advanced recovery paths. A shared connection summary explains
who should use the local, LAN, current Internet, or last-known Internet address. Router, firewall and
diagnostic internals remain progressively disclosed.
This change does not alter lease ownership, consent, router operations or firewall operations. When
Internet is selected, ordinary status follows the authoritative exact firewall rule, exact router mapping,
and running server. A router-reported address remains copyable as **Internet sharing configured** only
when those owned prerequisites are present; this is explicitly not a universal reachability guarantee.
Outside-in verification is an optional Advanced diagnostic and only its current evidence produces the
distinct **Connection confirmed** label. No automatic renderer polling is used.

---

## CP-FRICTION-007 — Creation port + networking

| Field | Value |
|---|---|
| Date | 2026-08-12 |
| Priority | High (equal) |
| Status | **Implemented** |
| Area | Create Server v2 |

Creation must offer a beginner-safe recommended port, a validated custom port, and the relevant
networking choice. Creation must never create public exposure by itself.

**Acceptance outcome.** Range, ChunkPilot conflicts, and local use are validated; public router or
firewall changes still require their existing separate explicit confirmations.

Create Server v2 now starts at the standard Java port, accepts and validates an exact port from 1 to
65535, preserves it across Back, shows it on Review, and writes the same value to the registered
definition and `server.properties`. Port availability is labelled as unknown until startup rather than
invented from configuration alone. The typed networking preference defaults to **LAN** and
also offers **Internet hosting** and **Configure later**. The internal private mode remains available
for existing profiles, tests, diagnostics, imports, and recovery but is not a primary creation choice. It records only the intended
next guidance; creation creates no mapping, firewall record, consent or public-access claim.

---

## CP-FRICTION-008 — Full Minecraft version history

| Field | Value |
|---|---|
| Date | 2026-08-12 |
| Priority | High (equal) |
| Status | **Implemented** — complete inventory and exact certification campaign with documented blockers |
| Area | Version catalog and selection |

ChunkPilot ultimately needs every truthfully supportable multiplayer version, including historical
releases and technically validated Beta/Alpha server versions. The interaction may be search, filters,
load-older, or another designed browser rather than a giant dropdown.

**Acceptance outcome.** Offered history is reachable and each version carries verified artifact,
runtime, and compatibility evidence; a manifest entry alone is never presented as support.

The Mojang inventory is complete, searchable and virtualized across releases, development builds,
Beta and Alpha. Artifact, integrity, Java, launch, capability, certification and limitation evidence
are separate. With explicit disposable-root EULA authorization, the reusable runner gave all 907
current manifest entries terminal evidence: 833 exact runtime passes, seven exact technical failures,
and 67 entries for which Mojang publishes no official server artifact. The latest stable 26.2 release
is Recommended, 95 other exact stable releases are Verified, and passed development builds remain
risk-labelled Experimental. Beta and Alpha without an official artifact remain blocked; ChunkPilot
does not substitute unofficial mirrors. The WebUI now offers a native, single-use, hash-bound import
for exact user-owned 1.0, b1.8, and b1.8.1 server JARs. Those local runs do not become global official
support. Vanilla 1.2.5 has exact Java 8, legacy-status, readiness, clean-stop, and cleanup evidence. The seven
runtime failures retain their exact bootstrap, readiness, or clean-stop reason rather than being
silently promoted.

---

## CP-FRICTION-009 — Modpack creation / discovery

| Field | Value |
|---|---|
| Date | 2026-08-12 |
| Priority | High (equal) |
| Status | **In progress** |
| Area | Create Server v2 / providers |

Modpacks are first-class. The creation experience needs official-API discovery for Modrinth and
licensed CurseForge access, exact release history, compatibility evidence, official server packs, and
transactional pack construction. Partial or invented provider functionality remains hidden.

**Acceptance outcome.** A beginner can discover an exact compatible pack version and create a working,
recoverable server through licensed official provider paths.

The WebUI now browses the official Modrinth and user-keyed CurseForge APIs, exposes provider state,
supports explicit and cancellable search, accepts exact project links, displays provider images,
selects an exact integrity-complete `.mrpack`, imports a local `.mrpack` through a native picker, and
uses the hardened transaction to materialize server-only files plus the exact declared Fabric,
NeoForge, Forge or Quilt loader. Exact provider identity persists and pack-level update/rollback uses
the existing recovery architecture without independently updating constituent mods.

The item remains **In progress**: provider linking for a local pack, period popularity history,
runtime certification for representative public packs, and a complete conflict UI
are not delivered. The beginner outcome is therefore not yet fully accepted.

---

## CP-FRICTION-010 — UI-exit network safety

| Field | Value |
|---|---|
| Date | 2026-08-12 |
| Priority | High (equal) |
| Status | **In progress** — implemented on `fix/public-connectivity-lease-safe-exit`; independent read-only review and real Windows acceptance remain |
| Area | App / Agent lifecycle / public connectivity ownership |
| Defect | [CP-2026-020](BUG-REGISTER.md#cp-2026-020--chunkpilot-managed-network-exposure-survives-ui-process-death) |

The permanent user-facing rule is:

> Close ChunkPilot to stop hosting safely. Minimize it to keep hosting.

**Acceptance outcome.** A random Agent-minted capability bound to the exact UI process creates
independent per-server public-connectivity lease generations. Normal close and proven process death
immediately revoke leases, stop renewal, stale external verification, begin exact-owned bounded router
cleanup, safely stop every managed server, and exit the Agent only after exact managed listeners are
gone. Pipe disconnect and minimize/tray do not trigger. Relaunch and Agent restart inherit no lease;
old router state is cleanup-only. Durable exact Windows Firewall configuration may remain because it is
neither a listener nor a public route. Stale/replayed/wrong-session generations fail before mutation.

**Why this remains In progress.** Deterministic automated coverage and isolated packaged smoke are
implementation gates, not real-machine acceptance. The first independent review identified four High
boundary defects; the correction generation-fences router execution, seals the exiting Agent epoch,
orders stale cleanup before restoration, gives exit a bounded cancellation-aware lifecycle path, and
requires exact raw process creation identity. Final independent review and real Windows close/taskkill
acceptance remain. [CP-2026-020](BUG-REGISTER.md) stays Open until those gates accept the behavior.

**Known limits.** Simultaneous App-and-Agent termination, machine power loss, and an unreachable router
cannot promise immediate cleanup. Router failure retains truthful `RemovalPending` evidence and finite
router leases expire without renewal. No other friction item is implemented by this branch.

---

## CP-FRICTION-011 — Evidence-based server health

| Field | Value |
|---|---|
| Date | 2026-08-21 |
| Priority | High (equal) |
| Status | **Implemented locally; awaiting acceptance** |
| Area | Native snapshot / Overview / troubleshooting routing |

Current server problems should appear where the server is managed, using evidence ChunkPilot actually
has rather than generic advice or a permanent “healthy” banner.

**Acceptance outcome.** Native code maps selected-server crash/watchdog/Java/port and owned-network
evidence into typed, deduplicated issues. Overview shows at most two compact notices with evidence,
safe deep links, and per-server/per-fingerprint dismissal. Resolved issues disappear with the
authoritative snapshot and a genuinely new occurrence returns. Deliberate stops and missing
outside-in diagnostics do not create warnings.

---

## CP-FRICTION-012 — Offline help and troubleshooting

| Field | Value |
|---|---|
| Date | 2026-08-21 |
| Priority | High (equal) |
| Status | **Implemented locally; awaiting acceptance** |
| Area | Settings / Help & troubleshooting |

Operators need one calm, searchable place for Minecraft server symptoms without sending private logs,
server state, or search terms to a cloud service.

**Acceptance outcome.** The bundled Help Center currently contains 28 structured articles across
startup, Java, networking, players, performance, worlds, plugins, mods/modpacks, and recovery. Search
matches plain language, aliases, and exact console signatures; filters, no-results, related articles,
safe steps, stop conditions, server deep links, and reviewed primary sources work offline. External
documentation opens only through an HTTPS host allowlist after a user chooses it. Sources and volatile
claims are registered in `docs/research/MINECRAFT-SERVER-HELP-SOURCES.md`.
