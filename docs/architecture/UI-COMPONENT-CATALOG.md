# UI component catalog

The complete list of shared components. **Search this file before building anything.** If something
here does the job, or is one variant away from doing it, use or extend it rather than writing a new
control.

Rules for this document:

- Every key listed here must resolve in the loaded theme.
- Every public `App…` key defined under `src/ChunkPilot.App/Themes/Controls/` must appear here.
- Both directions are enforced by `DesignSystemContractTests`, so the catalog cannot drift from the
  code in either direction.

The **Gallery** column says how the component is verified in the Design Gallery:

| Value | Meaning |
|---|---|
| `shown` | Referenced by name in the gallery. Enforced by test. |
| `implicit` | Applied automatically to the standard WPF element; the gallery uses that element directly. |
| `window` | A `Window`-level style, so it cannot be hosted inside the gallery's user control. |
| `internal` | Consumed by another shared template rather than by a page. |

Every component: exposes an accessible name or inherits one, keeps keyboard navigation intact, draws
a visible focus state if it is interactive, and never communicates state through colour alone.

## Type

Anatomy: a single `TextBlock` style. Pick by role, not by the size you happen to want.

| Key | Element | Purpose | States | Gallery |
|---|---|---|---|---|
| `AppWindow` | `Window` | Window defaults: canvas background, native font, layout rounding | normal | `implicit` |
| `AppDisplayText` | `TextBlock` | One page-defining value | normal | `shown` |
| `AppNumericText` | `TextBlock` | One measured figure in a summary tile; never used for prose | normal | `shown` |
| `AppTitleLargeText` | `TextBlock` | Page title; trims rather than wraps | normal | `shown` |
| `AppTitleText` | `TextBlock` | Major section heading | normal | `shown` |
| `AppSubtitleText` | `TextBlock` | Group heading | normal | `shown` |
| `AppBodyText` | `TextBlock` | Default body copy; wraps | normal | `shown` |
| `AppBodyStrongText` | `TextBlock` | A row's primary value; trims | normal | `shown` |
| `AppSecondaryText` | `TextBlock` | Supporting explanation | normal | `shown` |
| `AppMutedText` | `TextBlock` | Metadata and detail | normal | `shown` |
| `AppCaptionText` | `TextBlock` | Badge and dense-label text | normal | `shown` |
| `AppLabelText` | `TextBlock` | Field label above an input | normal | `shown` |
| `AppEyebrowText` | `TextBlock` | Label above a group of sections | normal | `shown` |
| `AppMonoText` | `TextBlock` | Paths, addresses, versions, hashes, output | normal | `shown` |
| `AppScreenReaderText` | `TextBlock` | Text for assistive technology only | normal | `internal` |

## Icons

Anatomy: `Kind` (intent) + `Scale` (size step) + `Variant` (weight). Nothing else.

| Key | Element | Purpose | States | Gallery |
|---|---|---|---|---|
| `AppIcon` | `ds:AppIcon` | The only icon element; decorative, outside the automation tree | small, medium, large, hero, filled | `shown` |

## Buttons

Anatomy: focus ring, surface, optional leading icon via `ds:AppButton.Icon`, label. One shared
template supplies structure, focus and the disabled treatment; variants differ only in resting tokens
and their own hover/pressed triggers.

| Key | Element | Purpose | States | Gallery |
|---|---|---|---|---|
| `AppPrimaryButton` | `Button` | The single most likely safe action on a surface | normal, hover, pressed, focus, disabled | `shown` |
| `AppSecondaryButton` | `Button` | Supporting actions; also the implicit `Button` style | normal, hover, pressed, focus, disabled | `implicit` |
| `AppSubtleButton` | `Button` | Low-emphasis actions in dense rows and toolbars | normal, hover, pressed, focus, disabled | `shown` |
| `AppDangerButton` | `Button` | A destructive action, after review | normal, hover, pressed, focus, disabled | `shown` |
| `AppDangerSubtleButton` | `Button` | A destructive action offered inline, before review | normal, hover, pressed, focus, disabled | `shown` |
| `AppLinkButton` | `Button` | Navigation to detail; never a state change | normal, hover, focus, disabled | `shown` |
| `AppIconButton` | `Button` | Repeated well-known action; tooltip and accessible name required | normal, hover, pressed, focus, disabled | `shown` |
| `AppIconButtonDanger` | `Button` | Row-level remove action | normal, hover, pressed, focus, disabled | `shown` |
| `AppIconToggleButton` | `ToggleButton` | Icon button for disclosure headers | normal, hover, pressed, checked, focus, disabled | `internal` |

## Inputs

Anatomy: focus ring, recessed surface, optional hint via `ds:AppInput.Placeholder`, content. Focus is
a ring plus an accent edge. A validation failure changes the edge and is expected to be accompanied
by a message.

| Key | Element | Purpose | States | Gallery |
|---|---|---|---|---|
| `AppTextBox` | `TextBox` | Single-line entry; implicit style | normal, hover, focus, placeholder, read-only, disabled, invalid | `implicit` |
| `AppMultilineTextBox` | `TextBox` | Multi-line entry | normal, focus, disabled | `shown` |
| `AppMonoTextBox` | `TextBox` | Paths, addresses and hashes | normal, focus, read-only, disabled | `shown` |
| `AppPasswordBox` | `PasswordBox` | Secret entry; implicit style | normal, focus, disabled | `implicit` |
| `AppSearchBox` | `components:AppSearchBox` | Search glyph, hint, clear control that appears only when there is text | empty, filled, focus, disabled | `shown` |
| `AppNumberBox` | `components:AppNumberBox` | Bounded integer entry with steppers; clamps on every path | normal, focus, at-bounds, disabled | `shown` |
| `AppSlider` | `Slider` | Range entry | normal, focus, disabled | `implicit` |

## Selection

Anatomy varies, but none of these animate: the state must be readable the instant it changes.

| Key | Element | Purpose | States | Gallery |
|---|---|---|---|---|
| `AppCheckBox` | `CheckBox` | Choice applied when a form is confirmed | unchecked, checked, indeterminate, hover, focus, disabled | `implicit` |
| `AppToggleSwitch` | `CheckBox` | Preference applied immediately | off, on, hover, focus, disabled | `shown` |
| `AppRadioButton` | `RadioButton` | One choice from a small set | unselected, selected, hover, focus, disabled | `implicit` |
| `AppComboBox` | `ComboBox` | One choice from a list; themed drop-down | closed, open, focus, disabled | `implicit` |
| `AppComboBoxItem` | `ComboBoxItem` | Drop-down row; the chosen item is ticked, not just tinted | normal, highlighted, selected, disabled | `implicit` |
| `AppPropertyChoiceTemplate` | `DataTemplate` | Renders a choice that carries both a stored value and a label. Bound as `ItemTemplate` so the closed control and the open list agree and neither falls back to the item's `ToString` | normal | `internal` |
| `AppSegmentedControl` | `ItemsControl` | Container for in-page view switching; the approved alternative to `TabControl` | normal | `shown` |
| `AppSegmentedItem` | `RadioButton` | One view within a segmented control | normal, checked, hover, focus, disabled | `shown` |

## Navigation

Anatomy of a row: state-independent icon, label, and a selection indicator on the leading edge.
Destinations are data with stable identifiers, bound to a list — never hand-placed buttons.

| Key | Element | Purpose | States | Gallery |
|---|---|---|---|---|
| `AppNavigationRail` | `Border` | The rail container; narrows in Compact | wide, standard, compact | `shown` |
| `AppNavigationList` | `ListBox` | Bound destination list with keyboard cycling | normal | `shown` |
| `AppNavigationRow` | `ListBoxItem` | One destination | normal, hover, selected, focus, disabled | `shown` |
| `AppNavigationRowTemplate` | `DataTemplate` | Row content: expects `Icon`, `Label`, `Description` | normal | `shown` |
| `AppNavigationRowTemplateCompact` | `DataTemplate` | Icon-only row; the label moves to the tooltip. Selected by `AppNavigationList` in Compact mode | compact | `shown` |
| `AppNavigationRowLabel` | `TextBlock` | Row label; hides itself in Compact | normal, compact | `internal` |
| `AppNavigationSectionLabel` | `TextBlock` | Group label above destinations; hidden in Compact | normal, compact | `shown` |
| `AppWorkspaceTabs` | `ListBox` | Horizontal destination strip under the selected-server header | normal | `shown` |
| `AppWorkspaceTabItem` | `ListBoxItem` | One workspace tab. Selection is an accent underline sized to the tab's own content, never an enclosing shape | normal, hover, selected, focus, disabled | `shown` |
| `AppWorkspaceTabTemplate` | `DataTemplate` | Tab content: expects `Icon`, `Label`, `Description` | normal | `internal` |
| `AppWorkspaceTabButton` | `RadioButton` | The same tab language for an in-page view switch bound to a boolean rather than a selected item | normal, hover, checked, focus, disabled | `shown` |

## Surfaces

Exactly one level of grouping is allowed. A card may contain rows and controls, never another card.

| Key | Element | Purpose | States | Gallery |
|---|---|---|---|---|
| `AppPageSurface` | `Border` | The outermost container of a destination; one per page | wide, standard, compact | `shown` |
| `AppCard` | `Border` | Grouped content on the working surface | normal | `shown` |
| `AppRaisedCard` | `Border` | The single most important block on a surface | normal | `shown` |
| `AppSunkenSurface` | `Border` | Recessed well for console, logs and read-only payloads | normal | `shown` |
| `AppAccentSurface` | `Border` | Current-server or active-operation context strip | normal | `shown` |
| `AppDivider` | `Border` | Hairline separator | normal | `shown` |
| `AppScrim` | `Border` | Dims the page behind a modal surface | normal | `shown` |
| `AppPageHeader` | `components:AppPageHeader` | Icon, title, one-line explanation, status slot, secondary actions, one primary action | normal, no-description, compact | `shown` |
| `AppSectionCard` | `components:AppSectionCard` | Icon, header, description, header actions, content; optional disclosure | normal, no-header, collapsible, collapsed | `shown` |

## Data display

| Key | Element | Purpose | States | Gallery |
|---|---|---|---|---|
| `AppStatusBadge` | `components:AppStatusBadge` | Tone dot or glyph plus mandatory text | neutral, info, success, warning, danger, accent, subtle | `shown` |
| `AppServerRow` | `components:AppServerRow` | Optional detached server icon, state indicator, name, subtitle, state text, trailing slot | all tones, hover, selected, icon/no-icon, no-subtitle, no-state | `shown` |
| `AppInfoRow` | `components:AppInfoRow` | Label, value, optional trailing action; states "unknown" explicitly | known, unknown, monospaced, with-action | `shown` |
| `AppList` | `ListBox` | Virtualised list container | normal | `implicit` |
| `AppListRow` | `ListBoxItem` | One list row | normal, hover, selected, focus, disabled | `implicit` |
| `AppConsoleSurface` | `ListBox` | Bounded, virtualised, monospaced output | normal | `shown` |
| `AppTable` | `DataGrid` | Read-only tabular data; one horizontal hairline per row, no striping | normal, empty | `implicit` |
| `AppTableHeaderCell` | `DataGridColumnHeader` | Column header | normal | `implicit` |
| `AppTableRow` | `DataGridRow` | Table row | normal, hover, selected | `implicit` |
| `AppTableCell` | `DataGridCell` | Table cell; selection is expressed by the row | normal, selected | `implicit` |
| `AppProgressBar` | `ProgressBar` | Determinate track, or a pulsing muted track when the total is unknown | determinate, indeterminate, reduced-motion | `implicit` |
| `AppBusyIndicator` | `components:AppBusyIndicator` | The single busy affordance; static under Reduced Motion | active, inactive, reduced-motion | `shown` |
| `AppProgressPanel` | `components:AppProgressPanel` | Operation name, status line, track, operation identity, optional cancel | active, indeterminate, success, warning, danger, cancellable | `shown` |

## Feedback

| Key | Element | Purpose | States | Gallery |
|---|---|---|---|---|
| `AppAlert` | `components:AppAlert` | Inline non-modal message: tone icon, title, message, detail slot, actions, optional dismiss | neutral, info, success, warning, danger, dismissible, with-actions | `shown` |
| `AppToast` | `components:AppToast` | Transient confirmation of a finished outcome; always dismissible | info, success, warning, danger, accent, with-action | `shown` |
| `AppToastHost` | `ItemsControl` | Shell-owned toast stack on the overlay layer | normal | `shown` |
| `AppEmptyState` | `components:AppEmptyState` | Hero icon, title, explanation, at most one real action | first-run, filtered, unavailable, no-action | `shown` |
| `AppLoadingState` | `components:AppLoadingState` | Busy indicator, what is loading, optional detail | loading, reduced-motion | `shown` |

## Overlays and dialogs

Dialog windows keep the native Windows frame — title bar, Alt+F4, snap, DPI handling — and restyle
only the inside. Reinventing window chrome costs real behaviour.

| Key | Element | Purpose | States | Gallery |
|---|---|---|---|---|
| `AppToolTip` | `ToolTip` | Supplementary detail; plain text wraps | normal | `implicit` |
| `AppContextMenu` | `ContextMenu` | Themed context menu on the overlay surface | normal | `implicit` |
| `AppMenuItem` | `MenuItem` | Menu row with check mark and gesture text | normal, highlighted, checked, disabled | `implicit` |
| `AppMenuSeparator` | `Separator` | Menu group separator | normal | `implicit` |
| `AppDialogWindow` | `Window` | Dialog window defaults; native frame retained | normal | `window` |
| `AppDialogHeader` | `Border` | What the dialog will do, stated plainly | normal | `shown` |
| `AppDialogBody` | `Border` | Dialog content | normal | `shown` |
| `AppDialogFooter` | `Border` | Cancel first in tab order, committing action last | normal | `shown` |
| `AppDialogSurface` | `Border` | In-window modal surface for reviewable destructive actions | normal | `shown` |

`ServerIconCropControl` is the product-specific, keyboard-operable square crop surface used by the
server-icon dialog. It consumes shared surface, stroke and accent resources; exposes normalized pan
and zoom state; and shares `ServerIconPixelCrop` geometry with the Agent output path. The surrounding
dialog uses the standard header/body/footer anatomy and always shows the actual 64 x 64 preview.

## Scrolling

| Key | Element | Purpose | States | Gallery |
|---|---|---|---|---|
| `AppScrollBar` | `ScrollBar` | Thin scrollbar with no arrow steppers; the whole thumb is the grab target | normal, hover, dragging | `implicit` |
| `AppPageScrollViewer` | `ScrollViewer` | The one approved vertical scroller for a destination's primary content. Its bar is an **overlay**: 24 dip of grab target drawn over the content's right edge, a 4 dip pill, and no layout width — so content does not shift when the bar appears | normal | `shown` |

## Behaviours and attached properties

Not resource keys, but part of the shared vocabulary.

| Member | Purpose |
|---|---|
| `ds:AppButton.Icon` | Leading icon on any shared button template |
| `ds:AppInput.Placeholder` | Hint text on the shared text input templates |
| `ds:AppLayout.IsResponsive` | Set on a window; keeps `AppLayout.Mode` in step with its width |
| `ds:AppLayout.Mode` | Inherited Wide/Standard/Compact state that templates trigger on |
| `ds:AppMotion.IsEnabled` | Inherited animation permission; storyboards test it before starting |
| `ds:AppAccessibility.IsHighContrast` | Inherited high-contrast state for cases a brush swap cannot cover |
| `AppTheme.Initialize` / `Attach` / `ApplyPreview` | Theme loading, per-window accessibility state, gallery previews |
| `AppWindowChrome.Apply` | The shared dark caption and application icon for **every** window. Applied by `AppTheme.Attach`, so a window that joins the design system gets it by joining. Asks DWM for immersive dark mode rather than imitating a caption, so the real minimize, maximize, close, snap layouts, Alt+F4 and Alt+Tab behaviour and the active/inactive states are kept; skipped under high contrast, and a Windows build that refuses the attribute simply keeps its own caption |
| `AppTone` | Neutral, Info, Success, Warning, Danger, Accent — the tone vocabulary |

## Not yet built

These are named in the roadmap but do not exist yet, and must not be faked by a page:

- **Command palette** — a searchable global command surface owned by the shell.
- **Server switcher** — a dedicated switcher surface; `AppServerRow` is the row it will be built from.
- **Change timeline** — belongs to the Safety Lab milestone.

When one is built, it is added to this catalog and to the Design Gallery before any page uses it.
