# External reachability probe

ChunkPilot can answer one question that nothing on this computer can answer honestly:

> Can a connection from outside this home network reach this Minecraft server's public TCP endpoint?

The answer comes from an **optional**, **stateless**, **account-free** Cloudflare Worker that attempts
one short TCP connection back to the address a ChunkPilot request arrived from. The desktop
application is fully usable without it, no server-management operation depends on it, and it is never
contacted except when somebody presses **Check from outside**.

---

## Why a remote vantage point is required

A local socket cannot prove public reachability, and neither can connecting to the router's own WAN
address from inside the LAN.

NAT gateways are expected to support *hairpinning*: a packet sent from an internal host to the
external address and port of one of its own mappings is translated and delivered back inside. RFC 5382
makes this a requirement for TCP NATs (REQ-9: "A NAT MUST support hairpinning for TCP"), and RFC 4787
defines the same behaviour for UDP. That is exactly the property that makes a self-connect worthless
as evidence: it can succeed while the internet path is broken, because the connection never left the
building.

The only trustworthy answer therefore has to originate from a machine that is genuinely outside the
user's network. That is the whole reason this service exists, and it is the reason it does nothing
else.

Primary sources:

- [RFC 5382 — NAT Behavioral Requirements for TCP](https://www.rfc-editor.org/rfc/rfc5382.txt)
- [RFC 4787 — NAT Behavioral Requirements for Unicast UDP](https://www.rfc-editor.org/rfc/rfc4787.txt)

---

## What a successful check proves, and what it does not

A completed TCP handshake proves that **the public TCP network path reached the server's listening
port**. ChunkPilot says exactly that and stops:

> **Public access verified**
> TCP 25566 answered from outside your network.

It is not evidence of client/server version compatibility, authentication, whitelist access, bans, mod
compatibility, gameplay quality or latency. ChunkPilot never says every friend can join.

A failed handshake proves only that the probe could not establish TCP. ChunkPilot composes a
diagnosis from evidence it already holds — the router mapping, the Windows Firewall state, the address
classification — and offers possibilities rather than asserting a cause.

---

## The security invariant: never a public port scanner

The public API **must not** accept an arbitrary destination and connect to it.

The Worker derives the connection target from the public source address of the incoming HTTPS request,
which Cloudflare sets at its own edge (`CF-Connecting-IP`). A request that reaches a Worker has, by
construction, passed through Cloudflare, so there is no path by which a caller supplies that value.

```
POST /v1/probe
{ "apiVersion": 1, "requestId": "<32 hex>", "expectedAddress": "<router-reported IPv4>", "port": 25566 }
```

The Worker:

1. normalises and validates the observed source address, strictly (no `010.0.0.1`, no `0x7f.1`, no
   `2130706433`, no shorthand, no hostname);
2. validates `expectedAddress` as a globally routable IPv4 address;
3. validates the TCP port (1–65535, never 25, which Cloudflare prohibits);
4. compares `expectedAddress` with the observed requester address;
5. **only if they match exactly**, attempts TCP to `observed-address : requested-port`.

`expectedAddress` is never a destination. If it differs from the observed address, **no socket is
opened at all** and the typed result is `source_mismatch`.

There is no hostname input, no DNS input, no redirect target, no private-network target, no localhost
target, and no user-supplied IP target. The property is enforced by the control flow in
`src/handler.ts`, not by a blocklist, and it is covered by tests that assert the injected connector was
never called.

Cloudflare independently blocks outbound sockets to its own IP ranges and to localhost/private
addresses, which is defence in depth rather than the primary control.

---

## The client side of that invariant: address family

Because the destination is always the observed source address, the request has to *arrive* over the
family that is being verified. A check of an IPv4 mapping that leaves a dual-stack computer over IPv6
gives the Worker an IPv6 source and an IPv4 expectation — two addresses it cannot compare and must not
connect to — so the only answers it can give are `source_mismatch` and `unsupported_address_family`.
That is the service behaving correctly, and it means a perfectly forwarded server can never be
verified. This was CP-2026-018, observed on real hardware.

The fix is entirely in the desktop client, in `ExternalProbeTransport`:

- the router-reported address decides the family, and anything that is not IPv4 is refused before a
  request is sent rather than being sent over whatever the transport prefers;
- the configured hostname is resolved, **all** records are read and only addresses of that family are
  kept, so the outcome does not depend on whether A or AAAA happened to be listed first;
- up to three addresses of that family are tried, each on its own bounded wait, within the handler's
  existing connect budget;
- no IPv4 address, or no IPv4 address that answers, is a truthful failure. Nothing is retried over
  IPv6, because a check made over IPv6 is not a check of the IPv4 mapping.

Only the socket is affected. The request URI, the TLS host, SNI, certificate validation and the Host
header remain the configured hostname and are never a resolved numeric address, and the system proxy
setting is untouched. IPv6 is never disabled — not for the process, not for the machine, and users are
never asked to.

When a VPN, a proxy or upstream NAT means Cloudflare observes an address other than the router's, the
service's source comparison is still the authority and the result is still no conclusion. Pinning the
family makes a correct check possible; it does not, and must not, manufacture a match.

---

## API contract

`POST /v1/probe` — the only route. Anything else is `invalid_request`.

### Request

| Field | Type | Rule |
|---|---|---|
| `apiVersion` | number | Must equal `1` |
| `requestId` | string | Exactly 32 lowercase hex characters (128 bits of client randomness) |
| `expectedAddress` | string | A globally routable IPv4 address in canonical dotted-quad form |
| `port` | number | Integer 1–65535, excluding 25 |

Maximum body: 512 bytes. `Content-Type` must be `application/json`. Method must be `POST`.

### Response

```json
{
  "apiVersion": 1,
  "requestId": "0f1e2d3c4b5a69788796a5b4c3d2e1f0",
  "result": "reachable",
  "observedAddress": "203.0.113.7",
  "observedFamily": "ipv4",
  "port": 25566,
  "checkedAt": "2026-08-08T19:42:00.000Z",
  "connectMilliseconds": 118
}
```

| `result` | HTTP | Meaning |
|---|---|---|
| `reachable` | 200 | TCP was established from outside to the observed address and port |
| `unreachable` | 200 | The connection was refused or timed out. No cause is claimed |
| `source_mismatch` | 200 | Observed and expected addresses differ. **No socket was opened** |
| `unsupported_address_family` | 200 | IPv6 caller, non-routable IPv4 caller, or no readable source. No socket |
| `invalid_request` | 400 | The request was refused. A bounded `reason` code says which field |
| `rate_limited` | 429 | Too many checks from this address |
| `probe_error` | 503 | The service failed. Nothing about the server was learned |

`reason` is one of `method`, `path`, `content_type`, `body_too_large`, `malformed_json`,
`api_version`, `request_id`, `expected_address`, `port`. Provider exceptions are never exposed and are
never parsed into diagnoses.

### TCP behaviour

One bounded attempt, 5 seconds. On success the socket is closed immediately: no bytes are written, no
bytes are read, and no Minecraft protocol is spoken. The check is therefore Minecraft-version
independent. The Workers socket API documents no connection timeout of its own, so the Worker imposes
one and races it against `socket.opened`.

---

## Abuse controls

- Cloudflare Rate Limiting binding `PROBE_RATE_LIMITER`, keyed on the observed source address:
  **6 requests per 60 seconds**. Conservative for a button somebody presses, useless for polling.
- A limiter that cannot answer returns `probe_error` rather than becoming a free pass.
- `POST` only, one path only, JSON only, 512-byte body cap enforced while the body streams.
- Strict field validation and port-range validation.
- `Cache-Control: no-store`, `X-Content-Type-Options: nosniff`.
- No CORS headers: the desktop application is the client, not a browser.
- Nothing from the request is reflected into the response beyond the correlation id and the caller's
  own observed address.

### What the correlation id does and does not do

`requestId` is 128 bits of client randomness, echoed by the service and required to match before an
answer is allowed to mean anything. Its job is to stop one answer being mistaken for another: a reply
to a superseded check, a reply that arrived late, a reply about a different port or a different
address. It does that completely.

It is **not** authentication. It travels in the request, so the service that receives it knows it, and
an answer carrying it proves only that whoever answered saw the request. Nothing in this design — and
nothing that could be added to it — lets the desktop application verify from the outside that a TCP
handshake actually happened. The service's timestamp is not authentication either, which is why the
displayed "Verified 7:42 PM" comes from the local clock rather than from the answer.

### Trust boundary

The configured probe service is a **trust root for a Reachable assertion**. A compromised or
substituted service could fabricate one: it knows the request id, the port and the address, because
they are exactly what it was asked about.

ChunkPilot narrows what that assertion can be turned into, rather than pretending to eliminate it:

- The endpoint is not user-editable in normal production UI; it comes from build configuration, must
  be an HTTPS origin, and anything else fails closed.
- Redirects are refused, so a valid endpoint cannot hand the request to another host.
- A `reachable` answer is accepted only when the contract version, the correlation id, the HTTP
  status, the port, the address family and the observed address all agree with the request that was
  sent, and only when it carries a finite, non-negative connection time. Every other typed result is
  held to its own contract status.
- The assertion is combined with local Agent state that the service cannot influence: the exact
  running process, the exact router mapping establishment, the exact ports. A fabricated `reachable`
  still expires the moment any of those change.
- The claim it produces is bounded — "TCP answered from outside your network at this time" — and never
  grows into a claim about the game.

Replacing or compromising the official probe endpoint could falsify the remote observation. That is
inherent to asking somebody else to look, which is the only way to look from outside at all. A secret
compiled into a widely distributed desktop executable would not change it.

### No embedded shared secret

There is deliberately no API key in the desktop application. A secret compiled into a widely
distributed executable is not a service-authentication boundary — anyone with the binary has it. The
real boundary is structural: the destination is always the caller's own address, so the worst a hostile
caller can do is ask the service to connect to itself, at six attempts a minute. Rate limiting,
validation, HTTPS and API versioning carry the rest.

---

## Privacy

- No accounts, no sign-in, no telemetry.
- No database: no D1, KV, R2, Durable Objects or queues. The Worker is stateless.
- No durable request storage, and persistent Workers observability is explicitly disabled in
  `wrangler.jsonc`.
- Public IPs, ports and request bodies are never logged.
- The request carries four fields and nothing else — no server name, no world data, no player data, no
  machine identity, no token.

The service necessarily *sees* the caller's public address, because it must connect back to it. That is
inherent to the design and is stated to the user before the first deliberate probe, in a compact line
beside the button rather than a modal:

> ChunkPilot will ask its external probe to connect to this server's public TCP port. The check sends
> the port number and uses the public address the probe sees. No world, player, or server files are
> sent.

---

## Local development

```bash
cd services/ChunkPilot.ReachabilityProbe
npm install
npm test          # node --test, no Cloudflare account, no sockets opened
npm run typecheck # tsc against @cloudflare/workers-types and @types/node
```

Tests run against `handleProbe` with an injected recording connector, so no test can reach the public
internet. `src/worker.ts` is the only file that imports `cloudflare:sockets`, and it is excluded from
the test typecheck project for that reason.

To run the Worker locally:

```bash
npx wrangler@4 dev
```

Then point a development ChunkPilot build at it. The client requires HTTPS in production; plain HTTP
is accepted only on loopback and only by in-process tests, never by the shipped application.

---

## Cloudflare deployment (manual, not performed by the repository)

Nothing in this repository deploys anything. These are the steps a person performs once, deliberately.

**Requirements**

- A Cloudflare account (free plan is sufficient for `workers.dev`).
- Wrangler **4.36.0 or later** — the Rate Limiting binding requires it. `npx wrangler@4` satisfies this.
- No custom domain and no DNS change: `workers.dev` provides the HTTPS endpoint.

**Steps**

1. `cd services/ChunkPilot.ReachabilityProbe`
2. `npm install`
3. `npm test && npm run typecheck` — do not deploy a service whose contract tests do not pass.
4. `npx wrangler@4 login` — opens a browser for the account owner to authorise.
5. Review `wrangler.jsonc`. It declares:
   - `name`, `main`, `compatibility_date`, `workers_dev: true`
   - `observability.enabled: false` — persistent request logging stays off
   - one `ratelimits` entry: `PROBE_RATE_LIMITER`, `namespace_id: "1001"`, `simple: { limit: 6, period: 60 }`
     (the period must be `10` or `60`; `namespace_id` is any positive integer unique within the account)
   - no D1, KV, R2, Durable Object, queue or cron binding
6. `npx wrangler@4 deploy`
7. Wrangler prints the deployed URL, of the form
   `https://chunkpilot-reachability-probe.<your-subdomain>.workers.dev`. That is the **origin** — do not
   append a path.

**Configuring a development ChunkPilot build**

Set the origin (not the full probe URL) before starting ChunkPilot:

```powershell
$env:CHUNKPILOT_REACHABILITY_PROBE_URL = "https://chunkpilot-reachability-probe.<your-subdomain>.workers.dev"
```

The client appends `/v1/probe` itself. A value that is not an absolute HTTPS origin — with a path,
query, fragment, credentials, or another scheme — is refused and the feature reports that external
verification is unavailable in this build. There is no user-editable endpoint in the normal production
UI.

**Removing the Worker afterwards**

```bash
npx wrangler@4 delete
```

Then unset `CHUNKPILOT_REACHABILITY_PROBE_URL`. ChunkPilot returns to reporting that external
verification is unavailable; nothing else changes, and no server, router or firewall state is affected.

No secrets are stored in this repository, and none are required by the service.

---

## VPN, proxies and other outbound paths

A VPN being installed must not break the feature, and ChunkPilot must not quietly work around one.

The probe reports the address it observed. When that differs from the address the router reports,
ChunkPilot does **not** attempt the TCP probe and does **not** call the server unreachable. It reports
**Different public address detected** and shows both addresses:

> The external check reached the internet through a different address than your router reports. A VPN,
> another network path, or upstream NAT may be involved.

Nothing is changed automatically: no VPN is disabled or bypassed, no route or adapter metric is
altered, and no proxy setting is touched.

A deliberate second check forced out over the trusted physical server interface was investigated and
deliberately **not** implemented. `SocketsHttpHandler.ConnectCallback` can bind an outbound socket to a
chosen local address, which is documented and changes no system state — but binding a source address
does not by itself decide which interface Windows sends the packet over. With a full-tunnel VPN
holding a higher-priority default route, the connection would still take the tunnel or fail outright,
so the check would be non-deterministic. Making it deterministic would require route or interface-metric
manipulation, which is exactly the kind of silent security weakening this feature must not perform. The
mismatch therefore stays a professional diagnostic with both addresses shown, and the user decides.

---

## Result lifetime

A verified result is point-in-time evidence, held in the Agent's memory and bound to the exact endpoint
it was gathered about: server id, public address, external port, internal port, **router mapping
instance** and run identity (process id and start time).

The mapping instance is an opaque value the Agent mints while one router entry is open and drops the
moment none is. It exists because mechanism, transport, client and ports describe what a mapping *is*
and cannot tell one establishment from another: a router that drops an entry and is asked for the same
one again produces something identical in every observable way, and evidence about the first entry must
not survive into the second. The identity is stable for as long as that one entry stays open — ordinary
polling and lease renewal on unchanged terms observe the same value — and is replaced whenever a
mapping is established that is not the previous one continuing. It is never persisted, because it names
a live router entry rather than a setting.

Continuation has to be **proven**, never assumed, and the two mechanism families prove it with
completely different evidence:

- **UPnP IGD** can read the router's table, so the router's own answer decides. An entry it reports as
  free, or as belonging to somebody else, is proof the previous establishment has ended — whatever
  ChunkPilot had written down. Ownership is dropped there and then; nothing is sent to the router,
  because what is present is not ChunkPilot's and what was ChunkPilot's is no longer present.
- **PCP and NAT-PMP** can read nothing, and their renewal message is byte-for-byte a creation message —
  RFC 6886 section 3.7 says so outright — so a gateway that restarted answers a renewal exactly as it
  answers a creation. The only evidence that distinguishes them is the epoch every response carries:
  PCP's Epoch Time (RFC 6887 section 8.5) and NAT-PMP's Seconds Since Start of Epoch (RFC 6886 section
  3.6), both of which a gateway resets when it loses its mapping table. ChunkPilot validates it with
  each RFC's own tolerant rule, per gateway and per mechanism, and preserves an identity only on an
  affirmative continuation.

Continuation also has to be **on the same network**. A mapping is created on one router, reached over
one adapter, and it stays there; a look at a different one says nothing whatsoever about it. The record
therefore remembers the exact binding a mapping was established on — the interface as well as the
gateway address, because 192.168.1.1 over Wi-Fi is a different router from 192.168.1.1 over Ethernet —
and an observation made through another binding can never write that ownership, prove a continuation, or
supply the router-reported address a verified endpoint is quoted from. While the owned and the
discovered binding disagree the mapping is reported as needing attention rather than open, which drops
the identity and makes the verification bound to it stale, including when the computer later returns to
the original network. The record itself is kept intact, because the entry may still have to be
withdrawn — and a withdrawal is never addressed to a router that does not hold it.

A row written before ownership recorded a network is treated the same way, and for the same reason: it
names a gateway address without saying which adapter reached it, so nothing observable identifies the
mapping it claims. It reads as needing attention rather than open, no external check can be made against
it, and no removal is sent to whatever router is reachable now — not for UPnP, whose table can only show
a coincidence, not for NAT-PMP, whose deletion is scoped by this computer's own internal port, and not
for PCP, whose nonce makes a stray deletion harmless without making it evidence of anything. The safe
resolution is a fresh mapping on the network this computer provably is on, which carries a new identity
and needs a new deliberate check.

That resolution starts by ending the old claim rather than carrying it forward. A row like this asserts
two different things through one value — that a mapping may exist somewhere, and that ChunkPilot owns
one here — and only the first is true, so they are separated before anything about the router in front
of us is judged. Otherwise an entry that merely resembles the row would prove ownership by resemblance,
and ChunkPilot's description is a constant every install writes. What the old row described is kept as a
possible exposure, in a field no ownership rule reads: it is reported alongside whatever happens next,
because a port open on another router does not close because a new one opened here, and it stops being
reported once the finite lease it recorded has run out.

Where continuity cannot be established either way, the identity is replaced. That is deliberately the
cheap mistake to make: it costs a stale external result and one more deliberate check, while the
opposite mistake would present evidence about one router entry as current for another. It also stays
separate from whether the mapping is open at all — "active but not proven to be the same one" is a real
state, and it is not reported as a router failure.

Every read recomputes the current endpoint and compares. Any difference makes the result **stale**, and
a stale result is never presented as currently verified. That single comparison covers Stop, a new
Start, a removed or recreated mapping — including one recreated on identical terms — a changed external
or internal port, a changed Java runtime or server target (both of which require a restart, changing
the run identity), a changed public address, and Direct internet being turned off.

Nothing is persisted, so no database migration was needed and an Agent restart truthfully reports
**Not checked** rather than resurrecting an old claim. Verification is never renewed automatically, and
there is no background polling of the service: zero idle network cost, one tiny HTTPS request and one
external TCP connect attempt per deliberate press.
