# WebUI reference synthesis

All 18 supplied screenshots were inspected at original resolution on 2026-08-14. They are design evidence only; no competitor artwork, branding, copy, or page composition is included in ChunkPilot.

## Priority evidence

The seven newer Shockbyte views are the strongest structural reference. Their persistent server identity, compact platform/version/player metadata, stable contextual navigation, decisive lifecycle control, full-height console, dense file rows, searchable configuration list, content catalog rhythm, and task scheduling layout make server management easy to locate. ChunkPilot adopts that continuity and task focus, but uses a smaller original hero, a different shell and route model, neutral graphite surfaces, local-first language, and capability-driven destinations.

CloudGuard, the dark member-settings table, SnowUI, and Windows Task Manager contribute precision: near-black layers separated by hairlines, compact headers, disciplined tables, readable metric units, resource bars, consistent baselines, and high information density. ChunkPilot rejects enterprise fleet language, promotional banners, billing/support navigation, fake global analytics, and decorative infrastructure illustrations.

The light Quantro settings screen supplies the clearest hierarchy for category navigation, aligned form rows, bounded input widths, and a sticky unsaved Save/Discard state. The dark notification-search modal contributes focused keyboard search and segmented choices. ChunkPilot keeps dark presentation while adopting their predictable spacing and dirty-state behavior.

The server-type selector informs the Create Server flow: one focused stage, compact search, scannable choices, a strong selected state, and an obvious Continue action. ServerSide contributes simple server-card rhythm and balanced Create/Import placement. The Bisect screens are coverage references for console, files, configuration, backup, schedules, users, networking, and startup, but their saturated navy fields, browser-panel proportions, and long undifferentiated forms are rejected. The catalog screenshot contributes only category/search/card rhythm for truthful installed content; its casino imagery, promotional intensity, and palette are rejected.

## Original direction: Graphite Beacon

Graphite Beacon is ChunkPilot's WebUI design system. It uses a near-black graphite canvas, charcoal navigation, raised neutral working surfaces, cool-white text, one restrained blue-cyan focus/accent family, semantic green/amber/red state colors, one-pixel borders, 4-10 px radii, and modest shadows. The ChunkPilot logo may retain its indigo identity; operational UI does not inherit a purple wash. Server heroes use generated CSS geometry and the local server icon, never Minecraft or competitor art.

The shell is windowed-first at 1280 x 820: a 228 px persistent navigation column, compact integrated title bar, and one flexible workspace. Below about 1120 px the rail compacts and secondary columns stack; primary content never scrolls horizontally. At larger sizes, tables and metric detail expand instead of stretching empty cards.

## Surface decisions

- Dashboard: real server identity first, compact actionable counts, host performance only with authoritative units, attention and activity only when present, and purpose-built zero/one/many layouts.
- Server workspace: a stable compact hero, metadata and status, one lifecycle split action, Share/Open Folder in secondary actions, and obvious Overview/Console/Players/Files/Content/Backups/Versions/Settings navigation.
- Console: one dominant virtualized output surface with a compact filter toolbar and command line; no nested decorative cards.
- Files and tables: dense aligned rows, sticky column labels where useful, clear folder/file or state distinction, restrained row hover, and native actions only where the backend is safe.
- Settings: searchable category rail, aligned reusable rows, concise descriptions, inline validation, and a persistent Save/Discard bar when dirty.
- Create Server: seven focused stages with professional labels, searchable options, preset-first RAM with exact Advanced entry, explicit unchecked EULA, contextual validation, and a concise review.
- Content: installed/capability-backed material only. Catalog visuals are reserved for real provider support; unsupported content receives a professional unavailable state.

## Deliberate rejections

No hosting plans, accounts, support desk, database upsells, remote SFTP, promotional art, fake metrics, fake servers, giant rounded cards, card-within-card composition, heavy glassmorphism, purple-dominant palette, browser chrome, stock admin-template layout, Discord imitation, or copied competitor wording. Unknown values are labelled unavailable rather than rendered as zero.

## Direct inspection corrections

Release-host and fixture captures exposed concrete defects that compilation did not: the first local navigation used a bare virtual origin and produced WebView's network error; the strict message-source comparison then rejected the full `index.html` URL; route changes retained a 42 px workspace scroll offset; unsupported settings categories advertised empty surfaces; and the first deterministic file-editor capture ran before the row was mounted. The host now navigates to the explicit local entry point, validates the parsed trusted origin, resets workspace scroll on route changes, shows only real settings categories, and waits for the editor row before capture. The final 27-image packaged set includes the editor loaded with authoritative fixture text and the Agent-backed Automation surface.
