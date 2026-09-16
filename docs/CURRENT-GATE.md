# Current ChunkPilot Gate

## 1.0.1 candidate gate — September 15, 2026

Branch: `codex/1.0-readiness-20260915`. Tested candidate:
`b3483022ef7b805693aded1eaafd6ef4f17e0870`. Product **1.0.1**, Windows numeric version
**1.3.2**, database schema **6**, creation journal shape **2**. This candidate was built from a
clean tree with the explicit public CurseForge service endpoint. The selected public build uses
an **empty endpoint with personal-key setup**; paid service capacity remains a separate open gate.
Candidate service evidence is not proof of the
final public build, publication or public keyless-service activation.

### Completed checks

| Gate | Result |
| --- | --- |
| Frontend tests, typecheck, lint and production build | 342 passed; checks passed |
| Full native unit suite | 2,295 passed; no failures or skips |
| Full native integration suite | 523 passed; no failures; one file-symbolic-link privilege skip |
| CurseForge service suite | 106 passed |
| Dependency restore, framework-dependent and self-contained publish, portable staging, installer build | Passed; zero compiler warnings/errors |
| Isolated packaged Agent smoke | 15 checks; zero errors; clean exit; ApplicationService mode and no personal credential |

The unsigned candidate artifacts are under `artifacts/release/v1.0.1`. The independent audit
matched all 27 portable ZIP entries and all 26 shipped-file SBOM hashes; there were no missing,
extra or mismatched files. The SPDX SBOM has 194 dependency records, including the build/test
graph—not 194 shipped runtime libraries. Public signature paths are package-relative; no
prohibited consumer files or selected private-path/credential markers were found. The portable
README covers both service-enabled and personal-key builds. Installer execution, upgrade and
automated packaged UI-close checks remain hosted-runner gates, not local checks claimed here.

### Real workflow evidence and open gates

- Earlier exact package `377917e` completed a fresh official small-pack case
  **1172292 / 6110282 / 6110285** in about 61 seconds: two Agent lifetimes without a personal
  credential, real Java initial/restart readiness, loopback Minecraft status, save/stop and
  complete owned-process/listener cleanup. This is not a public-network or GUI-creation claim.
- A separate isolated-copy backup/restore workflow verified the automatic pre-restore backup,
  matched 204 restored file hashes, restarted the restored server, checked loopback status and
  saved/stopped it cleanly. All 208 retained baseline files and the original report were unchanged.
  These earlier workflow results do not independently certify every later binary.
- Exact candidate `b348302` applied the official small-pack update from client/server
  **6110282 / 6110285** to **6211453 / 6211455**. Updated Java startup, loopback Minecraft status
  and save-first stop passed. Rollback verified 101 world/mod/configuration/settings files,
  including 16 world files and 21 mods, and preserved three sentinels. The old runtime and RAM
  settings were restored; rolled-back startup, status and save-first stop passed. All 208 original
  files and eight protected evidence files remained unchanged. Exact-owned Agent/Java exit and
  port 25585 cleanup passed.
- Both earlier failed update-audit reports remain retained: the first exposed the missing exact
  game-version filter, fixed before candidate B; the second stopped after a successful update
  because the harness rejected an equivalent working directory ending in `\.`. The corrected
  harness accepted its canonical root, separately verified the selected managed Java runtime,
  and continued from the verified applied state to complete runtime and rollback checks.
- Physical candidate B inspection with a fresh Agent showed the persisted last-backup date and
  verified recovery point before opening Backups, then showed both verified backup records.
  After rollback, **Check pack release** resolved provider names and offered client release
  **6211453**. Normal Alt+F4 closed App and Agent; TCP and UDP port 25585 were absent.
- Large-archive service delivery remains blocked by the observed Cloudflare Workers Free CPU
  limit. No billing change or public keyless activation is claimed. The selected personal-key
  public build still needs its own final build and release gates. Small-pack success is not blanket
  large-pack, loader, router, Windows-version or low-end-hardware certification.

The last physical review also exposed raw machine names in update and version-health labels.
The shared update label now uses the existing human-readable formatter without changing its status
or evidence. Unidentified imported loader versions are distinguished from known versions missing
from the catalog. The corrected frontend passed **356 tests**, typecheck, lint and build; native
presentation tests passed **101/101**. The final Release workflow must rebuild and test that source.

Current candidate build evidence: `logs/readiness-candidate-b-publish.log`,
`logs/readiness-candidate-b-package.log`, and `logs/readiness-candidate-b-agent-smoke.json`.
Runtime evidence is retained under `artifacts/readiness-small-runtime/evidence` and
`artifacts/readiness-backup-review/evidence`, including the successful
`pack-update-runtime-continuation.json` and both earlier failed reports. These local evidence
directories are ignored. The September 14 record below is preserved historical evidence, not
the current release status.

## 1.0 release gate — September 14, 2026

Branch: `codex/release-readiness-2026-09-14`. Product version **1.0.0**; Windows numeric version
**1.3.1** preserves upgrade order from previously distributed 1.3.0-alpha installers. Database schema
**6** and creation journal shape **2** are unchanged. Starting source:
`66e5f2aeb67b9b0f04ce58103e809447c5257089`. The earlier recovery package remains at
`artifacts/recovery/modpack-controls-base-005b9d9`.

This source-freeze record is not universal external certification. The manual **Release** workflow
records the exact commit, completed build/test gates, installer upgrade results, artifact hashes and
publication status. A failed gate prevents that workflow from publishing.

## Delivered

- Exact official CurseForge server-pack relationships, typed generated content, manifest pin ordering,
  author distribution checks and a durable bounded payload ledger.
- Native personal CurseForge key setup: official validate-before-replace, Windows DPAPI protection,
  native session and originating-pipe cancellation fences, removal that preserves installed servers.
  The renderer receives no plaintext or encrypted credential; no shared key is bundled.
- Serialized deliberate bind apply/restart, truthful connection summaries, stopped-player access,
  repeatable icon editing, clearer startup progress and installed-pack identity.
- Strict backup inventory/server identity checks, staged restore rollback, reparse protection,
  newest verified recovery retention, verified-stop requirements for stopped data mutations.
- Atomic per-attempt Windows Job ownership for new server processes and automation children,
  exact legacy reattachment, bounded status reads and previous-attempt cleanup.
- Fresh same-attempt empty-player shutdown checks, stale confirmation fences, bounded roster
  rendering, thumbnail decoding and network concurrency.

## Observed checks before the immutable package gate

- Initial combined run: **1,827 unit passed / 1 failed**, **452 integration passed / 1 failed**.
  The image-cancellation fixture observed a disposable callback instead of the canceled operation;
  it was corrected. The file-symbolic-link fixture lacked Windows privilege and now reports an
  explicit capability skip; real directory-junction tests pass. Initial failures are not counted
  as passes without subsequent execution.
- Process-focused integration: **55 passed / 1 failed**, followed by **7/7 passed** after correcting
  traversal into a proven-foreign reused-PID branch. All 44 selected ManagedServer cases passed,
  including Job orphan cleanup, binding restart and running backup.
- Backup-focused: **25 passed / 1 unavailable skip**. Eight reproduced defects and the additional
  ancestor-destination empty-backup defect passed after correction.
- Final frontend source: **250/250 tests in 39 files**, typecheck, lint and production build passed.
- Credential/process unit: **25/25 passed** (7 service, 4 native dialog, 14 status/ownership).
  Real named-pipe cancellation integration: **4/4 passed**.
- The next complete integration run passed **463 / 0 failed / 1 privilege skip**. Its two unit
  failures (obsolete alpha-only README assertion and animated PNG metadata identification) were fixed;
  the corrected full unit run passed **1,845/1,845**. New generic stopped-data guard checks passed
  **8/8**, including persisted identity and actual owned TCP/UDP listeners.
- Targeted native compilation had zero warnings/errors. Final all-source tests, exact-package visual
  fixtures, portable smoke and disposable-runner installer upgrades remain mandatory release gates.

Logs/TRX live under ignored `logs/`. Package manifests bind artifacts to committed inputs; an older
`dev-current` does not certify newer source. No normal database or personal server was a test fixture.

The complete native gate at `0c651ea01a598066c9cb4de609e9bfeb57f5ee90` passed **1,850 unit tests**
and **469 integration tests**, with zero failures, one explicit file-symbolic-link privilege skip,
and zero compiler warnings/errors. Packaged Agent smoke passed **15/15**. Twelve exact-package
desktop fixtures were captured and reviewed. That review exposed transitional performance wording
and a missing fixture-only CurseForge preflight response. Publication was deliberately cancelled
before any tag/assets were created; the UI-only corrections and malformed-response regression passed
**261/261 frontend tests**, typecheck, lint and build. Native implementation files were unchanged.
The final exact commit still must pass the immutable release workflow; its results are authoritative.

The first exact `dda6e68` hosted gate passed **1,850 unit tests** and **261 frontend tests**, but
integration ended at **469 passed / 1 failed / 0 skipped**: the minimize/WM_CLOSE lifecycle assertions
passed, then immediate deletion of its disposable WebView2 profile encountered a still-locked log.
Publication stopped before packaging or tag creation. The fixture now uses the existing bounded
cleanup helper after exact process exit; lifecycle assertions remain unchanged. This failed hosted
attempt is retained as evidence, not counted as a successful release gate.

## Live provider evidence

The new native credential service passed a live official API validation in disposable protected
storage, followed by exact StaTech metadata retrieval. Both session checks ran and the stored key
was readable through the production DPAPI store. This downloaded no pack payload, started no Java,
created no world, and did not exercise the native dialog end-to-end. Three diagnostic regressions pass.

Official StaTech **1605376 / 8699911 / 8699918** is authenticated. Client and server archives passed
native download/hash and preflight: **475,063,831 bytes**, 2,775 server files, Minecraft 1.21.1 /
NeoForge 21.1.248. Server SHA-256:
`696aa67e9dc88af3aa2f135b71fd781a880f4ef5c1345c1ce46836aebfb322c5`.
This is **archive/plan verification**, not a new game-server launch or player join.

AE2 Blackout **1060372 / 8393438** is authenticated as a resource pack. Generated Fabulously Optimized
**396246 / 8217274** correctly stops at dependency **697845 / 5424446**, whose author disallows
distribution. No required content was omitted or restriction bypassed. Phase payload ledger:
**475,405,117 bytes**, below the retained 2 GiB ceiling.

Earlier ATM10 acceptance is separate: **925200 / 8764211 / 8764245**, Minecraft 1.21.1 /
NeoForge 21.1.249. Installation, promotion, second launch, local status and clean stop passed at
`5a82ac82a392bdd33f61e85256b44d446d13afdd`; a subsequent owner join was reported. The retained world
was not adopted, launched or modified here. That earlier run does not independently certify this freeze.

A fresh small official pack **1172292 / 6110282 / 6110285** passed on exact package `0c651ea`:
download/install, real Java readiness, Minecraft status, explicit save, normal stop and every exact
process/listener cleanup check. Total time was about 50 seconds; no forced termination was needed.
Its new disposable world is retained separately from older acceptance worlds. The cumulative phase
ledger increased to **490,068,440 bytes**, with no uncertain reservations. This proves the packaged
native workflow, not a GUI-driven creation or public network connection.

The same small official case was freshly repeated on exact package `dda6e68`: **46 seconds** overall,
real Java readiness in **9.830 seconds**, loopback status in **58.7 ms**, and save-first normal stop in
**1.405 seconds**. All owned process/listener cleanup checks passed, with no forced termination.
Cumulative completed payload is **504,731,763 bytes**, eight completed ledger entries and no uncertain
reservations. The canonical and evidence ledgers were reconciled with exclusive locks and atomic
compare-and-replace, preserving the previous ledger as recovery evidence. No old world was touched.

Exact `dda6e68` packaged UI acceptance covered twelve desktop render states plus stopped overview,
CurseForge exact-release keyboard selection, and high-contrast keyboard navigation. Fifteen PNGs and
identity-bound evidence are retained under ignored `logs/`. These are synthetic WebView2 fixtures;
renderer scale flags do not certify actual Windows DPI configurations or phone support.

## Remaining boundaries

- Every router, Starlink network, antivirus, Windows version and low-end PC is **not** physically
  certified. Router/firewall configuration is not outside-in evidence. CGNAT may require an external
  tunnel/private network or an ISP public address; there is no universal bypass.
- Unsigned builds may trigger SmartScreen. No source-license grant was selected; public visibility
  alone is not an open-source license.
- CurseForge requires each user's own approved API access and honors author restrictions. Shared-key
  distribution authorization is not claimed.
- One local file-symbolic-link test is unavailable without Windows privilege. The development machine
  is not reconfigured to enable it; the hosted disposable Windows gate reports its own capability.
- Some historical versions lack official server artifacts and need original user-supplied files.
- Backup restore is an overlay, not indiscriminate deletion of files missing from an archive.
  Unexpected activation termination retains recovery evidence. Path checks do not claim to eliminate
  every hostile concurrent filesystem race.

See [backup safety](operations/BACKUP-SAFETY.md), [providers](features/PROVIDERS.md),
[credential delivery](architecture/CURSEFORGE-CREDENTIAL-DELIVERY.md),
[releasing](release/RELEASING.md) and [bug register](troubleshooting/BUG-REGISTER.md).
