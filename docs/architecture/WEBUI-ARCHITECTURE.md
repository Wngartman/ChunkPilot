# ChunkPilot WebUI architecture

ChunkPilot WebUI is the only shipped product interface, not a second backend. Normal startup uses one native WPF host window, one persistent Microsoft WebView2 control, and one locally bundled React application. The former WPF product shell is excluded from active source and packaging.

## Authority boundary

The C# application still creates and authenticates the UI session and connects to the Agent through the existing named pipe. Agent, Core, and Infrastructure remain authoritative for lifecycle, process ownership, Java, files, backups, recovery, networking, persistence, and creation. React receives bounded snapshots and sends allowlisted requests through WebView2 web messaging. It receives no host object, filesystem handle, pipe client, database connection, shell surface, or unrestricted path access.

## Production content

Vite emits hashed files into `src/ChunkPilot.WebUi/dist`. Build and publish copy them into `WebUi` beside `ChunkPilot.exe`. WebView2 maps that directory to `https://chunkpilot.local/`; no HTTP listener, browser, Node runtime, remote font, CDN, or Internet connection is required at runtime.

## Native host and state flow

`WebUiWindow` owns the WebView2 profile, navigation policy, browser restrictions, renderer recovery, native window commands, heartbeat cadence, bridge lifetime, and safe exit. `MainViewModel` remains the application presentation seam. `WebUiSnapshotMapper` converts confirmed state to protocol v1. The host publishes a bounded snapshot after the existing one-second authoritative refresh; it adds no backend poller. React rejects stale revisions, lazy-loads major destinations, prefetches the principal route chunks after initialization, virtualizes console output, and performs no hidden chart work. Navigation changes renderer-local route state immediately and then sends any required authoritative selection command, so a bridge round trip cannot delay click feedback or shell rendering.

Missing runtime, missing assets, initialization failure, and unrecoverable renderer exit use a native fallback. A reload performs a new handshake and full snapshot. Renderer failure does not change Agent ownership or server lifecycle.
