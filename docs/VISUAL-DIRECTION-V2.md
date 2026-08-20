# ChunkPilot visual direction v2

Status: planning document. Companion documents: [`VISUAL-AUDIT-V2.md`](VISUAL-AUDIT-V2.md) (why), [`VISUAL-SYSTEM-V2-SPEC.md`](VISUAL-SYSTEM-V2-SPEC.md) (what), [`VISUAL-MIGRATION-ROADMAP.md`](VISUAL-MIGRATION-ROADMAP.md) (when).

Evidence labels are defined in [`VISUAL-AUDIT-V2.md`](VISUAL-AUDIT-V2.md#evidence-labels).

## The decision

**ChunkPilot adopts Direction A′ — "Instrument panel".**

A native premium control centre that reads as a *precision instrument for something valuable*, not as an administrative console and not as a consumer entertainment app. Dark surfaces pitched at a **readable depth rather than near-black**, with structure carried by decisive edges; a quiet neutral field with a **small number of loud moments**; density that increases with the user's engagement rather than being fixed at one level.

The one-line test for every future decision:

> Does this make the application feel like it is *holding something valuable steady*?

The prime is deliberate. Direction A as sketched in the brief ("highly polished but quiet") is right about polish and wrong about quiet — quietness is what the product already has, and it is the failure. `[L]` The interface currently occupies L\* 3–14.5, some seven points darker than comparable modern dark applications, with card edges at two-thirds the strength they need. The product is not too loud; it is under-articulated. A′ keeps A's restraint in *colour and ornament* while being distinctly assertive in *depth, weight and composition*.

## Why this direction

| Requirement | How A′ satisfies it |
| --- | --- |
| First-time Minecraft host | Calm, uncluttered, one obvious action per page. Never confronted with a table of raw columns. |
| Enthusiast | Density scales up as servers accumulate; the same surfaces just carry more. |
| Technical administrator | Console, tables and diagnostics get a purpose-built dense treatment inside the same system, not a different-looking region. |
| Community / commercial operator | The instrument metaphor extends to fleets and remote nodes without a restyle; nothing in it is Minecraft-specific. |
| "Apple polish" | Comes from surface hierarchy, optical alignment and restraint — not from imitation. |
| "Discord immediacy" | Comes from commands acting before any animation resolves, plus visible state feedback at 90–150 ms — not from imitation. `[L]` The existing motion durations are already correct; the work is in perceived responsiveness (see the performance section of the spec), not in retiming. |
| Lightweight on ordinary PCs | Depth is delivered with surface value and hairlines, which cost nothing. `[R: ElevationTokens.xaml]` The existing decision to avoid card shadows is preserved. |
| `Easy by default, advanced on demand` | Expressed structurally: a default surface carries the answer, and disclosure carries the evidence. |

## The four commitments

These are the load-bearing ideas. Everything in the spec derives from them.

### 1. Depth you can actually see

`[D]` Depth is specified in perceptual lightness (`ΔL*`), never in WCAG ratios — the ratio formula is meaningless between two dark surfaces and would mislead every implementer who used it.

| Boundary class | Target |
| --- | --- |
| Working range, sunken → raised | **L\* 6 → 20** (currently 3.1 → 14.5) |
| Surface fill step | **ΔL\* ≥ 4** (currently 4.7 — already correct, preserve it) |
| Card / structural stroke against its own fill | **ΔL\* ≥ 12** (currently 8.4) |
| Hover against its rest state | **ΔL\* ≥ 4** (currently 5.0 — already correct) |
| Pressed against hover | **ΔL\* ≥ 3** (currently 3.4 — already correct) |

`[I]` The single most important line in that table is the third. The fill steps are already right; the edges are not. Lifting the range gives every step more perceptual room and, as a free consequence, drops primary text from a harsh 17:1 to a comfortable ~13:1 without touching the text token.

Separately, and to the WCAG standard rather than `ΔL*`: anything that **conveys information** — selection, focus, error, status — must reach **3:1 against adjacent colour**, and must never rely on colour alone.

### 2. One loud thing per surface

`[D]` Purple is spent on exactly one element per surface: the primary action, or the current selection, or the focused control — never two at once. The current system already says this `[R: docs/UI-DESIGN-SYSTEM.md]` and largely honours it. What changes is that once the surrounding neutrals are pitched correctly, the accent stops being the only thing with any presence, so it can be spent even more sparingly and still dominate.

### 3. Pages compose vertically

`[D]` A page is not a stack of controls with leftover space. Every page declares what owns its vertical remainder: a list that grows, a panel that fills, or an intentional composition that is *designed* to be spacious. Empty states become **full-height compositions**, not centred labels.

### 4. Weight carries hierarchy, size carries importance

`[D]` With a real weight ramp restored (Regular / Semilight / SemiBold, with Medium abolished as unusable), hierarchy stops depending on size steps of 2–3 px. This is what will make the typography read as modern.

## Directions considered and rejected

Three directions were developed before A′ was selected.

### Direction B — Community-first modern desktop *(rejected as primary)*

More expressive: strong per-server identity, server artwork, richer imagery, more colour energy.

**Why it is weaker as the primary identity:**

- `[I]` It optimises for the populated, multi-server, socially-oriented case — but the product's hardest and most common moment is the **empty first run**, where there is no server identity to be expressive with. A direction that is strongest when full and weakest when empty is the wrong primary for an application whose worst screen today is the empty one.
- Server artwork implies fetching or generating imagery. `[R: AGENTS.md]` "Local-first and private. No accounts, telemetry, ads, or remote services." Modpack icons are available from providers, but vanilla and imported servers have none, so the system would be inconsistent by construction.
- `[I]` It pulls toward the "gamer" register the user explicitly excluded, and away from the commercial-operator future.

**What is kept from it:** per-server identity in rows and the workspace header, expressed with a generated deterministic mark and colour rather than fetched artwork. See [`VISUAL-PAGE-SPECIFICATIONS-V2.md`](VISUAL-PAGE-SPECIFICATIONS-V2.md).

### Direction C — Technical operator workspace *(rejected as primary)*

Dense, highly structured, fleet-oriented, diagnostics-forward.

**Why it is weaker as the primary identity:**

- `[S][I]` It is the direction the product is *already accidentally drifting toward* — the Automation page is a bare column table over a void, and the user's report that the app "still resembles a developer or administrative utility" is precisely a description of unintentional Direction C. Choosing it deliberately would ratify the complaint.
- It fails the first-time host badly, and `AGENTS.md` puts "Make the beginner path obvious" above "Keep advanced control available" in the decision hierarchy `[R]`.

**What is kept from it:** the density model. C is correct that operators need real density; A′ delivers it as a **density tier applied to data surfaces** (console, tables, file lists, fleet views) rather than as the whole product's register.

### Direction A as briefed — "quiet" native premium *(superseded by A′)*

Rejected only in emphasis. `[L]` Quietness is the current state and the measured cause of the problem. A′ is A with the depth and weight axes turned up to where they are perceptible.

## What this direction explicitly is not

- Not glass, blur-everywhere, or neon.
- Not an imitation of Apple, Discord, Prism Launcher, Steam, Xbox, or any hosting panel. `[R: AGENTS.md]` "Never copy competitor source, assets, wording, or branding."
- Not a "designed-looking" interface. Ornament that does not carry information is prohibited.
- Not a density increase for its own sake. Empty space is legitimate when it is *composed*; it is only a defect when it is *leftover*.

## Optional Windows backdrop

`[D]` **Recommended: Mica on the window root only, off by default in 1.x, behind an appearance setting.**

Conditions, all of which must hold:

- Applied to the window backdrop beneath the sidebar and page canvas only.
- **Never** behind console, tables, file lists or any dense data surface — those keep an opaque `AppSurfaceSunken`.
- Requires a solid fallback on Windows 10 and on failure. `[R: AGENTS.md]` Windows 10 support is required.
- Automatically disabled under High Contrast, Reduced Motion/transparency preferences, and battery saver.
- `[U]` Must be measured for idle CPU and composition cost before it ships enabled.

`[I]` Mica is a genuine "premium native Windows" cue, but it is a refinement, not a fix — it would do nothing for the surface-contrast problem, and shipping it before RC-1 is addressed would be decoration over a defect. It is deliberately scheduled late.

## Restrained gradient policy

`[D]` Gradients are permitted in exactly three places and nowhere else:

1. The brand mark and its application-icon renderings.
2. The primary button, as a ≤6% luminance vertical gradient — enough to give the one loud element a physical quality.
3. Large empty-state / first-run compositions, as a very low-contrast radial wash anchored to the brand mark.

Everything else is flat. `[I]` This keeps the "premium" cue concentrated on the moments that carry brand meaning, and keeps large scrolling surfaces cheap to render.

## Success criteria

The direction is working when all of the following are true:

1. `[U]` A screenshot of a populated Servers page is recognisably ChunkPilot with the wordmark cropped out.
2. Every card boundary is visible without hunting for it, measured at ΔL\* ≥ 12 between the stroke and its own fill.
3. Hovering any interactive row produces a change the user notices without being told to look.
4. No page ends in more than one screen-height of undesigned empty space at 1920×1080.
5. The taskbar icon is the same apparent size as its neighbours.
6. The application still starts, navigates and scrolls at least as fast as `02ec1bf`, with no material working-set increase.
7. High Contrast and Reduced Motion remain fully coherent.

Criteria 2, 4, 5 and 6 are measurable and are gates in [`VISUAL-ACCEPTANCE-CHECKLIST.md`](VISUAL-ACCEPTANCE-CHECKLIST.md). Criteria 1 and 3 are judgement calls made against a runtime screenshot.
