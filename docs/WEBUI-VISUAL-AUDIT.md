# WebUI visual and interaction audit

Baseline: packaged WebView2 fixtures and an isolated packaged `--webui-preview` run from commit `38a07c1`, captured under `artifacts/webui-visual-polish/baseline` at 1280×820, 1100×700, and 1440×900. All 18 supplied references were inspected directly.

## Evidence

| Current problem | Evidence | Why it feels amateur | Reference principle | Intended correction |
|---|---|---|---|---|
| Component-first composition | Dashboard is a metric strip followed by repeated bordered grids | The component system is more visible than the product task | Shockbyte pages read as one workspace; Linear uses fewer enclosures | Establish the page grid first and reserve panels for independent tools |
| Server context is secondary | Host CPU, memory, and disk precede the server roster | It resembles an infrastructure template | Shockbyte, Xbox, and Playnite foreground the owned server or game | Make servers the first viewport; reduce host resources to a compact supporting rail |
| Typography is brittle | 10–11 px metadata, synthetic fractional weights, negative heading tracking | Dense text looks cramped instead of precise | CloudGuard and Task Manager use confident values and readable supporting text | Compare corrected native Segoe with bundled Inter Variable; use fewer sizes and no negative tracking |
| Flat action hierarchy | Share, folder, restart, lifecycle, and overflow compete in one row | A technical toolbar replaces a clear next action | Shockbyte makes lifecycle primary and demotes secondary operations | Keep one lifecycle control prominent; place folder, restart, rename, and icon in secondary/overflow groups |
| Repeated state | State appears in sidebar, hero, metric strip, performance header, and warnings | Repetition adds chrome rather than decisions | Linear removes unearned visual competition | Keep lifecycle state in server identity; show consequences elsewhere |
| Generic card soup | Overview contains a metric card, performance card, 2×2 status card, console card, and connection card | The first viewport does not read as one operational answer | Best references use asymmetric compositions and dividers | Compose performance, joinability, protection, and activity as one workbench |
| Tonal hierarchy is too flat | Canvas, surface, and raised surface differ little, while borders outline almost everything | The result becomes black rectangles traced in gray | Linear uses warmer neutral steps and fewer separators | Strengthen shell/workspace/tool tones; remove borders that do not clarify interaction |
| Placeholder title-bar copy | `Local server control` is centered in the drag region | It reads like internal design commentary | Native tools keep title regions quiet or contextual | Use quiet route/server context and preserve the drag surface |
| Implementation copy is visible | `Managed by the local Agent`, `existing creation transaction`, and world-traversal commentary | Safety documentation leaks into ordinary operation | Production tools lead with user goals | Keep truthfulness but move implementation detail out of primary copy |
| Create exposes roadmap state | Disabled `More server types` appears beside Vanilla | A disabled future option looks unfinished | The supplied selector shows actionable choices only | Remove unsupported choices and make the real Vanilla path feel intentional |
| Create stage is artificially tall | Two small choices occupy a 520 px stage | The page looks like a wireframe awaiting content | Focused creation references size stages to their decisions | Use a compact platform selection with readiness facts and deliberate progress |
| Abrupt responsive mode | Sidebar jumps from 228 px to 76 px at 1120 px | Context disappears suddenly while header actions can still crowd | Discord and Vercel preserve location as navigation recedes | Add an intermediate compact mode and move secondary actions earlier |
| Route motion implies latency | Every route mounts with a 200 ms translate/fade | Ordinary desktop navigation feels slower than it is | Persistent desktop shells respond immediately | Remove translation and keep only a brief opacity transition where useful |
| Snapshot churn is broad | A new full snapshot and revision are emitted every second even when presentation data is unchanged | Shell and page rerender continuously at idle | Event-driven desktop surfaces should settle at idle | Suppress presentation-identical snapshots and use stable store selectors |

## Reference synthesis

- **Shockbyte** supplies the persistent server identity, compact server metadata, contextual tabs, large console, dense file rows, and obvious lifecycle action. Its branding, artwork, hosting/billing concepts, and exact layout are rejected.
- **CloudGuard, SnowUI, and the dark settings/member references** supply column discipline, data-to-chrome ratio, compact resource summaries, professional tables, and tonal separation. Enterprise-fleet language and decorative analytics are rejected.
- **Quanto and the settings-search references** supply nested category structure, aligned form widths, dirty-state visibility, and controlled Save/Discard placement.
- **The supplied server-type selector** supplies focus, scannability, and a strong selected state. Unsupported platforms and oversized modal treatment are rejected.
- **ServerSide, Bisect, and the catalog reference** contribute server-card rhythm, task coverage, and content-browsing cadence only. Their light palette, older hosting-panel conventions, and promotional art are rejected.

Bounded public research also inspected current public material from [Linear](https://linear.app/now/behind-the-latest-design-refresh), [Discord](https://discord.com/blog/improving-our-mobile-experience), [Vercel](https://vercel.com/changelog/new-dashboard-navigation-available), [Render](https://render.com/docs/render-dashboard), [Docker Desktop](https://www.docker.com/blog/docker-desktop-4-28/), [Playnite](https://playnite.link/), [Crafty Controller](https://craftycontrol.com/), and [Pterodactyl](https://pterodactyl.io/). The recurring useful principles are stable context, subdued supporting chrome, task-first hierarchy, restrained motion, and layouts that remain dense without outlining every region.

## Explored directions

1. **Server Workbench** — server-first 8/4 compositions, compact host/attention rail, persistent atmospheric server identity, and task surfaces built from rows and dividers.
2. **Game Library Control Center** — artwork-forward featured servers and a more consumer-game-library shell.
3. **Operator Ledger** — a very dense Linear-style table shell with minimal atmosphere and keyboard-first navigation.

Server Workbench is selected. It carries the strongest Shockbyte principle without becoming a hosting panel, uses the precision of the best dark dashboards, retains consumer-game identity, scales to future loaders and games, and has the lowest rendering cost of the expressive options.

## Selected correction

The revised design system is **Forge Signal**: neutral carbon surfaces, warm-white type, a restrained mineral-teal accent, semantic operational colors, Inter Variable for interface text, a system monospace stack for technical text, 4–8 px radii, one-pixel borders only where interaction or data boundaries require them, and 80–160 ms opacity/surface motion. Lucide remains the sole functional icon family.

Core pages use a server-first asymmetric grid. The shell stays stable; the active server is persistent; lifecycle is the primary action; console and tables become edge-to-edge task surfaces; settings use aligned rows with a quiet category rail; Create Server becomes a compact guided workspace with no roadmap controls.

## Final visual review

The packaged renderer was reviewed directly in 27 deterministic states at 1280×820, plus 1100×700 and 1440×900. Separate captures covered 125% and 150% scaling, forced colors, and reduced motion. Baseline/final contact sheets for Dashboard, Server Overview, and Create Server are stored under `artifacts/webui-visual-polish/comparisons`.

The largest defects found during iteration were a clipped lifecycle action in the multi-server Dashboard, abrupt sidebar collapse, competing server-header actions, repeated server state, and excessive Overview enclosures. The final pass corrected the grid width, introduced an intermediate navigation width, moved secondary operations into overflow, kept state beside identity, and replaced the Overview card matrix with one asymmetric operational workspace.

Scores use the canonical 1280×820 captures. Every core surface remains at or above 4; a 4 means a visible refinement remains possible, not that the requirement is absent.

| Criterion and visible evidence | Dashboard | Workspace | Overview | Create | Settings |
|---|---:|---:|---:|---:|---:|
| Composition — first viewport reads as one server task | 5 | 5 | 5 | 4 | 4 |
| Hierarchy — title, state, primary action, then support | 5 | 5 | 5 | 5 | 4 |
| Typography — Inter weights are crisp and restrained | 5 | 5 | 5 | 5 | 5 |
| Spacing — shared 4–24 px rhythm aligns regions | 4 | 5 | 5 | 5 | 4 |
| Alignment — rows, metadata, tabs, and actions share baselines | 5 | 5 | 5 | 5 | 5 |
| Density — real state fills the viewport without crowding | 5 | 5 | 5 | 4 | 4 |
| Navigation — persistent location and server context | 5 | 5 | 5 | 5 | 5 |
| Selected states — teal edge/tone differs clearly from hover | 5 | 5 | 5 | 5 | 5 |
| Action hierarchy — lifecycle or Continue is dominant | 5 | 5 | 5 | 5 | 5 |
| Surface treatment — carbon tonal steps replace card soup | 5 | 5 | 5 | 4 | 4 |
| Icon consistency — Lucide stroke and optical sizes match | 5 | 5 | 5 | 5 | 5 |
| Copy — operational language replaces implementation commentary | 5 | 5 | 5 | 5 | 5 |
| Responsiveness — 1100×700 and scaled captures remain usable | 4 | 4 | 4 | 4 | 4 |
| Interaction polish — stable shell, compact overflow, sticky actions | 4 | 5 | 5 | 5 | 5 |
| Originality — Forge Signal is neither a hosting clone nor a template | 5 | 5 | 5 | 5 | 5 |
| Accessibility — forced colors, focus, contrast, reduced motion | 4 | 4 | 4 | 4 | 4 |
| Performance perception — translation delay and idle snapshot churn removed | 4 | 4 | 4 | 4 | 4 |
