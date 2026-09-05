# Bug register

Verified defects and limitations with evidence sufficient to track. A fix awaiting manual acceptance
may remain here as **Fixed locally** with its regression evidence; after acceptance, the commit and test
become the durable record and the entry can leave this active register.

---

## CP-2026-067 — Connection summaries promoted a host LAN address to live server availability

**High; fixed locally with focused regressions; packaged/visual acceptance pending.** A loopback-only
ATM10 instance was displayed as home-network available, and its stopped header retained that claim.
The certification request deliberately used ThisComputerOnly. `MapServer`/`MapConnectivity` collapsed
private/unknown modes to HomeNetwork, while React inferred availability from the PC's LAN address
without checking effective binding or lifecycle.

A shared native summary separates requested audience, bounded saved configuration, launch baseline,
and fresh TCP LISTEN evidence from the exact held root process/raw creation identity. All joining surfaces
and local/LAN copy actions use that result. Stopped/starting/unknown states outrank retained public setup;
no binding, firewall, router, or user server is changed. Wrapper descendants remain explicitly unknown.
Focused evidence: 36 native unit cases, one real isolated fake-server start/restart/stop integration,
and six new WebUI boundary regressions passed. Guided binding apply/restart remains a product gate,
not part of this read-only presentation fix. Prior networking defects and open friction remain separate.

## CP-2026-066 — NeoForge advertised loopback-only creations on the LAN

**High; corrected and packaged live lifecycle verified, owner acceptance pending.** ATM10 project 925200, client 8764211,
server 8764245 reached its expected loopback listener but also bound wildcard IPv4/IPv6 UDP.
The exact-owned JVM thread dump caught Minecraft's LAN pinger; NeoForge's documented default
enables that advertisement independently of `server-ip`. Attribution to the observed socket remains
probable, not proven by a thread name alone. The unknown-UDP fail-closed rule was correct.

New NeoForge **This computer only** creations now disable only the documented networking Boolean.
Validation uses a disposable world-local override, preserving intended configuration and pack defaults.
The bounded editor rejects ambiguous files and preserves unrelated values/comments/BOM/line endings.
Focused reruns: 45 unit and 21 integration passed, including synthetic UDP still blocking and
configuration restoration after failures. Existing servers are not rewritten; no UDP bypass is added.
The exact retained-input retry at `5a82ac8` passed staged validation, promotion, second start/status/stop,
Job emptiness and port absence with zero repeated archive bytes and no unresolved UDP.

## CP-2026-064 — Startup validation mistook non-listening TCP connections for inbound listeners

**High; fixed and packaged live control verified, owner acceptance pending.** At base `12bb1f8`, production rejected
`ownedEndpoints.Any(endpoint => !endpoint.IsLoopback)` without testing TCP state. The controller's
separate listener-only collector hid this production mismatch. A documentation-address reproduction
demonstrates the old false rejection; the original saved report did not preserve its raw socket tuple.

`StartupNetworkPolicy` now serves both paths. Non-loopback TCP listeners block; non-listening connections
retain direction/purpose uncertainty; non-loopback UDP purpose, unreadable tables and uncertain ownership
return cannot-verify. Exact Job generation fencing, IPv6 scope parsing and failure-path cleanup are covered.
Focused boundary/controller/nullable/creation slice: 102 passed. The packaged Optimized Performance
control passed staged validation and promoted start/status/stop, with 71 non-listening observations;
its complete cleanup recheck passed at `9d3899e` with no new archive download. This does not establish pack security.

## CP-2026-065 — Staged validation could boot the intended world and erase late-failure input

**High; core and native retry verified live, owner acceptance pending.** Validation previously changed bind settings but
kept `level-name`; official archives were deleted before startup completed. It now uses an exact-owned
disposable world, preserves intended-world sentinels/configuration, and restores only after Job-empty proof.
Verified official archive input can survive a late failure in a bounded operation-owned journal/store.
Fixtures cover timeout, cancellation, stale/failed collectors, tampered archives, restart retention, and
retry with archive requests disabled. Agent retry/discard, generation fencing, restart restoration and
WebUI elapsed/milestone/recovery tests are implemented. Current focused recovery/provider/controller
unit run: 82 passed; transaction/retry/staged integration: 60 passed; new UI recovery tests: 3 passed.
ATM10 proved two real late-failure/native retry paths with zero repeated archive bytes; the second
completed promotion and second start/status/stop at `5a82ac8`. See CURRENT-GATE for preserved failures,
successful lifecycle evidence, content checks and remaining owner acceptance.

## CP-2026-063 — A valid catalog not-found response was reported as a broken Agent frame

| Field | Value |
|---|---|
| Date | 2026-08-29 |
| Git state | `codex/curseforge-runtime-browser-parity`; fixed in `da829fe` |
| Provider identity | Live approved CurseForge API; project `1521865`, exact client file `7968569`; metadata only and no payload download |
| Severity | Medium — unavailable or provider-hidden exact projects produced a misleading transport error instead of the existing truthful not-found path |
| Area | App/Agent named-pipe catalog resolution and headless runtime certification |
| Status | **Fixed; packaged live not-found rerun and deterministic regression** |
| Validation | Exact-HEAD packaged Agent now reports truthful provider not-found; 65/65 focused certification and 1,653/1,653 unit tests pass |

### Reproduction and root cause

The live packaged Agent authenticated, recovered the DPAPI-protected provider configuration, and then resolved the
exact CurseForge selection to no project. `ResolveCatalogProject` deliberately serializes that nullable result as
JSON `null`. `AgentResponse.Payload` is a nullable `JsonElement`, so deserialization cannot distinguish that JSON
value from an omitted payload. Both clients treated it as a malformed successful response and surfaced `Agent
returned no response payload` instead of letting their existing nullable catalog path report that the project or
release was unavailable.

### Resolution and boundary

Both named-pipe clients now preserve JSON `null` only for the exact nullable operation/type contracts:
`ResolveCatalogProject` with `CatalogItem` and `PluginRelease` with `PluginRelease`. Every other successful response
still requires a payload, so a missing Dashboard, lifecycle, mutation, or operation-state payload remains a
transport failure. The WebUI and certification callers then convert the nullable catalog result into their existing
truthful provider-not-found messages.

**VERIFIED:** 65 focused runtime-certification tests and all 1,653 unit tests pass. The wire-level regression proves that both client
implementations accept the two intentional nullable lookup results, reject a wrong response type, and continue to
reject a missing non-catalog payload. The exact-HEAD packaged rerun returned `The exact CurseForge project/file
could not be resolved` instead of a missing-payload transport error. It downloaded no provider payload, both exact
Agent Jobs exited with code `0`, zero Job processes remained, and the fresh run moved to Recycle Bin.

**PENDING:** the live project remains unresolved by the approved API and therefore is not a certified pack
candidate. This fix corrects the error classification; it does not invent provider availability or retry a hidden
release.

---

## CP-2026-062 — Pre-start cancellation capacity could permanently strand App acceptance retry

| Field | Value |
|---|---|
| Date | 2026-08-29 |
| Git state | `codex/curseforge-runtime-browser-parity`; implementation commit pending |
| Provider identity | Deterministic coordinator and acceptance fixtures only; no provider request or credential |
| Severity | High — after 4,096 pre-start fences, every later cancelled creation or add-on request could remain nonterminal until the Agent restarted |
| Area | App/Agent exact-operation acceptance, modpack creation and managed-content cancellation fencing |
| Status | **Fixed locally; headless deterministic evidence only** |
| Validation | Coordinator integration (`11/11`), `AgentOperationAcceptanceTests` (`7/7`), and full Release solution build (`0` warnings, `0` errors) |

### Reproduction and root cause

Both coordinators bounded retained pre-start cancellation tombstones at 4,096. Once that capacity was full,
`CancelOrFence` permanently returned `FenceEstablished=false`. The App correctly refused to infer terminal
cancellation from that response and retried with the exact request, but the reachable Agent could never produce a
different result. Ownership remained fail-closed; liveness did not, so the task was deterministically stuck until
the Agent process restarted.

### Resolution and boundary

Reaching the retained-fence limit now seals new registration for that coordinator's remaining Agent lifetime under
the same Begin/cancel gate. An exact operation identity retained before the seal still replays only its known
request and terminal state. Every previously unseen Begin is rejected before authorization consumption or work,
so a later cancellation for an unseen identity can revoke its unconsumed authorization and return synthetic
terminal-cancelled evidence without retaining another attacker-sized operation object. The App therefore receives
`FenceEstablished=true` rather than looping on a permanently exhausted reachable coordinator.

**VERIFIED:** the `11/11` coordinator integration and `7/7` acceptance unit suites cover the capacity boundary,
known exact replay, changed-request rejection, unseen Begin refusal, synthetic terminal cancellation, and
creation/managed-content authorization revocation. The full Release solution build reports zero warnings and zero
errors.

**PENDING:** if the Agent itself remains permanently unavailable after an ambiguous Begin dispatch, the App still
keeps cancellation nonterminal and retries. It cannot truthfully claim a fence when no Agent can prove whether the
Begin crossed the pipe. The coordinator-lifetime seal fixes permanent saturation in a reachable Agent; it does not
turn transport loss into cancellation evidence.

---

## CP-2026-061 — Listener ancestry trusted reusable parent PIDs instead of exact live process generations

| Field | Value |
|---|---|
| Date | 2026-08-29 |
| Git state | `codex/curseforge-runtime-browser-parity`; implementation commit pending |
| Provider identity | Deterministic Windows process/Job fixtures only; no provider request or credential |
| Severity | Critical — a recycled raw parent PID could make a foreign ancestry chain appear to connect an endpoint owner to the exact task server |
| Area | Headless runtime certification, Windows endpoint-owner ancestry, PID/creation identity |
| Status | **Fixed locally; headless deterministic evidence only** |
| Validation | `CurseForgeRuntimeCertificationTests` (`56/56`), focused certification checks (`14/14`), and `ChunkPilot.Certification` Release build (`0` warnings, `0` errors) |

### Reproduction and root cause

Endpoint owners already had stable Job PID/creation identities, but the final subtree check followed raw parent PIDs
from a Windows process snapshot. A parent could exit and its PID could be reused after establishing the historical
relationship. Treating that number as the original process could accept ancestry that was no longer a live exact
chain to the task-server process.

### Resolution and boundary

The task root, endpoint owner, and every intermediate ancestor must now exist in the same stable Job PID/creation
identity set captured across all three endpoint-proof snapshots. Every traversed generation is revalidated live,
and each parent must have been created strictly before its child; missing, exited, unknown, reordered, cyclic, or
PID-reused links fail closed. The exact task-root equality case needs no inferred parent link. The existing repeated
TCP4/TCP6/UDP4/UDP6 inventories and sanitized evidence policy remain unchanged.

**VERIFIED:** `56/56` certification tests and the focused `14/14` checks cover the live stable root/descendant case,
missing and exited intermediates, raw PID reuse, creation-order refusal, and fail-closed endpoint policy. The
Certification Release build reports zero warnings and zero errors.

**PENDING:** no live server launch is certified by these deterministic fixtures. The irreducible observation
boundary from CP-2026-058 remains: sequential native tables and repeated snapshots cannot exclude an endpoint that
opens and closes wholly between observations or a mutation after the final observation.

---

## CP-2026-060 — ZIP expansion used a compressed-size multiplier that could under-reserve update staging

| Field | Value |
|---|---|
| Date | 2026-08-29 |
| Git state | `codex/curseforge-runtime-browser-parity`; implementation commit pending |
| Provider identity | Synthetic local ZIP central-directory fixtures and local update packages only; no provider request or credential |
| Severity | High — a valid high-ratio archive could pass the old `compressed bytes × 5` estimate, exhaust the candidate volume after recovery work began, and weaken the update/rollback safety margin |
| Area | Server-pack update storage forecast, bounded ZIP inspection, pre-snapshot mutation gate |
| Status | **Fixed locally; headless deterministic evidence and full Release build green** |
| Validation | Focused storage/migration unit tests (`14/14`), `ServerImportInspectionTests` (`9/9`), `VersionUpdateIntegrationTests` (`27/27`), and full Release solution build (`0` warnings, `0` errors) |

### Reproduction and root cause

The update forecast treated candidate expansion as five times the larger of the package size or 64 MiB. ZIP
compression ratios are not bounded by that multiplier, so a small verified archive could declare a much larger
valid expanded tree and still reach snapshot/candidate preparation with inadequate space reserved. The estimate
also used an unbounded recursive current-server enumeration that could follow a directory reparse point, disagree
with later snapshot/migration reads, or discover an unsafe/oversized tree only after output work began. Forecasting
had to account simultaneously for the exact current-server recovery snapshot and generated-candidate staging on
their actual target volumes.

### Resolution and boundary

After the exact package has been downloaded or accepted from verified reuse, ChunkPilot reads its bounded central
directory and sums the declared expanded file sizes with saturating arithmetic. Encrypted entries, unsupported
compression, unsafe/link/path-colliding metadata, excessive entries, and oversized totals fail closed. Before any
recovery snapshot or active-server mutation, storage is checked again using that verified archive expansion plus
the current-server snapshot requirement and, for generated CurseForge candidates, the sealed resolved payload and
its required staging copies. The current-server requirement comes from the same bounded, cancellation-aware,
case-insensitive full-tree inventory used by snapshot and migration. It permits at most 500,000 entries, never
follows a reparse point, rejects inaccessible, missing, duplicate/case-colliding, or redirected components, and
revalidates file size, last-write identity, and the no-reparse ancestor chain immediately before each inventoried
file read. Snapshot additionally bounds the streamed length and validates the entry again after reading. Snapshot
and migration refuse before producing their output when the initial full-tree boundary cannot be proven.
The old `compressed bytes × 5` value no longer authorizes bounded archive staging.

**VERIFIED:** the focused `14/14` storage/migration, `9/9` inspection, and `27/27` version-update suites prove exact
high-ratio central-directory forecasting, unsafe metadata refusal, bounded no-follow full-server inventory,
current-server/generated staging inclusion, and refusal before snapshot, migration output, or server mutation. The
full Release solution build reports zero warnings and zero errors.

**PENDING:** these deterministic fixtures do not certify a live provider archive download, freeze unrelated
external filesystem mutation between observations, or make a point-in-time free-space reading immune to unrelated
external disk use.

---

## CP-2026-059 — Reparse paths and late add-on collisions could redirect or overwrite managed JAR mutations

| Field | Value |
|---|---|
| Date | 2026-08-29 |
| Git state | `codex/curseforge-runtime-browser-parity`; implementation commit pending |
| Provider identity | Deterministic local JAR/path fixtures and authorized-plan stubs only; no provider request or credential |
| Severity | Critical — a junction/reparse component could redirect inventory or mutation outside the server, while a destination created after review could be mistaken for the approved activation target |
| Area | Mod/plugin inventory, activation, disable/remove/rollback, provenance/config storage, managed-content execution |
| Status | **Fixed locally; headless deterministic evidence only** |
| Validation | `PluginManagementTests` (`43/43`), `ManagedContentPlanAuthorizationIntegrationTests` (`5/5`), and Release build (`0` warnings, `0` errors) |

### Reproduction and root cause

Lexical containment and an early destination-ownership check were not sufficient when an active `mods`/`plugins`
folder, disabled/recovery destination, provenance/config path, or exact JAR component could become a Windows
junction, reparse point, or directory. A second race existed between review/download and activation: another file
could appear, or a reviewed same-project file could be swapped, while the operation waited to acquire the content
lock. Mutating after either stale check could cross the owned path boundary or overwrite a newly user-owned file.

### Resolution and boundary

Inventory and every add-on mutation now walk the exact server-owned path without following reparse points and
refuse redirected, directory-substituted, or ownership-uncertain components. Disabled, Recovery, provenance, and
config destinations receive the same containment rule. Activation revalidates the exact destination under the
content lock immediately before mutation; a newly appeared file or changed same-project baseline is preserved,
no foreign provenance is written, and the reviewed operation fails closed. Rollback likewise restores only through
an unchanged safe destination chain.

**VERIFIED:** the focused `43/43` unit and `5/5` managed-content integration suites cover active-folder junctions,
foreign targets, destination-directory swaps, unsafe disabled/Recovery/provenance/config chains, late target
creation, same-project file replacement, rollback, and cancellation. The Release build reports zero warnings and
zero errors.

**PENDING:** ChunkPilot's content lock serializes ChunkPilot operations, not unrelated processes. Windows does not
offer this user-mode path a single atomic conditional replace that also proves the same file identity and a
no-reparse chain, so an external writer can still race the final verification and filesystem mutation. This
external conditional-replace TOCTOU remains disclosed rather than claimed eliminated.

---

## CP-2026-058 — Runtime certification inspected only one IPv4 TCP port instead of every owned endpoint

| Field | Value |
|---|---|
| Date | 2026-08-29 |
| Git state | `codex/curseforge-runtime-browser-parity`; implementation commit pending |
| Provider identity | No provider request or credential is needed; deterministic native-table fixtures only |
| Severity | Critical — an owned process could open a wildcard, non-loopback, IPv6, UDP, or secondary listener while the selected IPv4 TCP port still appeared loopback-only |
| Area | Headless runtime certification, Windows endpoint ownership, Job/process identity |
| Status | **Fixed locally; headless deterministic evidence only** |
| Validation | `CurseForgeRuntimeCertificationTests` (`54/54`) and zero-warning build, including native TCP/UDP IPv4/IPv6 parsing, wildcard/secondary-port rejection, Job/subtree ownership, repeated capture, stable-process socket mutation, ephemeral-child/PID-reuse, and sanitized-failure regressions |

### Reproduction and root cause

The original proof queried owners only for the selected TCP port. It could prove that one IPv4 listener belonged
to the certification Job, but it did not inventory other TCP ports, IPv6 listeners, or UDP endpoints owned by the
same server subtree. A passing result therefore did not prove the complete loopback-only endpoint policy.

### Resolution and boundary

The certifier now captures Windows owner-PID tables for TCP listeners and UDP endpoints in both address families.
It performs two complete TCP4/TCP6/UDP4/UDP6 inventories bracketed by three stable Job snapshots. Job process
accounting and the complete PID/creation set must match before, between, and after the inventories, and the exact
owned endpoint multiset (transport, family, address bytes/scope, port, and PID) must match across both observations.
Every accepted owner is then revalidated live and must remain under the captured task-server process subtree. The
only approved endpoint is exact loopback TCP on the selected Minecraft port; wildcard, non-loopback, UDP,
secondary-port, identity-raced, unreadable, or mutation-raced evidence fails closed. Only sanitized counts and
policy results enter the report.

**VERIFIED:** 54 focused headless certification tests and a zero-warning build pass with exact parser, ownership,
ephemeral-child, PID-reuse, repeated-inventory, stable-process socket-mutation, and policy fixtures.

**PENDING:** no live server launch is certified here. Each full inventory is still four sequential Windows tables,
not one atomic cross-family snapshot. Two matching observations detect state that persists into either inventory,
but cannot rule out a socket opened and closed entirely between observations or a mutation after the second
observation. The evidence is therefore repeated stable observation, not a frozen or future endpoint guarantee.

---

## CP-2026-057 — Free-space checks used a drive-root approximation instead of the actual mounted target volume

| Field | Value |
|---|---|
| Date | 2026-08-29 |
| Git state | `codex/curseforge-runtime-browser-parity`; implementation commit pending |
| Provider identity | Deterministic Windows volume fixtures and one local-volume probe only; no provider payload |
| Severity | High — junctions or mounted folders could make two paths share a different capacity balance than their apparent drive roots |
| Area | Update/preflight storage forecasting, Windows mount and volume resolution |
| Status | **Fixed locally; headless deterministic evidence only** |
| Validation | `StorageSpaceGuardTests` mounted-alias, fail-closed, and real local-volume cases; focused storage/preflight suite (`7/7`) |

### Reproduction and root cause

The forecast grouped paths using their apparent drive root. On Windows, a directory can resolve through a mount
folder or junction onto another volume, so this could combine unrelated balances or fail to combine two aliases
of the same underlying volume.

### Resolution and boundary

The probe walks to the nearest existing ancestor, opens that exact target, resolves its final path through mount
points, obtains the authoritative volume GUID and mount path, and measures available-to-caller bytes there.
Requirements aggregate by volume GUID. Unresolved, redirected-to-network, unsupported, or contradictory volume
evidence fails closed.

**VERIFIED:** deterministic aliases of one mounted volume aggregate before approval, resolution failure refuses
the forecast, and the Windows probe returns a real local volume identity headlessly.

**PENDING:** no real low-disk or live CurseForge transfer was exercised by this entry.

---

## CP-2026-056 — Certification temporary files escaped the recyclable run root

| Field | Value |
|---|---|
| Date | 2026-08-29 |
| Git state | `codex/curseforge-runtime-browser-parity`; implementation commit pending |
| Provider identity | No provider request or credential value is needed to reproduce this path-boundary defect |
| Severity | High — bundle extraction and temporary files could remain outside the task-owned evidence and cleanup boundary |
| Area | Headless certification wrapper, temporary environment, recoverable cleanup |
| Status | **Fixed locally; headless deterministic evidence only** |
| Validation | `RecoverableCleanupMovesOnlyExactRunAndRetainsCampaignEvidence`, unexpected-sibling and reparse-point refusal tests, failed/cancelled cleanup ordering, focused certification suite |

### Reproduction and root cause

The wrapper created disposable data and managed-server trees but inherited global `TEMP`, `TMP`, and .NET
single-file extraction locations. Cleanup proved only the data/server siblings, so task-created temporary content
could escape both the bounded inventory and the recoverable removal decision.

### Resolution and boundary

Every fresh run now owns exactly `data`, `servers`, and `temp` below one marked run root. The wrapper scopes
`TEMP`, `TMP`, and `DOTNET_BUNDLE_EXTRACT_BASE_DIR` to that `temp` tree only while starting the packaged controller,
then restores the caller environment. Final cleanup refuses unexpected siblings or reparse points, inventories the
complete bounded run tree, and moves that exact root to the Windows Recycle Bin. It never falls back to permanent
recursive deletion; failure retains the run for review.

**VERIFIED:** deterministic cleanup fixtures prove exact-root selection, temp inclusion, evidence-before-move,
unexpected-entry retention, reparse refusal, and no cleanup before owned Jobs and ports are gone.

**PENDING:** a large live campaign and its final Recycle Bin move are not certified by this entry.

---

## CP-2026-055 — A dirty or hidden worktree could be stamped as the current HEAD certification candidate

| Field | Value |
|---|---|
| Date | 2026-08-29 |
| Git state | `codex/curseforge-runtime-browser-parity`; implementation commit pending |
| Provider identity | No provider call or key is involved; Git/package fixtures only |
| Severity | Critical — certification could execute binaries or a controller whose source was not exactly the claimed commit |
| Area | Development packaging, Git provenance, packaged certification controller |
| Status | **Fixed locally; headless deterministic evidence only** |
| Validation | package-input staged/untracked/ignored/skip-worktree regressions, dirty-build-then-revert rejection, complete package-manifest tamper tests; focused unit suite (`60/60`); Certification Release build with zero warnings/errors; both PowerShell scripts parse |

### Reproduction and root cause

The prior freshness check primarily compared the stamped executable version with `HEAD`. A development build made
from dirty, staged, untracked, ignored-source, `skip-worktree`, or `assume-unchanged` input could carry that same
stamp. The headless controller could also run from persistent `bin`/`obj` output outside the package manifest.

### Resolution and boundary

Packaging computes a canonical digest of package-affecting blobs across `HEAD`, the index, and the actual
worktree, including hidden Git states. A certifiable build is materialized from an isolated `git archive` of the
exact commit. The development manifest records those input digests and SHA-256/size for every packaged file.
The controller is published under `artifacts\dev-current\Certification`, included in that manifest, and the
wrapper executes only this packaged copy after independently rechecking commit, input proof, product versions,
complete inventory, and file hashes. A dirty build may still be useful for local development, but it is marked
ineligible and cannot enter certification.

**VERIFIED:** deterministic tests reject each hidden-diff and post-build-revert shape plus added, removed, changed,
duplicated, redirected, or stale manifest content. The current uncommitted worktree correctly reports ineligible.

**PENDING:** no final clean-commit package or live certification has been produced by this entry.

---

## CP-2026-054 — Case-insensitive JAR filename collisions could replace unrelated add-on ownership

| Field | Value |
|---|---|
| Date | 2026-08-29 |
| Git state | `codex/curseforge-runtime-browser-parity`; implementation commit pending |
| Provider identity | Synthetic exact releases and local JAR fixtures only; no live provider download |
| Severity | Critical — two reviewed files or an existing user/different-project file could resolve to the same Windows destination |
| Area | Mod/plugin plan authorization, exact-plan and single-release installation, provenance |
| Status | **Fixed locally; headless deterministic evidence only** |
| Validation | `Registration_rejects_case_insensitive_destination_filename_collisions`; exact-plan pre-download, existing-owner, late-race, and single-release collision regressions in `PluginManagementTests` |

### Reproduction and root cause

Windows treats `Library.jar` and `library.JAR` as the same destination. Plan identity was exact by project/release,
but it did not require destination filenames to be unique case-insensitively or prove that an existing path had
same-project provider ownership. A user file, another project's file, or a file created during download could
therefore be overwritten or moved as if it belonged to the incoming release.

### Resolution and boundary

Authorization rejects any plan assigning one case-insensitive destination filename to multiple releases.
Materialization refuses an existing user-owned, ownership-uncertain, or different-project destination before
download and rechecks immediately after download before activation. The same rule applies to ordinary
single-release provider installs. Refusal preserves the existing bytes and provenance and creates no Recovery
entry for a file ChunkPilot does not own.

**VERIFIED:** deterministic tests prove zero downloads for known collisions, preservation of existing bytes,
post-download race refusal, and no false ownership or Recovery mutation.

**PENDING:** no live CurseForge mod/dependency install or removal is certified by this entry.

---

## CP-2026-053 — Cancellation could lose a race with delayed Begin under a reusable empty operation identity

| Field | Value |
|---|---|
| Date | 2026-08-29 |
| Git state | `codex/curseforge-runtime-browser-parity`; implementation commit pending |
| Provider identity | Deterministic operation/server/project/file identities only; no credential or payload |
| Severity | Critical — cancellation could return while delayed creation or managed-content Begin still consumed one-use authority and started work |
| Area | App-to-Agent acceptance, creation and managed-content cancellation, operation identity |
| Status | **Fixed locally; headless deterministic evidence only** |
| Validation | `AgentOperationAcceptanceTests` (`7/7`), creation acceptance integration (`4/4`), managed-content authorization integration (`5/5`), including empty-ID refusal and both Begin/cancel orderings |

### Reproduction and root cause

A caller could stop waiting before the Agent knew whether Begin had arrived. Lookup-and-retry alone left a gap:
cancellation could observe no operation, return, and then a delayed Begin could consume the authorization and
start. Accepting `Guid.Empty` on these external exact-operation paths also allowed the Agent to substitute a new
identity, defeating reconciliation of the same request.

### Resolution and boundary

Cancellation now sends the complete exact request to an Agent method serialized under the same gate as Begin.
If Begin won, that registered operation is cancelled. If cancellation won, the Agent reserves the operation ID
as a terminal pre-start tombstone, revokes unconsumed authorization, and makes delayed exact Begin reattach to the
cancelled result without starting work. Changed same-ID requests fail closed. App and Agent reject `Guid.Empty`
before authorization, lookup, Begin, or fencing on these externally controlled workflows.

**VERIFIED:** deterministic tests cover lost fence acknowledgements, capacity retry, cancel-before-Begin,
Begin-before-cancel, replay, mismatch, zero authorization/download after pre-cancel, and empty identity refusal.

**PENDING:** this does not certify transport loss against a live packaged Agent or a real provider operation.

---

## CP-2026-052 — Update and preflight storage checks omitted simultaneous same-volume and generated payload needs

| Field | Value |
|---|---|
| Date | 2026-08-29 |
| Git state | `codex/curseforge-runtime-browser-parity`; implementation commit pending |
| Provider identity | Deterministic byte counts and volume fixtures only; no live provider payload is claimed |
| Severity | High — an update could pass its initial free-space check, then exhaust the shared volume while downloading, snapshotting, or materializing a generated candidate |
| Area | CurseForge client-manifest preflight, download-only update, recovery snapshot, package cache, candidate staging |
| Status | **Fixed locally; live payload/runtime certification still pending** |
| Validation | `StorageSpaceGuardTests`, `Exact_client_size_is_checked_against_staging_space_before_CDN_download`, `CurseForge_generated_update_forecast_includes_the_sealed_dependency_plan_before_snapshot`, focused preflight/update suites, full unit suite, Release solution build |

### Reproduction

The old update check compared snapshot and candidate requirements independently even when both paths resolved to
the same Windows volume. Two individually acceptable requirements could therefore exceed the single shared free-
space balance in aggregate. The forecast also omitted the exact resolved dependency bytes for a generated
CurseForge candidate, did not reserve package-cache space separately, and used the renderer-shaped target before
CurseForge preflight had established authoritative sizes. Client-manifest preflight itself began the CDN download
without first checking space for the exact archive.

Deterministic fixtures reproduce the failure with two same-volume requirements that each fit but do not fit
together, a sealed two-GiB generated dependency graph, zero available staging space before a client-manifest
download, and values near `long.MaxValue`.

### Root cause and impact

Free-space checks were path-local comparisons rather than one forecast grouped by storage-volume identity.
Generated materialization was added after the original package-expansion estimate and its sealed `TotalResolvedBytes`
was not included. Ordinary checked arithmetic could also overflow into an unrelated arithmetic failure at extreme
sizes. A late disk-full failure could consume bandwidth, leave recoverable staging, or interrupt snapshot or
candidate construction even though the UI had already accepted the operation.

### Resolution

`StorageSpaceGuard` now resolves every requirement to its actual volume, adds simultaneous requirements on the
same volume, evaluates distinct volumes independently, and uses saturating add/multiply operations. Update
preparation forecasts current-server snapshot bytes plus reserve, package cache plus download reserve, candidate
expansion plus reserve, and the validated sealed generated-plan total. Download-only updates reserve the
canonical package size before transfer. CurseForge preflight reserves the exact client archive size plus its
download margin before the CDN request.

All CurseForge update storage decisions now follow native exact-release preflight, so a small renderer-supplied
size cannot approve a larger official or generated operation. An insufficient forecast returns before the
archive download, recovery snapshot, or server switch.

### Final boundary

**VERIFIED:** deterministic storage tests prove same-volume aggregation, independent-volume handling, overflow-
safe arithmetic, no CDN request when client-manifest staging is insufficient, inclusion of the complete sealed
generated payload, and failure before snapshot or world mutation. The full unit suite and Release build passed
headlessly.

**PENDING:** these synthetic forecasts do not certify a completed live CurseForge payload, real low-disk Windows
volume, update, rollback, or recovery campaign.

---

## CP-2026-051 — A lost managed-content begin acknowledgement could consume the plan but strand observation

| Field | Value |
|---|---|
| Date | 2026-08-29 |
| Git state | `codex/curseforge-runtime-browser-parity`; implementation commit pending |
| Provider identity | Deterministic project/file/server identities only; no credential or live payload is used |
| Severity | High — exact plan authority could be consumed while the App reported a transport failure and could not prove whether installation was already running |
| Area | Managed-content begin, one-use dependency-plan authorization, named-pipe acknowledgement, server-selection fence |
| Status | **Fixed locally; live mod/dependency certification still pending** |
| Validation | `AgentOperationAcceptanceTests` managed-content acknowledgement cases, `ManagedContentPlanAuthorizationIntegrationTests` exact replay/cross-server cases, focused integration suite, full unit suite, Release App/Agent builds |

### Reproduction

Begin a reviewed CurseForge mod-with-dependencies install, allow the Agent to consume the exact one-use plan and
create operation state, then lose the named-pipe response before the App receives it. The caller previously saw
only an exception. Cleanup could revoke local review evidence, but the Agent might already be downloading. A
retry could not distinguish a lost acknowledgement from a rejected request and could encounter an already-
consumed authorization or duplicate operation.

The deterministic regression injects that response-loss shape, an explicit exact-ID lookup miss, a returned
operation belonging to another server, and same-ID cross-server replay.

### Root cause and impact

Plan authorization was one-use, but command acceptance and delivery of the acceptance response were treated as
one event. The bridge had no non-throwing exact-operation lookup, and Agent begin did not define identical replay
as idempotent. A local I/O, timeout, cancellation, or decode failure could therefore strand authoritative Agent
work behind a false client-side failure or encourage an unsafe second begin.

### Resolution

The App generates the managed-content operation ID before dispatch. After an ambiguous transport failure it asks
`FindManagedContentOperation` for that exact ID. A found snapshot is accepted only when operation, server, kind,
provider, project, and release all match; only an explicit miss permits replay of the unchanged request.

`ManagedContentOperationCoordinator` serializes acceptance, records the original server/project/release/provider,
dependency mode, restart choice, and authorization identity, and consumes the stored plan inside that gate. An
identical replay returns the existing snapshot without re-consuming the authorization or downloading again. A
different request under the same ID fails closed. Once accepted, the App registers the authoritative observer
before a later selection fence can reject the renderer response.

### Final boundary

**VERIFIED:** deterministic tests prove lookup-after-response-loss, unchanged replay only after an explicit miss,
wrong-server lookup rejection, exact replay without second plan consumption, cross-server same-ID
refusal, and one materialization of the reviewed files. The focused integration suite, full unit suite, and
Release App/Agent builds passed headlessly.

**PENDING:** no completed live CurseForge mod/dependency download, install/remove workflow, or packaged visual
acceptance is claimed by this entry.

---

## CP-2026-050 — A lost CurseForge creation acknowledgement could leave accepted work reported as failed

| Field | Value |
|---|---|
| Date | 2026-08-29 |
| Git state | `codex/curseforge-runtime-browser-parity`; implementation commit pending |
| Provider identity | Deterministic official-pack request identity only; no credential or live archive is used |
| Severity | High — a one-use creation authorization could be consumed and a server creation started while the App treated the missing response as rejection |
| Area | CurseForge creation begin, exact preflight authorization, named-pipe acknowledgement, cancellation/reattach |
| Status | **Fixed locally; live creation/runtime certification still pending** |
| Validation | `AgentOperationAcceptanceTests` creation cancellation/reattach case, `ModpackCreationAcceptanceIntegrationTests`, focused integration suite, full unit suite, Release App/Agent builds |

### Reproduction

Allow `BeginModpackCreation` to reach the Agent, consume its one-use CurseForge preflight authorization, and
register the operation, then drop or cancel the response before the App decodes it. The App previously could only
surface the transport exception and revoke its local evidence. The Agent could still be creating the server, but
an ordinary retry could collide with the existing operation or fail because the authorization had already been
consumed.

The deterministic regressions inject a lost/cancelled acknowledgement, twelve concurrent exact replays, and a
same-ID replay whose destination name differs from the accepted request.

### Root cause and impact

Creation already had a durable client-generated operation ID, but the begin route was not a complete
acknowledgement-loss protocol. There was no non-throwing exact-ID lookup and no serialized Agent record proving
whether a replay was the same full client-controlled plan. Transport cancellation was therefore incorrectly
usable as evidence that no Agent operation existed.

### Resolution

After an ambiguous I/O, timeout, cancellation, or response-decode failure, the App queries `FindInstallProgress`
for the exact operation ID. It reattaches when found and replays only after an explicit miss. If cancellation is
already requested, it performs bounded exact lookups without replaying work onto a changed selection; when
acceptance is found, it sends the cancellation intent to that authoritative operation.

`InstallationCoordinator` serializes begin by operation ID, records every client-controlled
`ModpackCreationPlan` field, consumes the one-use authorization at most once, and returns the existing operation
for an identical replay. Changed fields, an injected trusted generated plan, or collision with another install
identity fail closed.

### Final boundary

**VERIFIED:** deterministic tests prove cancellation reattaches to the accepted operation, twelve concurrent
exact replays authorize once, the accepted operation remains queryable, and a changed same-ID request is refused.
The focused integration suite, full unit suite, and Release App/Agent builds passed headlessly.

**PENDING:** this entry does not certify a completed live official server-pack or generated-candidate creation,
server launch, packaged visual flow, or behavior across Agent restart.

---

## CP-2026-049 — The long-lived App could pass the CurseForge credential-source path to an elevated helper

| Field | Value |
|---|---|
| Date | 2026-08-29 |
| Git state | `codex/curseforge-runtime-browser-parity`; implementation commit pending |
| Provider identity | No provider request or credential value is needed to reproduce this defect |
| Severity | High — a bootstrap-only secret-source location remained ambient App state and could cross into a later shell-launched elevated process |
| Area | App startup, exact Agent launch, Windows environment inheritance, elevated firewall helper |
| Status | **Fixed locally** — the App captures the path once, clears its process environment immediately, and supplies it only to the exact non-shell Agent child |
| Validation | `App_moves_the_bootstrap_source_to_only_the_exact_Agent_child_environment`, credential child-environment regressions, focused credential suite, Release App build |

### Reproduction

`CHUNKPILOT_CURSEFORGE_KEY_FILE` contains a path, never key contents, but the App previously retained that variable
for its full lifetime so a later Agent launch could inherit it. The same long-lived App can also start the narrow
Windows Firewall helper through `UseShellExecute=true` and the `runas` shell verb. That shell path does not support
the positive environment filtering used for managed non-shell children, so the helper could inherit the
credential-source path even though firewall work has no need for it.

The defect is an environment-boundary leak, not evidence that the helper opened the file or that credential bytes
were exposed. A synthetic absolute path is sufficient to reproduce the unwanted inheritance shape.

### Root cause and impact

The path-only override was treated as general App process state until the Agent removed it after bootstrap.
Clearing the variable inside the Agent protected Agent descendants but could not retroactively remove it from the
parent App or from unrelated children the App launched. A provider-specific removal on non-shell children also
cannot constrain Windows shell/elevation inheritance.

### Resolution

`AgentClient` now captures the optional source path once at construction and immediately removes
`CHUNKPILOT_CURSEFORGE_KEY_FILE` from the App process environment. Only the exact `ChunkPilot.Agent.exe`
`ProcessStartInfo`, which uses `UseShellExecute=false`, receives that captured path. The private reference is
discarded when an existing Agent answers or immediately after the Agent child starts. The Agent retains its
existing import-and-clear behavior, so neither the long-lived App nor subsequent browser, shell, or elevated
helper launches inherit the source path.

### Final boundary

**VERIFIED:** the deterministic regression proves the live App environment is cleared, the exact non-shell Agent
start receives the synthetic path, and an unrelated shell start has no such variable. The focused credential
suite and Release App build pass headlessly.

**PENDING:** this correction does not claim that immutable managed strings can be forensically erased, and it
does not change the public credential-delivery gate. No key value was read, logged, packaged, or sent to the
firewall helper during this verification.

---

## CP-2026-048 — CurseForge review state was replayable and not bound to one exact Agent operation

| Field | Value |
|---|---|
| Date | 2026-08-29 |
| Git state | `codex/curseforge-runtime-browser-parity`; implementation commit pending |
| Provider identity | Deterministic numeric project/file fixtures only; no credential or provider response is retained by this entry |
| Severity | Critical — a copied, stale, wrong-server, or replayed review could reach a provider-backed creation or dependency installation without proving that the current Agent still authorized that exact action |
| Area | CurseForge modpack preflight, generated-candidate file graph, mod/dependency planning, App/WebUI cancellation and selection fences |
| Status | **Fixed locally; live payload/runtime certification still pending** |
| Validation | `CurseForgeCreationAuthorizationRegistryTests`, `CurseForgePreflightEvidenceStoreTests`, `CurseForgeManagedContentPlanAuthorizationRegistryTests`, `CurseForgeManagedContentPlanEvidenceStoreTests`, `ManagedContentPlanAuthorizationIntegrationTests`, WebUI contract and provider-plan renderer regressions |

### Reproduction

A successful CurseForge creation preflight returned exact evidence, but the later creation request had no
Agent-lifetime, single-use authorization proving that it consumed that review. Dependency planning likewise
returned a renderer-carried plan that could be replayed, paired with another server, or installed after the
selection changed. Cancellation, renderer disconnect, response loss, or a late completion could leave a review
usable even though the App no longer displayed it. Repeating that race could also accumulate outstanding reviews.

The deterministic regressions exercise an altered creation field, an injected generated file plan, replay of a
consumed review, a dependency-plan digest forgery, a server switch, a root project/file mismatch, cancellation
before a slow response becomes ready, 65 rapid invalidations, and late cleanup racing a newer review.

### Root cause and impact

Exact provider resolution and user review were separated in time, but continuity between them was represented by
presentation data rather than Agent-held, one-time operation authorization. The App had no exact revocation
contract for abandoned reviews, and the Agent did not retain the authoritative dependency graph independently of
the renderer. Provider metadata could therefore be resolved again at materialization or a stale request could
attempt to consume evidence outside its original server/release context.

### Resolution

The Agent now deep-copies each ready creation review into a short-lived, bounded registry. The authorization is
bound to the exact project, client file, official server file when present, approved URLs, sizes, hashes,
Minecraft/loader/Java identity, and the complete generated required-dependency plan. It can be consumed once;
expiry, mismatch, injection, and replay fail closed. A generated candidate receives only the Agent's stored plan.

CurseForge mod/dependency plans use a separate short-lived registry bound to server ID, provider, root project,
root file, ordered exact releases, and a SHA-256 digest. Installation consumes the stored plan before operation
state is created and performs no second provider metadata resolution. Modrinth's existing path is unchanged.

The App keeps only the current exact review identity. Cancellation, provider/project/release replacement, server
switch, explicit invalidation, failed preflight send/response delivery, and a creation begin explicitly reconciled
as unaccepted revoke that exact authorization. Creation uses a pre-completion revocation tombstone so cancellation
can win even when the slow Agent preflight has not registered yet. Generation fences ensure late cleanup for an
old review cannot erase a newer one. Accepted-work acknowledgement loss is handled separately by
[CP-2026-050](#cp-2026-050--a-lost-curseforge-creation-acknowledgement-could-leave-accepted-work-reported-as-failed)
and [CP-2026-051](#cp-2026-051--a-lost-managed-content-begin-acknowledgement-could-consume-the-plan-but-strand-observation).

### Final boundary

**VERIFIED:** deterministic unit and integration regressions prove exact binding, bounded capacity, monotonic
expiry, cancellation-before-ready revocation, wrong-server rejection, digest/plan tamper rejection, one-use
consumption, replay failure, no second dependency resolution, and preservation of a newer review during stale
cleanup. Release builds and the focused suites passed headlessly.

**PENDING:** no statement here certifies a completed live CurseForge archive download, server launch, mod install,
update, rollback, or packaged visual workflow. Those remain part of the separate runtime and user-acceptance gate.

---

## CP-2026-047 — CurseForge update preflight verified identity but retained caller-controlled artifact fields

| Field | Value |
|---|---|
| Date | 2026-08-29 |
| Git state | `codex/curseforge-runtime-browser-parity`; implementation commit pending |
| Provider identity | Deterministic project `123`, client file `500`, official server file `501`; no live file identity retained |
| Severity | Critical — a forged URL, hash, size, filename, package type, or generated file plan could remain operational after nominal exact-release preflight |
| Area | Whole-pack update download, cache reuse, official server-pack selection, generated-candidate materialization |
| Status | **Fixed locally; live update certification still pending** |
| Validation | `VersionUpdateIntegrationTests` covers preflight ordering, forged operational fields, project/client/server relationship mismatch, official/generated contradiction, missing exact generated plan, JSON wire exclusion, and canonical CDN download |

### Reproduction

The previous update preflight checked project/client/server-file identity and the manifest-derived platform, then
returned the original `PackVersionInfo` with only Minecraft, loader, loader version, and Java major replaced. The
request's download URL, declared size, hashes, filename, package type, declared files, and generated-path choice
remained authoritative. The package type was also used to decide whether the selected file represented an
official server pack before preflight, allowing an untrusted field to shape the trust check itself.

A deterministic request can therefore name the correct numeric release while supplying an attacker URL, tiny
size, unrelated digests, a JAR filename, and a contradictory package type. Before this correction, exact manifest
preflight did not replace those operational fields.

### Root cause and impact

The provider preflight was treated as a compatibility assertion instead of the sole source of download authority.
That left a time-of-check/time-of-use split between exact native provider evidence and the wire model later used
for cache selection, download, verification, and extraction. Generated updates also re-resolved dependencies from
the manifest during materialization rather than consuming the exact graph reviewed at preflight.

### Resolution

Every CurseForge update path now performs native operation-time preflight before free-space evaluation, snapshot,
reviewed-cache reuse, cache lookup, or download. The linked numeric project and selected client/provider file IDs
determine whether an official server relationship is expected. The ready result must match that exact tuple.

The Agent then replaces every operational artifact field with approved preflight evidence: URL, bounded size,
provider SHA-1, locally verified client SHA-256 when applicable, safe filename, official `zip` or generated
`curseforge-manifest` package type, cleared unrelated hashes/declared files, and exact platform/Java identity.
Generated updates require the complete validated dependency plan from that preflight. The trusted plan property is
excluded in both JSON directions, so App/renderer input can neither supply nor observe native download authority.

### Final boundary

**VERIFIED:** deterministic tests prove preflight occurs before cache reuse and local mutation, forged artifact
fields are replaced, contradictory identity or package evidence is rejected, generated materialization cannot
proceed without the exact native plan, that plan does not cross JSON, and the downloader uses the canonical
approved CDN URL and integrity evidence. The focused update suite and Release build passed headlessly.

**PENDING:** no real provider-backed update, rollback, or injected-failure recovery is claimed by this entry.

---

## CP-2026-046 — Credential plaintext and parent environment survived beyond their minimum child boundary

| Field | Value |
|---|---|
| Date | 2026-08-29 |
| Git state | `codex/curseforge-runtime-browser-parity`; implementation commit pending |
| Provider identity | No provider request or credential value is needed to reproduce this defect |
| Severity | High — an abandoned plaintext allocation could remain until garbage collection, and managed or downloaded child code inherited unrelated variables from the process that launched ChunkPilot |
| Area | CurseForge credential import, managed server launch, Java/loader helpers, staged validation, approved automation children, runtime certification |
| Status | **Fixed locally** — original plaintext allocation is zeroed and child code starts from a bounded inherited environment |
| Validation | `Originally_allocated_plaintext_buffer_is_zeroed_after_import`, `ChildProcessEnvironmentPolicyTests`, managed-server child-environment integration regressions, and audited native child launch paths |

### Reproduction

Credential provisioning allocated a 4,097-byte read buffer, then called `Array.Resize` to keep only the bytes
read. When the length changed, .NET allocated a second array and the `finally` block zeroed only that smaller
replacement. The original allocation containing the file bytes became unreachable without first being cleared.

Separately, removing only `CHUNKPILOT_CURSEFORGE_KEY_FILE` from a child left every other inherited parent variable
available to managed servers, downloaded Java/loader helpers, staged candidates, approved external automation,
and certification processes. A synthetic secret-like environment sentinel demonstrates that the former launch
shape propagated unrelated parent state even though no child needed it.

### Root cause and impact

The credential cleanup followed the current array reference rather than the original allocation. Child launch
hardening was also expressed as one provider-specific removal instead of a positive inheritance policy. Neither
defect writes a credential to disk or logs, but both unnecessarily broaden secret lifetime and the set of ambient
state visible to code outside the trusted App/Agent boundary.

### Resolution

Provisioning decodes only the populated span of the original bounded allocation and zeroes that same full
allocation in `finally`; it no longer resizes away from the buffer that must be cleared. The test hook observes
only the post-zeroed allocation and proves every byte is zero.

`ChildProcessEnvironmentPolicy` now replaces inherited state with a small Windows/Java execution baseline before
explicit per-server variables are applied. Managed servers, Java discovery/installers, loader helpers, staged
validation, approved automation programs, and runtime certifiers use this policy; the CurseForge source-path
variable is still removed explicitly. Shell execution is rejected where this restricted environment is required.

### Final boundary

**VERIFIED:** deterministic tests prove the original allocation is cleared, unrelated parent sentinels are
removed, required Windows/Java variables remain, explicit server variables are restored afterward, and a shell
launch cannot pretend to enforce the policy. Focused tests and Release builds passed headlessly.

**PENDING:** these tests do not claim memory-forensic erasure of immutable .NET strings or replace the existing
public credential-delivery gate. The bounded developer credential remains local and is not packaged.

---

## CP-2026-045 — Modpack discovery performed per-card provider work and leaked browser state across providers

| Field | Value |
|---|---|
| Date | 2026-08-29 |
| Git state | `codex/curseforge-runtime-browser-parity`; local browser checkpoint based on `a5ec5df9846122442589015c8a340023b1657e8e`, commit pending |
| Provider identity | Official Minecraft game `432`; live aggregate evidence only, with no credential value or response body retained |
| Severity | High — normal browsing could multiply one visible page into hundreds of serialized API reads, show another provider's rows under the selected provider, and repeat or hide catalog results |
| Area | Create Server v2, Modrinth/CurseForge discovery, pagination, provider switching, exact-release detail |
| Status | **Fixed locally; live runtime certification still pending** |
| Validation | Shallow-search/no-N+1 and three-page provider regressions; renderer cancellation, provider-session, pagination, underfilled-page, query-scroll, lazy-detail, preflight-detail and virtualization regressions; direct packaged live browser inspection |

### Reproduction

The previous renderer hardcoded a 20-item page. CurseForge discovery then resolved file and server-pack
relationships serially for every visible project before returning the page. Static path analysis showed a
worst-case upper bound of about 1,201 provider reads for 20 cards. This delayed the first useful result and made
ordinary browsing perform exact-release work the user had not requested.

Browser state was shared across providers. Switching from CurseForge to Modrinth could briefly relabel the old
CurseForge rows as Modrinth rows, and a late response could overwrite the new provider. Modrinth did not send its
requested offset, so **Load more** could fetch page one again. Early CurseForge server-path filtering also
underfilled a page while renderer pagination inferred position from the accepted item count, hiding or repeating
later provider rows.

Two related presentation defects appeared while correcting the browser. Starting a new query while the result
list remained deeply scrolled could immediately trigger an unintended second-page request. A completed
CurseForge preflight updated the parent-owned exact release but could leave the selected detail pane displaying
the earlier preflight-required state.

### Root cause and impact

Discovery and exact-project resolution were one operation even though cards need only shallow project metadata.
The generic catalog contract did not carry provider totals and cursors, the Modrinth adapter omitted `offset`,
and renderer query, selection, paging, and scroll state had one owner rather than one session per provider.
Consequently local filtering and stale asynchronous completion could change provider position or visible
identity. The result was slow, sparse and sometimes factually misleading discovery rather than merely a visual
density problem.

### Resolution

Both providers now return shallow 50-project pages with the provider's true cursor and total. CurseForge search
does no per-card file request; selecting one card lazily resolves its exact project/release detail. The renderer
deduplicates appended rows, keeps a bounded 200-item session, virtualizes beyond the first 20 cards, automatically
loads near the end, and retains an accessible **Load more** action. Search, filters, sort, loaded pages,
selection, and scroll position are isolated per provider. Superseded requests are cancelled and fenced, and a
cached provider session restores without a network refetch.

Modrinth now sends the requested offset. Cursor advancement remains based on the raw provider page even when
policy rejects or cross-page deduplication removes visible rows. A new query resets the list to the top before it
can satisfy the append threshold, and parent preflight output is synchronized into the selected exact-detail
model. Shallow cards explicitly say that server setup has not been checked rather than claiming compatibility.

### Final boundary

**VERIFIED:** in the packaged live candidate, Modrinth rendered 50 of 12,631 results. CurseForge rendered 49,
then 97, then 144 accepted unique rows while its official bounded total remained 10,000; the smaller visible
counts were caused by live cross-page duplicates or policy-invalid rows, while the raw cursor continued correctly.
Returning to an already loaded provider restored the visible session in roughly 130-169 ms without relabelling
rows. Exact SkyFactory 5 detail resolved live and identified its official server pack. Automated regressions cover
the shallow/no-N+1 boundary, three 50-row pages, true offsets/cursors, stale completion, provider session restore,
query scroll reset, lazy exact detail, underfilled pages and preflight-result synchronization.

**PENDING:** the prior 15-second renderer timeout cancelled the first real preflight and its partial download was
deleted. Extended-operation handling is being corrected separately. No archive/server/mod payload, generated
candidate, provider-backed update, rollback, or controlled recovery is certified by this browser checkpoint, and
final user acceptance has not occurred.

---

## CP-2026-044 — Credential presence and isolated harnesses could cross the approved source boundary

| Field | Value |
|---|---|
| Date | 2026-08-29 |
| Git state | `codex/curseforge-live-certification`; implementation commit `3f505aa1bdf3a9b57b99ddff909132da9b18394d` plus the final verification checkpoint containing this entry |
| Provider identity | Official Minecraft game `432`; no credential value, derivative, or source-file detail retained |
| Severity | High — a presence check unnecessarily decrypted protected data, and an isolated test child could use the developer-approved live source instead of proving missing-credential behavior |
| Area | Agent SelfTest, integration fixtures, portable Agent smoke, packaged normal-close ownership smoke |
| Status | **Fixed locally** — presence is non-decrypting and every isolated App/Agent child receives an explicitly missing fixture-local key source |
| Validation | Focused security `35/35`, raw named-pipe behavior `1/1`, full isolated integration `355/355`, public-distribution contract `15/15`, packaged Agent smoke, and packaged normal-close/process-ownership smoke passed |

### Reproduction

Agent SelfTest called `GetSecret` only to report whether the CurseForge key existed. Separately, integration and
release harness children set isolated data/instance roots but did not override
`CHUNKPILOT_CURSEFORGE_KEY_FILE`. On a developer machine with the approved default source present, a test intended
to exercise isolated or missing-credential behavior could authenticate against the live provider. The audit found
this as a potential unintended live-auth path; no request count is inferred from the source review.

The first real-child regression also exposed a Windows assertion mistake: `cmd.exe /c set <missing-variable>`
correctly returns nonzero and writes a "variable not defined" diagnostic to stderr. That was a test expectation
failure, not credential exposure; the final assertion requires nonzero exit and proves the synthetic source path
is absent from child output.

### Root cause and impact

Credential isolation was enforced for managed Java/staged validation but not treated as a universal property of
all verification children. SelfTest also reused the decrypting retrieval operation instead of the protected
store's presence operation. This extended plaintext lifetime without need and made local test behavior dependent
on an authorized developer-machine input outside each fixture root.

### Resolution

SelfTest now calls the non-decrypting `ISecretStore.Contains`. A harmless real child proves the key-file variable
is absent after provisioning, and a behavioral raw named-pipe regression proves removal is unknown while status
requires the authenticated UI capability. Every integration-launched App/Agent and both release smoke harnesses
override the source with a nonexistent path inside their temporary fixture root. Public-distribution contract
tests retain that boundary.

### Final boundary

**VERIFIED:** the focused security, behavioral pipe, full integration, distribution-contract, packaged Agent,
and normal-close ownership reruns pass. The release harnesses create and remove only isolated temporary roots and
cannot authenticate with the approved source through environment inheritance.

**UNAVAILABLE:** this fix does not substitute for the blocked live UI close/relaunch DPAPI campaign. The real key
file was not altered, removed, printed, hashed, or copied.

---

## CP-2026-043 — CurseForge API response fields could become durable installed-state records

| Field | Value |
|---|---|
| Date | 2026-08-29 |
| Git state | `codex/curseforge-live-certification`, implementation commit `3f505aa1bdf3a9b57b99ddff909132da9b18394d` |
| Provider identity | Live game `432`; deterministic rollback identity project `123`, client file `456`, installed/server file `789` |
| Severity | High — update checks, labels, URLs, provider hashes, and response descriptions could outlive the user-requested provider session and exceed the reviewed persistence boundary |
| Area | CurseForge update source, histories, journals/logs, generated evidence, mod provenance, snapshots, and update cache |
| Status | **Fixed locally** — one persistence policy now minimizes every reviewed durable sink while retaining exact local rollback identity |
| Validation | `CurseForgePersistencePolicyTests`, `CurseForgeServerCreationTests`, CurseForge identity/rollback and opaque-cache integration regressions; focused security/unit `55/55` and CurseForge update integration `3/3` passed |

### Reproduction

Before the correction, recording a CurseForge update check serialized the complete result. Update sources and
snapshots retained project/release labels, source URLs, changelogs, update notes, and provider-derived names.
Download history retained the provider URL, filename, version and provider digest; cache filenames embedded a
provider hash and filename. Creation/update history and operation logs/journals accepted provider response text,
and generated-pack evidence copied manifest labels, optional project IDs, ignored override paths, and an API size.

These are locally observable persistence defects; no credential is required to reproduce them. The synthetic
sentinel regressions use deliberately recognizable provider labels, descriptions, URLs, filenames, and digests,
then inspect the SQLite rows, operation log/journal text, snapshot metadata, and cache names.

### Root cause and impact

The original transactional model correctly needed an exact installed identity for update and rollback, but it
had no distinction between that safety state and the larger API object that supplied it. Provider models flowed
straight into generic persistence methods and human-readable descriptions. A rollback snapshot also carried only
the client/release ID, so restoring it could not prove the exact installed server file and was tempted to reuse a
newer identity.

This was not a world-integrity failure, but it could create an offline provider-response cache in everything but
name and make a support bundle or local database contain provider data unrelated to recovery.

### Resolution

CurseForge update checks are no longer persisted. The durable update source retains only exact numeric project,
client-file and installed-file IDs, fixed identity origin, launch compatibility, installed time, and fixed local
evidence. Snapshot manifests carry the exact project/client/installed-file tuple and locally computed hashes;
provider release name, URL, changelog, update notes, and release label are removed. A legacy snapshot without exact
installed-file identity fails closed on restore.

Download rows keep local byte count, local SHA-256 and local status, but not provider URL, filename, version or
digest. Cache files use an opaque operation identity. Creation/update history, journals, logs, and recovery
descriptions use fixed local text. Generated evidence recomputes size from the installed file and stores local
relative path/hash/size plus counts, not API label/path lists. Mod provenance uses a fixed origin enum to separate
API-derived operational identity from archive-manifest and user-entered identity.

### Final boundary

**VERIFIED:** deterministic source, download, snapshot, rollback, creation-log/history, generated-evidence,
provenance, and opaque-cache regressions pass (`55/55` focused unit/security and `3/3` CurseForge update
integration); the Infrastructure Release build completed with zero warnings or errors.

**UNAVAILABLE:** no completed live pack install/update existed at this checkpoint, so a post-operation scan of a
real provider-backed server database, snapshot, journal, and cache cannot be claimed. Written CurseForge approval
for retaining even the minimum installed identity/local ownership evidence also remains unavailable; public
activation stays gated.

---

## CP-2026-042 — CurseForge creation and update lacked exact client-manifest loader proof

| Field | Value |
|---|---|
| Date | 2026-08-29 |
| Git state | `codex/curseforge-live-certification`, implementation commit `3f505aa1bdf3a9b57b99ddff909132da9b18394d` |
| Provider identity | Deterministic project `10`, client file `111`, official server-pack file `222`; live exact-project/file discovery was verified but the live identity was intentionally not copied into this durable register |
| Severity | Critical — an official server archive could be materialized using only a loader family, without proving the exact loader version required to launch it |
| Area | Create Server v2, exact CurseForge release review, generated candidate, official server pack, update preflight |
| Status | **Fixed locally** — creation and update now require a verified exact client manifest before snapshot, materialization, or active switch |
| Validation | `CurseForgeModpackPreflightServiceTests`, creation-plan contract, WebUI preflight bridge/UI regressions, and `CurseForge_update_requires_exact_client_manifest_preflight_before_snapshot_or_switch` |

### Reproduction

The live file response established values such as Minecraft version and `Forge`/`Fabric`, but that list is a
compatibility family, not an exact loader build. The prior release mapper could still mark an integrity-verifiable
official server pack or generated path as creatable while `LoaderVersion` was empty or inferred from incomplete
API metadata. An update could reach recovery-point creation before discovering that uncertainty.

The deterministic fixture makes the API list deliberately misleading while the exact downloaded client
`manifest.json` declares `forge-47.3.0`. Before this gate, the API family could be treated as sufficient evidence.

### Resolution

The native Agent now performs an operation-scoped preflight for the exact project/client/server-file tuple. It
downloads the exact client archive from the approved CDN path, enforces declared size and provider SHA-1,
computes a local SHA-256, safely reads the bounded manifest, establishes the primary loader and version,
Minecraft version and required Java major, and removes staging on success, unsupported content, hash failure,
or cancellation. The official-server-pack path still requires this client-manifest proof.

The WebUI can create only the exact release returned ready by preflight and carries its local client-archive
SHA-256 into the Agent request. The Agent independently rechecks the creation plan. CurseForge update preflight
runs before a recovery snapshot or active-directory mutation and fails if the returned identities disagree.

### Final boundary

**VERIFIED:** exact Forge loader extraction, unsupported-primary-loader handling, hash failure, cancellation,
staging cleanup, creation gating, identity contradiction, and pre-switch update rejection are deterministic and
covered.

**UNAVAILABLE:** the live checkpoint resolved an exact client file and approved CDN URL but did not download and
preflight that archive, build a candidate, or launch it. Real-pack loader/runtime certification remains open.

---

## CP-2026-041 — Unknown CurseForge distribution state was allowed and filtered pages lost provider position

| Field | Value |
|---|---|
| Date | 2026-08-29 |
| Git state | `codex/curseforge-live-certification`, implementation commit `3f505aa1bdf3a9b57b99ddff909132da9b18394d` |
| Provider identity | Minecraft game `432`, modpack class `4471`; synthetic availability IDs `123/456/789`; live dynamically selected two-page result |
| Severity | High — absent/null distribution evidence could become selectable, while filtering a provider page could repeat or skip later results |
| Area | CurseForge modpack/mod discovery, distribution control, provider pagination |
| Status | **Fixed locally** — availability and distribution now require explicit affirmative evidence and pagination follows the raw provider cursor |
| Validation | Live two-page pagination with no repeated project; distribution/file fail-closed fixtures; exact-category and underfilled-page regressions |

### Reproduction

`FileAvailable` previously treated a missing `isAvailable` field as available. Project filtering accepted a
missing `allowModDistribution` field and also accepted JSON `null`. That turns uncertain provider permission into
permission. Separately, the generic catalog layer inferred the next page from the count of post-policy visible
items; when distribution or server-capability filtering removed rows, the UI position no longer matched the
provider's raw page.

### Resolution

A project is eligible only when both `isAvailable` and `allowModDistribution` are explicit JSON `true`; an exact
file likewise requires explicit `isAvailable: true`. False, null, absent, malformed, unavailable, contradictory
server-pack, or unapproved-host evidence fails closed.

The CurseForge adapter now returns its own `NextIndex` and `HasMore` from the official pagination object using
the raw result count/total, capped by the provider's 10,000-row boundary. Local filtering no longer changes the
provider cursor. Categories are first resolved through the official inventory and sent as exact numeric IDs.

### Final boundary

**VERIFIED:** the authenticated live campaign traversed two provider pages without repeating the first-page
projects; exact project/file, official server-pack relationship, links, and one required mod dependency also
resolved. Deterministic regressions prove explicit distribution/file permission and underfilled-page behavior.

**UNAVAILABLE:** the campaign did not encounter a naturally distribution-disabled live file. That negative class
is therefore deterministic-only and is not presented as live provider evidence.

---

## CP-2026-040 — CurseForge credential bootstrap and request lifetime did not enforce the live security boundary

| Field | Value |
|---|---|
| Date | 2026-08-29 |
| Git state | `codex/curseforge-live-certification`, implementation commit `3f505aa1bdf3a9b57b99ddff909132da9b18394d` |
| Provider identity | Official Minecraft game identity `432`; no credential value or derivative retained |
| Severity | High — an unvalidated rotation could replace the last usable native credential, bootstrap path state could reach child processes, and one cancellation race could retain a completed response as a session cache |
| Area | Native credential import/rotation, DPAPI, API transport, Agent/managed-process environment |
| Status | **Fixed locally and live-authenticated** — validate-before-store, preserved last-known-good credential, fixed production transport, and request-owned in-flight cleanup |
| Validation | Live official identity accepted in isolated DPAPI data; credential rotation/DPAPI/environment regressions; API redirect, cancellation, timeout, rate-limit, response-bound, and in-flight lifetime regressions |

### Reproduction

The previous provisioner validated only local file shape and immediately wrote the candidate to protected
storage. It could replace an accepted credential with a rejected value and did not distinguish authentication
rejection from temporary provider failure. The long-lived Agent also retained the key-file *path* environment
variable, so managed Java or installer children could inherit a bootstrap detail they never needed.

`ISecretStore.Contains` decrypted the stored value merely to answer whether it existed, extending plaintext
handling. Production construction also accepted an arbitrary `HttpClient`, so redirect policy was not guaranteed.
Finally, in-flight API cleanup lived in an individual waiter's `finally`: if that waiter cancelled before the
shared request finished, no later owner was guaranteed to remove the completed task, turning coalescing into an
unbounded session response cache.

### Resolution

The candidate credential is sent directly to exact HTTPS `api.curseforge.com/v1/games/432` and persisted only
after the response proves Minecraft identity `432`. Authentication rejection, timeout/offline failure and caller
cancellation leave the prior DPAPI entry unchanged; a rejected first import leaves storage empty. Temporary byte
buffers are zeroed. DPAPI presence inspection reads only the protected dictionary entry and decryption buffers are
zeroed after use.

The key-file variable accepts an absolute path only, is cleared from the Agent immediately after bootstrap, and
is removed from every managed child `ProcessStartInfo`. There is no pipe method to set/remove a key and React sees
only authenticated availability. Production API construction owns a no-cookie/no-redirect handler, validates the
final exact host, separates caller cancellation from timeout, retries one bounded `Retry-After`, and removes an
in-flight task from inside the shared task itself.

### Final boundary

**VERIFIED:** the authorized credential authenticated successfully and survived isolated DPAPI round-trip; live
metadata then completed 15/15 bounded categories. Deterministic tests cover rejected/offline/cancelled rotation,
child-environment removal, malformed protected values, redirect/host rejection, completed-request cleanup, and
zero network access when the credential is missing.

**UNAVAILABLE:** a real rejected key and live rate-limit were deliberately not induced. Public desktop key
sharing is not authorized by this result and remains gated; only this user's approved local native credential was
used.

---

## CP-2026-039 — Server selection discards its immediate snapshot and waits behind unrelated detail work

| Field | Value |
|---|---|
| Date | 2026-08-27 |
| Severity | High — ordinary server navigation can block for roughly ten seconds while a prior server's unstamped details remain in memory |
| Area | Native/WebUI server selection, async identity isolation, presentation cache |
| Status | **Fixed locally** — immediate authoritative response, native loading/ready identity fence, and bounded exact-ID workspace cache |
| Fixed commit | Gate 1 commit `Make server switching immediate and isolated` |
| Validation | Native selection-fence regression; direct-response, duplicate-name, cached A/B isolation, loading preservation, out-of-order rapid-selection, and 8-entry LRU regressions; 30 warm, 10 cold, 20 rapid measurement |

The native bridge already returned a snapshot synchronously from `snapshot.selectServer`, but the renderer
discarded it and waited for a periodic presentation event. That refresh can include Agent dashboard and
local connectivity work, explaining the user-reported full-page `Opening <server>` delay without any fixed
ten-second renderer timer. Applying the response alone was unsafe: native detail collections do not all
carry server IDs, and the one global detail loader could still be finishing the previous selection.

Selection now applies its returned snapshot immediately. A native `Loading`/`Ready` fence exposes unstamped
details only after the newest selected server's serialized pass finishes; changing identity clears those
native models so a failed section cannot reveal prior data. The renderer keeps at most eight ready snapshots
by exact server ID and may show that server's cached workspace while the native model refreshes. Cold opens
show the new header/navigation immediately and delay the page-local loading state by 150 ms. Selection itself
performs no provider request.

Measured store/presentation boundary during the full WebUI suite on the Release test host: cached warm p50
`0.001 ms`, p95 `0.009 ms`, max `0.049 ms`; cold authoritative acknowledgement p50 `0.308 ms`, p95/max
`0.499 ms`; 20 reverse-completed rapid requests settled in `5.735 ms`, with 30 native selection requests
and zero provider requests. These
are deterministic in-process boundary measurements, not a claim about end-user compositor latency. The
pre-fix wall-time baseline remains the user's roughly ten-second report; the deterministic reproduction is
that the immediate native result was discarded.

---

## CP-2026-038 — A pre-cancelled renderer request is still sent to the native host

| Field | Value |
|---|---|
| Date | 2026-08-22 |
| Severity | Medium — navigation cancellation can still start provider work and leave the renderer waiting for timeout |
| Area | WebUI bridge cancellation |
| Status | **Fixed** — pre-cancelled requests are rejected before allocation or native dispatch |
| Fixed commit | `6641c29ebf932ee58f4e07f209576fee8edc95d6` |
| Validation | `client.test.ts`: pre-aborted signal sends zero bridge messages; active cancellation and late-response regressions remain green |

`AbortSignal.addEventListener` does not invoke a newly added listener when its signal was already aborted.
The bridge therefore posted work and left its promise pending until timeout. The request now checks the
signal before allocating an ID, and timeout cleanup also removes the abort listener.

---

## CP-2026-037 — A late Files read can replace the newly selected file

| Field | Value |
|---|---|
| Date | 2026-08-22 |
| Severity | Medium — the editor can display content owned by a previous selection and invite a mistaken edit |
| Area | WebUI Files workspace |
| Status | **Fixed** — read/save generation fences bind results to the current file and baseline hash |
| Fixed commit | `6641c29ebf932ee58f4e07f209576fee8edc95d6` |
| Validation | `ServerWorkspace.files.test.tsx`: delayed file A is ignored after file B is selected |

The Files editor had no request generation. A slow read for the previous row could win after a faster read
for the current row and replace the editor text. Reads and saves now capture a generation, file identity,
and hash; late work is ignored, and unmount invalidates all pending results.

---

## CP-2026-036 — Structured diagnostic entries bypass credential redaction

| Field | Value |
|---|---|
| Date | 2026-08-22 |
| Severity | Critical — a locally exported support bundle could retain credentials present in structured activity data |
| Area | Diagnostics / privacy boundary |
| Status | **Fixed** — one redaction boundary covers text logs and serialized structured JSON |
| Fixed commit | `6641c29ebf932ee58f4e07f209576fee8edc95d6` |
| Validation | Unit regressions prove Bearer, Basic, API-key, access/refresh token, client-secret, password, and credential removal while useful context remains |

The bundle redacted launch and log text but wrote `activity.json` and `inventory.json` directly. The shared
redactor also missed several common authorization forms. Structured JSON now passes through the same expanded
redactor. Player names, UUIDs, addresses, paths, timestamps, and versions remain intentionally available for
diagnosis, and the export UI truthfully asks the user to review the local bundle before sharing.

---

## CP-2026-035 — Same-user clients can hold unbounded Agent pipe connections

| Field | Value |
|---|---|
| Date | 2026-08-22 |
| Severity | Medium — a same-user local process can retain unbounded connected pipes or oversized request buffers |
| Area | App-to-Agent named-pipe transport |
| Status | **Fixed** — admission is capped before listening and every request has byte and time bounds |
| Fixed commit | `6641c29ebf932ee58f4e07f209576fee8edc95d6` |
| Validation | Integration regressions reject a 300 KiB request, cap 16 stalled clients, and prove the Agent accepts a valid request afterward |

The server limited concurrent handlers only after a pipe was connected, immediately created another listener,
and used unbounded line reading with no read deadline. It now acquires the 16-client admission gate before
creating a listener, accepts at most 256 KiB of strict UTF-8 input, applies a five-second read deadline, and
continues serving valid same-user clients after rejection. `PipeOptions.CurrentUserOnly` remains enforced.

---

## CP-2026-034 — Repository-local installer prerequisites fail on their default invocation

| Field | Value |
|---|---|
| Date | 2026-08-21 |
| Severity | Medium — a clean packaging machine cannot acquire the pinned local compiler or WebView2 bootstrapper with the documented command |
| Area | Release tooling / prerequisite acquisition |
| Status | **Fixed locally** — defaults are resolved after script binding and Inno's documented help exit is handled explicitly |
| Validation | Both scripts invoked without path overrides; compiler identity, pinned installer hash/signature, and Microsoft bootstrapper identity verified; distribution contracts |

PowerShell evaluates parameter defaults before `$PSScriptRoot` is available in this invocation path, so
both acquisition scripts attempted `Split-Path` on an empty value. A fresh Inno acquisition then treated
the compiler's expected nonzero help-banner exit as a terminating native error. Defaults now resolve from
the already established repository root inside the script body, and only help exit codes 0 or 1 are
accepted before normalizing the successful result.

---

## CP-2026-033 — Normal Internet setup waits on an automatic outside-in probe

| Field | Value |
|---|---|
| Date | 2026-08-21 |
| Severity | High — working owned setup can look incomplete and repeatedly depend on an optional external service |
| Area | WebUI / Internet sharing presentation |
| Status | **Fixed locally** — three-step owned-state setup; outside-in is Advanced and explicit |
| Validation | React running/stopped/setup/failure assertions, no-auto-request regression, native connectivity mapping, fixture review |

The ordinary Connectivity flow treated an external probe as step four, started it from a React effect,
and used its result as the durable setup label. Normal status now follows the exact Windows rule, router
mapping, and server lifecycle evidence. Optional outside-in testing remains available as a point-in-time
Advanced diagnostic and is still the only basis for the distinct **Connection confirmed** label.

---

## CP-2026-032 — Published shortcuts reference an icon file that is not packaged

| Field | Value |
|---|---|
| Date | 2026-08-21 |
| Severity | Medium — taskbar, window chrome and shortcuts can show generic or inconsistent identity |
| Area | WPF / publish / installer identity |
| Status | **Fixed locally** — one embedded and published multi-frame ICO plus stable AppUserModelID |
| Validation | Distribution contract, Release build, explicit post-publish copy, matching source/output SHA-256, nine ICO frames, associated executable icon, and successful installer compilation |

The executable embedded `assets/ChunkPilot.ico`, but the installer shortcut pointed at
`{app}\Assets\ChunkPilot.ico` even though the original item metadata did not actually copy that file. The desktop shortcut also lacked
the Start-menu AppUserModelID and the borderless WebUI window had no explicit icon. The project now copies
the same ICO through an explicit post-publish target, the process/window/shortcuts share `ChunkPilot.Desktop`, and no alternate icon was
introduced.

---

## CP-2026-031 — WebUI drops authoritative player UUIDs and cannot show player heads

| Field | Value |
|---|---|
| Date | 2026-08-21 |
| Severity | Medium — player identity is harder to distinguish and differs from the native player surface |
| Area | Player snapshot / native image bridge / WebUI |
| Status | **Fixed locally** — UUID-bound official Mojang skin path with bounded cache and local fallback |
| Validation | Official/cached/invalid-host/offline native tests; React rendered/fallback/selected-server tests |

The access model already carried authoritative UUIDs and the WPF control already knew the official Mojang
profile route, but `WebUiSnapshotMapper` omitted UUIDs. The renderer now requests a head only for a UUID in
the selected server's current rows. Native code allowlists Mojang hosts, bounds downloads/cache size,
composites the face and hat layers, and returns no broken URL on failure.

---

## CP-2026-030 — Server settings and MOTD draft can cross a rapid server selection

| Field | Value |
|---|---|
| Date | 2026-08-21 |
| Severity | Stop-the-line — a draft or late read can be shown or saved against the wrong server |
| Area | WebUI server settings / native async property load |
| Status | **Fixed locally** — immutable server identity on snapshots, remount boundary, stale-response guards |
| Validation | A-to-B dirty draft regression, late authoritative snapshot regression, native stale-load/build contracts |

`serverSettings` had no server ID, React retained one component state while `serverId` changed, and the
native property read applied its response without rechecking selection after `await`. Settings snapshots
now name their immutable owner, a server change remounts the editor behind the existing unsaved-change
guard, mismatched snapshots render unavailable rather than old values, and native late responses are
dropped before mutating selected state. Save requests keep the captured server ID and abort later memory
work if selection changed, so a newly selected server is never the fallback target.

---

## CP-2026-029 — Stale running evidence restarts a server after reboot and Stop can wait behind Start

| Field | Value |
|---|---|
| Date | 2026-08-20 |
| Severity | Stop-the-line — a server can start without persisted user policy and a manual Stop can appear stuck |
| Area | Agent startup reconciliation / serialized lifecycle / WebUI lifecycle completion |
| Affected | `v1.3.0-alpha.2` (`5a763e2f621b3273e312784969ceb6650bce3678`) and development commit `56c57ed27d2974c0c3540d320bf1b5f56503634c` |
| Status | **Fixed locally** — explicit startup authority, preemptive manual Stop, bounded gate wait and truthful completion |
| Fixed branches | `codex/fix-lifecycle-reboot-stop` (`52f143365065ac1f6efd6d63b5384bd93a856689`) and `codex/hotfix-alpha3-lifecycle` (`45262e0e6fc009f35296fa10cff2295315ec24f2`) |
| Validation | Isolated stale-reboot reproduction, explicit autostart/schedule starts, restart suppression, unresponsive/duplicate Stop, reconnect, exact process identity and owned-network cleanup tests |

Every successful ordinary start was persisted as `RestorePreviousRunningState`. On the next Agent launch,
`ServerSupervisor` treated that runtime observation as permission to start again, including stale
`CrashRecovery` and restart intent. This made a Windows reboot followed by App/Agent startup look like
an authorized autostart even when the server had no autostart setting or Start schedule.

Manual Stop also waited for the per-server operation gate before recording stop intent or cancelling the
operation that held the gate. A Start waiting for readiness (up to the configured startup timeout) or a
restart delay therefore kept Stop queued while the UI optimistically displayed `Stopping`. The fixed path
records manual-stop intent first, invalidates pending restart generations, cooperatively cancels the active
operation, has a ten-second gate-acquisition deadline, reconciles the final process observation, and sends
the real failed `OperationResult` through the WebUI completion event.

Ordinary running-state evidence is now persisted with `AutostartMode.Never`. Only the explicit server
autostart setting or an explicit persisted `AgentStart`/`WindowsLoginWithDelay` policy authorizes Agent
startup; user-created Start schedules remain an independent, tested authority.

---

## CP-2026-028 — Loader creation can continue after the WebUI reports a timeout

| Field | Value |
|---|---|
| Date | 2026-08-19 |
| Severity | High — retry can duplicate a creation transaction whose acceptance response was lost |
| Area | WebUI / native bridge / managed-loader creation |
| Status | **Fixed** — client-owned durable operation identity and reconnect polling |
| Fixed branch | `feature/complete-loaders-modpacks` |
| Validation | Delayed/lost acceptance, duplicate guard, progress reattach, and native prompt-acceptance contract tests |

Catalog/provider I/O could occur before Agent acceptance and exceed the renderer's request timeout.
Creation now carries a client-generated operation ID before submission, validates cached reviewed
selection without provider work, and reconciles progress after a lost response rather than enabling a
second submission.

---

## CP-2026-027 — Modrinth add-on install loses pending and installed state

| Field | Value |
|---|---|
| Date | 2026-08-19 |
| Severity | High — the UI invited duplicate installs and did not show the authoritative outcome |
| Area | WebUI / add-on operations / inventory reconciliation |
| Status | **Fixed** — correlated deferred operation state and exact inventory reconciliation |
| Fixed branch | `feature/complete-loaders-modpacks` |
| Validation | Pending/progress/cancel/failure/retry/remount/installed/loaded React tests and native bridge contracts |

The bridge awaited the complete serialized restartable operation and React cleared its busy state at
the request boundary. Add-on mutation now returns an operation ID promptly, survives navigation and
reload, and reconciles exact provider project/release identity against Agent inventory. `Loaded`
remains separate and requires loader-specific current-session evidence.

---

## CP-2026-026 — WebUI reports a timeout after an accepted server start succeeds

| Field | Value |
|---|---|
| Date | 2026-08-17 |
| Severity | High — a successful Paper start is presented as failure and invites duplicate input |
| Impact | The WebUI waits on the complete lifecycle command behind a request timeout instead of acknowledging the accepted operation |
| Area | WebUI / native bridge / lifecycle command contract |
| Status | **Fixed** — prompt acceptance and operation-correlated completion event |
| Fixed branch | `feature/paper-polish-fabric-neoforge` |
| Validation | Native deferred-lifecycle contract tests; React pending, late failure, duplicate, early completion, and stale operation-ID tests; isolated Paper fixture |

The old WebUI handler awaited the full WPF lifecycle command before returning the correlated bridge
response. Paper could reach readiness after the browser request timeout, producing “ChunkPilot did not
answer in time” while the authoritative server was already Running. Start, stop, and restart now
return a native-generated operation ID immediately. Snapshots carry lifecycle state; a separate
operation completion event carries the same ID. Duplicate in-flight commands reuse the active ID,
and React ignores a stale completion for a different operation rather than clearing newer work.

---

## CP-2026-025 — WebUI omitted the authoritative Internet-hosting workflow

| Field | Value |
|---|---|
| Date | 2026-08-16 |
| Severity | High — ordinary users could not find or truthfully complete friend sharing |
| Impact | Share copied only a local address; public reachability was hardcoded unavailable; router, firewall and outside-in commands were absent from WebUI |
| Area | WebUI / selected server connectivity presentation |
| Status | **Fixed** — authoritative connectivity snapshot and command adapters restored |
| Fixed branch | `feature/verified-vanilla-paper-plugins` |
| Validation | Bridge allowlist/refresh policy, mode persistence, Share address truthfulness, consent, fixtures, keyboard dialog, and packaged visual coverage |

The native App and Agent already owned network mode, router mapping, exact firewall setup, cancellation,
cleanup, and outside-in verification. The WebUI mapper replaced that state with a fixed unavailable
summary, its copy action always selected the local endpoint, creation collapsed intent into three
ambiguous choices, and Settings exposed only a port row. The fix projects the existing state without
duplicating authority, distinguishes local/LAN/router-reported/verified-public endpoints, and refuses
public copy without outside-in proof. No real firewall, router, or public port was touched by the fix.

---

## CP-2026-024 — WebView2 crash helper can retain the preview profile after ChunkPilot closes

| Field | Value |
|---|---|
| Date | 2026-08-16 |
| Severity | Medium — immediate close leaves one browser-owned helper and locks isolated data |
| Impact | The native UI and Agent exit, but packaged smoke cannot remove the app-specific WebView profile |
| Area | WebUI native host / WebView2 environment lifecycle |
| Status | **Fixed** — ChunkPilot-owned crash reporting prevents the orphan helper |
| Fixed branch | `feature/minecraft-version-platform` |
| Validation | Environment-options unit contract plus packaged WebUI close rerun with complete process and temporary-root cleanup |

The first final packaged smoke closed the native UI in 165 ms and stopped its intended Agent, but
WebView2's separate crash-reporting helper survived and retained `ExtensionActivityEdge` in the
isolated profile. The host already owns renderer failure recovery and diagnostics, so its single
environment now enables WebView2 custom crash reporting rather than the separate uploader. The rerun
closed in 155 ms, left no invisible UI or browser helper, preserved an unrelated Agent, and removed
both temporary roots.

---

## CP-2026-023 — Real WebUI version rows cannot be selected

| Field | Value |
|---|---|
| Date | 2026-08-16 |
| Severity | High — the authoritative creation path cannot advance past Version |
| Impact | Fixture rows work, but every row returned by the real C# bridge is disabled |
| Area | WebUI / creation catalog bridge contract |
| Status | **Fixed** — one typed authoritative catalog contract now drives real and fixture rows |
| Fixed branch | `feature/minecraft-version-platform` |
| Validation | Bridge mapping tests, exact-selection tests, 906-entry browser tests, full unit and integration suites |

The React browser required `support`, `selectable`, artifact, Java and launch evidence. The real
`creation.catalog` response emitted only an older subset (`id`, label, channel, Java and warning), so
`selectable` was `undefined` and every live row was disabled. Fixture data already used the newer
shape and concealed the mismatch. The bridge now serializes the complete authoritative option,
creation resolves the exact selected ID from that same catalog, and unavailable rows remain
inspectable without becoming selectable.

---

## CP-2026-022 — MOTD formatting mutates text or loses the selected range

| Field | Value |
|---|---|
| Date | 2026-08-16 |
| Severity | Medium — appearance editing can change saved text or apply formatting to the wrong range |
| Impact | Focusing or switching modes can flatten formatting; toolbar clicks lose the browser selection |
| Area | WebUI / server appearance / MOTD editor |
| Status | **Fixed** — semantic runs and stable text offsets replace transient DOM state |
| Fixed branch | `feature/minecraft-version-platform` |
| Validation | Parser/serializer, no-mutation, multi-line selection, every style, Unicode, undo/redo, raw fallback, animated-obfuscation selection stability and Reduced Motion cleanup tests |

The preview spans carried only CSS while selection recovery looked for semantic formatting metadata.
Clicking a toolbar control also moved focus before the operation could consume the DOM `Range`, and
mode synchronization reparsed rendered text into the draft. The editor now stores formatting in a
small run document, represents selection as stable text offsets, restores it after toolbar actions,
and never derives authoritative text from rendered markup. Unsupported input stays losslessly in raw
mode.

---

## CP-2026-021 — Changing a server icon a second time fails with an access error

| Field | Value |
|---|---|
| Date | 2026-08-12 |
| Severity | Medium — a repeatable customization fails after succeeding once |
| Impact | A second icon change in one session can fail with Access denied instead of replacing the icon |
| Area | App / `ServerIconService.ConvertAndInstallAsync` / `InstallServerIcon` |
| Status | **Fixed** — lock-free previews and transactional finalization covered by repeated-change regression |
| Roadmap task | [CP-FRICTION-003](PRODUCT-FRICTION-REGISTER.md#cp-friction-003--server-icon-workflow) |
| Workaround | Restart ChunkPilot before changing the icon again |

Reproduction: change a server icon successfully, then choose and apply a different image in the same
session. The root cause was WPF URI image binding retaining a file-backed handle to the mutable
`server-icon.png`; the Agent's second atomic replacement was then denied by Windows. ChunkPilot now
decodes server and library previews fully into memory with delete-sharing, finalizes output before
publishing a library entry, and refreshes visible identity only after Agent success. Automated A -> B
-> C, reopen, source-move, same-source, cancellation and locked-finalization cases cover the fix.

---

## CP-2026-020 — ChunkPilot-managed network exposure survives UI process death

| Field | Value |
|---|---|
| Date | 2026-08-12 |
| Severity | High — an inbound path and renewal authority survived the application that enabled it |
| Impact | Router renewal and cached Public access verified could outlive the UI; persisted intent could recreate exposure after Agent restart |
| Area | App / Agent / public connectivity lifecycle |
| Status | **Open** — replacement implementation awaits independent read-only review and real Windows acceptance |
| Roadmap task | [CP-FRICTION-010](PRODUCT-FRICTION-REGISTER.md#cp-friction-010--ui-exit-network-safety) |
| Correction branch | `fix/public-connectivity-lease-safe-exit` |
| Previous experiment | `fix/ui-exit-network-safety` at `01d891730b68c4db11117b80bffb4c6ba1a9d5bb`, preserved unmodified for evidence |

### Verified original behavior and root cause

The Agent treated durable `DirectInternetEnabled` intent and a running server as sufficient renewal
authority. Unexpected UI loss left the Agent, Minecraft process, mapping, renewal worker, and current
external result alive. Exposure had no non-transferable capability or generation, so a replacement UI
or Agent restart could inherit state nobody had explicitly re-enabled.

### Replacement implementation awaiting acceptance

The accepted-base reimplementation uses an Agent-minted memory-only UI capability bound to PID plus
exact raw process-creation identity and an independent lease ID/generation per server. Lease authority,
renewal, and current external verification end synchronously on normal close or proven process death;
exact-owned router cleanup and world-safe stop of all managed servers then proceed independently under
bounded rules. A new Agent owns no lease and treats persisted public state as cleanup-only. Durable
exact Windows Firewall configuration is intentionally unchanged and is not treated as a listener,
public route, or verification.

The experiment's exact process identity, observer, bounded cleanup, retained ownership evidence, and
race scenarios were selectively reimplemented. Its global `UiSessionNetworkOwnership`, elevated
firewall guardian, firewall `LeaseSessionId`, helper guard operation, deferred firewall backend, and
window-lifetime firewall documentation were rejected.

### Independently verified review defects and correction

The first read-only deployment review found four independent High defects: router work carried only a
point-in-time lease decision; fire-and-forget restoration could cross stale-exposure cleanup; safe exit
could wait forever behind the server operation gate; and detached-process escalation accepted a
one-second start-time tolerance.

The bounded correction carries exact server, lease, generation, and Agent epoch authority through the
serialized router executor and revalidates it before durable and wire mutations. Safe exit atomically
seals the Agent against replacement registration, revokes an exact lease snapshot, and cleanup can
affect only those generations, including a latest generation whose manual cleanup was already pending;
the old unscoped global router-release surface was removed. Startup
loads restoration intent without starting it, completes stale listener stop and ownership-safe route
cleanup first, and suppresses affected restoration; unavailable metadata storage leaves restoration
inert. Exit cancels the active cancellable operation, uses a bounded priority wait for the lifecycle
path, preserves transactional unwind and world-safe save/stop/escalation, uses bounded router
persistence and whole-attempt deadlines, and keeps the Agent alive with leases revoked while a known
process remains. New process records persist
raw Windows creation `FILETIME`; exact matching has no tolerance, and legacy records without it cannot
authorize automatic termination.

Regression evidence covers queued establishment/renewal after revocation, old cleanup versus a newer
generation, a non-cancellable late router-create success retained only for exact cleanup,
exit-epoch registration rejection, production startup stale-exposure ordering, cooperative,
delayed and failing operation cancellation, listener/process terminal verification, exact identity
round-trip and one-tick/PID-reuse rejection, legacy fail-closed behavior, and late router/external
results. The entry remains Open until final independent review and real Windows acceptance.

### Why this entry remains Open

Automated tests use synthetic processes, fake routers/probes, temporary stores, and isolated roots.
They do not mutate a real router or Windows Firewall and do not invoke UAC. Independent read-only review
and real Windows normal-close/taskkill acceptance remain required. No guarantee is claimed for
simultaneous App-and-Agent termination, machine power loss, or immediate removal from an unreachable
router.

---

## CP-2026-019 — Normal Create Server action still opens superseded creation flow

| Field | Value |
|---|---|
| Date | 2026-08-11 |
| Starting branch | `fix/external-probe-ipv4-affinity` |
| Starting commit | `86232f71578373ce60a71026f44a1df8748b7171` |
| Severity | Medium |
| Impact | The production entry point does not use the newer validated Vanilla creation workflow and exposes competing creation experiences |
| Area | App / Create Server / navigation |
| Status | **Fixed** |
| Reported by | Real manual acceptance on the current reviewed product branch |
| Roadmap task | Create Server v2 Vanilla product cutover |
| Workaround | None required |
| Fixed branch | `fix/create-server-v2-product-cutover` |
| Fixed commit | `Make Create Server v2 the default Vanilla flow` |
| Regression | `CreateServerV2ProductCutoverTests`, updated live/preview isolation coverage, existing live Vanilla and navigation suites |
| Validation | All three normal CTAs share the semantic Vanilla command; packaged zero-server Dashboard and Servers actions both opened the same live v2 wizard without a switch |

### Reproduction

Use any normal **Create server** action. ChunkPilot opens `InstallServerWindow`, while the newer
functional Vanilla workflow requires the development-only `--create-server-v2-live-vanilla` switch.

### Root cause

All three normal Create server controls still bind `MainViewModel.InstallServerCommand`, whose handler
directly constructs `InstallServerViewModel` and `InstallServerWindow`. The validated live Vanilla
composition exists only in the command-line startup branch, so the shell has no product route to it.

### Resolution

`MainViewModel.CreateVanillaServerCommand` now raises a presentation-independent Vanilla creation
request. `MainWindow` handles that request at the composition root, supplies the real Agent gateway,
location chooser and completion navigator, and reuses the existing live wizard if it is already open.
The retained development switch calls that same shell route. The Dashboard and Servers presentations
all bind the semantic command; **Add existing server** continues to use its by-reference import path.

The later WebUI product cutover migrated the broader version, platform, memory, storage, and
networking fields into the single shipped Create server flow. The now-unreachable
`InstallServerWindow`, its view model, its dead presentation event, and three review scripts for the
removed `--create-server-v2-live-vanilla` switch were deleted rather than left as misleading product
scaffolding. The synthetic `--create-server-v2-preview` remains isolated for native design review.

---

## CP-2026-018 — External reachability probe can use IPv6 while verifying an IPv4 mapping

| Field | Value |
|---|---|
| Date | 2026-08-11 |
| Starting branch | `fix/natpmp-full-datagram-confirmation` |
| Starting commit | `a5fe0f225a932b36219fa6d05081de04de2490b9` |
| Severity | Medium — no false result is produced, and no correct one can be produced either |
| Impact | A valid IPv4 Direct internet server may be impossible to verify from outside on a dual-stack Windows computer, even though the router mapping and the firewall rule are both exactly right |
| Area | External reachability / HTTP transport |
| Status | **Fixed** |
| Reported by | Manual acceptance of the reviewed build on real hardware, 2026-08-11 |
| Roadmap task | Friend connectivity / External reachability |
| Workaround | None, and none should be required. Users are never told to disable IPv6 |
| Fixed branch | `fix/external-probe-ipv4-affinity` |
| Fixed commit | `Pin external probe to IPv4 mapping family`, the single local commit on that branch |
| Regression | `ExternalProbeIpv4AffinityTests`: twenty-five cases over the transport and over the production client with only its resolver and socket replaced |
| Validation | Removing the family filter makes the dual-stack, ordering and IPv6-only cases select an IPv6 address; removing the refusal makes the IPv6-only case issue the check rather than fail |

### Reproduction

A disposable Vanilla server, running, TCP 25566, listening on `10.0.0.140:25566`. Direct internet
established over UPnP IGD through the gateway on `10.0.0.1`: external port 25566, a routable
router-reported public IPv4 address, a 3600-second renewable lease. Windows Firewall independently
showed ChunkPilot's own exact rule — TCP 25566, the exact managed Java runtime, Public profile, Rule
ready. External access read **Not checked**, and no claim about public reachability had been made.

**Check from outside** was then pressed deliberately. The deployed `workers.dev` Worker answered
successfully over HTTP and reported the router's IPv4 address as expected, an IPv6 address as observed,
and `unsupported_address_family`.

ChunkPilot behaved correctly with that answer: it concluded nothing and did not report Reachable. The
temporary mapping and firewall rule were removed through ChunkPilot afterwards and the server stopped.
The addresses themselves are deliberately not recorded here; the reproduction turns on their families,
not their values.

### Root cause

The probe client set no address family for its own HTTPS connection. On a dual-stack computer the
transport is free to prefer IPv6 for `workers.dev`, so the request that was supposed to demonstrate an
IPv4 path arrived at Cloudflare over IPv6.

The service is then being asked an impossible question. Its one safety rule is that it may connect back
only to the address it actually observed — never to a caller-supplied one — so with an IPv6 source and
an IPv4 expectation there is nothing it can compare and nothing it may connect to. `source_mismatch`
and `unsupported_address_family` are the correct answers to that request, and they are the only answers
it can give. The defect is entirely on the ChunkPilot side: the transport did not match what was being
verified.

### Resolution

`ExternalProbeTransport` opens the probe's connection over the family of the endpoint being verified.
The client parses the router-reported address, refuses anything that is not IPv4 before sending, and
carries the family on the request; the handler's connect callback resolves the configured hostname,
keeps only addresses of that family, and connects to one of them — up to three, each on its own bounded
wait, so several A records are tolerated and a long record set is not a loop.

Nothing above the socket changes. The request URI, the TLS host, SNI, certificate validation and the
authority are still the configured hostname, never a resolved numeric address, and the proxy setting is
untouched. If the hostname has no IPv4 address, or none of them answers, the check fails truthfully as
`ServiceUnavailable` and nothing is retried over IPv6: a verification of an IPv4 mapping made over IPv6
would not be a verification of it.

The Worker is unchanged. It still connects only to the source address Cloudflare observed, and it is
still the authority on whether that address is the expected one — so a VPN, a proxy or upstream NAT
that changes what Cloudflare sees continues to produce no conclusion rather than a false one.

---

## CP-2026-017 — Router mapping ownership can be rebound to a different network binding

| Field | Value |
|---|---|
| Date | 2026-08-10 |
| Starting branch | `fix/router-continuity-evidence-propagation` |
| Starting commit | `d39aecf` |
| Severity | High — a mapping made on one router can be relabelled as belonging to another, while keeping the identity every external result is bound to |
| Impact | **Public access verified** stays current for a mapping ChunkPilot is no longer anywhere near; a later withdrawal can be addressed to a router that never held the mapping; the exposure that does exist stops being tracked; and a check can rewrite what a past establishment was made of |
| Area | Router mapping ownership / External reachability verification |
| Status | **Fixed** — reopened twice; see *Reopened* below |
| Reported by | Independent read-only deployment review of `d39aecf`, verdict `EXTERNAL REACHABILITY DEPLOYMENT REVIEW: NO-GO`; reopened by the final deployment reviews of `ee7aae0` and `cecc55e`, both verdict `EXTERNAL REACHABILITY FINAL DEPLOYMENT REVIEW: NO-GO` |
| Roadmap task | External reachability verification |
| Workaround | Do not deploy the external reachability probe |
| Fixed branch | `fix/router-binding-and-natpmp-recovery`, then `fix/legacy-router-ownership-and-natpmp-dispatch`, completed on `fix/legacy-upnp-and-natpmp-send-confirmation` |
| Fixed commit | `Fix legacy UPnP ownership and NAT-PMP send confirmation`, the single local commit on the third branch |
| Regression | Twenty-one cases in `RouterMappingContinuityIntegrationTests`, through the production store, coordinator, providers and external reachability coordinator — nine for the binding rules and twelve for rows written before ownership recorded a network |
| Validation | First pass: restoring the assignment that caused it — a check writing its own binding onto the owned one — fails nine of forty-four coordinator cases. Second pass: restoring the `IsKnown` guards fails six of the eight upgrade cases, including a wrong-router deletion for each of NAT-PMP, UPnP and PCP. Third pass: removing the ownership reset fails all four running-legacy cases, including the adoption of a coincident UPnP entry |

### Why this is not CP-2026-015

CP-2026-015 is about *time*: an entry that stopped existing, and evidence that outlived it. This is about
*place*: an entry that still exists exactly where it was made, and a record that stopped saying so. The
two share a symptom and nothing else. CP-2026-015 stays fixed and its guards are untouched.

### Reproduction

1. Start a server with Direct internet on and a mapping established over any mechanism — say Ethernet,
   gateway 192.168.1.1.
2. Press **Check from outside** and reach **Public access verified**.
3. Move the computer to another network, or simply let it fall back to Wi-Fi: another adapter, another
   router. Undocking a laptop is enough.
4. Press **Check**.

Observed: continuity correctly refused to read the new router's epoch as evidence about the old
mapping — and then `RouterMappingCoordinator.CheckAsync` wrote that router's address into the record's
`GatewayAddress` anyway. `HasActiveMapping` stayed true and the `MappingInstanceId` was preserved, so
the mapping made on the first router was now recorded, projected and persisted as the second router's.
**Public access verified** stayed on screen for a public endpoint that no longer existed; a later
**Turn off** resolved the current binding and would have sent the deletion to the second router; and the
exposure genuinely open on the first one was no longer described by anything.

The same address on two adapters is the case that makes this more than a laptop problem. 192.168.1.1 is
the most reused address on any home network, so the record's gateway text kept matching after a move
that changed the router completely, and every later comparison agreed with itself.

### Root cause

Ownership binding and current discovery were the same field. `RouterMappingRecord.GatewayAddress` was
written by every check and every attempt, from whatever binding had just been resolved, while also
serving as the evidence for which gateway owned the mapping. Nothing in the record said which interface
the mapping was made through, so two routers answering on the same address were indistinguishable, and
an observation of one was silently accepted as an observation of the other.

### Resolution

`RouterBindingIdentity` names a network context as what it is — the adapter a request leaves through and
the gateway it is sent to — and it is the same identity the per-gateway epoch history has always been
filed under. The record now carries two of them, and they can never be confused: `OwnedBinding`, written
only where a mapping is successfully established, and `DiscoveredBinding`, written by every check and
every attempt and used for nothing but diagnosis.

From that, four rules. A check may no longer write ownership or a router-reported address against a
mapping that is not on the router it reached. Continuity is read only when the report's binding *is* the
owned one, matched on interface as well as address. A mapping continues only when it is renewed on the
binding that owns it, so an identical-looking entry on another router mints a fresh identity rather than
inheriting one. And a withdrawal resolved to a different binding sends nothing at all: the record keeps
its owner and the removal is retried when the computer is back on that network, rather than being
addressed to a stranger's router.

While the owned and discovered bindings disagree, the mapping is projected as **Needs attention** rather
than as open. The record is left completely intact — it may still have to be withdrawn — but the
identity a projection mints is dropped, which is what ends a verification the move invalidated and what
stops the original one returning when the computer comes back. Reconciliation then establishes a mapping
on the network the computer is actually on, as a new mapping with a new identity.

### Reopened — the rows that predate the rule it enforces

The final review of `ee7aae0` confirmed all of the above for records this build writes, and then found
that the guards it added ran only where an owned binding was fully known. A row written by the previous
build carries no interface, so `OwnedBinding.IsKnown` is false and every one of those guards was skipped
— the state the guards exist for was the one state that fell through them, straight onto whatever router
this computer happens to reach now.

The consequences differ by mechanism and all of them are wrong:

- **NAT-PMP** has no nonce and cannot read a table. RFC 6886 section 3.4 scopes a deletion to the
  requesting client's own internal port, so a deletion sent to a router that never held the mapping can
  only ever remove something else this computer has on that port.
- **UPnP** can read a table, and reading it proves nothing here. ChunkPilot's description is a constant
  every install writes, and the internal endpoint is this computer's own, so an entry matching the old
  row in every value is evidence that *some* ChunkPilot made it and never that this row did.
- **PCP** carries a mapping nonce, which RFC 6887 section 11.1 defines as "a random value chosen by the
  PCP client to identify this mapping", and ChunkPilot's provider both sends it and refuses to delete
  without it. A stray delete is therefore structurally incapable of removing another application's
  mapping — but PCP cannot read a table either, so nothing before the request can show the router
  reachable now is the one holding the mapping, and a reply cannot tell a real deletion from a no-op.

Alongside the deletions, the record kept `HasActiveMapping` with nothing to contradict it, so the card
read **Router port is open** for a mapping ChunkPilot could not identify, an external check was eligible
against it, and a check on the current router could write that router's WAN address onto it.

### Resolution of the reopened path

`RouterMappingPolicy.UpgradeStoredRecord` reads an old row's bare gateway address as it is loaded and
folds it into `OwnedBinding` with no interface. That is what it always was — an address, not a network —
so `IsKnown` reports it as not fully known, it matches nothing, and it authorises nothing, while
remaining available to tell the owner which router still holds the port. It is never written back.

Proof replaced permission everywhere the guards were skipped. A removal now requires the owned binding
to match the current one, so an unproven network is refused exactly like a wrong one and nothing is sent
for any mechanism; the record keeps its owner and its exposure rather than being quietly dropped. A
check may only write an owned mapping's evidence when it reached that mapping's own network, so no
adoption can happen by resemblance. And an active mapping with no provable network projects as **Needs
attention** rather than open, which drops the identity, makes any external result stale and blocks a new
check — an upgrade cannot inherit a verification, and cannot be talked into one by matching values.

The safe resolution is a fresh mapping, not a deletion: reconciliation establishes one on the network
this computer provably is on, which yields a known owned binding and a new identity under the ordinary
rules. PCP's nonce was deliberately not used to build a narrower path, because strength of ownership is
not evidence of location, and a legacy row must never produce a claim of closure it cannot support.

### Reopened again — the safe resolution walked straight back into it

The review of `cecc55e` confirmed the upgrade, the fail-closed removals and the stopped-server
behaviour, and then followed the one path those left open: the *running* server, where the safe
resolution actually runs.

`EstablishAsync` derives its working records from the one it was given, so a legacy row's
`HasActiveMapping = true` flowed into `attempted` and then into `candidate` — the record that
`RouterMappingPolicy.ProvesOwnership` is asked about. That method's first question is whether ChunkPilot
holds anything at all, and a legacy row answered yes. With a UPnP router in front of it holding an entry
that matched the old row in every value there is — same public port, same internal endpoint, and the
description every ChunkPilot install writes — ownership was proven by resemblance. The conflict branch
was skipped, `AddPortMapping` was sent for somebody else's entry, and the record was written back
claiming that router as owner.

One bit was doing two jobs: recording that a mapping may exist somewhere, and asserting that ChunkPilot
owns one here. The second was never true, because the build that wrote it never recorded where "here"
was.

### Resolution of the third reopened path

`RouterMappingPolicy.ResolveUnprovableClaim` separates them, and runs before anything about the router
in front of us is evaluated — at the top of `EstablishAsync` and again before the port-change withdrawal
in reconciliation. The claim ends: `HasActiveMapping` and `RemovalPending` are cleared, the owned
binding is emptied, and nothing is left for an ownership rule to read. What the claim described becomes
a `LegacyRouterExposure` — port, transport, mechanism, the gateway address the old build recorded, and
the lease it recorded — kept somewhere no ownership rule looks, so it can be told to the owner and can
never prove anything.

From there the current router is a fresh ownership context. An entry that merely resembles the old row
is foreign: not adopted, not renewed, not deleted, reported through the conflict semantics that already
existed. An empty router gets a new mapping under the ordinary rules, with a known owned binding and a
new identity, and the earlier verification stays stale. The exposure survives that success rather than
being hidden by it — a port open somewhere else does not close because a new one opened here — and it is
bounded: once the finite lease it recorded has run out, it stops being reported at all.

---

## CP-2026-015 — Router mapping continuity survives unobserved gateway state loss or foreign replacement

| Field | Value |
|---|---|
| Date | 2026-08-09 |
| Starting branch | `fix/external-reachability-identity-races` |
| Starting commit | `f05c6b5` |
| Severity | High — **Public access verified** can be shown for a router mapping that was never probed |
| Impact | A point-in-time external result outlives the exact mapping it was gathered about, which is the one thing the result-lifetime model exists to prevent |
| Area | Selected server › Overview › Connect › Direct internet › External access; router mapping identity |
| Status | **Fixed** — reopened once; see *Reopened* below |
| Reported by | Independent read-only deployment re-review of `f05c6b5`, verdict `EXTERNAL REACHABILITY RE-REVIEW: NO-GO`; reopened by the final review of `85e12e1`, verdict `EXTERNAL REACHABILITY FINAL REVIEW: NO-GO` |
| Roadmap task | External reachability verification |
| Workaround | Do not deploy the external probe service |
| Fixed branch | `fix/router-mapping-continuity-proof`, completed on `fix/router-continuity-evidence-propagation` |
| Fixed commit | `Fix router continuity evidence propagation`, the single local commit on the second branch |
| Regression | `RouterMappingContinuityIntegrationTests` (33 cases through the production providers, service, router coordinator and external reachability coordinator) and `GatewayEpochContinuityTests` (37 cases over the RFC rules and the real wire formats) |
| Validation | First pass: disabling the four original guards fails 7 of 16 coordinator cases. Second pass: discarding the propagated evidence again fails 10 of the 33 coordinator cases and 2 of the 37 provider cases; all pass with it |

### Why this is not CP-2026-012

CP-2026-012 is the *observed* ABA: ChunkPilot removed a mapping itself and asked for an identical one
back. That is genuinely fixed and stays fixed. This entry is the *unobserved* case — the mapping
stopped existing without ChunkPilot doing anything, and nothing in the reply that followed said so.

### Reproduction — PCP or NAT-PMP gateway restart

1. Start a server with Direct internet on and the mapping established over PCP or NAT-PMP.
2. Press **Check from outside** and reach **Public access verified**.
3. Restart the router, or otherwise make it lose its mapping table. ChunkPilot observes nothing: no
   command was issued and neither protocol can read a mapping table.
4. Let the ordinary lease renewal run.

Observed: the renewal message is byte-for-byte a creation message — RFC 6886 section 3.7 states this
outright — so the gateway creates a new mapping and answers exactly as it would have answered a
renewal. Every value ChunkPilot could compare matched, `RouterMappingCoordinator.EstablishAsync`
treated the entry as continuing, `MappingInstanceId` was preserved, and the verification gathered about
the mapping that no longer existed was still reported as current for the one that replaced it.

### Reproduction — UPnP foreign replacement

1. As above, over UPnP, and reach **Public access verified**.
2. Have something else take the public port on the router: another device's forward on the same
   external and internal port numbers.
3. Let reconciliation run.

Observed: `EstablishAsync` read the table, could not prove ownership, and returned `attempted` — which
is `record with { … }` and therefore still carried `HasActiveMapping = true` from the record.
`ResolvePhase` reads `HasActiveMapping` before it reads `LastFailure`, so the phase projected as
`Active`, the projection then suppressed the failure because the phase was settled and good, and
`ResolveMappingInstance(true)` handed back the identity the verified result was bound to. The card read
**Router mapping active** and **External access verified** for somebody else's port forward.

The same branch also covered the case where the router simply no longer lists the entry: if the
re-creation that followed failed, `attempted` again carried the old ownership and the old identity.

### Root cause

`MappingInstanceId` was replaced on evidence ChunkPilot could see, and preserved by default when it
could not. For a mechanism that cannot read the router's table that default was unconditional: any
successful request on unchanged terms counted as a continuation. Nothing in the code consulted the one
piece of evidence both datagram protocols provide for exactly this question — PCP's Epoch Time
(RFC 6887 section 8.5) and NAT-PMP's Seconds Since Start of Epoch (RFC 6886 section 3.6), both of which
a gateway resets when it loses its mapping state. Both fields were parsed past and discarded.

### Resolution

The two datagram providers now read the epoch from every response and validate it with the RFC's own
rule — PCP's `client_delta`/`server_delta` comparison with its +2 and /16 tolerances, NAT-PMP's
seven-eighths conservative estimate with its 2-second allowance — against a per-gateway history scoped
by interface, gateway address and control port, held by the provider instance and never static. The
result is reported as a three-valued `GatewayContinuity`: confirmed, state lost, or unknown.

The coordinator now requires affirmative evidence to preserve an identity rather than the absence of
contrary evidence. A mechanism that can read the table must have seen the entry; a mechanism that
cannot must have a confirmed epoch. Evidence of state loss ends the establishment whether or not the
request that revealed it succeeded, and a capability check that reveals it stops claiming the mapping
is open. Where a readable table shows the entry gone or foreign, ownership is dropped before any later
branch can carry it forward, and nothing is sent to the router: what is there is not ChunkPilot's, and
what was ChunkPilot's is no longer there.

Failing closed here costs a fresh mapping identity and therefore a stale external result — never a
false claim that a mapping is open, and never a deletion of somebody else's.

### Reopened — the evidence was gathered correctly and then thrown away

The final review of `85e12e1` confirmed the epoch parsing, the arithmetic, the gateway scoping and the
UPnP replacement handling, and then found two paths on which the reading never reached the code that
acts on it. Both restore the original symptom in full: **Public access verified** stays on screen for a
mapping that no longer exists.

**Substitute public port.** A restarted gateway may answer a mapping request successfully while
assigning a public port other than the one that was asked for. `PcpMappingProvider.CreateAsync` and
`NatPmpMappingProvider.CreateAsync` both withdraw that substitute and convert the operation into
`ForeignMappingPresent` — by constructing a *new* outcome through `RouterMappingOutcome.Failed`, which
defaults `Continuity` to `Unknown`. The reading taken from the authoritative response was dropped on
the floor. The coordinator then saw a conflict carrying no evidence of state loss, kept
`HasActiveMapping`, kept the `MappingInstanceId`, and the verified result stayed current for a mapping
the gateway had already replaced.

**Refused discovery.** `RouterCapabilityReport` carried `Selected` and `Attempts`, and the coordinator
read continuity only from `Selected`. A gateway that has just restarted is exactly the gateway most
likely to refuse the request whose header proves it restarted — and a refused attempt is never
selected, so the proof sat in `Attempts` where nothing looked. The same held when the check found
nothing usable at all: `EstablishAsync` returned the record with its ownership intact, so the old
establishment survived on the grounds that no replacement had been found for it yet.

### Resolution of the reopened paths

Continuity is now a fact about the gateway, carried independently of how any operation was finally
classified and combined only through `GatewayContinuityEvidence.Stronger`, where evidence of loss
always wins. Both providers combine the readings from the mapping request and from the withdrawal of
the substitute, and attach the result to the conflict they return.

`RouterCapabilityReport.ContinuityFor(mechanism)` reads across every attempt, selected or not, so a
refused mechanism's answer still reaches the Agent; `WithContinuitySpent` marks a cached report's
readings as consumed so one observation is never acted on twice. The coordinator asks for continuity by
the mechanism that actually owns the mapping, and only when the check reached the same gateway the
record was established against — so a PCP restart cannot invalidate a NAT-PMP entry, a NAT-PMP restart
cannot invalidate a UPnP one, and another router's epoch cannot invalidate this one's. Ownership is
withdrawn on proof of state loss even when the operation ends unsupported, refused or conflicted: the
coordinator no longer waits for a successful replacement before admitting that the old entry is gone.

---

## CP-2026-016 — NAT-PMP reboot recovery omits the RFC-required randomised recreation delay

| Field | Value |
|---|---|
| Date | 2026-08-10 |
| Starting branch | `fix/router-mapping-continuity-proof` |
| Starting commit | `85e12e1` |
| Severity | Medium — protocol non-compliance; no user-visible untruth and no data at risk |
| Impact | Every ChunkPilot client on a network would re-register its mappings the instant a gateway finished rebooting, which is the synchronised stampede RFC 6886 introduced the delay to prevent. A gateway that drops or refuses requests under that load leaves ChunkPilot reporting a failure it caused |
| Area | Router mapping › NAT-PMP › reboot recovery |
| Status | **Fixed** — reopened four times; see *Reopened* below |
| Reported by | Independent read-only deployment final review of `85e12e1`; reopened by the deployment review of `d39aecf` and by the final deployment reviews of `ee7aae0`, `cecc55e`, and `ac889e0` |
| Roadmap task | External reachability verification / router mapping |
| Workaround | None needed; the omission makes recovery more aggressive rather than less correct, and a single-client home network never sees it |
| Fixed branch | `fix/router-continuity-evidence-propagation`, then `fix/router-binding-and-natpmp-recovery`, `fix/legacy-router-ownership-and-natpmp-dispatch`, and `fix/legacy-upnp-and-natpmp-send-confirmation`, completed on `fix/natpmp-full-datagram-confirmation` |
| Fixed commit | `Require full NAT-PMP datagram confirmation`, the single local commit on the fifth branch |
| Regression | Six `GatewayEpochContinuityTests` cases, twenty-eight `NatPmpRebootRecoveryTests` cases — three against the production UDP channel and three against its exact full-datagram decision seam — and one production-path case in `RouterMappingContinuityIntegrationTests` |
| Validation | Deterministic throughout: the wait is injected and released by the test, nothing waits for real time, and the production draw is checked against the RFC's interval over 500 samples. Restoring the outstanding-task guard fails the overlapping-reboot case; removing the serial gate fails the concurrent-recreation and queued-cancellation cases; restoring the wait-then-return-then-send shape fails the case where a reboot is recorded after the gate and before the wire; consuming at invocation instead of at confirmation fails the refused-send and cancelled-mid-send cases; ignoring the send byte count fails the complete-versus-short decision and pending-generation regressions |

### Protocol requirement

RFC 6886 section 3.7: a client renewing its mappings because Seconds Since Start of Epoch showed a
reboot "MUST first delay by a random amount of time selected with uniform random distribution in the
range 0 to 5 seconds, and then send its first port mapping request", after which requests "SHOULD be
issued serially, one at a time".

### Evidence

`NatPmpMappingProvider` detected the reset correctly and reported it, and the coordinator acted on it
correctly, but nothing anywhere delayed the recreation that followed. The section was named in the
class remarks as deliberately not implemented.

### Resolution

`NatPmpRebootRecovery` arms a wait the moment a response proves the reboot — which is where "delay,
then send" begins — and the next mapping request for that gateway waits for it and spends it. Requests
that arrive while it is still running wait for the same one rather than starting their own, which also
gives the burst immediately after a reboot the serial behaviour the section asks for. Withdrawals do
not wait, because withdrawal is not recreation, and a gateway that has not rebooted has nothing armed,
so ordinary renewal is untouched. A wait that is cancelled leaves the obligation standing: the request
it was holding back never went out.

The draw and the wait are behind `INatPmpRebootDelay`, whose production implementation draws uniformly
from 0 to 5 seconds with `RandomNumberGenerator` and waits with `Task.Delay`. Nothing blocks a thread,
every wait honours the operation's cancellation token, and the scope is per interface, gateway and
control port — the same identity the epoch history itself is filed under.

PCP's own rapid-recovery procedure (RFC 6887 section 14) remains deliberately unimplemented and is
still documented as such; this entry covers NAT-PMP only.

### Reopened — half of section 3.7, and one reboot at a time

The deployment review of `d39aecf` confirmed the detection, the draw, the injection and the scoping, and
then found that the mechanism holding them could represent only one reboot and could not order anything.

**A second reboot while the first wait is running was lost.** `NoteRebooted` returned immediately
whenever an outstanding task already existed for that gateway, so a gateway that restarted again before
anything had been recreated armed nothing. The reference comparison that followed — which stopped an
older wait removing a newer one — could not help, because the newer one was never created. The recovery
owed to the second reboot simply did not happen, and the first request out of the door was sent into a
gateway that had been up for no time at all.

**Sharing one wait is not sending serially.** RFC 6886 section 3.7 does not stop at the delay: "After
that request is acknowledged by the gateway, the client may then send its second request, and so on…
The requests SHOULD be issued serially, one at a time; the client SHOULD NOT issue multiple concurrent
requests." Every caller awaited the same task, so the instant it completed all of them sent at once —
the synchronised burst the delay exists to break up, merely moved five seconds later.

### Resolution of the reopened paths

A reboot now arms a numbered obligation rather than a task. Each detected reboot increments the
generation and starts its own wait, so a second reboot is represented rather than discarded; a waiter
that reaches the end of an older wait re-reads the obligation, finds the newer generation and waits for
that too, so nothing held for the second reboot escapes through the door the first one opened, and only
the generation actually served is marked discharged.

Recreation then runs through a per-gateway gate that admits one request at a time and holds it until the
gateway has answered, which is what the section's "after that request is acknowledged" asks for. The
gate belongs to the recovery, not to the provider: it exists only for a gateway with an outstanding or
in-flight recovery, is dropped once the obligation is discharged and nothing is using it, and never
spans two gateways — so ordinary renewal is unaffected and one router's recovery never delays another's.

Cancellation was preserved and extended. A caller cancelled during the wait still sends nothing and
still leaves the obligation standing; a caller cancelled while queued for the gate releases no permit it
never took, leaves the gate usable, and marks nothing discharged.

### Reopened again — the obligation was discharged before anything was sent

The final review of `ee7aae0` confirmed the generation model and the serialisation, and then found that
the moment an obligation was considered served had nothing to do with a request going out. Recovery
waited, marked the generation satisfied, returned, and only then did the caller send — three separate
steps with the protocol state committed at the first of them.

**A caller that never sent could discharge the obligation.** Anything between the mark and the send —
cancellation, an exception, a failure to reach the transport at all — left the reboot recorded as
recovered while not one datagram had left. The next recreation then sent immediately, having waited for
nothing, which is exactly the un-delayed re-registration RFC 6886 section 3.7 exists to prevent.

**A reboot recorded in that gap was stepped over.** A second reset detected after the mark and before
the send arrived at a state that already said the gateway was recovered, so the request went out without
ever observing it. The generation model was right; the moment it was consulted was not.

### Resolution of the second reopened path

Recovery no longer hands the caller a decision and trusts it to send afterwards. `RecreateAsync` hands
the operation a `Dispatch`, the provider puts every datagram of a recreation through it, and the last
check of the obligation and the instant the request goes out happen inside the one lock `NoteRebooted`
also takes — the exchange is *started* there and awaited outside, so no thread blocks and no monitor is
held across a network response. A reboot recorded before that instant cannot be stepped over, because it
is either already visible to the check or recorded after the request began; a reboot recorded after it
is a new event, left standing for the next recreation, which is the most any client can do about a
gateway that restarts mid-request.

A generation is therefore consumed at the wire and nowhere else. RFC 6886 section 3.7 states the
requirement as a condition on sending — "delay … and then send its first port mapping request" — so
transmission discharges it: a request that goes out and is then refused or never answered has been
delayed exactly as asked, and delaying the next one again would answer a requirement the RFC does not
make. Everything short of transmission leaves the obligation standing. What a failed request means for
the mapping is decided separately, by the outcome and epoch rules that own it.

Only the first datagram of a recreation is held. Its retransmissions and the withdrawal of a substitute
port belong to a request already under way, and holding those for a reboot recorded behind them would
stall that request rather than answer the reboot — which the next recreation does.

### Reopened a third time — starting a send is not sending one

The review of `cecc55e` accepted the generation model and the atomic check, and then read the transport.
`UdpGatewayDatagramChannel.ExchangeAsync` opens a socket, awaits `UdpClient.SendAsync`, and only then
waits for a reply. The recovery marked the generation served the moment it *invoked* that method — while
the returned operation was still outstanding.

So everything the send could still do wrong happened after the obligation had already been discharged.
A socket that could not be opened, a send that failed, a caller that gave up while the datagram was
still going out: the reboot was recorded as recovered though nothing had reached the gateway, and the
next recreation went out with no delay at all. The same window swallowed a reset recorded between the
invocation and the send completing.

RFC 6886 section 3.7 asks for a delay before a request is *sent*. Method invocation is not a send.

### Resolution of the third reopened path

`IGatewayDatagramChannel.ExchangeAsync` now takes an `onSent` callback, and the UDP channel invokes it
on the line after `await client.SendAsync(...)` returns — the point at which the platform reports the
datagram handed to the socket, which is the strongest confirmation UDP offers, since it acknowledges
nothing. Everything that could have stopped it leaves by throwing before that line, so the callback runs
only when a datagram genuinely went out, and it runs whether or not a reply ever arrives: confirmation
is about the send, and the reply is a separate question with a separate answer.

The two moments are now distinct and both are kept. The dispatch still makes its last check and starts
the send inside the one lock `NoteRebooted` also takes, and *captures* the generation there instead of
consuming it. The confirmation marks that captured generation served and never "whatever is current",
so a reboot recorded while the datagram was on its way survives for the next recreation. Until a
confirmation arrives the operation is not treated as under way either, so its own retransmission is
checked against the obligation again rather than waved through.

PCP passes no callback: it has no reboot obligation of its own, because RFC 6887 section 14's rapid
recovery is deliberately unimplemented.

### Reopened a fourth time — completion did not prove the complete datagram

The final deployment review of `ac889e0` found that the production UDP channel awaited
`UdpClient.SendAsync` but discarded its returned byte count. It invoked `onSent` whenever the operation
completed, without requiring that the socket had accepted every byte in the payload. A successful short
result could therefore consume the captured reboot generation even though the complete NAT-PMP datagram
had not been confirmed.

### Resolution of the fourth reopened path

`UdpGatewayDatagramChannel` now compares the returned byte count with `payload.Length` and invokes
`onSent` only when they are equal. A mismatch returns the channel's existing failed-exchange result, so
the provider retries and, if every attempt fails, reports no mapping success. The captured reboot
generation remains pending until a later full-length send confirms it.

Because an OS UDP socket does not provide a deterministic way to force a successful short datagram,
the byte-count decision is the smallest internal production seam. The real socket path calls that seam;
its regressions prove full-length confirmation exactly once, short-result rejection without confirmation,
and a short result leaving recovery pending for a later qualifying full send. Existing loopback tests
continue to exercise the real UDP channel for successful send, reply timeout, and pre-send cancellation.

---

## CP-2026-014 — An integration fixture assumes TCP 25565 is free

| Field | Value |
|---|---|
| Date | 2026-08-09 |
| Starting branch | `fix/external-reachability-identity-races` |
| Starting commit | `f05c6b5` |
| Severity | Medium — a test defect only; no production behaviour is affected |
| Impact | The full integration suite cannot pass on any machine that is running a Minecraft server, which is most of the machines this product is developed on |
| Area | `tests/ChunkPilot.IntegrationTests/AgentReconnectIntegrationTests` |
| Status | **Fixed** |
| Reported by | Independent read-only deployment re-review of `f05c6b5` |
| Workaround | Free TCP 25565 before running the suite |
| Fixed branch | `fix/router-mapping-continuity-proof` |
| Fixed commit | `Fix router mapping continuity proof`, the single local commit on this branch |
| Regression | `TestPortAllocatorTests`, four cases |
| Validation | Reproduced and fixed against an isolated fake listener holding TCP 25565: the fixture fails with the old default and passes with the reservation, while the listener is still up |

### Reproduction

1. Have anything listening on TCP 25565.
2. Run `AgentReconnectIntegrationTests`.

Observed: `Lost_UI_session_keeps_server_running_and_relaunch_reports_recovery` fails. The agent is
right — it reports that another local process is holding the port and refuses to start the fake server
— and the failure says nothing at all about the agent behaviour the test exists to prove.

### Root cause

`CreateStoredFakeServerAsync` took `int port = 25565`. Two of the fixture's tests used that default, so
the fixture's port was the single most contended port number on any machine that runs Minecraft.

### Resolution

The parameter is now required and every caller reserves a port through `TestPortAllocator`, which asks
the kernel for a free ephemeral port, refuses to hand out 25565, records everything already issued so
two fixtures in one process can never collide, and re-probes the candidate so a port taken in between
is discarded rather than returned. Production code is unchanged: the agent's port-in-use check is the
behaviour that caught this and it was left exactly as it was. The other integration files already
allocate dynamically and were deliberately left alone.

---

## CP-2026-013 — An external reachability result for one server can be displayed under another

| Field | Value |
|---|---|
| Date | 2026-08-08 |
| Starting branch | `feature/external-reachability-verification` |
| Starting commit | `cf80e5d` |
| Severity | High — a verified public endpoint belonging to one server can be shown, and copied, from a different server's workspace |
| Impact | Direct contradiction of the milestone's truthfulness boundary: the address a person copies to give to a friend could belong to a server they are not looking at |
| Area | Selected server › Overview › Connect › Direct internet › External access |
| Status | **Fixed** |
| Reported by | Independent read-only deployment review, verdict `EXTERNAL REACHABILITY REVIEW: NO-GO` |
| Roadmap task | External reachability verification |
| Workaround | Do not deploy the external probe service |
| Fixed branch | `fix/external-reachability-identity-races` |
| Fixed commit | `Fix external reachability identity races`, the final local commit on this branch |
| Regression | `ExternalReachabilitySelectionTests` — ten deterministic ViewModel tests that hold a check open and move the selection underneath it |
| Validation | Verified by temporarily reverting the guard: six of the ten fail without it and all pass with it |

### Reproduction

1. Select server A, with Direct internet set up and its router port open.
2. Press **Check from outside**.
3. While the check is still running, select server B.
4. Wait for A's answer to arrive.

Observed: `CheckExternalReachabilityAsync` captured A's id, awaited the Agent, and then assigned the
returned presentation state unconditionally. B's freshly loaded state was overwritten with A's, so A's
**Public access verified** endpoint appeared — and could be copied — in B's workspace.

Separately, `CancelExternalReachabilityAsync` sent the cancel request for whichever server happened to
be selected when the button was pressed, rather than the server that owned the running check.

### Resolution

Every App-side external reachability operation now carries the server it was issued for and the local
sequence it was issued under. An answer is published only if the Agent answered about that server, that
server is still the one on screen, and no newer operation has superseded it; otherwise it is discarded
in silence. A deliberate command supersedes background reads on publication, while a background read
never supersedes the command whose answer the user is waiting for. Cancel takes its target from the
authoritative state being displayed — the operation actually in flight — and does nothing when none is.

Tightened on `fix/router-mapping-continuity-proof`: the server check is now exact rather than
"matches, or carries no id at all". Every state the Agent produces names its server, so an empty id is
an unattributable payload rather than a wildcard, and admitting one would have let a malformed answer
overwrite a real server's visible reachability. Covered by three further
`ExternalReachabilitySelectionTests` cases.

---

## CP-2026-012 — An identical router mapping recreated after removal resurrects a stale verification

| Field | Value |
|---|---|
| Date | 2026-08-08 |
| Starting branch | `feature/external-reachability-verification` |
| Starting commit | `cf80e5d` |
| Severity | High — ChunkPilot can present **Public access verified** for a router mapping that was never checked |
| Impact | A point-in-time external result outlives the exact mapping it was gathered about, which is the one thing the result-lifetime model exists to prevent |
| Area | Selected server › Overview › Connect › Direct internet › External access; router mapping state |
| Status | **Fixed** |
| Reported by | Independent read-only deployment review, verdict `EXTERNAL REACHABILITY REVIEW: NO-GO` |
| Roadmap task | External reachability verification |
| Workaround | Do not deploy the external probe service |
| Fixed branch | `fix/external-reachability-identity-races` |
| Fixed commit | `Fix external reachability identity races`, the final local commit on this branch |
| Regression | Seven `ExternalReachabilityIntegrationTests` cases against a running server and a scripted gateway, plus policy-level identity tests |
| Validation | Verified by temporarily reverting the identity: four of the seven fail without it and all pass with it |

### Reproduction

1. Start a server with Direct internet on and the router mapping active.
2. Press **Check from outside** and reach **Public access verified**.
3. Withdraw the mapping and have the router establish it again on identical terms — same mechanism,
   transport, internal client, internal port, external port and public address. The server keeps
   running throughout, so its run identity is unchanged.

Observed: `ExternalReachabilityPolicy.DescribeMapping` composed the mapping identity from values alone.
The recreated mapping produced a byte-identical identity, endpoint equality held, and the result
gathered about the first mapping was still reported as current for the second. Classic ABA.

### Resolution

`RouterMappingState` now carries an opaque `MappingInstanceId` that names one continuous establishment
rather than describing a mapping. The Agent mints it while an entry is open and drops it the moment the
projected phase says none is, so an entry that is withdrawn can never lend its identity to the next
one. Renewing the same entry on unchanged terms keeps it, and reading the same live mapping never
manufactures a new one. Where the mechanism can read the router's table, a port the router reports as
free is treated as proof the previous entry is gone. `DescribeMapping` requires the instance and fails
closed without it, so evidence can never bind to a mapping ChunkPilot cannot tell apart from another.

---

## CP-2026-011 — An unrelated foreign allow suppresses exact Minecraft firewall setup

| Field | Value |
|---|---|
| Date | 2026-08-08 |
| Starting branch | `fix/windows-firewall-policy-read` |
| Starting commit | `ce26d59` |
| Severity | High — a random third-party allow could prevent the user from creating a deterministic ChunkPilot-owned exact rule |
| Impact | The real Public-profile approval and owned create/remove lifecycle were unreachable while an Adobe capability rule existed |
| Area | Selected server › Overview › Connect › Windows Firewall › foreign rule coverage |
| Status | **Fixed** |
| Fixed branch | `fix/firewall-foreign-rule-coverage` |
| Fixed commit | `Fix foreign firewall rule coverage`, the final local commit on this branch |
| Regression | Typed INetFwRule/2/3 reader coverage, 25-case semantic matrix, Adobe-style Public consent, exact/broad/unknown allow and block, ownership/removal regressions |
| Validation | Focused: 102 unit + 39 integration; full: 1181 unit + 200 integration, zero skipped; Release build 0 warnings/errors; six focused WPF renders; rebuilt live reader 633/633 rules |

### Verified real-machine evidence

The original acceptance run resolved the exact managed Java runtime, TCP 25566, Ethernet interface
index 16, `10.0.0.140`, gateway `10.0.0.1`, and the Public Windows profile. ChunkPilot nevertheless
reported `ExistingForeignAllow / Broader` owned by `ExistingWindowsRule`, named `Adobe Native Client`,
and withheld Public approval and the ChunkPilot-owned rule lifecycle.

A read-only COM probe found two Adobe rules. The relevant rule is enabled inbound allow, Any protocol,
Any port, Any application, Domain/Private/Public, all addresses and interface types. Its
`LocalUserOwner` is populated with the user's SID and `EdgeTraversalOptions` is 1. The outbound twin
has the same owner constraint. `LocalAppPackageId`, all three authorized user/machine lists, and
`SecureFlags` are empty/zero. The rebuilt reader enumerated all 633 rules currently present with no
unavailable policy or per-rule field and classified the inbound Adobe rule `UnknownOrUnsupported`;
the earlier baseline probe had enumerated 631 rules. Neither probe changed a rule.

### Root cause

`WindowsFirewallPolicy.FindCoveringAllowRule` delegated to one boolean `Applies` predicate. That
predicate checked enabled/inbound, service, protocol, local port, application, profiles, remote port,
local/remote address, and interface type. It did not read or evaluate `Interfaces`,
`IcmpTypesAndCodes`, `EdgeTraversalOptions`, `LocalAppPackageId`, `LocalUserOwner`, authorized local or
remote user lists, authorized remote-machine lists, or `SecureFlags`; it also ignored the already-read
`EdgeTraversal` value. Any Program/Port/Protocol rule passing that incomplete subset was returned as a
covering allow, and the coordinator converted a non-exact match to `ExistingForeignAllow / Broader`.
That was the exact false-positive path for the Adobe rule.

### Correction and regression evidence

The rule snapshot now retains all documented INetFwRule, INetFwRule2, and INetFwRule3 match dimensions
plus per-property availability. A pure semantic evaluator classifies rules as exact equivalent, broad
unrestricted, constrained match, constrained non-match, unknown/unsupported, or non-match. Only a
foreign `ExactEquivalent` allow can suppress setup. Broad, user-owned, packaged/AppContainer,
security-conditioned, and incompletely understood allows remain untouched and appear only as optional
technical evidence; the normal exact ChunkPilot setup remains available.

Known applicable blocks still win over allows. A block clearly excluding the Java target is ignored;
an unknown potentially applicable block prevents a false ready claim and is reported as unverified.
Owned identity still requires the persisted stable ID, exact name, ChunkPilot group, matching
description, and live postcondition. Removing an owned rule leaves every foreign rule untouched.

---

## CP-2026-010 — A valid Windows Firewall policy is collapsed to `ReadFailed`

| Field | Value |
|---|---|
| Date | 2026-08-08 |
| Starting branch | `feature/firewall-compatibility-diagnostics` |
| Starting commit | `7778e0a` |
| Severity | High — a readable enabled Public profile was reported unavailable and its approval workflow was unreachable |
| Impact | `Check again` discarded trustworthy firewall and server evidence and could not offer the existing Public-profile consent action |
| Area | Selected server › Overview › Connect › Windows Firewall › policy read |
| Status | **Fixed** |
| Fixed branch | `fix/windows-firewall-policy-read` |
| Checkpoint | `065322d` (`WIP checkpoint Windows Firewall policy read correction`) |
| Fixed commit | `Fix Windows Firewall policy read`, the final local commit on this branch |
| Regression | `NetFwPolicyReaderTests` plus partial-policy coordinator and presentation coverage |
| Validation | 85 focused unit + 36 focused integration; 1143 full unit + 197 full integration; Release build 0 warnings/errors; three focused WPF renders |

### Verified evidence

On the affected non-elevated Windows session, BFE and MpsSvc were running and every firewall profile
was enabled. `HNetCfg.FwPolicy2` activation, `CurrentProfileTypes` (Public / 4), and
`FirewallEnabled(Public)` succeeded outside ChunkPilot. The same read-only reflection path used by
ChunkPilot also successfully read all three enabled flags, all three `BlockAllInboundTraffic` flags,
`LocalPolicyModifyState`, the rule collection, and its count. `_NewEnum` returned
`System.Runtime.InteropServices.CustomMarshalers.EnumeratorViewOfEnumVariant`.

ChunkPilot nevertheless projected `FirewallPlatformUnavailable / ReadFailed`, with profile firewall
and local policy unknown, so Public approval could not be reached even though TCP 25566, the exact
managed Java executable, Ethernet/index 16, `10.0.0.140`, gateway `10.0.0.1`, and Public were known.

### Root cause

`INetFwRules::_NewEnum` succeeded. Modern .NET projected its native `IEnumVARIANT` as an
`EnumeratorViewOfEnumVariant` implementing `System.Collections.IEnumerator`. `NetFwPolicyReader`
incorrectly required the returned managed wrapper itself to implement the raw
`ComTypes.IEnumVARIANT` interface. The cast returned null, rule enumeration was declared incomplete,
and the all-or-nothing reader converted that later failure into a platform-wide `ReadFailed`.

PowerShell succeeded because ordinary PowerShell enumeration consumes the CLR `IEnumerator`
projection; it did not make the invalid raw-interface cast. This was a COM marshaling/projection
assumption, not another incorrect manually declared COM interface signature.

The identical invalid cast also existed in the elevated helper's pre-mutation ownership read. It was
corrected to the same managed enumeration contract; no privilege boundary or mutation surface changed.

### Resolution

The policy reader now consumes the CLR `IEnumerator` returned by `_NewEnum`. COM activation remains
the platform boundary, while `CurrentProfileTypes`, per-profile `FirewallEnabled`, per-profile
`BlockAllInboundTraffic`, `LocalPolicyModifyState`, and rule enumeration are captured independently.
A later failure retains every earlier proven value and reports the exact typed unavailable field.

Creation and update still fail closed unless the current profile, that exact profile's enabled and
block-all state, local modify state, and complete rule enumeration are all known. Removal and any
claim that an owned rule is absent require complete rule enumeration. Thus a partial read can no
longer falsely make the whole platform unavailable, but it can never authorize an unsafe mutation.

### Regression and real-machine evidence

Deterministic reader tests cover successful Public policy, genuine activation failure, and independent
failure of CurrentProfileTypes, FirewallEnabled, LocalPolicyModifyState, BlockAllInboundTraffic, and
rule enumeration. Coordinator fixtures retain the exact Java runtime, TCP 25566, Ethernet/index 16,
local address, gateway, and Public profile for each partial-policy failure while refusing mutation.
Public, Private, Domain/managed policy, disabled firewall, CP-2026-009 correlation, and VPN/WinTUN
selection remain covered.

After the user disabled Smart App Control, `testhost` loaded the newly rebuilt assemblies normally.
The compiled `NetFwPolicyReader` then completed a read-only real-machine pass: platform Available,
Public active, Domain/Private/Public enabled, no block-all-inbound profile, local modify state OK,
631 rules read, no unavailable field, and complete Public-profile mutation evidence.

The focused WPF review rendered only complete Public policy, partial Rules failure, and genuine
platform-unavailable states at 1000x700. The partial state retains server/network evidence but offers
only Check again/View details; only the complete Public state offers Public approval. No render or
probe invoked the elevated helper or changed Windows Firewall, the network profile, adapters, routes,
services, Smart App Control, Defender, or the router.

---

## CP-2026-009 — Windows Public profile is lost for the routed Ethernet adapter

| Field | Value |
|---|---|
| Date | 2026-08-08 |
| Branch | `feature/friend-connectivity-windows-firewall` |
| Commit | `ab6da80` (observed) |
| Severity | High — a valid Windows profile was reported as unavailable and the consent path could not be reached |
| Area | Selected server › Overview › Connect › Windows Firewall |
| Status | **Fixed** |
| Fixed branch | `fix/windows-firewall-profile-target-resolution` |
| Fixed commit | `Fix Windows Firewall network target resolution`, the single commit on that branch |
| Validation | Real read-only NLM reproduction; 1101 unit + 186 integration tests; Release build 0 warnings/errors; six focused WPF renders |

### Reproduction

Run a managed server on TCP 25566 on the real routed Ethernet interface. ChunkPilot correctly shows
`10.0.0.140:25566`, while Windows reports Ethernet interface index 16, gateway `10.0.0.1`, and the
`NootsBoots` connection profile as Public on that same index. Press **Check again**.

Observed: ChunkPilot reported **Windows Firewall unavailable**, local port 0, blank Java executable,
profile None, and no network. The Public-profile approval flow was unreachable even though Windows had
provided a single exact physical adapter/profile correlation.

### Root cause

`NetworkCategoryView` asked Network List Manager for `IEnumNetworkConnections`, then reflected its
automation `_NewEnum` property and cast the returned .NET automation wrapper to `IEnumVARIANT`. On the
real machine that object is `EnumeratorViewOfEnumVariant`, so the cast failed and the view returned an
empty collection. Public itself was not rejected; no profile ever reached the Public policy branch.

`WindowsFirewallTargetResolver` also returned a blank all-or-nothing failure as soon as any target
layer failed. The coordinator consequently projected an independently known managed Java path and
port 25566 as blank and 0. Those values were secondary casualties of the network failure, not separate
runtime or port discovery failures on the accepted server.

### Resolution

The NLM view now uses strongly typed documented COM interfaces and resolves each NLM adapter GUID to
its Windows IP interface index. The selector first retains the existing physical/routed LAN policy,
then requires one connected known-category binding matching either that positive interface index or
the exact normalized adapter GUID. Multiple matches fail closed. Alias text is never identity.

Target resolution now evaluates Java, port, and profile independently and retains every authoritative
field when another field is missing. The coordinator exposes specific Java, port, or network-profile
states. A resolved Public profile remains a non-error result and enters the separate explicit Public
approval path; no rule is created before that approval and UAC.

### Regression evidence

Coverage reproduces Ethernet/index 16/Public with `10.0.0.140` and `10.0.0.1`, adds WinTUN/index 51
and link-local adapters without changing the winner, proves alias-independent index correlation,
rejects unmatched, ambiguous, and VPN-only topologies, and verifies partial Java/port/profile evidence
through the coordinator and ViewModel. Router tests remain unchanged and are rerun as regression
coverage. No test or diagnostic mutates Windows Firewall or the router.

### Roadmap task

None outstanding.

---

## CP-2026-008 — A stopped server keeps reporting "Router port is open"

| Field | Value |
|---|---|
| Date | 2026-08-08 |
| Branch | `fix/real-router-upnp-mapping` |
| Commit | `8f8cfa4` (observed) |
| Severity | High — the interface asserted an open inbound port for a server that was not running |
| Area | Selected server › Overview › Connect › Direct internet |
| Status | **Fixed** |
| Fixed branch | `fix/router-mapping-stop-cleanup` |
| Fixed commit | `Fix router mapping cleanup on server stop`, the single commit on that branch |
| Validation | 1056 unit + 161 integration tests; rendered `stopped-configured` and `cleanup-failed` |

### Reproduction

On a clean ChunkPilot-owned port, with the owner's pre-existing manual 25565 forward excluded as a
confounder:

1. Set up Direct internet for a server on port 25566 and start it.
2. The router accepts: `UPnP AddPortMapping accepted TCP 25566 to 10.0.0.140:25566 for 3600 seconds.`
3. Press **Stop**.

Observed: Minecraft stops and `Get-NetTCPConnection -LocalPort 25566` returns nothing, but the card
still reads **Router port is open** with the mechanism, internal endpoint, external port, the original
lease expiry and the original AddPortMapping result all presented as current.

### Evidence

The Agent's Stop handler did call `SynchronizeAsync`, which withdrew the mapping and persisted the
result, so the exposure itself was closed. What never happened was the App asking again:
`LoadRouterMappingAsync` ran only from `LoadServerDetailsAsync`, which is guarded by
`detailsServerId != value.Definition.Id` and therefore only on a change of selected server. The
one-second `RefreshAsync` re-read the dashboard and never the mapping, so the card kept the state it
had been handed when the port was opened.

### Impact

Stale, and stale in the most dangerous direction: an interface asserting an open inbound port for a
server that is not running. Two design faults sat behind it. Durable intent and the live mapping had no
distinct resting state, so "configured" had nowhere to render except as the previous phase; and a
stopped server's mapping was withdrawn on a 90-second in-memory grace rather than on the lifecycle
itself, which also meant a crash held the port open for up to a minute and a half and an Agent restart
began the grace again from zero.

### Workaround

Switching to another server and back re-read the state and showed the truth.

### Resolution

`RouterMappingPhase.Inactive` gives "set up, nothing open" a state of its own, distinct from both an
active mapping and a server that was never configured. Stop and restart are now told apart by
`ManagedServer.IsRestartInProgress` — the recorded lifecycle intent paired with the operation still
holding the server's gate — instead of by elapsed time, so a stopped server loses its exposure on the
first reconciliation and a failed restart cannot hold one open indefinitely. The App re-reads the
mapping after every lifecycle command and on the periodic refresh while Direct internet is the selected
method, with a sequence guard so a slower earlier read cannot resurrect a mapping that was removed.

A related consent defect was found and fixed in the same pass: cancelling setup left the recorded
intent enabled, so the next reconciliation would have opened the port the owner had just backed out of.

### Roadmap task

None outstanding.

---

## CP-2026-007 — Direct internet returns to "Not set up" after the router answers

| Field | Value |
|---|---|
| Date | 2026-08-08 |
| Branch | `feature/friend-connectivity-router-mapping` |
| Commit | `271075a` (observed) |
| Severity | Critical — the feature could not be reached at all, and no failure could ever be explained |
| Area | Selected server › Overview › Connect › Direct internet |
| Status | **Fixed** |
| Fixed branch | `fix/real-router-upnp-mapping` |
| Fixed commit | `Fix real-router automatic mapping failure`, the single commit on that branch |
| Validation | Deterministic reproduction at `271075a`; 1030 unit + 156 integration tests; rendered `router-answered` and `mapping-rejected` states |

### Reproduction

Real home router, real run:

1. Select **Direct internet** on Access and save.
2. Open **Overview** and press **Set up automatically**.
3. A brief checking state appears.

Observed: the card returns to **Not set up** with the setup button available again and no explanation.

### Evidence

From the user's Technical details immediately after the attempt:

```
Mechanism               None established
Gateway                 Not identified
Internal endpoint       Not established
External port           —
Address classification  Globally routable
UPnP urn:schemas-upnp-org:service:WANIPConnection:1 answered at
http://10.0.0.1:49152/upnp/control/WANIPConnection0 and reported external address 73.203.43.174.
```

The detail line is the UPnP provider's *supported*-discovery text, and the address classification can
only be set on the supported branch. SSDP discovery, the device description, control-URL resolution
and `GetExternalIPAddress` had therefore all succeeded. The screen contradicted its own evidence.

`AddPortMapping` was never called: the run stopped before the confirmation could be offered.

### Impact

`RouterMappingCoordinator.ResolvePhase` returned `Off` whenever `DirectInternetEnabled` was false,
before considering the result of the operation that had just run. Intent is only recorded by
`EnableAsync`, `EnableAsync` is only reachable through the confirmation, and the confirmation only
opens on a `Supported` result — so a successful check always projected as "Not set up" and the feature
was unreachable on every router. A *failed* check projected as "Not set up" too, so no router failure
of any kind could reach the user.

Two presentation defects made the diagnostic screen actively misleading: `DirectInternetGateway` and
`DirectInternetExternalPortLabel` had no `NotifyPropertyChangedFor` entry and kept their first-bound
values, and the router-reported address row rendered with an empty value beside a live Copy button
because the endpoint string required an external port that no mapping had yet produced.

### Workaround

None. No sequence of user actions could reach the confirmation.

### Resolution

A settled phase is now derived from durable evidence — removal pending, active mapping, last failure,
last successful check — and the runtime carries only "an operation is in flight". Cancellation is not
treated as a failure. Attempts that create nothing record what they learned (gateway, candidate LAN
address, mechanism that answered) without writing ownership evidence. The two missing notifications
were added and are held by a reflective test that fails if any property changes with the state without
announcing it.

### Roadmap task

None outstanding. Whether this particular router accepts `AddPortMapping` is still unknown and needs
one more real-router run; the code now reports either outcome truthfully.

---

## CP-2026-001 — Game rules cannot be established on Minecraft 26.x

| Field | Value |
|---|---|
| Date | 2026-07-30 |
| Branch | `feature/vanilla-workspace-stabilization` |
| Commit | `c4b49c9` (observed), fixed presentation shipped in the stabilization commit |
| Severity | Medium — a feature is unavailable, nothing is unsafe and nothing is misreported |
| Area | Settings › Game rules |
| Status | **Open** |

### Reproduction

1. Create a Vanilla server on Minecraft 26.2 and start it.
2. Open **Settings › Game rules**.

Observed: the card reports that the server did not accept any of the game rules ChunkPilot knows, and
offers no controls.

### Evidence

The server rejects every rule name in `GamerulePolicy`, including the *set* form. From the server's own
log during an isolated review run:

```
[21:49:26] [Server thread/INFO]: Incorrect argument for command
[21:49:26] [Server thread/INFO]: gamerule keepInventory<--[HERE]
```

`gamerule`, `gamerule keepInventory`, `gamerule keepInventory true` and `gamerule query keepInventory`
all fail the same way, so this is not a query-syntax problem: Minecraft 26.x does not recognise the
rule names ChunkPilot carries. Earlier versions (1.x) answer normally, and the integration fixtures
cover both the answering and the refusing server.

### Impact

On Minecraft 26.x the Game rules card lists nothing and explains why. No control lies about a value, no
command is sent that would fail, and every other Settings control is unaffected. On 1.x versions the
card works: values are read from the running server and changes are applied and re-read.

### Workaround

Type the rule directly in **Console** using the names that version uses.

### Roadmap task

Discover rules from the world instead of from a static list: read the `GameRules` compound from
`level.dat` (NBT, gzip) while the server is stopped or immediately after a confirmed save, which yields
the exact rule names and values for *any* version. Treat the current list as a label-and-description
lookup rather than as the source of which rules exist. Belongs with the 1.5 Safety Lab work, where
world-file reading already has a home.

---

## CP-2026-002 — Create Server blocking reason is below the full version list

| Field | Value |
|---|---|
| Date | 2026-08-07 |
| Branch | `feature/vanilla-workspace-premium-polish` |
| Commit | `a0af4164d1235c6527304f4570efecf19725b01e` |
| Severity | High — a valid recovery instruction is not visible when the primary action becomes unavailable |
| Area | Create Server v2 › Vanilla setup |
| Status | **Fixed** |
| Fixed branch | `feature/create-overview-audit-remediation` |
| Fixed commit | `faee7d28eabca6efb654d613411663f7492d7334` |
| Validation | Full Release suite: 783 unit + 102 integration tests; `CS-57` through `CS-59` rendered re-audit |

### Reproduction

1. Create a disposable Vanilla server.
2. Begin a second creation with the same name, or choose a non-empty destination.
3. Remain at the top of **Vanilla setup**.

Observed: **Next** is disabled, but the visible Name and Location cards show no reason. The alert that
explains the collision is rendered after the complete Minecraft version list and Selection card; at
1120×780 it is roughly 800 pixels below the initial viewport. The duplicate-name alert itself is
accurate once found.

### Impact

The workflow looks inert at the exact moment the user needs recovery guidance. Duplicate name,
invalid filesystem name and destination collision need distinct inline explanations beside the field
or location that must change; the page-level summary can remain supplemental.

### Resolution

Name and destination-policy failures now render in danger treatment beside the field or Location card
that must change. Destination failures no longer repeat below the full version list.

---

## CP-2026-003 — Create Server carries the previous step's scroll offset into Review

| Field | Value |
|---|---|
| Date | 2026-08-07 |
| Branch | `feature/vanilla-workspace-premium-polish` |
| Commit | `a0af4164d1235c6527304f4570efecf19725b01e` |
| Severity | Medium — the next step opens partway through its content and hides its heading and summary |
| Area | Create Server v2 › Setup to Review navigation |
| Status | **Fixed** |
| Fixed branch | `feature/create-overview-audit-remediation` |
| Fixed commit | `faee7d28eabca6efb654d613411663f7492d7334` |
| Validation | Full Release suite: 783 unit + 102 integration tests; `CS-35` rendered after entering Review from Setup's bottom |

### Reproduction

1. On **Vanilla setup**, scroll down through the version list and Selection card.
2. Press **Next**.

Observed: **Review** retains the Setup page's vertical offset. Its title and the first review sections
open above the viewport. `Ctrl+Home` reveals the intended top state.

### Impact

Step navigation lacks a stable beginning, harms scanning and can make Review appear incomplete. Each
step transition should reset the shared page scroll viewer to the top before moving keyboard focus.

### Resolution

Every step transition resets the shared scroller. Review receives programmatic focus on its non-tab-stop
heading, preventing initial focus from scrolling the EULA back into view after the reset.

---

## CP-2026-004 — Overview can label a VPN address as the local network address

| Field | Value |
|---|---|
| Date | 2026-08-07 |
| Branch | `feature/vanilla-workspace-premium-polish` |
| Commit | `a0af4164d1235c6527304f4570efecf19725b01e` |
| Severity | High — connection guidance presents a non-LAN address under a LAN label |
| Area | Selected server › Overview › Connection |
| Status | **Fixed** |
| Fixed branch | `feature/create-overview-audit-remediation` |
| Fixed commit | `faee7d28eabca6efb654d613411663f7492d7334` |
| Validation | Full Release suite: 783 unit + 102 integration tests; selector unit matrix; `OV-19`, `OV-22`, `OV-27` rendered re-audit |

### Reproduction

1. Run ChunkPilot on a machine with an active ExpressVPN WinTUN adapter and ordinary local adapters.
2. Select a server and inspect **Connection › Local network**.

Observed: Overview displayed `100.64.100.6:25566`. Windows reported `100.64.100.6/32` belongs to
`ExpressVPNOpenVPN WinTUN Adapter`, not the physical LAN. The current adapter selection excludes the
`Tunnel` enum type but ranks remaining active adapters by link speed, which does not exclude WinTUN.

### Impact

Copying this address to a household player is unlikely to provide LAN connectivity and contradicts
the label. Adapter selection should prefer routable private-subnet addresses on physical/default-route
interfaces, explicitly identify VPN candidates, or report that a LAN address could not be established.

### Resolution

LAN selection now accepts only RFC1918 IPv4 or IPv6 ULA addresses from physical Ethernet or Wi-Fi
interfaces, excludes known VPN/virtual adapter markers, and prefers the effective OS route. Ambiguous
interfaces without route evidence produce no asserted LAN address.

---

## CP-2026-005 — A specific port-bind failure is replaced by a generic exit message

| Field | Value |
|---|---|
| Date | 2026-08-07 |
| Branch | `feature/vanilla-workspace-premium-polish` |
| Commit | `a0af4164d1235c6527304f4570efecf19725b01e` |
| Severity | High — startup fails without exposing the actionable cause already detected by the Agent |
| Area | Selected server › Overview › startup failure |
| Status | **Fixed** |
| Fixed branch | `feature/create-overview-audit-remediation` |
| Fixed commit | `faee7d28eabca6efb654d613411663f7492d7334` |
| Validation | Full Release suite: 783 unit + 102 integration tests; 17 ManagedServer lifecycle tests; real occupied-port `OV-14` through `OV-16` |

### Reproduction

1. Start a disposable server while its configured port is already occupied.
2. Wait for startup to exit.

Observed: the server becomes **Crashed** and Overview reports `Server exited during startup with code
0.` The captured console proves `FAILED TO BIND TO PORT` and `Address already in use`; the Agent first
records `Port 25565 appears to be in use` but its monitor then replaces that detail with the generic
exit message.

### Impact

The user is sent toward logs instead of the direct recovery action: stop the conflicting service or
choose another port. Preserve the more specific detected failure, surface it on Overview and avoid
making exit code 0 the primary explanation for an unsuccessful startup.

### Resolution

Startup failures now retain the strongest detected evidence across output pumps and process-exit races.
Port bind, out-of-memory, known runtime/configuration, and generic failures have explicit precedence;
the rendered result gives the port and recovery action even when the process exits with code 0.

---

## CP-2026-006 — Running performance charts mix samples from separate process attempts

| Field | Value |
|---|---|
| Date | 2026-08-07 |
| Branch | `feature/vanilla-workspace-premium-polish` |
| Commit | `a0af4164d1235c6527304f4570efecf19725b01e` |
| Severity | Medium — chart window and averages do not describe the current run |
| Area | Selected server › Overview › CPU and memory performance |
| Status | **Fixed** |
| Fixed branch | `feature/create-overview-audit-remediation` |
| Fixed commit | `faee7d28eabca6efb654d613411663f7492d7334` |
| Validation | Full Release suite: 783 unit + 102 integration tests; current-attempt integration test; real `OV-22` through `OV-27` metrics |

### Reproduction

1. Start a server and let the process exit during startup.
2. Correct the startup condition and start it again.
3. Compare current uptime with the chart sample-window caption.

Observed: at 23 seconds of current uptime, the charts claimed `14 real samples over 6 min 58 sec`;
at 57 seconds they claimed `31 real samples over 7 min 33 sec`. `ManagedServer` retains its in-memory
sample list across process starts while `StartedAt` and uptime reset.

### Impact

Current, average and peak values mix separate process attempts and the time-window caption is
impossible relative to the displayed uptime. Either reset runtime samples for each process identity or
label and visualize the data explicitly as multi-run history.

### Resolution

Successful fresh launches clear the bounded sample window and reset per-PID CPU/network baselines.
Attempt-scoped readiness and pump tasks also prevent a previous monitor from contaminating a later
launch. In the rendered run, every sample timestamp was at or after the current `StartedAt` value.
