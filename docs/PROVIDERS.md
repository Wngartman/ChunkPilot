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
| CurseForge | Catalog when configured | Hidden until the user supplies an API key. The key is encrypted for the current Windows user with DPAPI. Server-pack file metadata is resolved to its official HTTPS URL and SHA-1; no key is bundled. |
| Geyser/Floodgate | Java/Bedrock crossplay packages | Official downloads v2 metadata, platform-specific package, and required SHA-256. |
| ViaVersion | Optional cross-version plugin | Official Modrinth project release and SHA-512. |
| FTB | Server packs | Unavailable until a documented supported public server-pack API is configured. No scraping fallback. |
| Direct HTTPS | Advanced package | User-supplied. Provider-originated selections carry their official hash into installation. |
| Local ZIP/JAR/folder | Advanced/import path | No network request; safe staging and archive traversal checks apply. |

Catalog metadata is cached for six hours by provider and exact query. A provider outage may use unexpired cache. Searches are bounded, client-only entries are excluded by default, and no continuous polling occurs.

Official references: [Fabric server install](https://fabricmc.net/use/server/), [Quilt server install](https://quiltmc.org/en/install/server/), [NeoForge server install](https://docs.neoforged.net/user/docs/server/), [Modrinth search API](https://docs.modrinth.com/api/operations/searchprojects/), [CurseForge API](https://docs.curseforge.com/rest-api/).

CurseForge catalog/update adapters require a user-supplied key encrypted by the existing DPAPI
secret store. The WebUI modpack creation flow does not expose that incomplete path and never scrapes
CurseForge pages. See [MODPACKS.md](MODPACKS.md).
