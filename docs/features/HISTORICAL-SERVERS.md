# Historical Minecraft servers

ChunkPilot uses a provenance-first provider chain. Automatic installation is offered only when an official artifact or an explicitly authorized provider path supplies enough evidence to verify the exact server. Otherwise the UI offers an original user-supplied ZIP, JAR, or folder import and explains why automatic retrieval is unavailable.

## Exact legacy results

| Minecraft target | Automatic result | Fallback |
| --- | --- | --- |
| 1.2.5 Vanilla | Available from the official Mojang server artifact. Existing compact certification evidence records Java 8, readiness, clean stop, expected files, no GUI, and cleanup. Historical status-query behavior remains explicitly limited. | Import also remains available. |
| 1.0 Vanilla | Mojang's current official metadata does not contain a dedicated-server download. ChunkPilot does not substitute a client JAR or mirror. | Import an original server ZIP, JAR, or folder. |
| Beta 1.8 Vanilla | Mojang's current official metadata does not contain a dedicated-server download. | Import an original server ZIP, JAR, or folder. |
| Beta 1.8.1 Vanilla | Mojang's current official metadata does not contain a dedicated-server download. | Import an original server ZIP, JAR, or folder. |

## Ornithe

ChunkPilot includes typed support for Ornithe Meta v3 headless Fabric and Quilt profiles. It preserves the provider's exact historical game identifiers, validates the official metadata and Maven origins, records launch profile identity and integrity requirements, and supports the exact 1.0, 1.2.5, Beta 1.8, and Beta 1.8.1 metadata targets.

Production automatic creation remains unavailable until the Agent-owned headless materializer and exact runtime certification are complete. Ornithe still requires an original Minecraft server base artifact: 1.2.5 has an official Mojang artifact, while the other three targets require a legitimately obtained user-supplied base JAR. ChunkPilot does not download them from community mirrors.

Legacy Fabric does not publish builds for these exact four targets. Historical Forge has a distinct official archive shape for 1.2.5 and is not treated as a modern installer JAR; it remains unavailable until that archive path is independently inspected and runtime-certified.

## Import safety

Native pickers return a short-lived opaque token to the WebUI, never a native path. The Agent reopens and rehashes the selected input before use, rejects traversal, reparse points, path collisions, reserved Windows names, excessive expansion, and executable pack scripts, then installs through unique staging and transactional registration. The source remains unchanged.
