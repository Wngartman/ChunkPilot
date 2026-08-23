# Network egress inventory

ChunkPilot is local-first. It has no account requirement, telemetry, advertising, analytics, or automatic
crash upload. Network access is limited to the paths below. Ordinary server supervision and file editing do
not contact ChunkPilot-operated services.

Every internet request inherently exposes the computer's public IP address to the destination. ChunkPilot's
HTTP clients also send a product user-agent. The **Data sent** column lists the additional application data
placed in the URL or body.

| Destination or class | Trigger | Automatic? | Data sent | Bound and cache behavior |
|---|---|---:|---|---|
| `piston-meta.mojang.com` and manifest-selected official Mojang download hosts | Browse or create/update a Vanilla server, or resolve an official historical artifact | Only inside the selected workflow | Minecraft version and artifact URL from official metadata | Metadata calls are normally 30–45 seconds; large verified downloads are streamed with longer operation timeouts and local artifact reuse |
| `fill.papermc.io`, `api.purpurmc.org` | Browse, create, or update Paper/Purpur | Only inside the selected workflow | Minecraft version and build identity | 30–45 second metadata calls; catalogs use bounded local caches; artifacts are streamed and hash-checked |
| `meta.fabricmc.net`, `meta.quiltmc.org`, `quiltmc.org`, `maven.quiltmc.org`, `maven.minecraftforge.net`, `files.minecraftforge.net`, `maven.neoforged.net` | Browse, create, certify, or update a managed loader server | Only inside the selected workflow | Minecraft and loader versions | 30–45 second metadata calls, four-hour catalog cache where applicable; installers/libraries use bounded operation timeouts and integrity checks |
| `meta.legacyfabric.net`, `meta.ornithemc.net`, `maven.fabricmc.net`, `maven.quiltmc.org`, `maven.ornithemc.net` | Explicit historical-loader creation/certification | Only inside the selected workflow | Historical Minecraft, loader, and intermediary identities | Official-host allowlists; metadata calls are bounded; materialized artifacts require provider hashes or official sidecars |
| `api.adoptium.net` and the exact asset URL returned by Adoptium | A selected server needs a managed Java runtime not already present | Only inside creation/update that needs Java | Required Java major, Windows/x64/JRE selection | 30 second metadata call; runtime download is streamed, checksum-verified, and cached locally |
| `api.modrinth.com`, `cdn.modrinth.com` | Search/browse, resolve a link, install, or update a modpack/mod/plugin | Search occurs while the user uses provider browsing; no background polling | Search text, Minecraft version, loader, project/release identifiers | Catalog cache is fresh for six hours and usable offline for up to 30 days; plugin search cache is five minutes; downloads are bounded, streamed, and hash-checked |
| Allowlisted `.mrpack` origins: `cdn.modrinth.com`, GitHub download hosts, `gitlab.com`, and public Google storage redirects used by GitLab | Import a user-selected `.mrpack` whose manifest names one of these origins | No | Exact manifest-selected file paths | HTTPS only, at most five redirects, public DNS addresses only, 20-minute stream timeout, manifest size/hash verification |
| `api.curseforge.com`, `media.forgecdn.net` | Developer-gated CurseForge browse/link/update/install path | No in the public build; unavailable without approved credentials | Search/version/project/file identity and the configured API credential | 30–45 second metadata calls; the secret is stored through Windows credential protection and is not placed in diagnostic exports; downloads are streamed and verified when provider hashes exist |
| `api.github.com` and GitHub release asset hosts | Check or install a server pack explicitly linked to GitHub Releases | User action or an enabled update check/schedule | Repository, release, and asset identities | 45 second metadata timeout; up to 50 releases; downloaded target must pass the recorded size/hash policy |
| User-configured HTTPS direct-manifest and artifact hosts | Check or install an explicitly linked direct update manifest | User action or an enabled update check/schedule | The configured manifest URL, pack identity, and selected artifact URL | 45 second metadata timeout; HTTPS required; exact SHA-256 required before activation |
| `download.geysermc.org`, plus Modrinth for ViaVersion | Explicitly install/update Java/Bedrock crossplay packages | No | Selected project/platform and release identity | 30 second metadata and bounded streamed install; official provider SHA-256/SHA-512 required |
| `sessionserver.mojang.com`, `textures.minecraft.net` | Render a head for a player row that has an authoritative UUID | **Yes, while the Players workspace is visible** | Player UUID to Mojang; Mojang-selected texture identifier | Six-second HTTP timeout, one MiB per response, at most 128 successful/in-flight player entries in memory; failure falls back to a local generic head |
| `api.mcstatus.io` | User confirms the legacy one-time external status test | No; explicit consent for each test | Configured public hostname/address and port in the request path | Ten-second timeout; result is point-in-time and not treated as durable proof |
| Optional configured outside-in probe origin (`CHUNKPILOT_REACHABILITY_PROBE_URL`) | User presses **Check from outside** in Advanced connectivity | No; no production endpoint is compiled into this build | Router-reported public IPv4, server TCP port, and random correlation ID | HTTPS origin only, redirects disabled, 15-second total timeout, four KiB response limit, `no-store`; service contract is stateless |
| `terraria.org` | Development-only `--experimental-terraria-preview` certification | No; not reachable from ordinary Create Server | Fixed Terraria release package path | Exact host/path, redirects constrained to the same origin, 20-minute streamed download, expected size/hash and local cache verification |
| Allowlisted documentation and repair sites | User opens an EULA, Help source, issue/release link, or WebView2 repair page in the system browser | No | The selected documentation URL | External browser owns its network behavior; in-product Help uses a fixed HTTPS host allowlist |

## Local-network traffic

These paths do not contact an internet service, but they can send packets on the PC or home network:

- Minecraft status and port checks connect to the selected configured address/port. Normal lifecycle checks
  use loopback for a local managed server.
- Router discovery uses PCP and NAT-PMP to the selected default gateway on UDP 5351, then UPnP SSDP to
  `239.255.255.250:1900` and the exact local control URL announced by that router. Read-only detection can
  occur while evaluating connectivity. Creating or deleting mappings still requires the explicit Internet
  hosting workflow and exact ownership evidence.
- Minecraft itself opens the server's configured TCP listener, and Geyser may open its separately configured
  UDP listener. Those are server processes owned by the Agent, not analytics or ChunkPilot service calls.

## Logging, diagnostics, and credentials

Provider credentials are protected locally through the Windows secret store. Normal logs identify providers,
stages, hosts, and artifact names without intentionally logging authorization values. Diagnostic bundle export
redacts recognized authorization headers, API keys, tokens, passwords, and credential fields from both text and
structured JSON. It deliberately retains useful context such as server/player names, UUIDs, addresses, local
paths, versions, timestamps, and operation evidence, so the UI tells the user to review a bundle before sharing.

No diagnostic bundle is uploaded by ChunkPilot. Creating a bundle writes it locally; sharing it is a separate
user action outside the application.
