# Contributing to ChunkPilot

ChunkPilot is a Windows desktop application built with .NET/WPF, WebView2, React, and TypeScript. The repository is publicly readable, but no open-source source-code license is granted; contribution rights are not the same as a general license to redistribute the source.

## Before changing code

1. Read `AGENTS.md` and the relevant documentation under `docs/`.
2. Work on a focused branch and keep user server data out of tests.
3. Use temporary data roots, fake servers, and mocked provider responses.
4. Preserve lifecycle ownership, transactional file operations, and truthful capability states.

## Proportionate validation

Use the repository development command:

```powershell
.\scripts\dev-build.ps1 -Tier Quick
```

Available tiers are:

- `Quick` for bounded WebUI, CSS, copy, and deterministic frontend work.
- `Feature` for ordinary bridge, provider, and non-destructive application features.
- `HighRisk` for lifecycle, ownership, deletion, backups, networking, schemas, or secrets.
- The immutable full distribution workflow is reserved for an explicit release.

The command writes a runnable development package to `artifacts/dev-current` and prints the exact launch command. See [development documentation](docs/development/README.md) for prerequisites and additional checks.

## Pull requests

- Keep changes scoped and explain the user-visible result.
- Add regression coverage for defects and safety invariants.
- Report checks that were skipped or unavailable; compilation alone is not runtime verification.
- Do not include secrets, personal paths, real server data, caches, generated packages, or release artifacts.
- Do not enable public access, accept a Minecraft EULA, or run arbitrary downloaded server content in ordinary tests.

Security vulnerabilities should be reported through [SECURITY.md](SECURITY.md), not a public issue.
