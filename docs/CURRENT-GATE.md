# Current ChunkPilot Gate

**VALIDATION FIX AND ATM10 LIFECYCLE VERIFIED — USER ACCEPTANCE PENDING**

## Repository

Worktree: `temp/atm10-creation-hardening`; branch: `codex/atm10-creation-hardening`.
Starting SHA: `12bb1f8cf308d94a3ad871c3d7a657abf74ad0c9`. Recovery/checkpoint: `ea1855e`.
Tested live code: `5a82ac82a392bdd33f61e85256b44d446d13afdd`. The final handoff commit changes
documentation only; the packaged manifest records its exact Git identity and unchanged code-input digest.
Local checkpoints `7176309`, `4ab5bf7`, `2a3948f`, `9d3899e`, `f906d3a`, and `5a82ac8` preserve
successive live-discovered corrections rather than rewriting history. No push, tag, installer,
signing, publication, installation or normal-data-root migration was performed.

Version remains `1.3.0-alpha.5`; database schema `6`. Creation journal shape is `2`, with shape-1
read compatibility and fail-closed handling by older builds. Primary checkout, unrelated worktrees/stash,
real servers, installed release, firewall/router, registry and services are untouched.

Self-contained candidate: `artifacts/dev-current/ChunkPilot.exe`, with Agent and Certification siblings.
Manifest: `artifacts/dev-current/.chunkpilot-build-manifest.json`, schema 3, isolated HEAD archive,
437 package-input files, exact HEAD/index/worktree input agreement. Code-input SHA-256:
`e020a5491dfd6f0b9c7fb7c762322eb370aa8013210506c4c6e2290ff63aacea`.
Installer, portable ZIP, signing and publication are outside this correction, not failed checks.

## Actual root cause

Production rejected `ownedEndpoints.Any(endpoint => !endpoint.IsLoopback)` without checking TCP
state. The controller used a different listener-only collector, so its passing tests missed the production
decision. A documentation-address regression captures the old false rejection. The older failed report
did not preserve its raw socket tuple; its exact destination cannot be reconstructed.

The corrected control exposed separate cleanup defects: newly retained input/evidence was called unsafe
residue; timestamp-prefixed terminal logs were misparsed; managed Java left an empty extraction wrapper.
All were corrected and regression-tested. Only the exact verified-empty control wrapper was removed
non-recursively; no files were deleted by that repair.

ATM10 then exposed NeoForge's independent LAN advertisement. `server-ip=127.0.0.1` did not disable
the dedicated-server pinger. This was a networking-preference gap, not a reason to allow unknown UDP.

## Observed connection

Control validation: exact-owned Java PID 20920, raw creation 134330984935300860, expected loopback
listener and 71 non-listening observations (Established/FinWait1). No SynSent was captured; initiating
direction, hostname, component and purpose remain unknown.

Initial ATM10: Java PID 10352, raw creation 134331033769765365; expected loopback listener plus
wildcard IPv4/IPv6 UDP port 58729. Inventory/ownership succeeded; UDP purpose could not be verified.
It failed closed, stopped cleanly, restored configuration, removed its disposable world, proved Job
empty/port absent, and retained its archive without promotion. The first native retry reproduced
unknown UDP with Java PID 26064, raw creation 134331042465026125.

One bounded, exact-identity-checked read-only thread dump caught `LanServerPinger`. NeoForge's
[official configuration](https://github.com/neoforged/NeoForge/blob/1.21.1/src/main/java/net/neoforged/neoforge/common/NeoForgeConfig.java)
and [dedicated-server patch](https://github.com/neoforged/NeoForge/blob/1.21.1/patches/net/minecraft/server/dedicated/DedicatedServer.java.patch)
explain its default-on LAN advertisement. This supports probable attribution, not socket-level proof
from a thread name. No packet-content capture, TLS interception, certificate changes, whole-machine
history collection or credential inspection was used.

Successful ATM10 validation: Java PID 28524, raw creation 134331054191171218, port 56302. History:
one expected listener, 88 non-listening TCP observations with direction unknown, one SynSent outgoing
attempt, no unresolved UDP. Destination hostnames/third-party purposes remain unestablished.
This is bounded observed startup validation, not an air gap, telemetry verdict or mod security certification.

## Correction

One typed policy serves production, controller and regressions. Non-loopback TCP LISTEN rows block;
non-listening rows retain uncertainty; SynSent establishes an outgoing attempt only. Unknown UDP,
ownership, collector failures and stale attempts fail closed. Exact Job-member handles/raw creation
identities span IPv4/IPv6 capture. No PID-only attribution, test-only bypass or real wildcard fixture.

Startup uses a uniquely owned disposable world/loopback port, query/RCON disabled, absolute deadline,
bounded evidence/output and exact-owned shutdown. Success/cancellation/failure independently check Job
emptiness, listener absence, configuration restoration and owned-world removal. Cleanup failures retain
the original failure and cannot offer unsafe retry or claim no change.

New NeoForge creations choosing **This computer only** disable only `advertiseDedicatedServerToLan`
in the effective new configuration. Validation uses its own world-local override; intended configuration
bytes and pack defaults are preserved. The bounded editor preserves other values/comments/BOM/line
endings and rejects ambiguous shapes. Existing registered servers are not rewritten. Unknown UDP
still blocks, including in the new integration regression.

## Live outcomes

All reports below are under `artifacts/cf-startup-control/evidence/` and remain unedited.

| Check | Exact small official control | Exact new ATM10 acceptance case |
|---|---|---|
| Project / client / server file | 1172292 / 6110282 / 6110285 | 925200 / 8764211 / 8764245 |
| Selection | Optimized Performance, fixed prior selection | Explicitly approved new latest-official case, pinned before transfer |
| Minecraft / loader | 1.21.1 / Fabric | 1.21.1 / NeoForge 21.1.249 |
| Verified client / server bytes | 679,356 / 13,983,967 | 201,751,521 / 1,205,156,753 |
| Staged startup/status/clean stop | Passed, 37.97 s | Passed, 110.45 s |
| Promotion | Passed | Passed, same retained operation/server identity |
| Promoted start / status / stop | Final recheck: 8.59 s / 70 ms / 0.86 s | 84.71 s / 34 ms / 25.07 s |
| Job empty / game port absent | Passed / passed | Passed / passed |
| Final report | `certification-control-9d3899e-resume.json`: PASSED | `certification-atm10-5a82ac8-retry.json`: PASSED |

Small staged/promoted lifecycle passed at `7176309`; complete cleanup passed at `9d3899e`. Its three
failed reports remain. Both ATM10 unknown-UDP reports (`certification-atm10-9d3899e.json`,
`certification-atm10-f906d3a-retry.json`) remain FAILED, not relabeled. Successful retry creation took
144.48 s. Both exact Agent Jobs exited 0 with zero members.

Historical ATM10 operation `e0ca54b5f22f4dfbb9ecaf0ed63def6f` lost its provider file IDs. It establishes
Minecraft/NeoForge only. This new case does not reproduce that unknown release. EULA acceptance was
deliberate and limited to these disposable tests.

Content: all 87 small-archive files matched. ATM10's pre-validation production ownership baseline has
2,229 entries: 1,293 byte-identical, 770 byte-identical under documented language-splitter
`.snbt_merged` names, 165 present but rewritten during startup, and the operation marker intentionally
removed at promotion. No unexplained missing content. All 461 mods, 102 libraries, three default configs
and the datapack match. 537/538 KubeJS files match; `Crops.js` explicitly generates changed
`cropInfo.json`. FTB Quests loaded 15 translation tables. Runtime config/quest rewrites are recorded
in `atm10-content-preservation.json`; gameplay semantics are not certified. This compares the
production pre-validation baseline, not an independent original-archive entry manifest.
The [language-splitter documentation](https://github.com/pietro-lopes/FTB-Quests-Lang-Splitter#features)
explains the merge/rename. No mod, quest, recipe, script or datapack was removed to obtain startup.

## Progress and retry

Existing measured transfer progress remains. The WebUI distinguishes indeterminate first-start elapsed
time, last meaningful milestone and fresh output; exposes cancel/Activity; bounds reconnects; and restores
native retained operations across navigation/restart. Warning spam cannot fake advancement or extend
deadlines. Recovery labels identify what did and did not change.

Native Retry/Discard revalidate exact provider relationship/digests and local SHA-256/ownership/expiry,
fence generations/duplicates, reconstruct mutable validation state and preserve server identity.
Generic creation cannot smuggle retry flags or overwrite journals. Uncertain cleanup is RecoveryRequired.
Archive retention is capped at eight operations / 8 GiB total / 4 GiB each, expires after 48 hours and
reserves partial capacity. Promotion releases input; late failures retain it.

ATM10 operation `7198ad8a-cdf1-4d0e-992b-d3f644bc1455` proved real late failure and two native retries.
Both transferred **zero repeated CurseForge archive bytes**, with fresh metadata/API checks. The second
succeeded and released its archive. Java/loader traffic is separate. The new ledger totals
1,421,571,597 completed bytes, zero unknown reservations, 725,912,051 bytes remaining under its unchanged
conservative 2 GiB cap. The user authorized 3 GiB new payload; the cap was not raised. Historical ledger
and conservative unknown reservations are untouched.

## Checks

Focused final correction: 45 unit and 21 integration passed. HighRisk at tested code: 1,701 unit,
408 headless integration and 192 WebUI tests passed, zero failed/skipped among selected tests.
Restore, explicit Release solution build (zero warnings/errors), typecheck/lint/frontend build and
self-contained App/Agent/controller publish passed. Database migration fixtures are included in the
passing integration suite; no normal user database was migrated. Visible `Minimize_keeps_hosting_then_WM_CLOSE` is explicitly
excluded, not passed. No desktop takeover or normal-close foreground smoke was performed.

Initial failures were corrected, not hidden: obsolete endpoint expectations; path helper misuse;
fast-root-exit handle lifetime; missing loader-temp parent/attempt ID; pre-cancel directory assumption;
obsolete journal deletion; collision recovery preservation; new helper's absent-parent check.
The final public-document check caught an absolute local worktree path; it was changed to a relative path.
The CancellationToken-order warning was corrected before the passing rerun. Failed live cleanup and
unknown-UDP evidence remain separate from successful reruns.

History audit at tested code: 79 commits, 1,461 reachable blobs, zero unexpected secrets/prohibited paths,
two existing documented false positives. Git emitted long-path warnings for ignored generated pack
cache directories, not tracked source omissions. Pre-existing missing source-license file remains a
publication issue. NuGet check found no listed vulnerabilities in nine projects.
Packaged Agent smoke passed all 15 self-checks, created only a disposable database, and exited 0.
All 51 public Markdown documents/local links and `git diff --check` passed. Final manifest/clean-tree
rechecks accompany the documentation-only handoff package; its code-input digest is unchanged.

## Remaining gate and retained candidate

Stopped ATM10 server `e902afe1-e0fc-4805-9194-05fe2db7015c` is retained at:
`artifacts/cf-startup-control/runs/run-20260905-173518-1859e8fb455b47a38cd97085fdc55329/servers/ATM10-latest-official-acceptance`.
Intended settings: loopback `127.0.0.1:25586`, world `world`, online mode/whitelist true, query/RCON false,
2–8 GiB heap. Validation world/ephemeral settings are absent. Its archive was released after promotion;
the small stopped control and its archive remain.

Pending: owner foreground progress/failure/retry/layout/keyboard/normal-close acceptance, client join,
multiplayer, quests/recipes/gameplay, broader pack/version/provider scenarios. No public reachability,
modpack update/rollback, provider approval, signing, licensing or public Beta readiness is implied.
This milestone's startup/lifecycle and retained-input retry gates are verified, not those separate gates.

**WAITING ON USER ACCEPTANCE**
