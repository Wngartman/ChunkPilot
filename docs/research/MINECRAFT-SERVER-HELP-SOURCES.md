# Minecraft Server Help Sources

Reviewed: 2026-08-21

This register is the evidence boundary for ChunkPilot's bundled Help Center. Product articles use
ChunkPilot's own authoritative server state first, then link to current primary documentation. The
Help Center remains useful offline; external sources open only after a user chooses a source link.

| Source | URL | Status | Review scope | Volatility / caveat |
|---|---|---|---|---|
| Minecraft Java Edition server download | https://www.minecraft.net/en-us/download/server | Official | Java requirement, EULA boundary, LAN/firewall/port-forwarding context | Product FAQ and download wording can change |
| Paper basic troubleshooting | https://docs.papermc.io/paper/basic-troubleshooting/ | Official project | Startup signatures, Java mismatch, plugin load failures | Examples track current Paper behavior; exact class versions age |
| Paper adding plugins | https://docs.papermc.io/paper/adding-plugins/ | Official project | `latest.log`, missing dependencies, invalid plugin metadata, load-state checks | Plugin loader behavior can change |
| Paper updating | https://docs.papermc.io/paper/updating/ | Official project | Stop-before-replace, backup scope, safe update sequencing | Operational recommendations can change |
| Paper profiling | https://docs.papermc.io/paper/profiling/ | Official project | Evidence-based lag profiling and current bundled spark guidance | Bundled profiler/version guidance is especially fragile |
| Fabric installing mods | https://docs.fabricmc.net/players/installing-mods | Official project | Loader, Minecraft-version, Java-edition, and trusted-source matching | Page currently targets the active Fabric documentation version |
| NeoForge server guide | https://docs.neoforged.net/user/docs/server/ | Official project | Server setup, EULA, port/firewall context, mods, server packs, backup-before-update | Versioned loader and installer behavior can change |
| NeoForge troubleshooting and FAQ | https://docs.neoforged.net/user/docs/faq/ | Official project | Logs, profiling, and dependency-preserving mod isolation | Support paths and tooling can change |
| Modrinth modpack format | https://support.modrinth.com/en/articles/8802351-modrinth-modpack-format-mrpack | Official provider | `.mrpack`, overrides, server-overrides, client-overrides | Format revisions and provider behavior can change |
| Microsoft Windows Firewall rule guidance | https://learn.microsoft.com/en-us/windows/security/operating-system-security/network-security/windows-firewall/configure | Official | Exact program, port, scope, profile, and allow-rule principles | Enterprise policy UI and Windows wording can change |
| Oracle `UnsupportedClassVersionError` | https://docs.oracle.com/en/java/javase/17/docs/api/java.base/java/lang/UnsupportedClassVersionError.html | Official | JVM meaning of unsupported class-file versions | API meaning is stable; Minecraft's required Java release is not |

## Product interpretation rules

- An official page is evidence for its own platform, not universal proof for every loader or server.
- Exact current versions, provider availability, bundled profilers, authentication outages, and
  router capabilities are fragile. ChunkPilot must show live/local evidence or say they are unknown.
- A local listener, a Windows rule, and a router mapping are separate facts. None alone proves public
  reachability.
- Community sources may be added only when official documentation leaves a real gap. They must be
  labelled community in both this register and the product.
- No article recommends disabling Windows Firewall, routine `online-mode=false`, deleting world
  files, overwriting the only copy, or replacing JARs while a server is running.
