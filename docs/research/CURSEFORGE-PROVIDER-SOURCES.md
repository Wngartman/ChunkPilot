# CurseForge provider source record

Research date: 2026-09-14

Only official CurseForge/Overwolf sources define the provider. ChunkPilot does not scrape CurseForge pages,
infer unlisted files, synthesize CDN URLs, or treat a client manifest as proof of a dedicated-server package.

## Authoritative sources

| Topic | Official source | Provider consequence |
|---|---|---|
| Authentication, base URL, pagination | [REST API](https://docs.curseforge.com/rest-api/) | HTTPS `api.curseforge.com`, `x-api-key`, page size 1–50, total indexed results no greater than 10,000 |
| Developer approval | [API key application](https://support.curseforge.com/support/solutions/articles/9000208346) | Missing or rejected credential is `AuthenticationRequired`; no network request is attempted without a native secret |
| Credential and data restrictions | [Third-party API terms](https://support.curseforge.com/support/solutions/articles/9000207405-curseforge-3rd-party-api-terms-and-conditions) | No public key, no proxy workaround, no persistent CurseForge response cache under the general terms, bounded local development only |
| Author distribution consent | [Project Distribution Toggle](https://support.curseforge.com/en/support/solutions/articles/9000207877) | `allowModDistribution != true`, unavailable project/file, or missing authorized download URL is a hard unsupported/distribution result |
| Direct download authentication | [CDN authentication announcement](https://blog.curseforge.com/introducing-api-key-authentication-for-curseforge-file-downloads/) | Download requests must use only official returned URLs and the approved authentication behavior; no guessed ForgeCDN path |

## Endpoint contracts used

| Operation | Official REST route | ChunkPilot use |
|---|---|---|
| Minecraft versions | `GET /v1/minecraft/version` | Filter choices and exact version labels; never compatibility proof by itself |
| Search projects | `GET /v1/mods/search` | Minecraft game ID `432`; modpacks class ID `4471`; bounded search, game version, loader, sort, index, and page size |
| Exact project | `GET /v1/mods/{modId}` and bounded batch equivalent where applicable | Numeric project identity, slug/name/author/summary, official link/icon, distribution and availability state |
| Project files | `GET /v1/mods/{modId}/files` | Exact release history and filtering; never use only `latestFiles` as complete history |
| Exact file | `GET /v1/mods/{modId}/files/{fileId}` | Immutable file identity, channel, dates, size, hashes, game versions, loader, dependencies, availability, `isServerPack`, `serverPackFileId`, `alternateFileId`, and `parentProjectFileId` |
| Download URL | `GET /v1/mods/{modId}/files/{fileId}/download-url` | Resolve an official authorized URL when `downloadUrl` is absent; reject non-HTTPS or non-approved hosts |

The REST schema defines file relation types as embedded library `1`, optional dependency `2`, required
dependency `3`, tool `4`, incompatible `5`, and include `6`. The provider preserves these distinctions;
required dependencies block an incomplete plan, optional dependencies remain disclosed, and incompatible
relations are explicit conflicts rather than silent exclusions.

The schema defines release type `1` release, `2` beta, and `3` alpha; loader values include Forge `1`, Fabric
`4`, Quilt `5`, and NeoForge `6`. ChunkPilot stores both the API enum evidence and game-version strings and
does not guess a loader when they conflict or are absent.

## Server-pack support rule

An official candidate is directly supportable only when all of these are established from exact responses:

1. The numeric project and selected client file are available and distribution is allowed.
2. The selected file has a positive `serverPackFileId`, or its exact `alternateFileId` points to an
   author-labelled server pack. An additional file's optional `parentProjectFileId`, when present, must
   match the selected client file. Missing optional reverse fields do not invalidate the forward link.
   File names never locate or select a different release; the server-pack label classifies only the
   already-linked additional file. A dedicated server flag is not mandatory for author-linked additional
   files because the live API can report `isServerPack=false` for those server ZIPs.
3. The exact referenced server-pack file is available and has an official HTTPS download route.
4. Minecraft version and loader evidence are compatible with ChunkPilot's supported managed-loader path.
5. Size is bounded and at least one provider hash is present; current CurseForge file hashes are SHA-1 or
   MD5, so ChunkPilot records the provider hash and also computes its own SHA-256 after download.
6. Archive preflight finds no traversal, reparse, absolute, reserved-device, ADS, case-collision, or bounded
   expansion violation.

Client-only files, packs whose server pack is absent/unavailable, author-disabled distribution, arbitrary
scripts, unknown launch contracts, and manifests requiring guessed artifacts are `Unsupported` with the exact
reason. A local archive can still be inspected independently, but its origin is not promoted to verified
CurseForge identity without exact project and file evidence.

## Generated candidate rule

ChunkPilot may offer a generated server candidate only when the selected exact file manifest and all required
artifact identities can be resolved through official API responses, every required file is available for
third-party distribution, the loader has an existing verified headless installation path, and the resulting
plan needs no arbitrary operating-system launcher. The UI must label this as **Generated server candidate**, show the
evidence and limitations before install, and keep it distinct from an **Official server pack**.

The provider must not convert incomplete metadata into a best guess. Missing exact identity, unavailable
required downloads, unknown loader/bootstrap behavior, or client-only configuration produces a truthful
unsupported result.

Required manifest content is not synonymous with mods. Generated plan schema 2 distinguishes exact mod
JARs (class 6, loader and Minecraft metadata required) from resource-pack ZIPs (class 12, exact Minecraft
metadata required, no invented loader requirement). Resource packs remain byte-for-byte under
`resourcepacks/`, never renamed into `mods/` or silently omitted. Their ZIP containers use the common
bounded path/link/collision checks and require root `pack.mcmeta`. The plan digest binds content kind and
destination semantics. Unknown content classes remain unsupported rather than being dropped.

Gameplay content under `config`, `defaultconfigs`, `kubejs`, `scripts`, `resourcepacks`, `resources`,
`datapacks`, `global_packs`, `openloader`, and `patchouli_books` is preserved in its authored structure.
KubeJS and CraftTweaker scripts are gameplay inputs, not Windows launchers. Root launch scripts are not
copied or executed. Other override roots remain disclosed as ignored; a generated candidate is not a
claim that every possible modpack runtime or custom content loader is supported.

Authenticated metadata-only verification on 2026-09-14 established StaTech project `1605376`, client
`8699911` -> alternate file `8699918` -> parent client `8699911`, and confirmed AE2 Blackout
`1060372/8393438` as class 12. This is exact provider relationship/content evidence, not Minecraft runtime
certification. Installed upstream labels are refreshed in memory from the exact project/client-file IDs
after an explicit release check; the persistent CurseForge minimization policy remains unchanged.

The exact StaTech client and author-linked server ZIP were then downloaded through the native authenticated
CDN path and verified: 475,063,831 combined bytes; client manifest establishes NeoForge `21.1.248` on
Minecraft `1.21.1`; the server ZIP contains 2,775 files with 456,017,987 expanded bytes and passes bounded
import inspection. Its locally computed SHA-256 is
`696aa67e9dc88af3aa2f135b71fd781a880f4ef5c1345c1ce46836aebfb322c5`.
No Java process, world, or server runtime was started. The existing conservative 2 GiB payload ledger
guards archive-only and generated-plan certification too; interrupted reservations are not discarded.

Generated traversal resolves unversioned dependencies against all exact client-manifest pins before
visiting any root, including optional entries that become required dependencies. It never picks a newer
file merely because the dependency was encountered before its manifest entry. A real no-official-pack
preflight exposed this ordering defect; genuinely contradictory explicit pins still fail closed.

## HTTP and failure policy

- Only `https://api.curseforge.com` is accepted for metadata. Redirects are disabled for API calls.
- Download redirects are followed manually with a small bound and an explicit allowlist derived from official
  returned CurseForge hosts; credentials are never forwarded to an unapproved host.
- Metadata responses must be JSON with bounded bytes and expected schema. Archive responses are streamed.
- 401/403 map to authentication/policy unavailable; 404 to exact identity unavailable; 408/timeouts and 5xx
  to temporary provider failure; 429 honors bounded `Retry-After` once and then reports rate limiting.
- General third-party terms currently prohibit saving/caching API data. CurseForge persistent/offline cache
  remains disabled unless application-specific written terms authorize it. In-memory request coalescing may
  retain only live-task state, not provider response data after completion.
- Cancellation propagates through metadata, downloads, hashing, extraction, loader resolution, validation,
  and activation. No cancellation is translated into a failed install or a completed package.

## Production status

The typed and locally approved provider may be exercised only through the native developer credential gate.
Public production activation remains **PUBLIC CREDENTIAL DELIVERY STILL GATED**; see
`docs/architecture/CURSEFORGE-CREDENTIAL-DELIVERY.md` for the unblocking evidence required.
