# Networking and sharing

ChunkPilot keeps five independent truths separate:

- **Firewall permission**: an exact Windows rule permits traffic for one executable, port, protocol,
  and profile. It does not prove a process is listening.
- **Local listener**: the exact managed server process owns its local port. It does not prove LAN or
  internet reachability.
- **LAN reachability**: a private address may work for devices on the same home network. It is not a
  public address.
- **Public route**: a current public-connectivity lease owns an exact router mapping or future tunnel.
  A route alone is not proof that a friend can connect.
- **External verification**: one independently checked server run, mapping instance, endpoint, and lease
  was reachable at one moment. It becomes stale as soon as any identity changes.

The WebUI **Share** dialog never substitutes the LAN value for the public value. Copying a public address is rejected unless it was independently confirmed, and a public value equal to the LAN value is rejected.

Modes:

- **This computer only**: no router/firewall change.
- **Home network**: use LAN IPv4 and allow a scoped firewall rule only if wanted.
- **Direct internet** (port forwarding): Java normally uses TCP; Bedrock normally uses UDP. ChunkPilot can ask the router to forward the server's own port to this computer, on request and after confirmation — see below. A local listening test is not proof of public reachability.
- **Official tunnel**: optional provider-controlled process and assigned address. ChunkPilot remains usable without it.
- **Configure later**: the server remains local.

A private/shared WAN address may indicate CGNAT; the safe next step is to ask the ISP or use a reviewed tunnel.

## WebUI presentation mapping

The server hero reduces the authoritative state to Local only, Available on your home network,
Setting up Internet access, Friends can join, Needs attention, Verification unavailable, or Server
must be running to verify. Overview shows one recommended address and a Manage connectivity action.
The Connectivity settings category keeps local, LAN, router-reported, and externally verified public
endpoints separate and exposes the existing router, firewall, cancellation, removal, and outside-in
commands behind deliberate actions and confirmations. “Internet hosting” is the beginner-facing name
for the existing Direct internet/port-forwarding mode; it does not enable exposure when merely selected.

Create Server records only one of Local only, Home network, Internet hosting, or Configure later as a
next-step preference. It never creates a mapping, firewall rule, lease, or public claim.

---

## Direct internet: automatic router port mapping

### It is opt-in, per server, and never automatic

ChunkPilot does not create a router mapping because a server started, because Overview was opened,
because the UI reconnected, or because the Agent restarted. Two separate, deliberate user actions are
required for one specific server:

1. Choose **Direct internet** as the connection method. This only reveals the setup experience.
2. Read the confirmation and accept it. This is the first and only action that changes a router.

The default for every server, including every server that already existed before this feature, is off.
Consent is recorded per server, is never inherited by another server, and is forgotten when Direct
internet is turned off — re-enabling asks again.

### What ChunkPilot implements

| Mechanism | Specification | Used for |
|---|---|---|
| PCP version 2 | RFC 6887 | ANNOUNCE (opcode 0) as a read-only capability check; MAP (opcode 1) to create, renew and delete |
| NAT-PMP | RFC 6886 | External address request (opcode 0) as a read-only capability check; TCP/UDP mapping requests (opcodes 2 and 1) to create, renew and delete |
| UPnP IGD | UPnP Forum *WANIPConnection:1 Service Template v1.01*, and the SSDP and SOAP control rules of the *UPnP Device Architecture* | `GetExternalIPAddress`, `GetSpecificPortMappingEntry`, `AddPortMapping`, `DeletePortMapping` |

### What ChunkPilot does not implement

- PCP: the PEER opcode, PCP options (THIRD_PARTY, PREFER_FAILURE, FILTER), authentication, multicast
  ANNOUNCE listening, and epoch-based rapid recovery. Mapping loss after a router restart is recovered
  by periodic renewal instead.
- NAT-PMP: unsolicited multicast address-change announcements.
- UPnP: eventing (GENA), the IGD:2 actions `AddAnyPortMapping`, `DeletePortMappingRange` and
  `GetListOfPortMappings`, `GetGenericPortMappingEntry` enumeration, IPv6 firewall control, and the
  M-POST control fallback.
- Neither RFC's unbounded retransmission schedule. RFC 6886 describes nine attempts over 64 seconds
  before concluding non-support, and RFC 6887 sets no maximum at all. ChunkPilot retransmits a bounded
  three times, and when nothing answers it reports that the router's capability **could not be
  confirmed** rather than asserting the router lacks the mechanism.
- IPv6 mappings. v1 maps IPv4 only.

### Mechanism selection

Mechanisms are probed **strictly in sequence** — PCP, then NAT-PMP, then UPnP IGD — and the first to
answer wins. Never in parallel: two mechanisms asking one router to forward one port at the same time
is how duplicate and orphaned mappings happen.

PCP and NAT-PMP are probed first because they share UDP port 5351, their checks are a single datagram,
and both checks are read-only by construction. A NAT-PMP-only gateway answers a PCP version 2 request
with `UNSUPP_VERSION`, so the fall-through is a defined outcome rather than a timeout. UPnP IGD is
last because SSDP plus two HTTP round trips is the most expensive check, even though it is the
mechanism most consumer routers actually enable.

Once a mechanism has established a mapping it is recorded, and renewal and removal use that mechanism
by name. A live mapping never changes mechanism underneath itself.

### Transport

Java Edition's game port is TCP, and that is the only transport v1 maps. A UDP mapping is never created
because a port number happens to be known. Transport is modelled explicitly end to end so future
Bedrock support (UDP) cannot be confused with Java.

### Port selection

The external port always equals the server's own authoritative port, which stays synchronised with
`server.properties`. ChunkPilot never silently substitutes a different public port: if the router
offers one — which both RFC 6886 and RFC 6887 allow when the requested port is taken — the substitute
is withdrawn and the situation is reported as the port being in use.

### Mapping ownership

ChunkPilot only removes or modifies a mapping it can prove it created. Ownership requires **all** of:

- persisted evidence that ChunkPilot created a mapping on that exact public port and transport;
- the router's entry pointing at this computer's recorded private address and internal port;
- a description matching ChunkPilot's own, when the router reports one at all.

Anything short of that is a conflict: the entry is left untouched and the state says so. Two protocol
properties reinforce this — a PCP delete carries the 96-bit mapping nonce ChunkPilot minted, so it is
structurally incapable of removing another application's mapping, and a NAT-PMP delete is scoped by
RFC 6886 to the requesting client's own internal port.

The description written into a router's table is the constant string `ChunkPilot Minecraft`. It names
the application and never the server, the user or the machine.

### Leases and renewal

ChunkPilot asks for a 3600-second lease — deliberately shorter than RFC 6886's recommended 7200 — so
that an unclean shutdown closes the exposure sooner. While the server is running and Direct internet is
on, the Agent renews once half the lease has passed, never more often than once a minute.

A router that answers UPnP error 725 (`OnlyPermanentLeasesSupported`) is retried once with a lease of 0
and the result is reported as **permanent**. That is a materially different state: the router will
never expire the entry, so closing it is entirely ChunkPilot's responsibility.

### Server lifecycle

| Event | Behaviour |
|---|---|
| Start, Direct internet off | No router action at all. |
| Start, current Direct internet lease | The recorded mapping is re-established or confirmed. Starting alone never creates a lease. |
| Running with a current lease | Finite router leases are renewed before they lapse. |
| Normal stop | The mapping is withdrawn as soon as the server settles. Direct internet stays set up. |
| Safe restart | The mapping is preserved, because the restart operation itself says so — not because a timer has not expired yet. |
| Crash, or a restart that failed | Withdrawn on the first reconciliation. Nothing waits. |
| Normal application exit | Public leases end immediately; verification is stale; every managed server is safely stopped; exact-owned routes are withdrawn under bounded cleanup; then the Agent exits. |
| Task Manager, taskkill, or unexpected UI death | Exact process loss triggers the same lease revocation, safe server stop, bounded route cleanup, and Agent exit. A pipe disconnect with the same UI still alive does nothing. |
| Minimize or tray | The UI process remains alive, so hosting and current leases continue. |
| Agent restart | No public lease is inherited. Old intent/mapping evidence is cleanup-only, associated managed listeners are stopped, and explicit enable is required again. |
| Hard power loss | Finite leases expire on their own. On restart ChunkPilot reconciles rather than assuming the old mapping still exists. |
| Server deletion | A provably owned mapping is closed first. A failure is reported and retained for retry, not hidden. |

### Public-connectivity lease and UI lifetime

The user-facing rule is:

> Close ChunkPilot to stop hosting safely. Minimize it to keep hosting.

The Agent mints one random, memory-only capability for the exact UI process. The identity is the Windows
process ID plus raw process-creation `FILETIME`, compared exactly with no tolerance or local-time
conversion. PID reuse therefore cannot inherit authority. Process observation preserves three distinct
answers: `Alive`, `Gone`, and `Unknown`. `Gone` acts promptly; `Unknown` has one bounded monotonic
deadline. Named-pipe connectivity and heartbeat age are not competing death policies, and one failed
observation/store pass cannot stop later passes.

A public-connectivity lease is independent per server and has its own ID and increasing generation. The
lease—not the window, Agent process, Minecraft process, firewall rule, or a database intent flag—owns
router renewal, future tunnel endpoints, exact public endpoint identity, and external checks/results.
Exposure-changing requests prove the current App capability, server, requested operation, and current
lease generation (plus run/mapping/endpoint identity where applicable) before any durable intent,
consent, router operation, renewal, or probe changes. A stale, replayed, missing, or wrong-session
generation is an authorization error, not a log-only warning. An old generation cannot alter a newer
one.

When the actual UI process ends, the Agent synchronously removes all lease authority, cancels router
renewal, and invalidates active and cached external verification before slow work begins. It then starts
two independent bounded paths: exact-owned route cleanup and the existing per-server world-safe shutdown
pipeline (native save/flush, graceful stop, process-tree and listener verification, and exact-owned
bounded escalation only if graceful stop fails). Router failure never holds a listener open; it remains
`RemovalPending` with ownership evidence, and nothing may recreate or renew it. Foreign or uncertain
router state is never deleted.

The Agent exits after exact managed processes are confirmed gone and cleanup succeeds or reaches that
truthful bounded pending state. If an exact managed process cannot be stopped or proved gone, the Agent
remains available and records the failure rather than falsely claiming terminal shutdown. No full backup
is created merely because the UI closed.

A new Agent has no capability or lease to inherit. Persisted `DirectInternetEnabled`, old consent, an old
mapping row, or a durable firewall rule cannot reopen exposure. Stale exact-owned router evidence is
cleanup-only; the associated managed listener is stopped; a new UI must explicitly enable Direct
internet to receive a fresh generation.

This policy does not promise that cleanup runs after simultaneous App-and-Agent termination, machine
power loss, or an unreachable router. In those cases finite router lease expiry and later ownership-safe
reconciliation are the truthful boundaries.

### How a state is decided

The Agent combines durable cleanup/evidence state with the current in-memory lease: a removal that
failed, a mapping it owns, the last failure, the last successful capability check, and whether this
Agent currently holds authority to expose that server. Cleanup evidence survives a screen refresh and
Agent restart; exposure authority never does. A failure stays visible until the user retries, turns
Direct internet off, or something authoritative changes.

Wanting Direct internet, holding a current public lease, and having a router port open are separate.
Intent is persisted for truthful cleanup/display, the in-memory lease is the only creation/renewal
authority, and the mapping is exact router evidence. A stopped server with a live UI may keep its lease
and intent while losing the physical mapping. UI or Agent death loses the lease and clears intent;
persisted remnants are cleanup-only. Only a mapping that exists right now may show a router lifetime,
an internal endpoint, or an address with a port on it.

Telling a stop apart from a restart is decided by the lifecycle operation, never by elapsed time. A
deliberate restart may keep its mapping while that restart is still running, which is answered by the
restart itself; the moment it ends, successfully or not, a server that is not running holds no mapping.

Answering a capability check is not the same as owning a mapping, and the two are recorded separately.
A check that succeeds notes the mechanism that answered, the gateway, and the private address a mapping
would forward to — none of which is ownership evidence, so none of it can make a mapping on the router
look like ChunkPilot's. Only a mapping that was actually created writes the fields that authorise a
later removal.

Cancelling an attempt is not a failure of the router. It creates nothing, records nothing, and leaves
the surface on the last settled truth.

### What ChunkPilot will and will not claim

A router accepting a mapping does **not** prove that a friend can connect. Nothing in the interface
says publicly reachable, connectable, or open to the internet on that evidence. A successful mapping is
described as the router port being open, and its tone is deliberately informational rather than
success.

When the router states its own WAN address, it is labelled **Router-reported address** and carries the
caveat that ChunkPilot has not verified it from outside. That address is classified locally:

| Class | Meaning |
|---|---|
| Globally routable | Nothing local contradicts it. Still not proof of reachability. |
| Private use (RFC 1918) | Another network layer appears to exist above the router. |
| Shared address space (RFC 6598, `100.64.0.0/10`) | The provider appears to use carrier-grade NAT. |
| Link-local, loopback, documentation, reserved | Not a usable internet address. |
| Not reported | The router said nothing readable. |

For the private and shared classes the interface says *your router appears to be behind another network
layer* — never that CGNAT is certain. The evidence itself is available under **Technical details**.

### Windows Firewall

ChunkPilot can read Windows Firewall policy without elevation and, only after a separate confirmation
and the normal Windows administrator prompt, create one narrow Java Edition rule. The rule is inbound,
enabled, allow, TCP, the server's exact authoritative port, its exact ChunkPilot-managed Java
executable, and one applicable authorized Windows profile. It does not enable the firewall, change
default policy or network category, create UDP access, or touch a foreign rule.

Public networks require an additional explicit approval. Domain or otherwise managed policy is
reported when a local rule would not take effect. Port, Java, or profile changes make an owned rule
stale until the owner explicitly updates it. Stop and Restart leave a valid rule in place; removing it
is a separate elevated action. See [Windows Firewall access](WINDOWS-FIREWALL-ACCESS.md) for the trust,
ownership, verification, and recovery boundaries.

Firewall profile selection follows the same trusted physical LAN path used by router mapping. Exact
adapter GUID/InterfaceIndex correlation prevents an active VPN, WinTUN, Hyper-V, VMware/VirtualBox,
link-local, or disconnected virtual adapter from replacing a proven Ethernet or Wi-Fi path. A read-only
diagnostic preserves independently known Java, port, address, gateway, interface, NLM, policy, and rule
evidence and shows one prioritized recovery action. It never changes adapter state, routes, services,
Group Policy, firewall defaults, or a Windows network category.

### External reachability

There is no automatic external probe. ChunkPilot does not contact a third-party "what is my IP" or
port-check service to decide whether Direct internet worked. None of the following is treated as
evidence of public reachability: a local socket connect, a Minecraft status response on localhost, a
LAN connection, a successful router mapping, or a router-reported WAN address.

A third layer, **External access**, sits below Router and Windows Firewall on Overview and is reached
only by pressing **Check from outside**. It asks an optional, stateless, account-free ChunkPilot
service — a Cloudflare Worker — to attempt one short TCP connection back to the address the request
arrived from. A remote vantage point is required because RFC 5382 requires TCP NATs to support
hairpinning, so a connection made from inside the LAN to the router's own external address can succeed
while the internet path is broken.

The service derives its connection target from the public source address Cloudflare observed, never
from an address the caller supplied; when the router-reported and observed addresses differ, no socket
is opened and the result is a typed source mismatch rather than a reachability failure. The feature is
optional in the strongest sense: there is no production endpoint compiled in, no account, no telemetry
and no stored request, and a build without a configured endpoint says external verification is
unavailable rather than failing.

A completed handshake is reported as **Public access verified** — *TCP 25566 answered from outside your
network* — and nothing more. It does not evidence version compatibility, authentication, whitelist
access, bans, mod compatibility or latency. A verified result is bound to the exact server run, router
mapping and public endpoint it was gathered about, and stops being current the moment any of those
change. See [External reachability probe](EXTERNAL-REACHABILITY-PROBE.md) for the API contract, the
anti-scanning boundary, the privacy model and the manual deployment steps.

### Known limitations

- Router support varies widely. Many consumer routers ship with automatic port forwarding disabled, and
  some implement it incorrectly. No universal compatibility is claimed.
- A router behind carrier-grade NAT cannot be made reachable by forwarding a port on it, however
  correctly the forwarding is configured.
- A router that answers nothing is reported as unconfirmed, not unsupported — ChunkPilot's retry window
  is deliberately short enough that a slow router could be missed.
- A verified Windows Firewall rule and router mapping still do not prove external reachability; only
  the deliberate external check does, and only for the endpoint and moment it was made.
- External verification is IPv4 only, matching the router-mapping path. A check that leaves over IPv6
  is reported as an address-family mismatch rather than as an unreachable server.
- No official probe endpoint is deployed yet, so external verification is unavailable in current
  builds unless a development endpoint is configured.
- Manual, guided router configuration is not implemented. The interface says so rather than pretending
  it exists.
- IPv6 port mapping is not implemented.
- Automatic mapping is tested against controlled local and in-process gateway fixtures, never against a
  live consumer router.

---

## Crossplay

Crossplay installation is capability-gated and requires a stopped compatible Paper/Purpur or Fabric server. ChunkPilot resolves Geyser and Floodgate through the official Geyser downloads API, resolves optional ViaVersion through its Modrinth release metadata, requires provider hashes, creates a conventional backup, rejects another owned Bedrock UDP port, and tracks the exact JARs it installs. Removal moves only those owned JARs to Recovery and preserves generated configuration.

Java uses its normal TCP address; Bedrock requires a distinct UDP port and address. Floodgate changes authentication to permit Bedrock identities and must be reviewed. After the first restart, review Geyser's generated configuration and run the connection checklist before sharing anything publicly. Official references: [Geyser downloads](https://geysermc.org/download/?project=other-projects), [Floodgate setup](https://geysermc.org/wiki/floodgate/setup/).

---

## Primary sources

Protocol behaviour in this document was verified against these, not against tutorials or copied
libraries:

- [RFC 6886 — NAT Port Mapping Protocol (NAT-PMP)](https://www.rfc-editor.org/rfc/rfc6886.txt)
- [RFC 6887 — Port Control Protocol (PCP)](https://www.rfc-editor.org/rfc/rfc6887.txt)
- [UPnP Forum — *WANIPConnection:1 Service Template Version 1.01*](https://upnp.org/specs/gw/UPnP-gw-WANIPConnection-v1-Service.pdf)
- [UPnP Forum — *UPnP Device Architecture 1.0*](https://upnp.org/specs/arch/UPnP-arch-DeviceArchitecture-v1.0.pdf) (SSDP discovery and SOAP control)
- [RFC 1918 — Address Allocation for Private Internets](https://www.rfc-editor.org/rfc/rfc1918.txt)
- [RFC 6598 — IANA-Reserved IPv4 Prefix for Shared Address Space](https://www.rfc-editor.org/rfc/rfc6598.txt)
- [RFC 5382 — NAT Behavioral Requirements for TCP](https://www.rfc-editor.org/rfc/rfc5382.txt) (hairpinning)
- [RFC 4787 — NAT Behavioral Requirements for Unicast UDP](https://www.rfc-editor.org/rfc/rfc4787.txt)
