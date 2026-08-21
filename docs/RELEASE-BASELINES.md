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
