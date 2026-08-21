# UI copy and state guide

ChunkPilot uses plain language first and technical details under **Advanced** or **More details**.

Every async surface distinguishes `Loading`, `Ready`, `Busy`, `Unavailable`, `Unknown`, `Failed`, and `Complete`. Copy must say what was confirmed, what is not known, and what the user can do next.

Preferred patterns:
- “Server is running” when lifecycle state is confirmed.
- “Reachability is unknown” when only a local check exists.
- “No backups yet” with a real backup action, never a fake timeline.
- “Update staged; current version is unchanged” until activation and validation complete.
- “Recovery point required” before a destructive or risky change.

Never claim public accessibility, compatibility, player counts, TPS, update identity, or successful activation without evidence. Error copy includes a safe next action and preserves technical details for diagnostics.

## Voice

Product interface, not a narrator. Labels are nouns; values are states.

| Instead of | Write |
| --- | --- |
| You will see this name in ChunkPilot, and it becomes the server's folder name. | Used as the server name and the default folder name. |
| Who can reach it — Nobody yet. Public access is not set up. | Public access — Not configured |
| Stopped. You choose when to start it | Initial state — Stopped |
| What you have / Java it will use | Summary / Runtime |
| Finished release · Java 25 · 16 June 2026 | Released June 16, 2026 · Requires Java 25 |
| Save atomically | Save changes |
| Required by Mojang to run a Minecraft server. Creation is blocked until it is accepted. Opening the document does not accept it. | Acceptance required. / Accept the Minecraft EULA |
| Gameplay presets — NormalSurvival, TechnicalVanilla | Game rules — Keep items on death, Random tick speed |
| Min RAM (MB) | Minimum memory (GB) |
| 0 / 10 online, above a list of players who are not online | 3 players online · 10 slots |

Rules:

- One fact, one place. If a value is on the review rows it is not also in a paragraph above them.
- Second person only where a direct instruction or a legal choice genuinely needs it — the EULA control, an action label.
- No repeated reassurance, and no sentence whose only content is that ChunkPilot is being careful.
- Section titles are subjects (`Server`, `Runtime`, `Location`, `Access`, `Agreement`), never questions or narration.
- Provenance, hashes, URLs, operation identifiers and full paths that are not the primary fact belong under a **Technical details** disclosure. Safety warnings, consent and blocking errors never do.
- A value that was never established says so (`Not established`, `Not published`, `Unknown`) rather than rendering blank.
- Dates are localized and written naturally (`June 16, 2026`), and a missing date shortens the line rather than leaving a separator with nothing after it.
- A placeholder is a hint, never a value. It names the field (`Server name`) or gives an instruction
  (`Type a command`); it is never an example somebody has to clear, and it disappears on focus so the
  only thing at the insertion point is the caret.
- Implementation vocabulary is not a label. Atomic writes, transactions, journals and manifests are how
  ChunkPilot works; the button says what the user is doing.
- Units are the ones people use. Memory is GB in the interface and MiB in the launch arguments, with the
  exact figures shown as detail.

## Player access

Six facts, never conflated: who is **online**, who is **known**, who is **whitelisted**, who is an
**operator**, who is **banned**, and how many **slots** the server has. The online count and the slot
count are separate statements, because a list of known players under "0 / 10 online" contradicts itself.

Status is a word before it is a colour: `Online`, `Offline · seen 8/29 8:00 PM`, `Banned`,
`Cannot join` for a player the whitelist excludes.

Every row's state comes from the Agent, which reads the server's own files and its own console output.
A switch reflects the server, not the click: it is disabled while the change is in flight, reverted with
the server's own wording if the change is refused, and re-read from authoritative state on success. A
moderation control on a stopped server is disabled, because every one of those actions is a console
command.

Banning asks first — it disconnects the player. Pardoning, whitelisting and operator changes apply
straight away and are reversible in one gesture.

## Game rules

Values are read from the running server and labelled with where they came from (`Reported by the
server`, `Queued for the next start`, `Not read from the server`). A stopped server gets a sentence
saying when the controls become available, never a switch showing Vanilla's documented default: that
would be a guess about somebody's world.
