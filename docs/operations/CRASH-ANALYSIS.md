# Local crash analysis

Unexpected Agent-owned server exits create a bounded local `CrashAnalysisReport` before any automatic
restart is scheduled. The report correlates the current run, exit code, Java and platform identity,
active operation, console tail, newest relevant logs, installed add-on metadata, and pack provenance.
It does not recursively scan worlds, execute add-ons, call a cloud service, or treat one regex match as
proof.

Confidence is `Unknown`, `Possible`, `HighlyLikely`, or `Confirmed`. Confirmed requires authoritative or
corroborated evidence; missing, stale and truncated evidence remain visible. The snapshot carries only a
compact summary and report ID. Full bounded evidence is requested through the typed WebUI bridge and is
stored locally in the existing diagnostics history.

The first WebUI actions are deliberately narrow: open console, open the relevant log folder, create a
redacted support bundle, or retry through the existing lifecycle authority. ChunkPilot does not delete,
replace, disable, or rewrite a suspected user-owned add-on from a diagnostic guess.

Creation failures can attach the same report model. Automated fixtures cover memory exhaustion, Java
mismatch, missing dependencies, duplicate/corrupt JARs, world locks, bind conflicts, readiness failures,
missing/stale/malformed evidence, false positives, cancellation, persistence, and redaction.
