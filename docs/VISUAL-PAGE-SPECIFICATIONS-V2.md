# ChunkPilot page specifications v2

Status: planning document. Applies the system in [`VISUAL-SYSTEM-V2-SPEC.md`](VISUAL-SYSTEM-V2-SPEC.md) to each page. Evidence labels are defined in [`VISUAL-AUDIT-V2.md`](VISUAL-AUDIT-V2.md#evidence-labels).

**Truthfulness rule, binding on every page below.** `[R: AGENTS.md]` Never invent TPS, player counts, uptime, reachability, backup success, or update availability. A value ChunkPilot does not know renders as an explicit *Unknown* or *Not configured*, never as a blank, a dash, a zero or a plausible guess.

Pages requiring **full recomposition** (not token updates): Dashboard, Automation, and every empty state. Pages requiring **refinement only**: Servers, Activity, Settings, and the server workspace pages.

---

## 1. Shared page skeleton

```
┌────────────────────────────────────────────────────────┐
│ AppPageHeader   title · subtitle · status · actions    │  auto
├────────────────────────────────────────────────────────┤
│ Toolbar (optional)                                     │  48
├────────────────────────────────────────────────────────┤
│ FILLER — exactly one region with Height=*              │  *
│   owns the vertical remainder and the page's scroller  │
├────────────────────────────────────────────────────────┤
│ Footer / status strip (optional)                       │  auto
└────────────────────────────────────────────────────────┘
```

`[D]` Rules for every page:

- The header is **always** present — including in empty states. This fixes defect **D-7**.
- Exactly one filler region. If a page has no natural filler, it uses the full-height empty composition (`VISUAL-SYSTEM-V2-SPEC.md` §3.5).
- Primary action lives in the header. **When the page is empty, the header actions are the only actions** — the empty state offers the same commands as a large centred cluster and the header's are hidden. This resolves the duplication in **S-5** without removing either affordance. `[S][R: MainWindow.xaml:330-347, 422-446]`
- Capped regions are centred, never left-aligned.

---

## 2. Dashboard — full recomposition

### 2.1 No servers

`[L]` Current: a ~330×110 island in a ~1.6 Mpx field at 1920×1040, with **no page header at all**.

`[D]` Replacement:

| Region | Content |
| --- | --- |
| Header | Title "Dashboard" · subtitle "Your servers, at a glance" · no actions |
| Filler, centred column (max 640) | 64 px brand mark · "Set up your first server" (24 SemiBold) · one explanatory sentence ≤56ch · **[Create server]** primary + **[Add existing server]** secondary |
| Below, strip (max 1280, centred) | Three truthful capability cards |

`[D]` The three cards state only verifiable behaviour, with no numbers:

1. **"Java is handled"** — ChunkPilot downloads and manages the correct Java runtime. It never changes your system Java. `[R: AGENTS.md]`
2. **"Changes are reversible"** — Updates are staged, verified, then activated, with a recovery point kept first. `[R: AGENTS.md]`
3. **"Your worlds stay yours"** — Existing folders are used in place by reference unless you choose otherwise. `[R: AGENTS.md]`

`[D]` Prohibited here: fake statistics, sample server rows, a mock activity feed, an empty "Recent activity" card, marketing superlatives, or an oversized single card. `[I]` An empty placeholder card is worse than no card — it reads as a loading failure.

### 2.2 With servers

| Region | Content |
| --- | --- |
| Header | Title · subtitle · **[Create server]** |
| Row 1 — "Needs attention" | Shown **only if non-empty**. Crashed, failed operation, update staged but not activated, recovery point required. Warning-toned card. |
| Row 2 — Active servers | Up to 4 server summary cards; running first. Each: identity mark, name, implementation + version, lifecycle badge, primary lifecycle action. |
| Row 3 — Recent operations | Last 5 completed or in-flight operations, human-readable, with a link to Activity |
| Filler | Row 2 expands; the page scrolls as a whole |

`[D]` The Dashboard **does not** reproduce the Servers library. It caps at 4 summary cards and links onward. `[D]` Backup and update information appears **only when known** — no "last backup: never" unless that is a verified fact rather than an absence of data.

`[D]` Sub-empty-states: if "Needs attention" is empty it is **removed**, not shown empty. If there are no recent operations, that card shows a single muted line, not an empty table.

---

## 3. Servers library — refinement

`[S]` Already one of the stronger pages. Refine; do not discard.

| Element | Spec |
| --- | --- |
| Header | Title "Servers" · subtitle · result count in the status slot · **[Add existing]** secondary + **[Create server]** primary |
| Toolbar | Search (280) · filter combo · sort combo. Wraps to two rows in Compact. Left-aligned; **does not** stretch to fill width. |
| Filler | The list. Uncapped width, virtualised, owns the page scroller. |
| Row | 56 px. Identity mark 32 · name 16 SemiBold · meta 13 muted (implementation, version) · state badge · action cluster |
| Row hover | `AppSurfaceHover`, 90 ms |
| Row selection | `AppAccentSubtle` + 3 px `AppAccentIndicator` left edge |
| Primary row action | Whole row opens the server workspace |
| Secondary actions | **Always visible**, not hover-only — hover-only actions are a keyboard and touch failure |
| Attention state | 3 px left edge in the warning/danger tone **plus** a badge. Never a full-row tint. |
| Missing metadata | Explicit "Version unknown" / "Not detected" `[S]` already correct in the gallery |
| Long names | Ellipsis + tooltip + full automation name |
| Empty result (filtered) | Inline: "No servers match *term*" + **[Clear filters]**. Not the full empty composition. |
| No servers at all | Full-height empty composition; header actions hidden (§1) |
| Ultra (≥1800) | Optional detail preview panel, 380 wide, right side, showing the selected server's summary. Collapsible; off by default. |
| Compact | Meta drops to one line; action cluster collapses to an overflow button |

`[D]` **Server identity mark:** a deterministic 32 px rounded square derived from a hash of the server id, carrying the server's initials, from a fixed 8-hue palette at fixed saturation and lightness. `[I]` This is what Direction B was right about — per-server identity — delivered without fetching artwork, without network access, and without inconsistency between modpack and vanilla servers.

`[D]` The list stays a flat virtualised list. No grouping, no alternating tint, no card grid. `[I]` Browsing speed is the point of this page.

---

## 4. Automation — full recomposition

`[S]` Current: an empty four-column table (Name / Action / Type / Next run / Enabled) occupying a small band at the top of a large blank page. `[I]` This is the single most "administrative utility"-looking screen in the product.

### 4.1 Empty

| Region | Content |
| --- | --- |
| Header | Title "Automation" · subtitle "Schedules and recipes that run on their own" |
| Filler, centred column (max 640) | 64 px icon · "Nothing runs automatically yet" · one sentence · **[Create automation]** primary |
| Below, strip | Three **template cards**, clearly labelled as templates and not as existing items |

`[D]` The templates are *offers*, not fabricated rows: "Back up every night", "Restart daily at 4am", "Check for updates weekly". Each is a card with a title, one line of description and a **[Use this]** action. **No empty table is shown.** `[I]` An empty table with column headers is the clearest possible signal that a product is a database viewer.

### 4.2 Populated

`[D]` Responsive rows, not a raw column table:

| Element | Spec |
| --- | --- |
| Row | 56 px. Name 16 SemiBold · action summary in plain language ("Back up Survival, keep 7 days") · next run as relative + absolute time · enabled switch · overflow |
| Enabled | A switch — it applies immediately `[R: VISUAL-SYSTEM-V2-SPEC.md §6.2]` |
| Attention | Failed last run: danger left edge + badge + reason |
| Next run unknown | "Not scheduled", never blank |
| Grouping | By target server when more than one server has automation |
| Wide+ | An optional right panel showing the selected automation's history |

`[D]` 12-hour times in the UI, raw timestamps preserved in logs `[R: AGENTS.md]`.

---

## 5. Activity — refinement

| Element | Spec |
| --- | --- |
| Header | Title · subtitle · filter chips (All / Failed / In progress) |
| Filler | Virtualised timeline, uncapped, owns the scroller |
| Entry | 20 px status icon · plain-language summary (14) · server name · relative time · duration. Expandable. |
| Expanded | Operation id (mono), exact timestamps, affected paths, evidence, and for failures the preserved failed state `[R: AGENTS.md]` |
| States | Completed · In progress (with progress) · **Cancelled** · **Failed** · **Rolled back** — each with its own icon and tone, never colour alone |
| Rollback | Shown as a linked pair with its originating operation, not as an unrelated entry |
| Empty | Full-height composition: "No activity yet. Operations you run will be recorded here." No fabricated entries. |
| Grouping | Day separators (Today / Yesterday / date) |

`[D]` Raw technical detail is always one disclosure away and never on the surface. `[R: AGENTS.md]` "Plain language first; technical details under Advanced or More details."

---

## 6. Settings — refinement

| Element | Spec |
| --- | --- |
| Layout | Single column, `AppMeasureForm` 720, **centred** `[R: MainWindow.xaml:487 currently 720 left-aligned — centre it]` |
| Sections | General · Appearance and accessibility · Java and runtimes · Storage and backups · Network · Privacy and diagnostics · Advanced · About |
| Section | `AppCard` with a 16 SemiBold title and a one-line description |
| Row | Label + control right-aligned; description below the label in muted 13 |
| Search | **Justified** once sections exceed ~8. Filters rows and shows the matching section. Not before then. |
| Disclosure | Advanced collapsed by default; a collapsed section may **never** hide state needed to act safely `[S]` already the stated rule |
| Destructive | Isolated at the bottom of their section, danger-toned, always requiring explicit confirmation naming what will be affected |
| Privacy | States plainly that nothing leaves the PC without consent `[R: AGENTS.md]`. Future diagnostics are opt-in and shown as off. |
| Appearance | Theme, Reduced Motion (respecting the system default), density, and — when it ships — the Mica backdrop toggle |

`[D]` **Global vs server Settings must be unmistakable.** Global Settings is reached from the global rail group. Server Settings exists only inside a server workspace, and its page header carries the server's identity mark and name. `[D]` The two never share a title: "Settings" globally, "*Server name* settings" in the workspace.

---

## 7. Selected-server workspace — shared foundation

`[D]` One foundation for Overview, Console, Manage, Access, Protection and Settings.

```
┌──────────────────────────────────────────────────────────────┐
│ IDENTITY HEADER                                              │
│  [mark 40] Name (24 SemiBold)        [Primary lifecycle]     │
│            Paper 1.21.4 · Running     [Secondary] [⋯]        │
│  ── warning strip, only when there is a warning ──           │
├──────────────────────────────────────────────────────────────┤
│ Overview │ Console │ Manage │ Access │ Protection │ Settings │  segmented
├──────────────────────────────────────────────────────────────┤
│ FILLER                                                       │
└──────────────────────────────────────────────────────────────┘
```

| Element | Spec |
| --- | --- |
| Identity header | Persistent across all six destinations. Never scrolls away. |
| Lifecycle state | Badge with icon + text; lifecycle **state** and **intent** stay visually distinct `[R: AGENTS.md]` |
| Primary action | Exactly one, reflecting intent: Start / Stop / Cancel. Disabled with a reason, never hidden. |
| Warnings | A strip below the header, only when real: EULA not accepted, recovery point required, update staged, crash loop |
| Subnavigation | Segmented control `[R: AGENTS.md]` — **never** a `TabControl` |
| Content width | Overview/Access/Protection/Settings capped at `AppMeasureContent` 1280 centred; Console and Manage uncapped |
| Capability gating | Destinations irrelevant to the server's `ServerCapabilityProfile` are hidden, not disabled `[R: AGENTS.md]` |
| Compact | Segmented control scrolls horizontally; identity header drops to one line |

### 7.1 Overview

`[D]` Two columns at Wide+, one at Standard and below.

- **Left:** current state, primary next action, recent operations (5).
- **Right:** configuration summary as information rows — Runtime (Java version, memory), World (name, size), Access (LAN address, public state), Protection (last verified recovery point), Updates (staged or current).
- Every unknown value says so explicitly.
- No graphs, no gauges, no TPS, no player count unless and until ChunkPilot genuinely measures them.

### 7.2 Console

| Element | Spec |
| --- | --- |
| Surface | `AppSurfaceWell` (L\* 3), 13 mono, uncapped, virtualised, bounded buffer |
| Toolbar | Search · autoscroll state · copy · export · clear view (never clears the log) |
| Autoscroll | Follows only while the user is at the bottom; scrolling up pauses and shows unseen-line count + **[Jump to latest]** `[R: AGENTS.md]` |
| Command input | Pinned at the bottom, mono, history on ↑/↓, disabled with a reason when the server is not running |
| Highlighting | Severity only (warn/error), by a left edge plus the level text — never by recolouring the log line, which would corrupt the reading of raw output |
| Raw integrity | Text is never rewritten, re-wrapped destructively, or trimmed mid-line. Copy yields exactly what the server emitted. |
| Accessibility | One polite live region; not announced line by line |
| Motion | **None** |

### 7.3 Manage

`[D]` Segmented sub-sections, each a list with a consistent row: Files · Worlds · Mods · Plugins · Packs · Versions. Sub-sections not supported by the server's capability profile are absent.

- Rows show name, version/size, source, and state.
- Removal defaults to Recovery/Recycle Bin and **lists exact paths** `[R: AGENTS.md]`.
- The active version and the active world are visibly marked and cannot be deleted `[R: AGENTS.md]`.
- Ownership-uncertain files are labelled and excluded from bulk actions.

### 7.4 Access

`[D]` Ordered by increasing risk, each a card: **LAN** (address, copy) · **Public** (explicitly off by default; never auto-enabled) · **Crossplay** (only when verified, with the Java TCP / Bedrock UDP distinction explicit) · **Allowlist** · **Operators** · **Advanced networking** (collapsed).

- Addresses for localhost, LAN, public and Bedrock are **distinct**, each copied from its own action `[R: AGENTS.md]`.
- A local port check is **never** presented as proof of public reachability `[R: AGENTS.md]`.
- Reachability that has not been verified reads "Unknown — only a local check ran" `[S]` already correct in the gallery.

### 7.5 Protection

`[D]` Cards: **Recovery points** (list with created, size, kind, verified state) · **Restore** · **Retention** · **Verification**.

- In-progress and failed backups are visibly distinct and **cannot** be restored, exported, or presented as complete `[R: AGENTS.md]`. `[S]` The gallery already models `Incomplete` correctly with an em-dash size — preserve that.
- Restore requires a verified recovery point and states plainly what will be overwritten.
- A failed restore preserves the failed state and says so `[S]` already correct.

---

## 8. Create Server v2 — visual integration

`[R]` Per `CREATE-SERVER-V2-ARCHITECTURE.md` and `CREATE-SERVER-V2-ROADMAP.md`. This section is visual only; it changes nothing about that architecture.

`[D]` This is the product's flagship first-run moment and must not look like an administrative form.

| Element | Spec |
| --- | --- |
| Window | Modal, 960×680 at Standard, resizable, 92% width in Compact. Its own window, not a page. |
| Composition | Left step rail (200) · centre content · right details panel (320, Wide+ only) |
| Step rail | Vertical, numbered, showing completed / current / upcoming. Completed steps are clickable; upcoming are not. |
| Intent cards | The first step is a grid of large choice cards (Vanilla / Performance / Modpack / Plugins / Crossplay / Custom), each with a 32 px icon, a title and one plain-language line. Selection uses the accent edge + fill, never colour alone. |
| Version and project lists | The standard virtualised row treatment — the wizard invents no new list style |
| Catalog results | Row with project icon (when the provider supplies one), name, author, downloads, and compatibility conclusion |
| Details panel | Selected project's description, versions, dependencies and compatibility evidence |
| Search / filters | The standard search box and combos |
| Compatibility evidence | Confirmed / Likely / Possible / Unknown / Unavailable, each with its own icon and text `[R: AGENTS.md]` — **never** a green tick for an unverified match |
| Warnings | Inline at the point of choice, not deferred to Review |
| Review | Single column, 720 centred; every decision listed with an inline **[Change]** returning to that step; the EULA control is unchecked and explicit `[R: AGENTS.md]` |
| Progress | Full-panel operation view: current phase, determinate bar only when the total is known, operation id in mono, staging path, and a working **Cancel** |
| Failure | Plain-language cause, what was and was not changed, the preserved staging directory, and **[Retry]** / **[Back]** — never a dead end |
| Completion | The new server's identity card and one clear next action; the wizard does not merely close |
| Advanced | A collapsed disclosure on each step; never a separate "expert mode" |
| Compact | Step rail collapses to a horizontal progress strip; details panel becomes a sheet |

`[D]` The wizard uses only shared components. If it needs something new, that component is added to the shared system and the Design Gallery **first** `[R: AGENTS.md]`.

---

## 9. Dialogs, feedback and window chrome

| Element | Spec |
| --- | --- |
| Dialog | 480 wide (92% Compact), `AppSurfaceOverlay`, `AppElevationDialog`, focus trapped, Escape cancels, initial focus on the **safe** action |
| Destructive dialog | Names exactly what will be affected; the confirming action is the danger button and is **not** the default focus |
| Toast | 360, bottom-right, stacking to 3; auto-dismiss on success, **persistent on error** |
| Alert | Inline, tone surface + tone stroke + icon + text |
| Never | A default `MessageBox` for a product error `[R: AGENTS.md]` |
| Title bar | Dark, native buttons, 24 px app mark, title = "ChunkPilot" or "*Server* — ChunkPilot" |
| Close | Never blocks with a modal confirmation; UI closes immediately; `SafeApplicationExit` to the agent `[R: AGENTS.md]` |
| Splash | Mark at 96 px on the canvas surface; hands over on the first real frame `[S]` size already acceptable — only the mark changes |
