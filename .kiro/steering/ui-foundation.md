---
inclusion: always
---
# UI foundation contract (reuse first)

The design system in `src/ChunkPilot.App/Themes` and `src/ChunkPilot.App/DesignSystem` is the
permanent foundation. All UI work extends it; nothing works around it.

## Before writing any UI
1. Search `docs/UI-COMPONENT-CATALOG.md` for a component that already does the job, or is one variant
   away from doing it. Reuse or extend it.
2. If a genuinely new control is needed, add it to the shared system **and** the Design Gallery
   first, document it in the catalog with its anatomy and required states, and only then use it in a
   page.
3. Adding a token means updating `Tokens/ColorTokens.xaml` (or the relevant token file),
   `Overlays/HighContrast.xaml` where applicable, and `docs/UI-DESIGN-SYSTEM.md` in the same change.

## Always
- Consume semantic tokens: `App…` brushes with `DynamicResource`, metrics and typography with
  `StaticResource`.
- Use shared component keys and the lookless components; compose them, never re-template them.
- Use `ds:AppIcon` / `ds:AppButton.Icon` with `AppIconKind`.
- Use `ds:AppLayout.Mode` triggers for responsive behaviour and `ds:AppMotion.IsEnabled` for motion.
- Keep public keys prefixed `App…` and internal building blocks `Internal…`.
- Keep the merged-dictionary list in `App.xaml` flat and in step with `AppTheme.ThemeDictionaries`.

## Never
- Page-local colours, hex literals, font families, font sizes, radii, shadows or motion values.
- A second button, input or card template, or any unexplained one-off styling.
- Emoji, raw glyph strings, private-use characters, `Segoe Fluent Icons` / `Segoe MDL2` references, or
  any `FluentIcons` reference outside `Themes/Controls/Icons.xaml`.
- `TabControl` for navigation; use shell destinations or `AppSegmentedControl`.
- A scroll region nested inside the primary page scroller, or horizontal scrolling of primary content.
- A default `MessageBox` for a product error; use `AppAlert`, `AppToast` or a dialog surface.
- Adding entries to `Themes/Compatibility/LegacyAliases.xaml`. It only shrinks.

## Verification
Run `DesignSystemContractTests` and a Release build for every UI change. Review visual changes in the
Design Gallery (`ChunkPilot.exe --design-gallery`, or `--render <dir>` for Wide/Standard/Compact
captures) using its synthetic fixtures only.

See `docs/UI-DESIGN-SYSTEM.md`, `docs/UI-COMPONENT-CATALOG.md`, `docs/UI-RESPONSIVE-RULES.md`.
