# Vanilla runtime certification

`ChunkPilot.Certification` is a resumable, isolated command-line campaign runner for the official
Minecraft Java Edition catalog. It uses the same catalog, Java resolver, launch profiles, readiness
rules, and exact process-ownership paths as the application. Test roots, downloaded artifacts, managed
Java runtimes, ledgers, and full failure logs remain under an ignored certification cache.

## EULA boundary

The runner never infers Minecraft EULA acceptance. Without
`--accept-minecraft-eula-for-certification` it performs every official metadata, integrity-metadata,
Java, launch-profile, capability, storage, and cleanup preflight, records a terminal blocked result,
and does not download a server JAR, write `eula=true`, or start Java. The switch applies only to unique
disposable certification roots and does not affect Create Server's unchecked consent control or any
production server.

## Commands

Pre-EULA inventory and environment campaign:

```powershell
dotnet run --project .\src\ChunkPilot.Certification\ChunkPilot.Certification.csproj -c Release -- certify-vanilla --all --refresh --cache .\artifacts\vanilla-certification --max-concurrency 4
```

Resume exact runtime certification after the user deliberately authorizes test EULA acceptance:

```powershell
dotnet run --project .\src\ChunkPilot.Certification\ChunkPilot.Certification.csproj -c Release -- certify-vanilla --all --accept-minecraft-eula-for-certification --cache .\artifacts\vanilla-certification --max-concurrency 4 --timeout-seconds 240
```

The runner also supports `--version`, `--category`, `--retry-failed`, `--force`,
`--timeout-seconds`, repeated `--java <major>=<absolute-java.exe-path>` overrides, cancellation, and
an alternate `--cache` root. `--export-evidence <path>` writes compact identity-bound terminal
evidence into the production manifest. Complete passes promote support; failures keep their exact
reason without including local paths, diagnostics, ports, generated files, or downloaded artifacts.
Explicit Java overrides are health-checked for the requested major and
64-bit architecture before use; they are useful when a verified runtime is already present inside an
approved isolated test root and the certification environment cannot download another copy. Its atomic JSON ledger stores the
exact version and artifact identity, metadata revision, Java/profile evidence, timestamps, readiness
and stop evidence, generated files, categorized failure, retry count, and cleanup result. Repeated runs
reuse terminal evidence when its identities still match.

## 2026-08-16 pre-EULA campaign

All 906 official manifest entries reached a terminal pre-EULA result in 5.5802 seconds:

- 67 blocked because Mojang publishes no official dedicated-server artifact;
- 13 blocked because a safe Java requirement cannot be resolved from official or documented evidence;
- 826 blocked at the explicit certification EULA gate after metadata/profile validation;
- 0 exact runtime attempts, passes, failures, cancellations, or cleanup failures;
- 0 downloaded artifact bytes and no owned Java process before or after the run.

At that point this evidence was not runtime certification. Production support therefore remained 826
Experimental, 80 Unavailable, 0 Verified, and 0 Recommended until a deliberately authorized runtime
campaign passed.

## 2026-08-17 authorized campaign

The user explicitly authorized `eula=true` only inside the campaign's disposable roots. The official
manifest contained 907 entries at final refresh: 840 had an official dedicated-server artifact with
integrity metadata and 67 did not. Every eligible artifact reached a terminal exact result. The
campaign used verified managed Java 8, 16, 17, 21, or 25 as required, a unique loopback-only port,
`online-mode=false`, no query or RCON, and no firewall or router changes.

- 833 exact runtime passes: 96 stable releases, 480 snapshots, 198 pre-releases/release candidates,
  and 59 experimental snapshots;
- 1 exact runtime bootstrap failure (`20w20a`);
- 4 exact readiness timeouts (`17w15a`, `14w27a`, `14w27b`, and `14w10a`);
- 2 exact clean-stop failures (`18w47a` and `18w47b`);
- 67 entries blocked because Mojang publishes no official dedicated-server artifact, including all
  26 Beta and all 35 Alpha entries in the current manifest;
- 0 unresolved Java or launch profiles, 0 cancelled/pending entries, 0 residual work roots, 0
  cleanup failures after reconciliation, and 0 owned Java processes after the campaign;
- 28,251,887,518 bytes of hash-addressed official server artifacts and 29,170,027,898 bytes for the
  complete ignored certification cache including managed Java and compact diagnostics;
- ledger wall-clock span 22 hours 32 minutes 57 seconds, including the PC restart and idle/resume
  intervals; this is not continuous CPU time.

Production support is now 1 Recommended, 95 Verified, 737 Experimental, and 74 Unavailable.
`Recommended` is the exactly certified latest stable release. Other exactly passed stable releases
are `Verified`; development builds remain risk-labelled `Experimental` even when they passed exact
runtime certification. `Unavailable` now means either no official server artifact or one of the seven
specific exact runtime failures above.

## Promoted runtime evidence

Only exact passed identities are promoted into the offline production catalog. Promotion matches the
version ID, Mojang server SHA-1, Mojang metadata SHA-1, and Java major; any identity change falls back
to metadata-only classification until that exact artifact is certified again. The compact reviewed
registry is the embedded `Resources/vanilla-runtime-certification-v1.json` manifest loaded by
`VanillaRuntimeCertificationEvidence.cs`. Downloaded JARs, Java runtimes, disposable
worlds, full ledgers, and failure logs remain ignored under `artifacts/vanilla-certification`.
