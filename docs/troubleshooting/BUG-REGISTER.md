# Bug register

Verified defects and limitations with evidence sufficient to track. A fix awaiting manual acceptance
may remain here as **Fixed locally** with its regression evidence; after acceptance, the commit and test
become the durable record and the entry can leave this active register.

---

## CP-2026-034 — Repository-local installer prerequisites fail on their default invocation

| Field | Value |
|---|---|
| Date | 2026-08-21 |
| Severity | Medium — a clean packaging machine cannot acquire the pinned local compiler or WebView2 bootstrapper with the documented command |
| Area | Release tooling / prerequisite acquisition |
| Status | **Fixed locally** — defaults are resolved after script binding and Inno's documented help exit is handled explicitly |
| Validation | Both scripts invoked without path overrides; compiler identity, pinned installer hash/signature, and Microsoft bootstrapper identity verified; distribution contracts |

PowerShell evaluates parameter defaults before `$PSScriptRoot` is available in this invocation path, so
both acquisition scripts attempted `Split-Path` on an empty value. A fresh Inno acquisition then treated
the compiler's expected nonzero help-banner exit as a terminating native error. Defaults now resolve from
the already established repository root inside the script body, and only help exit codes 0 or 1 are
accepted before normalizing the successful result.

---

## CP-2026-033 — Normal Internet setup waits on an automatic outside-in probe

| Field | Value |
|---|---|
| Date | 2026-08-21 |
| Severity | High — working owned setup can look incomplete and repeatedly depend on an optional external service |
| Area | WebUI / Internet sharing presentation |
| Status | **Fixed locally** — three-step owned-state setup; outside-in is Advanced and explicit |
| Validation | React running/stopped/setup/failure assertions, no-auto-request regression, native connectivity mapping, fixture review |

The ordinary Connectivity flow treated an external probe as step four, started it from a React effect,
and used its result as the durable setup label. Normal status now follows the exact Windows rule, router
mapping, and server lifecycle evidence. Optional outside-in testing remains available as a point-in-time
Advanced diagnostic and is still the only basis for the distinct **Connection confirmed** label.

---

## CP-2026-032 — Published shortcuts reference an icon file that is not packaged

| Field | Value |
|---|---|
| Date | 2026-08-21 |
| Severity | Medium — taskbar, window chrome and shortcuts can show generic or inconsistent identity |
| Area | WPF / publish / installer identity |
| Status | **Fixed locally** — one embedded and published multi-frame ICO plus stable AppUserModelID |
| Validation | Distribution contract, Release build, explicit post-publish copy, matching source/output SHA-256, nine ICO frames, associated executable icon, and successful installer compilation |

The executable embedded `assets/ChunkPilot.ico`, but the installer shortcut pointed at
`{app}\Assets\ChunkPilot.ico` even though the original item metadata did not actually copy that file. The desktop shortcut also lacked
the Start-menu AppUserModelID and the borderless WebUI window had no explicit icon. The project now copies
the same ICO through an explicit post-publish target, the process/window/shortcuts share `ChunkPilot.Desktop`, and no alternate icon was
introduced.

---

## CP-2026-031 — WebUI drops authoritative player UUIDs and cannot show player heads

| Field | Value |
|---|---|
| Date | 2026-08-21 |
| Severity | Medium — player identity is harder to distinguish and differs from the native player surface |
| Area | Player snapshot / native image bridge / WebUI |
| Status | **Fixed locally** — UUID-bound official Mojang skin path with bounded cache and local fallback |
| Validation | Official/cached/invalid-host/offline native tests; React rendered/fallback/selected-server tests |

The access model already carried authoritative UUIDs and the WPF control already knew the official Mojang
profile route, but `WebUiSnapshotMapper` omitted UUIDs. The renderer now requests a head only for a UUID in
the selected server's current rows. Native code allowlists Mojang hosts, bounds downloads/cache size,
composites the face and hat layers, and returns no broken URL on failure.

---

## CP-2026-030 — Server settings and MOTD draft can cross a rapid server selection

| Field | Value |
|---|---|
| Date | 2026-08-21 |
| Severity | Stop-the-line — a draft or late read can be shown or saved against the wrong server |
| Area | WebUI server settings / native async property load |
| Status | **Fixed locally** — immutable server identity on snapshots, remount boundary, stale-response guards |
| Validation | A-to-B dirty draft regression, late authoritative snapshot regression, native stale-load/build contracts |

`serverSettings` had no server ID, React retained one component state while `serverId` changed, and the
native property read applied its response without rechecking selection after `await`. Settings snapshots
now name their immutable owner, a server change remounts the editor behind the existing unsaved-change
guard, mismatched snapshots render unavailable rather than old values, and native late responses are
dropped before mutating selected state. Save requests keep the captured server ID and abort later memory
work if selection changed, so a newly selected server is never the fallback target.

---

## CP-2026-029 — Stale running evidence restarts a server after reboot and Stop can wait behind Start

| Field | Value |
|---|---|
| Date | 2026-08-20 |
| Severity | Stop-the-line — a server can start without persisted user policy and a manual Stop can appear stuck |
| Area | Agent startup reconciliation / serialized lifecycle / WebUI lifecycle completion |
| Affected | `v1.3.0-alpha.2` (`5a763e2f621b3273e312784969ceb6650bce3678`) and development commit `56c57ed27d2974c0c3540d320bf1b5f56503634c` |
| Status | **Fixed locally** — explicit startup authority, preemptive manual Stop, bounded gate wait and truthful completion |
| Fixed branches | `codex/fix-lifecycle-reboot-stop` (`52f143365065ac1f6efd6d63b5384bd93a856689`) and `codex/hotfix-alpha3-lifecycle` (`45262e0e6fc009f35296fa10cff2295315ec24f2`) |
| Validation | Isolated stale-reboot reproduction, explicit autostart/schedule starts, restart suppression, unresponsive/duplicate Stop, reconnect, exact process identity and owned-network cleanup tests |

Every successful ordinary start was persisted as `RestorePreviousRunningState`. On the next Agent launch,
`ServerSupervisor` treated that runtime observation as permission to start again, including stale
`CrashRecovery` and restart intent. This made a Windows reboot followed by App/Agent startup look like
an authorized autostart even when the server had no autostart setting or Start schedule.

Manual Stop also waited for the per-server operation gate before recording stop intent or cancelling the
operation that held the gate. A Start waiting for readiness (up to the configured startup timeout) or a
restart delay therefore kept Stop queued while the UI optimistically displayed `Stopping`. The fixed path
records manual-stop intent first, invalidates pending restart generations, cooperatively cancels the active
operation, has a ten-second gate-acquisition deadline, reconciles the final process observation, and sends
the real failed `OperationResult` through the WebUI completion event.

Ordinary running-state evidence is now persisted with `AutostartMode.Never`. Only the explicit server
autostart setting or an explicit persisted `AgentStart`/`WindowsLoginWithDelay` policy authorizes Agent
startup; user-created Start schedules remain an independent, tested authority.

---

## CP-2026-028 — Loader creation can continue after the WebUI reports a timeout

| Field | Value |
|---|---|
| Date | 2026-08-19 |
| Severity | High — retry can duplicate a creation transaction whose acceptance response was lost |
| Area | WebUI / native bridge / managed-loader creation |
| Status | **Fixed** — client-owned durable operation identity and reconnect polling |
| Fixed branch | `feature/complete-loaders-modpacks` |
| Validation | Delayed/lost acceptance, duplicate guard, progress reattach, and native prompt-acceptance contract tests |

Catalog/provider I/O could occur before Agent acceptance and exceed the renderer's request timeout.
Creation now carries a client-generated operation ID before submission, validates cached reviewed
selection without provider work, and reconciles progress after a lost response rather than enabling a
second submission.

---

## CP-2026-027 — Modrinth add-on install loses pending and installed state

| Field | Value |
|---|---|
| Date | 2026-08-19 |
| Severity | High — the UI invited duplicate installs and did not show the authoritative outcome |
| Area | WebUI / add-on operations / inventory reconciliation |
| Status | **Fixed** — correlated deferred operation state and exact inventory reconciliation |
| Fixed branch | `feature/complete-loaders-modpacks` |
| Validation | Pending/progress/cancel/failure/retry/remount/installed/loaded React tests and native bridge contracts |

The bridge awaited the complete serialized restartable operation and React cleared its busy state at
the request boundary. Add-on mutation now returns an operation ID promptly, survives navigation and
reload, and reconciles exact provider project/release identity against Agent inventory. `Loaded`
remains separate and requires loader-specific current-session evidence.

---

## CP-2026-026 — WebUI reports a timeout after an accepted server start succeeds

| Field | Value |
|---|---|
| Date | 2026-08-17 |
| Severity | High — a successful Paper start is presented as failure and invites duplicate input |
| Impact | The WebUI waits on the complete lifecycle command behind a request timeout instead of acknowledging the accepted operation |
| Area | WebUI / native bridge / lifecycle command contract |
| Status | **Fixed** — prompt acceptance and operation-correlated completion event |
| Fixed branch | `feature/paper-polish-fabric-neoforge` |
| Validation | Native deferred-lifecycle contract tests; React pending, late failure, duplicate, early completion, and stale operation-ID tests; isolated Paper fixture |

The old WebUI handler awaited the full WPF lifecycle command before returning the correlated bridge
response. Paper could reach readiness after the browser request timeout, producing “ChunkPilot did not
answer in time” while the authoritative server was already Running. Start, stop, and restart now
return a native-generated operation ID immediately. Snapshots carry lifecycle state; a separate
operation completion event carries the same ID. Duplicate in-flight commands reuse the active ID,
and React ignores a stale completion for a different operation rather than clearing newer work.

---

## CP-2026-025 — WebUI omitted the authoritative Internet-hosting workflow

| Field | Value |
|---|---|
| Date | 2026-08-16 |
| Severity | High — ordinary users could not find or truthfully complete friend sharing |
| Impact | Share copied only a local address; public reachability was hardcoded unavailable; router, firewall and outside-in commands were absent from WebUI |
| Area | WebUI / selected server connectivity presentation |
| Status | **Fixed** — authoritative connectivity snapshot and command adapters restored |
| Fixed branch | `feature/verified-vanilla-paper-plugins` |
| Validation | Bridge allowlist/refresh policy, mode persistence, Share address truthfulness, consent, fixtures, keyboard dialog, and packaged visual coverage |

The native App and Agent already owned network mode, router mapping, exact firewall setup, cancellation,
cleanup, and outside-in verification. The WebUI mapper replaced that state with a fixed unavailable
summary, its copy action always selected the local endpoint, creation collapsed intent into three
ambiguous choices, and Settings exposed only a port row. The fix projects the existing state without
duplicating authority, distinguishes local/LAN/router-reported/verified-public endpoints, and refuses
public copy without outside-in proof. No real firewall, router, or public port was touched by the fix.

---

## CP-2026-024 — WebView2 crash helper can retain the preview profile after ChunkPilot closes

| Field | Value |
|---|---|
| Date | 2026-08-16 |
| Severity | Medium — immediate close leaves one browser-owned helper and locks isolated data |
| Impact | The native UI and Agent exit, but packaged smoke cannot remove the app-specific WebView profile |
| Area | WebUI native host / WebView2 environment lifecycle |
| Status | **Fixed** — ChunkPilot-owned crash reporting prevents the orphan helper |
| Fixed branch | `feature/minecraft-version-platform` |
| Validation | Environment-options unit contract plus packaged WebUI close rerun with complete process and temporary-root cleanup |

The first final packaged smoke closed the native UI in 165 ms and stopped its intended Agent, but
WebView2's separate crash-reporting helper survived and retained `ExtensionActivityEdge` in the
isolated profile. The host already owns renderer failure recovery and diagnostics, so its single
environment now enables WebView2 custom crash reporting rather than the separate uploader. The rerun
closed in 155 ms, left no invisible UI or browser helper, preserved an unrelated Agent, and removed
both temporary roots.

---

## CP-2026-023 — Real WebUI version rows cannot be selected

| Field | Value |
|---|---|
| Date | 2026-08-16 |
| Severity | High — the authoritative creation path cannot advance past Version |
| Impact | Fixture rows work, but every row returned by the real C# bridge is disabled |
| Area | WebUI / creation catalog bridge contract |
| Status | **Fixed** — one typed authoritative catalog contract now drives real and fixture rows |
| Fixed branch | `feature/minecraft-version-platform` |
| Validation | Bridge mapping tests, exact-selection tests, 906-entry browser tests, full unit and integration suites |

The React browser required `support`, `selectable`, artifact, Java and launch evidence. The real
`creation.catalog` response emitted only an older subset (`id`, label, channel, Java and warning), so
`selectable` was `undefined` and every live row was disabled. Fixture data already used the newer
shape and concealed the mismatch. The bridge now serializes the complete authoritative option,
creation resolves the exact selected ID from that same catalog, and unavailable rows remain
inspectable without becoming selectable.

---

## CP-2026-022 — MOTD formatting mutates text or loses the selected range

| Field | Value |
|---|---|
| Date | 2026-08-16 |
| Severity | Medium — appearance editing can change saved text or apply formatting to the wrong range |
| Impact | Focusing or switching modes can flatten formatting; toolbar clicks lose the browser selection |
| Area | WebUI / server appearance / MOTD editor |
| Status | **Fixed** — semantic runs and stable text offsets replace transient DOM state |
| Fixed branch | `feature/minecraft-version-platform` |
| Validation | Parser/serializer, no-mutation, multi-line selection, every style, Unicode, undo/redo, raw fallback, animated-obfuscation selection stability and Reduced Motion cleanup tests |

The preview spans carried only CSS while selection recovery looked for semantic formatting metadata.
Clicking a toolbar control also moved focus before the operation could consume the DOM `Range`, and
mode synchronization reparsed rendered text into the draft. The editor now stores formatting in a
small run document, represents selection as stable text offsets, restores it after toolbar actions,
and never derives authoritative text from rendered markup. Unsupported input stays losslessly in raw
mode.

---

## CP-2026-021 — Changing a server icon a second time fails with an access error

| Field | Value |
|---|---|
| Date | 2026-08-12 |
| Severity | Medium — a repeatable customization fails after succeeding once |
| Impact | A second icon change in one session can fail with Access denied instead of replacing the icon |
| Area | App / `ServerIconService.ConvertAndInstallAsync` / `InstallServerIcon` |
| Status | **Fixed** — lock-free previews and transactional finalization covered by repeated-change regression |
| Roadmap task | [CP-FRICTION-003](PRODUCT-FRICTION-REGISTER.md#cp-friction-003--server-icon-workflow) |
| Workaround | Restart ChunkPilot before changing the icon again |

Reproduction: change a server icon successfully, then choose and apply a different image in the same
session. The root cause was WPF URI image binding retaining a file-backed handle to the mutable
`server-icon.png`; the Agent's second atomic replacement was then denied by Windows. ChunkPilot now
decodes server and library previews fully into memory with delete-sharing, finalizes output before
publishing a library entry, and refreshes visible identity only after Agent success. Automated A -> B
-> C, reopen, source-move, same-source, cancellation and locked-finalization cases cover the fix.

---

## CP-2026-020 — ChunkPilot-managed network exposure survives UI process death

| Field | Value |
|---|---|
| Date | 2026-08-12 |
| Severity | High — an inbound path and renewal authority survived the application that enabled it |
| Impact | Router renewal and cached Public access verified could outlive the UI; persisted intent could recreate exposure after Agent restart |
| Area | App / Agent / public connectivity lifecycle |
| Status | **Open** — replacement implementation awaits independent read-only review and real Windows acceptance |
| Roadmap task | [CP-FRICTION-010](PRODUCT-FRICTION-REGISTER.md#cp-friction-010--ui-exit-network-safety) |
| Correction branch | `fix/public-connectivity-lease-safe-exit` |
| Previous experiment | `fix/ui-exit-network-safety` at `01d891730b68c4db11117b80bffb4c6ba1a9d5bb`, preserved unmodified for evidence |

### Verified original behavior and root cause

The Agent treated durable `DirectInternetEnabled` intent and a running server as sufficient renewal
authority. Unexpected UI loss left the Agent, Minecraft process, mapping, renewal worker, and current
external result alive. Exposure had no non-transferable capability or generation, so a replacement UI
or Agent restart could inherit state nobody had explicitly re-enabled.

### Replacement implementation awaiting acceptance

The accepted-base reimplementation uses an Agent-minted memory-only UI capability bound to PID plus
exact raw process-creation identity and an independent lease ID/generation per server. Lease authority,
renewal, and current external verification end synchronously on normal close or proven process death;
exact-owned router cleanup and world-safe stop of all managed servers then proceed independently under
bounded rules. A new Agent owns no lease and treats persisted public state as cleanup-only. Durable
exact Windows Firewall configuration is intentionally unchanged and is not treated as a listener,
public route, or verification.

The experiment's exact process identity, observer, bounded cleanup, retained ownership evidence, and
race scenarios were selectively reimplemented. Its global `UiSessionNetworkOwnership`, elevated
firewall guardian, firewall `LeaseSessionId`, helper guard operation, deferred firewall backend, and
window-lifetime firewall documentation were rejected.

### Independently verified review defects and correction

The first read-only deployment review found four independent High defects: router work carried only a
point-in-time lease decision; fire-and-forget restoration could cross stale-exposure cleanup; safe exit
could wait forever behind the server operation gate; and detached-process escalation accepted a
one-second start-time tolerance.

The bounded correction carries exact server, lease, generation, and Agent epoch authority through the
serialized router executor and revalidates it before durable and wire mutations. Safe exit atomically
seals the Agent against replacement registration, revokes an exact lease snapshot, and cleanup can
affect only those generations, including a latest generation whose manual cleanup was already pending;
the old unscoped global router-release surface was removed. Startup
loads restoration intent without starting it, completes stale listener stop and ownership-safe route
cleanup first, and suppresses affected restoration; unavailable metadata storage leaves restoration
inert. Exit cancels the active cancellable operation, uses a bounded priority wait for the lifecycle
path, preserves transactional unwind and world-safe save/stop/escalation, uses bounded router
persistence and whole-attempt deadlines, and keeps the Agent alive with leases revoked while a known
process remains. New process records persist
raw Windows creation `FILETIME`; exact matching has no tolerance, and legacy records without it cannot
authorize automatic termination.

Regression evidence covers queued establishment/renewal after revocation, old cleanup versus a newer
generation, a non-cancellable late router-create success retained only for exact cleanup,
exit-epoch registration rejection, production startup stale-exposure ordering, cooperative,
delayed and failing operation cancellation, listener/process terminal verification, exact identity
round-trip and one-tick/PID-reuse rejection, legacy fail-closed behavior, and late router/external
results. The entry remains Open until final independent review and real Windows acceptance.

### Why this entry remains Open

Automated tests use synthetic processes, fake routers/probes, temporary stores, and isolated roots.
They do not mutate a real router or Windows Firewall and do not invoke UAC. Independent read-only review
and real Windows normal-close/taskkill acceptance remain required. No guarantee is claimed for
simultaneous App-and-Agent termination, machine power loss, or immediate removal from an unreachable
router.

---

## CP-2026-019 — Normal Create Server action still opens superseded creation flow

| Field | Value |
|---|---|
| Date | 2026-08-11 |
| Starting branch | `fix/external-probe-ipv4-affinity` |
| Starting commit | `86232f71578373ce60a71026f44a1df8748b7171` |
| Severity | Medium |
| Impact | The production entry point does not use the newer validated Vanilla creation workflow and exposes competing creation experiences |
| Area | App / Create Server / navigation |
| Status | **Fixed** |
| Reported by | Real manual acceptance on the current reviewed product branch |
| Roadmap task | Create Server v2 Vanilla product cutover |
| Workaround | None required |
| Fixed branch | `fix/create-server-v2-product-cutover` |
| Fixed commit | `Make Create Server v2 the default Vanilla flow` |
| Regression | `CreateServerV2ProductCutoverTests`, updated live/preview isolation coverage, existing live Vanilla and navigation suites |
| Validation | All three normal CTAs share the semantic Vanilla command; packaged zero-server Dashboard and Servers actions both opened the same live v2 wizard without a switch |

### Reproduction

Use any normal **Create server** action. ChunkPilot opens `InstallServerWindow`, while the newer
functional Vanilla workflow requires the development-only `--create-server-v2-live-vanilla` switch.

### Root cause

All three normal Create server controls still bind `MainViewModel.InstallServerCommand`, whose handler
directly constructs `InstallServerViewModel` and `InstallServerWindow`. The validated live Vanilla
composition exists only in the command-line startup branch, so the shell has no product route to it.

### Resolution

`MainViewModel.CreateVanillaServerCommand` now raises a presentation-independent Vanilla creation
request. `MainWindow` handles that request at the composition root, supplies the real Agent gateway,
location chooser and completion navigator, and reuses the existing live wizard if it is already open.
The retained development switch calls that same shell route. The Dashboard and Servers presentations
all bind the semantic command; **Add existing server** continues to use its by-reference import path.

The later WebUI product cutover migrated the broader version, platform, memory, storage, and
networking fields into the single shipped Create server flow. The now-unreachable
`InstallServerWindow`, its view model, its dead presentation event, and three review scripts for the
removed `--create-server-v2-live-vanilla` switch were deleted rather than left as misleading product
scaffolding. The synthetic `--create-server-v2-preview` remains isolated for native design review.

---

## CP-2026-018 — External reachability probe can use IPv6 while verifying an IPv4 mapping

| Field | Value |
|---|---|
| Date | 2026-08-11 |
| Starting branch | `fix/natpmp-full-datagram-confirmation` |
| Starting commit | `a5fe0f225a932b36219fa6d05081de04de2490b9` |
| Severity | Medium — no false result is produced, and no correct one can be produced either |
| Impact | A valid IPv4 Direct internet server may be impossible to verify from outside on a dual-stack Windows computer, even though the router mapping and the firewall rule are both exactly right |
| Area | External reachability / HTTP transport |
| Status | **Fixed** |
| Reported by | Manual acceptance of the reviewed build on real hardware, 2026-08-11 |
| Roadmap task | Friend connectivity / External reachability |
| Workaround | None, and none should be required. Users are never told to disable IPv6 |
| Fixed branch | `fix/external-probe-ipv4-affinity` |
| Fixed commit | `Pin external probe to IPv4 mapping family`, the single local commit on that branch |
| Regression | `ExternalProbeIpv4AffinityTests`: twenty-five cases over the transport and over the production client with only its resolver and socket replaced |
| Validation | Removing the family filter makes the dual-stack, ordering and IPv6-only cases select an IPv6 address; removing the refusal makes the IPv6-only case issue the check rather than fail |

### Reproduction

A disposable Vanilla server, running, TCP 25566, listening on `10.0.0.140:25566`. Direct internet
established over UPnP IGD through the gateway on `10.0.0.1`: external port 25566, a routable
router-reported public IPv4 address, a 3600-second renewable lease. Windows Firewall independently
showed ChunkPilot's own exact rule — TCP 25566, the exact managed Java runtime, Public profile, Rule
ready. External access read **Not checked**, and no claim about public reachability had been made.

**Check from outside** was then pressed deliberately. The deployed `workers.dev` Worker answered
successfully over HTTP and reported the router's IPv4 address as expected, an IPv6 address as observed,
and `unsupported_address_family`.

ChunkPilot behaved correctly with that answer: it concluded nothing and did not report Reachable. The
temporary mapping and firewall rule were removed through ChunkPilot afterwards and the server stopped.
The addresses themselves are deliberately not recorded here; the reproduction turns on their families,
not their values.

### Root cause

The probe client set no address family for its own HTTPS connection. On a dual-stack computer the
transport is free to prefer IPv6 for `workers.dev`, so the request that was supposed to demonstrate an
IPv4 path arrived at Cloudflare over IPv6.

The service is then being asked an impossible question. Its one safety rule is that it may connect back
only to the address it actually observed — never to a caller-supplied one — so with an IPv6 source and
an IPv4 expectation there is nothing it can compare and nothing it may connect to. `source_mismatch`
and `unsupported_address_family` are the correct answers to that request, and they are the only answers
it can give. The defect is entirely on the ChunkPilot side: the transport did not match what was being
verified.

### Resolution

`ExternalProbeTransport` opens the probe's connection over the family of the endpoint being verified.
The client parses the router-reported address, refuses anything that is not IPv4 before sending, and
carries the family on the request; the handler's connect callback resolves the configured hostname,
keeps only addresses of that family, and connects to one of them — up to three, each on its own bounded
wait, so several A records are tolerated and a long record set is not a loop.

Nothing above the socket changes. The request URI, the TLS host, SNI, certificate validation and the
authority are still the configured hostname, never a resolved numeric address, and the proxy setting is
untouched. If the hostname has no IPv4 address, or none of them answers, the check fails truthfully as
`ServiceUnavailable` and nothing is retried over IPv6: a verification of an IPv4 mapping made over IPv6
would not be a verification of it.

The Worker is unchanged. It still connects only to the source address Cloudflare observed, and it is
still the authority on whether that address is the expected one — so a VPN, a proxy or upstream NAT
that changes what Cloudflare sees continues to produce no conclusion rather than a false one.

---

## CP-2026-017 — Router mapping ownership can be rebound to a different network binding

| Field | Value |
|---|---|
| Date | 2026-08-10 |
| Starting branch | `fix/router-continuity-evidence-propagation` |
| Starting commit | `d39aecf` |
| Severity | High — a mapping made on one router can be relabelled as belonging to another, while keeping the identity every external result is bound to |
| Impact | **Public access verified** stays current for a mapping ChunkPilot is no longer anywhere near; a later withdrawal can be addressed to a router that never held the mapping; the exposure that does exist stops being tracked; and a check can rewrite what a past establishment was made of |
| Area | Router mapping ownership / External reachability verification |
| Status | **Fixed** — reopened twice; see *Reopened* below |
| Reported by | Independent read-only deployment review of `d39aecf`, verdict `EXTERNAL REACHABILITY DEPLOYMENT REVIEW: NO-GO`; reopened by the final deployment reviews of `ee7aae0` and `cecc55e`, both verdict `EXTERNAL REACHABILITY FINAL DEPLOYMENT REVIEW: NO-GO` |
| Roadmap task | External reachability verification |
| Workaround | Do not deploy the external reachability probe |
| Fixed branch | `fix/router-binding-and-natpmp-recovery`, then `fix/legacy-router-ownership-and-natpmp-dispatch`, completed on `fix/legacy-upnp-and-natpmp-send-confirmation` |
| Fixed commit | `Fix legacy UPnP ownership and NAT-PMP send confirmation`, the single local commit on the third branch |
| Regression | Twenty-one cases in `RouterMappingContinuityIntegrationTests`, through the production store, coordinator, providers and external reachability coordinator — nine for the binding rules and twelve for rows written before ownership recorded a network |
| Validation | First pass: restoring the assignment that caused it — a check writing its own binding onto the owned one — fails nine of forty-four coordinator cases. Second pass: restoring the `IsKnown` guards fails six of the eight upgrade cases, including a wrong-router deletion for each of NAT-PMP, UPnP and PCP. Third pass: removing the ownership reset fails all four running-legacy cases, including the adoption of a coincident UPnP entry |

### Why this is not CP-2026-015

CP-2026-015 is about *time*: an entry that stopped existing, and evidence that outlived it. This is about
*place*: an entry that still exists exactly where it was made, and a record that stopped saying so. The
two share a symptom and nothing else. CP-2026-015 stays fixed and its guards are untouched.

### Reproduction

1. Start a server with Direct internet on and a mapping established over any mechanism — say Ethernet,
   gateway 192.168.1.1.
2. Press **Check from outside** and reach **Public access verified**.
3. Move the computer to another network, or simply let it fall back to Wi-Fi: another adapter, another
   router. Undocking a laptop is enough.
4. Press **Check**.

Observed: continuity correctly refused to read the new router's epoch as evidence about the old
mapping — and then `RouterMappingCoordinator.CheckAsync` wrote that router's address into the record's
`GatewayAddress` anyway. `HasActiveMapping` stayed true and the `MappingInstanceId` was preserved, so
the mapping made on the first router was now recorded, projected and persisted as the second router's.
**Public access verified** stayed on screen for a public endpoint that no longer existed; a later
**Turn off** resolved the current binding and would have sent the deletion to the second router; and the
exposure genuinely open on the first one was no longer described by anything.

The same address on two adapters is the case that makes this more than a laptop problem. 192.168.1.1 is
the most reused address on any home network, so the record's gateway text kept matching after a move
that changed the router completely, and every later comparison agreed with itself.

### Root cause

Ownership binding and current discovery were the same field. `RouterMappingRecord.GatewayAddress` was
written by every check and every attempt, from whatever binding had just been resolved, while also
serving as the evidence for which gateway owned the mapping. Nothing in the record said which interface
the mapping was made through, so two routers answering on the same address were indistinguishable, and
an observation of one was silently accepted as an observation of the other.

### Resolution

`RouterBindingIdentity` names a network context as what it is — the adapter a request leaves through and
the gateway it is sent to — and it is the same identity the per-gateway epoch history has always been
filed under. The record now carries two of them, and they can never be confused: `OwnedBinding`, written
only where a mapping is successfully established, and `DiscoveredBinding`, written by every check and
every attempt and used for nothing but diagnosis.

From that, four rules. A check may no longer write ownership or a router-reported address against a
mapping that is not on the router it reached. Continuity is read only when the report's binding *is* the
owned one, matched on interface as well as address. A mapping continues only when it is renewed on the
binding that owns it, so an identical-looking entry on another router mints a fresh identity rather than
inheriting one. And a withdrawal resolved to a different binding sends nothing at all: the record keeps
its owner and the removal is retried when the computer is back on that network, rather than being
addressed to a stranger's router.

While the owned and discovered bindings disagree, the mapping is projected as **Needs attention** rather
than as open. The record is left completely intact — it may still have to be withdrawn — but the
identity a projection mints is dropped, which is what ends a verification the move invalidated and what
stops the original one returning when the computer comes back. Reconciliation then establishes a mapping
on the network the computer is actually on, as a new mapping with a new identity.

### Reopened — the rows that predate the rule it enforces

The final review of `ee7aae0` confirmed all of the above for records this build writes, and then found
that the guards it added ran only where an owned binding was fully known. A row written by the previous
build carries no interface, so `OwnedBinding.IsKnown` is false and every one of those guards was skipped
— the state the guards exist for was the one state that fell through them, straight onto whatever router
this computer happens to reach now.

The consequences differ by mechanism and all of them are wrong:

- **NAT-PMP** has no nonce and cannot read a table. RFC 6886 section 3.4 scopes a deletion to the
  requesting client's own internal port, so a deletion sent to a router that never held the mapping can
  only ever remove something else this computer has on that port.
- **UPnP** can read a table, and reading it proves nothing here. ChunkPilot's description is a constant
  every install writes, and the internal endpoint is this computer's own, so an entry matching the old
  row in every value is evidence that *some* ChunkPilot made it and never that this row did.
- **PCP** carries a mapping nonce, which RFC 6887 section 11.1 defines as "a random value chosen by the
  PCP client to identify this mapping", and ChunkPilot's provider both sends it and refuses to delete
  without it. A stray delete is therefore structurally incapable of removing another application's
  mapping — but PCP cannot read a table either, so nothing before the request can show the router
  reachable now is the one holding the mapping, and a reply cannot tell a real deletion from a no-op.

Alongside the deletions, the record kept `HasActiveMapping` with nothing to contradict it, so the card
read **Router port is open** for a mapping ChunkPilot could not identify, an external check was eligible
against it, and a check on the current router could write that router's WAN address onto it.

### Resolution of the reopened path

`RouterMappingPolicy.UpgradeStoredRecord` reads an old row's bare gateway address as it is loaded and
folds it into `OwnedBinding` with no interface. That is what it always was — an address, not a network —
so `IsKnown` reports it as not fully known, it matches nothing, and it authorises nothing, while
remaining available to tell the owner which router still holds the port. It is never written back.

Proof replaced permission everywhere the guards were skipped. A removal now requires the owned binding
to match the current one, so an unproven network is refused exactly like a wrong one and nothing is sent
for any mechanism; the record keeps its owner and its exposure rather than being quietly dropped. A
check may only write an owned mapping's evidence when it reached that mapping's own network, so no
adoption can happen by resemblance. And an active mapping with no provable network projects as **Needs
attention** rather than open, which drops the identity, makes any external result stale and blocks a new
check — an upgrade cannot inherit a verification, and cannot be talked into one by matching values.

The safe resolution is a fresh mapping, not a deletion: reconciliation establishes one on the network
this computer provably is on, which yields a known owned binding and a new identity under the ordinary
rules. PCP's nonce was deliberately not used to build a narrower path, because strength of ownership is
not evidence of location, and a legacy row must never produce a claim of closure it cannot support.

### Reopened again — the safe resolution walked straight back into it

The review of `cecc55e` confirmed the upgrade, the fail-closed removals and the stopped-server
behaviour, and then followed the one path those left open: the *running* server, where the safe
resolution actually runs.

`EstablishAsync` derives its working records from the one it was given, so a legacy row's
`HasActiveMapping = true` flowed into `attempted` and then into `candidate` — the record that
`RouterMappingPolicy.ProvesOwnership` is asked about. That method's first question is whether ChunkPilot
holds anything at all, and a legacy row answered yes. With a UPnP router in front of it holding an entry
that matched the old row in every value there is — same public port, same internal endpoint, and the
description every ChunkPilot install writes — ownership was proven by resemblance. The conflict branch
was skipped, `AddPortMapping` was sent for somebody else's entry, and the record was written back
claiming that router as owner.

One bit was doing two jobs: recording that a mapping may exist somewhere, and asserting that ChunkPilot
owns one here. The second was never true, because the build that wrote it never recorded where "here"
was.

### Resolution of the third reopened path

`RouterMappingPolicy.ResolveUnprovableClaim` separates them, and runs before anything about the router
in front of us is evaluated — at the top of `EstablishAsync` and again before the port-change withdrawal
in reconciliation. The claim ends: `HasActiveMapping` and `RemovalPending` are cleared, the owned
binding is emptied, and nothing is left for an ownership rule to read. What the claim described becomes
a `LegacyRouterExposure` — port, transport, mechanism, the gateway address the old build recorded, and
the lease it recorded — kept somewhere no ownership rule looks, so it can be told to the owner and can
never prove anything.

From there the current router is a fresh ownership context. An entry that merely resembles the old row
is foreign: not adopted, not renewed, not deleted, reported through the conflict semantics that already
existed. An empty router gets a new mapping under the ordinary rules, with a known owned binding and a
new identity, and the earlier verification stays stale. The exposure survives that success rather than
being hidden by it — a port open somewhere else does not close because a new one opened here — and it is
bounded: once the finite lease it recorded has run out, it stops being reported at all.

---

## CP-2026-015 — Router mapping continuity survives unobserved gateway state loss or foreign replacement

| Field | Value |
|---|---|
| Date | 2026-08-09 |
| Starting branch | `fix/external-reachability-identity-races` |
| Starting commit | `f05c6b5` |
| Severity | High — **Public access verified** can be shown for a router mapping that was never probed |
| Impact | A point-in-time external result outlives the exact mapping it was gathered about, which is the one thing the result-lifetime model exists to prevent |
| Area | Selected server › Overview › Connect › Direct internet › External access; router mapping identity |
| Status | **Fixed** — reopened once; see *Reopened* below |
| Reported by | Independent read-only deployment re-review of `f05c6b5`, verdict `EXTERNAL REACHABILITY RE-REVIEW: NO-GO`; reopened by the final review of `85e12e1`, verdict `EXTERNAL REACHABILITY FINAL REVIEW: NO-GO` |
| Roadmap task | External reachability verification |
| Workaround | Do not deploy the external probe service |
| Fixed branch | `fix/router-mapping-continuity-proof`, completed on `fix/router-continuity-evidence-propagation` |
| Fixed commit | `Fix router continuity evidence propagation`, the single local commit on the second branch |
| Regression | `RouterMappingContinuityIntegrationTests` (33 cases through the production providers, service, router coordinator and external reachability coordinator) and `GatewayEpochContinuityTests` (37 cases over the RFC rules and the real wire formats) |
| Validation | First pass: disabling the four original guards fails 7 of 16 coordinator cases. Second pass: discarding the propagated evidence again fails 10 of the 33 coordinator cases and 2 of the 37 provider cases; all pass with it |

### Why this is not CP-2026-012

CP-2026-012 is the *observed* ABA: ChunkPilot removed a mapping itself and asked for an identical one
back. That is genuinely fixed and stays fixed. This entry is the *unobserved* case — the mapping
stopped existing without ChunkPilot doing anything, and nothing in the reply that followed said so.

### Reproduction — PCP or NAT-PMP gateway restart

1. Start a server with Direct internet on and the mapping established over PCP or NAT-PMP.
2. Press **Check from outside** and reach **Public access verified**.
3. Restart the router, or otherwise make it lose its mapping table. ChunkPilot observes nothing: no
   command was issued and neither protocol can read a mapping table.
4. Let the ordinary lease renewal run.

Observed: the renewal message is byte-for-byte a creation message — RFC 6886 section 3.7 states this
outright — so the gateway creates a new mapping and answers exactly as it would have answered a
renewal. Every value ChunkPilot could compare matched, `RouterMappingCoordinator.EstablishAsync`
treated the entry as continuing, `MappingInstanceId` was preserved, and the verification gathered about
the mapping that no longer existed was still reported as current for the one that replaced it.

### Reproduction — UPnP foreign replacement

1. As above, over UPnP, and reach **Public access verified**.
2. Have something else take the public port on the router: another device's forward on the same
   external and internal port numbers.
3. Let reconciliation run.

Observed: `EstablishAsync` read the table, could not prove ownership, and returned `attempted` — which
is `record with { … }` and therefore still carried `HasActiveMapping = true` from the record.
`ResolvePhase` reads `HasActiveMapping` before it reads `LastFailure`, so the phase projected as
`Active`, the projection then suppressed the failure because the phase was settled and good, and
`ResolveMappingInstance(true)` handed back the identity the verified result was bound to. The card read
**Router mapping active** and **External access verified** for somebody else's port forward.

The same branch also covered the case where the router simply no longer lists the entry: if the
re-creation that followed failed, `attempted` again carried the old ownership and the old identity.

### Root cause

`MappingInstanceId` was replaced on evidence ChunkPilot could see, and preserved by default when it
could not. For a mechanism that cannot read the router's table that default was unconditional: any
successful request on unchanged terms counted as a continuation. Nothing in the code consulted the one
piece of evidence both datagram protocols provide for exactly this question — PCP's Epoch Time
(RFC 6887 section 8.5) and NAT-PMP's Seconds Since Start of Epoch (RFC 6886 section 3.6), both of which
a gateway resets when it loses its mapping state. Both fields were parsed past and discarded.

### Resolution

The two datagram providers now read the epoch from every response and validate it with the RFC's own
rule — PCP's `client_delta`/`server_delta` comparison with its +2 and /16 tolerances, NAT-PMP's
seven-eighths conservative estimate with its 2-second allowance — against a per-gateway history scoped
by interface, gateway address and control port, held by the provider instance and never static. The
result is reported as a three-valued `GatewayContinuity`: confirmed, state lost, or unknown.

The coordinator now requires affirmative evidence to preserve an identity rather than the absence of
contrary evidence. A mechanism that can read the table must have seen the entry; a mechanism that
cannot must have a confirmed epoch. Evidence of state loss ends the establishment whether or not the
request that revealed it succeeded, and a capability check that reveals it stops claiming the mapping
is open. Where a readable table shows the entry gone or foreign, ownership is dropped before any later
branch can carry it forward, and nothing is sent to the router: what is there is not ChunkPilot's, and
what was ChunkPilot's is no longer there.

Failing closed here costs a fresh mapping identity and therefore a stale external result — never a
false claim that a mapping is open, and never a deletion of somebody else's.

### Reopened — the evidence was gathered correctly and then thrown away

The final review of `85e12e1` confirmed the epoch parsing, the arithmetic, the gateway scoping and the
UPnP replacement handling, and then found two paths on which the reading never reached the code that
acts on it. Both restore the original symptom in full: **Public access verified** stays on screen for a
mapping that no longer exists.

**Substitute public port.** A restarted gateway may answer a mapping request successfully while
assigning a public port other than the one that was asked for. `PcpMappingProvider.CreateAsync` and
`NatPmpMappingProvider.CreateAsync` both withdraw that substitute and convert the operation into
`ForeignMappingPresent` — by constructing a *new* outcome through `RouterMappingOutcome.Failed`, which
defaults `Continuity` to `Unknown`. The reading taken from the authoritative response was dropped on
the floor. The coordinator then saw a conflict carrying no evidence of state loss, kept
`HasActiveMapping`, kept the `MappingInstanceId`, and the verified result stayed current for a mapping
the gateway had already replaced.

**Refused discovery.** `RouterCapabilityReport` carried `Selected` and `Attempts`, and the coordinator
read continuity only from `Selected`. A gateway that has just restarted is exactly the gateway most
likely to refuse the request whose header proves it restarted — and a refused attempt is never
selected, so the proof sat in `Attempts` where nothing looked. The same held when the check found
nothing usable at all: `EstablishAsync` returned the record with its ownership intact, so the old
establishment survived on the grounds that no replacement had been found for it yet.

### Resolution of the reopened paths

Continuity is now a fact about the gateway, carried independently of how any operation was finally
classified and combined only through `GatewayContinuityEvidence.Stronger`, where evidence of loss
always wins. Both providers combine the readings from the mapping request and from the withdrawal of
the substitute, and attach the result to the conflict they return.

`RouterCapabilityReport.ContinuityFor(mechanism)` reads across every attempt, selected or not, so a
refused mechanism's answer still reaches the Agent; `WithContinuitySpent` marks a cached report's
readings as consumed so one observation is never acted on twice. The coordinator asks for continuity by
the mechanism that actually owns the mapping, and only when the check reached the same gateway the
record was established against — so a PCP restart cannot invalidate a NAT-PMP entry, a NAT-PMP restart
cannot invalidate a UPnP one, and another router's epoch cannot invalidate this one's. Ownership is
withdrawn on proof of state loss even when the operation ends unsupported, refused or conflicted: the
coordinator no longer waits for a successful replacement before admitting that the old entry is gone.

---

## CP-2026-016 — NAT-PMP reboot recovery omits the RFC-required randomised recreation delay

| Field | Value |
|---|---|
| Date | 2026-08-10 |
| Starting branch | `fix/router-mapping-continuity-proof` |
| Starting commit | `85e12e1` |
| Severity | Medium — protocol non-compliance; no user-visible untruth and no data at risk |
| Impact | Every ChunkPilot client on a network would re-register its mappings the instant a gateway finished rebooting, which is the synchronised stampede RFC 6886 introduced the delay to prevent. A gateway that drops or refuses requests under that load leaves ChunkPilot reporting a failure it caused |
| Area | Router mapping › NAT-PMP › reboot recovery |
| Status | **Fixed** — reopened four times; see *Reopened* below |
| Reported by | Independent read-only deployment final review of `85e12e1`; reopened by the deployment review of `d39aecf` and by the final deployment reviews of `ee7aae0`, `cecc55e`, and `ac889e0` |
| Roadmap task | External reachability verification / router mapping |
| Workaround | None needed; the omission makes recovery more aggressive rather than less correct, and a single-client home network never sees it |
| Fixed branch | `fix/router-continuity-evidence-propagation`, then `fix/router-binding-and-natpmp-recovery`, `fix/legacy-router-ownership-and-natpmp-dispatch`, and `fix/legacy-upnp-and-natpmp-send-confirmation`, completed on `fix/natpmp-full-datagram-confirmation` |
| Fixed commit | `Require full NAT-PMP datagram confirmation`, the single local commit on the fifth branch |
| Regression | Six `GatewayEpochContinuityTests` cases, twenty-eight `NatPmpRebootRecoveryTests` cases — three against the production UDP channel and three against its exact full-datagram decision seam — and one production-path case in `RouterMappingContinuityIntegrationTests` |
| Validation | Deterministic throughout: the wait is injected and released by the test, nothing waits for real time, and the production draw is checked against the RFC's interval over 500 samples. Restoring the outstanding-task guard fails the overlapping-reboot case; removing the serial gate fails the concurrent-recreation and queued-cancellation cases; restoring the wait-then-return-then-send shape fails the case where a reboot is recorded after the gate and before the wire; consuming at invocation instead of at confirmation fails the refused-send and cancelled-mid-send cases; ignoring the send byte count fails the complete-versus-short decision and pending-generation regressions |

### Protocol requirement

RFC 6886 section 3.7: a client renewing its mappings because Seconds Since Start of Epoch showed a
reboot "MUST first delay by a random amount of time selected with uniform random distribution in the
range 0 to 5 seconds, and then send its first port mapping request", after which requests "SHOULD be
issued serially, one at a time".

### Evidence

`NatPmpMappingProvider` detected the reset correctly and reported it, and the coordinator acted on it
correctly, but nothing anywhere delayed the recreation that followed. The section was named in the
class remarks as deliberately not implemented.

### Resolution

`NatPmpRebootRecovery` arms a wait the moment a response proves the reboot — which is where "delay,
then send" begins — and the next mapping request for that gateway waits for it and spends it. Requests
that arrive while it is still running wait for the same one rather than starting their own, which also
gives the burst immediately after a reboot the serial behaviour the section asks for. Withdrawals do
not wait, because withdrawal is not recreation, and a gateway that has not rebooted has nothing armed,
so ordinary renewal is untouched. A wait that is cancelled leaves the obligation standing: the request
it was holding back never went out.

The draw and the wait are behind `INatPmpRebootDelay`, whose production implementation draws uniformly
from 0 to 5 seconds with `RandomNumberGenerator` and waits with `Task.Delay`. Nothing blocks a thread,
every wait honours the operation's cancellation token, and the scope is per interface, gateway and
control port — the same identity the epoch history itself is filed under.

PCP's own rapid-recovery procedure (RFC 6887 section 14) remains deliberately unimplemented and is
still documented as such; this entry covers NAT-PMP only.

### Reopened — half of section 3.7, and one reboot at a time

The deployment review of `d39aecf` confirmed the detection, the draw, the injection and the scoping, and
then found that the mechanism holding them could represent only one reboot and could not order anything.

**A second reboot while the first wait is running was lost.** `NoteRebooted` returned immediately
whenever an outstanding task already existed for that gateway, so a gateway that restarted again before
anything had been recreated armed nothing. The reference comparison that followed — which stopped an
older wait removing a newer one — could not help, because the newer one was never created. The recovery
owed to the second reboot simply did not happen, and the first request out of the door was sent into a
gateway that had been up for no time at all.

**Sharing one wait is not sending serially.** RFC 6886 section 3.7 does not stop at the delay: "After
that request is acknowledged by the gateway, the client may then send its second request, and so on…
The requests SHOULD be issued serially, one at a time; the client SHOULD NOT issue multiple concurrent
requests." Every caller awaited the same task, so the instant it completed all of them sent at once —
the synchronised burst the delay exists to break up, merely moved five seconds later.

### Resolution of the reopened paths

A reboot now arms a numbered obligation rather than a task. Each detected reboot increments the
generation and starts its own wait, so a second reboot is represented rather than discarded; a waiter
that reaches the end of an older wait re-reads the obligation, finds the newer generation and waits for
that too, so nothing held for the second reboot escapes through the door the first one opened, and only
the generation actually served is marked discharged.

Recreation then runs through a per-gateway gate that admits one request at a time and holds it until the
gateway has answered, which is what the section's "after that request is acknowledged" asks for. The
gate belongs to the recovery, not to the provider: it exists only for a gateway with an outstanding or
in-flight recovery, is dropped once the obligation is discharged and nothing is using it, and never
spans two gateways — so ordinary renewal is unaffected and one router's recovery never delays another's.

Cancellation was preserved and extended. A caller cancelled during the wait still sends nothing and
still leaves the obligation standing; a caller cancelled while queued for the gate releases no permit it
never took, leaves the gate usable, and marks nothing discharged.

### Reopened again — the obligation was discharged before anything was sent

The final review of `ee7aae0` confirmed the generation model and the serialisation, and then found that
the moment an obligation was considered served had nothing to do with a request going out. Recovery
waited, marked the generation satisfied, returned, and only then did the caller send — three separate
steps with the protocol state committed at the first of them.

**A caller that never sent could discharge the obligation.** Anything between the mark and the send —
cancellation, an exception, a failure to reach the transport at all — left the reboot recorded as
recovered while not one datagram had left. The next recreation then sent immediately, having waited for
nothing, which is exactly the un-delayed re-registration RFC 6886 section 3.7 exists to prevent.

**A reboot recorded in that gap was stepped over.** A second reset detected after the mark and before
the send arrived at a state that already said the gateway was recovered, so the request went out without
ever observing it. The generation model was right; the moment it was consulted was not.

### Resolution of the second reopened path

Recovery no longer hands the caller a decision and trusts it to send afterwards. `RecreateAsync` hands
the operation a `Dispatch`, the provider puts every datagram of a recreation through it, and the last
check of the obligation and the instant the request goes out happen inside the one lock `NoteRebooted`
also takes — the exchange is *started* there and awaited outside, so no thread blocks and no monitor is
held across a network response. A reboot recorded before that instant cannot be stepped over, because it
is either already visible to the check or recorded after the request began; a reboot recorded after it
is a new event, left standing for the next recreation, which is the most any client can do about a
gateway that restarts mid-request.

A generation is therefore consumed at the wire and nowhere else. RFC 6886 section 3.7 states the
requirement as a condition on sending — "delay … and then send its first port mapping request" — so
transmission discharges it: a request that goes out and is then refused or never answered has been
delayed exactly as asked, and delaying the next one again would answer a requirement the RFC does not
make. Everything short of transmission leaves the obligation standing. What a failed request means for
the mapping is decided separately, by the outcome and epoch rules that own it.

Only the first datagram of a recreation is held. Its retransmissions and the withdrawal of a substitute
port belong to a request already under way, and holding those for a reboot recorded behind them would
stall that request rather than answer the reboot — which the next recreation does.

### Reopened a third time — starting a send is not sending one

The review of `cecc55e` accepted the generation model and the atomic check, and then read the transport.
`UdpGatewayDatagramChannel.ExchangeAsync` opens a socket, awaits `UdpClient.SendAsync`, and only then
waits for a reply. The recovery marked the generation served the moment it *invoked* that method — while
the returned operation was still outstanding.

So everything the send could still do wrong happened after the obligation had already been discharged.
A socket that could not be opened, a send that failed, a caller that gave up while the datagram was
still going out: the reboot was recorded as recovered though nothing had reached the gateway, and the
next recreation went out with no delay at all. The same window swallowed a reset recorded between the
invocation and the send completing.

RFC 6886 section 3.7 asks for a delay before a request is *sent*. Method invocation is not a send.

### Resolution of the third reopened path

`IGatewayDatagramChannel.ExchangeAsync` now takes an `onSent` callback, and the UDP channel invokes it
on the line after `await client.SendAsync(...)` returns — the point at which the platform reports the
datagram handed to the socket, which is the strongest confirmation UDP offers, since it acknowledges
nothing. Everything that could have stopped it leaves by throwing before that line, so the callback runs
only when a datagram genuinely went out, and it runs whether or not a reply ever arrives: confirmation
is about the send, and the reply is a separate question with a separate answer.

The two moments are now distinct and both are kept. The dispatch still makes its last check and starts
the send inside the one lock `NoteRebooted` also takes, and *captures* the generation there instead of
consuming it. The confirmation marks that captured generation served and never "whatever is current",
so a reboot recorded while the datagram was on its way survives for the next recreation. Until a
confirmation arrives the operation is not treated as under way either, so its own retransmission is
checked against the obligation again rather than waved through.

PCP passes no callback: it has no reboot obligation of its own, because RFC 6887 section 14's rapid
recovery is deliberately unimplemented.

### Reopened a fourth time — completion did not prove the complete datagram

The final deployment review of `ac889e0` found that the production UDP channel awaited
`UdpClient.SendAsync` but discarded its returned byte count. It invoked `onSent` whenever the operation
completed, without requiring that the socket had accepted every byte in the payload. A successful short
result could therefore consume the captured reboot generation even though the complete NAT-PMP datagram
had not been confirmed.

### Resolution of the fourth reopened path

`UdpGatewayDatagramChannel` now compares the returned byte count with `payload.Length` and invokes
`onSent` only when they are equal. A mismatch returns the channel's existing failed-exchange result, so
the provider retries and, if every attempt fails, reports no mapping success. The captured reboot
generation remains pending until a later full-length send confirms it.

Because an OS UDP socket does not provide a deterministic way to force a successful short datagram,
the byte-count decision is the smallest internal production seam. The real socket path calls that seam;
its regressions prove full-length confirmation exactly once, short-result rejection without confirmation,
and a short result leaving recovery pending for a later qualifying full send. Existing loopback tests
continue to exercise the real UDP channel for successful send, reply timeout, and pre-send cancellation.

---

## CP-2026-014 — An integration fixture assumes TCP 25565 is free

| Field | Value |
|---|---|
| Date | 2026-08-09 |
| Starting branch | `fix/external-reachability-identity-races` |
| Starting commit | `f05c6b5` |
| Severity | Medium — a test defect only; no production behaviour is affected |
| Impact | The full integration suite cannot pass on any machine that is running a Minecraft server, which is most of the machines this product is developed on |
| Area | `tests/ChunkPilot.IntegrationTests/AgentReconnectIntegrationTests` |
| Status | **Fixed** |
| Reported by | Independent read-only deployment re-review of `f05c6b5` |
| Workaround | Free TCP 25565 before running the suite |
| Fixed branch | `fix/router-mapping-continuity-proof` |
| Fixed commit | `Fix router mapping continuity proof`, the single local commit on this branch |
| Regression | `TestPortAllocatorTests`, four cases |
| Validation | Reproduced and fixed against an isolated fake listener holding TCP 25565: the fixture fails with the old default and passes with the reservation, while the listener is still up |

### Reproduction

1. Have anything listening on TCP 25565.
2. Run `AgentReconnectIntegrationTests`.

Observed: `Lost_UI_session_keeps_server_running_and_relaunch_reports_recovery` fails. The agent is
right — it reports that another local process is holding the port and refuses to start the fake server
— and the failure says nothing at all about the agent behaviour the test exists to prove.

### Root cause

`CreateStoredFakeServerAsync` took `int port = 25565`. Two of the fixture's tests used that default, so
the fixture's port was the single most contended port number on any machine that runs Minecraft.

### Resolution

The parameter is now required and every caller reserves a port through `TestPortAllocator`, which asks
the kernel for a free ephemeral port, refuses to hand out 25565, records everything already issued so
two fixtures in one process can never collide, and re-probes the candidate so a port taken in between
is discarded rather than returned. Production code is unchanged: the agent's port-in-use check is the
behaviour that caught this and it was left exactly as it was. The other integration files already
allocate dynamically and were deliberately left alone.

---

## CP-2026-013 — An external reachability result for one server can be displayed under another

| Field | Value |
|---|---|
| Date | 2026-08-08 |
| Starting branch | `feature/external-reachability-verification` |
| Starting commit | `cf80e5d` |
| Severity | High — a verified public endpoint belonging to one server can be shown, and copied, from a different server's workspace |
| Impact | Direct contradiction of the milestone's truthfulness boundary: the address a person copies to give to a friend could belong to a server they are not looking at |
| Area | Selected server › Overview › Connect › Direct internet › External access |
| Status | **Fixed** |
| Reported by | Independent read-only deployment review, verdict `EXTERNAL REACHABILITY REVIEW: NO-GO` |
| Roadmap task | External reachability verification |
| Workaround | Do not deploy the external probe service |
| Fixed branch | `fix/external-reachability-identity-races` |
| Fixed commit | `Fix external reachability identity races`, the final local commit on this branch |
| Regression | `ExternalReachabilitySelectionTests` — ten deterministic ViewModel tests that hold a check open and move the selection underneath it |
| Validation | Verified by temporarily reverting the guard: six of the ten fail without it and all pass with it |

### Reproduction

1. Select server A, with Direct internet set up and its router port open.
2. Press **Check from outside**.
3. While the check is still running, select server B.
4. Wait for A's answer to arrive.

Observed: `CheckExternalReachabilityAsync` captured A's id, awaited the Agent, and then assigned the
returned presentation state unconditionally. B's freshly loaded state was overwritten with A's, so A's
**Public access verified** endpoint appeared — and could be copied — in B's workspace.

Separately, `CancelExternalReachabilityAsync` sent the cancel request for whichever server happened to
be selected when the button was pressed, rather than the server that owned the running check.

### Resolution

Every App-side external reachability operation now carries the server it was issued for and the local
sequence it was issued under. An answer is published only if the Agent answered about that server, that
server is still the one on screen, and no newer operation has superseded it; otherwise it is discarded
in silence. A deliberate command supersedes background reads on publication, while a background read
never supersedes the command whose answer the user is waiting for. Cancel takes its target from the
authoritative state being displayed — the operation actually in flight — and does nothing when none is.

Tightened on `fix/router-mapping-continuity-proof`: the server check is now exact rather than
"matches, or carries no id at all". Every state the Agent produces names its server, so an empty id is
an unattributable payload rather than a wildcard, and admitting one would have let a malformed answer
overwrite a real server's visible reachability. Covered by three further
`ExternalReachabilitySelectionTests` cases.

---

## CP-2026-012 — An identical router mapping recreated after removal resurrects a stale verification

| Field | Value |
|---|---|
| Date | 2026-08-08 |
| Starting branch | `feature/external-reachability-verification` |
| Starting commit | `cf80e5d` |
| Severity | High — ChunkPilot can present **Public access verified** for a router mapping that was never checked |
| Impact | A point-in-time external result outlives the exact mapping it was gathered about, which is the one thing the result-lifetime model exists to prevent |
| Area | Selected server › Overview › Connect › Direct internet › External access; router mapping state |
| Status | **Fixed** |
| Reported by | Independent read-only deployment review, verdict `EXTERNAL REACHABILITY REVIEW: NO-GO` |
| Roadmap task | External reachability verification |
| Workaround | Do not deploy the external probe service |
| Fixed branch | `fix/external-reachability-identity-races` |
| Fixed commit | `Fix external reachability identity races`, the final local commit on this branch |
| Regression | Seven `ExternalReachabilityIntegrationTests` cases against a running server and a scripted gateway, plus policy-level identity tests |
| Validation | Verified by temporarily reverting the identity: four of the seven fail without it and all pass with it |

### Reproduction

1. Start a server with Direct internet on and the router mapping active.
2. Press **Check from outside** and reach **Public access verified**.
3. Withdraw the mapping and have the router establish it again on identical terms — same mechanism,
   transport, internal client, internal port, external port and public address. The server keeps
   running throughout, so its run identity is unchanged.

Observed: `ExternalReachabilityPolicy.DescribeMapping` composed the mapping identity from values alone.
The recreated mapping produced a byte-identical identity, endpoint equality held, and the result
gathered about the first mapping was still reported as current for the second. Classic ABA.

### Resolution

`RouterMappingState` now carries an opaque `MappingInstanceId` that names one continuous establishment
rather than describing a mapping. The Agent mints it while an entry is open and drops it the moment the
projected phase says none is, so an entry that is withdrawn can never lend its identity to the next
one. Renewing the same entry on unchanged terms keeps it, and reading the same live mapping never
manufactures a new one. Where the mechanism can read the router's table, a port the router reports as
free is treated as proof the previous entry is gone. `DescribeMapping` requires the instance and fails
closed without it, so evidence can never bind to a mapping ChunkPilot cannot tell apart from another.

---

## CP-2026-011 — An unrelated foreign allow suppresses exact Minecraft firewall setup

| Field | Value |
|---|---|
| Date | 2026-08-08 |
| Starting branch | `fix/windows-firewall-policy-read` |
| Starting commit | `ce26d59` |
| Severity | High — a random third-party allow could prevent the user from creating a deterministic ChunkPilot-owned exact rule |
| Impact | The real Public-profile approval and owned create/remove lifecycle were unreachable while an Adobe capability rule existed |
| Area | Selected server › Overview › Connect › Windows Firewall › foreign rule coverage |
| Status | **Fixed** |
| Fixed branch | `fix/firewall-foreign-rule-coverage` |
| Fixed commit | `Fix foreign firewall rule coverage`, the final local commit on this branch |
| Regression | Typed INetFwRule/2/3 reader coverage, 25-case semantic matrix, Adobe-style Public consent, exact/broad/unknown allow and block, ownership/removal regressions |
| Validation | Focused: 102 unit + 39 integration; full: 1181 unit + 200 integration, zero skipped; Release build 0 warnings/errors; six focused WPF renders; rebuilt live reader 633/633 rules |

### Verified real-machine evidence

The original acceptance run resolved the exact managed Java runtime, TCP 25566, Ethernet interface
index 16, `10.0.0.140`, gateway `10.0.0.1`, and the Public Windows profile. ChunkPilot nevertheless
reported `ExistingForeignAllow / Broader` owned by `ExistingWindowsRule`, named `Adobe Native Client`,
and withheld Public approval and the ChunkPilot-owned rule lifecycle.

A read-only COM probe found two Adobe rules. The relevant rule is enabled inbound allow, Any protocol,
Any port, Any application, Domain/Private/Public, all addresses and interface types. Its
`LocalUserOwner` is populated with the user's SID and `EdgeTraversalOptions` is 1. The outbound twin
has the same owner constraint. `LocalAppPackageId`, all three authorized user/machine lists, and
`SecureFlags` are empty/zero. The rebuilt reader enumerated all 633 rules currently present with no
unavailable policy or per-rule field and classified the inbound Adobe rule `UnknownOrUnsupported`;
the earlier baseline probe had enumerated 631 rules. Neither probe changed a rule.

### Root cause

`WindowsFirewallPolicy.FindCoveringAllowRule` delegated to one boolean `Applies` predicate. That
predicate checked enabled/inbound, service, protocol, local port, application, profiles, remote port,
local/remote address, and interface type. It did not read or evaluate `Interfaces`,
`IcmpTypesAndCodes`, `EdgeTraversalOptions`, `LocalAppPackageId`, `LocalUserOwner`, authorized local or
remote user lists, authorized remote-machine lists, or `SecureFlags`; it also ignored the already-read
`EdgeTraversal` value. Any Program/Port/Protocol rule passing that incomplete subset was returned as a
covering allow, and the coordinator converted a non-exact match to `ExistingForeignAllow / Broader`.
That was the exact false-positive path for the Adobe rule.

### Correction and regression evidence

The rule snapshot now retains all documented INetFwRule, INetFwRule2, and INetFwRule3 match dimensions
plus per-property availability. A pure semantic evaluator classifies rules as exact equivalent, broad
unrestricted, constrained match, constrained non-match, unknown/unsupported, or non-match. Only a
foreign `ExactEquivalent` allow can suppress setup. Broad, user-owned, packaged/AppContainer,
security-conditioned, and incompletely understood allows remain untouched and appear only as optional
technical evidence; the normal exact ChunkPilot setup remains available.

Known applicable blocks still win over allows. A block clearly excluding the Java target is ignored;
an unknown potentially applicable block prevents a false ready claim and is reported as unverified.
Owned identity still requires the persisted stable ID, exact name, ChunkPilot group, matching
description, and live postcondition. Removing an owned rule leaves every foreign rule untouched.

---

## CP-2026-010 — A valid Windows Firewall policy is collapsed to `ReadFailed`

| Field | Value |
|---|---|
| Date | 2026-08-08 |
| Starting branch | `feature/firewall-compatibility-diagnostics` |
| Starting commit | `7778e0a` |
| Severity | High — a readable enabled Public profile was reported unavailable and its approval workflow was unreachable |
| Impact | `Check again` discarded trustworthy firewall and server evidence and could not offer the existing Public-profile consent action |
| Area | Selected server › Overview › Connect › Windows Firewall › policy read |
| Status | **Fixed** |
| Fixed branch | `fix/windows-firewall-policy-read` |
| Checkpoint | `065322d` (`WIP checkpoint Windows Firewall policy read correction`) |
| Fixed commit | `Fix Windows Firewall policy read`, the final local commit on this branch |
| Regression | `NetFwPolicyReaderTests` plus partial-policy coordinator and presentation coverage |
| Validation | 85 focused unit + 36 focused integration; 1143 full unit + 197 full integration; Release build 0 warnings/errors; three focused WPF renders |

### Verified evidence

On the affected non-elevated Windows session, BFE and MpsSvc were running and every firewall profile
was enabled. `HNetCfg.FwPolicy2` activation, `CurrentProfileTypes` (Public / 4), and
`FirewallEnabled(Public)` succeeded outside ChunkPilot. The same read-only reflection path used by
ChunkPilot also successfully read all three enabled flags, all three `BlockAllInboundTraffic` flags,
`LocalPolicyModifyState`, the rule collection, and its count. `_NewEnum` returned
`System.Runtime.InteropServices.CustomMarshalers.EnumeratorViewOfEnumVariant`.

ChunkPilot nevertheless projected `FirewallPlatformUnavailable / ReadFailed`, with profile firewall
and local policy unknown, so Public approval could not be reached even though TCP 25566, the exact
managed Java executable, Ethernet/index 16, `10.0.0.140`, gateway `10.0.0.1`, and Public were known.

### Root cause

`INetFwRules::_NewEnum` succeeded. Modern .NET projected its native `IEnumVARIANT` as an
`EnumeratorViewOfEnumVariant` implementing `System.Collections.IEnumerator`. `NetFwPolicyReader`
incorrectly required the returned managed wrapper itself to implement the raw
`ComTypes.IEnumVARIANT` interface. The cast returned null, rule enumeration was declared incomplete,
and the all-or-nothing reader converted that later failure into a platform-wide `ReadFailed`.

PowerShell succeeded because ordinary PowerShell enumeration consumes the CLR `IEnumerator`
projection; it did not make the invalid raw-interface cast. This was a COM marshaling/projection
assumption, not another incorrect manually declared COM interface signature.

The identical invalid cast also existed in the elevated helper's pre-mutation ownership read. It was
corrected to the same managed enumeration contract; no privilege boundary or mutation surface changed.

### Resolution

The policy reader now consumes the CLR `IEnumerator` returned by `_NewEnum`. COM activation remains
the platform boundary, while `CurrentProfileTypes`, per-profile `FirewallEnabled`, per-profile
`BlockAllInboundTraffic`, `LocalPolicyModifyState`, and rule enumeration are captured independently.
A later failure retains every earlier proven value and reports the exact typed unavailable field.

Creation and update still fail closed unless the current profile, that exact profile's enabled and
block-all state, local modify state, and complete rule enumeration are all known. Removal and any
claim that an owned rule is absent require complete rule enumeration. Thus a partial read can no
longer falsely make the whole platform unavailable, but it can never authorize an unsafe mutation.

### Regression and real-machine evidence

Deterministic reader tests cover successful Public policy, genuine activation failure, and independent
failure of CurrentProfileTypes, FirewallEnabled, LocalPolicyModifyState, BlockAllInboundTraffic, and
rule enumeration. Coordinator fixtures retain the exact Java runtime, TCP 25566, Ethernet/index 16,
local address, gateway, and Public profile for each partial-policy failure while refusing mutation.
Public, Private, Domain/managed policy, disabled firewall, CP-2026-009 correlation, and VPN/WinTUN
selection remain covered.

After the user disabled Smart App Control, `testhost` loaded the newly rebuilt assemblies normally.
The compiled `NetFwPolicyReader` then completed a read-only real-machine pass: platform Available,
Public active, Domain/Private/Public enabled, no block-all-inbound profile, local modify state OK,
631 rules read, no unavailable field, and complete Public-profile mutation evidence.

The focused WPF review rendered only complete Public policy, partial Rules failure, and genuine
platform-unavailable states at 1000x700. The partial state retains server/network evidence but offers
only Check again/View details; only the complete Public state offers Public approval. No render or
probe invoked the elevated helper or changed Windows Firewall, the network profile, adapters, routes,
services, Smart App Control, Defender, or the router.

---

## CP-2026-009 — Windows Public profile is lost for the routed Ethernet adapter

| Field | Value |
|---|---|
| Date | 2026-08-08 |
| Branch | `feature/friend-connectivity-windows-firewall` |
| Commit | `ab6da80` (observed) |
| Severity | High — a valid Windows profile was reported as unavailable and the consent path could not be reached |
| Area | Selected server › Overview › Connect › Windows Firewall |
| Status | **Fixed** |
| Fixed branch | `fix/windows-firewall-profile-target-resolution` |
| Fixed commit | `Fix Windows Firewall network target resolution`, the single commit on that branch |
| Validation | Real read-only NLM reproduction; 1101 unit + 186 integration tests; Release build 0 warnings/errors; six focused WPF renders |

### Reproduction

Run a managed server on TCP 25566 on the real routed Ethernet interface. ChunkPilot correctly shows
`10.0.0.140:25566`, while Windows reports Ethernet interface index 16, gateway `10.0.0.1`, and the
`NootsBoots` connection profile as Public on that same index. Press **Check again**.

Observed: ChunkPilot reported **Windows Firewall unavailable**, local port 0, blank Java executable,
profile None, and no network. The Public-profile approval flow was unreachable even though Windows had
provided a single exact physical adapter/profile correlation.

### Root cause

`NetworkCategoryView` asked Network List Manager for `IEnumNetworkConnections`, then reflected its
automation `_NewEnum` property and cast the returned .NET automation wrapper to `IEnumVARIANT`. On the
real machine that object is `EnumeratorViewOfEnumVariant`, so the cast failed and the view returned an
empty collection. Public itself was not rejected; no profile ever reached the Public policy branch.

`WindowsFirewallTargetResolver` also returned a blank all-or-nothing failure as soon as any target
layer failed. The coordinator consequently projected an independently known managed Java path and
port 25566 as blank and 0. Those values were secondary casualties of the network failure, not separate
runtime or port discovery failures on the accepted server.

### Resolution

The NLM view now uses strongly typed documented COM interfaces and resolves each NLM adapter GUID to
its Windows IP interface index. The selector first retains the existing physical/routed LAN policy,
then requires one connected known-category binding matching either that positive interface index or
the exact normalized adapter GUID. Multiple matches fail closed. Alias text is never identity.

Target resolution now evaluates Java, port, and profile independently and retains every authoritative
field when another field is missing. The coordinator exposes specific Java, port, or network-profile
states. A resolved Public profile remains a non-error result and enters the separate explicit Public
approval path; no rule is created before that approval and UAC.

### Regression evidence

Coverage reproduces Ethernet/index 16/Public with `10.0.0.140` and `10.0.0.1`, adds WinTUN/index 51
and link-local adapters without changing the winner, proves alias-independent index correlation,
rejects unmatched, ambiguous, and VPN-only topologies, and verifies partial Java/port/profile evidence
through the coordinator and ViewModel. Router tests remain unchanged and are rerun as regression
coverage. No test or diagnostic mutates Windows Firewall or the router.

### Roadmap task

None outstanding.

---

## CP-2026-008 — A stopped server keeps reporting "Router port is open"

| Field | Value |
|---|---|
| Date | 2026-08-08 |
| Branch | `fix/real-router-upnp-mapping` |
| Commit | `8f8cfa4` (observed) |
| Severity | High — the interface asserted an open inbound port for a server that was not running |
| Area | Selected server › Overview › Connect › Direct internet |
| Status | **Fixed** |
| Fixed branch | `fix/router-mapping-stop-cleanup` |
| Fixed commit | `Fix router mapping cleanup on server stop`, the single commit on that branch |
| Validation | 1056 unit + 161 integration tests; rendered `stopped-configured` and `cleanup-failed` |

### Reproduction

On a clean ChunkPilot-owned port, with the owner's pre-existing manual 25565 forward excluded as a
confounder:

1. Set up Direct internet for a server on port 25566 and start it.
2. The router accepts: `UPnP AddPortMapping accepted TCP 25566 to 10.0.0.140:25566 for 3600 seconds.`
3. Press **Stop**.

Observed: Minecraft stops and `Get-NetTCPConnection -LocalPort 25566` returns nothing, but the card
still reads **Router port is open** with the mechanism, internal endpoint, external port, the original
lease expiry and the original AddPortMapping result all presented as current.

### Evidence

The Agent's Stop handler did call `SynchronizeAsync`, which withdrew the mapping and persisted the
result, so the exposure itself was closed. What never happened was the App asking again:
`LoadRouterMappingAsync` ran only from `LoadServerDetailsAsync`, which is guarded by
`detailsServerId != value.Definition.Id` and therefore only on a change of selected server. The
one-second `RefreshAsync` re-read the dashboard and never the mapping, so the card kept the state it
had been handed when the port was opened.

### Impact

Stale, and stale in the most dangerous direction: an interface asserting an open inbound port for a
server that is not running. Two design faults sat behind it. Durable intent and the live mapping had no
distinct resting state, so "configured" had nowhere to render except as the previous phase; and a
stopped server's mapping was withdrawn on a 90-second in-memory grace rather than on the lifecycle
itself, which also meant a crash held the port open for up to a minute and a half and an Agent restart
began the grace again from zero.

### Workaround

Switching to another server and back re-read the state and showed the truth.

### Resolution

`RouterMappingPhase.Inactive` gives "set up, nothing open" a state of its own, distinct from both an
active mapping and a server that was never configured. Stop and restart are now told apart by
`ManagedServer.IsRestartInProgress` — the recorded lifecycle intent paired with the operation still
holding the server's gate — instead of by elapsed time, so a stopped server loses its exposure on the
first reconciliation and a failed restart cannot hold one open indefinitely. The App re-reads the
mapping after every lifecycle command and on the periodic refresh while Direct internet is the selected
method, with a sequence guard so a slower earlier read cannot resurrect a mapping that was removed.

A related consent defect was found and fixed in the same pass: cancelling setup left the recorded
intent enabled, so the next reconciliation would have opened the port the owner had just backed out of.

### Roadmap task

None outstanding.

---

## CP-2026-007 — Direct internet returns to "Not set up" after the router answers

| Field | Value |
|---|---|
| Date | 2026-08-08 |
| Branch | `feature/friend-connectivity-router-mapping` |
| Commit | `271075a` (observed) |
| Severity | Critical — the feature could not be reached at all, and no failure could ever be explained |
| Area | Selected server › Overview › Connect › Direct internet |
| Status | **Fixed** |
| Fixed branch | `fix/real-router-upnp-mapping` |
| Fixed commit | `Fix real-router automatic mapping failure`, the single commit on that branch |
| Validation | Deterministic reproduction at `271075a`; 1030 unit + 156 integration tests; rendered `router-answered` and `mapping-rejected` states |

### Reproduction

Real home router, real run:

1. Select **Direct internet** on Access and save.
2. Open **Overview** and press **Set up automatically**.
3. A brief checking state appears.

Observed: the card returns to **Not set up** with the setup button available again and no explanation.

### Evidence

From the user's Technical details immediately after the attempt:

```
Mechanism               None established
Gateway                 Not identified
Internal endpoint       Not established
External port           —
Address classification  Globally routable
UPnP urn:schemas-upnp-org:service:WANIPConnection:1 answered at
http://10.0.0.1:49152/upnp/control/WANIPConnection0 and reported external address 73.203.43.174.
```

The detail line is the UPnP provider's *supported*-discovery text, and the address classification can
only be set on the supported branch. SSDP discovery, the device description, control-URL resolution
and `GetExternalIPAddress` had therefore all succeeded. The screen contradicted its own evidence.

`AddPortMapping` was never called: the run stopped before the confirmation could be offered.

### Impact

`RouterMappingCoordinator.ResolvePhase` returned `Off` whenever `DirectInternetEnabled` was false,
before considering the result of the operation that had just run. Intent is only recorded by
`EnableAsync`, `EnableAsync` is only reachable through the confirmation, and the confirmation only
opens on a `Supported` result — so a successful check always projected as "Not set up" and the feature
was unreachable on every router. A *failed* check projected as "Not set up" too, so no router failure
of any kind could reach the user.

Two presentation defects made the diagnostic screen actively misleading: `DirectInternetGateway` and
`DirectInternetExternalPortLabel` had no `NotifyPropertyChangedFor` entry and kept their first-bound
values, and the router-reported address row rendered with an empty value beside a live Copy button
because the endpoint string required an external port that no mapping had yet produced.

### Workaround

None. No sequence of user actions could reach the confirmation.

### Resolution

A settled phase is now derived from durable evidence — removal pending, active mapping, last failure,
last successful check — and the runtime carries only "an operation is in flight". Cancellation is not
treated as a failure. Attempts that create nothing record what they learned (gateway, candidate LAN
address, mechanism that answered) without writing ownership evidence. The two missing notifications
were added and are held by a reflective test that fails if any property changes with the state without
announcing it.

### Roadmap task

None outstanding. Whether this particular router accepts `AddPortMapping` is still unknown and needs
one more real-router run; the code now reports either outcome truthfully.

---

## CP-2026-001 — Game rules cannot be established on Minecraft 26.x

| Field | Value |
|---|---|
| Date | 2026-07-30 |
| Branch | `feature/vanilla-workspace-stabilization` |
| Commit | `c4b49c9` (observed), fixed presentation shipped in the stabilization commit |
| Severity | Medium — a feature is unavailable, nothing is unsafe and nothing is misreported |
| Area | Settings › Game rules |
| Status | **Open** |

### Reproduction

1. Create a Vanilla server on Minecraft 26.2 and start it.
2. Open **Settings › Game rules**.

Observed: the card reports that the server did not accept any of the game rules ChunkPilot knows, and
offers no controls.

### Evidence

The server rejects every rule name in `GamerulePolicy`, including the *set* form. From the server's own
log during an isolated review run:

```
[21:49:26] [Server thread/INFO]: Incorrect argument for command
[21:49:26] [Server thread/INFO]: gamerule keepInventory<--[HERE]
```

`gamerule`, `gamerule keepInventory`, `gamerule keepInventory true` and `gamerule query keepInventory`
all fail the same way, so this is not a query-syntax problem: Minecraft 26.x does not recognise the
rule names ChunkPilot carries. Earlier versions (1.x) answer normally, and the integration fixtures
cover both the answering and the refusing server.

### Impact

On Minecraft 26.x the Game rules card lists nothing and explains why. No control lies about a value, no
command is sent that would fail, and every other Settings control is unaffected. On 1.x versions the
card works: values are read from the running server and changes are applied and re-read.

### Workaround

Type the rule directly in **Console** using the names that version uses.

### Roadmap task

Discover rules from the world instead of from a static list: read the `GameRules` compound from
`level.dat` (NBT, gzip) while the server is stopped or immediately after a confirmed save, which yields
the exact rule names and values for *any* version. Treat the current list as a label-and-description
lookup rather than as the source of which rules exist. Belongs with the 1.5 Safety Lab work, where
world-file reading already has a home.

---

## CP-2026-002 — Create Server blocking reason is below the full version list

| Field | Value |
|---|---|
| Date | 2026-08-07 |
| Branch | `feature/vanilla-workspace-premium-polish` |
| Commit | `a0af4164d1235c6527304f4570efecf19725b01e` |
| Severity | High — a valid recovery instruction is not visible when the primary action becomes unavailable |
| Area | Create Server v2 › Vanilla setup |
| Status | **Fixed** |
| Fixed branch | `feature/create-overview-audit-remediation` |
| Fixed commit | `faee7d28eabca6efb654d613411663f7492d7334` |
| Validation | Full Release suite: 783 unit + 102 integration tests; `CS-57` through `CS-59` rendered re-audit |

### Reproduction

1. Create a disposable Vanilla server.
2. Begin a second creation with the same name, or choose a non-empty destination.
3. Remain at the top of **Vanilla setup**.

Observed: **Next** is disabled, but the visible Name and Location cards show no reason. The alert that
explains the collision is rendered after the complete Minecraft version list and Selection card; at
1120×780 it is roughly 800 pixels below the initial viewport. The duplicate-name alert itself is
accurate once found.

### Impact

The workflow looks inert at the exact moment the user needs recovery guidance. Duplicate name,
invalid filesystem name and destination collision need distinct inline explanations beside the field
or location that must change; the page-level summary can remain supplemental.

### Resolution

Name and destination-policy failures now render in danger treatment beside the field or Location card
that must change. Destination failures no longer repeat below the full version list.

---

## CP-2026-003 — Create Server carries the previous step's scroll offset into Review

| Field | Value |
|---|---|
| Date | 2026-08-07 |
| Branch | `feature/vanilla-workspace-premium-polish` |
| Commit | `a0af4164d1235c6527304f4570efecf19725b01e` |
| Severity | Medium — the next step opens partway through its content and hides its heading and summary |
| Area | Create Server v2 › Setup to Review navigation |
| Status | **Fixed** |
| Fixed branch | `feature/create-overview-audit-remediation` |
| Fixed commit | `faee7d28eabca6efb654d613411663f7492d7334` |
| Validation | Full Release suite: 783 unit + 102 integration tests; `CS-35` rendered after entering Review from Setup's bottom |

### Reproduction

1. On **Vanilla setup**, scroll down through the version list and Selection card.
2. Press **Next**.

Observed: **Review** retains the Setup page's vertical offset. Its title and the first review sections
open above the viewport. `Ctrl+Home` reveals the intended top state.

### Impact

Step navigation lacks a stable beginning, harms scanning and can make Review appear incomplete. Each
step transition should reset the shared page scroll viewer to the top before moving keyboard focus.

### Resolution

Every step transition resets the shared scroller. Review receives programmatic focus on its non-tab-stop
heading, preventing initial focus from scrolling the EULA back into view after the reset.

---

## CP-2026-004 — Overview can label a VPN address as the local network address

| Field | Value |
|---|---|
| Date | 2026-08-07 |
| Branch | `feature/vanilla-workspace-premium-polish` |
| Commit | `a0af4164d1235c6527304f4570efecf19725b01e` |
| Severity | High — connection guidance presents a non-LAN address under a LAN label |
| Area | Selected server › Overview › Connection |
| Status | **Fixed** |
| Fixed branch | `feature/create-overview-audit-remediation` |
| Fixed commit | `faee7d28eabca6efb654d613411663f7492d7334` |
| Validation | Full Release suite: 783 unit + 102 integration tests; selector unit matrix; `OV-19`, `OV-22`, `OV-27` rendered re-audit |

### Reproduction

1. Run ChunkPilot on a machine with an active ExpressVPN WinTUN adapter and ordinary local adapters.
2. Select a server and inspect **Connection › Local network**.

Observed: Overview displayed `100.64.100.6:25566`. Windows reported `100.64.100.6/32` belongs to
`ExpressVPNOpenVPN WinTUN Adapter`, not the physical LAN. The current adapter selection excludes the
`Tunnel` enum type but ranks remaining active adapters by link speed, which does not exclude WinTUN.

### Impact

Copying this address to a household player is unlikely to provide LAN connectivity and contradicts
the label. Adapter selection should prefer routable private-subnet addresses on physical/default-route
interfaces, explicitly identify VPN candidates, or report that a LAN address could not be established.

### Resolution

LAN selection now accepts only RFC1918 IPv4 or IPv6 ULA addresses from physical Ethernet or Wi-Fi
interfaces, excludes known VPN/virtual adapter markers, and prefers the effective OS route. Ambiguous
interfaces without route evidence produce no asserted LAN address.

---

## CP-2026-005 — A specific port-bind failure is replaced by a generic exit message

| Field | Value |
|---|---|
| Date | 2026-08-07 |
| Branch | `feature/vanilla-workspace-premium-polish` |
| Commit | `a0af4164d1235c6527304f4570efecf19725b01e` |
| Severity | High — startup fails without exposing the actionable cause already detected by the Agent |
| Area | Selected server › Overview › startup failure |
| Status | **Fixed** |
| Fixed branch | `feature/create-overview-audit-remediation` |
| Fixed commit | `faee7d28eabca6efb654d613411663f7492d7334` |
| Validation | Full Release suite: 783 unit + 102 integration tests; 17 ManagedServer lifecycle tests; real occupied-port `OV-14` through `OV-16` |

### Reproduction

1. Start a disposable server while its configured port is already occupied.
2. Wait for startup to exit.

Observed: the server becomes **Crashed** and Overview reports `Server exited during startup with code
0.` The captured console proves `FAILED TO BIND TO PORT` and `Address already in use`; the Agent first
records `Port 25565 appears to be in use` but its monitor then replaces that detail with the generic
exit message.

### Impact

The user is sent toward logs instead of the direct recovery action: stop the conflicting service or
choose another port. Preserve the more specific detected failure, surface it on Overview and avoid
making exit code 0 the primary explanation for an unsuccessful startup.

### Resolution

Startup failures now retain the strongest detected evidence across output pumps and process-exit races.
Port bind, out-of-memory, known runtime/configuration, and generic failures have explicit precedence;
the rendered result gives the port and recovery action even when the process exits with code 0.

---

## CP-2026-006 — Running performance charts mix samples from separate process attempts

| Field | Value |
|---|---|
| Date | 2026-08-07 |
| Branch | `feature/vanilla-workspace-premium-polish` |
| Commit | `a0af4164d1235c6527304f4570efecf19725b01e` |
| Severity | Medium — chart window and averages do not describe the current run |
| Area | Selected server › Overview › CPU and memory performance |
| Status | **Fixed** |
| Fixed branch | `feature/create-overview-audit-remediation` |
| Fixed commit | `faee7d28eabca6efb654d613411663f7492d7334` |
| Validation | Full Release suite: 783 unit + 102 integration tests; current-attempt integration test; real `OV-22` through `OV-27` metrics |

### Reproduction

1. Start a server and let the process exit during startup.
2. Correct the startup condition and start it again.
3. Compare current uptime with the chart sample-window caption.

Observed: at 23 seconds of current uptime, the charts claimed `14 real samples over 6 min 58 sec`;
at 57 seconds they claimed `31 real samples over 7 min 33 sec`. `ManagedServer` retains its in-memory
sample list across process starts while `StartedAt` and uptime reset.

### Impact

Current, average and peak values mix separate process attempts and the time-window caption is
impossible relative to the displayed uptime. Either reset runtime samples for each process identity or
label and visualize the data explicitly as multi-run history.

### Resolution

Successful fresh launches clear the bounded sample window and reset per-PID CPU/network baselines.
Attempt-scoped readiness and pump tasks also prevent a previous monitor from contaminating a later
launch. In the rendered run, every sample timestamp was at or after the current `StartedAt` value.
