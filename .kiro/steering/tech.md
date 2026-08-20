---
inclusion: always
---
# Technical UI rules
Keep WPF UI concerns in `ChunkPilot.App`. Bind to existing ViewModels and Agent contracts; do not move provider, persistence, process, filesystem, or lifecycle ownership into views. Use async commands, nullable-safe C#, bounded collections, semantic navigation IDs, centralized resources, and no local TCP API. Validate XAML with the Release build and preserve named-pipe/reconnect/WM_CLOSE behavior.
