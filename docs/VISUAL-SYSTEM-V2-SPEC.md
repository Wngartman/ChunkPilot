# ChunkPilot visual system v2 — specification

Status: planning document. Implementation-ready specification for the direction chosen in [`VISUAL-DIRECTION-V2.md`](VISUAL-DIRECTION-V2.md). Evidence labels are defined in [`VISUAL-AUDIT-V2.md`](VISUAL-AUDIT-V2.md#evidence-labels).

This document specifies tokens and components. Page composition is in [`VISUAL-PAGE-SPECIFICATIONS-V2.md`](VISUAL-PAGE-SPECIFICATIONS-V2.md).

**Every candidate hex below is a starting value verified by calculation, not a final value.** Final values are confirmed by rendering the Design Gallery and re-measuring. Ratios quoted are computed, not estimated.

---

## 1. Typography

### 1.1 The two findings that drive this section

`[L]` **Segoe UI Variable resolves correctly and must be kept.** All three optical faces resolve to `SEGUIVAR.TTF` with the correct named instance. Do not bundle a third-party font. There is no licensing, distribution, file-size or fallback cost worth paying here, because there is nothing to fix in the family.

`[L]` **Medium (500) does not exist on Windows and must be abolished.** No Segoe UI family ships a Medium face; WPF rounds it to SemiBold. `AppFontWeightMedium` is therefore a lie that silently collapses the middle of the hierarchy.

### 1.2 Weight tokens

| Token | Value | Availability | Use |
| --- | --- | --- | --- |
| `AppFontWeightRegular` | `Normal` (400) | ✔ | Body, secondary, metadata, table cells, console, **Display** |
| `AppFontWeightSemilight` | `350` — **new** | ✔ verified present as `Semilight` | Reserved for a future hero numeral ≥40 px. **Currently unused.** |
| `AppFontWeightStrong` | `SemiBold` (600) | ✔ | Titles, card titles, server names, the one strong value in a row |
| ~~`AppFontWeightMedium`~~ | — | ✘ **removed** | Every consumer moved to `Regular` |

> **Implemented.** The old `AppFontWeightSemibold` key was renamed `AppFontWeightStrong` so the token names describe intent rather than a specific face. Display uses **Regular**, not Semilight: at 30 px on a dark surface Semilight blooms and reads as thin rather than refined, and the brief explicitly rules out weak light text for important information. Semilight is retained as a verified-present token but has no consumer until a genuine hero numeral exists.

`[D]` The three current `Medium` consumers all move to **`Regular`**, not to `SemiBold`:

| Consumer | File | Change |
| --- | --- | --- |
| `AppLabelText` | `Themes/Controls/Text.xaml:102` | → `Regular`, keep the secondary foreground |
| Segmented control | `Themes/Controls/Selection.xaml:357` | → `Regular`; the *selected* segment takes `SemiBold` |
| Button text | `Themes/Controls/Buttons.xaml:75` | → `Regular`; **primary** button keeps `SemiBold` |

`[I]` This single change is expected to do more for "modern typography" than any other edit in this plan. It removes SemiBold from the majority of the pixels currently wearing it and restores an actual contrast between chrome and headings.

### 1.3 Size and line-height ramp

`[D]` The current ramp (30/24/19/16/14/13/12) is sound; the steps between 19/16/14 are tight but workable once weight carries hierarchy. Two changes: add a `Numeric` role, and fix the stale documentation (defect D-4).

| Token | Size | Line height | Change |
| --- | --- | --- | --- |
| `AppFontSizeDisplay` | 30 | 36 | unchanged |
| `AppFontSizeTitleLarge` | 24 | 30 | unchanged |
| `AppFontSizeTitle` | 19 | 26 | unchanged |
| `AppFontSizeSubtitle` | 16 | 22 | unchanged |
| `AppFontSizeBody` | 14 | 20 | unchanged |
| `AppFontSizeSmall` | 13 | 18 | unchanged |
| `AppFontSizeCaption` | 12 | 17 | unchanged |
| `AppFontSizeNumeric` | 20 | 24 | **new** — a single measured value in a summary tile |

### 1.4 Semantic roles

Family column: **D** = `Segoe UI Variable Display`, **T** = `Text`, **S** = `Small`, **M** = `Cascadia Mono, Consolas, Courier New`. Fallback for D/T/S is `Segoe UI` (already declared `[R: TypographyTokens.xaml:12-15]`).

| Role | Fam | Size | Weight | LH | Use | Max measure | Misuse to avoid |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Brand / app identity | D | 19 | SemiBold | 26 | Sidebar wordmark, splash | — | Never used as a page title |
| Hero value | D | 30 | Semilight | 36 | One defining number or state per page | — | Never for prose |
| Numeric value | D | 20 | SemiBold | 24 | Summary-tile figures | — | Never for text labels |
| Page title | D | 24 | SemiBold | 30 | `AppPageHeader` title | — | Never more than one per page |
| Page subtitle | T | 14 | Regular | 20 | One line under the page title | 90ch | Never a second sentence |
| Section title | T | 19 | SemiBold | 26 | Major page division | — | Not inside a card |
| Card title | T | 16 | SemiBold | 22 | `AppCard` header | — | Not for a whole row of cards' body text |
| Server title | T | 16 | SemiBold | 22 | Server name in rows and workspace header | — | Never trimmed below 20 chars |
| Body | T | 14 | Regular | 20 | Default | 80ch | — |
| Strong body | T | 14 | SemiBold | 20 | The one primary value in a row | — | Never two per row |
| Supporting body | T | 14 | Regular | 20 | Secondary foreground | 80ch | Not for critical state |
| Metadata | S | 13 | Regular | 18 | Muted foreground; version, size, time | — | Never for an action label |
| Field label | S | 13 | **Regular** | 18 | Above an input, always present | — | A placeholder is never a label |
| Caption | S | 12 | Regular | 17 | Badge and dense-row text | — | Never for an explanation |
| Badge | S | 12 | SemiBold | 17 | `AppStatusBadge` | — | Text always required, never colour alone |
| Navigation | T | 14 | Regular | 20 | Rail rows; **selected takes SemiBold** | — | — |
| Button | T | 14 | Regular | 20 | All buttons; **primary takes SemiBold** | — | Never uppercase |
| Table header | S | 12 | SemiBold | 17 | Column headers, sentence case | — | **Not uppercase** — see 1.5 |
| Table body | T | 14 | Regular | 20 | Cells | — | — |
| Empty-state title | D | 24 | SemiBold | 30 | — | 40ch | Not 16 px — see the audit |
| Empty-state body | T | 14 | Regular | 20 | — | **56ch** | Never wider than 56ch |
| Dialog title | D | 19 | SemiBold | 26 | — | — | — |
| Mono technical | M | 13 | Regular | 18 | Paths, hashes, addresses, versions | — | Never for prose |
| Console | M | 13 | Regular | 18 | Log output | — | Never trimmed; wrap or scroll |

### 1.5 Uppercase policy

`[D]` Uppercase is permitted in exactly one role: the **section eyebrow** (`AppEyebrowText`, 12 px SemiBold, muted, `+0.5px` tracking). It is prohibited in table headers, buttons, badges, tabs and navigation. `[L]` The gallery currently uses uppercase for group eyebrows only, which is correct; the rule is recorded so it stays that way.

### 1.6 Rendering

| Setting | Current | Proposed | Rationale |
| --- | --- | --- | --- |
| `TextFormattingMode` | `Ideal` `[R: Text.xaml:23]` | **`Ideal`** — keep | Correct for a scalable modern UI |
| `TextRenderingMode` | `ClearType` `[R: Text.xaml:24]` | **`Grayscale`** — change, but gate it | `[L]` At 3× zoom, light-on-dark text shows pronounced orange/blue subpixel fringing. Grayscale AA is cleaner in this exact case. |
| `UseLayoutRounding` | `True` `[R: Text.xaml:22]` | keep | |
| `SnapsToDevicePixels` | per-element `[R: Navigation.xaml:59]` | keep on borders/dividers, never on text | |

`[U]` **The `Grayscale` change must be A/B verified before it is made unconditional.** Render the gallery both ways at 100%, 125% and 150% scaling, compare at 1:1 and 3×, and confirm small text (12–13 px) does not become mushy. `[I]` The risk is real: grayscale AA loses horizontal resolution, which hurts most at exactly the sizes ChunkPilot uses for metadata. If 13 px degrades, the fallback is `ClearType` retained for ≤13 px and `Grayscale` for ≥14 px, applied at the text-style level.

`[D]` DPI: no per-scale font overrides. The ramp is in device-independent pixels and Windows scaling handles it. Line heights are already explicit with `BlockLineHeight` `[R: Text.xaml:38]`, which prevents scale-dependent baseline drift.

---

## 2. Colour

### 2.1 Neutral ramp

`[D]` Lift the working range from L\* 3.1–14.5 to L\* 6–18, widen the strokes, and keep the cool tint. Candidate values below are **calculated to hit the stated L\***; the implementer must re-add the cool blue bias (the current ramp's B channel runs ~6–8 sRGB points above R/G — preserve that character, the generator below flattened it).

| Token | Target L\* | Candidate | Role |
| --- | --- | --- | --- |
| `PaletteNeutral00` | 3 | `#0B0B0E` | Console and data wells only — the one true black level |
| `PaletteNeutral05` | 6 | `#131316` | Sidebar rail, sunken insets |
| `PaletteNeutral10` | 10 | `#1B1B1F` | **Application canvas** |
| `PaletteNeutral14` | 14 | `#242429` | Secondary surface, toolbars |
| `PaletteNeutral18` | 18 | `#2C2C32` | **Raised card** |
| `PaletteNeutral22` | 22 | `#35353C` | Hover |
| `PaletteNeutral26` | 26 | `#3D3D45` | Pressed, popup/overlay surface |
| `PaletteNeutral32` | 32 | `#4B4B53` | **Structural stroke** |
| `PaletteNeutral40` | 40 | `#5E5E66` | Strong stroke, dividers needing emphasis |
| `PaletteNeutral52` | 52 | `#7C7C84` | Disabled text |
| `PaletteNeutral62` | 62 | `#96969E` | Muted text |
| `PaletteNeutral74` | 74 | `#B6B6BE` | Secondary text |
| `PaletteNeutral92` | 92 | `#E8E8ED` | Primary text |

`[L]` Verified against the direction's targets:

| Target | Result |
| --- | --- |
| Working range sunken → raised | L\* 5.9 → 18.0 ✔ |
| Canvas → raised card, ΔL\* ≥ 4 | **8.2** ✔ |
| Raised card → stroke, ΔL\* ≥ 12 | **14.0** ✔ |
| Raised → hover, ΔL\* ≥ 4 | **4.2** ✔ |
| Hover → pressed, ΔL\* ≥ 3 | **3.6** ✔ |
| Primary text on canvas | **14.09:1** (was 16.99 — softer, still far above AA) ✔ |
| Secondary on canvas | 8.51:1 ✔ · Muted on raised **4.73:1** ✔ AA |

### 2.2 The accent conflict, and how it is resolved

`[L]` Lifting the surfaces creates a genuine, unavoidable conflict that the implementer will otherwise rediscover the hard way:

- On the new raised card (L\* 18), the current accent `#7B5CE0` measures only **2.96:1** — below the 3:1 needed for a fill to be a perceivable component boundary.
- Lightening the accent to fix that (`#8A6BF0` → 3.62:1) drops **white-on-accent to 3.86:1**, below the 4.5:1 AA minimum for the 14 px primary-button label.

**You cannot satisfy both with one purple.** `[D]` The resolution is two accent tokens with different jobs:

| Token | Candidate | White on it | vs raised card | Job |
| --- | --- | --- | --- | --- |
| `AppAccent` | `#7B5CE0` *(unchanged)* | **4.72:1** ✔ AA | 2.96:1 | **Filled** primary button and other accent fills that carry a label. The label carries the contrast; add a 1 px `AppAccentPressed` stroke so the button's own edge clears 3:1. |
| `AppAccentIndicator` | `#9B7DF5` **new** | 3.16:1 ✘ | **4.41:1** ✔ | **Unlabelled** accent marks: the rail selection pill, selected-row edges, active tab underline, progress fill. Never carries text. |
| `AppAccentHover` | `#8E72EC` | — | — | unchanged |
| `AppAccentPressed` | `#6A4CC8` | — | — | unchanged; doubles as the primary button's edge stroke |
| `AppAccentSubtle` | `#241E3A` | — | — | Selected-row tint; retune to sit ΔL\* ≈ 4 above the surface it tints |
| `AppTextAccent` | `#B9A6FF` | — | 6.62:1 ✔ | Accent-coloured text and links |
| `AppFocusRing` | `#E4DCFF` **changed** | — | 10.62:1 | **Fixes D-5.** vs the accent fill: **3.21:1** ✔ (was 2.24:1) |

`[D]` The purple hue itself does not change. `[L]` `#7B5CE0` is accessible and well-judged; the audit found it under-used, not wrong. Resist re-hueing — a hue change would invalidate the brand mark work in [`VISUAL-BRAND-AND-ICON-V2.md`](VISUAL-BRAND-AND-ICON-V2.md) for no measured gain. **No secondary brand colour is introduced.**

### 2.3 Status colours

`[D]` Status hues keep their identity. One value changes, to fix a real accessibility failure.

| Token | Current | Proposed | Reason |
| --- | --- | --- | --- |
| `AppDanger` | `#D9525F` | **`#CE4553`** *(implemented)* | Fixes **D-6**: white-on-danger rises from **3.95:1** to **4.56:1** ✔ AA. The planned `#C8404E` gave a better 4.88:1 for text but fell to **2.88:1** against a lifted card, failing non-text contrast for the button's own edge. `#CE4553` is the value that clears **both** (4.56:1 text, 3.08:1 edge). |
| `AppSuccess` | `#3FBF7F` | unchanged | |
| `AppWarning` | `#D9A038` | unchanged | |
| `AppInfo` | `#4A97D9` | unchanged | |
| `AppNeutral` | `#6A6A78` | retune to the new ramp | Must keep reading as *unknown*, never as healthy |
| `*Subtle` tints | — | retune all four | Each must sit ΔL\* 4–6 above the surface it banners on, not near-black |
| `AppAccentMuted` | `#4A3D7A` | **retire or restrengthen** | `[L]` 1.85:1 on a raised surface — too weak to indicate anything (S-6) |

`[D]` Status colour is never the only signal. Every status carries an icon and a text label. `[R]` The system already enforces this `[Navigation.xaml:8-9]`; it is restated because the lifted palette makes coloured fills more tempting.

### 2.4 High Contrast

`[D]` The overlay `[R: Themes/Overlays/HighContrast.xaml]` continues to replace brushes wholesale from system colours. Two additions required by this spec:

- `AppAccentIndicator` → `SystemColors.HighlightBrush`.
- `AppFocusRing` → `SystemColors.HighlightTextBrush` (or `ControlText`), which must clear 3:1 against both the accent and the surface in the active system theme.

`[D]` Any new token added anywhere in this document must be added to the high-contrast overlay in the same commit. This is already the repository rule `[R: AGENTS.md]`.

---

## 3. Layout

### 3.1 Why the interface reads as empty, small, flat and cramped at once

`[I]` Four separate causes, commonly mistaken for one:

| Symptom | Actual cause |
| --- | --- |
| Too empty | Nothing owns the vertical remainder of a page (RC-4) |
| Too small | Empty states and summary content are sized to their text, then centred in a large void |
| Too flat | Palette range and stroke strength (RC-1) — a layout problem only in appearance |
| Too constrained | `AppContentMaxWidth` 1120 applied **left-aligned**, producing ragged dead space rather than symmetric margins `[L]` |

### 3.2 Content width and alignment

`[D]` Replace the single `AppContentMaxWidth` with three measures chosen by content type, and **always centre a capped region**.

| Measure | Value | Applies to |
| --- | --- | --- |
| `AppMeasureProse` | 640 | Explanatory text, empty-state bodies, settings descriptions |
| `AppMeasureForm` | 720 | Settings, single-column forms, wizard steps `[R: MainWindow.xaml:487 already uses 720]` |
| `AppMeasureContent` | 1280 | Cards, summary grids, dashboards |
| *(uncapped)* | — | Lists, tables, console, file browsers — these earn full width |

`[D]` Rules:

1. A capped region is **centred**, never left-aligned. This is the direct fix for the 296 px of ragged dead space `[L]`.
2. Lists, tables and the console are **never** capped. Width is genuinely useful to them.
3. Beyond 1600 px, a capped content region stops growing and the surplus becomes symmetric margin — that is a *composed* margin, not dead space, provided rule 4 holds.
4. **Every page declares a vertical filler.** Exactly one region per page has `Height = *`. If a page has no natural filler, it uses a full-height composition (see 3.5), not a centred island.

### 3.3 Breakpoints

`[D]` Keep the existing three modes `[R: MetricTokens.xaml:119-120]` and add one. `AppLayout.Mode` already delivers these `[R: DesignSystem/AppLayout.cs]`.

| Mode | Width | Rail | Page padding | Columns |
| --- | --- | --- | --- | --- |
| Compact | < 900 | 56 icon-only | 16,14,16,16 | 1 |
| Standard | 900–1279 | 232 | 24,20,24,24 | 1–2 |
| Wide | 1280–1799 | 232 | 32,28,32,32 | 2–3 |
| **Ultra** *(new)* | ≥ 1800 | 232 | 40,32,40,40 | 2–3, capped and centred |

`[D]` Ultra does **not** add a fourth column and does **not** stretch cards further. It adds margin and, where specified, a detail panel. `[I]` Stretching every card across an ultrawide monitor is the failure mode the brief explicitly warns against, and it is worse than margin.

### 3.4 Verified size matrix

| Size | Mode | Behaviour |
| --- | --- | --- |
| 800×600 | Compact | Single column; rail icon-only; toolbars wrap to two rows; dialogs go to 92% width |
| 1000×700 | Standard | Single column; toolbar on one row; empty states full-height |
| 1280×720 | Wide | Two columns; vertical space is tight — filler regions must scroll, not clip |
| 1440×900 | Wide | Reference design size |
| 1920×1080 | Ultra | Content capped 1280 and centred; Servers gains an optional detail panel |
| 3440×1440 | Ultra | Identical to 1920 plus symmetric margin. Never a fourth column. |

`[D]` At 125% and 150% scaling the mode is chosen from **device-independent** width, so a 1920 physical / 150% display is 1280 DIP = Wide, not Ultra. That is correct and intended.

### 3.5 Empty-state layout — the single biggest composition change

`[S][L]` Current: a text block sized to its content, centred in the page, at any window size. At 1920×1040 that is a ~330×110 island in a ~1.6 Mpx field.

`[D]` Replacement — **empty states are full-height compositions**:

```
┌─ page padding ────────────────────────────────────────────┐
│ AppPageHeader  (ALWAYS present — fixes defect D-7)        │
├───────────────────────────────────────────────────────────┤
│                                                           │
│   ┌── centred column, max 640 ──┐   ← vertically centred  │
│   │  mark / illustration  64px  │      in the *filler*,   │
│   │  Empty-state title    24px  │      not the page       │
│   │  Explanation      ≤56ch     │                         │
│   │  [Primary]  [Secondary]     │                         │
│   └─────────────────────────────┘                         │
│                                                           │
│   ┌── "what happens next" strip, max 1280, centred ──┐    │
│   │  3 truthful capability cards, equal width        │    │
│   └──────────────────────────────────────────────────┘    │
│                                                           │
└───────────────────────────────────────────────────────────┘
```

`[D]` Rules:

- The page header is always present. An empty state never replaces page identity.
- The centred column is vertically centred **within the filler region**, with the "next" strip anchored below it — so the composition occupies the page instead of floating in it.
- The strip is omitted below 700 px height and in Compact mode.
- Its cards state only what ChunkPilot verifiably does. No metrics, no fake counts, no placeholder rows.

### 3.6 Scroll ownership

`[D]` Unchanged in principle and already correct `[R: AGENTS.md]`; restated because the new full-height compositions make it easy to break:

- Exactly one scroller per page, owned by the page container.
- Console, tables and file lists virtualise internally and **must not** be nested inside the page scroller — they are the filler region and size to it.
- No horizontal scrolling of primary content at any supported size. Toolbars wrap; tables drop or collapse columns.

---

## 4. Surface and depth

`[D]` Depth is carried by **surface value first, stroke second, shadow only where something genuinely floats**. `[R: ElevationTokens.xaml:4-8]` This is the existing rule and it is correct — the audit's finding was that the values were too timid, not that the model was wrong.

| Surface | Token | Level | Stroke | Shadow |
| --- | --- | --- | --- | --- |
| Application canvas | `AppSurfaceCanvas` | L\* 10 | — | none |
| Sidebar rail | `AppSurfaceSunken` | L\* 6 | right edge, `AppStrokeSubtle` | none |
| Title bar | inherits canvas | L\* 10 | bottom hairline | none |
| Main content | canvas | L\* 10 | — | none |
| Standard card | `AppSurface` | L\* 14 | `AppStroke` | none |
| Raised card | `AppSurfaceRaised` | L\* 18 | `AppStroke` | none |
| Toolbar | `AppSurface` | L\* 14 | bottom hairline | none |
| Selected server context | `AppAccentSubtle` | ΔL\* +4 over parent | 3 px `AppAccentIndicator` left edge | none |
| Console / data well | `AppSurfaceWell` **new** | L\* 3 | inset hairline | none |
| Dialog | `AppSurfaceOverlay` | L\* 26 | `AppStroke` | `AppElevationDialog` |
| Popover / menu | `AppSurfaceOverlay` | L\* 26 | `AppStroke` | `AppElevationOverlay` |
| Tooltip | `AppSurfaceOverlay` | L\* 26 | `AppStroke` | `AppElevationOverlay` |
| Hover | `AppSurfaceHover` | +ΔL\* 4 | — | none |
| Pressed | `AppSurfacePressed` | +ΔL\* 3 over hover | — | none |
| Focus | — | — | 2 px `AppFocusRing`, outside the control | none |
| Disabled | `AppSurfaceDisabled` | parent level | `AppStrokeDisabled` | none |
| Skeleton | `AppSurfaceHover` | — | — | none; a single 1.1 s opacity pulse, disabled under Reduced Motion |
| Success / Warning / Danger / Info | `App*Subtle` | ΔL\* +4–6 over parent | matching `App*` stroke | none |

`[D]` **Shadows on cards remain prohibited.** `[R: ElevationTokens.xaml:6-8]` `AppElevationLow` is currently invisible `[L]` and should be **deleted** rather than strengthened — with the lifted ramp, surface value alone separates a card, and a large scrolling list of shadowed cards is exactly the WPF cost the repository has correctly avoided.

`[D]` **No blur, no glass, no texture, no noise** anywhere except the optional window backdrop below.

### 4.1 Optional Mica backdrop

`[D]` Deliberately last in the roadmap. Conditions are in [`VISUAL-DIRECTION-V2.md`](VISUAL-DIRECTION-V2.md#optional-windows-backdrop). Summary: window root only; never behind console, tables or file lists; solid fallback on Windows 10 and on any failure; auto-disabled under High Contrast, reduced-transparency and battery saver; off by default in 1.x; `[U]` idle CPU and composition cost measured before it is offered.

---

## 5. Motion

`[D]` The existing tokens are correct `[R: MotionTokens.xaml]` — 90 / 150 / 220 ms, cubic ease-out, no overshoot. **Do not retime them.** The work is in coverage and discipline, not duration.

| Interaction | Duration | Property | Notes |
| --- | --- | --- | --- |
| Button / row hover | 90 ms | `Background` opacity | Enter only; exit is instant |
| Button press | **0 ms** | — | Must be instantaneous. Never animate a press. |
| Selection move (rail pill) | 150 ms | `TranslateTransform.Y` | The pill slides; the fill cross-fades |
| Page transition | 150 ms | `Opacity` + 8 px `TranslateTransform.Y` | `[R: AppMotionOffset]` already exists |
| Content refresh | 90 ms | `Opacity` | Never re-animate on every binding update |
| Advanced-section expand | 150 ms | `Height` | The one sanctioned layout animation; see below |
| Dialog open | 150 ms | `Opacity` + 0.98→1 `ScaleTransform` | Scale about the centre |
| Toast | 150 ms in / 90 ms out | `Opacity` + `TranslateTransform.X` | |
| Server state change | 150 ms | `Opacity` cross-fade of the badge | Never a colour tween — the intermediate colour would be a state that is not true |
| Empty → populated | 150 ms | `Opacity`, staggered ≤3 items × 40 ms | Never stagger a long list |
| Progress (determinate) | continuous | `Width` | |
| Busy (indeterminate) | 1.1 s cycle | `RotateTransform` | `[R: AppDurationBusyCycle]` |
| Success confirmation | 150 ms | `Opacity` | **Only after the operation is verified**, never on optimistic completion |
| Error appearance | **0 ms** | — | Errors appear instantly. Never animate a failure into view. |

`[D]` Rules:

- **Animate `Opacity` and `Transform` only.** The single exception is the expand/collapse `Height`, which is bounded, user-initiated and one-at-a-time.
- **Never animate** `Width`/`Height` of list items, `Margin`, `Padding`, `Foreground`, `FontSize`, or any property on a virtualised item container.
- **No animation in the console or in any dense data surface.** Ever.
- **No idle or decorative motion.** Nothing loops except an active busy indicator.
- **Commands never wait for animation.** A click dispatches its command on the same input event; visuals catch up. This is the whole of "Discord immediacy" and it is an ordering rule, not a timing one.
- All animations are interruptible and are torn down when their element is recycled.
- Reduced Motion: both existing mechanisms remain `[R: MotionTokens.xaml:4-9]` — durations zeroed by overlay *and* `AppMotion.IsEnabled` gating storyboard triggers so they never start.

---

## 6. Components

Shared rules for every component below: radius from `MetricTokens`; colour from semantic tokens via `DynamicResource`; typography from a named role in §1.4; focus is a 2 px `AppFocusRing` **outside** the control's border, never an inset that shifts layout; disabled never relies on opacity alone; every icon-only control has a tooltip and an `AutomationProperties.Name`.

### 6.1 Buttons

| Variant | Height | Padding | Radius | Fill | Stroke | Text |
| --- | --- | --- | --- | --- | --- | --- |
| Primary | 36 | 16,8 | 6 | `AppAccent` + ≤6% vertical gradient | 1 px `AppAccentPressed` *(clears the 3:1 edge)* | 14 **SemiBold** on `AppTextOnAccent` |
| Secondary | 36 | 16,8 | 6 | `AppSurface` | 1 px `AppStroke` | 14 Regular |
| Subtle | 36 | 12,8 | 6 | transparent | none | 14 Regular, secondary |
| Danger | 36 | 16,8 | 6 | **`#C8404E`** | 1 px darker | 14 SemiBold, white **4.88:1** ✔ |
| Icon | 32×32 | — | 6 | transparent | none | 20 px glyph |
| Split / overflow | 36 | — | 6 | as parent | divider hairline | `…` overflow uses a menu popover |

States: hover → surface +ΔL\* 4, 90 ms; pressed → +ΔL\* 3, **0 ms**; focus → 2 px ring; disabled → `AppSurfaceDisabled` + `AppTextDisabled`, no gradient; loading → busy indicator replaces the icon, label persists, control disabled.

`[D]` `[L]` Current button height renders ~36 px, which is correct. **Do not shrink buttons.** Compact mode keeps 36 — target size is an accessibility floor, not a density dial.

### 6.2 Inputs

| Component | Height | Radius | Notes |
| --- | --- | --- | --- |
| Text / password / search | 32 | 6 | Fill `AppSurfaceSunken`, 1 px `AppStroke`; focus adds the ring **and** an accent bottom edge |
| Numeric | 32 | 6 | Steppers 20 px, right-aligned; value is never left blank on focus loss |
| Combo box | 32 | 6 | Popup is `AppSurfaceOverlay`; **the chosen item is ticked, not merely tinted** `[S]` already correct |
| Check box | 20 box, 32 row | 4 | Indeterminate is a bar, never a faded tick |
| Radio | 20, 32 row | pill | |
| Switch | 36×20 | pill | Applies immediately; a check box belongs to a form that is confirmed |
| Segmented | 32 | 6 | Selected: `AppSurfaceRaised` + **SemiBold**; unselected Regular |
| Slider | 20 track area | pill | 4 px track, 14 px thumb; always paired with a numeric readout |

`[D]` Every input has a persistent label above it. A placeholder is a hint and never a substitute `[R]` — already the rule, restated.

### 6.3 Data display

| Component | Height | Notes |
| --- | --- | --- |
| Card | auto | `AppSurface`, 8 radius, 1 px `AppStroke`, 16 padding, no shadow |
| Raised card | auto | `AppSurfaceRaised`, otherwise identical; **one per surface maximum** |
| Toolbar | 48 | `AppSurface`, bottom hairline; wraps in Compact |
| Status badge | 22 | Pill, 12 SemiBold, dot + text; text always required |
| **Server row** | **56** *(was 52)* | Identity mark 32 px, name 16 SemiBold, meta 13 muted, state badge, action cluster. Selected: `AppAccentSubtle` + 3 px `AppAccentIndicator` left edge |
| Information row | 32 | Label `AppInfoRowLabelWidth` 180, value strong; **an unknown value says so** and is never an empty gap `[R]` |
| Table | 40 header / 44 row | Header 12 SemiBold sentence case; 1 px row hairline; `AppSurfaceHover` on hover; virtualised |
| List row | 36 | Hairline separated |
| Console | 20/line | `AppSurfaceWell` (L\* 3), 13 mono, virtualised, bounded, no animation, raw text preserved |
| Progress bar | 4 | Determinate only when the total is known |
| Busy indicator | 18 | Static under Reduced Motion `[R: AppBusyIndicator]` |
| Operation panel | auto | Title, sub-state, progress, operation id in mono, cancel |
| Alert | auto | Tone surface + tone stroke + icon + text; never colour alone |
| Toast | 360 wide | `AppSurfaceOverlay`, `AppElevationOverlay`, auto-dismiss except on error |
| Empty state | full-height | See §3.5 |
| Dialog | 480 / 92% in Compact | `AppElevationDialog`, focus trapped, Escape cancels, initial focus on the safe action |
| Tooltip | auto | 10,6 padding; never the only source of essential information |
| Scrollbar | 10 | `[S]` user-confirmed acceptable — **do not change** |
| Navigation item | 36 | See §7 |
| Page header | auto | Title 24, subtitle 14, status slot, action cluster |
| Section header | auto | 19 SemiBold, optional 12 uppercase eyebrow above |

### 6.4 Icons

`[D]` `[L]` Icons currently render optically small for their nominal box (S-4). Three changes:

1. Keep the scale tokens (16 / 20 / 24 / 32) but specify **glyph occupancy ≥ 80% of the nominal box**. `[L]` The current Fluent glyphs sit well below that, which is why `Small`, `Medium` and `Large` look nearly identical.
2. Use the **filled** Fluent variant for selected navigation and for status; keep regular weight elsewhere. `[R: Navigation.xaml:8]` The system already intends a filled glyph on selection.
3. Empty-state and brand marks step up to 48–64 px.

`FluentIcons` continues to appear in exactly one file `[R: AGENTS.md]`.

---

## 7. Shell and sidebar

`[S][L]` The sidebar is the strongest element in the product and is **not** being redesigned. What follows preserves it and repairs three defects around it.

### 7.1 Preserve exactly

- Width **232** `[R: MetricTokens.xaml:111]`, compact **56**.
- Row height 36, padding 10,8, 1 px inter-row gap.
- Brand block at the top: mark + wordmark, mark only in Compact.
- Icon 20 px + label 14, `AppInlineGapLeadingLarge` between them.
- Selection shown three ways — accent edge, surface, foreground `[R: Navigation.xaml:8-9]`.
- Rail is a bound list with stable identifiers, never hand-placed buttons `[R: Navigation.xaml:4-6]`.
- Rail surface one level below the canvas.

### 7.2 Change

| # | Change | Reason |
| --- | --- | --- |
| 1 | Selection = `AppAccentSubtle` fill + 3 px `AppAccentIndicator` pill + **SemiBold** label + filled glyph | Adds a weight cue; the pill is the only 3:1-class signal and must stay |
| 2 | **Separate focus from selection** | Fixes **D-3**. The focus ring renders only on `IsKeyboardFocusWithin` **and** `KeyboardNavigation` being in keyboard mode — a pointer-selected row must not show a ring. `[S]` This is the "purple outline" the user questioned. |
| 3 | **Show the current destination on first launch** | Fixes **D-2** |
| 4 | **Collapse labels in Compact** | Fixes **D-1**. `[I]` If the inherited-property binding is the cause, bind the trigger to the rail rather than to `Self`, or set the mode explicitly on the item container. `[U]` Confirm at implementation. |
| 5 | Brand mark 24 px, wordmark 19 SemiBold, on the **new purple mark** | `[L]` The current mark is blue and clashes with the purple system |
| 6 | Active server appears as a **compact identity card** directly below the global destinations, not as another row | Keeps global pages and server workspaces visibly distinct |
| 7 | Global **Settings** stays in the global group; server **Settings** appears only inside the server workspace group, with a group eyebrow naming the server | Prevents the two Settings from being confused |

`[D]` **A compact rail mode is worth keeping** — but only once D-1 is fixed. `[L]` Today it is actively broken and worse than no compact mode.

`[D]` Semantic navigation behaviour, `ServerOpened`, and the navigation version guards are **not** touched by any of this `[R: Navigation/NavigationService.cs]`.

---

## 8. WPF performance strategy

`[D]` Performance is a design requirement here, not a follow-up. The direction was chosen partly because it is cheap: lifting surface values and strengthening strokes costs **nothing** at render time, whereas the shadows and blur it avoids are the expensive options.

### 8.1 Rules

| Area | Rule |
| --- | --- |
| Startup | No new work before first paint. Splash hands over on the first real frame. |
| Resource dictionaries | The `App.xaml` merged list stays flat and in step with `AppTheme.ThemeDictionaries` `[R: AGENTS.md]`. Nesting theme dictionaries breaks deferred `StaticResource` resolution. |
| Brushes / geometries | Every shared brush, pen and geometry is **frozen**. A new token is frozen by construction. |
| Effects | `AppElevationLow` is **deleted**. Overlay and dialog shadows keep `RenderingBias=Performance` and apply to at most a handful of simultaneously visible elements. |
| Templates | No nested `ControlTemplate` deeper than needed; no `Effect` inside any item template. |
| Lists | `VirtualizingPanel.IsVirtualizing=True`, `VirtualizationMode=Recycling` everywhere `[R: Navigation.xaml:36-37]` — extend to server rows, tables and file lists. |
| Console | Bounded buffer, batched appends, virtualised, no animation, no per-line `Effect` `[R: AGENTS.md]`. |
| Data virtualization | Only where a collection can exceed ~1000 items — file lists, console, activity. Not for server rows. |
| Images | Icons decoded at their display size via `DecodePixelWidth`; cached per size; **never** one 1024 px source scaled at runtime — that is the current icon defect in software form. |
| Page navigation | Cache global pages; recreate server-workspace pages on server switch. Never keep a live view-model for a server that is not open. |
| Bindings | No `UpdateSourceTrigger=PropertyChanged` on high-frequency numeric sources; throttle console and progress to the frame rate, not the event rate. |
| Collections | Batch bulk changes; never `Clear()`+refill a bound collection on refresh. |
| Dispatcher | Only `DispatcherPriority.Background` or lower for non-interactive work; no `Thread.Sleep` `[R: AGENTS.md]`. |
| Async | All I/O async with cancellation; the UI thread never awaits agent I/O without a cancellation path. |
| Low-end / battery | Mica, gradients and page-transition motion all off; equivalent to Reduced Motion plus a solid backdrop. |
| High DPI | Per-size icon assets at 16/20/24/32/40/48/64/128/256; no runtime downscaling of the 256. |

### 8.2 What to measure

`[U]` No numbers are asserted here. The only measurement taken during this planning session was working set at idle with zero servers: **168 MB** app, **54 MB** agent `[L]`. Everything below must be baselined against `02ec1bf` **before** the first visual commit, and re-checked at the roadmap's gates.

- Cold startup to first paint; warm startup; time to interactive.
- Navigation response between global pages, and on server switch.
- Server-list render at 1, 10, 50, 200 servers.
- Working set at 1 server and at 50 servers.
- Console throughput — sustained lines/second before the UI thread falls behind.
- CPU at idle, window focused and unfocused (the Mica gate).
- Animation frame consistency during page transition and rail selection.

`[D]` The gate is: **no measurable regression against `02ec1bf`.** A visual change that costs startup time or idle CPU is rejected, not optimised later.

---

## 9. Accessibility

`[D]` Non-negotiable. Two existing failures are fixed by this spec (**D-5** focus ring, **D-6** danger label); nothing here may introduce a new one.

| Requirement | Rule |
| --- | --- |
| Text contrast | AA 4.5:1 body, 3:1 large. `[L]` The new ramp gives 14.09 / 8.51 / 5.83 / 4.14 on canvas. |
| Non-text contrast | 3:1 for anything conveying state or bounding a control. Focus ring `#E4DCFF` clears the accent at **3.21:1** ✔ |
| Colour alone | Never. Every status carries an icon and a text label. |
| Focus visibility | 2 px ring outside the control, never clipped by a parent, never layout-shifting |
| Focus order | Follows visual order; the page header precedes content; dialogs trap focus and restore it on close |
| Keyboard | Every action reachable; no hover-only affordance; overflow menus keyboard-openable |
| Target size | 32×32 minimum for any interactive control, at every layout mode |
| Automation names | Every icon-only control; every row exposes a composed name; the rail exposes its label even when collapsed `[R: Navigation.xaml:118]` |
| Screen-reader grouping | Cards are named regions; tables expose headers; the console is a single live region with a polite update policy |
| Status | Announced when it changes, not polled |
| Text scaling | The ramp scales with Windows scaling; no fixed-height container may clip text at 150% |
| Long names | Server names trim with ellipsis and expose the full name in a tooltip and automation name; never truncate silently |
| Errors | Associated with their control, announced, and ordered before the submit action |
| Reduced Motion | First-class; both enforcement mechanisms retained |
| High Contrast | Every new token mapped in the overlay in the same commit |

`[D]` Any change that improves appearance while reducing any row of this table is rejected. `[R: AGENTS.md]` "Polish follows reliability."
