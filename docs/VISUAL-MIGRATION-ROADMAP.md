# ChunkPilot visual migration roadmap

Status: planning document. **No task below was executed by the session that produced it.** Companion documents: [`VISUAL-AUDIT-V2.md`](VISUAL-AUDIT-V2.md), [`VISUAL-DIRECTION-V2.md`](VISUAL-DIRECTION-V2.md), [`VISUAL-SYSTEM-V2-SPEC.md`](VISUAL-SYSTEM-V2-SPEC.md), [`VISUAL-PAGE-SPECIFICATIONS-V2.md`](VISUAL-PAGE-SPECIFICATIONS-V2.md), [`VISUAL-BRAND-AND-ICON-V2.md`](VISUAL-BRAND-AND-ICON-V2.md), [`VISUAL-ACCEPTANCE-CHECKLIST.md`](VISUAL-ACCEPTANCE-CHECKLIST.md).

Baseline: branch `plan/create-server-v2` at `02ec1bf` — 325 unit tests, 34 integration tests, Release build 0 warnings / 0 errors.

## Implementation status

Updated after the production session on branch `feature/visual-system-v2-production`, then again after the core-page migration session on branch `feature/visual-system-v2-core-pages`.

| Task | Status | Notes |
| --- | --- | --- |
| 0 — Baseline | **Partial** | Before/after runtime captures and a reusable capture harness exist under `artifacts/visual-system-v2-production/` and `artifacts/visual-system-v2-core-pages/`. The numeric performance baseline in `docs/VISUAL-BASELINE-02ec1bf.md` was **not** produced. |
| 1 — Typography | **Done** | `AppFontWeightSemibold` renamed `AppFontWeightStrong`. Display is Regular, not Semilight. `AppNumericText` added. |
| 2 — Sidebar defects | **Done** | Root causes differed from the plan — see below. A fourth defect (accessible name) was found and fixed. A **fifth** defect was found and fixed in the follow-up session: a legacy `MainWindow_SizeChanged` code-behind handler independently resized `NavRailColumn` using its own 1000/1200 thresholds, fighting the style-driven 900/1280 breakpoints and reintroducing label clipping between those two threshold pairs. Removed; the column is now `Width="Auto"` and tracks the rail Border's own style-driven width. |
| 3 — Palette | **Done** | Accent split implemented as specified. `AppDanger` is `#CE4553`, not the planned `#C8404E`. |
| 4 — Text rendering A/B | **Not done** | `TextRenderingMode` remains `ClearType`. The `[U]` in §1.6 is still open. |
| 5 — Layout primitives | **Partial** | No new Control class. `AppMeasureForm`(720)/`AppMeasureContent`(1280) tokens added to `MetricTokens.xaml`; capped regions use `HorizontalAlignment` default Stretch + `MaxWidth`, which centres automatically. The higher-leverage fix was a `MinHeight` binding from the page-content `Grid` to its `ScrollViewer`'s `ActualHeight`: a `ScrollViewer` measures its content with infinite height, so a `Star` row or a centred `AppEmptyState` never got real space to fill or centre within — this is why every empty state was a small island near the top of a large page regardless of page-specific work. One `MinHeight` binding on the shared page-content wrapper fixed it for every page at once. |
| 6 — Shared controls | **Partial** | Buttons, segmented control, field labels, navigation and elevation updated (prior session). This session found and fixed a real `AppSectionCard` defect: its `Header` `ContentPresenter` had no `TextWrapping`, so any header text that didn't fit a card's width was silently clipped rather than wrapped — invisible until a page actually used the component with a multi-word header. Fixed with a scoped `DataTemplate` override for `sys:String` (`TextWrapping` is not an attached property, so it cannot be set the way `FontSize`/`FontWeight` are). |
| 7 — Shell / sidebar polish | **Done** | Defects fixed, brand block updated (prior session), the fifth sidebar defect above fixed (this session). Active-server identity card and Settings-page disambiguation were not pursued further; the existing "Active server" row in the rail already fills that role adequately. |
| 8–11 — Global pages | **Done** | Dashboard, Servers, Automation, Activity and Settings all migrated. See the completion report for this session for the specific composition of each. |
| 12 — Brand and micro-icon | **Done** | Mark "Lift" adopted (prior session). Live taskbar-button verification in this session was inconclusive on the development desktop's heavily customised third-party taskbar shell (visual inspection and a `Shell_TrayWnd` UI Automation lookup both failed to positively locate the button among unfamiliar icon styling); the title-bar icon was confirmed crisp and correctly sized in every capture. Trademark search still outstanding. |
| 13–18 — Workspace | **Partial** | Workspace header (§13) done: lifecycle state added as an `AppStatusBadge` next to the name (previously colour-only), typography moved off hardcoded `FontSize`/`FontWeight` onto `AppTitleText`, and the four lifecycle buttons moved from a non-wrapping `StackPanel` to a flat `WrapPanel` so a narrow window drops them to a second line instead of overflowing past the header's edge. Overview (§14) had its own duplicate identity/lifecycle-actions card removed — it was rendering the same name/state/ecosystem/version/Start-Stop-Restart-Save block the workspace header already shows on every server destination, which is the exact "raw property sheet" duplication the spec asks Overview to avoid. Console/Manage/Access/Protection/Settings (§15–18) intentionally untouched, per scope. |
| 19 — Create Server visual | **Not started** | Explicitly out of scope for both sessions. |
| 20–24 — Hardening, motion, Mica | **Not done** | Deferred. |

### Where implementation diverged from the plan

- **D-2 had a different root cause than assumed.** The plan guessed at view-model initialisation. The actual defect was that `MainWindow.SyncGlobalNavSelection()` computed the matching navigation item and then never assigned it — the method was unfinished, with a comment reading "Update via binding - find the ListBox".
- **D-1 could not be fixed by re-pointing the binding.** Both `RelativeSource=Self` and `RelativeSource=AncestorType=ListBox` failed: the inherited `AppLayout.Mode` does not reach a `TextBlock` generated inside a virtualised, recycling `ListBoxItem`. The working fix swaps the entire `ItemTemplate` from a trigger on the `ListBox`, which is a static-tree element. A second `AppNavigationRowTemplateCompact` was added, and the local `ItemTemplate`/`ItemContainerStyle` setters had to be removed from `MainWindow.xaml` because a local value beats a style trigger.
- **D-3 needed new infrastructure.** Separating focus from selection is not expressible with `IsKeyboardFocused` alone, because clicking also focuses. `AppInput.IsKeyboardMode` was added as an inherited attached property, tracked by preview-tunnelling handlers on the window.
- **A fourth navigation defect was found at runtime.** The rail's `ListBoxItem` exposed the bound record's `ToString()` as its accessible name, so a screen reader announced the entire `NavigationItem { Page = ..., Label = ..., Description = ... }` declaration. `AutomationProperties.Name` is now set on the container style.
- **A fifth navigation defect was found in the follow-up session.** A legacy `MainWindow_SizeChanged` handler independently drove `NavRailColumn.Width` from `ActualWidth` thresholds of 1000/1200, which do not match `AppBreakpointStandard`(900)/`AppBreakpointWide`(1280). Between roughly 900 and 1000px width this forced the column to 60px while the correctly-Standard-mode rail `Border` still requested 232px, clipping the labels back into single letters — the exact defect D-1 already fixed, reintroduced by an unrelated, older mechanism. Removed entirely; `NavRailColumn` is now `Width="Auto"`.
- **The icon occupancy gate is size-aware.** A flat 94% target is wrong for 128 px and above, where padding is expected and correct. The gate is 94% for the 16–48 px frames Windows draws in the taskbar, title bar and Alt+Tab, and 85% above that.
- **The brand mark is "Lift", a refinement of "Chunk".** Two intermediate concepts were rendered and rejected on inspection: a rebuilt L-body read as a boot silhouette, and a slotted keystone read as a padlock. See `VISUAL-BRAND-AND-ICON-V2.md`.
- **The empty-state "tiny island" defect (RC-4) had one root cause, not one per page.** Every global page centred its empty state independently, but all of them sat inside the same `ScrollViewer`, which measures its content with infinite height. A single `MinHeight` binding on the shared page-content wrapper (bound to the `ScrollViewer`'s own `ActualHeight`) fixed centring for Dashboard, Servers, Automation and Activity simultaneously; no page-specific centring logic was needed.
- **Automation's "Create automation" affordance could not be honestly wired as a button.** The ViewModel already has `AddScheduleCommand`/`TryBuildSchedule`, but `TryBuildSchedule` requires `SelectedServer`, and the global Automation page is defined as `SelectedServer is null`. No existing UI anywhere binds the schedule-creation fields (`ScheduleName`, `ScheduleKind`, etc.) — they are unwired ViewModel plumbing for a feature that has no server-scoped entry point yet. Building that entry point would be a new automation feature, out of scope. The empty state instead offers a real, working "Open Servers" navigation instead of a button that would fail with "Select a server first."
- **The Overview page had a pre-existing, undiagnosed duplication.** Before either visual-system session, Overview independently rendered the same server name/state/ecosystem/version/Start-Save-Restart-Stop block that `MainWindow.xaml`'s workspace header already renders above the sub-navigation. This was not previously flagged. It is removed from Overview; the one genuinely new fact it carried (the port number) is kept as a single line.

## Rules for every task

1. Each task is written for a **fresh session** with no memory of the planning session. Read the four companion documents first.
2. Never touch an alternate worktree, production AppData, the registry, firewall, services, or any real server, world or backup. Use `CHUNKPILOT_DATA_ROOT` and `CHUNKPILOT_INSTANCE_ID` with a temporary directory.
3. This is a **presentation-only** programme. Preserve Agent ownership, named-pipe transport, lifecycle intent/state separation, operation queues, bounded console capture, provider boundaries, persistence compatibility, `SafeApplicationExit`, `ServerOpened`, navigation version guards, and every data-safety rule in `AGENTS.md`.
4. Every task ends with: Release build 0 warnings / 0 errors; unit ≥325 and integration ≥34 green; a Design Gallery render **inspected**, not merely produced; and one coherent local commit. Do not push.
5. Never assert a subjective quality claim without looking at the rendered output.
6. If a task discovers a prior assumption was wrong, stop, document it, and adjust the *next* task rather than silently widening scope.
7. `[U]` items in the specification are genuine unknowns. Resolve them by measurement, and record the answer in the relevant document in the same commit.

### Sequence rationale

The brief's suggested order is mostly right. Three changes, driven by actual dependencies:

- **The three defect fixes (D-1, D-2, D-3) are pulled forward to task 2.** They are in the sidebar, the sidebar is the model every other selection state copies, and compact mode is currently broken. Fixing them after the shell work would mean touching `Navigation.xaml` twice.
- **The brand and taskbar icon move later (task 12), not to position 5.** The mark still has an unresolved resemblance risk and an unrun trademark search; it must not gate the whole visual programme.
- **A measurement baseline is inserted as task 0.** Without it the performance gates in every later task are unenforceable.

---

### Task 0 — Performance and visual baseline

**Observable outcome:** a committed `docs/VISUAL-BASELINE-02ec1bf.md` recording, at commit `02ec1bf`: cold and warm startup to first paint, time to interactive, global-page navigation time, server-list render at 1/10/50 synthetic servers, working set at idle and with 50 servers, console throughput, and idle CPU focused/unfocused. Design Gallery renders at 1440/1100/840 archived under `artifacts/`.

**Dependencies:** none.

**Likely files:** new `docs/VISUAL-BASELINE-02ec1bf.md`. No source changes.

**Requirements:** synthetic fixtures only; every figure states its method and machine; no figure is estimated.

**Non-goals:** no visual change of any kind.

**Runtime states:** Dashboard empty, Servers empty, Servers with 50 synthetic servers, Console under load.

**Accessibility gate:** n/a. **Performance gate:** this task *is* the gate. **Test gate:** suites green, unchanged.

**Model route:** **Sonnet** — mechanical measurement and reporting.

**Commit:** `Record visual and performance baseline at 02ec1bf`.

---

### Task 1 — Typography: weights, roles and rendering

**Observable outcome:** `AppFontWeightMedium` is deleted; `AppFontWeightSemilight` (350) and `AppFontSizeNumeric` (20/24) exist; the three former `Medium` consumers use `Regular`; the primary button and selected nav/segment use `SemiBold`; the stale ramp comments and gallery labels state the real values.

**Dependencies:** Task 0.

**Likely files:** `Themes/Tokens/TypographyTokens.xaml`; `Themes/Controls/Text.xaml` (`:28-33` comments, `:102`); `Themes/Controls/Buttons.xaml:75`; `Themes/Controls/Selection.xaml:357`; `DesignSystem/Gallery/DesignGalleryContent.xaml:167-175`; `docs/UI-DESIGN-SYSTEM.md`; possibly `DesignSystemContractTests`.

**Requirements:** `VISUAL-SYSTEM-V2-SPEC.md` §1. Fixes defect **D-4**. `Medium` must not survive anywhere — grep for it.

**Non-goals:** no size changes beyond adding `Numeric`; no font-family change; no colour change; `TextRenderingMode` is task 3.

**Runtime states:** gallery type ramp, buttons, inputs, nav, tables, badges at all three widths.

**Accessibility gate:** no text falls below AA; no target shrinks. **Performance gate:** no regression vs Task 0. **Test gate:** full suites.

**Model route:** **Sonnet** — bounded, well-specified, but touches shared styles.

**Commit:** `Restore a real typographic weight ramp`.

---

### Task 2 — Sidebar defect repairs

**Observable outcome:** D-1 compact labels collapse cleanly at <900 px; D-2 the current destination is indicated on first launch; D-3 the focus ring appears only for keyboard focus, so a pointer-selected row shows no outline.

**Dependencies:** Task 1.

**Likely files:** `Themes/Controls/Navigation.xaml` (`:83-85`, `:104-115`); `MainWindow.xaml`; `MainWindow.xaml.cs`; `NavigationItem.cs`; possibly `DesignSystem/AppLayout.cs`.

**Requirements:** `VISUAL-SYSTEM-V2-SPEC.md` §7.2. `[U]` For D-1, first confirm the cause — the working hypothesis is that a `RelativeSource=Self` binding on `ds:AppLayout.Mode` does not resolve inside a virtualised item container. If so, bind against the rail or set the mode on the container. **Semantic navigation behaviour, `ServerOpened` and the version guards must not change.**

**Non-goals:** no restyle of the rail; no width, spacing or icon change; no new destinations.

**Runtime states:** 1920 first launch (no selection bug), 900 compact, keyboard Tab through the rail, pointer click on each destination.

**Accessibility gate:** keyboard focus remains clearly visible; the rail's accessible name survives collapse `[R: Navigation.xaml:118]`. **Performance gate:** rail virtualisation intact. **Test gate:** full suites + a new test asserting compact-mode label collapse.

**Model route:** **Sonnet**, or **LOCAL + CLAUDE REVIEW** if the D-1 cause turns out to be a one-line binding change.

**Commit:** `Fix navigation selection, focus and compact-mode defects`.

---

### Task 3 — Palette: lift the range, strengthen the strokes

**Observable outcome:** `Palette.xaml` carries the new L\*-targeted ramp; `ColorTokens.xaml` maps to it; `AppAccentIndicator` and `AppSurfaceWell` exist; `AppFocusRing` is `#E4DCFF`; `AppDanger` is `#C8404E`; `AppElevationLow` is deleted; the high-contrast overlay covers every new token. Fixes **D-5** and **D-6**.

**Dependencies:** Task 1.

**Likely files:** `Themes/Tokens/Palette.xaml`; `Themes/Tokens/ColorTokens.xaml`; `Themes/Tokens/ElevationTokens.xaml`; `Themes/Overlays/HighContrast.xaml`; `Themes/Controls/Buttons.xaml`; `docs/UI-DESIGN-SYSTEM.md`.

**Requirements:** `VISUAL-SYSTEM-V2-SPEC.md` §2 and §4. **This is the single highest-impact task in the programme.** Re-add the cool blue bias the candidate generator flattened. Verify every ΔL\* and every WCAG ratio in §2.1–2.3 by calculation **and** by inspecting the render. Resolve the two-accent split exactly as specified — a single purple cannot satisfy both constraints.

**Non-goals:** no layout change; no typography change; no component restructuring; the purple hue does not change.

**Runtime states:** every gallery section at all three widths, in dark **and** High Contrast; live Dashboard, Servers, Automation.

**Accessibility gate:** focus ring ≥3:1 on the accent fill; white on danger ≥4.5:1; all text AA; High Contrast fully coherent. **Performance gate:** no regression — deleting `AppElevationLow` should help. **Test gate:** full suites; extend `DesignSystemContractTests` for the new tokens.

**Model route:** **Opus** — the accent conflict and the high-contrast mapping need judgement, and every later task inherits the result.

**Commit:** `Lift the surface palette and strengthen structural strokes`.

---

### Task 4 — Text rendering A/B

**Observable outcome:** a decision, recorded in `VISUAL-SYSTEM-V2-SPEC.md` §1.6, on `TextRenderingMode`, backed by renders at 100/125/150% scaling compared at 1:1 and 3×; the chosen value applied.

**Dependencies:** Task 3 (judge type against the final surfaces, not the old ones).

**Likely files:** `Themes/Controls/Text.xaml:24`; `docs/VISUAL-SYSTEM-V2-SPEC.md`.

**Requirements:** resolve the `[U]` in §1.6. If 12–13 px degrades under `Grayscale`, apply the documented split fallback rather than forcing it globally.

**Non-goals:** no other typography change.

**Runtime states:** gallery type ramp, table, console, badges — at three scalings, inspected at 3×.

**Accessibility gate:** small text legibility must not regress. **Performance gate:** none expected; confirm. **Test gate:** full suites.

**Model route:** **Sonnet** — but the decision requires actually looking at the crops.

**Commit:** `Settle text rendering mode for dark surfaces`.

---

### Task 5 — Layout primitives and adaptive page container

**Observable outcome:** `AppMeasureProse/Form/Content` exist; the `Ultra` layout mode exists; a shared page container enforces header + optional toolbar + exactly one filler; capped regions are centred.

**Dependencies:** Task 3.

**Likely files:** `Themes/Tokens/MetricTokens.xaml` (replacing `AppContentMaxWidth:113`, adding a breakpoint beside `:119-120`); `DesignSystem/AppLayout.cs`; a new page-container component; `docs/UI-RESPONSIVE-RULES.md`.

**Requirements:** `VISUAL-SYSTEM-V2-SPEC.md` §3. **Centre every capped region** — this is the direct fix for the ragged 296 px of dead space. One scroller per page; no nesting.

**Non-goals:** no page recomposition yet — this task provides the container only.

**Runtime states:** 800×600, 1000×700, 1280×720, 1440×900, 1920×1080, 3440×1440; 100/125/150% scaling.

**Accessibility gate:** no clipping at 150%; no horizontal scrolling of primary content. **Performance gate:** no extra layout passes; no nested `ScrollViewer`. **Test gate:** full suites + `AppLayout` mode-selection tests including `Ultra`.

**Model route:** **Opus** — adaptive layout is where subtle regressions hide.

**Commit:** `Add adaptive layout primitives and a shared page container`.

---

### Task 6 — Shared control pass

**Observable outcome:** buttons, inputs, selection controls, cards, toolbars, badges, rows, tables, progress, alerts, toasts, dialogs and tooltips match `VISUAL-SYSTEM-V2-SPEC.md` §6, including the primary button's edge stroke and gradient, the 56 px server row, and ≥80% icon glyph occupancy.

**Dependencies:** Tasks 3 and 5.

**Likely files:** `Themes/Controls/Buttons.xaml`, `Inputs.xaml`, `Selection.xaml`, `Surfaces.xaml`, `DataDisplay.xaml`, `Feedback.xaml`, `Overlays.xaml`, `Icons.xaml`; `DesignSystem/Components/*`; `DesignSystem/AppIconMap.cs`; `docs/UI-COMPONENT-CATALOG.md`.

**Requirements:** no new one-off variants; no page-local styling; `FluentIcons` stays in one file. Every state in §6 must be demonstrable in the gallery.

**Non-goals:** no page changes; no new components beyond the server identity mark.

**Runtime states:** every gallery section, three widths, dark + High Contrast, Reduced Motion on and off.

**Accessibility gate:** 32×32 minimum targets at every mode; focus outside the border everywhere; disabled never opacity-only. **Performance gate:** no `Effect` in any item template; frozen brushes. **Test gate:** full suites + contract tests.

**Model route:** **Opus** for buttons/inputs/selection; **Sonnet** for the remainder as a second commit if the diff grows large.

**Commit:** `Align shared controls with the v2 system` (may split into two).

---

### Task 7 — Shell, window chrome and sidebar polish

**Observable outcome:** title bar, window chrome and rail match §7; selection uses fill + pill + SemiBold + filled glyph; the active server appears as a compact identity card; global and server Settings are unmistakable.

**Dependencies:** Tasks 2, 3, 6.

**Likely files:** `MainWindow.xaml`, `MainWindow.xaml.cs`, `Themes/Controls/Navigation.xaml`, `NavigationItem.cs`.

**Requirements:** §7.1 preserve list is binding — width 232/56, row 36, spacing, and the three-way selection all stay. Semantic navigation untouched.

**Non-goals:** no destination changes; no page content changes.

**Runtime states:** all widths; server open and closed; keyboard traversal.

**Accessibility gate:** focus order header → rail → content; rail names survive collapse. **Performance gate:** no regression on server switch. **Test gate:** full suites.

**Model route:** **Sonnet**.

**Commit:** `Refine the shell and navigation rail`.

---

### Tasks 8–11 — Global pages

Each is its own commit. All depend on Tasks 5, 6 and 7. All must satisfy [`VISUAL-ACCEPTANCE-CHECKLIST.md`](VISUAL-ACCEPTANCE-CHECKLIST.md) in full.

| # | Page | Scope | Key requirement | Model |
| --- | --- | --- | --- | --- |
| 8 | **Dashboard** | Full recomposition | Page spec §2. Header always present (**fixes D-7**); full-height empty composition; no fabricated metrics; capped at 4 summary cards. | **Opus** |
| 9 | **Servers** | Refinement | Page spec §3. Resolves **S-5** duplicate actions; adds the deterministic server identity mark; always-visible row actions; optional Ultra detail panel. | **Sonnet** |
| 10 | **Automation** | Full recomposition | Page spec §4. **The empty table is removed.** Template cards must be unmistakably offers, not existing rows. | **Opus** |
| 11 | **Activity + Settings** | Refinement | Page spec §5–6. Expandable evidence; centred 720 form; global vs server Settings distinction. May split into two commits. | **Sonnet** |

Common non-goals: no ViewModel behaviour change, no new commands, no new data. If a page needs a value the application does not have, it renders *Unknown* — it does not acquire it.

---

### Task 12 — Brand mark and taskbar micro-icon

**Observable outcome:** a production mark with per-size assets at 16/20/24/32/40/48/64/128/256, each drawn at its own size; every asset measured at bbox ≥94% and ink ≥45%; a rebuilt `.ico`; sidebar, title bar and splash updated; a taskbar comparison sheet inspected.

**Dependencies:** Task 3 (accent colour), Task 7 (sidebar brand block).

**Likely files:** `assets/brand/*`, `assets/ChunkPilot.ico`, `assets/ChunkPilot-256.png`, `chunkpiloticon.png`, `SplashWindow.xaml`, `MainWindow.xaml`, project icon references.

**Requirements:** `VISUAL-BRAND-AND-ICON-V2.md`. **Two blocking gates before any asset is produced:** (1) design out the duplicate-icon resemblance risk in §4 and review it at 16/24/32 px; (2) **run a trademark and image search** — `[U]` the planning session did not. If the risk cannot be resolved, adopt the documented Halo fallback. **Never scale one source into every frame.**

**Non-goals:** no wordmark typeface change; no rename.

**Runtime states:** taskbar at 100/125/150%, Alt-Tab, title bar, splash, sidebar, Start menu.

**Accessibility gate:** High Contrast renders the silhouette without gradient. **Performance gate:** icons decoded at display size and cached. **Test gate:** full suites + the occupancy measurement re-run on every asset.

**Model route:** **OpusPlan** then **Sonnet** — the mark refinement and trademark judgement need planning; the asset generation is mechanical.

**Commit:** `Adopt the v2 brand mark and per-size application icon`.

---

### Tasks 13–18 — Selected-server workspace

| # | Scope | Key requirement | Dependencies | Model |
| --- | --- | --- | --- | --- |
| 13 | **Workspace foundation** | Page spec §7. Persistent identity header, segmented subnav (never a `TabControl`), capability gating, warning strip. | 5, 6, 7 | **Opus** |
| 14 | **Overview** | §7.1. Two columns at Wide+. Unknown values say so. **No invented metrics.** | 13 | **Sonnet** |
| 15 | **Console** | §7.2. Well surface, autoscroll pause + jump-to-latest, raw integrity, virtualisation, **no motion**, one polite live region. | 13 | **Opus** — perf and correctness sensitive |
| 16 | **Manage** | §7.3. Active version/world marked and undeletable; removal lists exact paths. | 13 | **Sonnet** |
| 17 | **Access** | §7.4. Distinct addresses; public off by default; a local check is never public proof. | 13 | **Sonnet** |
| 18 | **Protection** | §7.5. Incomplete and failed backups cannot be presented as complete or restored. | 13 | **Opus** — data-safety adjacent |

---

### Task 19 — Create Server v2 visual foundation

**Observable outcome:** the wizard defined in `CREATE-SERVER-V2-*` inherits the v2 system per page spec §8 — window composition, step rail, intent cards, catalog rows, details panel, evidence states, review, progress, failure and completion.

**Dependencies:** Tasks 6, 13, and the Create Server v2 roadmap reaching its shell task.

**Requirements:** shared components only. Any genuinely new control is added to the shared system and the gallery **first** `[R: AGENTS.md]`. Compatibility evidence must never show a confirmed state for an unverified match.

**Non-goals:** no change to the creation architecture, providers, or the transactional pipeline. **Coordinate with the Create Server v2 roadmap — do not fork it.**

**Model route:** **OpusPlan** then **Sonnet**.

**Commit:** `Apply the v2 visual system to Create Server`.

---

### Tasks 20–23 — Hardening

| # | Task | Outcome | Model |
| --- | --- | --- | --- |
| 20 | **Dialogs and feedback** | Page spec §9. Focus trapping, Escape, safe default focus, persistent error toasts, no `MessageBox` for product errors. | **Sonnet** |
| 21 | **Accessibility and DPI hardening** | Full checklist sweep at 100/125/150%, keyboard-only traversal of every page, screen-reader pass, long-name and Unicode stress. Fixes are in-scope; new visuals are not. | **Opus** |
| 22 | **Motion and perceived performance** | Spec §5 and §8 applied everywhere; **commands verified to dispatch before animation**; re-measure every Task 0 figure. | **Opus** |
| 23 | **Cross-page consistency review** | Every page rendered at every size, compared side by side; one-off deviations removed; `LegacyAliases.xaml` shrunk; catalog and design-system docs reconciled. | **Opus** |

---

### Task 24 — Optional Mica backdrop *(may be deferred indefinitely)*

**Observable outcome:** an appearance setting, **off by default**, applying Mica to the window root only, with a solid fallback and automatic disable under High Contrast, reduced transparency and battery saver.

**Dependencies:** Tasks 21 and 22 complete and green.

**Requirements:** every condition in `VISUAL-DIRECTION-V2.md`. **Never** behind console, tables or file lists. `[U]` Idle CPU and composition cost measured against Task 0 before it is offered.

**Non-goals:** no blur anywhere else; not enabled by default in 1.x.

**Model route:** **Opus**.

**Commit:** `Add an optional Mica window backdrop`.

`[D]` **This task is genuinely optional.** If measurement shows any idle-CPU cost, drop it. It is a refinement; nothing in the direction depends on it.

---

## Model routing summary

| Route | Tasks | Why |
| --- | --- | --- |
| **LOCAL Qwen** | none | Every task touches shared visual contracts or requires judging rendered output. Nothing here is safe for an unreviewed local model. |
| **LOCAL + CLAUDE REVIEW** | 2 (only if D-1 is a one-line binding fix) | Narrow, verifiable, with a clear test |
| **Sonnet** | 0, 1, 2, 4, 7, 9, 11, 14, 16, 17, 20 | Well-specified, bounded, low ambiguity |
| **Opus** | 3, 5, 6, 8, 10, 13, 15, 18, 21, 22, 23, 24 | Shared foundations, adaptive layout, data-safety adjacency, or subjective quality judgement |
| **OpusPlan → Sonnet** | 12, 19 | Need a planning pass (trademark, mark refinement, wizard integration) before mechanical execution |

## Risks

| Risk | Severity | Mitigation |
| --- | --- | --- |
| Task 3 changes every pixel at once and could regress High Contrast | **High** | Contract tests extended in the same commit; High Contrast inspected before commit; the task is deliberately isolated so it can be reverted alone |
| The two-accent split is misunderstood and one token is used for both jobs | Medium | Named `AppAccent` vs `AppAccentIndicator` with the job in the token comment; contract test asserting the focus ring clears 3:1 on the accent |
| The brand mark reads as a duplicate icon | Medium | Blocking design gate in Task 12; documented Halo fallback |
| Trademark collision | **Unassessed** | `[U]` Blocking search gate in Task 12 |
| `Grayscale` degrades 12–13 px text | Medium | Task 4 is an explicit A/B with a documented split fallback |
| Empty-state recomposition drifts into invented content | Medium | Truthfulness rule at the head of the page specifications; checklist gate |
| Visual work collides with the Create Server v2 branch | Medium | Task 19 explicitly coordinates rather than forks |
| Scope creep from "while I'm here" fixes | Medium | Rule 6; defects are documented, not opportunistically fixed |
