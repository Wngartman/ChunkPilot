# Create Server v2 architecture

Status: planning document. No production behavior changed by this document. Companion documents: `CREATE-SERVER-V2-PRESET-INVENTORY.md`, `CREATE-SERVER-V2-ROADMAP.md`, `CREATE-SERVER-V2-RISK-REGISTER.md`, `CREATE-SERVER-V2-PROVIDER-RESEARCH.md`.

Evidence key: **[V]** verified in this repository. **[E]** current official external fact (see provider research notes for sources). **[I]** inference from verified evidence. **[U]** unknown, requires validation during implementation.

## 0. The central finding: most of the transactional pipeline already exists

**[V]** `docs/ARCHITECTURE.md` ("Managed installation transaction") and direct inspection of `src/ChunkPilot.Infrastructure/ManagedServerInstaller.cs` (701 lines) and `src/ChunkPilot.Agent/AgentPipeServer.cs` show that ChunkPilot already implements, end-to-end, in the Agent/Infrastructure layers:

- an operation-ID-keyed staging directory (`.chunkpilot-staging-<operation>`), same-volume as the destination;
- ZIP-entry path-traversal rejection during extraction;
- official-hash verification when the provider or catalog supplies one (`ExpectedSha1/256/512` on `ServerInstallRequest`);
- launch-detection/required-file validation before promotion;
- atomic `Directory.Move` promotion into the managed root;
- EULA gating (`eula.txt` written only after explicit `EulaAccepted`+`EulaAcceptedAt`, re-verified before activation, timestamp+URL recorded in SQLite — `ManagedServerInstaller.cs:253-292,524,595`);
- registration in SQLite only after promotion succeeds;
- cancellation via `CancelInstall`/`operationId`;
- crash/interruption recovery patterns proven out for the sibling "server-pack update" transaction (same document, "Server-pack update transaction" section) that the create path shares infrastructure with (`CanonicalPathLockManager`, operation journaling patterns).

**This means Create Server v2 is fundamentally a new App-layer (WPF) front end and a small number of Agent/Infrastructure gap-fills, not a from-scratch transactional rewrite.** The assignment's 17-phase pipeline (§ below) is mapped against what exists so the roadmap does not re-implement working, tested code.

| Assignment phase | Status | Where |
|---|---|---|
| 1. Capture user intent | **New** (App layer) | New wizard state model (§2) |
| 2. Resolve available options | **Exists**, needs a UI consumer | `GuidedCatalogService`, `LoaderMetadataService`, `InstallVersionsRequest`/`ServerDownloadCatalog` (`AgentPipeServer.cs:236-246`) |
| 3. Resolve compatibility | **Partially exists** (`CatalogPolicy.Filter`, `ClientRequirement`, `InstallationSupportState`), needs an explicit evidence model surfaced to the UI | `GuidedPlatformModels.cs:41-67, 571-607` (§3 below) |
| 4. Build deterministic creation plan | **Exists** for loaders (`LoaderInstallPlan`/`LoaderMetadataService`) and catalog versions (`CatalogVersion`); **new** unifying `CreationPlan` needed at the App/wizard boundary | §2 |
| 5. Human-readable review | **New** (App layer); `QuickStartPreset.ReviewItems` already models "plain-language bullet list" and should be reused as the pattern | `GuidedPlatformModels.cs:333` |
| 6. Validate destination/prerequisites | **Implemented** — `CreationDestinationPolicy` is the single deterministic answer, re-run immediately before promotion | §12 |
| 7. Obtain required user decisions | **New** (App layer: EULA, CurseForge key, overwrite/rename) | §7 |
| 8. Stage downloads/generated files | **Exists** | `ManagedServerInstaller.cs` staging |
| 9. Verify integrity/compatibility | **Exists** (hash verification); compatibility evidence surfaced to user is **new** | §3 |
| 10. Prepare runtime | **Exists** | `ManagedJavaRuntimeService`, `JavaRuntimePolicy` |
| 11. Prepare configuration | **Exists** (`InitialProperties` → `server.properties`) | `ManagedServerInstaller.cs` |
| 12. EULA acceptance | **Exists** at the data-safety level; **new** UI step | §7 |
| 13. Atomic activation | **Exists** (`Directory.Move`) | `ManagedServerInstaller.cs` |
| 14. Register with ChunkPilot | **Implemented inside the transaction** — it used to happen in the Agent coordinator *after* the installer had already reported success | §12 |
| 15. Verify registered result | **Implemented** — `ServerCreationTransaction.VerifyAsync` reads the record back and checks it against the activated folder before success is reported | §12 |
| 16. Clean up staging | **Exists** | `ManagedServerInstaller.cs` |
| 17. Roll back/recover after failure | **Implemented** — `ServerCreationRecoveryService` reconciles every durable checkpoint at Agent startup, before the server list is read | §12 |

**Consequence:** the architecture below spends most of its detail on the **new App-layer wizard** (state model, UI composition, provider consumption) and on the **specific, named gaps** in the existing backend, rather than re-specifying transactional mechanics that already work and are already tested by `LoaderAndJavaFixtureIntegrationTests.cs`.

## 1. Layering (unchanged, per `AGENTS.md`/`docs/ARCHITECTURE.md`)

```
ChunkPilot.App (WPF)          — new wizard Window + ViewModels. No provider networking, no loader logic.
ChunkPilot.Core               — new wizard state records (pure data), reuses existing contracts.
ChunkPilot.Infrastructure      — existing providers/services; gap-fills only (see §8, §9).
ChunkPilot.Agent               — existing pipe operations reused as-is; new operations only where a genuine
                                  gap exists (e.g. a single "resolve compatibility" call, if the App-layer
                                  planner needs data the existing operations don't already return together).
```

**[I]** The new wizard should remain a **modal `Window`** (matching `InstallServerWindow`'s and `ImportServerWindow`'s existing pattern), not a `NavigationService` page hosted inside `MainWindow`'s `ServerPageHost`/global-page `ScrollViewer`. Reasons: (1) creation is a bounded task-flow with its own back/next/create/cancel lifecycle, not a browsable destination; (2) keeping it a separate `Window` means zero risk to `NavigationService`, `ServerOpened`, or navigation version guards — all explicitly protected; (3) `MainViewModel.InstallServerAsync()`'s existing integration contract (`new InstallServerViewModel(...)`, `ShowDialog()`, read `viewModel.Result`) can be preserved almost verbatim, swapping only the concrete ViewModel/Window types.

## 2. Creation state model

The assignment warns against "a single oversized view model containing every path's unrelated fields." The current `InstallServerViewModel` (379 lines, ~30 observable properties, one method per source type) is already exhibiting this problem. The v2 model splits along the boundary between **what the user is deciding** and **what has been resolved for them**.

### 2.1 Domain records (`ChunkPilot.Core`, new file e.g. `CreationWizardModels.cs`)

Pure data, no UI, no networking — mirrors the existing style of `GuidedPlatformModels.cs`.

```csharp
// The six top-level intents. Distinct from InstallSourceType (an implementation detail) and
// from QuickStartKind (a domain-default bundle). CreationIntent is what the user actually clicked.
public enum CreationIntent { Vanilla, Plugins, Mods, Modpack, Crossplay, Advanced }

// One step's worth of user input, immutable, appended to as the wizard advances.
public sealed record CreationSelection
{
    public CreationIntent Intent { get; init; }
    public string MinecraftVersion { get; init; } = "";
    public InstallSourceType? SoftwareChoice { get; init; }        // resolved from Intent, editable in Advanced
    public string LoaderVersion { get; init; } = "";
    public CatalogProvider? ModpackProvider { get; init; }
    public string ModpackProjectId { get; init; } = "";
    public string ModpackVersionId { get; init; } = "";
    public string ServerName { get; init; } = "";
    public string IconPath { get; init; } = "";
    public bool CrossplayEnabled { get; init; }
    public AdvancedOverrides? Advanced { get; init; }               // null unless the user opened Advanced
}

// Only populated when the user has opened Advanced/custom, or overridden a resolved default
// from within another intent. Never populated implicitly.
public sealed record AdvancedOverrides
{
    public string CustomExecutableOrJarPath { get; init; } = "";
    public string JavaPath { get; init; } = "";
    public string LaunchArguments { get; init; } = "";
    public string ServerDirectory { get; init; } = "";
    public int? MinimumRamMb { get; init; }
    public int? MaximumRamMb { get; init; }
    public int? Port { get; init; }
    public IReadOnlyDictionary<string, string> AdditionalProperties { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

// Everything the system resolved on the user's behalf, kept separate from what they typed.
public sealed record ResolvedCreationContext
{
    public IReadOnlyList<string> AvailableMinecraftVersions { get; init; } = [];
    public IReadOnlyList<CatalogVersion> AvailableSoftwareVersions { get; init; } = [];
    public LoaderInstallPlan? LoaderPlan { get; init; }
    public ManagedJavaRuntime? ResolvedJavaRuntime { get; init; }
    public CompatibilityEvidence Compatibility { get; init; } = new();
    public IReadOnlyList<CatalogProviderStatus> ProviderStatuses { get; init; } = [];
}

// The deterministic plan, built once all required decisions exist. This is what the Review
// screen renders and what BuildInstallRequest() below turns 1:1 into ServerInstallRequest.
public sealed record CreationPlan
{
    public CreationSelection Selection { get; init; } = new();
    public ResolvedCreationContext Context { get; init; } = new();
    public IReadOnlyList<string> ReviewSummary { get; init; } = [];      // plain language, like QuickStartPreset.ReviewItems
    public IReadOnlyList<CreationValidationIssue> Issues { get; init; } = [];
    public bool CanProceed { get; init; }

    // The only place CreationPlan touches an existing contract: a pure, deterministic mapping.
    public ServerInstallRequest BuildInstallRequest() => new() { /* map fields 1:1 */ };
}

public enum CreationIssueSeverity { Blocking, Warning, Info }
public sealed record CreationValidationIssue(CreationIssueSeverity Severity, string Message, string? Field = null);
```

**Why this shape:** `CreationSelection` is what the wizard's `Back` button needs to restore; `ResolvedCreationContext` is what a provider call fills in and what gets invalidated/re-resolved when the user changes an earlier answer; `CreationPlan` is the single object the Review screen and the transaction executor both read, and it has one, tested, pure method (`BuildInstallRequest`) that is the entire coupling point to the existing Agent contract. No other part of the new code should construct a `ServerInstallRequest` directly.

### 2.2 App-layer wizard state (`ChunkPilot.App`, new files replacing `InstallServerViewModel.cs`)

Split by responsibility, not by source type:

- **`CreationWizardViewModel`** — owns the step sequence (`IntentSelection → Details → Review → Progress → Completion/Failure`), `Back`/`Next`/`Create`/`Cancel` commands, and holds one `CreationSelection` + one `ResolvedCreationContext` + one `CreationPlan?`. It does not know provider-specific field layouts.
- **One step ViewModel per intent's Details screen** (`VanillaDetailsViewModel`, `PluginsDetailsViewModel`, `ModsDetailsViewModel`, `ModpackDetailsViewModel`, `CrossplayDetailsViewModel`, `AdvancedDetailsViewModel`), each exposing only the properties relevant to that intent, each producing a partial `CreationSelection` update. This directly answers the assignment's "avoid a single oversized view model."
- **`CreationPlanner`** (App-layer service, not a ViewModel) — pure orchestration: given a `CreationSelection`, calls the existing agent operations (`BrowseCatalog`, `InstallVersions`, `ManagedJavaRuntimes`, a loader-plan lookup) and produces `ResolvedCreationContext` + `CreationPlan`. Testable without any WPF dependency (constructor takes `IAgentClient`, same pattern as `InstallServerViewModel` today).
- **`CreationTransactionRunner`** — thin wrapper around the existing `BeginInstall`/`InstallProgress`/`CancelInstall` polling loop (`InstallServerViewModel.cs:271-361` already has correct, working logic here — **carry it forward almost unchanged**, just decoupled from the mega-ViewModel).

### 2.3 What stays a per-server *setting* rather than a creation-time choice

**[I]** Per `AGENTS.md`'s "Chameleon UI" and the assignment's advanced-mode strategy: RCON/query toggles, whitelist management (beyond the initial on/off), gamerule editing, resource-pack URL, datapack management, and network/tunnel configuration are **existing per-server Settings/Access/Protection features already built** (`ServerAccessPage.xaml`, `ServerProtectionPage.xaml`, `NetworkConfigurations`/`TunnelProviders` tables per `ChunkPilotStore.cs`). Creation should seed sane defaults (as `QuickStartPreset.Properties` already does) and never duplicate a control that already exists post-creation. This is a hard boundary: **if a field already has a home in Settings/Access/Protection, Create Server only sets its initial value and never re-implements its UI.**

## 3. Compatibility model

**[V]** The domain already has the right *shape* of enums (`InstallationSupportState`, `ClientRequirement`) but they are coarser than the assignment's required distinctions and are not surfaced to the user anywhere in the current UI (`InstallServerWindow.xaml` never reads `InstallationSupport` or `ClientRequirement`).

**New, additive `ChunkPilot.Core` type** (does not replace `InstallationSupportState`, which stays for its current job of filtering catalog results in `CatalogPolicy.Filter`):

```csharp
public enum CompatibilityConclusion
{
    VerifiedCompatible,
    VerifiedIncompatible,
    ProviderDeclaredCompatible,     // provider says yes; ChunkPilot did not independently verify
    Inferred,                        // ChunkPilot derived this from version/loader matching heuristics
    Unknown,
    TemporarilyUnavailable,          // provider outage/rate limit; had cache or nothing
    UnsupportedByChunkPilot,          // e.g. FTB with no documented public API — see PROVIDERS.md
    RequiresUserSuppliedArtifact,     // e.g. CurseForge modpack with client-only pack, needs manual server pack
    RequiresAuthentication,           // e.g. CurseForge with no API key configured yet
    NoServerPackAvailable
}

public sealed record CompatibilityEvidence
{
    public CompatibilityConclusion Conclusion { get; init; } = CompatibilityConclusion.Unknown;
    public string MinecraftVersion { get; init; } = "";
    public string Loader { get; init; } = "";
    public string LoaderVersion { get; init; } = "";
    public int? RequiredJavaMajor { get; init; }
    public string ServerArtifactSource { get; init; } = "";   // e.g. "Modrinth official API", "CurseForge (user key)"
    public bool ServerPackAvailable { get; init; }
    public ClientRequirement ClientRequirement { get; init; } = ClientRequirement.Unknown;
    public DateTimeOffset? ProviderDataAsOf { get; init; }
    public string HashAlgorithm { get; init; } = "";           // "SHA1"/"SHA256"/"SHA512"/"" if none supplied
    public string HashValue { get; init; } = "";
    public IReadOnlyList<string> Assumptions { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
```

**Rule, non-negotiable per `AGENTS.md` principle 4 ("Truth over confidence theater"):** every `CatalogItem`/`CatalogVersion` the wizard shows must be paired with a `CompatibilityEvidence` whose `Conclusion` is never silently defaulted to `VerifiedCompatible`. Where the current `ModrinthCatalogProvider`/`CurseForgeCatalogProvider` return `HasServerPackage: true`, that maps to `ProviderDeclaredCompatible` at best (Modrinth/CurseForge assert a server file exists; ChunkPilot has not independently started it), **not** `VerifiedCompatible`. `VerifiedCompatible` is reserved for cases ChunkPilot's own code checked directly — e.g. the official Mojang/Paper/Purpur/Fabric/NeoForge/Forge paths where `LoaderMetadataService`/`ServerDownloadCatalog` resolve a known-good, ChunkPilot-authored install plan against official metadata with a verified hash.

**Do not build a universal compatibility database.** **[I]** The repository has no such store today (no "compatibility_matrix" table, no offline dataset). `CompatibilityEvidence` is computed per-query, per-session, from the same provider responses the catalog browse already returns, cached the same way (`GuidedCatalogService`'s existing 6-hour cache, §9). Building a persistent, curated, cross-version compatibility matrix is out of scope for v2 and is not requested by the assignment beyond per-item evidence.

## 4. Provider architecture

See `CREATE-SERVER-V2-PROVIDER-RESEARCH.md` for the detailed, source-cited breakdown. Summary for the architecture:

| Provider | Existing adapter | Wizard consumption plan |
|---|---|---|
| Mojang (vanilla) | `BuiltInServerCatalogProvider`/`ServerDownloadCatalog` **[V]** | Vanilla intent's version list, direct |
| Paper/Purpur | `BuiltInServerCatalogProvider` **[V]** | Plugins intent's software choice |
| Fabric/Quilt/Forge/NeoForge | `LoaderMetadataService` **[V]** | Mods intent's loader + version pickers |
| Modrinth | `ModrinthCatalogProvider` **[V]**, always-on, no auth | Modpack intent's default catalog tab |
| CurseForge | `CurseForgeCatalogProvider` **[V]**, gated on user-supplied DPAPI-encrypted key | Modpack intent's second tab, hidden/disabled with an explanation until a key is entered — new **Settings-level** key-entry UI is required (does not exist today; see roadmap task 12) |
| Geyser/Floodgate | `ICrossplayPackageProvider`/`OfficialCrossplayPackageProvider` **[V]** | Crossplay intent |
| Adoptium Temurin (Java) | `AdoptiumTemurinProvider`/`ManagedJavaRuntimeService` **[V]** | All intents' runtime resolution, invisible unless Advanced |
| FTB | **[V]** documented as unavailable, no scraping fallback (`docs/PROVIDERS.md`) | Not offered; if selected via a manifest/Advanced path, surfaced as `UnsupportedByChunkPilot` |
| Direct HTTPS manifest | `DirectManifest` adapter referenced in `docs/UPDATE-MANIFEST.md` — **[U]** confirm this is create-path-reachable vs. update-only during implementation | Advanced/custom only |

**No new provider dependency, package, or outbound call is added by this planning task.**

## 5. Java/runtime strategy

**[V]** Already fully implemented (`ManagedJavaRuntimeService`, `AdoptiumTemurinProvider`, `JavaRuntimePolicy` in `GuidedPlatformModels.cs:646+`, documented in `docs/MANAGED-JAVA.md`): required major version derived from Minecraft version thresholds (1.20.5+→21, 1.18–1.20.4→17, 1.17→16, older→8, 32-bit rejected), resolution order is explicit-reviewed-runtime → package/loader evidence → JAR class-file version → Minecraft version, offline behavior falls back to existing installed runtimes, never touches system `PATH`/`JAVA_HOME`. The wizard's only job is to **not show any of this by default** and to surface the resolved runtime (vendor, major version) as one read-only line on the Review screen, with an Advanced-only override to pick a different installed runtime or browse to a user-supplied `java.exe` (the existing `BrowseJavaCommand`/`JavaPath` fields already do this — carry forward into `AdvancedDetailsViewModel`).

## 6. Destination collision, staging, and cancellation

**[V]** `InstanceRoot` defaults to `%USERPROFILE%\ChunkPilot\Servers` (`InstallServerViewModel.cs:19`); staging uses a same-volume `.chunkpilot-staging-<operation>` directory (`docs/ARCHITECTURE.md`). **[U] Gap identified, not yet verified in code:** what happens today if `ServerName`/`InstanceRoot` collides with an existing folder — does `ManagedServerInstaller` reject, rename, or overwrite? This must be read and, if necessary, hardened during the "Transaction/staging foundation" roadmap task, **before** any UI work assumes a particular collision behavior. The wizard's Review screen must show the exact destination path and, if a collision is detected, block `Create` with a clear message rather than silently suffixing a number (silent renaming would violate "truthful state").

**Cancellation points [I], to be confirmed against `ManagedServerInstaller.cs` during implementation:** cancellable during `Downloading`/`Extracting` (per existing `InstallState` enum: `Planned, Staging, Downloading, Extracting, Installing, Validating, Finalizing, Completed, Cancelled, ...`); **non-cancellable once `Finalizing` begins** (the atomic directory-move window), consistent with `docs/ARCHITECTURE.md`'s "cancellation is honored before activation... ChunkPilot finishes transaction finalization... before returning control."

## 7. EULA handling (already compliant; wizard adds only the UI step)

**[V]** No change needed to `ManagedServerInstaller.cs`'s EULA logic (§0 above). The wizard's Review screen requires an **unchecked-by-default** checkbox bound the same way `InstallServerWindow.xaml:110-111` already does it (`IsChecked="{Binding EulaAccepted}"`), with a link to the official EULA (`https://www.minecraft.net/eula`, already wired via `OpenEulaCommand`). `Create` remains disabled until checked (`CanInstall()` already requires `EulaAccepted`, carry forward). Cancelling before acceptance: no side effects (nothing written). Cancelling after acceptance but before `Create`: nothing written either — `eula.txt` is only written during staging, which only happens after `BeginInstall` is actually called.

## 8. UI architecture

Layout resembling the assignment's model, built entirely from the **existing 5f19531 design system** — no new visual language:

```
┌─────────────────────────────────────────────────────────────────────┐
│ AppPageHeader-style title strip: "Create a server"  [Back] [Next/Create] │
├───────────┬───────────────────────────────────┬─────────────────────┤
│ Intent    │  Center: version/project/catalog   │ Right (width       │
│ selector  │  content for the selected intent   │ permitting):        │
│ (left,    │  (search box + AppCard list, reuse │ compatibility       │
│ compact,  │  AppServerRow-style rows for        │ evidence / details  │
│ icon+text │  catalog items)                     │ pane                │
│ per       │                                     │                     │
│ AppNavig- │                                     │                     │
│ ationRow) │                                     │                     │
├───────────┴───────────────────────────────────┴─────────────────────┤
│ Server name + icon identity row (near top per assignment; shown once│
│ intent+version are chosen, using AppCard + existing icon picker)     │
└───────────────────────────────────────────────────────────────────────┘
```

- **Reuse, do not reinvent:** `AppPageHeader` (title/description/primary+secondary actions), `AppCard`/`AppSectionCard` (grouping), `AppServerRow` (catalog/version list rows — it already supports name/subtitle/state-text/tone/trailing-content, which maps directly onto "project name / provider+category / server-pack-status badge / select action"), `AppSearchBox`, `AppStatusBadge` (compatibility-conclusion badges, one `Tone` per `CompatibilityConclusion` bucket), `AppEmptyState` (no search results, provider unavailable, no server pack), `AppAlert` (blocking validation issues on Review), `WrapPanel`-based toolbar wrapping pattern established on the Servers page (`MainWindow.xaml`, post-5f19531).
- **Responsive:** follow `docs/UI-RESPONSIVE-RULES.md`'s existing compact-mode breakpoints (`AppLayout.Mode`); at narrow widths the three-column layout above collapses to intent-selector-on-top (or a `ComboBox`) + center content, matching the same `Compact` `DataTrigger` pattern already used by `AppNavigationRail`/`AppPageHeader`.
- **Back/Next/Create:** stable position in the title strip (top-right), never in the scrollable body — mirrors the already-fixed `AppPageHeader.PrimaryContent`/`SecondaryContent` slots.
- **Accessibility:** every catalog row needs `AutomationProperties.Name` (pattern already used throughout `MainWindow.xaml`'s server rows); icon-only actions need tooltips (existing `AppIconButton` convention); focus rings via existing `InternalFocusRing`/`AppFocusRing` — no new mechanism. High Contrast: reuse `Themes/Overlays/HighContrast.xaml` overrides as-is (no new brushes needed since the wizard uses only existing tokens). Reduced Motion: any progress-screen animation must check `ds:AppMotion.IsEnabled` exactly like `AppProgressBar`'s indeterminate pulse already does (`Themes/Controls/DataDisplay.xaml`).
- **Loading/offline/provider-unavailable/empty states:** `CatalogProviderStatuses` (existing agent op, `AgentPipeServer.cs:255-256`) already returns per-provider `Available`/`Detail` — the wizard's job is to render that truthfully (e.g. CurseForge tab shows `AppEmptyState` "Add a CurseForge API key in Settings to browse modpacks here" when `IsAvailable` is false), not to invent a fallback catalog.

## 9. Modpack catalog design

Two provider tabs (Modrinth default/always-on, CurseForge second/gated), sharing one `AppServerRow`-based result list and one details pane:

- **List row:** icon (`CatalogItem.IconUrl`, lazy-loaded `Image` with a placeholder — **[I]** no image-loading service exists yet in `ChunkPilot.App`; this is new, small, and should use a bounded in-memory/disk cache to respect "no continuous polling" and provider rate limits), name, author, one-line summary, categories (as small badges), a **server-pack status badge** driven directly by `CompatibilityEvidence.ServerPackAvailable`/`Conclusion` (three visually distinct states per the assignment: "Server pack available," "Client only — no server package," "Needs review"), last-updated date.
- **Details pane (right column):** full description, supported Minecraft versions, loader, release channel selector, exact-version list (`CatalogVersion`), required Java major, download size if known, provider attribution line ("via Modrinth official API" / "via CurseForge, using your API key").
- **Truthful unavailable/unsupported states, explicit, no invented conversion:** if `InstallationSupport == ClientOnly` (already computed by `CurseForgeCatalogProvider.BrowseAsync`, `GuidedPlatformServices.cs:640-642`), the details pane shows "This is a client-only modpack. ChunkPilot cannot install it as a dedicated server" and disables selection — **never** offers a fake "convert to server" action, per the assignment's explicit prohibition.
- **Version mismatch:** if the user already picked a Minecraft version/loader before opening the modpack catalog (possible if they came from Mods intent and pivot), versions that don't match are shown but visually de-emphasized with an explicit "Doesn't match your selected Minecraft 1.21.1 / Fabric" caption rather than hidden — truthful, not silently filtered, unless the user explicitly filters.
- **CurseForge auth state:** entering a key does not happen inside the creation wizard (that would leak a "technical field" into the normal flow) — it happens once, in global Settings, exactly as `docs/ARCHITECTURE.md`'s "Provider secrets are kept outside SQLite... encrypted with DPAPI" already assumes a settings surface exists for it. **[U] Verify during implementation** whether a CurseForge-key settings control already exists anywhere in `ServerSettingsPage.xaml`/global Settings; initial grep found no such UI, so this is very likely a small new Settings addition, not a wizard addition.

## 10. Advanced-mode strategy

- **Fields shown:** exactly the current `InstallServerWindow.xaml` technical fields (`InstallSourceType`, source path/URL, Java path, launch/RAM/port/max-players), relocated wholesale into `AdvancedDetailsViewModel`/an Advanced-only screen. Nothing new needs inventing here — it is a **move**, not a redesign, of already-working bindings.
- **Overrides invalidate compatibility guarantees:** any field the user edits in Advanced that a resolved `CompatibilityEvidence` was based on (Java path, loader version, source URL) demotes that evidence's `Conclusion` to `Unknown` with a `Warnings` entry explaining why — implemented as a pure function in `CreationPlanner`, not scattered UI logic.
- **Review page marks unverified choices:** any `CompatibilityEvidence.Conclusion` other than `VerifiedCompatible`/`ProviderDeclaredCompatible` renders with `AppAlert` (Tone=Warning or Danger depending on severity), never silently.
- **Reusable advanced templates:** **[I]** not required by any existing persistence and not requested as a v2.0 must-have by the assignment beyond "whether users may save reusable advanced templates" — recommend **deferring** this to a later phase; if built, it is a natural extension of the existing (currently dead) `quick_start_presets` table, giving it a real purpose without requiring schema changes.
- **Old presets → reusable templates:** no migration needed (§4 of the preset inventory doc — nothing persists `QuickStartKind` today), so "transitioning" is purely a UI-vocabulary change, not a data migration.
- **Unsafe destination/launch constraints:** Advanced must still go through the same staging/atomic-activation transaction as every other path (assignment: "Advanced/custom must not bypass transactional creation or data-safety rules") — this is already structurally guaranteed because `BeginInstall`/`ServerInstallRequest` is the **only** entry point regardless of intent; Advanced cannot skip it because there is no other Agent operation that creates a managed server.
- **Per-server settings vs. creation-time choices:** per §2.3 above.

## 12. Creation transaction and recovery (implemented)

Status: **implemented and tested** by the transaction-hardening milestone. This section describes shipped behaviour, not a plan. Live Vanilla creation and the wizard's production wiring remain unbuilt.

### 12.1 What was actually wrong

`ManagedServerInstaller.InstallAsync` promoted the staging directory, wrote EULA and history rows for a server id that had no server row, deleted its journal row and reported `Completed`. `InstallationCoordinator` then called `supervisor.ImportAsync`, which is where registration really happened. A crash, a forced close or a failed write anywhere in that window left an activated managed directory, no server record, and no journal evidence that anything had been started — and startup recovery only ever looked at `"ServerPackUpdate"` operations, so nothing reconciled it.

### 12.2 Shape

```
ManagedServerInstaller   collects the files (unchanged) and hands them over as a candidate
ServerCreationTransaction   destination policy → staging → verify → promote → register → verify → clean up
ServerCreationRecoveryService   reconciles unfinished journal entries at Agent startup
```

There is no second installer. Downloading, extracting, hash verification and loader installation are untouched; what moved is everything that happens *after* a candidate exists.

### 12.3 Phases and durable evidence

`CreationPhase` has eighteen members and `CreationPhasePolicy` owns their rules: which may follow which, where cancelling is safe, which two form the critical section, and whether a server may appear in the library (only `Completed` and `CleanupPending`, and `CleanupPending` is reached only after verification passed).

Each durable side effect is bracketed: the journal records that it is about to happen, the effect runs, and a second write records that it did. `Phase` says where the operation believed it was; the separate `ActivationBegan`/`ActivationCompleted`/`RegistrationBegan`/`RegistrationCompleted`/`VerificationPassed` flags say what is known to have happened, and recovery trusts the flags.

### 12.4 Destination policy

`CreationDestinationPolicy.Evaluate` is the one answer, and it is re-run immediately before promotion rather than only at the start. It refuses: a non-empty folder, a file on the path, a junction or symlink, a folder registered to a managed *or* imported server, a folder inside or containing a known server, a folder another unfinished creation owns, and any overlap with the operation's own staging. An absent folder and a provably empty folder are accepted. Comparison is canonical and case-insensitive and ignores trailing separators, so a registered server whose directory was deleted still owns its path — the old "does the directory exist" check missed exactly that.

`CreationPathSafety` is the shared canonical-path vocabulary; `ManagedServerInstaller` delegates its containment check to it rather than keeping a second implementation.

### 12.5 Activation

Same volume uses one `Directory.Move`, which the filesystem makes atomic. Across volumes there is no such primitive, so the candidate is copied to a sibling landing directory, checked for its ownership marker, and only then renamed into place; the journal records `ActivationMode = StagedCopy` and the guarantee is stated as coming from the marker and the checkpoints, not from the filesystem. Calling that atomic would be untrue. In practice staging is always created beside the destination, so the cross-volume branch is defensive.

An ownership marker (`.chunkpilot-creation.json`, holding the operation id, server id and canonical destination) is written into the candidate before promotion, travels with it, and is removed during cleanup. Nothing deletes or takes over a directory that does not carry a matching marker.

### 12.6 Registration and verification

Registration happens inside the transaction, server row first so the acceptance and history rows never reference an id with no server. Verification then reads back: the folder exists and resolves to the expected path, it carries the marker, the server appears exactly once, its stored path matches, it is marked managed, its name, software and Minecraft version match the plan, and no other server claims the folder. A write returning without an exception is not treated as evidence.

If registration fails after promotion, the transaction rolls the promotion back — but only when the folder provably carries this operation's marker and the staging path is free. If it cannot prove that, it preserves everything and reports `RecoveryRequired`. If verification fails after registration, nothing is deleted at all.

### 12.7 Recovery

`Program.cs` runs `ServerCreationRecoveryService.RecoverAsync()` before `ServerSupervisor.InitializeAsync()`. Per entry, keyed on the evidence flags:

| Evidence | Behaviour |
| --- | --- |
| Activation never began | Remove operation-owned staging; the destination is untouched; close the journal |
| Activation began, outcome unknown | Read the marker: destination-owned → finish; staging-owned → discard staging; neither or foreign → change nothing, report attention |
| Activated, not registered | Register from the plan recorded at the last safe checkpoint, then verify |
| Registered, not verified | Verify; pass → finish, fail → change nothing, report attention |
| Verified, cleanup outstanding | Retry only the marker and the owned staging folder; never touch the server |

Idempotent by construction: each pass re-derives from the same evidence, and registration is an upsert keyed by the server id fixed before activation, so repeating it cannot produce a second server. Attempts are bounded at three, after which the entry is preserved and reported rather than retried on every start. A journal row this build cannot read — corrupt, or written by a newer schema — is never acted on and still reserves its destination.

### 12.8 Cleanup

Cleanup deletes exactly two things: the ownership marker in the destination, and the staging directory, and only when that directory is both named for this operation and under the recorded instance root. A cleanup failure never demotes a verified creation: the outcome becomes `CompletedWithCleanupWarning`, the journal stays at `CleanupPending`, and a later pass retries the files without touching the server.

### 12.9 Persistence

One additive change: a `creation_journal` table (`CREATE TABLE IF NOT EXISTS`, plus an index on the destination) and `PRAGMA user_version` 4 → 5. No existing table was altered and no data was migrated. The row stores identity, canonical paths, timestamps, the phase, the evidence flags, the outcome, the recovery attempt count and the planned `ServerDefinition` — the same record the `servers` table already holds, which is what makes a post-activation interruption resumable rather than a dead end. No secrets, credentials or payloads.

## 13. Live Vanilla creation (implemented)

Status: **implemented, tested, and authoritative for normal beginner Vanilla creation**. Every other intent remains unbuilt and is not exposed by this route.

### 13.1 Two switches, deliberately different

| Switch | What it is | Startup | Data | Side effects |
|---|---|---|---|---|
| `--create-server-v2-preview` | Design review | **Replaces** startup: no lock, no tray, no agent, no database | Invented, compiled into the binary | None. It holds nothing that could install anything |
| `--create-server-v2-live-vanilla` | The real thing, Vanilla only | **Follows** startup: the whole normal shell runs first | Official Mojang metadata through the Agent | Downloads, a managed Java runtime, a registered server |

The ordering is the whole difference. The preview must be unable to disturb anything, so it runs before the single-instance lock. The live wizard needs the Agent that owns the work, the database the server is registered in and the navigation the finished server is opened through, so it runs after all of them and opens one extra window over the shell. The live switch is retained as a development shortcut into the same shell composition used by every normal **Create server** action. The preview remains unreachable from product controls.

The product seam is semantic: `MainViewModel.CreateVanillaServerCommand` raises a Vanilla creation request and the shell composes the current WPF presentation, real Agent gateway, location chooser and completion navigator. The workflow is therefore authoritative without making its interim window a permanent shell architecture decision. `InstallServerWindow` is retained only while its broader advanced fields await an explicit product home; no normal beginner Vanilla route constructs it. **Add existing server** remains the separate by-reference import flow.

Review the live wizard against an isolated `CHUNKPILOT_DATA_ROOT` **and** `CHUNKPILOT_MANAGED_SERVERS_ROOT`. The second variable is new: before it existed, an isolated data root still created real servers in the real user profile.

### 13.2 Shape

```
LiveVanillaWizardViewModel   steps, selection, validation, review, operation watching
IVanillaCreationGateway      the entire App-to-Agent surface, six methods, one named pipe
AgentPipeServer              VanillaVersions | VanillaDestination | BeginVanillaCreation
                             InstallProgress | CancelInstall | VanillaCreations
InstallationCoordinator      BeginVanilla, destination preview, operation snapshots
```

The view model holds no `HttpClient`, no installer, no store and no transport; a compile-time test pins that. It never writes a file, never downloads anything and never registers anything. It submits one plan and then watches.

### 13.3 State, kept in four separable pieces

- **What the user chose** — intent, name, stable/snapshot channel, version, EULA acceptance.
- **What the catalogue is doing** — `LiveCatalogState`: idle, loading, available, cached, stale cache, no usable metadata, request failed; plus a separate flag for "the version you chose disappeared after a refresh".
- **What was resolved** — the version option (with its Java requirement and its source), the destination the Agent answered with, and the review the two produce.
- **What the operation is doing** — `CreationStage`, reported by the Agent.

### 13.4 CreationStage

`CreationPhase` stays the transaction's own state machine — it exists to make recovery a decision rather than a guess. `CreationStage` is what the user is told, and is carried on `InstallProgress` by whoever knows: the coordinator names its runtime steps, the installer names the download and its verification, and the transaction derives the rest through `CreationStagePolicy.ForPhase`. No enum name reaches the interface, and a test asserts that every stage has wording.

Determinate progress appears only where a real byte total exists, which is the server download. Everything else is a short discrete step and shows an indeterminate bar rather than an invented percentage.

### 13.5 Cancellation

`CancelInstall` is idempotent at both ends. While the operation is in a stage that can stop immediately the interface says the folder is untouched; inside promotion, registration or the final checks it says ChunkPilot will finish this step and stop at the next safe point, because promising an instant stop there would be a promise it cannot keep. Closing the window cancels nothing: the Agent owns the operation, and reopening reattaches through `VanillaCreations`.

### 13.6 Destination

`VanillaDestination` answers where a name would land, using the same `MakeSafeInstanceName` identity and the same `CreationDestinationPolicy` the transaction applies, and the transaction re-runs that policy immediately before promotion. A collision blocks with the policy's own wording and asks for a different name; it is never silently suffixed, because a silent rename is the untruthful state the data-safety rules prohibit.

The managed root is the default and stays recommended. **Change…** picks a different *parent* folder, not the server's own folder, and **Use default** returns to the managed root. That distinction is the whole design:

- The folder name is still generated from the display name, so renaming changes the child and keeps the chosen parent — an explicit location is never silently replaced.
- Choosing a folder is not consent to write into it. ChunkPilot only ever creates a new child inside it, and the destination policy still decides whether that child may exist.
- No new contract was needed: the choice travels as `VanillaCreationPlan.InstanceRoot`, which the plan and `VanillaDestinationRequest` already carried, so the Agent re-derives and re-checks the destination exactly as before.

The beginner flow still never asks for a path — the default needs no interaction, and the action is hidden entirely when no folder picker was supplied.

### 13.9 Presentation

The wizard was polished against runtime evidence rather than redesigned. Four defects had shared causes worth recording:

- **The window opened behind the terminal that launched it.** A process that does not own the foreground cannot take it, so `Activate()` only flashed the taskbar. `CreateServerLiveWindow.PresentInForeground` raises it once, from `ContentRendered`, with a topmost toggle that restores the previous value. It runs once, so a provider refresh or a running creation never pulls the user back.
- **The caret sat away from the text.** `AppTextBox` hard-coded `VerticalScrollBarVisibility="Auto"` on `PART_ContentHost`, so a single-line field reserved a scroll bar whenever the text view measured marginally taller than its content area. The visibilities and the content alignments are template-bound now, as the framework's own template does it.
- **The scroll bar was two pixels wide to the pointer.** The thumb's template *was* the pill, inset by four on every side, so the drag target was what was drawn. A transparent host now fills the thumb and takes the hit test while the pill keeps its width; `AppScrollBarThickness` and `AppScrollBarThumbThickness` are separate tokens for that reason.
- **Console follow never paused.** `MainWindow.ConsoleScrollChanged` existed but no XAML ever raised it, so the view model never learned where the viewport was. `ServerConsolePage` now reports it, and Enter is bound to the same command as Send so a disabled state cannot be bypassed. The command box is cleared only after the send is accepted.

Copy conventions for the whole flow are in `UI-COPY-AND-STATE-GUIDE.md`.

### 13.7 EULA

The control starts unchecked in every session and is set by nothing except the user: opening the official document, choosing a version and moving between steps all leave it alone, and changing the version or the name withdraws it, because what was accepted was a specific plan. Acceptance records the moment and the official address and nothing else — the legal text is never copied into persistence.

The Agent refuses a plan whose acceptance is incomplete, `eula.txt` is written only inside the operation-owned staging directory, and the journal now carries `EulaAcceptedUtc`/`EulaSourceUrl` so recovery can require durable evidence: a journal that recorded acceptance **and** an owned folder that contains the file. Either alone is weaker than it looks, and without both, recovery preserves everything and asks for a person.

### 13.8 Outcomes

Completion states the version, the managed runtime the server was actually given (matched from the created definition's launch executable, not assumed), that the server is stopped, and that public access is not configured. `Open server` goes through the shell's existing refresh-and-select path, so `ServerOpened` fires exactly as it does for any other server, and nothing is recreated or imported.

A cleanup warning is still a created server. A rollback says the change was undone. An attention-required outcome says ChunkPilot stopped to protect the files, never calls the server ready, keeps the operation id in technical details, and offers only Close and Copy details — no delete, overwrite, force-complete, take-ownership or ignore-and-continue.

## 11. What this document deliberately does not do

- It does not propose changing `ServerInstallRequest`, `ChunkPilotStore` schema, or any Agent pipe operation's wire shape — the roadmap's early tasks may add *new, additive* fields/operations where a real gap is found (e.g. a combined "resolve compatibility" call, if profiling during implementation shows the wizard would otherwise need 3+ round trips per screen), but that is an implementation-phase decision made against real UI needs, not speculated here.
- It does not invent a compatibility database, a new provider, or a conversion capability CurseForge/Modrinth do not themselves provide.
- It does not specify exact XAML markup — the roadmap's "New wizard shell" task is where that gets written, against real synthetic data, with its own runtime screenshot verification (matching the standard this repository already holds itself to for visual work).
