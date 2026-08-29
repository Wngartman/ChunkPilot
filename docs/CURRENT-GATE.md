# Current ChunkPilot Gate

> Git and inspected live behavior override this checkpoint. Automated work may establish a candidate but
> may not declare public credential permission, external-network behavior, installer/signing readiness, or
> user acceptance on the user's behalf.

## Repository state

- Worktree: `D:\ChunkPilot\temp\curseforge-live-certification`
- Branch: `codex/curseforge-live-certification`
- Starting provider-platform base: `35e8eb7ca059c3ae98cc917fd3260354c3a77b33`
- Live-certification implementation commit: `3f505aa1bdf3a9b57b99ddff909132da9b18394d`
- Final verification checkpoint: the commit containing this document; resolve it with `git rev-parse HEAD`.
- Version: `1.3.0-alpha.5`; database schema: `6`
- The primary checkout, installed Alpha 5, real servers, real worlds, firewall, router, registry, services, and
  unrelated Java processes were not modified. Nothing was pushed, tagged, published, installed, or released.

## Current result

The live CurseForge key was accepted and the bounded official metadata/API campaign passed all 15 sanitized
categories. Four live defects and one credential-isolation audit defect were corrected with deterministic
regressions. The real archive/runtime campaign did not run: an unrelated Windows Security firewall dialog for a
Prism Launcher/OpenJDK/Minecraft process covered the candidate UI and could not be dismissed without the user.

The candidate is therefore **not ready for CurseForge real-pack user acceptance**. API activation is verified;
official-pack creation, generated-candidate creation, Mods installation, provider-backed update/rollback, and
DPAPI close/relaunch remain unavailable rather than passed.

## Credential authentication

- AUTHORIZED: the user explicitly authorized local use of the one approved credential file for this campaign.
- VERIFIED: native validate-before-store provisioning accepted official Minecraft game identity `432` and
  imported the credential into isolated DPAPI data.
- VERIFIED: rejected, unavailable, cancelled, and malformed rotation paths preserve the last accepted native
  credential in deterministic regressions.
- VERIFIED: ordinary status and SelfTest presence checks do not decrypt the credential; raw named-pipe key
  mutation is unavailable; fixture and packaged test children receive a fixture-local missing key path.
- SECURITY: no credential value, prefix, suffix, length, hash, header, source contents, or serialized request was
  printed, committed, packaged, or written to campaign evidence.

## Live API evidence

The real provider session verified authentication/Minecraft identity, categories, 5,007 modloader rows,
103 Minecraft versions, modpack search, two-page provider pagination, exact project/file detail, official
server-pack relationship, project and exact-file link resolution, approved CDN URL metadata resolution, one
exact mod with a required dependency, and bounded not-found mapping. No persistent provider cache was used.

The selected live identities were:

- SkyFactory 5 project `392141`: release `5.0.8`, client file `6290684`, official server file `6290699`,
  Forge/Minecraft 1.20.1, server archive metadata size `295,045,888` bytes. Adjacent release `5.0.7` used client
  file `6132545` and server file `6132552`, with server archive metadata size `291,998,635` bytes.
- DarkRPG project `515345`: release `9.0.2`, client file `8658606`, official server file `8658669`,
  Fabric/Minecraft 1.20.1, server archive metadata size `3,334,980` bytes.
- Waystones project `245755`, file `7682270`, Forge/Minecraft 1.20.1, metadata size `533,738` bytes; required
  Balm dependency project `531761`, file `8545415`, metadata size `561,670` bytes.

No CurseForge archive or mod payload was downloaded. The approved CDN URL was resolved from official metadata
only. Consequently, published hash verification, local SHA-256, extraction, Java/loader materialization,
readiness, handshake, promotion, update/rollback, controlled-failure recovery, and provider-owned file cleanup
have no live-runtime result.

## Runtime and package evidence

- VERIFIED: the actual packaged dashboard rendered at `1280x820`; the blocking Windows dialog belonged to an
  unrelated Prism Launcher/OpenJDK/Minecraft process, not ChunkPilot.
- VERIFIED: after the campaign was stopped, exact candidate App PID `3876` and Agent PID `12428` were resolved by
  executable path, the App was terminated, UI-death cleanup completed, and zero candidate-owned processes and
  zero listeners on the isolated port remained. No unrelated process or security dialog was manipulated.
- VERIFIED: the ignored isolated campaign state was moved to the Windows Recycle Bin after evidence capture,
  recovering `32,026,112` bytes; small sanitized reports and the development package remain.
- VERIFIED: final deterministic evidence before this checkpoint was WebUI `156/156`, unit `1424/1424`,
  integration `355/355`, typecheck, lint, Vite production build, Release solution build with zero warnings/errors,
  restore, npm audit, NuGet audit, publication audit, and package secret/path scan.
- VERIFIED: packaged Agent self-test/shutdown and normal-close/process-ownership smokes pass with isolated data and
  an explicitly missing fixture-local CurseForge key source.
- UNAVAILABLE: actual packaged CurseForge search/review, EULA, download progress, creation/start/readiness/stop,
  Mods dependency review, update/rollback, controlled failure, and DPAPI close/relaunch inspection.

## Defects corrected

1. Credential rotation stored locally valid input before live authentication; it now validates exact official
   identity before DPAPI mutation and preserves the prior accepted value on all non-success paths.
2. Missing/null project or file distribution state could be treated as permission, and filtered pages could
   corrupt the provider cursor; availability now requires explicit `true` and pagination uses the raw cursor.
3. Creation/update could proceed without exact client-manifest loader proof; exact bounded preflight now runs
   before snapshot, materialization, or active switch.
4. API labels, URLs, hashes, descriptions, and cache filenames could become durable state; one persistence policy
   now retains only minimal exact numeric identity plus local rollback/ownership evidence.
5. SelfTest decrypted a credential merely to check presence, while isolated integration/release harness children
   could inherit the approved key-file source; presence is non-decrypting and every child fixture now overrides
   the source with a missing fixture-local path.

## Public credential and persistence boundary

`PUBLIC CREDENTIAL DELIVERY STILL GATED`. The approved key is non-transferable, and local authentication does not
authorize embedding or sharing it with end users. Current terms broadly prohibit saving/caching API data. Public
activation remains gated until CurseForge gives written clarification that ChunkPilot may retain the smallest
installed-state evidence required for exact launch, ownership, recovery, and rollback: numeric project/client/
server-file IDs, compatibility, fixed origin, and locally computed hashes/sizes/paths.

Distribution controls fail closed: project and file availability must be explicit, links cannot bypass exact
compatibility review, redirects/cookies are disabled, and final API/CDN hosts are revalidated.

## Remaining limitations

- Critical: the mandatory end-to-end live official server-pack workflow is unavailable.
- High: live generated-candidate, mod/dependency, compatible update/rollback, controlled failure, and DPAPI
  relaunch evidence are unavailable.
- Medium: live cancellation responsiveness, archive download throughput, extraction time, staged startup time,
  actual CurseForge UI failure states, and a natural distribution-disabled/`429` response were not observed.
- Provider-policy: written permission for public key delivery and minimum installed-state persistence is absent.
- External/manual: cancel the unrelated Windows Security prompt, relaunch the isolated candidate, and complete the
  real-pack campaign. Fresh-PC, signing, installer, and final visual/workflow acceptance remain external.

## Manual launch

```powershell
Set-Location 'D:\ChunkPilot\temp\curseforge-live-certification'
$env:CHUNKPILOT_CURSEFORGE_KEY_FILE = 'D:\ChunkPilot\.secrets\curseforge-api-key.txt'
& 'D:\ChunkPilot\temp\curseforge-live-certification\artifacts\dev-current\ChunkPilot.exe'
```

## Next blocked roadmap step

`CurseForge real-pack user acceptance`, after the mandatory live runtime campaign is completed. If accepted, the
next step is `Daily Minecraft management completeness and the equal-High friction register`.
