# Current ChunkPilot Gate

> Git and directly inspected runtime evidence override this checkpoint. Browser metadata and fixture tests cannot
> substitute for a downloaded payload, a real server lifecycle, mod mutation, update, rollback, or recovery.

## Repository state

- Worktree: isolated `temp\curseforge-runtime-browser-parity` checkout
- Branch: `codex/curseforge-runtime-browser-parity`
- Starting HEAD and prior live-API checkpoint: `a5ec5df9846122442589015c8a340023b1657e8e`
- Provider-platform base: `35e8eb7ca059c3ae98cc917fd3260354c3a77b33`
- Public-base ancestry: `origin/main` at `7cea6e2f2365d5e582d82ed8c9aa7d5bbae5a763`
- Version: `1.3.0-alpha.5`; database schema: `6`
- Browser/runtime-safety checkpoint: `8b408600d6b0d505045535593a8e7ceb6b20f87b`
- The checkpoint was committed from a clean worktree after the full local browser, unit, integration, build,
  documentation and fixture-render gates. It is not yet the final runtime-certified candidate.
- The primary checkout and preservation stash remain untouched. Nothing in this phase may be pushed, tagged,
  published, signed, installed, or released.

## Current gate

The packaged modpack browser has reached its local parity checkpoint. The active gate is now the mandatory live
CurseForge payload and runtime campaign, followed by final regression, package, security, cleanup, commit, and
user-acceptance checks.

## Browser checkpoint

- **VERIFIED — baseline root cause:** the old UI requested only 20 items and CurseForge serially enriched every
  card with exact file/server relationships before returning discovery. Static path analysis established a
  worst-case upper bound of about 1,201 provider reads for one 20-card page. Shared renderer state could show old
  provider rows during a switch; Modrinth omitted its requested offset; filtering could underfill a CurseForge page
  and make locally inferred pagination repeat or hide later provider rows.
- **VERIFIED — local correction:** both providers use shallow 50-project pages and propagate the provider's true
  cursor and total. Exact release/server-path work begins only when a card is selected. The renderer has separate
  bounded provider sessions, cancellable/stale-fenced requests, deduplicated append, a 200-item cap, virtualized
  cards, near-end auto-load and an accessible **Load more** action.
- **VERIFIED — related corrections:** new searches reset a deep result scroll before the append threshold can fire,
  and parent-owned CurseForge preflight output synchronizes into the selected exact-detail model.
- **VERIFIED — packaged live result:** Modrinth showed 50 of 12,631 packs. CurseForge showed 49, then 97, then 144
  accepted unique packs against the official bounded total of 10,000. The visible shortfall reflects live
  cross-page duplicates or policy-invalid rows; the raw provider cursor still advanced by its real page position.
- **VERIFIED — session/detail result:** returning to an already loaded provider restored its own rows in roughly
  130-169 ms without relabelling another provider's content. Exact SkyFactory 5 detail resolved live and identified
  an official server pack.
- **VERIFIED — regression boundary:** shallow/no-N+1 discovery, three raw 50-result pages, Modrinth offset,
  CurseForge raw-cursor advancement, provider isolation/restore, stale cancellation, query-scroll reset, lazy
  detail, underfilled pages, optional-cache fallback, preflight detail synchronization, bounded rendering and a
  four-slot cancellable/coalesced thumbnail pipeline have automated coverage.
- **VERIFIED — runtime safety corrections:** long preflight/update/rollback timeouts are method-scoped; migration-
  review downloads can be reused only once after exact Agent-side identity and hash revalidation; pending updates
  can be deliberately marked healthy in the WebUI; rollback requires exact confirmation; and stale Mods/Plugins
  metadata or install requests cannot reselect or target another server.
- **VERIFIED — checkpoint finalization:** WebUI `180/180`, .NET unit `1,450/1,450`, integration `358/358`, Release
  solution build with `0` warnings and `0` errors, npm audit with `0` vulnerabilities, public-document validation,
  `git diff --check`, and an actual native fixture render all passed before commit.
- **PENDING:** self-contained package rebuild and every mandatory live payload/runtime result below.

## Initial live-runtime failure

The first SkyFactory 5 preflight began downloading its exact client archive but the renderer's old global
15-second request timeout cancelled the native operation. The partial payload was deleted. This is a truthful
failed attempt, not a certified preflight, and it established that payload-scale operations cannot use the short
metadata timeout. The extended-operation/cancellation path must pass its own packaged and recovery checks before
the campaign resumes.

## Runtime evidence still missing

- No CurseForge archive or mod payload is yet certified as completely downloaded and verified in this phase.
- No official server pack has been created, launched, queried and stopped through the packaged candidate.
- No generated candidate has completed or reached a final, evidence-backed unsupported disposition.
- Waystones and Balm have not completed real dependency-aware install and remove against the isolated server.
- No adjacent provider-backed pack update, verified rollback, or controlled post-recovery-point failure has
  completed.
- Browser acceptance and metadata resolution do not satisfy any of these runtime requirements.

## Current bounded unit

1. Rebuild the self-contained development package from the exact committed checkpoint, run its Agent/default-
   WebUI/normal-close/security smokes, and record its embedded Git identity.
2. Reauthenticate through the approved native-only credential path and repeat
   exact preflight under the `2 GiB` aggregate CurseForge payload budget.
3. Complete official-pack create/start/status/stop, then generated-candidate disposition, real mod/dependency
   install/remove, adjacent-release update/rollback, and controlled-failure recovery in task-owned roots.
4. Inspect each actual packaged state, capture only sanitized evidence, run the full deterministic/build/package/
   close/security/publication/documentation gates, and move disposable large payloads to Recycle Bin.
5. Prove no task-owned process, listener, staging directory, secret-bearing artifact, or untracked residue remains;
   commit coherent checkpoints and stop for user acceptance without publishing.

## Download and safety bounds

- Maximum newly downloaded CurseForge payload across this campaign: `2 GiB`; every attempted and completed byte
  must be counted before another candidate is selected.
- Downloads, extracted servers, Java runtimes, caches, backups, and recovery points remain beneath a validated
  task-owned isolated root. Real servers, worlds, backups, production AppData and installed Alpha 5 are excluded.
- EULA remains explicit. Query, RCON and public hosting remain disabled. Runtime listeners are loopback-only on
  dynamically selected ports. No firewall, router, registry, service, route or adapter mutation is authorized.
- Windows security dialogs are never automated. Establish exact process ownership, report the dialog verbatim,
  and pause for the user if one blocks the campaign. Unrelated Prism Launcher state remains untouched.

## Provider-policy boundary

`PUBLIC CREDENTIAL DELIVERY STILL GATED`. The approved key may be imported only by the native prestarted Agent;
its value, derivative and source contents are never printed, copied, persisted in reports, passed in arguments or
exposed to React. Local authenticated use does not authorize embedding or redistributing a non-transferable key.
Written CurseForge clarification is still required for public activation and the minimum installed-state
persistence interpretation. No broker, proxy, user-facing key field, or persistent API-response cache is added.
The absent root license and Authenticode signing remain separate gates.

## Manual evidence still required

- Final user acceptance of browser density, organization, switching and the complete live workflow.
- Fresh-PC, Windows-version, antivirus, signing and installed-upgrade acceptance remain outside this local
  development checkpoint.
- Any owned Windows prompt must be handled by the user after exact ownership and requested action are reported.

## Next gate

After every live workflow and final verification gate passes: `CurseForge/modpack user acceptance, then daily
Minecraft management completeness and the equal-High friction register`. Do not advance to another game first.
