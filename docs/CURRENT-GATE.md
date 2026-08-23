# Current ChunkPilot Gate

> Git and directly inspected runtime evidence override this file. Automated work can establish a Beta
> Candidate; it cannot claim real router, firewall, signing, installer, personal-server, or user acceptance.

## Repository state

- Branch: `codex/beta-readiness-hardening`, based directly on public `origin/main` at
  `7cea6e2f2365d5e582d82ed8c9aa7d5bbae5a763`.
- Product hardening commit: `6641c29ebf932ee58f4e07f209576fee8edc95d6`.
- Final handoff HEAD: the documentation/copy commit containing this gate, directly descended from the
  product commit. Its exact object ID is authoritative from `git rev-parse HEAD` and the final report;
  a commit cannot embed its own object ID.
- Public Alpha 5 tag: `v1.3.0-alpha.5`, peeling to
  `69efa222e1a20471c1cc959283d02c0731d6ae5a`.
- Version `1.3.0-alpha.5`; database schema `6`; no migration or packaging-identity change.
- Primary checkout, unrelated worktrees, installed release, real server data, and preservation stash were
  not modified. Nothing was pushed, tagged, or published.

## Current gate

**BETA CANDIDATE** for user acceptance. This is not a public Beta declaration or release.

## Beta-candidate acceptance criteria

- Zero known open reproducible Critical or High product-code defects in the supported Minecraft core.
- Full HighRisk suites and exact packaged App/Agent ownership smokes pass with no data-safety regression,
  cross-server leak, lifecycle hang, hidden egress, or unexplained owned process.
- Major UI surfaces, narrow/scale/accessibility states, dependency/secret posture, and resource use have
  direct evidence. Registers and limitations remain truthful.

## VERIFIED evidence

- WebUI: 26 files / 145 tests, typecheck, lint, and 207-module production build passed.
- .NET: 1,339 unit and 351 integration tests passed, zero failed/skipped; Release solution build completed
  with zero warnings/errors; schema remained 6.
- Self-contained win-x64 App, Agent, and Firewall Helper published. Packaged Agent self-test passed 15/15.
  Exact committed bytes opened the default WebUI, accepted normal close in under 210 ms, stopped their exact
  Agent, preserved an unrelated isolated Agent, left zero invisible UI/owned processes, and cleaned both roots.
- Security: npm and NuGet audits found zero vulnerabilities. Gitleaks 8.30.1 scanned 48 reachable commits
  with zero findings; 1,027 reachable blobs contained zero prohibited paths. CSP, trusted origin, bridge
  allowlist/size bounds, session capability, DPAPI boundary, helper allowlist, and external-link policy were
  inspected. `docs/security/NETWORK-EGRESS.md` records production Internet and local-network paths.
- Four defects were reproduced before correction and now have regressions: CP-2026-035 through CP-2026-038.
  The Critical diagnostic-export issue and all other new findings are fixed.
- Visual/runtime: 54 deterministic packaged captures covered the major shell, creation, Vanilla, Paper,
  modded/modpack, console, players, files, content, backup, version, settings, help, and connectivity surfaces.
  Reviewed states included 920-wide long names, 1100x700, 1280-class, 1440x900, 125%, 150%, High Contrast,
  and Reduced Motion. Keyboard focus visibly traversed navigation, Enter activated Servers, and Alt+F4 closed.
- Performance used the same isolated package method, three launches, three-second warmup, and five-second
  idle window before and after. Cold startup was 1,333.3 -> 1,350.7 ms; warm mean 1,361.9 -> 1,322.8 ms.
  Mean App/Agent/WebView CPU was 0.716/0.176/0.670% -> 0.300/0.137/0.280%; combined working set
  669.8 -> 640.3 MiB; App/Agent/WebView working set 195.9/59.8/405.0 -> 182.7/57.9/390.7 MiB.
  Process/WebView/renderer counts remained 9/6/1. Mean close was 169.1 -> 200.6 ms, every run below 210 ms
  with zero leftovers. The changes do not affect the idle loop, so lower CPU/I/O is treated as system noise,
  not a claimed optimization; no material regression was observed.
- Debloat: no tracked file/package passed the proof-of-death bar, so none was removed. The owned baseline
  worktree and review profiles/captures recovered 977,325,406 bytes (about 932 MiB). Historical certification
  evidence and reachable old icon blobs were intentionally retained.

## REPORTED evidence

Alpha 5's installer lifecycle, Alpha 4 upgrade, clean extraction, and prior exact compatibility campaigns
remain release evidence from the accepted public snapshot; they were not rerun as if this were a release.

## UNKNOWN/manual evidence

- Real Windows Firewall elevation, the actual router, PCP/NAT-PMP/UPnP, CGNAT/double NAT, NAT loopback,
  outside-home Minecraft, task-kill behavior, and personal servers/worlds were deliberately not touched.
- Fresh second-PC install/upgrade experience, unsigned SmartScreen UX, public signing, CurseForge approval,
  and user visual/workflow acceptance remain external.
- No new live provider compatibility campaign was run; deterministic provider/failure fixtures and existing
  exact certification evidence were used.

## Known bugs/friction

- Critical blockers: 0. High product-code blockers: 0.
- Medium: CP-2026-001 remains open and isolated; unsupported Minecraft 26.x gamerules are shown as unavailable
  and Console remains the documented workaround.
- External/manual: CP-2026-020 remains register-open until real Windows/network close and task-kill acceptance,
  despite its implementation and automated ownership/close evidence passing here.
- CurseForge live access and the missing root source-license decision remain explicit non-defect decisions.

## Current bounded unit

Completed: beta-readiness hardening of existing functionality only. No feature breadth was added.

## Explicit non-goals

No new game/loader/provider, cloud service, account, telemetry, remote/mobile management, AI help, signing
identity, installer technology, product-license choice, real-server access, or public release work.

## Stop conditions

Any newly reproduced Critical/High defect, data-safety uncertainty, ownership-policy ambiguity, or unexplained
owned process/performance regression reopens this gate and changes the verdict to **NOT BETA READY**.

## Required verification

Passed: targeted regressions, full frontend/.NET HighRisk suites, Release build, self-contained publish,
packaged Agent/default-WebUI/normal-close smokes, audits, diff hygiene, performance, and packaged visual/runtime
review. Repository-wide `dotnet format --verify-no-changes` remains unavailable as a clean gate because 38
pre-existing files have unrelated whitespace drift; no mass formatting was performed.

## Manual acceptance still required

User review of the development build, real-machine networking/privilege checks, fresh-PC experience, personal
server workflows, public signing, and explicit authorization before any Beta publication.

## Next blocked roadmap step

User acceptance and real-machine connectivity acceptance before any public Beta declaration/release.
