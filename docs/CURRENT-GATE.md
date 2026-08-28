# Current ChunkPilot Gate

> Git and directly inspected runtime evidence override this file. Automated work may establish a candidate
> but may not declare visual, credential-policy, external-network, installer, signing, or user acceptance on
> the user's behalf.

## Repository state

- Worktree: `D:\ChunkPilot\temp\curseforge-modpack-platform`
- Branch: `codex/curseforge-modpack-platform`
- Starting HEAD and accepted Beta base: `592d1c1478f229ef335611c7952f145a1112d66b`
- Base ancestry: accepted Beta Candidate, itself based on public `origin/main` at
  `7cea6e2f2365d5e582d82ed8c9aa7d5bbae5a763`
- Version: `1.3.0-alpha.5`; database schema: `6`
- Primary checkout, preservation stash, installed Alpha 5, real servers, and real network state are excluded.
- Nothing from this phase is pushed, tagged, published, or released.

## Current gate

Gate 1 server-switch responsiveness is implemented and under final checkpoint validation. First-class
CurseForge/modpack lifecycle work remains gated until this fix is committed independently.

## Evidence entering this phase

- VERIFIED: the accepted Beta base and its automated package evidence are present and clean.
- REPORTED: the user accepted the candidate except that switching servers shows a full-page
  `Opening <server>` state for roughly ten seconds.
- REPORTED: CurseForge application access is approved. The credential file, approval terms, direct-client
  delivery policy, and live provider behavior have not yet been inspected or established in this worktree.
- VERIFIED: the native selection result was discarded by the renderer, which then waited for a periodic
  presentation refresh that can include unrelated Agent and connectivity work. There was no intentional
  ten-second navigation timer.
- VERIFIED: selection now applies the authoritative response immediately, native details remain behind an
  exact server-ID loading/ready fence, unstamped native models are cleared on identity change, and the
  renderer's ready-workspace cache is bounded to eight exact IDs.
- VERIFIED: 30 cached warm switches measured p50/p95/max `0.001/0.009/0.049 ms`; 10 cold authoritative
  acknowledgements measured `0.308/0.499/0.499 ms`; 20 reverse-completed rapid requests settled in
  `5.735 ms`. The boundary issued 30 native selection requests and zero provider requests.
- UNKNOWN/manual: real router/firewall/outside-in, fresh-PC, signing, installer, personal-server, real-pack,
  and final visual/workflow acceptance remain external.

## Current bounded unit

1. Make target identity and cached server workspace usable immediately while preserving immutable identity,
   generation fencing, cancellation, and wrong-server mutation protection.
2. Add a native-only, excluded, redacted CurseForge credential boundary and determine the honest public
   delivery policy from current official terms.
3. Extend the existing provider, creation, Content, update, backup, validation, and rollback architecture for
   exact supportable CurseForge modpacks and server mods. Unsupported packs must fail truthfully.

## Non-goals

No public release, signing, license decision, hosted credential proxy, API-key UI, client launcher, client
sync, scraping, arbitrary script execution, new game/provider breadth, Terraria expansion, remote management,
telemetry, cloud account, or general visual redesign.

## Stop conditions

Stop expansion for unclear credential-distribution permission, required scraping/proxying, guessed
compatibility, unsafe/destructive real-data testing, unresolved wrong-server mutation, failed transactional
rollback, or any unbounded provider/network behavior. Public CurseForge activation remains gated when direct
desktop delivery is not explicitly permitted.

## Required verification

- Server-switch warm/cold/rapid timing and identity regressions, with zero provider requests from selection.
- Credential exclusion/redaction/sentinel scans and official-source policy record.
- Deterministic provider, link, server-pack, generated-candidate, dependency, Content, update, rollback,
  cancellation, recovery, archive, SSRF, rate-limit, and failure-path tests.
- Full WebUI and .NET suites; Release build; self-contained package; packaged Agent/UI/close ownership smoke;
  dependency, publication, package-secret, documentation, and diff audits.
- Bounded live CurseForge smoke only after protections exist and only from the one authorized key-file path.

## Manual acceptance still required

Fast/cold switching UX, CurseForge discovery/details/exact selection, representative real supportable and
unsupported packs, update conflict review, real networking/privilege behavior, fresh-PC experience, signing,
and any public credential/release authorization.

## Next blocked roadmap step

CurseForge/modpack user acceptance and representative real-pack validation.
