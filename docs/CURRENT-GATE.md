# Current ChunkPilot Gate

> Git and directly inspected runtime evidence override this file. Automated work may establish a candidate
> but may not declare visual, credential-policy, external-network, installer, signing, or user acceptance on
> the user's behalf.

## Repository state

- Worktree: repository-local `temp\curseforge-modpack-platform`
- Branch: `codex/curseforge-modpack-platform`
- Starting HEAD and accepted Beta base: `592d1c1478f229ef335611c7952f145a1112d66b`
- Base ancestry: accepted Beta Candidate, itself based on public `origin/main` at
  `7cea6e2f2365d5e582d82ed8c9aa7d5bbae5a763`
- Server-switch checkpoint: `655e86e` (`Make server switching immediate and isolated`)
- Provider/credential checkpoint: `c34bf76` (`Activate the native CurseForge catalog and credential boundary`)
- Final lifecycle checkpoint: the commit containing this document; resolve it with `git rev-parse HEAD`.
- Version: `1.3.0-alpha.5`; database schema: `6`
- Primary checkout, preservation stash, installed Alpha 5, real servers, and real network state are excluded.
- Nothing from this phase is pushed, tagged, published, or released.

## Current gate

The instant-switch and first-class CurseForge/modpack implementation is an unpushed local Beta Candidate.
The automated and isolated runtime gates are green. Public CurseForge credential delivery, live provider
validation, representative real-pack acceptance, and final user acceptance remain open.

## Evidence entering this phase

- VERIFIED: the accepted Beta base and its automated package evidence are present and clean.
- REPORTED: the user accepted the candidate except that switching servers shows a full-page
  `Opening <server>` state for roughly ten seconds.
- REPORTED: CurseForge application access is approved.
- VERIFIED: official terms require the application credential but do not establish permission to distribute
  one shared credential in an arbitrary desktop binary. `PUBLIC CREDENTIAL DELIVERY STILL GATED`.
- VERIFIED: the one authorized local credential file was absent. No credential value was read, no alternate
  secret location was inspected, and live CurseForge authentication was not attempted.
- VERIFIED: the native selection result was discarded by the renderer, which then waited for a periodic
  presentation refresh that can include unrelated Agent and connectivity work. There was no intentional
  ten-second navigation timer.
- VERIFIED: selection now applies the authoritative response immediately, native details remain behind an
  exact server-ID loading/ready fence, unstamped native models are cleared on identity change, and the
  renderer's ready-workspace cache is bounded to eight exact IDs.
- VERIFIED: 30 cached warm switches measured p50/p95/max `0.001/0.009/0.049 ms`; 10 cold authoritative
  acknowledgements measured `0.308/0.499/0.499 ms`; 20 reverse-completed rapid requests settled in
  `5.735 ms`. The boundary issued 30 native selection requests and zero provider requests.
- VERIFIED: deterministic coverage implements official CurseForge discovery and exact link/file resolution,
  official server packs, supportable generated candidates, local manifest import, exact dependencies,
  ownership-aware whole-pack updates, rollback, and CurseForge Mods operations.
- VERIFIED: WebUI `151/151`, unit `1388/1388`, integration `351/351`, Release typecheck/lint/build, and the
  self-contained development package completed successfully. Packaged Agent, normal-close ownership, and
  three-iteration startup/idle/close smokes passed in isolated temporary data.
- VERIFIED: final 1440-class, compact 125%, high-contrast/reduced-motion 150%, and keyboard-focus fixture
  inspection found and corrected one compact rate-limit-card wrapping defect. No horizontal page overflow or
  stale provider/server content was observed in the reviewed states.
- UNKNOWN/manual: real router/firewall/outside-in, fresh-PC, signing, installer, personal-server, real-pack,
  and final visual/workflow acceptance remain external.

## Completed bounded unit

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

The exact local candidate can be opened from the isolated worktree with:

```powershell
$chunkPilotRoot = (Resolve-Path '.').Path
Set-Location (Join-Path $chunkPilotRoot 'temp\curseforge-modpack-platform')
$env:CHUNKPILOT_CURSEFORGE_KEY_FILE = Join-Path $chunkPilotRoot '.secrets\curseforge-api-key.txt'
& '.\artifacts\dev-current\ChunkPilot.exe'
```

## Next blocked roadmap step

CurseForge/modpack user acceptance and representative real-pack validation.
