# Current creation-system inventory and preset disposition

Status: planning document for Create Server v2. No production behavior changed by this document.

Evidence key used throughout: **[V]** verified directly in this repository at the commit this document was written against (branch `plan/create-server-v2`, based on `5f19531`). **[E]** current official external fact. **[I]** inference from verified evidence. **[U]** unknown, requires validation during implementation.

## 1. Where the current creation UI lives

**[V]** The only Create Server entry point is `InstallServerWindow.xaml` / `InstallServerWindow.xaml.cs` / `InstallServerViewModel.cs` in `ChunkPilot.App`. It is opened as a modal `Window` (`ShowDialog()`) from `MainViewModel.InstallServerAsync()` (`src/ChunkPilot.App/MainViewModel.cs:816-826`), not through the page-based `NavigationService`/`ServerOpened` system used by Dashboard/Servers/Overview.

**[V]** `InstallServerWindow.xaml` uses the **pre-overhaul legacy design system** (`Style="{StaticResource Card}"`, `PageHeaderTitle`, `PageHeaderDescription`, `MutedText`, `TextPrimary`, `PrimaryActionButton`), not the current token set (`AppCard`, `AppPageHeader`, `AppTextPrimary`, `AppPrimaryButton`) established by the visual-polish work through `5f19531`. This is the single clearest piece of evidence for the user's complaint: the Create Server surface was never carried into the new design system and looks visually foreign next to Dashboard/Servers/Overview.

**[V]** The form is one long `ScrollViewer` over a single `StackPanel` exposing, in this order: a quick-start `ComboBox`, a combined catalog browser (provider `ComboBox` + search `TextBox` + item/version `ComboBox`es), a raw `InstallSourceType` `ComboBox` (values: `Vanilla, Paper, Purpur, Fabric, Quilt, Forge, NeoForge, LocalZip, DirectUrl, ExistingPackageFolder` — `src/ChunkPilot.App/InstallServerViewModel.cs:23-35`), a free-text Minecraft version combo, a local package path/URL field, server name, build override, managed instance root path, Java executable path, an `Xms/Xmx/Port/MaxPlayers` `UniformGrid` of raw numeric fields, an EULA checkbox, and a progress/log block. Every item the assignment says must not appear in the normal flow (Java path, provider IDs as primary choice, raw ports, raw JVM/RAM fields, technical EULA presentation) is present and unconditional today.

**[V]** There is no intent step, no review step, no compatibility surfacing, and no distinction between beginner and advanced fields. Everything is always visible.

## 2. What a "preset" currently is — and what it is not

There are **three unrelated things in the repository that could be called "presets."** They must not be conflated.

### 2.1 `QuickStartKind` / `QuickStartPreset` (the one the user means)

**[V]** Defined in `src/ChunkPilot.Core/GuidedPlatformModels.cs`:
- `enum QuickStartKind` (line 13): `VanillaWithFriends, FasterVanilla, Modpack, PluginsAndMinigames, JavaBedrockCrossplay, BedrockDedicatedServer, ImportExistingServer, AdvancedCustomServer` — **8 values**.
- `record QuickStartPreset` (line 318): `Kind, Name, PlainLanguageSummary, SourceType, ManagedJava, WhitelistEnabled, OnlineMode, DailyBackup, BackupBeforeUpdates, MaxPlayers, NetworkMode, Properties (IReadOnlyDictionary<string,string>), ReviewItems (IReadOnlyList<string>)`.
- `static class QuickStartPresetFactory` (line 338): a pure `switch` expression, `Create(QuickStartKind, InstallSourceType fasterSoftware = Paper)`, returning a fully-populated `QuickStartPreset` per kind. No I/O, no persistence, no randomness — fully deterministic.

**Exact content per preset [V]:**

| Kind | Display name | `SourceType` | Managed Java | Whitelist | Daily backup | `server.properties` seeded | Plain-language summary |
|---|---|---|---|---|---|---|---|
| `VanillaWithFriends` | Vanilla With Friends | `Vanilla` | yes | yes | yes | `online-mode=true, white-list=true, max-players=8, difficulty=normal, enable-rcon=false, enable-query=false` | "Official Minecraft, no client changes, private by default." |
| `FasterVanilla` | Faster Vanilla | `Paper` (or `Purpur`/`Fabric` if `fasterSoftware` param overridden — **[I]** never actually overridden by any caller today) | yes | yes | yes | `online-mode=true, white-list=true, max-players=12, view-distance=8, simulation-distance=6` | "A performance-oriented server with clearly explained behavior differences." |
| `Modpack` | Modpack Server | `CustomPackage` | yes | yes | yes | none seeded | "An exact official server-pack version with matching client instructions." |
| `PluginsAndMinigames` | Plugins and Minigames | `Paper` | yes | yes | yes | none seeded | "Paper or Purpur with Vanilla-client plugin support." |
| `JavaBedrockCrossplay` | Java and Bedrock Crossplay | `Paper` | yes | yes | yes | none seeded | "Paper with reviewed Geyser/Floodgate configuration." |
| `BedrockDedicatedServer` | Bedrock Dedicated Server | `CustomPackage` | **no** | yes | yes | none seeded | "Official Windows Bedrock server with Bedrock-only controls." |
| `ImportExistingServer` | Import Existing Server | `ExistingPackageFolder` | no | no | no | none | "Read-only detection before registering the existing folder by reference." |
| `AdvancedCustomServer` (default arm) | Advanced Custom Server | `CustomPackage` | no | no | no | none | "Full control over software, Java, launch, memory, networking, world, and content." |

**How they're consumed [V]:** `InstallServerViewModel.OnSelectedQuickStartChanged` (line 148) sets `SourceType`, `UseManagedJava`, `MaxPlayers` from the chosen preset and rebuilds `QuickStartDetail` display text. At install time, `PresetAppliesToSource()` (line 363) only applies `Properties`/`DailyBackup` **if the current `SourceType` still equals the preset's `SourceType`** — i.e. if the user changes the source-type dropdown after picking a preset, the preset's domain defaults (whitelist, backup schedule, `server.properties` seed) are silently dropped without telling the user. **This is a real, reproducible defect** in the current UI (not something v2 needs to preserve) — it happens because presets are UI convenience *and* domain-default carriers at once, coupled through a fragile equality check on one field.

**Disposition:** these are genuinely useful **domain-default bundles** (whitelist policy, backup policy, starter `server.properties`) wrapped in a UI label. They are the right *idea* (curated starting points) with the wrong *presentation* (raw enum names like "Bedrock Dedicated Server" and "Advanced Custom Server" sitting in one flat combo box, no visual hierarchy, no icons, no distinction between "this is an intent" and "this is a software choice"). Recommendation: **keep `QuickStartKind`/`QuickStartPreset`/`QuickStartPresetFactory` in `ChunkPilot.Core` unchanged as the domain-default layer**; stop presenting them as a single dropdown; instead let the new intent-first wizard steps (Vanilla/Plugins/Mods/Modpack/Crossplay/Advanced) each *resolve to* the matching default bundle internally, and drop `ImportExistingServer` and `AdvancedCustomServer` from this enum's UI-facing role entirely (see §2.4 and §5).

### 2.2 `IGuidedCatalogProvider` implementations (not presets — providers)

**[V]** `BuiltInServerCatalogProvider`, `ModrinthCatalogProvider`, `CurseForgeCatalogProvider`, `UnavailableCatalogProvider` (`src/ChunkPilot.Infrastructure/GuidedPlatformServices.cs:364-736`) are **execution-plan resolvers**, not presets. They answer "what versions/projects exist and are they server-capable," not "what kind of server does the user want." Keep entirely; they are the foundation the new wizard's catalog pages call into (see architecture doc §4).

### 2.3 `LoaderInstallPlan` / `LoaderMetadataService` (not presets — deterministic install plans)

**[V]** `src/ChunkPilot.Infrastructure/GuidedPlatformServices.cs:1017-1208`. `LoaderMetadataService.ResolveAsync` dispatches on `InstallSourceType` (`Fabric`, `Quilt`, `Forge`, `NeoForge` all present, line 1029-1035) to per-loader resolvers that hit each project's official metadata endpoint and return a `LoaderInstallPlan` (loader, Minecraft version, loader version, installer version, download URL). This is the deterministic creation-plan layer the architecture document's "build a deterministic creation plan" phase already has a home for. Not user-facing at all today; keep as-is.

### 2.4 Quiz-only artifacts that should not survive into v2's user-facing vocabulary

- `ImportExistingServer` as a *value inside the Create Server combo box* **[V]** is actively harmful: "Add existing server" already exists as `MainViewModel.AddServerAsync()` → `ImportServerWindow`, a completely separate, safety-focused, read-only flow (`src/ChunkPilot.App/MainViewModel.cs:795-814`). Having "Import Existing Server" also selectable inside Create Server's quick-start dropdown creates two different UI paths to the same intent, one of which (`QuickStartKind.ImportExistingServer` → `SourceType.ExistingPackageFolder` inside the install flow) is not actually wired to the read-only `ImportServerViewModel`/`Detect` pipeline the assignment requires "Add existing" to use. **Recommendation: remove `ImportExistingServer` from the Create Server wizard's selectable intents entirely.** It can stay in the `QuickStartKind` enum (harmless, unused) or be deleted — deleting it is a v2 implementation-phase decision, not a planning-time one, because deleting an enum member is a source-breaking change to every `switch` that pattern-matches on it exhaustively (`QuickStartPresetFactory.Create`, and any test enumerating `Enum.GetValues<QuickStartKind>()`, e.g. `AgentPipeServer.cs:245`).
- `AdvancedCustomServer` as a *quick-start dropdown entry sitting next to "Faster Vanilla"* **[V]** undersells what Advanced/custom is supposed to be (a distinct top-level path with its own explicit-fields screen, not one more item in a list of curated bundles). Recommendation: keep the `QuickStartKind.AdvancedCustomServer` enum value and its default-arm preset (still useful as the domain-default bundle for "no curated defaults, don't seed backup schedule automatically") but stop rendering it inside the intent selector's normal five choices — Advanced/custom becomes the sixth, visually distinct top-level path per the assignment's required primary paths, not a quick-start list item.

## 3. Are any user-facing concepts genuinely missing from the current enum?

**[V]** `QuickStartKind` has no dedicated "Plugins" vs. "Mods" split as two of the assignment's six required top-level intents — `PluginsAndMinigames` maps only to Paper, and there is no separate "Mods" quick-start kind at all (Fabric/Forge/NeoForge/Quilt only appear as raw `InstallSourceType` combo values, never as a curated intent). **This is the real gap**, not a redundant-preset problem: the current 8 `QuickStartKind` values collapse "Plugins" and "Mods" into "pick a source type," which is exactly the raw-implementation-name-as-primary-decision problem the assignment calls out.

**Recommendation:** the six top-level intents (Vanilla, Plugins, Mods, Modpack, Crossplay, Advanced/custom) become the wizard's **only** primary decision. `QuickStartKind` gets two additions in the v2 implementation phase (not this planning phase): a value that represents "Mods, no modpack chosen yet, loader picked directly" (today only reachable by picking `Fabric`/`Forge`/`NeoForge`/`Quilt` as a raw `InstallSourceType`) — see the roadmap's Fabric/NeoForge/Forge vertical-slice tasks. `Plugins` already has a reasonable one-to-one match in `PluginsAndMinigames`.

**Is a 7th top-level choice needed?** **[I]** No. Bedrock Dedicated Server (today's 6th quick-start) is better modeled as a **sub-choice inside Crossplay or as part of Advanced/custom** than as a 7th top-level tile, because: (a) it shares almost nothing with the other five paths (no Java runtime, no loader, no catalog), (b) the assignment's required top-level list is explicit and closed at six, and (c) a pure-Bedrock dedicated server is a narrow, already-well-served-by-Mojang-directly scenario that does not need equal visual weight next to Vanilla/Mods/Modpack. Recommendation: fold it into the Advanced/custom path's software choice, or a secondary toggle inside Crossplay ("Bedrock players only, no Java client support") — a v2 implementation-phase UX decision, flagged here as **[U]** pending a look at real usage signal once telemetry-free usage patterns are understood (ChunkPilot has no telemetry per `AGENTS.md`, so this will be a judgment call, not data-driven).

## 4. Does persisted data depend on preset identifiers?

**[V]** No. `quick_start_presets` **table exists in the SQLite schema** (`src/ChunkPilot.Infrastructure/ChunkPilotStore.cs:168-173`, columns `id, server_id, json, created_utc`) **but has zero read or write call sites anywhere in the repository** (confirmed by exhaustive grep across `src/`). It is dead schema — created, never populated, never read. `QuickStartKind`/`QuickStartPreset` values are never written to `ServerDefinition`, `server_running_state`, or any other persisted record; they exist only transiently inside `InstallServerViewModel` during the creation dialog's lifetime and are discarded the moment `ServerInstallRequest` is built (the request carries only the *resolved* fields — `SourceType`, `InitialProperties`, `EnableDailyBackup`, etc. — never a preset identifier).

**Consequence for v2:** **there is no backward-compatibility or migration burden for preset identifiers.** No existing server's behavior, no database row, and no test depends on `QuickStartKind` values persisting or being stable. The `quick_start_presets` table can be left alone (harmless, `CREATE TABLE IF NOT EXISTS` is additive and non-destructive per `docs/ARCHITECTURE.md`) or given a real purpose later (e.g. recording which intent path produced a given server, for support/diagnostics) — that is a v2 implementation-phase option, not a requirement.

## 5. What must be preserved vs. what can be freely changed

| Preserve unchanged | Free to change |
|---|---|
| `QuickStartKind` enum values and `QuickStartPresetFactory` switch bodies (domain defaults: whitelist policy, backup policy, seeded `server.properties`, managed-Java default) — `ChunkPilot.Core` | The single flat `ComboBox` presentation of quick-starts in `InstallServerWindow.xaml` |
| `ServerInstallRequest`, `InstallOperationRequest`, `InstallOperationSnapshot`, `InstallProgress`, `InstallationResult` contracts (`ChunkPilot.Core/Models.cs`) — the Agent pipeline consumes these exactly as-is | `InstallServerViewModel` itself (the whole class is App-layer presentation logic, not a contract) |
| `CatalogQuery`, `CatalogItem`, `CatalogVersion`, `CatalogProvider`, `CatalogProviderStatus`, `CatalogPolicy` (`ChunkPilot.Core/GuidedPlatformModels.cs`) | `InstallServerWindow.xaml`/`.xaml.cs` entirely (replace) |
| `GuidedCatalogService`, `ModrinthCatalogProvider`, `CurseForgeCatalogProvider`, `BuiltInServerCatalogProvider`, `LoaderMetadataService`, `LoaderInstallationService`, `ManagedJavaRuntimeService`, `AdoptiumTemurinProvider`, `ManagedServerInstaller`, `CrossplayServices` (`ChunkPilot.Infrastructure`) — the entire transactional/provider backend | The `"QuickStartPresets"` agent pipe operation (`AgentPipeServer.cs:243-246`) — currently dead code, the App never calls it (`InstallServerViewModel` calls `QuickStartPresetFactory.Create` directly, client-side); either wire it up for real or remove it during v2 implementation |
| Agent/Core/Infrastructure layering, named-pipe transport, transactional staging (`docs/ARCHITECTURE.md` "Managed installation transaction") | Which fields the wizard exposes by default vs. behind Advanced |
| EULA write/verify logic in `ManagedServerInstaller.cs:253-292,524,595` (writes `eula.txt` only when `EulaAccepted && EulaAcceptedAt` present, re-verifies before activation, records timestamp+URL) | The EULA step's visual presentation and copy |

## 6. Summary disposition table

| Item | Kind | Disposition |
|---|---|---|
| `QuickStartKind`/`QuickStartPreset`/`QuickStartPresetFactory` | Domain default bundle | **Keep**, stop exposing as a raw dropdown; drive from intent selection |
| Flat `InstallSourceType` combo box as primary choice | UI-only | **Remove** from normal flow; retained only inside Advanced/custom |
| `ImportExistingServer` quick-start | Redundant/confusing | **Remove from wizard's selectable UI**; keep enum member if cheap, otherwise retire in implementation phase |
| `AdvancedCustomServer` quick-start | Undersold | **Promote** to its own top-level path, not a list item |
| `BedrockDedicatedServer` quick-start | Narrow scenario | **Fold into Crossplay or Advanced/custom**, not a 7th top-level tile |
| `quick_start_presets` SQL table | Dead schema | **Leave alone** (harmless); optionally repurpose later |
| `"QuickStartPresets"` agent pipe op | Dead code | **Wire up or remove** during implementation (not user-visible either way) |
| Provider adapters, loader metadata/install services, managed Java, crossplay services | Backend, already correct | **Keep entirely**; v2 is a consumer, not a rewrite |
