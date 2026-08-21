# Responsive WPF rules

ChunkPilot is a resizable native desktop application. Layout follows the width that is actually
available, never an assumption about the monitor.

## Mechanism

Set `ds:AppLayout.IsResponsive="True"` on a window. `AppLayout` then keeps the inherited
`AppLayout.Mode` property in step with that window's width, and every descendant — including control
templates — reacts with a trigger:

```xml
<DataTrigger Binding="{Binding Path=(ds:AppLayout.Mode), RelativeSource={RelativeSource Self}}"
             Value="Compact">
```

No page writes a `SizeChanged` handler and no page invents its own thresholds. The previous shell did
both, and ended up collapsing its sidebar at widths that disagreed with this document.

`AppLayout.Mode` can also be pinned on a subtree, which is how the Design Gallery shows all three
modes at one window size.

## Breakpoints

Declared as tokens in `Themes/Tokens/MetricTokens.xaml`, so code and documentation cannot disagree.

| Mode | Width (device-independent pixels) | Token |
|---|---|---|
| `Compact` | below 900 | `AppBreakpointStandard` |
| `Standard` | 900 to 1279 | |
| `Wide` | 1280 and above | `AppBreakpointWide` |

These are layout modes, not feature sets. **The same commands are available in every mode.**

- **Wide** — navigation rail with labels, one primary content column, and secondary columns where a
  second column genuinely helps.
- **Standard** — navigation rail with labels, one primary content column, secondary information
  folded underneath rather than beside.
- **Compact** — navigation narrows to icons with tooltips, action groups stack, optional summaries
  move behind progressive disclosure, page padding tightens.

## Rules

1. **No horizontal scrolling of primary content, at any width.** Content wraps, stacks or discloses.
2. **One vertical scroller per destination**, `AppPageScrollViewer`. A scroll region nested inside it
   traps the wheel and hides content. Bounded viewers that *are* the component — the console, a
   drop-down, a long table — are the only exceptions, and they declare a bounded height.
3. **Buttons wrap or stack before labels truncate.** Use `WrapPanel` for action groups, not a
   `StackPanel` that overflows.
4. **Nothing safety-relevant is dropped in Compact.** The page title, the current server, active
   operation progress and the primary action survive at every width. A destination is never removed;
   its label moves into the tooltip.
5. **No fixed-height empty tables.** Show `AppEmptyState` instead.
6. **Minimum heights, not fixed heights**, so Windows text scaling can grow a control.
7. **Respect high contrast** — see the token overlay in `UI-DESIGN-SYSTEM.md`.
8. **Restore focus** to the control that opened a compact flyout, drop-down or palette.
9. **Explanatory text is measured**, capped by `AppProseMaxWidth` rather than stretched across a wide
   window.
10. **Test all three widths.** `ChunkPilot.exe --design-gallery --render <directory>` rasterises the
    gallery at 1440, 1100 and 840 so a change can be compared across modes in one pass.
