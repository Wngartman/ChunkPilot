# Minecraft Java Edition version platform

ChunkPilot inventories Minecraft Java Edition versions from Mojang's official
`version_manifest_v2.json` and each entry's official metadata document. Server JAR URLs, SHA-1
digests, sizes, Java metadata, IDs, types, and timestamps are accepted only from those documents.
ChunkPilot does not use an unofficial binary mirror or derive a server download from a client download.

## Evidence model

One typed `VanillaVersionOption` carries the inventory record, release kind, official server artifact,
integrity metadata, Java requirement and its source, managed launch profile, capability profile,
certification evidence, limitations, support tier, and provenance. Create Server and the existing-server
Versions page consume the same catalog through the typed WebUI bridge.

Support is intentionally stricter than existence:

- **Recommended** is the manifest's latest stable release only after that exact build is runtime-certified.
- **Verified** requires complete metadata plus recorded isolated launch, readiness, and clean-stop evidence.
- **Experimental** has a verifiable official artifact and resolved managed profile but lacks full runtime
  certification, or is not a stable release. Creation requires an explicit acknowledgment.
- **Unavailable** remains searchable and names the missing artifact, integrity data, Java requirement, or
  launch profile.

Metadata validation is not runtime certification. `ChunkPilot.Certification` now provides the isolated,
resumable exact-version campaign documented in [Vanilla certification](VANILLA-CERTIFICATION.md), but
this milestone did not silently accept the Minecraft EULA. It therefore records zero runtime-certified
versions and makes no Recommended or Verified claims.

## Inventory snapshot

An official-source evidence probe on 2026-08-16 retrieved 906 entries: 102 releases, 743 entries typed
as snapshots by Mojang (including 198 pre-releases and 59 release candidates), 26 Beta entries, and
35 Alpha entries. Of these, 839 published an official server artifact with complete SHA-1 and size,
and 826 also resolved Java and a managed launch profile. The complete pre-EULA campaign gave every
entry a terminal result: 826 blocked at the deliberate EULA gate, 67 blocked by missing official
server artifacts, and 13 blocked by unresolved Java. Those 826 remain metadata/profile-validated
Experimental entries; the remaining 80 are Unavailable. Counts change when Mojang publishes or
reclassifies versions and are not hardcoded into the application.

## Java and launch policy

Official per-version `javaVersion.majorVersion` wins. A centralized numeric-release fallback covers
documented modern boundaries (Java 8, 16, 17, and 21) only when official metadata omits Java. Alpha,
Beta, and nonnumeric unresolved IDs are never guessed.

The managed launch profile resolves modern EULA + `nogui` and legacy `nogui` groups from release
timestamps. Alpha, Beta, pre-release-date, and unknown behavior stays unresolved. Capabilities such as
server icons, modern properties, status query, datapacks, and managed version change live on that
profile rather than in React version-string checks.

## Cache and failure behavior

The catalog cache has a versioned schema and retrieved timestamp. Writes use a same-directory partial
file followed by atomic replacement. A fresh cache avoids network work. A stale last-known-good cache
renders immediately while one deduplicated bounded-concurrency refresh runs in the background. Provider
timeouts, cancellation of an individual caller, corrupt cache input, and failed metadata requests do
not erase the last-known-good catalog.

## Current version-change boundary

Creation passes the exact selected catalog ID, artifact evidence, Java requirement, launch profile, and
warnings into the existing transactional Vanilla creation gateway. Existing servers continue to use
ChunkPilot's hardened linked-source update, recovery-point, verification, activation, rollback, and
cancellation workflow. This milestone does not add a casual arbitrary-target downgrade or bypass that
workflow. The Versions page shows the complete inventory and installed-version evidence, but an
arbitrary official catalog row is not an install button until the native update coordinator can safely
accept that typed target and enforce world-compatible migration policy.

Primary changing sources:

- `https://piston-meta.mojang.com/mc/game/version_manifest_v2.json`
- per-version metadata URLs supplied by that manifest
- `https://www.minecraft.net/en-us/download/server`
- `https://help.minecraft.net/hc/en-us/articles/4409225939853-Minecraft-Java-Edition-System-Requirements`
