# Current ChunkPilot Gate

## Repository and scope

- Bounded task: faithful large-modpack creation, with ATM10 as the live acceptance case.
- Worktree: `temp/atm10-creation-hardening`; branch: `codex/atm10-creation-hardening`.
- Inspected base: `12bb1f8cf308d94a3ad871c3d7a657abf74ad0c9`, the clean packaged
  `codex/curseforge-runtime-browser-parity` candidate. Its ancestry includes the existing
  server-switch and shallow provider-browser improvements. Version `1.3.0-alpha.5`, schema `6`.
- Primary checkout, unrelated worktrees, preservation stash, installed release, and real
  servers remain untouched. No push, tag, publication, installer, signing, or installation authorized.

## Current outcome: investigation checkpoint, not verified

The candidate-owned failed operation `e0ca54b5f22f4dfbb9ecaf0ed63def6f` retained its
loader log but not its exact CurseForge file identity or socket evidence. Minecraft `1.21.1`
and NeoForge `21.1.249` are established; the loader reported installation success before
staged validation failed. The user recalls selecting the latest ATM10 release. Exact original
client/server file IDs remain unknown. Any current-release substitution must be disclosed.

Source inspection established shared defects:

- Official and generated packs share validator wording that incorrectly says “generated”.
- Every non-loopback local endpoint is rejected without distinguishing TCP listeners,
  connections, or UDP bindings. Prior reports calling Established/remote-443 rows “outbound”
  went beyond that evidence: connection direction is not established by those fields alone.
- Endpoint attribution compares Job PID sets without creation-identity fencing.
- Verified remote archives are deleted before runtime validation, forcing repeat downloads.
- Failed preactivation attempts lose their journal even when staging cleanup fails.
- Validation changes bind properties but not `level-name`, potentially booting the intended world.
- Generated CurseForge overrides silently omit gameplay roots such as KubeJS.

The latest task narrows execution to one small official-pack control, then ATM10. Generated-pack,
mod-dependency, and update/rollback campaigns are deferred. The existing official path extracts
runtime-consumed scripting/content roots; no content-removal workaround is authorized.

These are source findings, not proof that the upstream pack is broken. No new live pack has run.

## Current implementation checkpoint (local core correction, live evidence pending)

- Shared typed inbound-startup decision and native parser now serve production and controller.
  Non-listening TCP connections retain direction/purpose uncertainty; non-loopback TCP listeners
  block; unresolved non-loopback UDP and missing ownership return cannot-verify.
- Native observation holds exact Job-member process handles and creation identities across capture.
  Startup is bounded and observed during loading, not only after Done.
- Validation uses an exact-owned disposable world and child temp directory. It restores properties
  only after Job-empty proof; failure, timeout and cancellation also check selected-port absence.
- Focused boundary/nullable/controller/creation tests: 102 passed. Expanded isolated loader/process/world
  integration: 21 passed. No skips or build warnings/errors in these final focused runs.
  Initial regressions exposed two obsolete controller expectations, a directory-vs-file safety helper
  misuse and a fast-root-exit handle lifetime; final focused reruns passed after correction.
- The expanded run initially found a missing parent directory in loader-temp setup (7 failures);
  the existing safe directory-creation helper corrected it. A fixture missing its new attempt identity
  also collided with retained validation evidence; the fixture now supplies a fresh ID on each call.
- Latest Agent Release build: zero warnings/errors. Verified-input journal retention and progress
  plumbing are in progress; authoritative retry/discard and packaged/live evidence remain pending.
- The user explicitly accepted the Minecraft EULA for these disposable control/ATM10 tests only.
- The controller now offers explicit stopped-run retention; failure and success both record selected-port
  absence independently of Job exit. Durable validation decisions survive mutable staging cleanup.
- Verified server-archive fixtures demonstrate a controlled late failure, retained journal, explicit reuse
  with HTTP archive requests disabled, and tamper rejection. Authoritative Agent/UI retry is not yet exposed.
- Next live run is the prior exact Optimized Performance control: project `1172292`, client `6110282`,
  server file `6110285`, subject to fresh native official metadata. It is not an ATM10 substitution.

Budget reconciliation: the previous 2 GiB campaign ledger is historical and unchanged
(990,582,328 bytes guarded, including conservative unknown reservations). The immediately preceding
ATM10 task explicitly authorized up to 3 GiB of newly downloaded CurseForge payload. No new campaign
payload has yet been downloaded, and no historical counter has been reset or silently increased.

## Implementation and evidence gates

Preserve exact official-release priority, pack content, transactional promotion, native credentials,
existing App/Agent lifetime policy, and exact-owned cleanup. Implement typed bounded endpoint evidence,
disposable validation worlds, truthful stage progress, and operation-owned verified-input retry.
Unknown UDP purpose or unavailable ownership must remain an explicit cannot-verify result, not
verified safety or confirmed exposure. Runtime monitoring is not preventive sandboxing.

Use synthetic records for forbidden-listener tests; do not open wildcard listeners on this PC.
All new runtime data, server files, child temporary paths, and recovery inputs must be task-owned.
New CurseForge payload budget is 3 GiB; record Java/loader downloads separately. One large JVM and
one heavyweight build at a time. EULA acceptance must come through the existing deliberate mechanism.

Pending: boundary regressions; HighRisk development checks; self-contained package and ownership/close
smoke; exact official metadata; packaged ATM10 creation/status/clean stop/promotion; promoted launch/stop;
file fidelity; deterministic late failure and retry with zero repeated archive bytes; smaller control;
packaged progress/recovery UX, scaling, keyboard, High Contrast, and Reduced Motion inspection.

## Next blocked gate

`VALIDATION FIX STILL BLOCKED`

Implementation and live evidence are pending. Startup/status alone will not prove client joins, quests,
recipes, all gameplay, public reachability, provider lifecycle completion, or public Beta readiness.
After the required evidence, the next gate is user acceptance of this fix, followed by the remaining
unproven live CurseForge lifecycle checks. Existing equal-High friction and publication gates remain open.
