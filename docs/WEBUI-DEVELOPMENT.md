# WebUI development

Prerequisites are Node 24 or later for development/build, .NET 10 SDK, and Microsoft Edge WebView2 Evergreen Runtime for preview. Production does not require Node.

```powershell
Set-Location 'D:\ChunkPilot\src\ChunkPilot.WebUi'
npm ci
npm run typecheck
npm run lint
npm test
npm run build
```

If npm reification is affected by the documented Windows package-tree issue, the local integrity-checked fallback is:

```powershell
Set-Location 'D:\ChunkPilot'
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\restore-webui-dependencies.ps1
```

The fallback restores exact lockfile tarballs under ignored `temp`; it does not install globally. Vite development serving is opt-in only and binds to `127.0.0.1`. Release uses built static assets. Fixture data is activated only outside the native bridge through an explicit fixture query and is never selected by normal preview startup. High-value appearance fixtures include `menu`, `appearance`, `icon-editor`, `motd-formatted`, and `motd-raw`; `profile=1` adds fixture-only navigation and long-task marks to the root element for deterministic performance review.

The version fixture contains stable, snapshot, pre-release, release-candidate, Beta, Alpha,
Experimental, and Unavailable examples. Production inventory is never supplied by fixtures: both
Create Server and the existing-server Versions page request the native `creation.catalog` contract.

Launch a built preview with `ChunkPilot.exe --webui-preview`. Normal startup continues to use the accepted WPF UI.

For a repeatable packaged idle sample that never touches normal application data:

```powershell
Set-Location 'D:\ChunkPilot'
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\measure-webui-preview.ps1 -SampleSeconds 10
```

The measurement launches the portable preview with a unique temporary data root, reports the native
host, Agent, and WebView2 child processes separately, closes through the normal native window path,
and removes its temporary data.
