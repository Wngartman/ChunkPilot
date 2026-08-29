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

CurseForge dependency plans carry a native authorization ID and SHA-256 digest in addition to their reviewable
entries. The App binds that pair to the exact selected server, provider, project and file. Provider/project/file
replacement, server switching, explicit renderer invalidation, cancellation, stale completion, or failed native
dispatch clears the App evidence and revokes the exact Agent authorization. Install consumes the App evidence
before sending and the Agent consumes its independently stored plan before creating operation state; a wrong
server, altered digest, replaced review, expired review or replay cannot install. Late cleanup is identity-scoped
and cannot revoke a newer review.

The install begin response is not proof of whether the Agent accepted the request. The App supplies an operation
ID before dispatch and, after an ambiguous pipe failure, calls `FindManagedContentOperation` for that exact ID.
A found snapshot must match operation, server, kind, provider, project, and release before observation continues.
Only an explicit miss allows replay of the unchanged request. Agent-side acceptance is serialized, consumes the
plan authorization at most once, returns the existing snapshot for an exact replay, and rejects the same ID with
different request fields. Cancellation sends the complete request to the same Agent acceptance gate: accepted
work is cancelled, while cancellation that wins first reserves a terminal tombstone and revokes unconsumed plan
authority so delayed Begin cannot start. An empty client operation ID is rejected before either path.

Modpack methods expose bounded official Modrinth search/project-link results, exact `.mrpack`
releases, resized provider images, and a native local-pack picker. Remote creation can use only an
exact release retained in the native catalog cache. Local selection returns an opaque, expiring,
single-use token plus sanitized inspection; the Agent re-inspects the archive and verifies its native
size/SHA-512 identity before materialization. `creation.begin` returns the client-owned durable
operation ID and `creation.progress` is the reattach path after renderer or response loss. React never
receives a local archive path or writes pack files.

CurseForge remote creation has an additional native continuity boundary. A ready preflight is retained under its
exact review method, project, client file and Agent operation ID. The later `creation.begin` must consume that
current App evidence and the Agent's matching short-lived authorization. An abandoned review, failed preflight
response delivery, or a begin explicitly proven unaccepted uses an exact revocation call; a pre-armed revocation
lease lets cancellation invalidate a slow preflight before its Ready result is registered. The App reduces
generated-plan output to the sanitized exact-release review and never accepts plan data back as authority:
creation receives the Agent registry's deep-copied plan once.

`creation.begin` is also acknowledgement-loss safe. The App asks `FindInstallProgress` for the same
client-generated operation ID after an ambiguous I/O, timeout, cancellation, or response-decode failure. It
reattaches when found and replays only after an explicit miss. The Agent stores the complete client-controlled
plan identity under that ID before work runs, makes an exact replay idempotent without consuming authorization a
second time, and rejects any changed request. If cancellation wins after acceptance, it targets the accepted
operation; cancellation is not interpreted as evidence that no operation exists. Cancellation before acceptance
uses the complete original plan to establish the same-gate terminal tombstone and does not replay work onto a
changed selection. Empty externally controlled creation operation IDs are rejected instead of replaced.

Creation and managed-content coordinators retain at most 4,096 pre-start tombstones each. Reaching that bound
seals previously unseen Begin requests for the rest of that Agent lifetime under the same acceptance gate. A
known identity still replays only its exact stored request; a new Begin is rejected before consuming authorization,
while cancellation can revoke its unconsumed authorization and return a synthetic terminal-cancelled snapshot
without retaining another operation object. The App still waits indefinitely when the Agent itself remains
permanently unavailable after ambiguous dispatch: without an Agent acknowledgement it cannot truthfully turn a
pending cancellation into terminal fence evidence.

Version/update requests remain selection-shaped rather than download-authoritative. For CurseForge, the Agent
uses linked numeric identity to run operation-time preflight and canonicalizes URL, size, hashes, filename,
package type and platform before cache or download work. The generated-plan trust property is ignored by JSON
serialization and deserialization, so it cannot cross the WebUI/App/Agent wire in either direction.
Storage forecasting is likewise Agent-owned: renderer-declared sizes cannot approve a download. Exact preflight
first establishes the client/server artifact size and generated-plan total, then the Agent aggregates snapshot,
cache, candidate, and generated-payload requirements by actual storage volume before a recovery snapshot,
package transfer, or active-server mutation. The current-server snapshot term comes from the same bounded,
cancellation-aware, no-follow full-tree inventory used by rollback snapshot and migration. It rejects an unsafe,
inaccessible, redirected, case-colliding, or over-500,000-entry tree before output and revalidates inventoried file
metadata and ancestor safety before consumption; snapshot also bounds and revalidates each streamed entry.

Connectivity projects the selected server's existing `NetworkConfiguration`, router mapping, exact
Windows Firewall state, outside-in verification, and exact last-checked endpoint as one read-only
presentation snapshot. Ordinary mode changes accept only Home/LAN or Internet; internal private and
migration modes remain representable. Copy commands name local, LAN, current router-reported,
last-checked, or independently verified Internet evidence. Only the independently verified form may
be labelled confirmed. Router and firewall mutation still require deliberate confirmation and remain
owned by the native App/Agent path.
Ordinary workspace loads and connectivity actions publish the resulting snapshot without redundantly
running the full Dashboard refresh first.

Every `ServerSummary` carries its game kind separately from its detected Minecraft ecosystem. The
central mapper exposes the Players capability for every Minecraft server, including Custom/Unknown
imports, and excludes Terraria. `playerAccess` projects only the selected server's authoritative
running state, whitelist state, moderation capabilities, authoritative player UUIDs, and last access error. `players.addAllowlist`
and `players.moderate` reuse the existing ViewModel/Agent moderation commands; React never edits access
files or sends an unvalidated raw console command. `players.head` accepts only a UUID already present in
the selected server snapshot. Native code retrieves the signed Mojang profile and
`textures.minecraft.net` skin, applies the face/hat layers, and returns a bounded data URL; React never
fetches an arbitrary player image URL. `serverSettings.serverId` and `issues[].serverId` make selected
server ownership explicit across asynchronous renderer updates.

`help.openExternal` accepts HTTPS URLs from the documented Minecraft, loader, provider, Microsoft, and
Oracle source-host allowlist. Help content and search are bundled locally; no search term or server state
leaves the PC.

`appearance.chooseIcon` opens the native file picker and returns a bounded PNG data URL plus a display filename, never a filesystem path. The browser produces a validated 64 x 64 PNG crop and sends it as an optional part of `settings.saveServer`; C# stages it under the isolated ChunkPilot data root and the Agent remains the sole owner of final `server-icon.png` installation. MOTD changes use the existing server-property save path. Version install, rollback, verify, and cancel requests delegate to the existing authoritative commands and preserve their native safety confirmations.

The C# records in `src/ChunkPilot.App/WebUi/WebUiContracts.cs` are the envelope source. TypeScript mirrors the deliberately small envelope and view model; round-trip and allowlist tests guard drift. This avoids a large generator dependency while the contract remains compact.
