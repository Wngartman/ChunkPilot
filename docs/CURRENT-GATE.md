# Current ChunkPilot Gate

## 1.0 release candidate — September 14, 2026

Branch: `codex/release-readiness-2026-09-14`. Product version **1.0.0**; Windows numeric version
**1.3.1** preserves upgrade order from previously distributed 1.3.0-alpha installers. Database schema
**6** and creation journal shape **2** are unchanged. Starting source:
`66e5f2aeb67b9b0f04ce58103e809447c5257089`. The earlier recovery package remains at
`artifacts/recovery/modpack-controls-base-005b9d9`.

This source-freeze record is not universal external certification. The manual **Release** workflow
records the exact commit, completed build/test gates, installer upgrade results, artifact hashes and
publication status. A failed gate prevents that workflow from publishing.

## Delivered

- Exact official CurseForge server-pack relationships, typed generated content, manifest pin ordering,
  author distribution checks and a durable bounded payload ledger.
- Native personal CurseForge key setup: official validate-before-replace, Windows DPAPI protection,
  native session and originating-pipe cancellation fences, removal that preserves installed servers.
  The renderer receives no plaintext or encrypted credential; no shared key is bundled.
- Serialized deliberate bind apply/restart, truthful connection summaries, stopped-player access,
  repeatable icon editing, clearer startup progress and installed-pack identity.
- Strict backup inventory/server identity checks, staged restore rollback, reparse protection,
  newest verified recovery retention, verified-stop requirements for stopped data mutations.
- Atomic per-attempt Windows Job ownership for new server processes and automation children,
  exact legacy reattachment, bounded status reads and previous-attempt cleanup.
- Fresh same-attempt empty-player shutdown checks, stale confirmation fences, bounded roster
  rendering, thumbnail decoding and network concurrency.

## Observed checks before the immutable package gate

- Initial combined run: **1,827 unit passed / 1 failed**, **452 integration passed / 1 failed**.
  The image-cancellation fixture observed a disposable callback instead of the canceled operation;
  it was corrected. The file-symbolic-link fixture lacked Windows privilege and now reports an
  explicit capability skip; real directory-junction tests pass. Initial failures are not counted
  as passes without subsequent execution.
- Process-focused integration: **55 passed / 1 failed**, followed by **7/7 passed** after correcting
  traversal into a proven-foreign reused-PID branch. All 44 selected ManagedServer cases passed,
  including Job orphan cleanup, binding restart and running backup.
- Backup-focused: **25 passed / 1 unavailable skip**. Eight reproduced defects and the additional
  ancestor-destination empty-backup defect passed after correction.
- Final frontend source: **250/250 tests in 39 files**, typecheck, lint and production build passed.
- Credential/process unit: **25/25 passed** (7 service, 4 native dialog, 14 status/ownership).
  Real named-pipe cancellation integration: **4/4 passed**.
- The next complete integration run passed **463 / 0 failed / 1 privilege skip**. Its two unit
  failures (obsolete alpha-only README assertion and animated PNG metadata identification) were fixed;
  the corrected full unit run passed **1,845/1,845**. New generic stopped-data guard checks passed
  **8/8**, including persisted identity and actual owned TCP/UDP listeners.
- Targeted native compilation had zero warnings/errors. Final all-source tests, exact-package visual
  fixtures, portable smoke and disposable-runner installer upgrades remain mandatory release gates.

Logs/TRX live under ignored `logs/`. Package manifests bind artifacts to committed inputs; an older
`dev-current` does not certify newer source. No normal database or personal server was a test fixture.

## Live provider evidence

The new native credential service passed a live official API validation in disposable protected
storage, followed by exact StaTech metadata retrieval. Both session checks ran and the stored key
was readable through the production DPAPI store. This downloaded no pack payload, started no Java,
created no world, and did not exercise the native dialog end-to-end. Three diagnostic regressions pass.

Official StaTech **1605376 / 8699911 / 8699918** is authenticated. Client and server archives passed
native download/hash and preflight: **475,063,831 bytes**, 2,775 server files, Minecraft 1.21.1 /
NeoForge 21.1.248. Server SHA-256:
`696aa67e9dc88af3aa2f135b71fd781a880f4ef5c1345c1ce46836aebfb322c5`.
This is **archive/plan verification**, not a new game-server launch or player join.

AE2 Blackout **1060372 / 8393438** is authenticated as a resource pack. Generated Fabulously Optimized
**396246 / 8217274** correctly stops at dependency **697845 / 5424446**, whose author disallows
distribution. No required content was omitted or restriction bypassed. Phase payload ledger:
**475,405,117 bytes**, below the retained 2 GiB ceiling.

Earlier ATM10 acceptance is separate: **925200 / 8764211 / 8764245**, Minecraft 1.21.1 /
NeoForge 21.1.249. Installation, promotion, second launch, local status and clean stop passed at
`5a82ac82a392bdd33f61e85256b44d446d13afdd`; a subsequent owner join was reported. The retained world
was not adopted, launched or modified here. That earlier run does not independently certify this freeze.

## Remaining boundaries

- Every router, Starlink network, antivirus, Windows version and low-end PC is **not** physically
  certified. Router/firewall configuration is not outside-in evidence. CGNAT may require an external
  tunnel/private network or an ISP public address; there is no universal bypass.
- Unsigned builds may trigger SmartScreen. No source-license grant was selected; public visibility
  alone is not an open-source license.
- CurseForge requires each user's own approved API access and honors author restrictions. Shared-key
  distribution authorization is not claimed.
- One local file-symbolic-link test is unavailable without Windows privilege. The development machine
  is not reconfigured to enable it; the hosted disposable Windows gate reports its own capability.
- Some historical versions lack official server artifacts and need original user-supplied files.
- Backup restore is an overlay, not indiscriminate deletion of files missing from an archive.
  Unexpected activation termination retains recovery evidence. Path checks do not claim to eliminate
  every hostile concurrent filesystem race.

See [backup safety](operations/BACKUP-SAFETY.md), [providers](features/PROVIDERS.md),
[credential delivery](architecture/CURSEFORGE-CREDENTIAL-DELIVERY.md),
[releasing](release/RELEASING.md) and [bug register](troubleshooting/BUG-REGISTER.md).
