# ChunkPilot UI design system

The canonical description of how ChunkPilot looks and behaves. Anything not described here is not
part of the design system, and anything that contradicts this document is a defect.

Companion documents: [`UI-COMPONENT-CATALOG.md`](UI-COMPONENT-CATALOG.md) (what exists) and
[`UI-RESPONSIVE-RULES.md`](UI-RESPONSIVE-RULES.md) (how it behaves at different widths).

## Visual identity

ChunkPilot is a premium native Windows tool for running Minecraft servers on your own machine. It
should feel like a well-made desktop application, not a hosting control panel that happens to run
locally.

- **Calm neutral surfaces.** The interface is built from dark, cool-tinted greys. Depth comes from
  the value of a surface and a hairline stroke, not from shadows, gradients or borders everywhere.
- **Controlled purple.** Purple is the brand, and it is spent only on identity, the current
  selection, focus, and the single primary action on a surface. A purple-tinted grey is not a
  neutral grey; the previous interface tinted every surface and the accent stopped meaning anything.
- **Content over chrome.** One meaningful surface per idea. No decorative panels, textures, grids,
  glow, noise or nested boxes.
- **Native typography.** Segoe UI Variable with Segoe UI fallback. No bundled fonts.
- **Truthful state.** Every state a surface can be in is designed: loading, empty, unavailable,
  warning, failed, busy, disabled, selected. Nothing is left to render as a blank rectangle.

Never reproduce another application's appearance, assets, wording or layout.

## Colour

Three layers, and only the first declares a literal colour.

| Layer | File | Rule |
|---|---|---|
| Palette | `Themes/Tokens/Palette.xaml` | The only file in the application allowed to contain a hex colour. |
| Semantic tokens | `Themes/Tokens/ColorTokens.xaml` | `App<Role><Variant>` brushes. The only colours components may name. |
| High-contrast overlay | `Themes/Overlays/HighContrast.xaml` | Replaces every semantic token with a Windows system colour. |

Roles:

- **Surfaces** — `AppSurfaceCanvas` (window backdrop), `AppSurfaceSunken` (rails, console, input
  wells), `AppSurface` (working surface), `AppSurfaceRaised` (cards), `AppSurfaceOverlay` (popups,
  dialogs, toasts), plus `AppSurfaceHover`, `AppSurfacePressed`, `AppSurfaceSelected`,
  `AppSurfaceDisabled`, `AppSurfaceTransparent`, `AppSurfaceScrim`.
- **Strokes** — `AppStrokeSubtle` (separates), `AppStroke` (outlines), `AppStrokeStrong`
  (interactive emphasis), `AppStrokeDisabled`. `AppBorder` and `AppBorderStrong` are retained
  aliases for the same vocabulary.
- **Text** — `AppTextPrimary`, `AppTextSecondary`, `AppTextMuted`, `AppTextDisabled`,
  `AppTextOnAccent`, `AppTextAccent`.
- **Accent** — `AppAccent`, `AppAccentHover`, `AppAccentPressed`, `AppAccentMuted`,
  `AppAccentSubtle`, `AppAccentDisabled`, and `AppFocusRing`.
- **Status** — `AppSuccess`, `AppWarning`, `AppDanger`, `AppInfo`, `AppNeutral`, each with a
  `…Subtle` tinted surface for banners and a `…Text` tone that is readable on dark surfaces.

`AppNeutral` is the default status tone, because an unset state must read as *unknown*, never as
healthy. Status colour is always accompanied by text, and usually by a glyph.

Contrast: primary, secondary and muted text all clear 4.5:1 on every surface token they are used on.
`AppTextDisabled` is deliberately below that threshold — disabled text is exempt, and a disabled
control must never be the only place a fact appears.

Component styles reference brushes with `DynamicResource`, which is what allows the high-contrast
overlay to re-style live windows. Metrics and typography use `StaticResource`; they do not change at
runtime.

## Typography

`Themes/Tokens/TypographyTokens.xaml` defines the values, `Themes/Controls/Text.xaml` the styles.
Pages pick a style; they never set `FontFamily`, `FontSize`, `FontWeight` or `Foreground` on a
`TextBlock`.

| Style | Size | Use |
|---|---|---|
| `AppDisplayText` | 28 | One page-defining value |
| `AppTitleLargeText` | 22 | Page title |
| `AppTitleText` | 18 | Major section |
| `AppSubtitleText` | 15 | Group heading |
| `AppBodyText` | 13 | Default |
| `AppBodyStrongText` | 13 | A row's primary value |
| `AppSecondaryText` | 13 | Supporting explanation |
| `AppMutedText` | 12 | Metadata and detail |
| `AppLabelText` | 12 | Field label |
| `AppCaptionText` | 11 | Badges and dense labels |
| `AppEyebrowText` | 11 | Label above a group of sections |
| `AppMonoText` | 12 | Paths, addresses, versions, hashes, console output |

### Weights, and why there is no Medium

Windows ships **no Medium (500) face for any Segoe UI family**. Probing `Typeface.TryGetGlyphTypeface`
returns Light 300, Semilight 350, Regular 400, SemiBold 600, Bold 700 and Black 900 — nothing between
Regular and SemiBold. A request for Medium resolves *up* to SemiBold silently, which is how an earlier
ramp ended up rendering button text, field labels and section titles at the same weight.

Only the weights that exist are declared: `AppFontWeightRegular`, `AppFontWeightSemilight` (large
display type only) and `AppFontWeightStrong` (SemiBold, the single emphasis weight). Bold is not used,
and Medium must not be reintroduced.

Two levers give small text more presence without inventing a weight:

- **Optical size.** `AppMutedText` and `AppLabelText` use `AppFontFamilySmall` (Segoe UI Variable
  Small), the face drawn for 12–13px: heavier stems and slightly wider spacing at that size. Windows 10
  resolves the same Segoe UI fallback it always did.
- **Contrast.** `AppTextSecondary` and `AppTextMuted` each sit one step higher on the neutral ramp than
  they used to. The five text steps stay distinct: Primary 92, Secondary 84, Muted 74, Tertiary 62,
  Disabled 52. Tertiary is deliberately the dimmest label tone and is not lifted, because a recessive
  column heading is what creates the hierarchy it sits in.

There is deliberately **no implicit `TextBlock` style**. An implicit style outranks property
inheritance, so a global one would repaint and resize the label inside every button, badge and
header slot that legitimately sets text properties on its parent. Unstyled text inherits from its
container; every deviation is a named style.

## Spacing, sizing and radii

`Themes/Tokens/MetricTokens.xaml`. A 4px rhythm. Every margin, padding, gap, control height and
corner radius comes from a token; a literal value for any of those in a page is a defect. Explicit
`Width`/`Height` on a container is allowed only where the layout genuinely demands a fixed frame —
the Design Gallery sizes its own demonstration frames this way.

- **Scalars** `AppSpace2 … AppSpace40` for panel spacing and explicit dimensions.
- **Insets** `AppInset4 … AppInset24` uniform, plus purposeful ones: `AppPagePadding`,
  `AppPagePaddingCompact`, `AppCardPadding`, `AppSurfacePadding`, `AppControlPadding`,
  `AppControlPaddingCompact`, `AppControlPaddingLarge`, `AppRowPadding`, `AppNavRowPadding`,
  `AppBadgePadding`, `AppMenuPadding`, `AppTooltipPadding`.
- **Data display** `AppPerformanceChartHeight` gives lightweight process-metric charts one shared
  plot height; their current, average, peak and sample-window text remains outside the plot.
- **Server icon workflow** `AppServerIconCropDialogWidth`, `AppServerIconCropDialogHeight`,
  `AppServerIconLibraryDialogWidth`, `AppServerIconLibraryDialogHeight`,
  `AppServerIconCropViewport` and `AppServerIconPreviewSize` keep the crop and saved-library
  surfaces usable at the 800 x 600 review floor while the preview remains the exact Minecraft
  output size.
- **Stacking gaps** `AppStackGapTiny`, `AppStackGapSmall`, `AppStackGap`, `AppStackGapLarge`,
  `AppStackGapTop`.
- **Inline gaps** have a direction, and using the wrong one produces a label with no gap at all.
  `AppInlineGap` and `AppInlineGapLarge` are *trailing* gaps: put them on the leading element of a
  horizontal pair, such as an icon or a status dot before its text. `AppInlineGapLeading` and
  `AppInlineGapLeadingLarge` are the mirror image, for the element that follows when the leading one
  cannot carry the gap — the content presenter of a check box, radio button or switch, and the
  navigation row label.
- **Radii** `AppCornerSmall` 4, `AppCornerControl` 6, `AppCornerCard` 8, `AppCornerSurface` 12,
  `AppCornerPill`, `AppCornerNone`. Radii are typed `CornerRadius`, not doubles.
- **Sizing** `AppControlHeightCompact` 28, `AppControlHeight` 32, `AppControlHeightLarge` 36,
  `AppRowHeight` 40, `AppRowHeightLarge` 52, `AppNavRowHeight` 36, `AppIconButtonSize` 32, plus icon
  sizes, widths for search, toast, dialog and the navigation rail, and `AppProseMaxWidth` for
  centred explanatory text.

## Elevation

`Themes/Tokens/ElevationTokens.xaml`, three levels, all shared frozen effects.

| Token | Used for |
|---|---|
| `AppElevationLow` | A surface that needs separating from a busy background |
| `AppElevationOverlay` | Popups, menus, combo drop-downs, tooltips, toasts |
| `AppElevationDialog` | Modal dialog surfaces only |

Cards get no shadow. They are distinguished by surface value plus a hairline stroke, which keeps long
scrolling lists cheap to render. Never declare a `DropShadowEffect` in a page. Under high contrast,
shadows are removed entirely — they carry no information.

## Motion

`Themes/Tokens/MotionTokens.xaml`. Durations: `AppDurationInstant`, `AppDurationFast` 90ms,
`AppDurationStandard` 150ms, `AppDurationSlow` 220ms, `AppDurationBusyCycle`. Easing decelerates on
enter; there is no bounce or overshoot anywhere.

Motion is brief, centralised, and never required to understand a state change. Selection, toggles and
disclosure change instantly. The only continuous animation in the system is the busy indicator and
the indeterminate progress pulse.

Reduced Motion is enforced twice, on purpose:

1. `Themes/Overlays/ReducedMotion.xaml` collapses every duration and travel distance to zero.
2. `AppMotion.IsEnabled`, an inherited attached property published by `AppTheme`, is tested by every
   storyboard trigger so the animation never starts.

The second mechanism is the load-bearing one: a `Storyboard.Duration` inside a `ControlTemplate` is
resolved at parse time, so WPF cannot re-read it from a swapped dictionary. The overlay covers every
other consumer of the tokens. A component that cannot express its state with animation switched off
is not acceptable.

## Accessibility

- **Focus is always visible.** The Windows dotted focus rectangle is invisible on these surfaces, so
  every interactive template draws its own ring from `AppFocusRing` using the shared
  `InternalFocusRing` style. The ring is a permanently present transparent border, so gaining focus
  never changes layout.
- **Colour is never the only signal.** Every tone-bearing component requires text; selection is shown
  by an accent edge and a surface change as well as colour; a chosen combo-box item gets a tick.
- **High contrast is a first-class state**, handled by the overlay plus the inherited
  `AppAccessibility.IsHighContrast` flag for the cases a brush swap cannot cover.
- **Icons are decorative.** `AppIcon` removes itself from the automation tree; the containing control
  owns the accessible name. An icon-only control must set both `ToolTip` and
  `AutomationProperties.Name`.
- **Keyboard.** Navigation is a bound list, so arrow keys work. Dialog footers put cancel before the
  committing action. Focus returns to the control that opened a flyout or palette.
- **Text scaling.** Controls use minimum heights rather than fixed heights, and text wraps rather
  than truncating wherever the meaning depends on the full string.

## Icons

`FluentIcons.Wpf` is pinned centrally and referenced in exactly one place.

- Views declare intent: `<ds:AppIcon Kind="Play" Scale="Medium" />`, or
  `ds:AppButton.Icon="Play"` on a button.
- `AppIconKind` is the whole vocabulary. `AppIconMap` maps it to a glyph and **throws** on an
  unmapped member; a silent fallback glyph is how icon drift starts.
- `Themes/Controls/Icons.xaml` is the only XAML file allowed to name the `FluentIcons` namespace.
- Scales are `Small` 16, `Medium` 20, `Large` 24, `Hero` 32 (empty states only). Never set `Width`
  or `Height` on an icon. The step drives two things inside the template: which purpose-drawn glyph
  the package selects, and the rendered size. Setting only the first leaves every icon at the
  package default of 20 dip, so `DesignSystemContractTests` measures all four steps.
- `Variant="Filled"` is reserved for the selected state of a navigation destination.
- Prohibited: emoji, raw glyph strings, private-use characters, `Segoe Fluent Icons` or `Segoe MDL2`
  font references, and page-local icon mappings.

## States

Every component documents the states it supports, and every state is designed rather than implied.

| State | Requirement |
|---|---|
| Normal | Resting tokens; no borders that carry no meaning |
| Hover | Surface change only. Never a size or position change |
| Pressed | A further surface change. No scale or bounce |
| Focused | Visible ring from `AppFocusRing`, independent of hover |
| Disabled | Muted surface, stroke and text. Never the only place a fact appears; prefer omitting an action over disabling it |
| Selected | Accent edge plus surface change plus, where applicable, a glyph |
| Busy | `AppBusyIndicator` or an indeterminate track, always beside text saying what is happening |
| Loading | `AppLoadingState`. Distinct from empty and from unavailable |
| Empty | `AppEmptyState` explaining why, offering at most one real action |
| Warning | `Warning` tone with the risk stated and the safe next step offered |
| Error | `Danger` tone stating what happened, what is still true, and what to do next |
| Progress | Determinate only when the total is known; otherwise indeterminate. Never a fake percentage |

Copy rules live in [`UI-COPY-AND-STATE-GUIDE.md`](UI-COPY-AND-STATE-GUIDE.md). The short version: say
what is confirmed, say what is unknown, and offer only actions that exist.

## Structure

```
src/ChunkPilot.App/
  Themes/
    Tokens/          Palette, ColorTokens, TypographyTokens, MetricTokens, ElevationTokens, MotionTokens
    Controls/        Internal, Text, Icons, Buttons, Inputs, Selection, Navigation, Surfaces,
                     DataDisplay, Feedback, Overlays, ScrollBars
    Overlays/        HighContrast, ReducedMotion   (merged at runtime by AppTheme)
    Compatibility/   LegacyAliases                 (temporary; only ever shrinks)
  DesignSystem/
    AppIconKind, AppIconMap, AppIcon, AppTone, AppButton, AppInput,
    AppLayout, AppMotion, AppAccessibility, AppTheme
    Components/      the lookless composite controls
    Gallery/         the development-only Design Gallery
```

`App.xaml` merges the dictionaries as a **flat** list, mirrored by `AppTheme.ThemeDictionaries`.
This is not a style preference: `StaticResource` and `Style.BasedOn` only reliably resolve against
dictionaries merged earlier into the same collection. Wrapping these files behind a parent
dictionary breaks those lookups as soon as WPF defers a value — an `Effect` loses its shadow colour,
a style loses its `BasedOn`. Do not group them behind an aggregate dictionary.

`AppTheme` owns theme loading, the accessibility overlays, and the per-window accessibility flags.
`AppTheme.ApplyPreview` overrides them for a single window, which is how the Design Gallery previews
high contrast and Reduced Motion without touching the user's Windows settings.

## Design Gallery

`ChunkPilot.exe --design-gallery` opens the gallery. `--render <directory>` rasterises it at the
Wide, Standard and Compact widths and exits.

The gallery is development-only: no product control opens it, it takes no single-instance lock,
shows no tray icon, and never contacts the agent. All of its data is invented and hard-coded in
`GalleryPreviewData`. Reviewing the design system must not be able to disturb a running ChunkPilot
session or a managed server.

## Adding to the design system

Reuse first. Before creating anything:

1. Search [`UI-COMPONENT-CATALOG.md`](UI-COMPONENT-CATALOG.md) for a component that already does the
   job, or one variant away from doing it.
2. If a component is close, extend it — add a variant style or a property, and add the new state to
   the gallery.
3. Only if nothing fits, add a component. In this order:
   - decide which existing tokens it uses; add a token only if no existing role fits, and update
     `ColorTokens.xaml`, `HighContrast.xaml` and this document together;
   - implement it under `Themes/Controls/`, with a lookless control under
     `DesignSystem/Components/` if it needs structure;
   - name the public resource key `App…`, and prefix internal building blocks `Internal…`;
   - reference brushes with `DynamicResource` and metrics with `StaticResource`;
   - document it in the catalog with its anatomy and required states;
   - show it in the Design Gallery in every state it supports;
   - only then use it in a page.

`DesignSystemContractTests` enforces the mechanical half of this: keys resolve, the catalog and the
theme agree in both directions, the gallery covers the composite components, the overlays cover every
token, and no governed file smuggles in a colour, a font, a glyph, a `TabControl`, a nested scroll
region or a `MessageBox`.

## Prohibited

- Colours, font families, font sizes, radii, shadows or motion values declared in a page.
- A second button, input or card template. If a variant is needed, add it to the shared style set.
- Emoji, raw glyph strings, private-use characters, or icon-font references.
- `TabControl` for navigation. Use the shell's destinations, or `AppSegmentedControl` within a page.
- A scroll region nested inside the primary page scroller, or horizontal scrolling of primary content.
- A default `MessageBox` on a product path. Use `AppAlert`, `AppToast` or a dialog surface.
- Invented metrics, fake charts, placeholder rows, claimed public reachability, claimed compatibility,
  or work reported as complete before it is.
- A permanent developer status bar, or any developer surface reachable from the product UI.
- Moving provider, persistence, process, filesystem or lifecycle ownership into a view.
