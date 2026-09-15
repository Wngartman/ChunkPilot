# Exact Beta server feasibility

## Metadata assessment — 2026-09-14

**Result:** Beta `b1.8` and `b1.8.1` remain feasible only as explicitly supplied historical-server inputs, not one-click official downloads. This assessment fetched metadata and checksum text only. It did not download, redistribute, or run a Minecraft binary and does not establish multiplayer certification.

| Exact target | Current Mojang metadata | Ornithe server metadata |
| --- | --- | --- |
| `b1.8` | [`b1.8.json`](https://piston-meta.mojang.com/v1/packages/e5b20b1a15daa60effefd86da94b118086214e8b/b1.8.json): `old_beta`, Java 8 launcher requirement, `downloads.client` only; no dedicated-server URL or hash. | [Generation 2 / Fabric 0.19.5](https://meta.ornithemc.net/v3/versions/gen2/fabric-loader/b1.8/0.19.5/server/json): exact game ID and headless `KnotServer` profile available. |
| `b1.8.1` | [`b1.8.1.json`](https://piston-meta.mojang.com/v1/packages/440e3b845c3991492a3d0c5f0ccfda78ab90d9b6/b1.8.1.json): same client-only artifact boundary. | [Generation 2 / Fabric 0.19.5](https://meta.ornithemc.net/v3/versions/gen2/fabric-loader/b1.8.1/0.19.5/server/json): separate exact game ID and headless profile available. |

The [Ornithe generation endpoint](https://meta.ornithemc.net/v3/versions/intermediary_generations) reports generation 2 as stable. Both inspected Fabric server profiles contain eight libraries. Six carry inline hashes; the loader and intermediary require official Maven checksum sidecars. The following SHA-256 sidecars were retrieved successfully:

- [`b1.8` intermediary](https://maven.ornithemc.net/releases/net/ornithemc/calamus-intermediary-gen2/b1.8/calamus-intermediary-gen2-b1.8.jar.sha256): `962232c3f8bced0faca4542be42ee965c42b7033b50f737373663764b4064e84`.
- [`b1.8.1` intermediary](https://maven.ornithemc.net/releases/net/ornithemc/calamus-intermediary-gen2/b1.8.1/calamus-intermediary-gen2-b1.8.1.jar.sha256): `5b5d4800e59e1e4993bd841e85541c9a053da2cb4713922207fb0bd02b22f490`.
- [Fabric Loader 0.19.5](https://maven.fabricmc.net/net/fabricmc/fabric-loader/0.19.5/fabric-loader-0.19.5.jar.sha256): `93044e4dd46de5d8136701292f05e868da096d2c9fddb4793e4fdbcc63efc695`.

These hashes identify loader infrastructure, **not** the missing Minecraft dedicated-server base. The [Ornithe installation guide](https://wiki.ornithemc.net/wiki/Installing_Ornithe) describes historical server installation, but a loader profile is not proof that ChunkPilot can obtain an approved base artifact or launch that exact combination.

### Existing implementation and limits

`OrnitheHistoricalVersionPolicy`, `OrnitheHeadlessProfileProvider`, the immutable materialization plan, and certification policy already preserve exact identities and require the missing base input. Production automatic Ornithe creation stays disabled. Existing certification documentation records missing-official-artifact outcomes, not successful Beta launches.

`LegacyServerArtifactInspector` bounds and inspects a selected JAR without executing it, records SHA-256, and uses a short-lived native token. Its structural check is not independent proof of the selected Beta version; observed runtime identity must still be checked. Java 8 is the existing curated server policy and agrees with the inspected loader minimum. Mojang's client metadata alone does not certify a dedicated server on that runtime.

Authentication and exact-client login remain unverified. Do not promise current-account compatibility, silently disable `online-mode`, infer modern UUID access-file behavior, or treat `b1.8` and `b1.8.1` as interchangeable. The existing historical status policy tries legacy status sources and owned-console evidence; an unsuccessful status request means unavailable, not zero players. Query and RCON are not enabled to manufacture a result. Worlds must remain separate from modern-server testing.

### Smallest next unit

Certify **one explicitly supplied `b1.8.1` Vanilla server JAR** through the existing native token/rehash path and managed Java 8 in an isolated disposable root. Record the exact artifact hash, observed version, headless readiness, actual status-source result, save/stop acknowledgement, exact process cleanup, and a separately witnessed matching-client login before claiming multiplayer support. Do not redistribute the input. Only after that gate should the existing Ornithe plan be connected to an Agent-owned materializer for one pinned Fabric profile; `b1.8` requires its own evidence rather than inheriting the result.
