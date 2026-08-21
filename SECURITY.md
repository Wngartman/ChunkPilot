# Security policy

## Supported release

Security fixes are evaluated against the newest public ChunkPilot prerelease. The current public baseline is `v1.3.0-alpha.2`; pre-alpha status means APIs and file formats may still change, but world/data preservation remains a release requirement.

## Report a vulnerability privately

Use [GitHub's private vulnerability report](https://github.com/Wngartman/ChunkPilot/security/advisories/new). Do not open a public issue containing credentials, `secrets.dat`, DPAPI blobs, real server or player data, public/private addresses, unredacted logs, worlds, backups, or private filesystem paths.

Include the affected version, the smallest safe reproduction, expected impact, and sanitized evidence. Never upload a real world or production database to demonstrate a problem.

For ordinary non-sensitive defects, use the repository's bug-report template.

## Security boundaries

ChunkPilot is local-first and has no account or telemetry service. Provider/catalog requests and downloads occur only for the corresponding product workflow. Router, firewall, external-probe, EULA, destructive-data, and remote-download operations remain separately consented and truthfully reported.
