# WebUI parity matrix

| Workflow | WebUI path | Authority | Status |
|---|---|---|---|
| Dashboard and server library | Dashboard / Servers | `MainViewModel` snapshot | Wired |
| Select/manage server | Server workspace | existing selection and detail load | Wired |
| Start, stop, restart | Hero lifecycle action | existing commands and Agent | Wired |
| Console read/send | Console | bounded console and send command | Wired |
| Players | Players | player-access snapshot and existing moderation commands | Wired with destructive confirmation |
| Files/open folder/edit text | Files | path-confined Agent file service and native Explorer action | Wired; text editing is atomic and bridge-bounded |
| Plugins / Mods / Modpack | Capability label | Agent inventory, provider operations, and linked update source | Paper exposes Plugins; Fabric/NeoForge/Forge/Quilt expose Mods; an identity-linked pack exposes Modpack. Pack updates compare exact provider releases and never silently update constituent mods |
| Backups/create/verify/restore | Backups | existing backup commands | Wired |
| Versions/check/install/verify/rollback/cancel | Versions | official Mojang inventory plus existing update and version commands | Full searchable inventory and installed evidence wired; hardened linked-source operations retained; arbitrary catalog-target change remains unavailable |
| Global settings | Settings | existing settings save | Wired |
| Server properties | Server Settings | atomic server.properties path | Wired |
| Connectivity/share | hero / Overview / Share / Server Settings | existing network mode, router, exact firewall and outside-in commands | Four beginner modes, distinct local/LAN/verified-public addresses, consent, retry/cancel/stop and advanced evidence wired; public success remains outside-in only |
| Create Vanilla server | Create Server | official Mojang inventory plus hardened creation gateway | Exact selected ID, artifact/Java/profile evidence, experimental consent, review, and begin wired |
| Create Paper server | Create Server | official PaperMC Fill v3 inventory plus hardened creation transaction | Exact Minecraft version and build, identity-bound certification, Java, connectivity, EULA, review and begin wired; metadata-only builds require separate experimental consent |
| Create Fabric server | Create Server | official Fabric Meta endpoints plus hardened creation transaction | Exact Minecraft, Loader and installer identity, managed Java, official launcher SHA-256, connectivity, EULA and review wired; exact certified combination is recommended |
| Create NeoForge server | Create Server | official NeoForge Maven metadata and hardened creation transaction | Exact Minecraft and NeoForge version, official installer SHA-256, headless staged installer, generated argument launch, managed Java, connectivity, EULA and review wired |
| Create Forge / Quilt server | Create Server | official Maven/Meta catalogs and hardened creation transaction | Exact Minecraft, loader and installer identity, managed Java, connectivity, EULA and review wired; one exact recommended combination for each was runtime-certified |
| Create Modrinth pack server | Create Server | official Modrinth v2 API/CDN, native picker, Agent transaction | Search or pasted project link, exact `.mrpack` release, local `.mrpack`, archive/file hashes, server environment, exact declared loader/Java, connectivity, EULA and review wired; runtime proof occurs on first authoritative start |
| Paper build updates | Versions | official PaperMC build inventory and existing update transaction | Exact same-Minecraft-version build updates are staged, verified and rollback-capable; Minecraft-version upgrades remain a separate risk boundary |
| Fabric / NeoForge / Forge / Quilt loader updates | Versions | official loader catalogs and existing update transaction | Exact same-Minecraft-version loader updates materialize or run the reviewed installer in staging, then use existing recovery and rollback; game-version upgrades remain separate |
| Import server | Servers / Dashboard | existing read-only import dialog | Wired |
| Rename/change icon | server hero and Server Settings | existing safe workflows | Wired; staged pan/zoom/rotate crop and repeated replacement covered |
| Edit Vanilla MOTD | Server Settings / Server appearance | atomic `server.properties` path | Wired; two-line visual/raw round trip with restart metadata |
| Activity | Activity | authoritative activity history | Wired |
| Schedules/automation | Automation | existing Agent schedule authority | Common interval/daily tasks and removal wired |

“Wired” means the React action calls the existing presentation command and shows authoritative snapshots; it does not make WebUI the default. Items marked pending are listed as backend/presentation gaps and are not represented as complete controls.
