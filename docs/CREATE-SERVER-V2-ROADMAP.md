# Create Server v2 implementation roadmap

Status: planning document. No task in this roadmap has been executed by this planning session. Companion documents: `CREATE-SERVER-V2-ARCHITECTURE.md`, `CREATE-SERVER-V2-PRESET-INVENTORY.md`, `CREATE-SERVER-V2-RISK-REGISTER.md`, `CREATE-SERVER-V2-PROVIDER-RESEARCH.md`.

Each task below is written to be picked up in a **fresh session** with no memory of this one. "Likely files/symbols" are grounded in verified repository evidence (see the architecture and preset-inventory documents); where a file doesn't exist yet, that is stated explicitly.

General rules for every task:
- Never modify an alternate worktree or any real server, world, or backup.
- Every task ends with the stated test gate green and a Release build with 0 warnings/0 errors before its commit boundary.
- Every task is one coherent local commit unless it says otherwise. Do not push.
- If a task discovers the previous task's assumption was wrong (e.g. a destination-collision behavior that doesn't exist yet), stop, document the finding, and adjust the *next* task rather than silently expanding the current one's scope.

---

### Task 1 — Shared creation contracts and state model

**Observable outcome:** new `ChunkPilot.Core` types (`CreationIntent`, `CreationSelection`, `AdvancedOverrides`, `ResolvedCreationContext`, `CompatibilityConclusion`, `CompatibilityEvidence`, `CreationPlan`, `CreationValidationIssue`) exist, compile, and have unit tests proving `CreationPlan.BuildInstallRequest()` maps deterministically to `ServerInstallRequest` for at least one case per `CreationIntent`.

**Likely files:** new `src/ChunkPilot.Core/CreationWizardModels.cs`; new `tests/ChunkPilot.UnitTests/CreationWizardModelsTests.cs`.

**Requirements:** pure data + one pure mapping method; no I/O; no WPF reference; follow the existing `GuidedPlatformModels.cs` style (sealed records, `IReadOnlyList`/`IReadOnlyDictionary`, XML doc only where non-obvious).

**Non-goals:** no ViewModel, no XAML, no Agent operation change.

**Safety boundaries:** none beyond standard code-change hygiene — this task touches no runtime path.

**Test gate:** new unit tests + full existing unit suite (325 baseline) green. No integration tests needed (no I/O introduced).

**Recommended model route:** Sonnet (pure C# modeling, low ambiguity).

**Dependencies:** none — first task.

**Expected commit boundary:** one commit, `Add Create Server v2 shared state model`.

---

### Task 2 — New wizard shell using synthetic/offline data

**Observable outcome:** a new modal `Window` (`CreateServerWizardWindow`) with the six-intent left selector, center/right regions, and Back/Next/Create chrome, opened from a **temporary** side entry point (not yet wired to `MainViewModel.InstallServerCommand` — that switch is task 16), rendering entirely from an in-memory fake `IAgentClient`/fixture data so it can be visually verified without touching the real Agent. Uses only `5f19531`-era tokens/components (`AppPageHeader`, `AppCard`, `AppServerRow`, `AppSearchBox`, `AppStatusBadge`, `AppEmptyState`).

**Likely files:** new `src/ChunkPilot.App/CreateServerWizardWindow.xaml(.cs)`, new `src/ChunkPilot.App/CreationWizardViewModel.cs` + per-intent details ViewModels (empty/stub bodies for intents beyond Vanilla at this stage — filled in by tasks 5–9, 11).

**Requirements:** intent selector always visible; center region swaps per intent; keyboard focus and `AutomationProperties.Name` on every interactive element from the start (retrofitting accessibility later is the anti-pattern this avoids); responsive collapse at ≤1000px width per `docs/UI-RESPONSIVE-RULES.md`.

**Non-goals:** no real provider calls, no real install, no EULA write, no navigation-integration change yet.

**Safety boundaries:** runs against synthetic data only; if a smoke-test launch is done, it must use an isolated `CHUNKPILOT_DATA_ROOT` exactly as the visual-polish session's smoke tests did.

**Test gate:** design-system contract tests (existing `DesignSystemContractTests.cs`) must still pass unmodified; new navigation/focus tests for the wizard shell; runtime screenshot verification at 800×600/1440×900/1920×1080 (store under `artifacts/visual-review/create-server-v2/`, git-ignored, not committed).

**Recommended model route:** Sonnet, with a runtime visual-verification pass (screenshots) before commit, matching the standard this repository already applies to XAML work.

**Dependencies:** Task 1.

**Expected commit boundary:** one commit, `Add Create Server v2 wizard shell (synthetic data)`.

---

### Task 3 — Creation-plan and review presentation

**Observable outcome:** `CreationPlanner` (App-layer service) exists and can turn a `CreationSelection` into a `CreationPlan` with a real `ReviewSummary`/`Issues` list, still against fixture/fake data; the Review screen renders it using the `QuickStartPreset.ReviewItems` bullet-list pattern.

**Likely files:** new `src/ChunkPilot.App/CreationPlanner.cs`; Review screen XAML inside the wizard shell from task 2.

**Requirements:** `CreationPlanner` must be unit-testable with a fake `IAgentClient` (same interface `InstallServerViewModel` already uses); no XAML-only logic branches — all plan-building logic lives in `CreationPlanner`, not in the ViewModel or XAML converters.

**Non-goals:** no real network calls yet (task 5+ wires real providers per-intent).

**Safety boundaries:** none new.

**Test gate:** unit tests for `CreationPlanner` covering: all-required-fields-present → `CanProceed=true`; missing EULA acceptance → blocking issue; Java/loader mismatch → warning-level issue, not blocking (per architecture doc §10). Full unit suite green.

**Recommended model route:** Sonnet.

**Dependencies:** Tasks 1, 2.

**Expected commit boundary:** one commit, `Add Create Server v2 plan builder and review screen`.

---

### Task 4 — Transaction/staging foundation verification and gap-fill

> **Status: complete.** Delivered by the transaction-hardening milestone, and larger than this task
> anticipated: all three **[U]** items were genuine gaps rather than unknowns. Destination collision
> had no canonical or ownership check; the create path had no post-registration verification; and
> registration happened *outside* the installer entirely, in `InstallationCoordinator`, after the
> installer had already deleted its journal row and reported success. See
> `CREATE-SERVER-V2-ARCHITECTURE.md` §12 for what was built and tested. Per-intent interruption
> coverage remains Task 17.

**Observable outcome:** a written, verified answer (as code comments/tests, not just prose) to the three **[U]** items flagged in the architecture document: (a) destination-collision behavior in `ManagedServerInstaller`, (b) post-registration verification for the *create* path specifically, (c) agent-restart recovery behavior for an interrupted first install. Any genuine gap found gets a minimal, targeted fix — this task does **not** refactor the existing transaction, only closes gaps the inspection actually finds.

**Likely files:** `src/ChunkPilot.Infrastructure/ManagedServerInstaller.cs`; `src/ChunkPilot.Agent/ServerSupervisor.cs` (recovery-on-startup, same file the visual-polish session already read for lifecycle reconciliation); new/extended tests in `tests/ChunkPilot.IntegrationTests/LoaderAndJavaFixtureIntegrationTests.cs` or a new sibling file.

**Requirements:** if destination collision is currently unhandled, add explicit rejection with a clear error surfaced through `InstallOperationSnapshot.Error` — never silent overwrite, never silent auto-rename without telling the user (per `AGENTS.md` "Worlds are irreplaceable" and "Truth over confidence theater").

**Non-goals:** do not touch the update-path transaction (`ServerPackUpdateService`) — out of scope, already correct per `docs/ARCHITECTURE.md`.

**Safety boundaries:** any new integration test must use temporary app-data/server roots per existing convention; never an installed real server.

**Test gate:** new/updated integration tests green; full integration suite (34 baseline + additions) green; full unit suite green; Release build 0/0.

**Recommended model route:** Sonnet for investigation and small fixes; escalate to a fresh session with `xhigh`/`max` reasoning if a genuine recovery-path defect is found, since data-safety code deserves the highest scrutiny available.

**Dependencies:** none technically, but sequenced here so gaps are known before vertical slices depend on this behavior.

**Expected commit boundary:** one commit if no gap found (`Verify create-path transaction and recovery behavior`) or two commits if a fix is needed (verification commit, then a separately reviewable fix commit).

---

### Task 5 — Vanilla vertical slice — **done**

Delivered in two commits. The first added the Agent-side services (`VanillaVersionCatalogService`, `InstallationCoordinator.BeginVanilla`, the plan contract and its official-metadata Java handling). The second connected the App: the live wizard behind `--create-server-v2-live-vanilla`, deliberate EULA acceptance with durable evidence, destination preview, truthful progress and cancellation, recovery-required presentation, and completion through the shell's existing semantic navigation. See `CREATE-SERVER-V2-ARCHITECTURE.md` §13.

Two things differ from what was planned below. The version list comes from `VanillaVersionCatalogService` rather than `InstallVersionsRequest(InstallSourceType.Vanilla)`, because the wizard needs each version's Java requirement, server-download availability and integrity evidence, and the older operation returns version strings only. And the Java requirement is read from official `javaVersion.majorVersion` metadata first, with `JavaRuntimePolicy` as the labelled fallback: the repository's version rules were written when releases were numbered 1.x and infer the wrong major for the current date-based scheme.

**Observable outcome:** the Vanilla intent is fully real: version list from `InstallVersionsRequest(InstallSourceType.Vanilla)`, Java resolved silently via `ManagedJavaRuntimeService`, EULA step, Review, real `BeginInstall`/progress/completion against an isolated fixture Agent — end-to-end creation of a real (but test-fixture) vanilla server succeeds.

**Likely files:** `VanillaDetailsViewModel`; wizard Review/Progress/Completion screens now driven by real data for this one path; existing `ServerDownloadCatalog`/`BuiltInServerCatalogProvider` consumed as-is, unmodified.

**Requirements:** no Java path, no port, no raw properties visible by default; Advanced-path escape hatch still reachable via the Advanced intent, not smuggled into Vanilla's screen.

**Non-goals:** no snapshot/version-manager UI changes (separately owned per `docs/ARCHITECTURE.md`'s Version Manager).

**Safety boundaries:** end-to-end test must run against an isolated `CHUNKPILOT_DATA_ROOT` and never a real Minecraft download unless the test is explicitly marked as one that hits the real Mojang manifest (read-only metadata fetch is low-risk and already done by existing integration tests per `LoaderAndJavaFixtureIntegrationTests.cs:75`, "Beginner_Vanilla_flow_installs_managed_Java_and_exact_server_release" — reuse that pattern).

**Test gate:** new wizard-level Vanilla end-to-end test; existing `Beginner_Vanilla_flow_...` integration test still green; full unit + integration suites green; Release build 0/0; manual runtime smoke (launch wizard, complete Vanilla creation against fixture, confirm server appears in Dashboard/Servers).

**Recommended model route:** Sonnet.

**Dependencies:** Tasks 1–4.

**Expected commit boundary:** one commit, `Add Vanilla creation vertical slice`.

---

### Task 6 — Plugin-server vertical slice

**Observable outcome:** Plugins intent working end-to-end (Paper, with Purpur as a secondary in-screen choice if the assignment's "Paper or other supported providers only when verified" is read as allowing more than one — Purpur is already provider-verified per `docs/PROVIDERS.md`).

**Likely files:** `PluginsDetailsViewModel`; reuses `BuiltInServerCatalogProvider` unmodified.

**Requirements:** plain-language explanation of "plugin-capable" (per assignment) sourced from `QuickStartPreset` (`PluginsAndMinigames`) copy, not re-authored from scratch; future plugin-catalog integration point left as a clearly marked extension seam (e.g. a disabled/hidden "Browse plugins" affordance) — **do not build the plugin catalog itself** in this task (assignment: "future plugin catalog integration without coupling the initial wizard to it").

**Non-goals:** no plugin catalog implementation.

**Test gate:** vertical-slice end-to-end test mirroring task 5's pattern; full suites green; Release build 0/0.

**Recommended model route:** Sonnet.

**Dependencies:** Tasks 1–4 (parallelizable with Task 5 once those land, since Vanilla and Plugins do not share mutable state beyond the wizard shell).

**Expected commit boundary:** one commit, `Add plugin-server creation vertical slice`.

---

### Task 7 — Fabric vertical slice

**Observable outcome:** Mods intent, Fabric loader path, working end-to-end via `LoaderMetadataService`'s existing `ResolveFabricAsync`.

**Likely files:** `ModsDetailsViewModel` (created here, extended by tasks 8–9 for the other loaders — **do not** build all four loaders in one task, per the assignment's explicit instruction not to combine providers "merely because they look similar"; each loader's official metadata shape and installer behavior differs enough to warrant separate verification).

**Test gate:** vertical-slice end-to-end test using existing `LoaderAndJavaFixtureIntegrationTests.cs` patterns for Fabric specifically; full suites green.

**Recommended model route:** Sonnet.

**Dependencies:** Tasks 1–4.

**Expected commit boundary:** one commit, `Add Fabric creation vertical slice`.

---

### Task 8 — NeoForge vertical slice

**Observable outcome:** Mods intent, NeoForge loader path, via `LoaderMetadataService.ResolveNeoForgeAsync`. Sequenced before Forge because NeoForge is the actively-maintained fork with simpler current tooling per `docs/CREATE-SERVER-V2-PROVIDER-RESEARCH.md`.

**Test gate:** same pattern as task 7, NeoForge-specific.

**Recommended model route:** Sonnet.

**Dependencies:** Task 7 (shares `ModsDetailsViewModel` scaffolding).

**Expected commit boundary:** one commit, `Add NeoForge creation vertical slice`.

---

### Task 9 — Forge vertical slice

**Observable outcome:** Mods intent, Forge loader path, via `LoaderMetadataService.ResolveForgeAsync`.

**Test gate:** same pattern, Forge-specific — Forge's installer historically has more version-specific quirks than NeoForge (external, evolving; verify current behavior against `docs/PROVIDERS.md`'s citation and the official Forge Maven metadata at implementation time, not from this document's cached knowledge).

**Recommended model route:** Sonnet.

**Dependencies:** Task 8.

**Expected commit boundary:** one commit, `Add Forge creation vertical slice`.

---

### Task 10 — Modrinth catalog foundation

**Observable outcome:** the Modpack intent's list/details/search UI exists and works against **Modrinth only** (no CurseForge yet), including the server-pack-status badge, client-only detection, and version/loader/release-channel filtering, all via the existing `ModrinthCatalogProvider`/`GuidedCatalogService`.

**Likely files:** `ModpackDetailsViewModel`; new lightweight image-loading/caching helper for `CatalogItem.IconUrl` (flagged as new in the architecture document §9) — bounded cache, no continuous polling, respects Modrinth's documented rate limit (300 req/min per IP per official docs — see provider research notes).

**Non-goals:** no CurseForge tab yet (task 12–13).

**Test gate:** catalog-foundation tests (search, filter, server-pack-status rendering, client-only rejection) using fixture `CatalogItem`/`CatalogVersion` data, not live network calls in the test suite; full unit suite green.

**Recommended model route:** Sonnet.

**Dependencies:** Tasks 1–4.

**Expected commit boundary:** one commit, `Add Modrinth modpack catalog foundation`.

---

### Task 11 — Modrinth modpack vertical slice

**Observable outcome:** full Modpack-via-Modrinth creation, end-to-end, including the "no server pack" truthful-blocking state and the version-mismatch truthful-warning state described in the architecture document §9.

**Test gate:** end-to-end test with at least three fixture modpacks: one with a server pack, one client-only, one with a version that doesn't match a pre-selected Minecraft version. Full suites green.

**Recommended model route:** Sonnet.

**Dependencies:** Task 10.

**Expected commit boundary:** one commit, `Add Modrinth modpack creation vertical slice`.

---

### Task 12 — CurseForge research/authentication decision

**Observable outcome:** a short, dated decision record (append to `CREATE-SERVER-V2-PROVIDER-RESEARCH.md`, do not create a new file) confirming, against the *current* official CurseForge API terms and developer-portal state at implementation time, that: the existing user-supplied-key, DPAPI-encrypted, no-bundled-key approach remains compliant; whether the "API key now required for direct CDN file downloads" change (in effect or scheduled at the time this planning document was written — see provider research notes) has taken effect and whether `CurseForgeCatalogProvider`'s existing download-URL handling (`curseforge-file:{id}` placeholder scheme observed in `GuidedPlatformServices.cs:616-617`) needs the key attached to the actual file-download request, not just the search request. Also: design (not yet build) the small Settings-page control for entering/clearing the key.

**Likely files:** research doc update; a short design note for a new `ServerSettingsPage.xaml` section (implementation happens in task 13).

**Non-goals:** no code changes in this task — it is explicitly a research/decision task per the assignment's own sequencing.

**Test gate:** none (no code change); documentation-only, verify no other diff crept in.

**Recommended model route:** a fresh session with live web research capability (this task depends on external, time-sensitive facts a stale model cutoff cannot be trusted for — re-verify, do not assume this planning document's citations are still current).

**Dependencies:** Task 11 (so the UI pattern for "gated provider tab" already exists to slot CurseForge into).

**Expected commit boundary:** one commit, `Record CurseForge API decision for Create Server v2`.

---

### Task 13 — CurseForge implementation where permitted

**Observable outcome:** conditional on task 12's decision being "proceed" — CurseForge tab added to the Modpack intent, Settings-page key entry built, full vertical slice mirroring task 11's test shape. If task 12's decision is "do not proceed" (e.g. terms changed unfavorably), this task is skipped and the roadmap notes why, rather than forcing an implementation.

**Test gate:** same shape as task 11, plus a test for the "no key configured" truthful-unavailable state and a test confirming the key is never logged/written outside the encrypted secret store.

**Recommended model route:** Sonnet, informed by task 12's fresh findings.

**Dependencies:** Task 12.

**Expected commit boundary:** one commit, `Add CurseForge modpack creation vertical slice` (or a no-op documentation commit if skipped).

---

### Task 14 — Crossplay vertical slice

**Observable outcome:** Crossplay intent working end-to-end via existing `ICrossplayPackageProvider`/`OfficialCrossplayPackageProvider`, built on a Paper base (matching `QuickStartKind.JavaBedrockCrossplay`'s existing domain default), with the Java/TCP vs. Bedrock/UDP distinction explained in plain language and no invented public-reachability claims (existing `AGENTS.md` rule, already enforced elsewhere in the app for connection info — reuse that pattern, do not re-derive it).

**Test gate:** end-to-end test reusing `LoaderAndJavaFixtureIntegrationTests.cs`'s existing `Crossplay_packages_are_hash_verified_backed_up_and_removed_by_ownership` pattern for the creation-time path specifically (that test currently covers post-creation crossplay package management — confirm during implementation whether creation-time crossplay needs its own coverage or can share fixtures).

**Recommended model route:** Sonnet.

**Dependencies:** Task 5 (Paper base already proven via Plugins slice, task 6).

**Expected commit boundary:** one commit, `Add crossplay creation vertical slice`.

---

### Task 15 — Advanced/custom path

**Observable outcome:** Advanced intent screen exists, carrying forward every field currently in `InstallServerWindow.xaml` (source type, path/URL, Java, RAM, port, max players), wired through the same `CreationPlan`/`BeginInstall` pipeline as every other intent, with the compatibility-demotion behavior from the architecture document §10 implemented and tested.

**Test gate:** end-to-end test proving Advanced cannot bypass EULA/staging/hash-verification (i.e., the exact same `BeginInstall` code path is exercised); test proving an Advanced override demotes `CompatibilityEvidence` to `Unknown` with a recorded warning. Full suites green.

**Recommended model route:** Sonnet.

**Dependencies:** Tasks 1–4.

**Expected commit boundary:** one commit, `Add Advanced/custom creation path`.

---

### Task 16 — Legacy preset retirement/migration and navigation switch-over

**Vanilla product cutover delivered first (2026-08-11):** every normal beginner **Create server** action now enters the validated live Vanilla workflow through a semantic shell request, without a development switch. **Add existing server** remains separate. The old window files are intentionally retained but unreachable from normal Vanilla creation because their broader Advanced/custom capabilities have not yet been migrated; full legacy deletion and quick-start retirement remain dependent on Task 15 as described below.

**Observable outcome:** `MainViewModel.InstallServerCommand` now opens the new wizard instead of `InstallServerWindow`; `InstallServerWindow.xaml(.cs)` and `InstallServerViewModel.cs` are deleted (not left as dead code — per the preset-inventory document, nothing persists a dependency on them); `QuickStartKind.ImportExistingServer` removed from the wizard's selectable UI per the preset-inventory recommendation (enum member itself may be retired here too if the exhaustive-switch blast radius, checked in this task, is small — confirmed candidates: `QuickStartPresetFactory.Create`, `AgentPipeServer.cs:243-246`'s dead `"QuickStartPresets"` op, and any test enumerating `Enum.GetValues<QuickStartKind>()`).

**Requirements:** every place that constructed `new InstallServerWindow(...)` or `new InstallServerViewModel(...)` is updated; no dangling references; `docs/UI-COMPONENT-CATALOG.md`/`docs/UI-DESIGN-SYSTEM.md` get a one-line pointer update if they reference the old Create Server window (verify during implementation — not confirmed by this planning pass).

**Non-goals:** no change to `ImportServerWindow`/`ImportServerViewModel` (the assignment requires "Add existing server" to remain separate — it already is, and stays untouched).

**Safety boundaries:** this is the task most likely to affect `NavigationService`/`MainViewModel` — re-run the full navigation regression test set explicitly, not just the general suite.

**Test gate:** full unit suite; full integration suite; navigation regression tests specifically named/verified; Release build 0/0; runtime smoke test of the real `Create server` button on Dashboard/Servers opening the new wizard; `git diff --check`.

**Recommended model route:** Sonnet, but flagged for careful personal review given the navigation touch-point (matches the level of scrutiny the visual-polish session already applied to `MainWindow.xaml` changes).

**Dependencies:** Tasks 5, 6, 15 minimum (Vanilla/Plugins/Advanced must work before the legacy window can be safely retired — modpack/loader/crossplay slices can land after cut-over since they're additive intents, not required for a functioning replacement, provided the assignment's "avoid a flag-day rewrite" preference is honored by keeping this task late but not last).

**Expected commit boundary:** one commit, `Retire legacy Create Server window and quick-start dropdown`.

---

### Task 17 — Recovery and interruption hardening

**Observable outcome:** explicit tests proving: app crash mid-creation leaves no half-created server visible in Dashboard/Servers; agent restart mid-staging recovers cleanly (extends whatever task 4 found/fixed with real end-to-end coverage across every intent, not just the one task 4 happened to inspect first); cancellation at every documented cancellation point leaves no orphaned staging directory.

**Test gate:** new interruption/recovery integration tests; full suites green.

**Recommended model route:** Sonnet for implementation; consider a fresh session with elevated reasoning for the recovery-logic review itself, matching task 4's guidance.

**Dependencies:** Tasks 4–16 (needs every intent to exist so recovery can be tested per-intent, not just for Vanilla).

**Expected commit boundary:** one commit, `Harden Create Server v2 recovery and interruption handling`.

---

### Task 18 — Accessibility, responsive, and visual acceptance

**Observable outcome:** the same standard of runtime-verified visual/accessibility acceptance the visual-polish milestone (`5f19531`) already applied, now against the finished wizard across all six intents: keyboard-only completion of at least one full creation per intent; visible focus throughout; High Contrast pass; Reduced Motion pass; 800×600 through 1920×1080/maximized; 125%/150% DPI scaling **if the implementation environment permits changing display scaling** (the visual-polish session could not, since that requires registry changes explicitly forbidden — flag as unverified again here if the same constraint applies).

**Test gate:** design-system/contract tests; runtime screenshots stored under `artifacts/visual-review/create-server-v2/` (git-ignored); no regression in the existing 5f19531 contract tests.

**Recommended model route:** Sonnet, with genuine runtime screenshot verification, not a code-only pass.

**Dependencies:** Tasks 5–16.

**Expected commit boundary:** one commit, `Verify Create Server v2 accessibility and responsive behavior` (fixes only if genuine defects are found; otherwise a documentation-of-verification commit is not required — fold findings into this commit's message).

---

### Task 19 — End-to-end release hardening

**Observable outcome:** full release gate: complete unit + integration suites, Release build, a real (fixture-backed, non-real-Minecraft) end-to-end creation smoke test per intent run back-to-back in one session to catch cross-intent state leakage in the wizard shell, `git diff --check`, and a final documentation pass confirming `docs/ARCHITECTURE.md`/`docs/UI-COMPONENT-CATALOG.md`/`docs/UI-OVERHAUL-WORKLOG.md` (or their v2 successors) accurately describe the shipped wizard rather than the retired one.

**Test gate:** everything above, green, in one sitting.

**Recommended model route:** Sonnet for mechanical verification; a fresh Qwen or Sonnet session for an independent second-opinion review of the full diff against this roadmap's stated non-goals, mirroring the "independent review" pattern already used for the visual-polish milestone's commit `3dd5bcf`.

**Dependencies:** Tasks 1–18.

**Expected commit boundary:** one commit, `Ship Create Server v2` (or a small number of fix commits discovered during hardening, each independently reviewable).

---

## Sequencing rationale (what changed from the assignment's suggested order, and why)

The assignment's suggested sequence is preserved with two adjustments, both explained inline above:
1. **NeoForge before Forge** (tasks 8 before 9) — NeoForge is the actively-maintained fork with the simpler, more current official tooling; sequencing it first means Forge's more idiosyncratic installer history is tackled with a working three-loader pattern already proven, reducing risk on the hardest loader last rather than second.
2. **A dedicated Task 4 ("Transaction/staging foundation verification")** was inserted before any vertical slice, because the architecture inspection found three specific **[U]** unknowns (destination collision, create-path post-registration verification, create-path restart recovery) that every subsequent vertical slice implicitly depends on. Discovering a gap here after five vertical slices are already built would be far more expensive than discovering it first.

No task combines more than one loader or provider, per the assignment's explicit instruction.
