# Native UI architecture

`ChunkPilot.App` remains a WPF presentation layer. `App` owns startup, mutex, tray, reconnect, crash handling, and safe exit. The shell owns navigation presentation and responsive layout. Pages bind to ViewModels and existing agent commands; they do not perform provider networking, persistence, process control, or filesystem mutation.

Semantic navigation is represented by stable destination IDs. Existing numeric tab settings are read only for migration and are converted to IDs. Server lifecycle state and intent remain distinct. The Agent remains authoritative when the UI is closed or disconnected.

Shared resources are layered as colors, design tokens, typography, controls, templates, and icon mappings. A page may compose a component but may not create a second design system. Long operations expose IDs, progress, cancellation/checkpoints where supported, and truthful recovery outcomes.

The presentation decomposition is incremental and behavior-preserving: first establish resources and components, then shell/navigation, then destination views. Existing command names and safety paths are retained unless a focused adapter is required.
