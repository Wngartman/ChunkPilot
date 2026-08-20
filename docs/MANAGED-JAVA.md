# Managed Java

Managed Java is private per-user runtime storage under `%LOCALAPPDATA%\ChunkPilot\ManagedJava`. It is not a Windows Java installer.

ChunkPilot:

- gets Windows x64 Eclipse Temurin JRE metadata from the Adoptium API;
- requires the provider SHA-256;
- downloads into unique staging;
- blocks ZIP traversal;
- health-checks the absolute `bin\java.exe`;
- records vendor, major version, architecture, source, checksum, health, and usage;
- assigns the absolute path per server;
- moves unused owned runtimes to Recovery before removing their database record.

ChunkPilot never changes `PATH`, `JAVA_HOME`, Program Files, the registry Java installation, or system/user-selected Java. It never removes a runtime it does not own.

Resolution uses, in strongest-to-weakest order: an explicit reviewed runtime, package/loader evidence, the highest class-file version found in the JAR, and the Minecraft version. Java 32-bit is rejected for managed server use. Minecraft 1.20.5 and newer defaults to Java 21; 1.18-1.20.4 to Java 17; 1.17 to Java 16; older supported releases to Java 8.

## Vanilla creation reads the published requirement first

Those version rules were written when Minecraft releases were numbered 1.x, and they infer the wrong major for the current date-based scheme. `VanillaVersionCatalogService` therefore reads `javaVersion.majorVersion` from each version's own official metadata and falls back to the rules above only when the block is absent — recording on every entry which source applied, so the review screen can say "Mojang states it in this version's own metadata" or "Mojang did not state it, so ChunkPilot worked it out from the version number". A version for which neither establishes a requirement is offered to nobody rather than being given a hopeful default.

`InstallationCoordinator.BeginVanilla` then reuses an existing managed runtime of exactly that major version if there is one, and otherwise obtains one through the same Adoptium adapter and checksum verification described above. The wizard never asks for a Java path and never shows one; the completion screen names the runtime the server was actually given, matched from its launch executable rather than assumed.

`CHUNKPILOT_MANAGED_SERVERS_ROOT` redirects where managed servers are created, alongside `CHUNKPILOT_DATA_ROOT` for ChunkPilot's own data. Both are needed for an isolated validation run: the runtime cache follows the data root, but before this existed the servers themselves would still have been created in the real user profile.

Provider: [Eclipse Adoptium API](https://api.adoptium.net/q/swagger-ui/).
