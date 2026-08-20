# ChunkPilot visual audit v2

Status: planning document. No production XAML, C#, asset, project or test file was changed by the session that produced it. Companion documents: [`VISUAL-DIRECTION-V2.md`](VISUAL-DIRECTION-V2.md), [`VISUAL-SYSTEM-V2-SPEC.md`](VISUAL-SYSTEM-V2-SPEC.md), [`VISUAL-PAGE-SPECIFICATIONS-V2.md`](VISUAL-PAGE-SPECIFICATIONS-V2.md), [`VISUAL-BRAND-AND-ICON-V2.md`](VISUAL-BRAND-AND-ICON-V2.md), [`VISUAL-MIGRATION-ROADMAP.md`](VISUAL-MIGRATION-ROADMAP.md), [`VISUAL-ACCEPTANCE-CHECKLIST.md`](VISUAL-ACCEPTANCE-CHECKLIST.md).

Baseline inspected: branch `plan/create-server-v2`, commit `02ec1bf`, working tree clean.

## Evidence labels

| Label | Meaning |
| --- | --- |
| `[R]` | Verified repository evidence — a file and line that was read |
| `[S]` | Verified screenshot evidence — the attached runtime screenshots |
| `[L]` | Verified live-runtime evidence — the application or gallery was launched and inspected |
| `[I]` | Inference drawn from `[R]`/`[S]`/`[L]` |
| `[D]` | Design recommendation — a judgement, not a fact |
| `[U]` | Unknown, requires implementation testing to settle |

## How this audit was produced

- `[L]` The design gallery was rasterised deterministically with `ChunkPilot.exe --design-gallery --render`, which is the repository's own capture path (`DesignSystem/Gallery/DesignGalleryLauncher.cs`) `[R]`. Output: 1440 / 1100 / 840 px PNGs, inspected at 1:1 in 800 px slices.
- `[L]` The application was launched with `CHUNKPILOT_DATA_ROOT` and `CHUNKPILOT_INSTANCE_ID` pointed at a temporary scratch directory `[R: src/ChunkPilot.App/App.xaml.cs:42, src/ChunkPilot.Core/Models.cs:520-522]`, captured at 1920×1040 and 900×700, and inspected at 3–4× nearest-neighbour zoom. The instance and its agent were terminated afterwards. The user's own pre-existing session was left untouched.
- `[L]` WPF font resolution was probed directly through `System.Windows.Media.Typeface.TryGetGlyphTypeface` rather than assumed.
- `[L]` Palette contrast ratios were computed from the literal colours in `Themes/Tokens/Palette.xaml` `[R]` using the WCAG relative-luminance formula.
- `[L]` Brand asset optical occupancy was measured by alpha bounding box over `assets/brand/*.png`.

Automated tests and token files alone were deliberately not treated as sufficient.

---

## Part 1 — Executive diagnosis

The application does not feel unfinished because of one bad choice. It feels unfinished because **three independent systems are each operating at roughly a quarter of their intended strength, and they compound.**

1. **The palette is pitched too dark, and its borders are too weak to compensate.** The whole interface lives between L\* 3 and L\* 14.5, roughly seven points darker than comparable modern dark applications, and the strokes that actually delineate cards are about two-thirds of the strength they need. The individual surface *steps* are fine — this is a range-and-edge problem, not a step problem.
2. **The weight axis of the type system does not exist.** Windows ships no Medium (500) face for any Segoe UI family, so `AppFontWeightMedium` silently resolves to SemiBold. The intended three-weight ramp is a two-weight ramp, and a disproportionate share of the interface is rendering at SemiBold.
3. **Pages have no vertical composition.** Content stretches horizontally to fill the window but nothing owns the vertical remainder, so every page terminates in a large dead region and empty states float in it.

Everything the user described — bland, generic, unfinished, administrative, too much unused space, components too small, empty states as tiny islands — is downstream of those three. None of them is a taste disagreement. All three are measurable.

---

## Part 2 — Root causes

### RC-1 — The palette's working range is too dark and too narrow, and structural borders are too weak

**A correction to the obvious analysis.** WCAG contrast ratios are the wrong instrument for dark-on-dark surface steps: the `+0.05` term in the formula dominates at low luminance, so every dark pair reports a ratio near 1.0 regardless of how visible it actually is. Measured that way, ChunkPilot's surface steps look catastrophic (1.04–1.24:1) — but so do the reference products'. The correct instrument is perceptual lightness, `L*`.

`[L]` Measured in `L*` from `Themes/Tokens/Palette.xaml` `[R]`, with public reference values for comparison:

| Step | ChunkPilot ΔL\* | Reference |
| --- | --- | --- |
| Canvas → raised card | **4.7** | Discord secondary→primary 2.8; GitHub canvas→subtle 4.6 |
| Raised → hover | **5.0** | Discord primary→raised 3.2 |
| Hover → pressed | **3.4** | — |

`[I]` **The surface fill steps are not the problem.** At ΔL\* 4.7 they are larger than Discord's and equal to GitHub Dark's. The earlier, intuitive reading — "the steps are too small" — is wrong and would send an implementer in the wrong direction.

Two things *are* wrong.

**(a) The entire interface lives in the bottom eighth of the lightness scale.**

| Product | Working range (sunken → raised) |
| --- | --- |
| **ChunkPilot** | **L\* 3.1 → 14.5** |
| Discord | L\* 11.8 → 24.4 |
| macOS dark | L\* 11.3 → 18.1 |
| VS Code dark | L\* 8.2 → 11.8 |
| GitHub dark | L\* 5.0 → 9.6 |

`[I]` Three consequences follow:

- The application reads as **black**, not dark. `[S]` This matches the attached desktop screenshot, where ChunkPilot is conspicuously darker than every neighbouring window.
- Equal ΔL\* steps are less visible at the bottom of the scale than in the middle, so the same 4.7-step buys less separation here than it does for Discord.
- **There is no headroom below the canvas.** Sunken is already L\* 3.1. The system cannot add a deeper level, which is why console wells, table insets and the rail all crowd into the same near-black.

**(b) Borders are carrying the structure, and they are too weak to do it.**

`[I]` GitHub Dark demonstrates that a very dark working range *can* work — it sits at L\* 5.0–9.6, darker than ChunkPilot. It succeeds because its borders are strong:

| Edge | ΔL\* from the surface it sits on |
| --- | --- |
| GitHub subtle surface → border `#30363D` | **12.7** |
| **ChunkPilot raised card → `AppStroke` `#2B2B34`** | **8.4** |
| **ChunkPilot canvas → `AppStrokeSubtle` `#1F1F26`** | **7.2** |

`[L][S]` A ChunkPilot card is therefore a barely-lighter fill inside a barely-lighter outline. Neither cue is wrong; both are about two-thirds as strong as they need to be, and they fail together.

**(c) Related, and genuinely weak by any measure:**

- **Selection.** `AppSurfaceSelected` `#272040` is only ΔL\* 5.1 above a raised card, and it is a hue shift as much as a lightness shift. `[R: Themes/Controls/Navigation.xaml:78-82]` Selection survives in the rail because the rail *also* draws a 3 px accent pill — which is exactly why the sidebar is the one component whose selection reads clearly, and `[I]` plausibly why the user identifies it as the strongest element.
- **Elevation is decorative.** `[R: Themes/Tokens/ElevationTokens.xaml:20-23]` `AppElevationLow` is an 8 px blur at 0.35 opacity of `#66000000` — a near-black shadow cast onto near-black. `[L]` In the rendered gallery it is indistinguishable from `Flat`.

`[D]` The fix is therefore **not** "increase every step". It is: lift and widen the working range, and strengthen the structural strokes. That is a change to `Palette.xaml` and a handful of `ColorTokens.xaml` mappings — see [`VISUAL-SYSTEM-V2-SPEC.md`](VISUAL-SYSTEM-V2-SPEC.md).

### RC-2 — The type system has two weights, not three

`[L]` Probed directly. Every Segoe UI family on this machine exposes exactly these weights:

`Light 300 · Semilight 350 · Regular 400 · SemiBold 600 · Bold 700 · Black 900`

There is **no Medium (500)**. WPF rounds a requested Medium up to SemiBold:

```
Segoe UI Variable Text, Segoe UI   Medium    -> SEGUIVAR.TTF  face='Text Semibold'  resolvedWeight=SemiBold
Segoe UI Variable Text, Segoe UI   SemiBold  -> SEGUIVAR.TTF  face='Text Semibold'  resolvedWeight=SemiBold
Segoe UI                           Medium    -> SEGUISB.TTF   face='Semibold'       resolvedWeight=SemiBold
```

`[R]` `AppFontWeightMedium` is declared in `Themes/Tokens/TypographyTokens.xaml:37` and consumed in exactly three places, all of them high-frequency chrome:

| Consumer | File | Renders as |
| --- | --- | --- |
| Field label (`AppLabelText`) | `Themes/Controls/Text.xaml:102` | SemiBold |
| Segmented control text | `Themes/Controls/Selection.xaml:357` | SemiBold |
| Button text | `Themes/Controls/Buttons.xaml:75` | SemiBold |

`[I]` So **every button label, every field label and every segmented-control label renders at the same weight as a section title.** Add the 11 explicit SemiBold usages `[L: counted across the theme dictionaries]` and the result is an interface where nearly all non-body text is SemiBold. `[S]` This is visible in the attached Servers screenshot: "Create server", "Add existing", "Name A–Z", "All" and "No servers yet" are all the same heavy weight. Uniform heaviness reads as *blocky and administrative*, which is exactly the impression the user reported.

**The important correction to the obvious hypothesis:** Segoe UI Variable resolves correctly. All three optical faces — Display, Text, Small — resolve to `SEGUIVAR.TTF` with the correct named instance. `[L]` The font is not the problem. **How the weight axis is being used is the problem.** Replacing the typeface would not fix this and would add licensing and distribution cost for nothing.

### RC-3 — ClearType subpixel rendering on a dark theme

`[R: Themes/Controls/Text.xaml:23-24]`

```xml
<Setter Property="TextOptions.TextFormattingMode" Value="Ideal" />
<Setter Property="TextOptions.TextRenderingMode" Value="ClearType" />
```

`[L]` At 3× zoom on the live capture, every glyph carries pronounced orange/blue subpixel fringing. Light-on-dark is the case where RGB subpixel antialiasing is at its worst: the fringes are proportionally brighter against the dark field, and the type reads slightly soft and colour-contaminated rather than crisp.

`[I]` This is a genuine, independent contributor to "the typography does not feel modern" — separate from weight. `Grayscale` rendering is what most modern dark applications effectively use, and it is a one-token change. `[U]` Needs A/B verification on the user's actual display; some users prefer ClearType even on dark. This should be measured before being made unconditional.

### RC-4 — Pages have horizontal layout but no vertical composition

`[S][L]` The pattern is consistent across every global page:

- `[S]` Servers at ~1433 px: the toolbar stretches edge to edge, the (empty) result region is a 12 px sliver, and roughly 660 px of vertical space below it is empty canvas. The empty state is centred inside that void.
- `[S]` Automation at ~1428 px: the table header stretches edge to edge, the table body is empty, and roughly 500 px below is empty canvas.
- `[L]` Dashboard at 1920×1040: the empty state occupies about 330×110 px. The remaining content area — on the order of 1.6 million pixels — is empty.

`[I]` The failure is **not** that content is too narrow. Content is stretching correctly. The failure is that:

1. Nothing owns the vertical axis. There is no concept of "what fills the rest of the page".
2. Empty states are sized to their text and then centred in whatever is left, which converts a large window into a large void with a small label in it.
3. `[R: Themes/Tokens/MetricTokens.xaml:113]` `AppContentMaxWidth` is 1120, and `[L]` in the gallery the capped content is **left-aligned**, leaving 296 px of ragged dead space at 1440. Capping without centring produces asymmetric emptiness, which reads worse than either full-bleed or centred.

This is why the interface can be simultaneously "too empty" and "components too small": components are correctly sized for their content, the *page* has no opinion about the space around them.

### RC-5 — The brand mark is optically undersized and off-palette

`[L]` Measured alpha bounding box over `assets/brand/*.png`:

| Asset | Bounding box | % of frame | Ink coverage |
| --- | --- | --- | --- |
| `ChunkPilot-16.png` | 10×7 | 62.5% | 21.5% |
| `ChunkPilot-32.png` | 20×15 | 62.5% | 19.1% |
| `ChunkPilot-64.png` | 40×29 | 62.5% | 17.5% |
| `ChunkPilot-256.png` | 157×116 | 61.3% | 16.3% |
| `ChunkPilot-source-1024.png` | 626×460 | 61.1% | 15.9% |

`[I]` Three findings follow directly:

1. **Every size is the same artwork scaled.** The bounding box is 61–65% of the frame at every single size. There is no per-size optical redraw.
2. **The mark is wider than tall (1.35:1), so it fills only ~45% of the frame vertically.** Well-formed Windows app icons fill roughly 85–95% of the frame in their dominant dimension. `[S]` This is exactly why the taskbar icon looks about half the size of its neighbours in the attached desktop screenshot — because it is.
3. **Actual ink is 16–21% of the frame.** Most of the icon is transparent.

`[L]` Visual inspection of the 256 px asset: a gradient-shaded 3D cube with a thin orbital swoosh and a small arrowhead, plus specular highlights. At 32 px it is an indistinct blue blob; at 16 px it is unreadable. `[I]` The swoosh, the arrowhead and the facet shading are all sub-pixel below ~48 px.

`[L]` **The mark is blue; the interface accent is purple** (`#7B5CE0` `[R: Palette.xaml:33]`). Brand and product do not currently share a colour. `[S]` Confirmed in the sidebar and splash screenshots.

---

## Part 3 — Secondary symptoms

These are real, but they are consequences or smaller-order issues. Fixing them without fixing RC-1 to RC-4 would not change the overall impression.

| # | Symptom | Evidence |
| --- | --- | --- |
| S-1 | Icons are thin single-weight outlines at 20 px in a 232 px rail, reading as spindly and low-presence. | `[L]` 3× zoom of the live sidebar |
| S-2 | Section eyebrows (`FOUNDATIONS`, `ACTIONS`) are 12 px muted uppercase above very large cards — the label is far weaker than the thing it labels. | `[L]` gallery slice 0 |
| S-3 | Primary text at `#F2F2F5` is **16.99:1** on canvas — a direct consequence of RC-1(a). Near-white on near-black is harsher than necessary and contributes to a stark, unrefined feel. Modern dark UIs sit nearer 13–15:1. Lifting the canvas fixes this without touching the text token. | `[L]` computed |
| S-4 | The icon scale demo (`Small 16, Medium 20, Large 24, Hero 32`) renders four glyphs that are barely differentiated, because the glyph does not fill its nominal box. | `[L]` gallery slice 1; `[S]` matches the attached buttons screenshot |
| S-5 | Duplicate primary actions: Servers shows **Create server / Add existing** in the page header *and* again in the empty state. | `[S]` Servers screenshot; `[R: MainWindow.xaml:330-347, 422-446]` |
| S-6 | `AppAccentMuted` `#4A3D7A` is 1.85:1 on a raised surface — too weak to indicate anything it is used for. | `[L]` computed |
| S-7 | Radii are internally inconsistent in effect: 4/6/8/12 across control→card→surface is a sensible ramp, but with 1.1:1 surfaces the radius is often the *only* cue that a card exists. | `[R: MetricTokens.xaml:67-72]`, `[L]` |

---

## Part 4 — Defects found (not to be fixed in this planning session)

These are production defects discovered during inspection. They are carried into [`VISUAL-MIGRATION-ROADMAP.md`](VISUAL-MIGRATION-ROADMAP.md) rather than repaired here.

### D-1 — Compact navigation labels do not collapse `[L]` **High**

At 900×700 the rail correctly narrows to 56 px `[R: Navigation.xaml:24-27]`, but the row labels remain visible and are clipped to a single character ("D", "S", "A", "A", "S"), spilling to the rail edge. The `Compact` `DataTrigger` on `AppNavigationRowLabel` `[R: Navigation.xaml:110-114]` is not firing.

`[I]` Likely cause: `ds:AppLayout.Mode` is an inherited attached property, and the `RelativeSource=Self` binding on a `TextBlock` generated inside a virtualised `ListBox` item container is evaluated before the inheritance context is established. The identical trigger on the rail `Border` `[R: Navigation.xaml:25]` works because that element is in the static tree. `[U]` Must be confirmed at implementation time.

### D-2 — No navigation item shows a selected state on first launch `[L]` **High**

At 1920×1040 immediately after launch, with the Dashboard displayed, **no rail row shows an accent pill, a selected fill or a brightened foreground.** The current destination is not indicated at all. `[S]` After the user navigates, selection does appear.

### D-3 — Selection and keyboard focus are conflated in the rail `[S]` **Medium**

In the attached Servers and Automation screenshots the selected row carries a full purple rounded outline in addition to the fill and pill. That outline is `InternalFocusRing` driven by `IsKeyboardFocused` `[R: Navigation.xaml:83-85]`. Because selecting a destination also focuses its container, the selected row persistently renders as focused. `[I]` The result reads as a stray outline rather than an intentional state, and it is the "purple outline" the user questioned.

### D-4 — Type ramp labels and comments are stale `[R]` **Low**

`Themes/Controls/Text.xaml:28-33` documents the ramp as `Display 28 / TitleLarge 22 / Title 18 / Subtitle 15 / Body 13`. `Themes/Tokens/TypographyTokens.xaml:18-24` actually declares `30 / 24 / 19 / 16 / 14`. The Design Gallery repeats the wrong numbers as literal strings `[R: DesignGalleryContent.xaml:167-175]`, so the gallery actively misinforms anyone using it as a reference.

### D-5 — Focus ring fails contrast on a primary button `[L]` **Medium, accessibility**

`AppFocusRing` `#B9A6FF` against `AppAccent` `#7B5CE0` measures **2.24:1**, below the 3:1 non-text minimum. A focused primary button's ring is not reliably perceivable.

### D-6 — Danger button label fails WCAG AA `[L]` **Medium, accessibility**

White on `AppDanger` `#D9525F` measures **3.95:1**, below the 4.5:1 AA minimum for body-size text. `[S]` Visible on the "Delete backup" button in the attached buttons screenshot.

### D-7 — The no-server Dashboard has no page header `[L]` **Medium**

`[R: MainWindow.xaml:222-250]` The `AppEmptyState` replaces the entire page including its `AppPageHeader`. `[L]` Confirmed at 1920×1040: the first screen a new user sees has no title, no subtitle and no page identity — only a small centred island.

### D-8 — A `ChunkPilot` process was observed with no main window `[L]` **Unverified, informational**

During inspection, one of the user's pre-existing processes (PID 25676) had an empty `MainWindowTitle`. `[U]` This may be a legitimate minimised-to-tray state or the splash owner, or it may be the "invisible `ChunkPilot.App` process" that `AGENTS.md` forbids. Not investigated — it belonged to the user's session and was deliberately left alone. Flagged only so it is not lost.

---

## Part 5 — Classification

### Objective usability issues

- RC-1 (working range too dark, structural strokes too weak), RC-4 (no vertical composition), D-1, D-2, D-3, D-7, S-5.

### Objective accessibility issues

- D-5 — focus ring 2.24:1 against a primary button fill (below the 3:1 non-text minimum). A real, unambiguous failure.
- D-6 — danger button label 3.95:1 (below the 4.5:1 AA minimum for body-size text). A real, unambiguous failure.
- `[L]` Text colour is **not** an accessibility problem here. Muted `#8A8A96` is 5.08:1 on a raised surface, secondary `#AEAEB8` is 7.87:1, primary is 15.5:1 — all comfortably AA. The accessibility defects are confined to the two component-level contrast failures above.
- `[I]` Note that hover and pressed states, at ΔL\* 5.0 and 3.4, are visible enough not to be an accessibility failure — but selection, which conveys information, leans on the accent pill rather than the fill to meet 1.4.11. Removing the pill in any future redesign would create a failure.

### Personal-preference issues

- The specific purple hue. `[L]` `#7B5CE0` is a reasonable, accessible accent; the complaint is that it is under-used and under-differentiated, not that it is wrong.
- Corner radius values.
- ClearType vs grayscale (RC-3) — measurable in effect, but preference-sensitive in outcome. `[U]`

### Performance considerations

- `[L]` Working set with zero servers measured 168 MB (private 99 MB) for the WPF app and 54 MB (private 18 MB) for the agent. `[I]` This is unremarkable for .NET WPF and includes shared runtime pages, but it is the number to hold the line on. Nothing in this redesign may raise it materially.
- `[R: ElevationTokens.xaml]` Shadows are already shared frozen instances with `RenderingBias=Performance`, and cards deliberately avoid them. That decision is correct and must survive the redesign — raising surface contrast is *cheaper* than adding shadows, which is a fortunate alignment of quality and performance.
- Any move to a Windows backdrop (Mica) must not sit behind console or table surfaces.

---

## Part 6 — What is already strong and must be preserved

This audit is not a case for starting over. A large amount of the foundation is correct and unusually disciplined.

| Strength | Evidence | Why it matters |
| --- | --- | --- |
| **Semantic token architecture** | `[R]` Palette → ColorTokens → control styles, with `Palette.xaml` the only file allowed literal colours | The entire surface and colour fix in this plan is a change to *two* files. That is only possible because the indirection was built correctly. |
| **The sidebar** | `[S][L]` | Width, rhythm, icon/label pairing and the accent-pill selection are right. The pill is the only 3:1-class state signal in the product. Preserve the structure; fix D-1/D-2/D-3 around it. |
| **Selection is never colour alone** | `[R: Navigation.xaml:8-9]` accent edge + surface + foreground | Correct accessibility posture, already designed in. |
| **No implicit `TextBlock` style** | `[R: Text.xaml:6-11]` with the reasoning recorded | A subtle, correct decision that prevents a whole class of regressions. |
| **Reduced Motion is a real, enforced state** | `[R: MotionTokens.xaml:4-9]`, two independent mechanisms | Better than most commercial products. |
| **High-contrast overlay exists as a first-class dictionary** | `[R: Themes/Overlays/HighContrast.xaml]`; `[S]` the attached high-contrast gallery renders coherently | Do not regress this. |
| **Deterministic gallery rasterisation** | `[R: DesignGalleryLauncher.cs:93-131]` | The redesign has a built-in objective review harness. Every roadmap task below uses it. |
| **Scrollbars** | `[S]` user confirms acceptable; `[R: Themes/Controls/ScrollBars.xaml]` | Leave alone. |
| **Motion values** | `[R]` 90/150/220 ms, ease-out, no overshoot | Already correct. The product does not feel slow because of durations. |

The honest summary is that ChunkPilot has a well-engineered design system whose values are set too timidly to be seen. That is a much better position than a badly architected one, and it is why the migration in this plan is measured in token changes and bounded page passes rather than a rewrite.

---

## Part 7 — Screenshot evidence index

Claims marked `[S]` in this document set rely on the attached runtime screenshots as follows.

| Screenshot | Used for |
| --- | --- |
| Servers empty state (~1433 px) | S-5 duplicate actions; RC-4 vertical void; RC-2 uniform SemiBold in toolbar and buttons; D-3 purple outline on selected "Servers" row |
| Desktop with taskbar | RC-5 taskbar icon visibly smaller than neighbouring applications |
| Automation page (~1428 px) | RC-4 empty table over a large void; page composition |
| Dark design gallery (foundations/type/buttons) | RC-2 uniform weight; S-2 weak eyebrows; S-4 icon scale differentiation; D-6 danger button |
| Inputs and selectors gallery | Control density and field-label weight |
| Navigation / status / server rows gallery | Selection treatment; badge legibility; row density |
| Tables / console / progress gallery | Data-surface density; progress and operation panels |
| High Contrast gallery | Confirms the high-contrast overlay resolves coherently and must be preserved |
| Splash | RC-5 brand mark is blue while the interface accent is purple |
