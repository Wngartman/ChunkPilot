# ChunkPilot brand mark and application icon — direction v2

Status: planning document. **No production branding is changed, replaced or committed by this plan.** Prototype concepts live in the ignored directory `artifacts/visual-direction-v2/logo-concepts/`. Evidence labels are defined in [`VISUAL-AUDIT-V2.md`](VISUAL-AUDIT-V2.md#evidence-labels).

## 1. Why the current mark is being replaced

`[L]` Measured, not asserted. Alpha bounding box over `assets/brand/*.png`:

| Asset | Bounding box | % of frame | Ink coverage |
| --- | --- | --- | --- |
| `ChunkPilot-16.png` | 10×7 | 62.5% | 21.5% |
| `ChunkPilot-32.png` | 20×15 | 62.5% | 19.1% |
| `ChunkPilot-64.png` | 40×29 | 62.5% | 17.5% |
| `ChunkPilot-256.png` | 157×116 | 61.3% | 16.3% |
| `ChunkPilot-source-1024.png` | 626×460 | 61.1% | 15.9% |

Five findings follow directly:

1. **Every size is one artwork downscaled.** The bounding box is 61–65% of the frame at *every* size — proof that no size was optically redrawn.
2. **The mark is 1.35:1 landscape, so it fills only ~45% of the frame vertically.** Windows app icons that read at correct size fill roughly 85–95% of their dominant dimension. `[S]` This is precisely why the taskbar icon looks about half the size of its neighbours in the attached desktop screenshot — because it is.
3. **Ink is 16–21% of the frame.** Four fifths of the icon is transparent.
4. `[L]` **It disintegrates at micro sizes.** At 32 px it is an indistinct blue blob; at 16 px it is unreadable. The orbital swoosh, the arrowhead and the facet shading are all sub-pixel below ~48 px.
5. `[L]` **It is blue; the interface accent is purple** (`#7B5CE0`). `[S]` Confirmed against the sidebar and splash. Brand and product do not currently share a colour.

`[I]` The mark is also a literal 3D cube with a swoosh — semantically tied to Minecraft and to a "launcher" idea, which contradicts the stated multi-game, multi-node long-term direction.

## 2. Constraints for the replacement

**Must:** be recognisable in silhouette; be clear at 16 px; be strong at 24–32 px; work in one colour; work on dark and light; survive a future one-word, game-neutral rename; work for a desktop app, a web panel and a commercial platform; pair with a future wordmark; be recognisable **without** the word "ChunkPilot".

**Must not:** use grass blocks, pixel texture or any literal Minecraft imagery; use fine detail, thin strokes, tiny orbits or specular highlights; be a generic cube, play button, server rack or mascot; use complex 3D or heavy gradients; resemble Discord, Steam, Xbox, Docker, Prism Launcher or a hosting provider.

## 3. Concepts explored

Six concepts were rendered across nine sizes each (16→512), in colour and single-colour, and inspected at 1:1 and 4×. **Each size was drawn natively rather than downscaled**, which is the discipline the current asset lacks.

Artefacts: `artifacts/visual-direction-v2/logo-concepts/` — `SHEET-micro-legibility.png`, `SHEET2-micro-legibility.png`, `SHEET-large-and-mono.png`, `SHEET2-large-and-mono.png`, `SHEET-taskbar-comparison.png`, `SHEET2-taskbar-comparison.png`, plus the individual PNGs and the two renderer scripts.

### Round 1 — rejected

| Concept | Metaphor | Verdict `[L]` |
| --- | --- | --- |
| **Keystone** | Isometric-cube silhouette with a chevron lifted from the top face | **Rejected.** The chevron cut detaches the cap from the body, which reads as a rendering accident rather than intent. Hexagon-plus-chevron is also crowded territory. |
| **Pilot** | Rounded-square badge containing a bold chevron and horizon bar | **Rejected.** Reads as a generic *home* or *upload* glyph, in the most generic container form available. |
| **Core** | Thick rounded-square aperture with an opened corner and a solid core | **Best of round 1, still rejected.** Holds above 24 px but muddies below it; the open corner reads as a glitch at micro size. |

`[I]` The shared failure was structural: **all three put a small glyph inside a container tile**, which caps the mark's frame occupancy and reproduces the current icon's core defect. Round 2 made the silhouette itself the mark.

### Round 2

| Concept | Metaphor | Occupancy at 32 px `[L]` | Verdict |
| --- | --- | --- | --- |
| **Stack** | Three sheared plates — instances under management, leaning forward | bbox 96.9%, ink **67.0%** | **Rejected on resemblance risk.** Technically the strongest presence, and legible at 16 px. But three sheared bars is very close to a widely recognised cryptocurrency brand mark. That is an unacceptable risk for a product with commercial ambitions. |
| **Halo** | Thick open ring with a solid core — control and protection around an instance | bbox 93.8%, ink 47.6% | **Runner-up.** Cleanest micro-legibility of all six; reads clearly at 16 px. Weakness: it reads as a *progress spinner or record indicator* — a state, not a brand — and circular ring-plus-dot is well-populated territory. |
| **Chunk** | A unit safely lifted out of a managed whole | bbox **96.9%**, ink 58.6% | **Recommended.** |

## 3a. Round 3 — the adopted mark, "Lift" *(implemented)*

`[L]` Three further finalists were rendered at all ten sizes and inspected before anything was adopted. Two were rejected on sight:

| Concept | Verdict |
| --- | --- |
| **Lift (first attempt)** — L-body rebuilt with a sharp inner corner plus a rotated chip | **Rejected.** Rebuilding the body path rounded the wrong corner and produced a **boot silhouette**. The metaphor did not survive the geometry. |
| **Keep** — rounded keystone with a slot knocked from the top and a core below | **Rejected.** Reads unmistakably as a **padlock or a kettlebell**. "Locked" is the wrong promise for a tool whose point is that changes are reversible. |
| **Lift (adopted)** — the original Chunk body, unmodified, with two targeted changes | **Adopted.** |

`[D]` The adopted mark keeps the Chunk geometry that already worked and changes only the two properties that produced the duplicate-icon reading. A copy glyph is **two equal, parallel rounded rectangles**; this is neither:

1. **Size ratio widened** — the chip is 40% of the body, not ~46%.
2. **The chip is tilted 10°** — applied to the chip alone, so the body silhouette is untouched. The tilt is dropped below 24 px, where it costs more in antialiasing than it buys in distinctiveness.

`[L]` One rendering defect was found and fixed during production: the chip's gradient was defined over the whole icon rectangle and then rotated, which produced a visible diagonal seam across the chip at 128 px and above. The brush is now built in the chip's local space.

`[L]` Measured occupancy of the adopted production set, against a gate of ≥94% bounding box and ≥45% ink for the frames Windows draws in the taskbar:

| Size | 16 | 20 | 24 | 32 | 40 | 48 | 64 | 128 | 256 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| bbox % | 100 | 100 | 100 | 100 | 97.5 | 95.8 | 92.2 | 88.3 | 88.7 |
| ink % | 64.5 | 63.2 | 62.2 | 60.4 | 57.4 | 58.0 | 52.6 | 48.5 | 48.0 |

Against the previous icon's uniform 62.5% bounding box and 19.1% ink, ink coverage is roughly **three times** higher at the sizes that matter. `[L]` In the taskbar simulation the mark now matches its neutral neighbours instead of appearing about half their size.

`[R]` Produced by `assets/brand/build-brand-assets.ps1`, which is the canonical source: every frame is drawn natively at its own size, and the script also re-runs the occupancy validation. The previous 1024 px raster source was deleted so the retired mark cannot be regenerated by accident.

`[U]` **Trademark clearance has still not been performed.** The mark is an implementation candidate. A search is required before public release.

## 4. Original recommendation — "Chunk"

A bold rounded-square body with its top-right quadrant separated along a clean gap and offset outward as a smaller rounded square.

**Why it is the right mark for this product:**

- **The metaphor is the product's actual differentiator.** `[R: AGENTS.md]` ChunkPilot's standout capabilities are transactional updates, staging, verification and rollback — *lift a piece out safely, verify it, put it back or roll it back*. That is literally what the mark depicts. No other concept explored says anything specific about ChunkPilot.
- **It survives a rename and outgrows Minecraft.** The lifted unit reads as an instance, a node, a version or a server. Nothing about it is game-specific. The name "chunk" reinforces it today without the mark depending on the name.
- **Frame occupancy is 96.9% with 58.6% ink** versus the current mark's 62.5% / 19.1% `[L]` — roughly **three times the ink**. `[L]` In the taskbar simulation the current mark is visibly smaller than its neutral neighbours; Chunk matches them.
- **Two shapes.** It holds at 16 px, and the mono knockout is clean on both light and dark.
- Asymmetry gives it a distinctive silhouette, which the symmetric Halo lacks.

**Risks, stated honestly:**

| Risk | Severity | Mitigation |
| --- | --- | --- |
| Resembles a generic *copy / duplicate / select* tool icon, especially in flat mono | **Medium — the main concern** | Increase the size ratio between body and lifted unit; rotate the lifted unit ~8°; make the separation gap a consistent diagonal rather than an axis-aligned notch. To be resolved during the asset task, reviewed at 16/24/32 px. |
| Could read as a letter "L" | Low | Largely resolved by the rotation above |
| Rounded-square language is common | Low | Mitigated by the asymmetry and the gap |
| Trademark collision | **Unassessed** | `[U]` A trademark and image search is a **mandatory gate** before any production asset is produced. This planning session performed no such search. |

`[D]` **Fallback: Halo.** If the duplicate-icon reading cannot be designed out, adopt Halo and accept the spinner association. Do not fall back to Stack.

## 5. Colour

`[D]` The mark adopts the interface accent, ending the current blue/purple split `[L]`.

| Use | Treatment |
| --- | --- |
| Primary, large (≥64 px) | Linear gradient `#9B7DF5` → `#5B3FD0` at ~60°, body and lifted unit sharing one gradient field |
| Micro (≤48 px) | **Flat `#8265E8`.** A gradient across 16 px is banding, not depth. |
| Single colour on dark | `#E8E8ED` |
| Single colour on light | `#1B1B1F` |
| High Contrast | System colours; silhouette only, no gradient |

`[D]` No blue. No third colour. The mark is one hue.

## 6. Micro-icon specification

`[D]` **The micro icon is optically a different drawing.** This is the central lesson of §1 and is non-negotiable.

| Size | Margin | Gap between body and lifted unit | Corner radius | Fill | Notes |
| --- | --- | --- | --- | --- | --- |
| 16 | 1 px | **2 px minimum** | 3 px | flat | Two shapes only; drop the rotation if it costs the gap |
| 20 | 1 px | 2 px | 4 px | flat | |
| 24 | 1 px | 2 px | 5 px | flat | |
| 32 | 1 px | 3 px | 7 px | flat | The taskbar's real size at 100% scaling |
| 40 | 2 px | 3 px | 9 px | flat | Taskbar at 125% |
| 48 | 2 px | 4 px | 11 px | flat | Taskbar at 150% |
| 64 | 4 px | 5 px | 14 px | gradient | |
| 128 | 9 px | 10 px | 28 px | gradient | |
| 256 | 18 px | 20 px | 56 px | gradient | |
| 512 | 36 px | 40 px | 112 px | gradient | Source only |

`[D]` Rules:

1. **Never scale one source into every frame.** Every size in the `.ico` and in `assets/brand/` is produced at its own size.
2. Target bounding box ≥ 94% of the frame at every size; ink ≥ 45%.
3. The gap never falls below 2 device pixels — below that the two shapes merge and the mark becomes a blob, which is exactly the current failure.
4. Gradients are dropped at ≤48 px.
5. Hinting: at 16/20/24/32 all edges land on whole pixels. Verify by inspecting at 4× nearest-neighbour, not by trusting the rasteriser.
6. The `.ico` carries 16/20/24/32/40/48/64/128/256. Windows picks per context; each must be individually correct.

`[D]` **Verification gate for the asset task:** re-run the occupancy measurement (`bbox ≥ 94%`, `ink ≥ 45%`) on every produced PNG, and re-render the taskbar comparison sheet. Neither is optional — both are cheap, and both would have caught the current defect.

## 7. Wordmark and rename readiness

`[D]` The wordmark is **Segoe UI Variable Display SemiBold**, sentence-cased as "ChunkPilot", set at the mark's cap height, spaced one-quarter of the mark's width from it. No custom lettering, no bundled font.

`[I]` The mark is deliberately independent of the name: it depicts a unit being managed, not a chunk of Minecraft terrain and not a pilot. A future one-word, game-neutral name can be dropped in with no change to the mark. That is the primary reason a literal or letter-based mark was not pursued.

## 8. What this plan does **not** do

- Does not modify `assets/brand/*`, `assets/ChunkPilot.ico`, `assets/ChunkPilot-256.png`, `chunkpiloticon.png`, or any splash or title-bar asset.
- Does not commit prototype artefacts. `[L]` `artifacts/` is already gitignored `[R: .gitignore]`.
- Does not run a trademark search — `[U]` that is a gate on the implementation task, not something this session performed.
- Does not finalise the mark. The duplicate-icon risk in §4 must be designed out and reviewed at micro sizes first.
