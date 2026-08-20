# Create Server v2 risk register

Status: planning document. Companion documents: `CREATE-SERVER-V2-ARCHITECTURE.md`, `CREATE-SERVER-V2-ROADMAP.md`, `CREATE-SERVER-V2-PRESET-INVENTORY.md`, `CREATE-SERVER-V2-PROVIDER-RESEARCH.md`.

Ranked highest-impact first. Each entry states current evidence, mitigation, and the roadmap phase where it must be addressed. Evidence key: **[V]** verified in repository, **[E]** current official external fact, **[I]** inference, **[U]** unknown/requires validation.

## 1. World or file loss

**Risk:** a defect in the new wizard causes an existing server's world/config to be overwritten, deleted, or silently migrated during creation (e.g. a destination-collision bug, or the Advanced/custom path pointing at an occupied directory).

**Evidence:** **[V]** `ManagedServerInstaller.cs` already stages into a unique, same-volume directory and promotes atomically; **[U]** destination-collision behavior specifically for a *second* create pointed at an already-used `InstanceRoot`/`ServerName` combination is unverified (architecture doc §6).

**Mitigation:** Task 4 explicitly verifies and, if needed, hardens collision rejection before any vertical slice is built on top of it. The Review screen must show the resolved absolute destination path and block `Create` on any detected collision — never silently rename or overwrite.

**Phase:** Roadmap Task 4 (must land before Task 5).

## 2. Partial/half-created server visible in the normal library

**Risk:** an interrupted creation (crash, forced close, agent restart) leaves a server registered in SQLite or visible in Dashboard/Servers in an ambiguous, half-staged state.

**Evidence:** **[V]** `docs/ARCHITECTURE.md`'s data-safety rule: "a failed/cancelled install is never registered" — this is an existing, stated invariant for the *update* transaction; **[U]** whether the *create* transaction's registration-after-promotion ordering is airtight against every interruption point (in particular: agent process death between `Directory.Move` succeeding and `ChunkPilotStore.UpsertServerAsync` completing) needs explicit test coverage, not just code-reading confidence.

**Mitigation:** **[V] Closed for the transaction itself.** Registration moved inside `ServerCreationTransaction`, which journals `Activated` and `Registered` as separate durable checkpoints and verifies the persisted record against the activated folder before reporting success. `ServerCreationRecoveryService`, run from `Program.cs` *before* `ServerSupervisor.InitializeAsync`, finishes or refuses each interrupted entry, so a half-created server is resolved before the library is ever read. `CreationPhasePolicy.MayAppearAsServer` states the rule the code enforces: only `Completed` and `CleanupPending` may appear.

**Phase:** Transaction hardening milestone (implemented and tested). Per-intent coverage remains Roadmap Task 17.

## 3. Provider API changes (Modrinth/CurseForge/Mojang/loader metadata)

**Risk:** an external provider changes its API shape, auth requirements, or terms between this planning session and implementation, silently breaking a "verified" integration or making a documented behavior non-compliant.

**Evidence:** **[E]** confirmed during this session: CurseForge is actively changing direct-download authentication requirements (API key for CDN downloads moving from optional to enforced — see provider research notes for the exact source and date found). This is concrete evidence the risk is not hypothetical.

**Mitigation:** Task 12 is explicitly a fresh-research task, not a code task, precisely because provider terms are time-sensitive; every vertical-slice task (5–9, 11, 13, 14) instructs the implementing session to verify current official documentation rather than trust this document's or the architecture document's cached citations.

**Phase:** Ongoing; explicitly gated at Task 12 for CurseForge, and implicitly re-verified at the start of every provider-touching vertical slice (5–9, 11, 13, 14).

## 4. Corrupt or tampered downloads

**Risk:** a downloaded server JAR/loader installer/modpack archive is corrupted in transit or (lower likelihood, still real) tampered with, and is installed anyway.

**Evidence:** **[V]** hash verification already exists end-to-end for provider-supplied hashes (`ExpectedSha1/256/512` on `ServerInstallRequest`, verified in `ManagedServerInstaller.cs`); **[I]** the strength of verification is only as good as what each provider actually supplies — CurseForge's search API response does not appear (from the code read in this session) to carry a file hash the same way Modrinth's does; this needs explicit confirmation per-provider during implementation, not assumed uniform.

**Mitigation:** each vertical-slice task must confirm what hash strength its provider actually supplies and never claim `VerifiedCompatible`/verified-integrity in the UI beyond what the evidence supports (architecture doc §3's `CompatibilityEvidence.HashAlgorithm`/`HashValue` fields exist precisely to make this honest per-item rather than assumed globally).

**Phase:** Every provider-touching vertical slice (5–9, 11, 13, 14).

## 5. Runtime (Java) mismatch

**Risk:** a server is created with a Java runtime that cannot actually run the selected Minecraft/loader version, discovered only at first launch rather than at creation time.

**Evidence:** **[V]** `JavaRuntimePolicy.RequiredMajorForMinecraft` and `ManagedJavaRuntimeService` already resolve this correctly and are covered by existing integration tests (`Managed_Java_fixture_is_verified_healthy_private_and_does_not_change_environment`).

**Mitigation:** no new mitigation needed for the managed-Java path; the residual risk is entirely in the **Advanced/custom path's user-supplied Java**, where architecture doc §10 already specifies that any Advanced Java override demotes compatibility evidence to `Unknown` with an explicit warning rather than a false assurance.

**Phase:** Roadmap Task 15 (Advanced/custom).

## 6. Loader incompatibility (Fabric/Forge/NeoForge/Quilt version mismatches)

**Risk:** a chosen loader version does not actually support the chosen Minecraft version, or the loader's installer behavior changes between what `LoaderMetadataService` expects and what the loader's official Maven/metadata endpoint currently returns.

**Evidence:** **[V]** `LoaderMetadataService` already resolves all four loaders against official metadata (`GuidedPlatformServices.cs:1017-1208`); **[E]** loader installer mechanics are independently verified as still current for NeoForge in this session (official `docs.neoforged.net` steps match what `ManagedServerInstaller`/`LoaderInstallationService` is documented to do — non-detaching installer profile, captured output).

**Mitigation:** each loader's vertical-slice task (7, 8, 9) is required to re-verify current official metadata/installer behavior at implementation time, not trust this document.

**Phase:** Roadmap Tasks 7, 8, 9.

## 7. Modpack server-pack suitability (assuming every client pack has a usable server package)

**Risk:** the wizard implies or allows installing a modpack that has no real dedicated-server package, or silently "converts" a client pack.

**Evidence:** **[V]** `CurseForgeCatalogProvider` already computes `InstallationSupport = ClientOnly` when no `serverPackFileId` exists; `CatalogQuery.ServerPackRequired`/`ExcludeClientOnly` already filter these out by default in `CatalogPolicy.Filter`.

**Mitigation:** architecture doc §9's explicit rule: client-only modpacks are shown as blocked with a truthful explanation, never a fake conversion action. This is a **hard requirement**, called out twice by the assignment (once generally, once specifically for modpacks) — treat any UI affordance suggesting conversion as a defect, not a feature request, if one is ever proposed during implementation.

**Phase:** Roadmap Tasks 10, 11, 13.

## 8. EULA mishandling

**Risk:** the server becomes active/runnable before genuine, explicit EULA acceptance, or acceptance is recorded without a real user action.

**Evidence:** **[V]** already correctly gated in `ManagedServerInstaller.cs` (writes `eula.txt` only when `EulaAccepted && EulaAcceptedAt` are both present, re-verifies the file's content before activation). This is existing, tested, compliant behavior.

**Mitigation:** the wizard's only obligation is to not auto-check the box and to not allow `Create` while unchecked — both are direct carry-forwards of `InstallServerWindow.xaml`'s existing correct binding. Low residual risk given the backend guarantee already exists independent of the UI.

**Phase:** Roadmap Task 5 (first path to exercise EULA in the new wizard); verified again per-intent implicitly since every intent shares the same Review/EULA screen.

## 9. Cancellation leaving orphaned state

**Risk:** cancelling mid-creation leaves a staging directory, a partially-downloaded file, or an inconsistent database row.

**Evidence:** **[V]** `CancelInstall`/`operationId` mechanism exists and is exercised by the current UI (`InstallServerViewModel.CancelAsync`); **[U]** whether cleanup after cancellation is verified for every `InstallState` (not just the ones the current single vertical path happens to exercise) is unconfirmed.

**Mitigation:** **[V] Closed for the transaction itself.** `CreationPhasePolicy.CanCancelSafely` names the phases where cancelling is free, and `IsCriticalSection` names the two where it is not. A cancellation before `Activating` removes only operation-owned staging and deletes the journal; one that arrives during promotion or persistence is recorded and the operation runs to a consistent end, reported truthfully rather than as an immediate stop. Repeated cancellation is idempotent. Covered by the cancellation tests in `ServerCreationTransactionIntegrationTests`.

**Phase:** Transaction hardening milestone (implemented and tested). Per-intent coverage remains Roadmap Task 17.

## 10. Restart during activation (the atomic-move window)

**Risk:** the Windows process, agent, or machine restarts during the non-cancellable `Finalizing`/`Directory.Move` window, leaving the destination in an indeterminate state.

**Evidence:** **[V]** the sibling update-transaction has documented, tested recovery for exactly this class of interruption (`docs/ARCHITECTURE.md` "Server-pack update transaction" — retained sibling directory, operation journal, agent-startup recovery). **[U]** whether the *create* path (which has no "old version to fall back to," unlike an update) has equivalent recovery is the single most important open question in this entire risk register.

**Mitigation:** **[V] Answered.** The create path has no old version to fall back to, so recovery relies on an ownership marker written into the candidate before promotion plus the journal's separate `ActivationBegan`/`ActivationCompleted` flags. After a restart, `ServerCreationRecoveryService` reads the marker rather than inferring from directory existence: a destination carrying this operation's marker is treated as promoted and the creation is finished; a marker still in staging means promotion never happened and staging is discarded; a destination with no matching marker is never touched and is reported as needing attention. Recovery is bounded to three attempts so a permanently unhappy entry cannot retry on every start.

**Phase:** Transaction hardening milestone (implemented and tested at every durable checkpoint, including a runtime smoke that launches the real Agent). Per-intent coverage remains Roadmap Task 17.

## 11. Persistence/schema mismatch

**Risk:** the new wizard needs a database field that doesn't exist, and an ad hoc schema change is made without following the existing additive-migration discipline.

**Evidence:** **[V]** schema is at v4, additive-only, `CREATE TABLE IF NOT EXISTS` throughout (`docs/ARCHITECTURE.md`, `docs/DATABASE-MIGRATIONS.md`).

**Mitigation:** the architecture document explicitly scopes v2 to **not** require new schema for the wizard itself (§0, §11) — `ServerInstallRequest`/`ChunkPilotStore.UpsertServerAsync` already cover registration. If a genuine new persistence need emerges (e.g. recording which `CreationIntent` produced a server, for support diagnostics), it must follow the existing additive-migration pattern and be called out as its own reviewable change, not folded silently into a vertical-slice task.

**Phase:** **[V] One additive change made.** The transaction hardening milestone added a `creation_journal` table and advanced `PRAGMA user_version` from 4 to 5. It follows the existing discipline exactly: `CREATE TABLE IF NOT EXISTS`, no existing table altered, no data migrated, and an older database gains the table on first open. The three migration tests that assert the current schema version were updated. No further schema change is planned.

## 12. UI state divergence across intents (the "oversized ViewModel" failure mode recurring)

**Risk:** despite the split-by-responsibility design (architecture doc §2.2), intent-specific ViewModels accumulate cross-talk (e.g. a field set by the Mods intent leaking into the Modpack intent's resolved context after the user switches tabs).

**Evidence:** **[I]** this is exactly the class of bug the *current* `InstallServerViewModel.PresetAppliesToSource()` fragile-equality-check defect (preset-inventory doc §2.1) demonstrates already happened once in this codebase.

**Mitigation:** `CreationSelection`/`ResolvedCreationContext` are immutable records rebuilt on each transition, not mutated in place; switching intents constructs a fresh `CreationSelection` rather than patching the old one. Task 19's cross-intent smoke test exists specifically to catch this class of regression before release.

**Phase:** Roadmap Task 19; architecturally mitigated from Task 1 onward by using immutable records.

## 13. Security of downloaded artifacts — path traversal during archive extraction

**Risk:** a malicious or malformed ZIP/modpack archive writes outside the intended staging directory.

**Evidence:** **[V]** `docs/ARCHITECTURE.md`: "ZIP entries are normalized and rejected if they escape staging" — already implemented and, per the same document, applied uniformly across managed installation, crossplay packages, datapacks, and server-pack updates.

**Mitigation:** none new required; every vertical slice reuses the existing extraction code path — no task should introduce a second, parallel extraction implementation.

**Phase:** N/A (already mitigated); verify no vertical slice bypasses the shared extraction path.

## 14. Zip bombs

**Risk:** a catalog-sourced archive expands to an unreasonable, disk-exhausting size.

**Evidence:** **[U]** not confirmed in this session whether `ManagedServerInstaller`'s extraction path enforces a size/ratio limit distinct from path-traversal rejection. This is a real gap in verified evidence, not a confirmed defect.

**Mitigation:** Task 4's transaction-foundation review should explicitly check for (and, if absent, add) an extraction size ceiling and/or free-space pre-check (the sibling update transaction is documented to check "both snapshot/cache and server-volume free space" — confirm the create path does the same).

**Phase:** Roadmap Task 4.

## 15. Symlinks/reparse points inside archives

**Risk:** an archive entry is a symlink/junction pointing outside staging, used to escape the traversal check via indirection rather than a direct `../` path.

**Evidence:** **[U]** not confirmed whether the existing path-traversal rejection also rejects symlink entries specifically (a known bypass class for naive "reject `..` in the normalized path" checks). This is a real, specific, and currently unverified security question.

**Mitigation:** Task 4 must explicitly check this against the actual extraction code (not just the "path traversal" doc claim, which may or may not cover this specific vector) and add a rejection if missing, before any modpack/loader vertical slice ships.

**Phase:** Roadmap Task 4 — treat as a security-review item, not a routine code-read.

## 16. Secrets/credentials (CurseForge API key)

**Risk:** the key is logged, written to an unencrypted location, or exposed via a diagnostic bundle.

**Evidence:** **[V]** `docs/ARCHITECTURE.md`: "Provider secrets are kept outside SQLite in `%LOCALAPPDATA%\ChunkPilot\secrets.dat`... encrypted with DPAPI... never returned through the agent API"; `AGENTS.md`: "Secrets use Windows DPAPI and must be redacted from logs and diagnostic bundles." Both already stated as existing invariants.

**Mitigation:** Task 13's new Settings-page key-entry control must call into the existing `ISecretStore` exactly as `CurseForgeCatalogProvider`/`CurseForgeUpdateProvider.ApiKeyName` already expect — no new storage mechanism, no logging of the raw key value anywhere (including debug/trace logging), verified by a specific test in Task 13.

**Phase:** Roadmap Task 13.

## 17. Provider authentication (CurseForge terms compliance)

**Risk:** ChunkPilot's use of the CurseForge API drifts out of compliance with the 3rd-party API terms (e.g. by bundling a shared key, which the terms prohibit disclosing to third parties).

**Evidence:** **[E]** CurseForge's 3rd-party API terms explicitly prohibit disclosing the API key to third parties and require ceasing use/destroying keys on termination (see provider research notes for source). **[V]** the existing implementation already requires a *user-supplied* key, never a bundled one — this is the compliant design.

**Mitigation:** Task 12 exists specifically to re-confirm this remains compliant at implementation time, since terms can change.

**Phase:** Roadmap Task 12.

## 18. Dependency/licensing (new packages)

**Risk:** implementation introduces a new NuGet dependency (e.g. an image-loading library) with an incompatible license or unnecessary footprint, conflicting with `AGENTS.md`'s "native and lightweight" principle.

**Evidence:** **[I]** the one plausible new small dependency identified in this planning pass is an image-loading/caching helper for modpack icons (architecture doc §9) — WPF's built-in `BitmapImage`/`Image` with a manual `HttpClient` fetch and a simple on-disk cache is almost certainly sufficient and avoids any new dependency at all.

**Mitigation:** default to zero new dependencies; if a vertical-slice task believes one is genuinely needed, that decision must be surfaced explicitly in that task's commit message and reviewed, not added silently.

**Phase:** Roadmap Task 10 (where the icon-loading need first arises).

## 19. Support burden (six intents, four loaders, two modpack providers, crossplay, advanced)

**Risk:** the surface area shipped is large enough that support/maintenance cost outpaces the value delivered, or that partial completion (e.g. three of four loaders) ships in a confusing intermediate state.

**Evidence:** **[I]** the roadmap's vertical-slice structure means each intent/loader is independently shippable and testable, but the assignment's "avoid a flag-day rewrite" preference means the legacy window and the new wizard could theoretically coexist for a while — the roadmap instead cuts over once Vanilla+Plugins+Advanced work (Task 16), accepting a short window where Mods/Modpack/Crossplay are still catching up, rather than maintaining two Create Server entry points simultaneously (which would itself be a support and confusion burden).

**Mitigation:** Task 16's dependency list is deliberately minimal (5, 6, 15) so cut-over happens as early as safely possible; later intents land as pure additions to an already-shipped wizard, not as blockers to shipping anything.

**Phase:** Roadmap Task 16 (cut-over timing is itself the mitigation).

## 20. Scope expansion (this planning task's own risk)

**Risk:** the roadmap or architecture quietly grows to include work explicitly out of scope (Console/Manage/Access/Protection redesign, Automation/Activity/Settings redesign, provider behavior changes, networking, backups, updates, installer changes).

**Evidence:** **[V]** re-read against the assignment's explicit "do not redesign"/"do not modify" lists before finalizing this document; no task above touches any of those systems, and every task that touches shared architecture (Task 4, Task 16) is scoped to the smallest verified gap, not a broader refactor.

**Mitigation:** every roadmap task carries an explicit "Non-goals" line for exactly this reason; the completion report for this planning session states plainly that no implementation occurred.

**Phase:** Enforced throughout; re-check at Task 19 (release hardening) as the final gate.

## Status after the Vanilla vertical slice (Vanilla only)

The transaction-hardening milestone and the Vanilla vertical slice have closed or narrowed several of the entries above for the Vanilla path only. Every other intent remains exactly as ranked.

- **2. Partial/half-created server visible in the normal library** — closed for Vanilla. Registration happens inside the transaction, is verified by reading it back, and `CreationPhasePolicy.MayAppearAsServer` allows a server to appear only after verification passed. An interrupted attempt is reconciled at Agent startup before the server list is read. Covered by deterministic interruption and double-recovery tests.
- **4. Corrupt or tampered downloads** — narrowed for Vanilla. Mojang's published SHA-1 is verified, and the hash the user saw on the review screen is carried into the download and re-checked, so an artifact that changed between review and download is refused rather than installed. A version Mojang publishes no checksum for says so on the review screen instead of implying verification.
- **5. Runtime (Java) mismatch** — narrowed for Vanilla. The requirement is read from official `javaVersion.majorVersion` metadata first and only falls back to ChunkPilot's version rules when the metadata is silent, with the source recorded and shown. A version whose requirement neither establishes is offered to nobody. Live validation on 2026-07-28 confirmed the current release requires Java 25, which the old rules would have inferred as 21.
- **8. EULA mishandling** — narrowed for Vanilla. The control starts unchecked every session, nothing but the user sets it, changing the plan withdraws it, and the acceptance moment and official address are recorded without the legal text. The Agent refuses an unaccepted plan before anything is downloaded, `eula.txt` is written only inside operation-owned staging, and recovery now requires durable evidence in both the journal and the owned folder before it will finish a registration.
- **9. Cancellation leaving orphaned state** — narrowed for Vanilla. Cancelling is idempotent at both ends, is honoured immediately outside the critical section, and inside it says so rather than promising an instant stop. Closing the window cancels nothing; the Agent keeps the operation and a reopened wizard reattaches to it.
- **12. UI state divergence across intents** — not yet exercised. Only one intent is live, so the four-way state split (selection, catalogue, resolved, operation) has been proven for Vanilla alone. The failure mode this entry describes appears when the second intent lands.
