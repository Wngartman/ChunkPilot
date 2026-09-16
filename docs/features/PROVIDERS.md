# Provider behavior

ChunkPilot uses documented APIs and never scrapes provider websites.

| Provider | Purpose | Availability and trust |
|---|---|---|
| Mojang | Vanilla release metadata and server JAR | Official manifest; supplied SHA-1 is verified. |
| Paper | Paper versions/builds | Official downloads API. |
| Purpur | Purpur versions/builds | Official API. |
| Fabric | Loader metadata and server launcher | Official Fabric metadata endpoint and server launcher. |
| Quilt | Loader metadata and installer | Official Quilt metadata and Maven repository; published SHA-1 is verified. |
| Forge | Installer metadata | Official Forge Maven metadata and checksum sidecar. |
| NeoForge | Installer metadata | Official NeoForge Maven metadata and checksum sidecar. |
| Modrinth | Mods, plugins, and server-capable `.mrpack` projects/releases | Official v2 API and CDN. Exact `.mrpack` size, SHA-1 and SHA-512 are retained; project links resolve through the API, never scraping. |
| CurseForge | Modpack/mod discovery, exact files, official server packs, generated candidates, and whole-pack updates | Native official REST/CDN adapter with exact IDs, hashes, distribution checks, bounded responses, cancellation, and typed errors. Optional native setup accepts the user's own approved access and validates before storing with DPAPI CurrentUser protection. No shared key is bundled. |
| Geyser/Floodgate | Java/Bedrock crossplay packages | Official downloads v2 metadata, platform-specific package, and required SHA-256. |
| ViaVersion | Optional cross-version plugin | Official Modrinth project release and SHA-512. |
| FTB | Server packs | Unavailable until a documented supported public server-pack API is configured. No scraping fallback. |
| Direct HTTPS | Advanced package | User-supplied. Provider-originated selections carry their official hash into installation. |
| Local ZIP/JAR/folder | Advanced/import path | No network request; safe staging and archive traversal checks apply. |

Catalog metadata is normally cached for six hours by provider and exact query. CurseForge is the deliberate
exception: the currently reviewed third-party terms do not authorize persistent API-response caching, so
ChunkPilot coalesces only identical in-flight requests and reports that no offline CurseForge cache exists.
Searches are bounded, client-only entries are excluded by default, and no continuous polling occurs.

CurseForge browse results are presentation data, not reusable install authority. Exact remote creation and
mod/dependency review each establish a bounded, short-lived Agent authorization. Creation authorization is bound to
the project/client/server-file tuple, approved artifact integrity, platform/Java identity and any generated
dependency graph. Managed-content authorization is additionally bound to one server and the SHA-256 digest of
the complete ordered plan. Both are one-use, expire, reject replay or retargeting, and are revoked when their App
selection is cancelled or replaced. Materialization consumes the Agent's deep-copied plan without a second
provider metadata resolution.

One-use authorization is not coupled to a fallible acknowledgement frame. Creation and managed-content begin
carry client-generated operation IDs. After an ambiguous local transport failure, the App performs an exact-ID
Agent lookup and reattaches to matching accepted work; only an explicit miss permits replay of the same request.
Agent begin handlers serialize authorization consumption and operation registration, make identical replays
idempotent, and reject a reused ID with changed provider, release, server, destination, or behavior fields.

CurseForge whole-pack updates use operation-time provider preflight rather than a renderer-held plan capability.
Before local cache or snapshot work, the Agent validates exact linked numeric identity and replaces URL, size,
hashes, filename, package type and platform fields with current approved preflight evidence. The trusted
generated-plan property cannot be serialized from or to the App. Other providers keep their existing native
identity and integrity contracts.

The same preflight supplies storage authority. Before downloading the client manifest, ChunkPilot reserves its
exact provider size plus a margin. Before an update snapshot or package transfer, it aggregates snapshot, cache,
candidate-expansion, and sealed generated-dependency bytes by their actual storage volume. A renderer-supplied
small size cannot bypass this forecast, and arithmetic saturates rather than wrapping.

Official references: [Fabric server install](https://fabricmc.net/use/server/), [Quilt server install](https://quiltmc.org/en/install/server/), [NeoForge server install](https://docs.neoforged.net/user/docs/server/), [Modrinth search API](https://docs.modrinth.com/api/operations/searchprojects/), [CurseForge API](https://docs.curseforge.com/rest-api/).

CurseForge setup is available under Settings > General and in unavailable-provider discovery. The user's
own approved key is entered in a native password field, protected before App-to-Agent transport, validated
against the official API, and stored only after a successful response and a fresh UI-session check. Cancel
or validation failure preserves existing access. Setup also supports replacement and removal; React receives
only configured/cancelled state. Public packages contain no developer key. Service-enabled builds instead
use the separately deployed application backend described below; no credential is delivered to the client.

A local developer can explicitly run `scripts/start-curseforge-dev.ps1` or set an absolute source path in
`CHUNKPILOT_CURSEFORGE_KEY_FILE`; the variable contains a path, never the key. Ordinary startup does not
search for a developer file or restore a removed personal key. The Agent imports an explicitly supplied value
into its isolated DPAPI secret store and only native HTTP code can consume it. Distribution of a shared
developer application key is never part of the desktop payload. A service-enabled build routes access to
the separately deployed ChunkPilot application backend and needs no local key; direct personal setup is a
fallback for unconfigured builds. No service endpoint is activated until deployment acceptance. See
[MODPACKS.md](MODPACKS.md) and the [credential decision](../architecture/CURSEFORGE-CREDENTIAL-DELIVERY.md).
