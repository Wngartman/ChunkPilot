# Forge Signal design system

Forge Signal is the WebUI visual language: a near-black graphite canvas, violet-charcoal navigation, stepped neutral surfaces, cool-white text, and a restrained brand-violet signal for focus, selection, primary actions, chart lines, and the server hero. Semantic green, blue, amber, and red remain reserved for operational state. The established purple stacked-server/summit logo is the palette authority, but violet does not replace surface hierarchy or semantic meaning.

Global CSS custom properties in `src/ChunkPilot.WebUi/src/design-system/tokens.css` own color, typography, spacing, radius, border, shadow, control, motion, focus, density, sidebar, and breakpoint values. Feature CSS composes those tokens and must not add page-local colors. Interface typography uses a locally bundled Inter Variable Latin subset with Segoe UI Variable/Segoe UI/system fallbacks; console and identifiers use the Windows monospace stack. Lucide is the sole functional icon family.

Hierarchy comes first from alignment, spacing, one-pixel borders, dividers, rows, and tonal surface steps. Radii stay between 4 and 10 px. Cards are specific compositions rather than one universal rounded container. Motion is 80–220 ms, uses opacity or small transforms, and is removed by `prefers-reduced-motion`. Forced-colors retains focus, selection, and textual state.

Reusable primitives cover buttons, icon buttons, inputs, search, status, metrics, sparklines, headings, empty/unavailable states, confirmation dialogs, shell navigation, server identity, settings rows, save bars, and a portal-based action menu. Radix Dropdown Menu supplies collision-aware placement, keyboard navigation, focus return, and outside-click behavior for the server action menu; other controls remain small semantic primitives rather than adopting a themed component framework.

Product builds with configured CurseForge application-service access show **No API key required**
and never send users into personal-key setup. Provider outages and quotas remain retryable failures,
not a request for credentials. Builds using personal-key access retain a narrowly scoped native
credential dialog using the existing `AppDialogWindow`,
`AppPasswordBox`, `AppAlert`, and button resources. `CurseForgeSetupButton` reuses the shared WebUI
button in General settings and unavailable-provider discovery. Its bridge request has no parameters;
the renderer receives only configured/cancelled state. Empty input disables saving, validation is
bounded and cancellable, failures preserve previous configuration, and all submit/close paths clear
the native password field. Keys never appear in React state, browser storage, diagnostic messages,
or plaintext App-to-Agent requests. Provider discovery refreshes after an accepted native change.

## Compact layouts and interaction

Shared selected-state, typography, page-width, page-padding, touch-target, and scrollbar tokens keep
the shell and pages consistent. Primary content has one page scroller. Dialogs keep actions visible
with a bounded body scroller, stable initial focus, a keyboard focus loop, Escape, and focus return.
Searchable comboboxes preserve text editing and the selected item; the opt-in `wrapLabels` variant
shows full release identities in both the trigger and a viewport-bounded popup. Ordinary filters
retain compact single-line controls.

Modpack discovery separates search/actions from filters and responds to its own container width.
The two-pane layout reserves at least 340px for release details and becomes one column below
740px. Project status appears below the identity; measured virtual rows retain a bounded list.
Player, backup, and version actions stay on one row at desktop widths; compact rosters use cards.
Connection summaries give the address its own space, with evidence/actions below it. A short
overview audience label does not replace the exact native connection evidence under **How to join**.
An unavailable player count never carries a positive old protocol hint. A failed route keeps the
shell available and offers an explicit retry without silently restarting an operation.

The console fits the remaining workspace with a two-row compact toolbar and a reserved
`--cp-console-min-log-height` of 80px. Short fine-pointer windows use the shared 20px
`--cp-font-heading-compact` token; if header, progress, controls, and logs cannot all fit, the outer
workspace scrolls instead of removing the log viewport. These size tokens need no forced-colors
override; the existing semantic console and focus colors remain authoritative.

The file workspace uses available width for side-by-side panes. Selecting an editable file brings
the loaded editor into view and focuses it once per path, without refocusing during typing. The
shared navigation guard protects dirty drafts across file, folder, tab, and server navigation;
cancel retains the draft. File/folder changes and unmount invalidate read/save generations so a
late response cannot replace the newly selected view. Directory sizes remain unmeasured rather
than initiating extra traversal. These presentation and fixture checks are not old-hardware
performance certification or real-server acceptance.

Loader inventories show at most 48 rows per page and retain search over the entire catalog. Only
native `canResolveIntegrity` permits focused older-build verification. Selection and response must
match the exact platform, Minecraft version, loader version, and build ID; creation remains disabled
until the native result reports selectable and integrity metadata. Stale requests cannot replace a
new selection. Installed packs expose the shared JAR inventory read-only; there are no independent
mod update controls and no inferred per-file ownership labels.

Deterministic `curseforge-service`, `curseforge-service-unavailable`, and
`curseforge-service-rate-limited` fixtures exercise application-service presentation without keys,
provider calls, or an Agent. Fixture version labels remain explicitly `+fixture`, not build proof.
