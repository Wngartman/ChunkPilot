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
| CurseForge | Modpack/mod discovery, exact files, official server packs, generated candidates, and whole-pack updates | Native official REST/CDN adapter with exact IDs, hashes, distribution checks, bounded responses, cancellation, and typed errors. Development access uses the one approved local key file imported into DPAPI; no key is bundled and users are never asked for one. Public credential delivery remains gated. |
| Geyser/Floodgate | Java/Bedrock crossplay packages | Official downloads v2 metadata, platform-specific package, and required SHA-256. |
| ViaVersion | Optional cross-version plugin | Official Modrinth project release and SHA-512. |
| FTB | Server packs | Unavailable until a documented supported public server-pack API is configured. No scraping fallback. |
| Direct HTTPS | Advanced package | User-supplied. Provider-originated selections carry their official hash into installation. |
| Local ZIP/JAR/folder | Advanced/import path | No network request; safe staging and archive traversal checks apply. |

Catalog metadata is normally cached for six hours by provider and exact query. CurseForge is the deliberate
exception: the currently reviewed third-party terms do not authorize persistent API-response caching, so
ChunkPilot coalesces only identical in-flight requests and reports that no offline CurseForge cache exists.
Searches are bounded, client-only entries are excluded by default, and no continuous polling occurs.

Official references: [Fabric server install](https://fabricmc.net/use/server/), [Quilt server install](https://quiltmc.org/en/install/server/), [NeoForge server install](https://docs.neoforged.net/user/docs/server/), [Modrinth search API](https://docs.modrinth.com/api/operations/searchprojects/), [CurseForge API](https://docs.curseforge.com/rest-api/).

CurseForge provider contracts are developer-gated. A local developer provisions the repository-local
`.secrets\curseforge-api-key.txt`, or places its absolute path in
`CHUNKPILOT_CURSEFORGE_KEY_FILE`; the variable contains a path, never the key. The Agent imports the value
into its isolated DPAPI secret store and only native HTTP code can consume it. Public packages remain unable
to activate CurseForge until written direct-desktop credential delivery permission is established. See
[MODPACKS.md](MODPACKS.md) and the [credential decision](../architecture/CURSEFORGE-CREDENTIAL-DELIVERY.md).
