# Development workflow

ChunkPilot uses proportionate validation so a bounded UI fix does not invoke the complete installer and release factory.

## Prerequisites

- Windows 10 or Windows 11 x64
- The .NET SDK pinned by `global.json`
- Node.js 24 for WebUI development
- WebView2 Evergreen Runtime for packaged UI testing

Repository-local dependencies and output stay under the repository or `artifacts/`; do not install global npm packages.

## Build a runnable development package

```powershell
.\scripts\dev-build.ps1 -Tier Quick
```

The command checks the worktree, records a lightweight recovery patch when needed, runs the selected validation tier, and publishes the current application to `artifacts/dev-current`. It prints the exact launch command on success.

Use `-Tier Feature` for ordinary provider, bridge, or non-destructive application changes. Use `-Tier HighRisk` for lifecycle, process ownership, backups, deletion, networking, schemas, or secrets.

## Full release validation

The full release factory runs only for an explicit release or a frozen high-risk milestone:

```powershell
.\scripts\publish.ps1 -BuildInstaller -ReleaseTag v1.3.0-alpha.4
.\scripts\package-release.ps1 -ReleaseTag v1.3.0-alpha.4
```

Release automation builds once, tests those exact artifacts, and publishes only the tested commit. See [release instructions](../release/RELEASING.md).

## Safety

Use isolated temporary data roots and fixture servers. Never point development checks at a real world, production AppData, firewall, router, or unrelated process. Provider credentials are developer-owned secrets and must not enter source, React state, logs, screenshots, or packages.
