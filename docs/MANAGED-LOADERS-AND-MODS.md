# Managed loaders and mods

ChunkPilot 1.3 keeps Fabric, NeoForge, Forge and Quilt behind the same provider, Java, creation, lifecycle,
networking, backup, version, recovery, capability, and WebUI boundaries as Vanilla and Paper.

## Official identity

- Fabric versions come from Fabric Meta. A creation selection retains the exact Minecraft version,
  Loader version, installer version, generated server-launcher URL, and SHA-256.
- NeoForge versions come from official Maven metadata. ChunkPilot maps the documented NeoForge
  version scheme to Minecraft, downloads the exact installer plus `.sha256`, runs it headlessly in a
  unique staging root, and registers the generated argument-file launch rather than treating the
  installer as the server JAR.
- Forge versions come from the official Maven metadata and promotions documents. Modern exact
  installers use `--installServer`; the launch profile is the installer-generated argument file.
- Quilt versions and installer identity come from Quilt's official Meta service and Maven. The
  server installer is bound to the reviewed destination and generated launch JAR. Installer Java
  is separate from server Java: current installer 0.15.1 runs on private Java 17, while the created
  server keeps the exact Java 8, 16, 17, 21, or 25 runtime required by its Minecraft version.
- Legacy Fabric and Ornithe have explicit catalog identities. They are never presented as ordinary
  Fabric. Minecraft 1.0, Beta 1.8 and Beta 1.8.1 have no official Mojang dedicated-server artifact,
  so automatic creation remains blocked pending a user-supplied original JAR with exact integrity.
- Same-Minecraft-version loader updates use the `ManagedLoader` update provider. Fabric rematerializes
  the official launcher; NeoForge, Forge, and Quilt rerun the verified installer in staging. The existing update
  transaction owns recovery, activation validation, and rollback.

## Exact runtime certification

The resumable stable-catalog campaign reached a terminal result for 187 exact official catalog
entries. Isolated loopback certification passed 162: Fabric 47, NeoForge 13, Forge 59, and Quilt 43.
The newest exact pass for each platform is Recommended; the other exact passes are Verified. No
neighbouring version inherits that evidence.

Twenty-three entries remain preflight-blocked: eight NeoForge Minecraft versions publish no
selectable stable exact build and fifteen old Forge installers publish no provider SHA-1 or SHA-256.
Forge 1.20.3 / 49.0.2 does not produce a controllable non-detaching launch profile after installation.
Forge 1.21.9 / 59.0.5 reaches readiness but does not cleanly exit after the owned `stop` command within
the bounded certification timeout. Those two identities remain failed, not Verified.

Quilt 1.17.1 and older originally exposed a real toolchain boundary: installer 0.15.1 requires Java 17,
while the servers require Java 16 or 8. Creation, updates, and certification now resolve the installer
runtime separately; all 43 stable Quilt entries then passed exact readiness, legacy/modern status ping,
file checks, and clean stop with zero cleanup failures.

Run or resume one platform without touching normal application data:

```powershell
dotnet run --project .\src\ChunkPilot.Certification\ChunkPilot.Certification.csproj -c Release -- certify-loader --platform Quilt --all-stable --retry-failed --accept-minecraft-eula-for-certification --cache .\artifacts\managed-loader-certification
```

The EULA switch is required and writes `eula=true` only inside unique disposable certification roots.
Ledgers are atomic and resumable; JARs, Java runtimes, worlds, and full diagnostics remain ignored under
the certification cache. Production consumes only the compact, exact-identity evidence embedded in
`Resources/managed-loader-runtime-certification-v1.json`.

Ornithe 1.0, b1.8, and b1.8.1 each reached a terminal `BlockedMissingOfficialArtifact` result. Legacy
Fabric does not list those identities. Mojang publishes no official dedicated-server artifact for
them, so ChunkPilot did not substitute Betacraft or another archive mirror.

Official references:

- <https://meta.fabricmc.net/>
- <https://docs.fabricmc.net/players/faq>
- <https://docs.neoforged.net/user/docs/server/>
- <https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/neoforge>

## Mod management

Fabric and NeoForge capability profiles replace Plugins with Mods. The shared add-on pipeline:

1. inspects bounded `fabric.mod.json` or documented NeoForge TOML metadata without loading classes;
2. rejects wrong-loader and client-only JARs;
3. constrains Modrinth search and release selection to the exact game version, loader, server
   environment, JAR type, provider host, declared size, and SHA-512;
4. classifies required, optional, incompatible, embedded, and load-before relationships;
5. installs a recursively resolved required-dependency plan transactionally;
6. applies enable, disable, update, and removal only through the Agent's serialized safe-restart path;
7. preserves configuration and known-good JARs for recovery;
8. exposes common identity-owned config through the same atomic editor used for plugins.

Client requirements are retained from provider and archive metadata and appear in mod detail and
friend-facing context. Unknown evidence stays Unknown. ChunkPilot does not use `/reload`, execute a
JAR to inspect it, scrape providers, or offer CurseForge in this phase.

## Pack-platform boundary

`PackPlatformAssessment` centrally classifies explicit Vanilla, Paper/Bukkit/Spigot, Fabric,
NeoForge, Forge, Quilt, unknown, and conflicting metadata. Any mod-loader requirement or mod JAR
prevents Paper eligibility. Hybrid Paper/mod-loader servers are unsupported. Modrinth `.mrpack`
creation and whole-release updates use these same exact loader strategies; see
[MODPACKS.md](MODPACKS.md). CurseForge creation and legacy packs without redistributable official
server artifacts remain open; exact stable campaigns for the four typed modern loaders are complete.
