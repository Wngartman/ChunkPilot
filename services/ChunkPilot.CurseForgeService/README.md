# ChunkPilot CurseForge application-key service

This optional Cloudflare Worker is the server-side credential boundary for a public ChunkPilot
build. It is not a generic proxy. The approved application key lives only in the
`CURSEFORGE_API_KEY` Worker secret binding; it must never be compiled into an installer, browser
bundle, config file, command-line argument, repository file, CI artifact or request log.

Adding these files does not deploy a service or grant provider approval. An authorized operator
must deploy the distinct `chunkpilot-curseforge-service` Worker, configure its quota bindings,
provision the approved key through secret input, verify the live routes, and then configure the
desktop build with that verified HTTPS endpoint. No deployment/secret script runs during install,
test or package creation. There are no runtime npm dependencies.

## Protocol version 1

- `GET /health` returns only service identity, `protocolVersion: 1` and `configured`. This means
  secret/binding configuration is present, not that live upstream authentication succeeded.
- `GET /v1/games/432`, `GET /v1/minecraft/version?sortDescending=true`, Minecraft category/search,
  exact project, file-list, exact file and exact download-URL metadata routes support the current
  native client's allowlisted parameters. No arbitrary methods, body, upstream URL or headers are
  forwarded. Search/file pages are at most 50, and indexes at most 10,000. Project/file response
  identities are checked against Minecraft and the requested parent. Author-restricted projects can
  remain visible in metadata; download-URL retrieval itself requires current distribution approval.
- `GET /downloads/{positive-file-id}?sourceSha256={64-lowercase-hex}` independently resolves that
  file through the official batch file endpoint, resolves its exact Minecraft project, requires
  explicit availability and author distribution approval, a positive bounded byte length and SHA-1
  metadata. A missing URL may use only the exact official download-URL endpoint after author approval;
  it is never synthesized. The request is rejected if its source identity differs from the client.
- `GET /images/{positive-project-id}` resolves only that available, distribution-approved Minecraft
  project's official logo. Images are bounded at 512 KiB and raster MIME types; the desktop decoder
  still validates pixels and limits dimensions/frames.

Metadata keeps official source URLs unchanged. It never replaces official identities with service
URLs. The native downloader verifies the final file's expected hash and length before activation;
successful streaming here is not proof that a downloaded archive passed that client verification.

The source hash is SHA-256 of UTF-8 `https://` + lowercase hostname + the URL pathname decoded
**exactly once**. Native equivalent: lowercase `Uri.IdnHost` plus
`Uri.UnescapeDataString(Uri.AbsolutePath)`; Worker equivalent: lowercase `URL.hostname` plus
`decodeURIComponent(URL.pathname)`. This representation is for comparison only, never a download
target. It equates spaces/`%20`, Unicode/UTF-8 escapes and escaped unreserved characters across the
two runtimes; `%2520` stays `%20` after one decode. Invalid escape sequences are rejected.

## Network and secret boundaries

Only `https://api.curseforge.com` receives metadata calls. Download/image requests may reach only
the exact reviewed hosts `edge.forgecdn.net`, `mediafilez.forgecdn.net` and `media.forgecdn.net`.
HTTPS/default port 443 is required. Userinfo, query strings, fragments, controls, unreviewed
subdomains and redirects leaving this boundary are rejected. At most five manually checked CDN
redirects are followed. API redirects are rejected. Cookies, caller headers, provider errors,
exceptions, `Location`, API-key headers and upstream response headers are never returned.

JSON is bounded to 8 MiB with a 20-second whole metadata-operation deadline. Image operations also
have a 20-second total bound. Downloads have 20-second per-upstream-header deadlines, a 30-minute
whole operation deadline, 30-second stream idle deadlines and a 2 GiB maximum. Actual bytes must
equal official `fileLength`; truncation/overflow interrupts the stream. Cancellation propagates to
the upstream request. Body/header deadlines are separate so a normal large transfer is not cut off
20 seconds after its headers arrive.

Every successful JSON value/property is checked for accidental key echo. Raster bodies are scanned
before response, and file streams use a linear-time rolling scanner retaining at most key-length
minus one trailing bytes. An upstream key echo, including one split across chunks, therefore cannot
be completed downstream. Neither tests nor failures log the real key; all tests use a synthetic key.

## Capacity and privacy

The fail-closed `CURSEFORGE_RATE_LIMITER` binding uses namespace `1002`, distinct from reachability's
`1001`, with 300 requests/minute per Cloudflare-provided client IP. There is no caller-supplied IP
fallback. This ephemeral abuse limit is not end-user authentication and is not globally exact;
the separate SQLite Durable Object enforces the globally coordinated hard bounds.

One `CURSEFORGE_BUDGET` singleton atomically reserves each upstream request and its maximum byte
cost before network access. Defaults are 20,000 upstream requests and 50 GiB of reserved response
capacity per UTC reservation day, with 24 concurrent operations. Metadata reserves the full 8 MiB;
images reserve 512 KiB; files reserve official length. Redirect attempts reserve again. Reservations
are deliberately **not refunded**, even after failure, so a dead Worker cannot reset consumed
capacity. These are conservative reservation-day bounds, not a claim to measure exact delivered
bytes or calendar-day ingress for a download spanning midnight. Costs/caps must be reviewed before
any deployment or broad release. Exhaustion fails closed; it is not silently routed around.

The singleton persists only aggregate day/count/byte counters and random expiring operation IDs.
It never persists IPs, search terms, metadata, project/file IDs, file data or credentials. Old daily
counters are pruned. Metadata/image lease expiry is at most 80 seconds; download leases at most
31 minutes. Normal/canceled operation release and late acquire-response cleanup are bounded and
retained with `ExecutionContext.waitUntil`. A process loss or unavailable Durable Object still
conservatively holds capacity until expiry. Missing/malformed bindings fail closed. There is no
completed response cache, analytics binding, queue, cron, console logging or request observability.

Cloudflare and CurseForge necessarily process network requests while this optional integration is
used. This service is not a claim that Cloudflare keeps no platform security/access records. It does
not change local-only server management, require a ChunkPilot account or open local server ports.

## Offline verification

Use the project's locally installed development tools; no live CurseForge call is made:

```powershell
# From the repository root:
Set-Location '.\services\ChunkPilot.CurseForgeService'
npm.cmd ci --ignore-scripts --no-audit --no-fund
npm.cmd run typecheck
npm.cmd test
```

Tests exercise actual in-memory SQLite transactions, request/byte/concurrency limits and restarts,
route/SSRF rejection, exact identities, author restrictions, canonical source hashes, secret echo,
stream lengths, redirects, timeout/cancellation and late lease acquisition. They do not certify
deployment, real credentials, a hosting account's billing plan, live provider behavior or client
installation. Root release verification must establish those separately.
