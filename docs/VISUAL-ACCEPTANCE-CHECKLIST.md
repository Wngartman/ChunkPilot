# ChunkPilot visual acceptance checklist

Status: planning document. Run this against **every** page, dialog and component change from [`VISUAL-MIGRATION-ROADMAP.md`](VISUAL-MIGRATION-ROADMAP.md) onward. Companion: [`VISUAL-SYSTEM-V2-SPEC.md`](VISUAL-SYSTEM-V2-SPEC.md).

**A checklist item is passed only when it has been observed.** Reading the XAML is not observation. Render it, launch it, look at it. `[R: AGENTS.md]` "A feature is not complete because it compiles or renders."

Record the result in the task's commit message or worklog as `n/m passed`, and list every exception with a reason.

---

## A. Visual hierarchy

- [ ] Exactly **one** page title, and it is the largest text on the page.
- [ ] Exactly **one** primary (accent) action visible per surface. No surface has two purple elements competing.
- [ ] The most important value on the page is the most visually prominent thing on it.
- [ ] Every card has a title, or a self-evident reason not to.
- [ ] Section eyebrows are weaker than their sections, and sections are weaker than the page title.
- [ ] Nothing decorative is present that carries no information.

## B. Typography

- [ ] Every text element uses a named style from `Themes/Controls/Text.xaml`. No page sets `FontFamily`, `FontSize`, `FontWeight` or `Foreground` on a `TextBlock`.
- [ ] No use of `Medium` weight anywhere — it does not exist on Windows and silently becomes SemiBold.
- [ ] SemiBold appears only on: titles, card titles, server names, the one strong value in a row, badges, table headers, the primary button, and selected nav/segment items.
- [ ] No uppercase text outside the section eyebrow.
- [ ] Explanatory prose is no wider than 56ch (empty states) or 80ch (body).
- [ ] No important text is below 13 px. Nothing meaningful is at 11 px.
- [ ] Long strings trim with an ellipsis and expose the full value via tooltip **and** automation name.

## C. Spacing and layout

- [ ] Every margin, padding, gap and size comes from `MetricTokens.xaml`. No literals.
- [ ] The page has **exactly one** filler region with `Height=*`.
- [ ] Every capped region is **centred**, not left-aligned.
- [ ] Lists, tables and the console are **not** capped.
- [ ] At 1920×1080 there is no more than one screen-height of undesigned empty space.
- [ ] Beyond 1600 px, surplus width becomes symmetric margin — cards do not keep stretching.
- [ ] No fourth column appears at ultrawide.

## D. Surfaces and depth

- [ ] Every card boundary is visible without hunting for it (stroke vs its own fill, ΔL\* ≥ 12).
- [ ] Surface levels are used semantically — a raised card means "this matters more", not "this is a box".
- [ ] At most one raised card per surface.
- [ ] No shadow on any card. Shadows only on dialogs, popovers, menus and toasts.
- [ ] No blur, glass, texture, noise or gradient outside the three sanctioned uses.
- [ ] The console and data wells use the well surface, not the card surface.

## E. Colour

- [ ] Semantic `App…` brushes via `DynamicResource` only. **No hex literal outside `Palette.xaml`.**
- [ ] No page-local colour of any kind.
- [ ] Status colour is never the only signal — an icon and text always accompany it.
- [ ] Status hues are not used decoratively or as brand colour.
- [ ] `AppAccentIndicator` is used only for unlabelled accent marks; `AppAccent` only for labelled fills.

## F. States

- [ ] Hover, pressed, focus, selected, disabled and loading are all present and visibly distinct on every interactive element.
- [ ] Pressed feedback is **instantaneous**.
- [ ] Disabled controls are disabled with a **reason** available, not silently hidden.
- [ ] Selection uses at least two cues (edge + fill + weight), never colour alone.
- [ ] Keyboard focus and pointer selection are visually distinct — a clicked row shows no focus ring.

## G. Empty, loading and error

- [ ] The empty state includes the **page header**.
- [ ] The empty state is a full-height composition, not a small centred island.
- [ ] The empty state is not an empty table, an empty list or an empty card.
- [ ] Empty, loading, unavailable, failed and destructive states are each explicitly designed.
- [ ] A filtered no-results state is distinct from a genuinely-empty state.
- [ ] Loading uses a skeleton or a busy indicator, never a blank region.
- [ ] Errors state the cause in plain language, what was and was not changed, and a way forward.
- [ ] No default `MessageBox` for a product error.

## H. Narrow window

- [ ] Usable at 800×600.
- [ ] No horizontal scrolling of primary content at any size.
- [ ] No nested scroll region inside the page scroller.
- [ ] Toolbars wrap; they do not clip or overflow.
- [ ] The rail collapses to icons **and the labels actually disappear** (defect D-1).
- [ ] Every action remains reachable — nothing is dropped, only relocated.
- [ ] Dialogs fit at 92% width.

## I. Large monitor and DPI

- [ ] Inspected at 1280×720, 1440×900, 1920×1080 and 3440×1440.
- [ ] Inspected at 100%, 125% and 150% scaling.
- [ ] No clipped text and no clipped control at 150%.
- [ ] Icons are crisp at every scaling — no runtime downscale of an oversized source.
- [ ] Hairlines remain 1 device pixel and do not disappear or double.

## J. High Contrast

- [ ] Every new token is mapped in `Themes/Overlays/HighContrast.xaml`, in the same commit.
- [ ] The page is fully coherent under High Contrast — no invisible text, no invisible boundary.
- [ ] Focus is visible under High Contrast.
- [ ] No information is lost when colour is replaced by system colours.

## K. Reduced Motion

- [ ] Every animation is disabled, and no state becomes unreadable as a result.
- [ ] No state requires motion to be understood.
- [ ] Busy indicators render static.

## L. Keyboard and focus

- [ ] Every action is reachable by keyboard.
- [ ] Focus order follows visual order.
- [ ] The focus ring is never clipped by a parent and never shifts layout.
- [ ] Dialogs trap focus, Escape cancels, and focus is restored on close.
- [ ] Initial dialog focus is on the **safe** action, never the destructive one.
- [ ] No affordance is hover-only.

## M. Screen reader

- [ ] Every icon-only control has a tooltip **and** an `AutomationProperties.Name`.
- [ ] Rows expose a composed, meaningful name.
- [ ] Cards are named regions; tables expose their headers.
- [ ] The console is a single live region with a polite update policy — not announced line by line.
- [ ] Status changes are announced when they change.
- [ ] Validation errors are associated with their control and ordered before the submit action.

## N. Truthful data

- [ ] **No invented TPS, player count, uptime, reachability, backup success or update availability.**
- [ ] Unknown values render as an explicit *Unknown* / *Not configured* — never blank, never a dash implying zero, never a guess.
- [ ] Confirmed / likely / possible / unknown / unavailable are visually distinct.
- [ ] An in-progress or failed backup can never be presented as complete.
- [ ] A local port check is never presented as public reachability.
- [ ] No placeholder or sample content ships in a real state.

## O. Performance

- [ ] No regression against the Task 0 baseline for startup, navigation, list render, working set or idle CPU.
- [ ] Lists are virtualised with recycling.
- [ ] No `Effect` inside any item template.
- [ ] Shared brushes, pens and geometries are frozen.
- [ ] Images are decoded at display size and cached.
- [ ] No `UpdateSourceTrigger=PropertyChanged` on a high-frequency source.
- [ ] Commands dispatch **before** any animation resolves.
- [ ] The UI thread is never blocked; no `Thread.Sleep`.

## P. Evidence

- [ ] A runtime screenshot or Design Gallery render of every changed state was **produced and inspected**.
- [ ] Screenshots were taken with an isolated `CHUNKPILOT_DATA_ROOT`, never against real data.
- [ ] Subjective quality claims are backed by an inspected render.
- [ ] Every referenced file and symbol was verified to exist.

## Q. Hard prohibitions — any one of these fails the change

- [ ] No raw, unstyled WPF control anywhere.
- [ ] No black text on a dark surface — no default `Foreground` leakage.
- [ ] No hard-coded colour, font, size, radius, shadow or motion value outside the token files.
- [ ] No second button, input or card template.
- [ ] No duplicated navigation and no duplicated primary action on the same page.
- [ ] No `TabControl` used for navigation.
- [ ] No emoji, raw glyph string, private-use character or icon-font reference.
- [ ] No unresolved horizontal overflow.
- [ ] No oversized undesigned empty region.
- [ ] No fake metric, invented state, or placeholder pretending to be data.
- [ ] No addition to `Themes/Compatibility/LegacyAliases.xaml` — it only ever shrinks.

---

## Sign-off

| Field | |
| --- | --- |
| Task | |
| Commit | |
| Sizes inspected | |
| Scalings inspected | |
| High Contrast inspected | ☐ |
| Reduced Motion inspected | ☐ |
| Keyboard-only pass | ☐ |
| Baseline re-measured | ☐ |
| Result | `n/m passed` |
| Exceptions and reasons | |
