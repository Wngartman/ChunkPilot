# Native host and design-system architecture

`ChunkPilot.App` remains the WPF host. `App` owns startup, mutex, tray, reconnect, crash handling, and safe exit. The shipped interface is the locally bundled React app in one persistent WebView2 control; see [WebUI architecture](WEBUI-ARCHITECTURE.md). Native dialogs and development-only previews reuse the WPF design system. The former WPF product shell is not shipped.

Semantic navigation is represented by stable destination IDs. Existing numeric tab settings are read only for migration and are converted to IDs. Server lifecycle state and intent remain distinct. The Agent remains authoritative when the UI is closed or disconnected.

Shared resources are layered as colors, design tokens, typography, controls, templates, and icon mappings. A page may compose a component but may not create a second design system. Long operations expose IDs, progress, cancellation/checkpoints where supported, and truthful recovery outcomes.

React uses the typed, allowlisted native bridge; native dialogs use existing ViewModel commands. Neither presentation surface performs provider networking, persistence, process control, or filesystem mutation directly. Existing command names and safety paths remain authoritative.
