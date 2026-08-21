# ChunkPilot 1.3 data safety

- Normal UI close never blocks the WPF window. It sends `SafeApplicationExit`; the separate agent saves and stops the recorded running servers, logs results, then exits.
- A missing heartbeat or pipe disconnect is not proof of exit. Exact UI-process death is: it atomically revokes public leases and starts the same world-safe managed-server shutdown as normal close. Minimize/tray keeps hosting.
- Every managed installation uses a unique operation ID and staging directory. A failed/cancelled install is never registered.
- Loader installers execute through an absolute Java path with an argument list, captured output, timeout, cancellation, and no downloaded batch execution.
- Managed Java is per user and never modifies system Java.
- Backups use `.partial`, verify the archive, then rename atomically. Partial archives are not listed as usable backups.
- Important configuration writes create Recovery copies. Loaded text includes a content hash; an external edit blocks overwrite until reload.
- Content identity uses provider ID and hash. Sideloaded and duplicate active items are surfaced; replaced owned JARs belong in Recovery.
- Process records include PID, start time, executable, working directory, command signature, and relationship. PID alone is never enough to attach. A matching surviving process without full console evidence is marked detached/unknown and a duplicate start is blocked.
- Manual stop invalidates pending crash recovery. Safe restart performs one intended start. Crash restarts are bounded and back off.
- Database schema v4 is additive and retains all 1.0-1.2 tables and rows.

Uninstall preserves `%LOCALAPPDATA%\ChunkPilot` data and server folders by default. Imported servers are referenced in place and are never application-owned.
