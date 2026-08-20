# Provider and compatibility research notes — Create Server v2

Status: planning document, written 2026-07-26. Companion documents: `CREATE-SERVER-V2-ARCHITECTURE.md`, `CREATE-SERVER-V2-ROADMAP.md`, `CREATE-SERVER-V2-RISK-REGISTER.md`, `CREATE-SERVER-V2-PRESET-INVENTORY.md`.

Evidence key: **[V]** verified in this repository. **[E]** current official external fact, sourced below. **[I]** inference. **[U]** unknown, requires validation at implementation time — provider terms and APIs change; do not trust this document's citations without re-checking them when a vertical-slice task actually touches that provider.

This document supplements `docs/PROVIDERS.md`, which remains the authoritative day-to-day reference for provider behavior; it does not duplicate or override it.

## 1. Mojang (Vanilla)

**[V]** Existing adapter: `BuiltInServerCatalogProvider`/`ServerDownloadCatalog` (exact class names per `src/ChunkPilot.Infrastructure/GuidedPlatformServices.cs:364-421`; `ServerDownloadCatalog` referenced from `AgentPipeServer.cs:25`).

**[E]** Official version manifest: `https://piston-meta.mojang.com/mc/game/version_manifest_v2.json`, maintained by Mojang, updated on every Java Edition release; v2 adds a SHA-1 hash per version JSON and a `complianceLevel` field. This remains the correct, documented source. [version_manifest.json – Minecraft Wiki](https://minecraft.wiki/w/Version_manifest.json)

**Action for implementation:** superseded by the findings below. The existing adapter reads enough to download a jar but not enough to decide whether a version can be offered at all.

### 1.1 Live verification, 2026-07-28 (Vanilla vertical slice)

Retrieved directly from the official endpoints on 2026-07-28T04:42:36Z, through the real Agent against an isolated temporary data root.

**[E] The Java requirement is published, and inferring it is wrong.** Each per-version metadata document carries a `javaVersion` block, for example `{"component":"java-runtime-epsilon","majorVersion":25}`. All 24 versions resolved during live validation supplied it. This matters because Minecraft has moved to a date-based scheme — the current release is **26.2** — and the repository's existing `JavaRuntimePolicy.RequiredMajorForMinecraft`, written when releases were numbered 1.x, infers **21** for it. Official metadata says **25**. `VanillaVersionCatalogService` therefore reads `javaVersion.majorVersion` first and falls back to the version-number policy only when the block is absent, recording which source applied on every entry.

**[E] Not every manifest entry has a server download.** `downloads.server` is absent for early entries (verified against `a1.2.6`, which publishes `client` only), and some older releases additionally publish `windows_server` (verified against `1.2.5`). An entry without `downloads.server` is offered to nobody.

**[E] Integrity evidence is a provider-supplied SHA-1, not a signature.** `downloads.server` supplies `sha1`, `size` and `url` on `piston-data.mojang.com` over HTTPS. ChunkPilot verifies that SHA-1 exactly as given and reports it as provider-supplied integrity evidence. It is not relabelled SHA-256 and is not described as signature verification, because Mojang publishes no signature here.

**[E] Release types.** The manifest's `type` field carries `release`, `snapshot`, `old_beta` and `old_alpha`. Snapshots stay behind an explicit channel choice; `old_beta`/`old_alpha` are treated as historic and never offered.

**[U] Not re-verified:** whether Mojang intends the `26.x` scheme to persist, and whether `javaVersion` is guaranteed present on all future entries. The fallback path exists precisely because neither is promised.

## 1a. Eclipse Adoptium (managed Java)

**[E] Verified 2026-07-28.** `https://api.adoptium.net/v3/assets/latest/{major}/hotspot?architecture=x64&heap_size=normal&image_type=jre&jvm_impl=hotspot&os=windows&vendor=eclipse` returns a Windows x64 JRE for Java 25 (`jdk-25.0.4+7`, 58,474,646 bytes) with a 64-character SHA-256 checksum, which is what the existing `AdoptiumTemurinProvider` already requests and `ManagedJavaRuntimeService` already verifies. No adapter change was needed: the current release's requirement of Java 25 is served by the existing integration unchanged.

## 2. Paper / Purpur

**[V]** `BuiltInServerCatalogProvider` handles these via their official downloads APIs per `docs/PROVIDERS.md` ("Official downloads API" / "Official API"). Not independently re-verified against live endpoints in this session (no code change proposed; low risk, already production-tested per `docs/UI-OVERHAUL-STATE.json`'s `loader-java-integration-tests` completed marker).

**Action:** re-confirm current endpoint shape only if the Plugins vertical-slice task (Roadmap Task 6) hits an unexpected response — not expected to be necessary.

## 3. Fabric

**[V]** `LoaderMetadataService.ResolveFabricAsync` (`GuidedPlatformServices.cs:1029,1068`).

**[E]** Official Fabric Meta API base URL `https://meta.fabricmc.net`; loader-version query `GET /v2/versions/loader/{minecraft_version}`; direct server JAR download `GET /v2/versions/loader/{minecraft_version}/{loader_version}/server/jar`. Confirmed against the [FabricMC/fabric-meta](https://github.com/FabricMC/fabric-meta) repository and [Fabric's official server page](https://fabricmc.net/use/server/). Matches `docs/PROVIDERS.md`'s existing citation.

**Action:** none required beyond the standard re-verification every vertical-slice task performs against live behavior (Roadmap Task 7).

## 4. Quilt

**[V]** `LoaderMetadataService.ResolveQuiltAsync` (`GuidedPlatformServices.cs:1031,1102`). `docs/PROVIDERS.md` cites `https://quiltmc.org/en/install/server/` and states published SHA-1 is verified.

**[U]** Quilt is **not one of the roadmap's four vertical-slice loader tasks** (7–9 cover Fabric, NeoForge, Forge only) — the assignment's "Mods" requirements list Fabric/Forge/NeoForge implicitly via the six-intent structure but the existing `InstallSourceType` enum also includes Quilt, and `LoaderMetadataService` already resolves it. **Recommendation, not yet actioned:** add a Quilt vertical-slice task to the roadmap once Tasks 7–9 land, using the identical pattern — flagged here rather than silently dropped, since Quilt is already a first-class `InstallSourceType` with working resolution code; excluding it from the Mods intent's loader list without comment would be a silent scope reduction the user did not ask for.

## 5. Forge

**[V]** `LoaderMetadataService.ResolveForgeAsync` (`GuidedPlatformServices.cs:1033,1132`). `docs/PROVIDERS.md` cites official Forge Maven metadata and a checksum sidecar.

**[U]** Forge's installer has historically been the least uniform of the four loaders across Minecraft version eras (different installer JAR behaviors, different `run.bat`/`run.sh` generation across versions). This is **not** independently re-verified in this session against current Forge Maven metadata; Roadmap Task 9 explicitly requires re-verification at implementation time rather than trusting this note.

## 6. NeoForge

**[V]** `LoaderMetadataService.ResolveNeoForgeAsync` (`GuidedPlatformServices.cs:1035,1162`).

**[E]** Confirmed current in this session: official server installation documented at `https://docs.neoforged.net/user/docs/server/`. Current documented steps: download the installer JAR from `https://maven.neoforged.net/releases/net/neoforged/neoforge/{version}/neoforge-{version}-installer.jar`, run `java -jar neoforge-installer.jar --installServer`, adjust `user_jvm_args.txt` for RAM/JVM flags, run `./run.sh`/`run.bat`, then flip `eula=false` to `eula=true`. This matches the existing `ManagedServerInstaller`/`LoaderInstallationService` design (`docs/ARCHITECTURE.md`: "Generated `win_args.txt` or direct launcher JARs become non-detaching profiles; downloaded batch files are never executed" — i.e. ChunkPilot must not literally execute the generated `run.bat`, but must replicate its effective launch behavior directly, which is already the documented approach). [Installing a NeoForge Server | NeoForged docs](https://docs.neoforged.net/user/docs/server/)

**Action:** none required; existing adapter targets the correct, current official source. Reconfirm at Roadmap Task 8 regardless, per standing policy.

## 7. Modrinth

**[V]** `ModrinthCatalogProvider` (`GuidedPlatformServices.cs:423-548`).

**[E]** Official API base confirmed current: `docs.modrinth.com`. Rate limit confirmed current in this session: **300 requests per minute per IP**, identical whether authenticated or not; limit/remaining/reset communicated via `X-Ratelimit-Limit`/`X-Ratelimit-Remaining`/`X-Ratelimit-Reset` response headers; Modrinth's own documentation invites contact for higher-limit use cases. [Overview | Modrinth Documentation](https://docs.modrinth.com/api/) — matches `docs/PROVIDERS.md`'s existing "Official v2 API" citation.

**Action:** the new modpack-catalog UI (Roadmap Tasks 10–11) must respect this rate limit — the existing `GuidedCatalogService`'s 6-hour cache (`GuidedPlatformServices.cs:246-334`) already provides substantial headroom; no additional client-side rate-limiting logic is expected to be necessary given the existing cache, but the implementing session should confirm the wizard doesn't issue a burst of uncached requests per keystroke (i.e. search-as-you-type must be debounced — a UI detail for Task 10, not a backend change).

## 8. CurseForge

**[V]** `CurseForgeCatalogProvider` (`GuidedPlatformServices.cs:550-726`) — real, working integration against `https://api.curseforge.com/v1/mods/search` (`gameId=432` for Minecraft, `classId=4471` for modpacks), gated on a user-supplied API key stored via `ISecretStore`/DPAPI, never bundled.

**[E], time-sensitive, verified 2026-07-26 — action required:** CurseForge is enforcing API-key authentication for **direct file downloads from the CDN** (`edge.forgecdn.net`), separate from the search API's existing key requirement. The blog announcement states the enforcement date as **July 16, 2026** — i.e. **already in effect as of this document's writing (2026-07-26)**. Once enforced, requests to CDN download URLs without a valid key return `401 Unauthorized`. This explicitly targets "launchers, modpack installers, server tools, and similar integrations" — squarely ChunkPilot's use case. [Introducing API Key Authentication for CurseForge File Downloads](https://blog.curseforge.com/introducing-api-key-authentication-for-curseforge-file-downloads/)

**Why this matters for the existing code, not just new code:** `CurseForgeCatalogProvider.BrowseAsync` encodes download URLs as a placeholder scheme, `curseforge-file:{serverFileId}` (`GuidedPlatformServices.cs:616-617`), rather than a real HTTPS URL — meaning **the actual file download must be resolved separately**, presumably elsewhere in `ManagedServerInstaller`/`ManagedServerInstaller.cs` or a currently-unread part of the CurseForge integration. **This session did not locate and read that resolution code** (out of scope for a planning pass that must not read the entire repository) — **this is the single most important, concrete, dated action item this research produced**: Roadmap Task 12 must, as its first step, locate wherever `curseforge-file:` URLs are actually resolved to a real download and confirm the API key is attached to that request, not only to the search request. If it is not, CurseForge modpack installation is very likely broken *today*, independent of anything Create Server v2 does — this would be a pre-existing production defect the planning session surfaced, not something v2 introduces.

**Terms compliance [E]:** CurseForge's 3rd-party API terms prohibit disclosing the API key to any third party (except employees under confidentiality obligations) and require ceasing use and destroying the key upon termination of the terms. [CurseForge 3rd Party API Terms and Conditions](https://support.curseforge.com/support/solutions/articles/9000207405-curseforge-3rd-party-api-terms-and-conditions) The existing user-supplied-key design (never bundling a shared ChunkPilot key) is the only compliant approach available and should not change.

**Action:** Roadmap Task 12 (research/decision) is now more urgent than a routine "re-verify terms" pass — it must resolve the file-download question above before Task 13 (implementation) proceeds, and should note the finding regardless of outcome (fix needed vs. already handled elsewhere).

## 9. Geyser / Floodgate (crossplay)

**[V]** `ICrossplayPackageProvider`/`OfficialCrossplayPackageProvider` (`src/ChunkPilot.Infrastructure/CrossplayServices.cs`). `docs/PROVIDERS.md` cites "Official downloads v2 metadata, platform-specific package, and required SHA-256." Not independently re-verified against live Geyser endpoints in this session; existing integration test (`Crossplay_packages_are_hash_verified_backed_up_and_removed_by_ownership`) exercises this path already.

**Action:** re-verify at Roadmap Task 14 per standing policy; no evidence of a problem today.

## 10. ViaVersion

**[V]** `docs/PROVIDERS.md` cites "Official Modrinth project release and SHA-512." This is a Modrinth-hosted project, so it rides on the Modrinth adapter rather than needing its own. **[I]** Out of scope for the six required creation intents (ViaVersion is a cross-version compatibility plugin installed post-creation, not a creation-time choice) — no action needed for Create Server v2.

## 11. FTB

**[V]** `docs/PROVIDERS.md`: "Unavailable until a documented supported public server-pack API is configured. No scraping fallback." **[U]** not independently re-searched in this session for a change in FTB's public API availability — low priority given the existing, correct, conservative stance (`UnsupportedByChunkPilot` per the architecture document's compatibility model) already matches "do not scrape, do not fake support."

**Action:** if a future session finds FTB now offers a documented public server-pack API, that is a new provider integration decision requiring its own research task — not assumed here.

## 12. Adoptium Temurin (managed Java)

**[V]** `AdoptiumTemurinProvider` (`GuidedPlatformServices.cs:746-777`), `docs/MANAGED-JAVA.md` cites the [Eclipse Adoptium API](https://api.adoptium.net/q/swagger-ui/). Not independently re-verified live in this session; existing integration test (`Managed_Java_fixture_is_verified_healthy_private_and_does_not_change_environment`) covers this path.

**Action:** none required for Create Server v2 beyond consumption.

## 13. Direct HTTPS manifest (Advanced/custom path)

**[V]** `docs/UPDATE-MANIFEST.md` documents a JSON manifest format (packId, versions array with mandatory `versionId`/`downloadUrl`/`sha256`, HTTPS-only URLs, `stable`/`beta`/`alpha` channels). **[U]** this document is titled and appears scoped to the *update* path — whether the same manifest adapter is reachable from *creation* (i.e., "Advanced/custom" letting a user paste a manifest URL rather than a raw direct file URL) is not confirmed. The current `InstallServerViewModel`/`InstallServerWindow.xaml` only expose a raw `DirectUrl`/`LocalZip` source type for creation, not a manifest URL.

**Action:** Roadmap Task 15 (Advanced/custom) should confirm whether extending creation to accept a manifest URL (not just a raw file URL) is in scope — **[U] this is a genuine open question for the implementing session, not decided by this planning document**, since the assignment's Advanced/custom requirements list "custom executable or JAR... launch arguments" but does not explicitly ask for manifest-URL support at creation time. Default recommendation: **do not add it** unless a vertical-slice task finds a concrete need; keep Advanced/custom's creation-time surface identical to today's `DirectUrl`/`LocalZip`/`ExistingPackageFolder` options.

## 14. Summary of action items this research produced

| Finding | Type | Urgency | Roadmap task |
|---|---|---|---|
| CurseForge CDN downloads now require an API key (enforcement date already passed as of this writing) — must confirm the existing `curseforge-file:` resolution attaches the key | **[E]**, dated, actionable | **High — may be a pre-existing production defect, not just a v2 concern** | Task 12 |
| Quilt has working `LoaderMetadataService` support but no assigned vertical-slice task | **[I]**, scope gap | Medium | Recommend adding after Tasks 7–9 |
| Forge installer behavior not independently re-verified this session | **[U]** | Medium | Task 9 |
| Whether Advanced/custom should accept a manifest URL at creation time | **[U]** | Low | Task 15 |
| Zip-bomb / symlink-in-archive protections not confirmed for the create path specifically | **[U]** (see risk register #14, #15) | High (security) | Task 4 |
