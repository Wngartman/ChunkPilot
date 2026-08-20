# Complete UI overhaul worklog

## Recovery and baseline
- Implementation branch: `feature/chunkpilot-complete-ui-overhaul`
- Clean implementation base: `ff95d6280a1175ca27058000975ec18dac2c2184`
- Recovery branch: `recovery/rejected-midnight-pilot-20260724-2013`
- Recovery commit: `fc7937fce4315746db42050fb12d5fe60e3a3bf9`
- Recovery tag: `chunkpilot-before-complete-ui-overhaul-20260724`
- Product version: `1.3.0`; SQLite schema: `4`
- Alternate worktrees and real user server folders were not accessed.

Baseline restore completed. Release solution build completed with 0 warnings and 0 errors. The environment's test invocation returned without a summary and exit code 1 after testhost startup output; exact test totals are therefore not claimed until a project-specific rerun produces a summary. No real server data was used.

## Phase checkpoints
- Phase 0/1: design contract and durable documentation — committed as `e621c5b`.
- Phase 2: Fluent icon and token foundation — committed as `e621c5b`.
- Phase 3: shared components, motion, accessibility, and high-contrast resource foundation — committed as `a0a96fb`.
- Phase 4: semantic navigation and responsive shell foundation — committed as `454deaa`.
- Phase 5: responsive workspace shell, header status context, and surface styling — committed as `b276d94`.
- Phase 6: server workspace semantic destinations, legacy tab-setting compatibility, and Overview action cleanup — implemented and committed as `6dc4d11`.
- Phase 7a: console, management/world surfaces, and guided create/import entry workflows — implemented and committed as `ede86fd`, `7582f94`, and `9d0c238`.
- Phase 7b: dashboard/global page hierarchy and staged update surface typography — implemented and committed as `a61d862`.
- Focused UI resource/navigation tests passed 6/6 through VSTest/TRX after the destination pass. Release solution build passed with 0 warnings and 0 errors.
- Release validation: restore succeeded; full UnitTests passed 111/111; BackupIntegrationTests passed 2/2; LoaderAndJavaFixtureIntegrationTests passed 9/9; VersionUpdateIntegrationTests passed 7/7. The aggregate integration runner and ManagedServerIntegrationTests group hung in synthetic process supervision before producing a TRX summary; no failure result was fabricated. The source App WM_CLOSE integration test also did not produce a summary in this environment.
- Framework-dependent and self-contained win-x64 App/Agent publishes succeeded. Inno Setup compiled `installer/output/ChunkPilot-Setup-1.3.0-win-x64.exe`. Packaged WM_CLOSE smoke passed: target Agent shutdown, unrelated Agent survival, no invisible App, and temporary-root cleanup all passed. Installer SHA-256: `4443CBA27F786EE3688E58E380D54040C0B990DA6C1CBC077DA685055A6EAF7C`.
- Isolated installer upgrade execution was not run because the installer writes per-user AppData and Run registry state, which is outside the permitted synthetic-only validation boundary. Deterministic compositor screenshots were not accepted; visual evidence remains structural/resource/build based.
- Release checkpoint `b1f7029` was committed and verified. The temporary validation TRX reports were removed, the installer checksum sidecar matches the compiled artifact, and the final state is clean. Five limitations remain explicitly recorded in `UI-OVERHAUL-STATE.json`.

## Validation policy
Each phase requires `git diff --check`, targeted tests, Release build for XAML changes, and a local commit only after the gate is green. Milestones require isolated packaged startup/WM_CLOSE smoke with synthetic agents and temporary roots. Invalid desktop screenshot captures are never presented as visual acceptance.

## UI rebuild - Phase 1: design-system foundation

- Implementation branch: `feature/chunkpilot-ui-foundation`
- Base commit: `20e9c7e` on `feature/chunkpilot-complete-ui-overhaul`
- Recovery tag: `chunkpilot-before-ui-foundation-rebuild-20260725`
- Product version `1.3.0`, SQLite schema `4`, both unchanged. Presentation-only phase.
- Excluded locations were not accessed: alternate worktrees, real user servers, real worlds, real
  backups, installed application data, registry and firewall state.

### What changed
The previous overhaul reskinned the existing layout. This phase replaces the foundation itself:
neutral dark surfaces with purple reserved for brand, selection, focus and primary intent; a typed
token layer; twelve shared component dictionaries; thirteen lookless composite components; a semantic
icon abstraction with a single package touchpoint; centralised theme loading with accessibility
overlays; responsive layout as an inherited property rather than per-view size handlers; and a
development-only Design Gallery. Product pages were deliberately not redesigned.

### Architectural finding worth keeping
Theme dictionaries must be merged **flat** into `App.xaml`. An earlier revision of this phase grouped
them behind `Themes/Theme.xaml` and `Themes/Tokens/DesignTokens.xaml`; the extra nesting broke
deferred `StaticResource` and `Style BasedOn` resolution, first losing an `Effect`'s shadow colour and
then a text style's `BasedOn`. `AppTheme.ThemeDictionaries` holds the canonical ordered list and a
contract test asserts `App.xaml` matches it.

### Defects the new tests and the gallery renderer caught
- Two cross-dictionary resolution failures, found by the gallery renderer before any UI was opened.
- `AppDialogWindow` set `WindowStartupLocation`, which is a plain CLR property on `Window`, so
  realising the style threw. Dialog windows now set it themselves.
- A virtualised table rendered empty in a single off-screen layout pass; the renderer now performs two
  passes with the dispatcher drained between them.
- `ImportServerWindow.xaml.cs` was using a message box and was missing from the documented legacy set.

### Validation
- `dotnet restore` succeeded.
- Release solution build: 0 warnings, 0 errors.
- Unit tests: 139/139 passed, 0 skipped, including 28 design-system contract tests.
- Design Gallery rasterised at 1440, 1100 and 840 device-independent pixels and inspected. The capture
  is WPF's own render of ChunkPilot's element tree, not a compositor screenshot, so the captured
  content is unambiguously identified.
- The previously pending `deterministic-multi-size-visual-rendering` item is now addressed for the
  design system by the gallery renderer; it remains open for product pages, which have not been
  rebuilt.

### Limitations recorded honestly
- `Themes/Compatibility/LegacyAliases.xaml` still exists with 41 aliases so `MainWindow`,
  `ImportServerWindow` and `InstallServerWindow` keep working. Those aliases are resolved statically
  and therefore do **not** follow the high-contrast overlay. This is deliberate pressure to finish the
  rebuild, and a test pins the alias count so it can only shrink.
- Message boxes remain in `App.xaml.cs`, `DialogService.cs` and `ImportServerWindow.xaml.cs` pending
  the dialog rebuild; a test confines them to exactly those three files.
- Hover, pressed and focus states are verified structurally and interactively in the gallery, not by
  automated pixel comparison.
- `AppDialogWindow` is declared but not yet demonstrated in the gallery, because a `Window` style
  cannot be hosted inside the gallery's user control.
- No installer, publish or packaged smoke run was performed in this phase; it introduces no agent,
  persistence or installer change.

### Phase 1 closeout validation (2026-07-25)
- Branch: `feature/chunkpilot-ui-foundation`
- Unit tests: **141/141 passed** (Release configuration), 0 warnings, 0 errors, 0.81 s.
  Includes DesignSystemContractTests: theme resolution, layout mode breakpoints, catalog↔theme
  agreement, icon-intent mapping, compatibility-layer shrink-only, no glyph literals, no nested
  scrollers, no hand-constructed colours.
- Release build: 0 warnings, 0 errors.
- `git diff --check`: exit 0, no whitespace errors.
- Launch smoke: `ChunkPilot.exe` started with isolated `CHUNKPILOT_DATA_ROOT`,
  `CHUNKPILOT_NO_AGENT=1`, `CHUNKPILOT_NO_SINGLE_INSTANCE=1`. Process confirmed running after 4 s.
  WM_CLOSE via `CloseMainWindow()` sent; process exited; no invisible ChunkPilot process remained.
- Source diff review: 27 tracked files changed (866 insertions, 790 deletions), all confined to
  `ChunkPilot.App`, docs, tests, `AGENTS.md`, `CHANGELOG.md`. 53 new source files added. No changes
  to Core, Infrastructure, or Agent. No TODO/FIXME/NotImplementedException found.
- Design Gallery was rendered at Wide (1440), Standard (1100), and Compact (840) widths. **The user
  visually reviewed the rendered gallery images and confirmed correctness.** Automated image-tool
  inspection was blocked by repeated Kiro tool failures on generated image dimensions; this is
  explicitly waived for the Phase 1 closeout as the user's own review is authoritative.
- Temporary render artifacts, scripts, and synthetic data removed from `temp/` (gitignored).
- Working tree committed as the Phase 1 design-system foundation.
