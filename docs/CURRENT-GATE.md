# Current ChunkPilot Gate

> Git, package manifests, and directly inspected runtime evidence override this checkpoint. A server reaching its
> readiness message is not a successful certification when the isolation policy subsequently rejects its network
> behavior.

## Repository state

- Worktree: isolated `temp\curseforge-runtime-browser-parity` checkout
- Branch: `codex/curseforge-runtime-browser-parity`
- Base and inherited live-API checkpoint: `a5ec5df9846122442589015c8a340023b1657e8e`
- Runtime-safety/package checkpoint exercised by the live controller: `0be6c3f4cfaa212fc5f5e201a2f10467b1a6cc9f`
- Nullable Agent lookup fix: `da829fe` (`git rev-parse HEAD` remains authoritative after later documentation commits)
- Version: `1.3.0-alpha.5`; database schema: `6`
- The primary checkout, its `.secrets` directory, unrelated Java processes, and preservation stashes remain
  untouched. Nothing from this phase has been pushed, tagged, published, signed, installed, or released.

## Current gate

The browser-parity implementation and deterministic safety gates are complete, but the full live CurseForge
runtime milestone is **not ready for user acceptance**. Two different official Fabric server packs downloaded,
materialized, and reached Minecraft `Done`, then correctly failed closed because their task-owned Java processes
opened outbound HTTPS connections while staged validation was active. The written policy permits only the exact
loopback Minecraft TCP port during that phase. No pack was registered or promoted.

The current bounded unit is to determine whether that outbound traffic can be eliminated or explicitly modeled
without weakening isolation, then rerun official-pack, generated-candidate, mod/dependency, update, rollback, and
controlled-recovery workflows. Actual packaged UI and visual/keyboard acceptance require a later foreground
acceptance session.

## Browser checkpoint

- **VERIFIED root cause:** the old UI requested only 20 items and synchronously enriched every CurseForge card with
  exact file/server relationships before returning discovery. Static path analysis established a worst-case upper
  bound of about 1,201 provider reads for one 20-card page. Shared renderer state could briefly show another
  provider's rows; Modrinth omitted its requested offset; early filtering could underfill, repeat, or hide later
  CurseForge results.
- **VERIFIED correction:** both providers now use shallow 50-project pages with true provider cursors/totals. Exact
  release and server-path work is lazy. Provider sessions are separate, stale-fenced, cancellable, deduplicated,
  capped at 200 items, virtualized, and support near-end append plus an accessible **Load more** fallback.
- **VERIFIED packaged browser evidence from the earlier foreground checkpoint:** Modrinth rendered 50 of 12,631.
  CurseForge rendered 49, then 97, then 144 accepted unique results while the raw cursor advanced across three
  50-result pages. Returning to a loaded provider restored its rows in about 130-169 ms. Exact SkyFactory 5 detail
  identified an official server pack.
- **VERIFIED regression boundary:** no-N+1 discovery, three-page append, raw cursors, provider isolation, stale
  cancellation, scroll reset, optional-enrichment failure, detail continuity, bounded results, virtualization,
  and four-slot thumbnail work have deterministic coverage.
- **NOT REPEATED in the headless continuation:** actual packaged WebUI rendering, visual comparison, keyboard
  navigation, Windows scaling, High Contrast, Reduced Motion, and normal-close UI behavior.

## Headless live evidence

All continuation work used the exact packaged Agent/controller, named pipes, task-owned filesystem roots, Job
objects, loopback probes, and saved evidence. It did not open ChunkPilot, a browser, or any foreground window.

### Metadata and payloads

- More FPS project `531644`: client/server pairs `8021436`/`8021438` and `8608924`/`8608928` resolved as
  Minecraft 1.21.1 Fabric with Java 21. Client sizes were `7,225,930` and `7,238,878` bytes; server sizes were
  `41,069,614` and `62,131,991` bytes.
- Optimized Performance project `1172292`: client/server pairs `6110282`/`6110285` and `6120937`/`6120942`
  resolved as Minecraft 1.21.1 Fabric with Java 21. Client sizes were `679,356` and `680,080` bytes; server sizes
  were `13,983,967` and `14,183,771` bytes.
- The official-pack campaigns downloaded real client and server payloads and reached server readiness. Because
  validation then failed, seven payload reservations remain conservatively classified as unknown rather
  than being claimed as completed.
- Cumulative bounded ledger: `990,582,328` bytes guarded, `218,797,028` completed, seven unknown reservations
  totaling `771,785,300`, zero observed-incomplete bytes, and `1,156,901,320` bytes remaining under the 2 GiB cap.

### Runtime and cleanup

- More FPS and three Optimized Performance full/classification runs reached `Done`, then ended
  `FailedNothingChanged` after detecting an unexpected non-loopback endpoint.
- The final diagnostic classified the unexpected endpoints as outbound HTTPS: private local addresses connected
  to public IPv4 addresses on remote port 443. It was not a wildcard Minecraft listener.
- The policy was not weakened. Nothing was registered, promoted, installed into a real server, or exposed through
  firewall/router changes.
- Every exact Agent exited `0`; root identity validation passed; each Job contained zero processes after cleanup;
  exact run roots are absent. Reports do not claim recoverable Recycle Bin placement when Windows removed a root
  without returning a recoverable item.
- Final selected-port absence is deliberately uncertified in aborted campaigns because that postcondition was not
  reached. No task-owned ChunkPilot/Agent/Java process remained in the ownership checks.
- One initially selected port was already occupied by an unrelated process. It was left untouched and a clean
  port was selected.

## Defect found during headless verification

A live metadata lookup that truthfully returned JSON `null` exposed a transport-classification defect. Nullable
`JsonElement` cannot distinguish that value from an omitted payload, so both clients reported a broken Agent frame
instead of provider not-found. Commit `da829fe` permits null only for the exact `ResolveCatalogProject`/`CatalogItem`
and `PluginRelease`/`PluginRelease` contracts. Wrong types and all other missing payloads still fail closed. The
wire-level regression, all 65 focused certification tests, and all 1,653 unit tests pass. An exact-HEAD packaged
metadata rerun now reports truthful project/file not-found, authenticates after the DPAPI relaunch, exits both
Agents with code `0`, leaves zero Job processes and no payload reservation, and recycles the exact fresh run.

## Checks at the runtime checkpoint

- Release solution build: passed, zero warnings and zero errors.
- .NET unit tests: 1,653 passed; the focused certification suite has 65 passing tests including the new
  nullable-response regression.
- Integration tests excluding the foreground packaged-normal-close fixture: 403 passed.
- WebUI: 30 files and 189 tests passed; typecheck, lint, and Vite production build passed.
- npm audit: zero vulnerabilities. NuGet audit: no vulnerable packages.
- Public documentation: 51 tests passed. Publication audit: 69 reachable commits, 1,372 blobs, no unexpected
  Gitleaks findings and no prohibited paths. The two documented immutable false positives are a synthetic test
  token and a prior Git commit written next to the live-API checkpoint label; neither is credential material.
- The final self-contained development package is rebuilt after this document commit. Its schema-3 manifest must
  bind the current `git rev-parse HEAD`, a clean 430-file input inventory, matching HEAD/index/worktree hashes, and
  every packaged file's size and SHA-256 before the final packaged metadata probe is accepted.

## Security and provider-policy boundary

- The approved key authenticated through the native path and DPAPI-backed isolated storage. Its value was never
  printed, placed in arguments or React state, embedded in a package, or written to evidence.
- Native host allowlists, hash/archive checks, no arbitrary script execution, exact process ownership, Job cleanup,
  and the 2 GiB ledger remained enforced.
- `PUBLIC CREDENTIAL DELIVERY STILL GATED`: local-use authorization does not permit embedding or redistributing a
  shared credential. Written CurseForge clarification is still required for public credential delivery and the
  minimum installed-state persistence interpretation.
- The absent root license and Authenticode signing remain separate publication gates.

## Evidence still required

- A successful official server-pack create/start/status/stop with all network postconditions.
- A generated candidate success, or a complete evidence-backed unsupported result followed by another bounded
  candidate when practical.
- Real CurseForge mod plus required dependency install, runtime check, authoritative removal, and wrong-server
  fencing.
- Real adjacent pack update, recovery point, transactional rollback, sentinel preservation, and controlled failure.
- Actual packaged UI inspection at the required sizes/scaling modes plus High Contrast, Reduced Motion,
  keyboard-only navigation, and normal-close ownership smoke.
- Fresh-PC, Windows-version, antivirus, installed-upgrade, signing, and final user acceptance.

## Exact next gate

Keep the current strict network rule until the source and necessity of the staged server's outbound HTTPS are
understood. Then rebuild an exact committed package and resume the bounded live campaign in an isolated root. Do
not proceed to another game. When all live workflows and manual UI checks pass, the next product gate is:

`CurseForge/modpack user acceptance, then daily Minecraft management completeness and the equal-High friction register`.
