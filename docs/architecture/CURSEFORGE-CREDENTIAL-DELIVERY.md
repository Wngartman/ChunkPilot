# CurseForge credential delivery

Status date: 2026-08-27

## Production decision

**PUBLIC CREDENTIAL DELIVERY STILL GATED**

ChunkPilot has an approved developer application credential available for bounded local development, but
the currently published CurseForge third-party API terms do not establish permission to put that credential
in a desktop binary distributed to end users. Section 2.2 says the unique key is non-transferable, may not be
shared with a third party, and may be disclosed only to employees with corresponding confidentiality duties.
An extractable key in a public Windows executable would disclose it to end users, who are third parties.

This is a product-policy gate, not a missing-code gate. The typed provider, safe local provisioning,
deterministic fixtures, and local approved smoke path may exist while public builds truthfully keep
CurseForge unavailable.

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

- The only default source is `D:\ChunkPilot\.secrets\curseforge-api-key.txt`.
- `CHUNKPILOT_CURSEFORGE_KEY_FILE` may contain one absolute file path. It never contains the key value.
- The Agent reads a bounded single-value file directly and imports it into the existing DPAPI CurrentUser
  secret store. The bytes are cleared after import.
- No raw-key App/Agent command exists. The WebUI allowlist contains no key status/save/remove/console method,
  and renderer state never receives the key.
- `.secrets` and `curseforge-api-key*.txt` are ignored. Publication audit rejects either path even if a file
  is accidentally forced into Git.
- Diagnostic redaction covers `x-api-key`, ordinary API-key key/value forms, structured JSON, authorization
  credentials, URLs, and secret-like environment variables.
- Logs and provisioning results report only present/imported/unavailable state. They never include the key or
  source path.

The example at `docs/examples/curseforge-api-key.example.txt` is a non-secret placeholder and is explicitly
allowed through the ignore pattern so the safe shape remains reviewable.

## Public-build behavior

Until the gate below is cleared, public packages must not include or derive a shared CurseForge key. Public
provider status remains unavailable with plain-language activation copy. Modrinth and local server-pack
imports remain available. No hosted relay, proxy, scraping fallback, or user-key input is introduced by this
milestone.

## Unblocking checklist

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

