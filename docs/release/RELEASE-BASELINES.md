# Frozen release baselines

The `v1.3.0-alpha.1` tag has no GitHub release or binary assets. Its first publication attempt stopped
before release creation when the immutable-release guard failed closed; it is not an installable baseline.

## v1.3.0-alpha.2 — ChunkPilot 1.3.0 Alpha Snapshot

- Release identity: annotated tag `v1.3.0-alpha.2`; the exact commit is the immutable peeled tag target returned by `git rev-list -n 1 v1.3.0-alpha.2` and the GitHub release API.
- Product version: `1.3.0` plus the release source revision in executable informational metadata.
- SQLite schema: `6`.
- Installer: `ChunkPilot-Setup-v1.3.0-alpha.2.exe`.
- Portable: `ChunkPilot-Portable-v1.3.0-alpha.2-win-x64.zip`.
- Immutable hashes: `SHA256SUMS.txt` attached to the release and repeated in its generated `RELEASE_NOTES.md` asset.
- Publication date: 2026-08-20.
- Release: <https://github.com/Wngartman/ChunkPilot/releases/tag/v1.3.0-alpha.2>

This baseline must not be overwritten. Future installer/updater tests download these exact public assets, verify `SHA256SUMS.txt`, and prove that upgrade preserves servers, worlds, backups, settings, protected credentials, database state, and provider configuration.

## v1.3.0-alpha.3 — ChunkPilot 1.3.0 Alpha 3

- Release: https://github.com/Wngartman/ChunkPilot/releases/tag/v1.3.0-alpha.3
- Tag kind: annotated
- Commit: `b43a884e43783a7f1e42e41526fac5a238647574`
- Product version: `1.3.0+b43a884e43783a7f1e42e41526fac5a238647574`
- Schema: 6 (unchanged)
- Installer: `ChunkPilot-Setup-v1.3.0-alpha.3.exe`
- Installer SHA-256: `54de37df01f71e15c646eb40052550fa0cb56094c44b04a37c89b4c223f10aee`
- Portable SHA-256: `24ec168e94958d6e5b64988410d7a649f8f3f5e40047f750d052b5dc4c0a93b0`
- Publication workflow: https://github.com/Wngartman/ChunkPilot/actions/runs/32445260020
- Source gate: 108 frontend, 1,696 unit, and 347 integration tests; Release build completed
  without warnings or errors.
- Consumer gates: packaged Agent, default UI, WebUI Preview, clean portable extraction, clean install,
  same-version reinstall, data-preserving uninstall, SBOM validation, checksums, publication audit,
  and public redownload verification all passed on the exact release payload.
