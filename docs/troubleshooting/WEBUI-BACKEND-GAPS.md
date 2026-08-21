# WebUI backend and presentation gaps

These are confirmed gaps, not simulated features:

- The official Mojang inventory now supplies a complete historical browser and installed-version evidence. The hardened update coordinator still accepts only its existing linked update-source target; it does not yet accept an arbitrary typed Mojang catalog target with the required world-compatibility and downgrade policy. WebUI therefore does not turn catalog rows into unsafe install buttons.
- The authorized isolated Vanilla campaign is complete and its identity-bound evidence is embedded. Seven exact historical development builds remain unavailable because they failed bootstrap, readiness, or clean stop; 67 entries have no official Mojang server artifact. ChunkPilot does not substitute unofficial binaries.
- Modrinth `.mrpack` creation is authoritative, but CurseForge pack creation/import is not wired into
  WebUI. It requires the existing user-owned API-key boundary plus complete server-pack or generated
  pack materialization. No placeholder button is shown.
- Modrinth exposes all-time/follows/newest/updated ordering. ChunkPilot has not accumulated local
  7/30/365-day popularity snapshots, so period trends are explicitly unavailable rather than inferred.
- A locally selected `.mrpack` is integrity-bound and installable, but cannot receive provider
  updates until the user separately proves its provider project/release identity.
- Pack creation validates archive identity, file hashes, environment, loader installation and staged
  layout. It does not start arbitrary public packs during creation; first-start runtime health remains
  authoritative and is not pre-claimed.
- Plugin configuration remains file/folder based. The backend does not expose a safe universal schema for arbitrary third-party YAML or JSON, so WebUI does not fabricate graphical plugin forms.
- The authoritative server-settings surface does not yet expose a safe server-root relocation operation or a per-server Java-runtime assignment workflow suitable for WebUI. Those controls remain absent rather than writing paths from React.
- Diagnostics are visible through authoritative warnings and operation failures, but WebUI does not yet expose the complete native diagnostic-bundle and targeted recovery-action surface.

The WebUI file editor intentionally limits bridge-loaded text to 160 KiB even though the native editor accepts larger bounded files. Larger text files remain available through the prominent native Explorer action. This keeps WebView messages well inside the 256 KiB inbound limit and avoids moving bulk file contents through the presentation bridge.

Server icons are now projected as bounded PNG data URLs with cache invalidation; no path is exposed. Native file selection and the existing Agent-owned install operation remain authoritative.

None of these gaps changes Agent/Core/Infrastructure behavior or weakens data safety. They prevent claiming complete future-platform parity or default-readiness.
