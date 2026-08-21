# Current ChunkPilot Gate

> Git and directly inspected runtime evidence override this file. This checkpoint may describe a candidate,
> but it may not declare visual, hardware, installer, privilege, networking, or real-machine acceptance on
> the user's behalf.

## Repository State

- Worktree: isolated release-candidate checkout
- Branch: `codex/world-import-ui-hardening`
- Verified implementation checkpoints: `115491ce305af32b7fe809983506b44f3ac0d7c0` plus the Alpha 5 release-preparation commit reported at handoff
- Base: public Alpha 4, `3bd0131fac16625ff006191c4493cfcfb53153f1`
- Version: `1.3.0-alpha.5`; schema: `6` (unchanged; no migration)
- Preservation stash: untouched
- Publication: Alpha 5 candidate; push, tag, workflow, and public asset state are reported only after live verification
- This checkpoint is committed as a metadata/tooling descendant. The final live HEAD is reported at
  handoff because a commit cannot contain its own hash.

## Current Gate

The bounded server-state, player-identity, networking-clarity, server-health, and offline-help candidate is
implemented and passes the local automated, package, isolated-runtime, and visual gates. Broader 1.4 work
remains blocked on manual user acceptance and the external checks listed below.

## Verified Outcome

- Server settings snapshots carry immutable server identity; stale native loads, late responses, dirty MOTD
  drafts, and saves cannot cross a server-selection boundary.
- Visible access-list language is consistently **Whitelist** while internal allowlist contracts remain stable.
- Ordinary Internet setup is a three-step owned-state flow. Outside-in testing is an explicit Advanced,
  point-in-time diagnostic and does not replace router/firewall ownership evidence.
- Process, borderless window, Start-menu shortcut, desktop shortcut, embedded executable icon, and published
  ICO use one `ChunkPilot.Desktop` identity. The published icon hash matches the nine-frame source ICO.
- Player UUIDs survive the snapshot boundary. Official Mojang skin textures are host-allowlisted, bounded,
  cached, cropped locally, and replaced by local initials when unavailable.
- At most two server-health issues appear, only from authoritative crash/unresponsive or exact-owned network
  failure evidence. Dismissals are fingerprinted and reversible from Help.
- Settings includes 28 offline articles across 10 categories, exact-signature/alias search, stable deep links,
  safe steps, stop conditions, related help, and user-invoked allowlisted primary sources.
- Long server names cannot shrink their sidebar status indicator, and a server selection cannot render the
  previous server's player or whitelist state while the new authoritative snapshot is loading.
- Existing world folders and ZIPs are bounded, reviewed, revalidated, copied through managed transactional
  staging, and never modified. Paper-style Nether and End siblings are carried with the main world.
- Installed modpack identity, runtime requirements, update state, provider evidence, ownership boundaries,
  recovery, and inventory destinations are visually distinct.

## Final-Source Checks

- Frontend: **passed** — 25 files, 143 tests; typecheck, lint, and Vite production build; 207 modules.
- Native unit: **passed** — 1,337/1,337, zero skipped.
- Native integration: **passed** — 349/349, zero skipped, sequential isolated fixtures.
- Release build: **passed** — zero warnings and zero errors.
- Feature development package: **passed** — targeted native/package contracts 38/38.
- Self-contained win-x64: **passed** — App, Agent, firewall helper, WebUI, and `Assets\ChunkPilot.ico` present.
- Installer compile: **passed** — Inno Setup 7.0.2; local unsigned validation artifact only; not installed.
- `git diff --check`: **passed** — only expected LF-to-CRLF working-copy notices.
- Migration: **skipped** — schema is unchanged.

## Runtime and Visual Evidence

- Normal packaged launch used isolated `CHUNKPILOT_DATA_ROOT` and
  `CHUNKPILOT_MANAGED_SERVERS_ROOT`; the window appeared in 1,672 ms.
- The visible packaged app loaded Help, matched `UnsupportedClassVersionError` to the Java runtime article,
  and closed normally with no candidate App, Agent, or helper process left behind.
- Native fixture captures passed for health, players, owned connectivity, and Help at 125% scaling.
- Production WebUI inspection passed at 1440x1000 and 430x932 with no document-level horizontal overflow.
  Player rows, health actions, and Help search/deep links remained usable at the compact width.

## Initial Failures Closed by This Pass

- Initial focused WebUI run: 5 failures / 13 passes (settings isolation, old four-step connectivity, and three
  visible access-list labels).
- Initial package contract: missing explicit packaged `WebUiWindow` icon.
- Expanded MOTD regression: unavailable-to-authoritative transition exposed a React hook-order violation.
- Artifact inspection: MSBuild item metadata did not copy `Assets\ChunkPilot.ico`; an explicit publish target
  now does so and the output hash is verified.
- Clean prerequisite acquisition: both scripts resolved `$PSScriptRoot` too early and Inno help exit 1 was
  treated as failure; default invocations now pass.
- One MOTD assertion selected the visual editor while checking raw text, one build command used the wrong
  WebUI path casing, and one spaced fixture argument was initially unquoted; these were harness/invocation
  errors, not retained product defects.

## Unknown or User Acceptance Required

- Fresh installation and taskbar/shortcut identity on the other Windows PC from the original report.
- Real router, Windows Firewall elevation, public address, CGNAT/double-NAT, and outside-in behavior.
- Live Mojang profile/texture availability and cache behavior under real player traffic.
- Real server switching with user-authored MOTD data and real crash/network evidence.
- Relative startup performance against a controlled pre-change baseline; only the candidate measurement is
  available.

## User Acceptance

```powershell
Start-Process -FilePath '.\artifacts\self-contained-win-x64\ChunkPilot.exe'
```

Confirm server A/B/C MOTD isolation, Whitelist copy, automatic owned connectivity state, taskbar identity,
player-head fallbacks, evidence-backed issue cards, Help exact-error search, and normal close behavior.

## Next Gate

Resume broader 1.4 Connectivity and maps work only after this candidate passes user acceptance. Do not claim
fresh-machine install, external-network, live-skin, signed, pushed, tagged, or released status from this gate.
