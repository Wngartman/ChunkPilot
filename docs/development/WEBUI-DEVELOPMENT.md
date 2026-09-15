# WebUI development

Prerequisites are Node 24 or later for development/build, .NET 10 SDK, and Microsoft Edge WebView2 Evergreen Runtime. Production does not require Node.

```powershell
Set-Location '.\src\ChunkPilot.WebUi'
npm ci
npm run typecheck
npm run lint
npm test
npm run build
```

If npm reification is affected by the documented Windows package-tree issue, the local integrity-checked fallback is:

```powershell
Set-Location '<repository root>'
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\restore-webui-dependencies.ps1
```

The fallback restores exact lockfile tarballs under ignored `temp`; it does not install globally. Vite development serving is opt-in only and binds to `127.0.0.1`. Release uses built static assets. Fixture data is activated only through explicit development-only fixture arguments and is never selected by normal startup. High-value appearance fixtures include `menu`, `appearance`, `icon-editor`, `motd-formatted`, and `motd-raw`; `profile=1` adds fixture-only navigation and long-task marks to the root element for deterministic performance review.

The version fixture contains stable, snapshot, pre-release, release-candidate, Beta, Alpha,
Experimental, and Unavailable examples. Production inventory is never supplied by fixtures: both
Create Server and the existing-server Versions page request the native `creation.catalog` contract.

Normal `ChunkPilot.exe` startup opens the WebUI. The retained WPF layer is only the native host and recovery surface.

Server icon edits use a native, server-bound selection token and a bounded 256-pixel original.
The renderer stages the crop and adjustment recipe; the Agent checks the exact previously opened
icon under the shared file lock before replacing it. Cancelling or discarding writes nothing.
After a successful save, one local `ServerIcons/Edits/<server-id>.json` record retains that original
and recipe, so reopening an edit does not compound effects. Older or externally changed icons
fall back to their existing 64-pixel image with an explicit resolution warning. These records are
local application data, not server-pack files or release assets.

Ordinary startup displays only native process/readiness evidence through `startupProgress`.
The WebUI does not simulate percentages or infer readiness from an open port. Likewise, stopped
player-list editing is enabled only by the native `canManageWhileStopped` capability; kicking
always requires the actual running state. Long bridge commands have bounded per-command waits,
and a timeout never means success or triggers an automatic retry.

Large player lists use 50-row pages with whole-roster search, bounding rendered controls and
native head-image requests without dropping known players. Development fixtures `large-roster`
and `large-library` create 1,000 players and 128 servers only when explicitly selected. The
performance tests report synthetic store/jsdom timings separately from packaged/native timing;
their hard assertions cover row/request bounds, search reachability, and exact server identity.

For a repeatable packaged idle sample that never touches normal application data:

```powershell
Set-Location '<repository root>'
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\measure-webui.ps1 -SampleSeconds 10
```

The measurement launches packaged ChunkPilot with a unique temporary data root, reports the native
host, Agent, and WebView2 child processes separately, closes through the normal native window path,
and removes its temporary data.
