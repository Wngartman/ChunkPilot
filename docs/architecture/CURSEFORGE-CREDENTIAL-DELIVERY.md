# CurseForge credential delivery

Status date: 2026-09-14

## Production decision

**PERSONAL NATIVE SETUP IMPLEMENTED; SHARED APPLICATION-KEY DELIVERY STILL GATED**

The selected public-client model is now optional personal setup: the user enters their own approved
CurseForge API access in a native password window. ChunkPilot validates it against the official API and
stores it with Windows DPAPI CurrentUser protection. Settings > General > Set up CurseForge is always
reachable; unavailable CurseForge discovery also offers the same entry. This does not distribute the
developer's key or introduce a hosted relay. Users remain responsible for obtaining approved access for
their intended API use. This implementation is not evidence of separate CurseForge approval for every
end-user use case.

ChunkPilot has an approved developer application credential available for bounded local development, but
the currently published CurseForge third-party API terms do not establish permission to put that credential
in a desktop binary distributed to end users. Section 2.2 says the unique key is non-transferable, may not be
shared with a third party, and may be disclosed only to employees with corresponding confidentiality duties.
An extractable key in a public Windows executable would disclose it to end users, who are third parties.

This remaining gate applies to shipping a shared developer application credential. Public builds contain
no shared credential and keep CurseForge unavailable until the user supplies their own working access.

The official terms and application-guidance pages were rechecked on 2026-09-14. Their displayed
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

The application approval reported by the user proves that bounded development API access was granted. It
does not, without the actual approval terms or separate written permission, prove that the key may be shipped
to arbitrary end users or that persistent provider caching is allowed.

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
- Repository-local `.secrets\curseforge-api-key.txt` provisioning remains a development-only path;
  native code binds that developer path exactly and the file is never tracked or packaged.
- `CHUNKPILOT_CURSEFORGE_KEY_FILE` may contain one absolute file path. It never contains the key value.
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

Public packages must not include or derive a shared CurseForge key. A user can connect, replace, or remove
their own approved access using the native setup flow. Provider status stays unavailable until access is
configured, and subsequent provider responses remain authoritative about authentication or quota failures.
Modrinth and local server-pack imports remain available. No hosted relay, proxy, or scraping fallback is used.

Acceptance must distinguish fake-provider credential validation, native dialog checks, and a real approved
key checked live. Passing synthetic tests does not claim a live key, account approval, or public quota grant.

## Shared application-key delivery checklist

Every item requires evidence before changing the decision to `DIRECT CLIENT DELIVERY ESTABLISHED`:

1. Written CurseForge/Overwolf authorization for the named ChunkPilot application to distribute an
   application credential inside a public native desktop client, or an approved client credential mechanism
   that does not disclose a transferable secret.
2. Written terms covering the required bounded metadata cache, or a reviewed design that retains no API data
   beyond the live request and still meets provider limits and offline truthfulness.
3. Confirmed quota/rate-limit expectations for the projected public installation count and retry policy.
4. Confirmed CDN authentication requirements and allowed credential forwarding behavior for every official
   download host used by the provider.
5. A documented revocation and rotation process that disables the provider safely without breaking local
   server ownership, updates, recovery snapshots, or rollback.
6. A new official-source review against the then-current API terms, REST schema, distribution controls, and
   download authentication rules.
7. Explicit release authorization for the exact implementation and credential-delivery model after the
   security, package-secret, and independent publication audits pass.

Written application-specific terms override this general analysis only when they are supplied and reviewed.
