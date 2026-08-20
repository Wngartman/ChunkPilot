# WebUI bridge contract

Protocol version 1 uses WebView2 `postMessage` only. Inbound messages are limited to 256 KiB, accepted only from `https://chunkpilot.local`, parsed into an explicit envelope, and dispatched through a method allowlist.

```json
{"protocolVersion":1,"id":"web-42","method":"servers.start","params":{"serverId":"..."}}
```

Responses preserve the request ID and contain either `result` or a structured error with one of: `validation`, `unavailable`, `conflict`, `cancelled`, `timeout`, `backend_disconnected`, `protocol_mismatch`, or `internal`. Events carry an event name, monotonic revision, and payload. Renderer initialization is `renderer.ready` followed by `snapshot.get`; reload repeats both, so renderer-local state never becomes authoritative.

Method groups are window control, snapshot/selection, lifecycle, console, workspace loading, bounded files, backups, players, schedules, settings, connectivity, versions, server appearance, platform creation, and plugin/mod management. File reads and writes stay path-confined inside the Agent and preserve encoding, BOM, line endings, last-write identity, and content hash. Add-on config saves additionally carry the current inventory relative path and are accepted only when the Agent proves the exact plugin/mod identity owns the requested config path; running-server saves use the Agent's safe-restart/rollback operation. Command acceptance means only that the authoritative command path accepted the request. Lifecycle and progress remain snapshot/event driven.

Creation catalog methods expose Mojang, PaperMC, Fabric, NeoForge, Forge, Quilt and legacy-loader metadata as native-generated
typed presentation records. JavaScript supplies only selected IDs; the native creation gateway
re-resolves official artifact identity and integrity. Plugin/mod methods cover bounded inventory,
opaque native file selection, provider search/release/plan, staged install, enable/disable, removal,
config discovery, and config save. No native absolute source path crosses into React.

Modpack methods expose bounded official Modrinth search/project-link results, exact `.mrpack`
releases, resized provider images, and a native local-pack picker. Remote creation can use only an
exact release retained in the native catalog cache. Local selection returns an opaque, expiring,
single-use token plus sanitized inspection; the Agent re-inspects the archive and verifies its native
size/SHA-512 identity before materialization. `creation.begin` returns the client-owned durable
operation ID and `creation.progress` is the reattach path after renderer or response loss. React never
receives a local archive path or writes pack files.

Connectivity projects the selected server's existing `NetworkConfiguration`, router mapping, exact
Windows Firewall state, and outside-in verification as one read-only presentation snapshot. Allowed
commands select Local/Home/Internet/Configure-later mode, run the existing router/firewall/verification
actions, cancel them, stop sharing, and copy one explicitly named local, LAN, or verified-public
address. Public copy is rejected unless outside-in evidence supplies the exact endpoint. Router and
firewall mutation still require deliberate confirmation and remain owned by the native App/Agent path.
Ordinary workspace loads and connectivity actions publish the resulting snapshot without redundantly
running the full Dashboard refresh first.

`appearance.chooseIcon` opens the native file picker and returns a bounded PNG data URL plus a display filename, never a filesystem path. The browser produces a validated 64 x 64 PNG crop and sends it as an optional part of `settings.saveServer`; C# stages it under the isolated ChunkPilot data root and the Agent remains the sole owner of final `server-icon.png` installation. MOTD changes use the existing server-property save path. Version install, rollback, verify, and cancel requests delegate to the existing authoritative commands and preserve their native safety confirmations.

The C# records in `src/ChunkPilot.App/WebUi/WebUiContracts.cs` are the envelope source. TypeScript mirrors the deliberately small envelope and view model; round-trip and allowlist tests guard drift. This avoids a large generator dependency while the contract remains compact.
