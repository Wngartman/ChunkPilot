# Current ChunkPilot Gate

> Git and directly inspected runtime evidence override this file. Correct stale state before proceeding.
> Automated work may establish a Beta Candidate, but it may not declare real-machine, visual, installer,
> privilege, router/firewall, outside-in, signing, or user acceptance on the user's behalf.

## Repository state

- Isolated worktree: dedicated `temp/beta-readiness-hardening` checkout
- Branch: `codex/beta-readiness-hardening`
- Starting HEAD and ancestry: `7cea6e2f2365d5e582d82ed8c9aa7d5bbae5a763`, tracking exact `origin/main`
- Public Alpha 5 tag: `v1.3.0-alpha.5` peels to `69efa222e1a20471c1cc959283d02c0731d6ae5a`
- Version: `1.3.0-alpha.5`; database schema: `6`
- Primary checkout, unrelated worktrees, installed release, and preservation stash are untouched.
- Nothing from this audit is pushed, tagged, or published.

## Current gate

Beta-readiness audit and hardening of the existing Minecraft product, with no new feature breadth until the
reliability gate is understood. This document does not currently declare ChunkPilot a Beta Candidate.

## Beta-candidate acceptance criteria

- No known open reproducible Critical or High defect in the supported Minecraft core.
- Automated suites, Release build, development package, packaged smoke, close/ownership smoke, audits, and
  source hygiene checks pass.
- No known data-safety regression, cross-server state leak, lifecycle deadlock, hidden telemetry/egress,
  unexplained owned process, or unexplained measurable performance regression remains.
- Major screens receive direct packaged visual/runtime inspection, and the registers remain truthful.

## VERIFIED evidence

- The branch point, remote main, Alpha 5 tag identity, version metadata, and schema were directly inspected.
- The branch began clean in a dedicated worktree; no user-owned work was reset, discarded, or merged.
- The two commits after the Alpha 5 tag are release-verification/release-wrapper changes by Git inspection.

## REPORTED evidence

- Alpha 5 reportedly passed 143 WebUI, 1,337 unit, 349 integration, and 15 packaged-Agent tests, Release
  build, dependency/secret audits, clean extraction, install/upgrade/reinstall/uninstall, and persistence checks.
- Recent server-state isolation, Players, Help, networking clarity, world upload, modpack UI, icon, and
  debloat work is reported implemented. It remains subject to this branch's direct inspection and reruns.

## UNKNOWN/manual evidence

- Current local baseline and final suite results, packaged runtime behavior, visual/accessibility coverage,
  performance, complete production egress inventory, and open-defect counts are not yet established.
- Real router/firewall/outside-in behavior, real personal worlds/servers, signing, fresh-PC installation, and
  user acceptance remain external/manual evidence.

## Known bugs/friction

- No new defect is recorded until it is reproduced. Existing authoritative bug and friction registers remain
  in force and will not be erased.
- Missing root product license and approval-gated CurseForge access remain decisions, not audit fixes.

## Current bounded unit

Build the product-surface matrix; reproduce and fix all discovered Critical/High defects; fix only coherent,
bounded Medium defects; document production egress; measure resource use; remove only proven waste; and run
the proportional HighRisk development gate using synthetic fixtures.

## Explicit non-goals

No new games, loader breadth, cloud/network provider, account, telemetry, remote/mobile management, AI help,
signing identity, installer technology, product license choice, real-server access, or public release work.

## Stop conditions

Stop a branch of work for unresolved product/security/ownership policy, unsafe access to excluded state, or
two substantially different failed fixes. Any unfixed reproducible Critical/High defect blocks the verdict.

## Required verification

Targeted regressions; all frontend and .NET suites; typecheck/lint/build; Release build; self-contained package;
packaged Agent/default-WebUI/normal-close smokes; dependency and secret audits; `git diff --check`; measured
performance; direct packaged visual/runtime review; and installer checks only if packaging changes.

## Manual acceptance still required

User review of the resulting development build, a fresh-PC experience where needed, real networking and
privilege behavior, real personal-server workflows, public signing, and any later public Beta authorization.

## Next blocked roadmap step

No feature breadth or public Beta declaration/release proceeds until this audit has a final verdict and the
remaining user and real-machine acceptance gates are completed.
