# Current ChunkPilot Gate

## Repository

Worktree `temp/atm10-creation-hardening`, branch `codex/atm10-creation-hardening`.
Starting SHA `12bb1f8cf308d94a3ad871c3d7a657abf74ad0c9`; rollback note `ea1855e`;
first code checkpoint `71763092402039fa0fe867d2887a0e76bec2d05a`.
Version remains `1.3.0-alpha.5`, database schema `6`. Creation journal shape is now `2`;
shape-1 journals remain readable; older builds fail closed on newer journals.
Primary checkout, unrelated worktrees/stash, installed release, and real servers are untouched.
No push, tag, installer, signing, publication, or installation is authorized.

## Actual root cause and correction

Production formerly rejected every owned non-loopback endpoint, without checking TCP state.
The controller used a different listener-only collector, so its passing predicates did not cover
the production decision. Established plus remote port 443 cannot establish direction or purpose.
The shared typed policy distinguishes LISTEN, non-listening TCP, and UDP. Non-loopback TCP
listeners block. Unknown ownership, unavailable tables and non-loopback UDP purpose fail closed.
SynSent establishes an outgoing attempt only. Other connection states retain direction uncertainty.
This is bounded observed startup validation, not an air gap or a security certification of mods.

The native collector holds exact Job-member handles and raw process creation identities across
capture, including IPv4/IPv6 parsing. Validation boots an exact-owned disposable world, restores
configuration only after Job-empty proof, and independently proves its port is released. Forbidden
listeners are synthetic test records only; no real wildcard listener fixture is opened.

## Live control evidence

Exact Optimized Performance project `1172292`, client `6110282`, server file `6110285` ran through
the packaged Agent/named-pipe workflow at `7176309`. Package manifest schema 3 lists 434 inputs;
source kind `isolated-head-archive`, inputs match HEAD. Input-set SHA-256:
`39f48ae70080d4303acb6869fe322528c790444e9cf35baf97cc6bed4d3af5c5`.

Creation operation `73d80f8a-34c2-4d70-b514-68e191ed80be` completed in 45.44 seconds. Disposable
validation took 37.97 seconds and proved readiness/status, clean stop, Job empty, port absence,
configuration restoration and disposable-world removal. Promoted start passed in 26.43 seconds,
Minecraft status in 121 ms, and clean stop in 2.27 seconds. Owned Agent/bootstrap Jobs exited 0.

Validation evidence recorded 1 expected game listener and 71 non-listening connection observations
from exact-owned Java PID 20920, creation identity 134330984935300860. Established and FinWait1
were observed; SynSent was not. Destination hostname, initiating direction and component purpose
remain unknown. No IP-to-hostname inference is claimed.

**The original overall control report failed**: its cleanup gate misclassified the newly retained
input and bounded validation JSON as unsafe residue. Process/port cleanup did pass. The report is
preserved unchanged at `artifacts/cf-startup-control/evidence/certification-control-7176309.json`.
The stopped control and verified archive remain deliberately retained. The corrected controller
can recheck that exact stopped instance and archive against original evidence and fresh metadata,
then repeat promoted start/status/stop without another archive request. That recheck is pending.

## ATM10 identity and consent

Original operation `e0ca54b5f22f4dfbb9ecaf0ed63def6f` establishes Minecraft `1.21.1`, NeoForge
`21.1.249`, and completed loader installation. Cleanup erased the client/server file IDs. The
original release cannot be reproduced from the authorized task-owned records.
Explicit approval now permits the current latest official ATM10 release as a **new acceptance
case**, not a reproduction of that missing historical identity. Native discovery pins exact IDs
before preflight/download; subsequent checks use those exact IDs, never a moving latest pointer.
Deliberate Minecraft EULA acceptance is limited to these disposable control/ATM10 tests.

## Progress and retry

Native Agent commands expose retained failures across restart, retry generation fencing,
fresh exact provider relationship/digest checks, and explicit input discard. Generic creation
cannot smuggle a retry flag or overwrite an existing operation journal. Uncertain process cleanup
does not offer retry/discard or claim that nothing changed. Post-promotion bookkeeping errors
remain completed-with-warning rather than false failed-nothing-changed results.

Verified server inputs are operation-owned, SHA-256 bound, expire after 48 hours, and are capped
at eight operations / 8 GiB total / 4 GiB per input. Pending downloads reserve declared bytes.
Successful promotion releases its input; failures retain immutable input and reconstruct mutable
validation state on retry. No general provider cache, persisted CDN URL or credential is added.
Cancellation and cleanup exceptions carry durable structured validation evidence.

The WebUI shows actual transfer measurements, indeterminate startup elapsed time, last meaningful
milestone versus fresh log output, cancel/Activity, and native-authorized retry/discard. Repeated
warnings cannot reset startup deadlines or suppress the no-new-milestone warning. Status reconnect
is bounded and does not invent a terminal result.

## Checks and current gate

Final focused recovery/provider/controller unit run: 82 passed. Targeted transaction/retry/staged
runtime integration rerun: 60 passed. New WebUI recovery tests: 3 passed; typecheck passed.
Earlier core boundary slice: 102 passed; isolated loader/process/world integration: 21 passed.
Initial failures are retained: obsolete endpoint expectations; path helper misuse; fast-root-exit
handle lifetime; missing loader-temp parent; missing fixture attempt ID; pre-cancel directory
assumption; obsolete journal-deletion assertion; and a non-pack collision candidate cleanup
regression (corrected while preserving its prior recovery behavior).

Pending: full headless HighRisk development checks, final exact-input self-contained package,
corrected control recheck, current ATM10 lifecycle, content fidelity and packaged ownership smoke.
Foreground/scaling/keyboard/High Contrast/Reduced Motion/normal-close checks are not certified.
No client join, quest/recipe gameplay, public reachability, or public Beta readiness is claimed.

New payload ledger: client 679,356 + server 13,983,967 = 14,663,323 completed bytes. Historical
2 GiB ledger remains unchanged, including conservative unknown reservations. The prior task
authorized 3 GiB of new payload; the current ledger cap remains a conservative 2 GiB. Java/loader
transfers are separate. One large JVM and one heavyweight build at a time.

`VALIDATION FIX STILL BLOCKED` until the complete cleanup gate passes. Small staged validation and
promoted lifecycle have passed, but this is not an overall campaign success. Next: package the
reviewed correction, recheck the retained control, then run the separately authorized ATM10 case.
