# CurseForge credential delivery

Status date: 2026-09-15

## Production decision

**Published 1.0.0 uses personal setup. The private application service is deployed for acceptance testing.**

The service is live at `https://chunkpilot-curseforge-service.chunkpilot-reachability.workers.dev`.
The owner's application key exists only in its encrypted Worker secret binding. Fresh native checks
with an empty personal-key store resolve Minecraft identity and ATM10's exact official client/server
file relationship successfully. This does not yet establish a complete large-pack installation.

Workers Free terminated representative ATM10 downloads with `exceededCpu`; partial files were rejected
by length/hash verification. The production configuration requests a 60-second CPU ceiling and needs
Workers Paid, which starts at $5/month plus possible usage charges. No billing change was made by the
development task. A temporary Free-plan deployment supports metadata acceptance while the owner
decides on hosting capacity. It must not be described as large-pack certified.

The published 1.0.0 public-client model uses optional personal setup: the user enters their own approved
CurseForge API access in a native password window. ChunkPilot validates it against the official API and
stores it with Windows DPAPI CurrentUser protection. Settings > General > Set up CurseForge is always
reachable; unavailable CurseForge discovery also offers the same entry. This does not distribute the
developer's key or introduce a hosted relay. Users remain responsible for obtaining approved access for
their intended API use. This implementation is not evidence of separate CurseForge approval for every
end-user use case.

The actual approval email was inspected on 2026-09-15. It explicitly approves the named ChunkPilot
application for the third-party API. It does not limit that approval to development. The earlier
development-only description was an unsupported inference and is withdrawn. The email and its key are
not copied into this repository. The currently published third-party API terms do not establish permission to put that credential
in a desktop binary distributed to end users. Section 2.2 says the unique key is non-transferable, may not be
shared with a third party, and may be disclosed only to employees with corresponding confidentiality duties.
An extractable key in a public Windows executable would disclose it to end users, who are third parties.

Protecting that key does not mean every end user needs a separate key. The requested model is a private
application backend: users request CurseForge operations through ChunkPilot, and only the backend
authenticates to the official API/CDN. No application key, encrypted copy, or recoverable bootstrap secret
is delivered to their PC. The existing released build does not implement this automatic access.

The official terms and application-guidance pages were rechecked on 2026-09-15. Their displayed
modification dates and relevant restrictions remain the same. Successful native developer-key requests,
archive verification, and exact-release fixes do not authorize disclosure of a shared developer key.

## Official evidence

- [CurseForge 3rd Party API Terms and Conditions](https://support.curseforge.com/support/solutions/articles/9000207405-curseforge-3rd-party-api-terms-and-conditions),
  modified 2024-08-14: Sections 2.1–2.3 authorize External Apps but make the key non-transferable and
  confidential; Section 3 prohibits saving or caching API data and prohibits using the API to build a
  competing product; Section 9 requires API-key destruction on termination.
- [About the CurseForge API and How to Apply for a Key](https://support.curseforge.com/support/solutions/articles/9000208346),
  modified 2024-12-03: access is a unique key issued after application review; review considers author
  earnings, service/CDN impact, and author distribution consent.
- [CurseForge REST API](https://docs.curseforge.com/rest-api/): the official base is
  `https://api.curseforge.com`; authentication is `x-api-key`; page size is at most 50 and indexed results
  are capped at 10,000.
- [Project Distribution Toggle](https://support.curseforge.com/en/support/solutions/articles/9000207877),
  modified 2024-10-31: author-controlled distribution choice overrides license type and must be honored by
  third-party clients.
- [CurseForge CDN authentication announcement](https://blog.curseforge.com/introducing-api-key-authentication-for-curseforge-file-downloads/),
  published 2026-06-10: launchers, modpack managers, and server tools are expected to authenticate direct CDN
  downloads; the announcement does not override the third-party key-confidentiality terms.

The inspected approval establishes access for ChunkPilot, not only development. It does not describe a
special client-credential protocol, CDN relay arrangement, or quota grant. The general terms' prohibition
on identity-concealing proxies must not be treated as permission to deploy an arbitrary proxy, nor as proof
that application-service access is impossible. Confirm the proposed identified owner-operated backend's
delivery model with the provider before public activation. The code does not bypass author restrictions,
hide application identity, or persist API responses.

## Private application-service design

`services/ChunkPilot.CurseForgeService` is a separate Cloudflare Worker, not a new listener on the user's PC
and not an extension of the reachability probe. It accepts only bounded Minecraft metadata routes,
exact file-ID downloads, and project-ID artwork requests. Download authorization is freshly resolved from
the official API: game/project/file identity, availability, author distribution, URL, size and hash.
The source fingerprint is SHA-256 over UTF-8 `https://` plus the lowercase DNS hostname and the pathname
decoded once. Query strings, fragments and non-default ports are rejected. This shared native/JavaScript
representation avoids differences in percent escaping and hostname casing. It binds the request to its
reviewed source; it is not an authentication token or an authorization substitute, and is never used to
construct the actual upstream URL. Payload integrity is still verified locally.

The key is injected from the Worker secret binding `CURSEFORGE_API_KEY`. It is never a build property,
Wrangler plaintext `vars` field, client header, URL query parameter, package file, log, or response.
Upstream request headers are constructed from scratch. Errors are fixed messages, not upstream bodies.
Metadata and payloads remain bounded and cancellable. Responses retain official metadata rather than
disguising service addresses as CurseForge CDN addresses.

A rate-limit binding constrains each client, while one SQLite Durable Object coordinates aggregate daily
upstream-request/byte reservations and expiring concurrent-operation leases across all service locations.
It stores no search terms, IP addresses, provider metadata, or downloaded files. Missing required controls
fail closed. Conservative reservations are not refunded after an interrupted operation. An account-free
service is publicly callable: a hidden key does not authenticate the desktop app or eliminate quota abuse.
Review limits and hosting capacity before activation, including large packs and simultaneous downloads.
The streaming secret-echo check inspects every payload byte; a bounded-memory stream is not zero CPU.
Cloudflare's free request CPU allowance failed live large-archive checks. Verify the actual paid plan
and representative archive CPU use before promising large-pack service support; do not silently enable
paid capacity or change account billing.

The desktop uses the public `CurseForgeServiceEndpoint` assembly metadata when supplied at build time.
There is no endpoint or key typed by ordinary users. Without that metadata, the released personal-key
behavior is preserved. Native availability distinguishes `ApplicationService`, `PersonalKey`, and
`Unavailable`; configuration is not live reachability proof. A service outage never silently forwards a
stored personal key to the service. Images follow the same no-secret transport.

## Implemented local credential boundary

- Personal setup uses a native `PasswordBox`, never a React input. The renderer requests
  `providers.configureCurseForge` with an empty object and receives only configured/cancelled state.
- The App protects the entered value with DPAPI CurrentUser before sending the bounded encrypted request
  over the named pipe. The Agent authenticates the UI session, decrypts into bounded native memory,
  validates the key with the official API, rechecks cancellation and session ownership, and only then
  replaces the stored secret. Invalid or unavailable validation preserves the previous key.
- The native dialog can remove the saved personal key through an authenticated session request. Cancel,
  submit, and close clear its password field. Mutable transport buffers are cleared; unavoidable short-lived
  managed strings cannot be securely zeroed and are never logged, returned to React, or retained in a ViewModel.
- Developer provisioning requires an explicit `CHUNKPILOT_CURSEFORGE_KEY_FILE` absolute file path, never
  the key value. The development launcher sets it for its exact Agent child; the file is never tracked or packaged.
  Ordinary startup never searches a repository-local credential file, replaces a personal key, or restores a
  removed key when this opt-in environment value is absent.
- The Agent reads a bounded single-value file directly and imports it into the existing DPAPI CurrentUser
  secret store. It decodes only the populated span and clears the complete originally allocated byte buffer in
  `finally`; it never resizes away from the allocation that held the file bytes.
- The long-lived App does not retain the path-only override in its process environment. `AgentClient` captures it
  once, clears `CHUNKPILOT_CURSEFORGE_KEY_FILE` from the App immediately, and supplies it only on the exact
  `UseShellExecute=false` Agent start. The captured reference is discarded once an existing Agent answers or the
  child starts, so later browser, shell and elevated firewall-helper launches cannot inherit the source path.
- After bootstrap, the Agent removes the source-path variable. Managed servers, downloaded Java/loader helpers,
  staged validation, approved automation programs, and runtime certifiers start from a bounded Windows/Java
  environment allowlist, then receive only their explicit server/workflow variables. Removing one known secret
  name is not treated as sufficient child-process isolation.
- No raw-key App/Agent command exists. The WebUI allowlist exposes only the native setup launcher,
  and renderer state never receives plaintext or encrypted keys, source paths, or secret-store data.
- `.secrets` and `curseforge-api-key*.txt` are ignored. Publication audit rejects either path even if a file
  is accidentally forced into Git.
- Diagnostic redaction covers `x-api-key`, ordinary API-key key/value forms, structured JSON, authorization
  credentials, URLs, and secret-like environment variables.
- Logs and provisioning results report only present/imported/unavailable state. They never include the key or
  source path.

The example at `docs/examples/curseforge-credential-placeholder.txt` is a non-secret placeholder and is explicitly
allowed through the ignore pattern so the safe shape remains reviewable.

## Public-build behavior

Public packages must not include or derive a shared CurseForge key. A service-enabled package needs no
end-user key and shows that the application service is configured, not that it is necessarily online.
Authentication/quota/outage failures remain authoritative and retryable; they do not require obtaining a
personal key. An unconfigured build retains the native personal setup flow. Modrinth, local pack imports,
and existing server management remain available independently of the service. No scraping fallback is used.

Acceptance must distinguish fake-provider credential validation, native dialog checks, and a real approved
key checked live. Passing synthetic tests does not claim a live key, account approval, or public quota grant.

## Activation checklist

Before enabling the endpoint in a public release:

1. Hosting authentication and the distinct Worker deployment are complete. Confirm sufficient hosting
   capacity with the owner; the Free plan failed ATM10 streaming. Verify costs before any plan change.
2. Resolve provider delivery-model and quota requirements for the approved application. Do not treat the
   approval as either development-only or blanket permission for every distribution method.
3. The distinct Worker, budget object, and secret binding are deployed; the reachability probe is unchanged.
   Redeploy the production CPU limit after capacity is enabled, then record the exact deployed version.
4. Verify HTTPS, authenticated official search/exact metadata/image/download paths, author denial,
   cancellation, rate limiting, global-budget denial, outage behavior and absence of secrets in every
   client-visible response. Synthetic tests are not live acceptance.
5. Verify a clean user installation with no personal key can browse, install, start, update and recover a
   disposable server. Server startup still requires deliberate EULA consent and adequate hardware.
6. Build the new version with only the verified public service address, then perform package privacy,
   upgrade, checksum and public-download verification. Do not silently replace 1.0.0's immutable assets.

Rotate or revoke the key at the host without updating clients. During rotation/outage the provider may be
unavailable, but local worlds, server ownership and recovery state remain unchanged. Changing the service
origin requires a reviewed client update. Never recover service access by distributing a key to clients.

Written application-specific terms override this general analysis only when they are supplied and reviewed.
