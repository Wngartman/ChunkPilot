# Code signing and SmartScreen

ChunkPilot's release factory is signing-ready but no trusted publisher identity is configured. Public Alpha 4 artifacts are therefore expected to be unsigned, and Windows SmartScreen may warn. Repository cleanup and checksums do not create Authenticode trust or SmartScreen reputation.

## Trusted signing gate

Obtain an organization-validated or extended-validation code-signing identity from a trusted provider, or configure an approved managed signing service. The private key must never be committed, uploaded to pull-request jobs, printed, or placed in a release artifact.

For a locally available protected certificate, set `CHUNKPILOT_SIGNING_CERT_THUMBPRINT` in the trusted release environment. `scripts/sign-release.ps1` signs the App, Agent, Firewall Helper, and installer with SHA-256 and requires a trusted RFC 3161 timestamp. `scripts/verify-release-signatures.ps1 -RequireSigned` fails if any required first-party file is invalid, untrusted, or lacks a timestamp.

GitHub Actions must provision the signing identity only in the protected release job after pull-request code has completed. A repository secret containing only a thumbprint is not sufficient; the corresponding private key or managed-signing identity must be provisioned through the selected provider's secure mechanism.
