# Backup and restore safety

ChunkPilot's default archives live outside server roots under `%LOCALAPPDATA%\ChunkPilot\Backups\<ServerId>`. A destination inside the source tree is rejected to prevent recursive backup growth.

Backup creation streams source files into a temporary ZIP, computes SHA-256 hashes, writes a manifest, closes the archive, verifies it **while it is still `.partial`**, and only then promotes it to the final name. A backup that fails verification is never renamed into place and no record is written for it, so an unverified archive cannot be offered as a restore point. Failed temporary archives and their orphaned manifests are cleaned, and a cleanup failure never replaces the exception that explains the real problem.

The manifest records the bytes that actually reached the archive and the hash of those bytes. Verification therefore compares the archive against itself and stays truthful for a file the server appends to while the backup runs, such as the current log. World data does not move during a backup because saving is frozen first (below).

## Running-server backups

A backup of a running server coordinates a consistent save state through the Agent before anything is read: automatic saving is turned off, the world is flushed, and the flush is confirmed on the server's own console. Saving is turned back on in a `finally`, so it is restored whether the backup succeeded, failed or was cancelled. The whole operation runs on the server's single operation queue, so it is exclusive with every other data operation.

### Locked files

Exclusion patterns cover logs, caches and configured paths without silently skipping world data. A pattern containing no `/` is a file-name pattern and matches at any depth, the way ignore files behave everywhere; a pattern that does contain `/` stays anchored at the server root.

That distinction is a data-safety rule, not a convenience. Minecraft holds an **exclusive byte-range lock** on `session.lock` in every loaded world folder for as long as the world is open, and reading a locked range fails with ERROR_LOCK_VIOLATION — "The process cannot access the file because another process has locked a portion of the file." The default profile had always excluded `session.lock`, but while the pattern was root-anchored it never matched `world/session.lock`, and that one file failed the whole backup of any running server.

`session.lock` is the only file ChunkPilot treats this way, and it is a documented lock artifact rather than data: the server recreates it on every start and it holds no world state.

Any other locked file is a hard failure, never a silent skip:

- Opening a file is retried up to three times, and only for a sharing violation, which genuinely clears while another process finishes a write.
- A byte-range lock is not retried, because it is held deliberately for as long as its owner wants it.
- The file is probed for readability *before* an archive entry is created, so a locked file cannot leave a half-written entry behind. An entry is written exactly once; the earlier retry-around-the-write produced a duplicate entry under the same name, which verification then read.
- The failure names the exact path, states that no backup was created and that nothing in the server folder was changed, and leaves no `.partial` archive behind.

Retention is applied only to records and archives owned by the selected server/profile. Count, age, and storage limits remove the oldest eligible ChunkPilot backup; imported server content is never a retention target.

Restore requires a stopped server and explicit confirmation. By default it first creates a pre-restore safety backup. Archive paths are canonicalized and ZIP-slip entries are rejected. Content is extracted to staging, validated against the manifest, then copied into the server through scoped replacements. Unrelated files are preserved.

Backup deletion is a separate, confirmed action identifying the exact archive. The uninstaller never deletes backup ZIP archives. If the user elects not to retain settings during uninstall, only the SQLite record database, diagnostic bundles, and ChunkPilot logs are removed.

## Version snapshots and rollback

A conventional backup and a version rollback snapshot serve different purposes. Unattended updates require a newly verified conventional backup. Every installed pack update also creates a verified compressed full snapshot of the active server, including worlds, before downloading or switching anything.

The snapshot ZIP embeds a unique manifest with the size and SHA-256 of every original file. ChunkPilot verifies the archive after creation. Rollback verifies the archive again, extracts it into a same-volume candidate, rehashes every extracted file, and only then switches it into the active path. Mutable worlds are copied into the archive; hard links are never used.

The pre-update directory and operation journal remain until the new server passes both console readiness and a local Minecraft status query. If startup fails, ChunkPilot terminates the failed process tree, restores the verified prior snapshot, restores the prior launch/source records, and restarts the old version if it had been running. Agent startup recovers an interrupted switch before loading managed server definitions.

Version deletion is limited to an inactive snapshot owned by the selected server. The confirmation lists its archive and manifest. ChunkPilot refuses to delete the active version or the last verified usable version and moves removed snapshot files to `%LOCALAPPDATA%\ChunkPilot\Recovery\DeletedVersionSnapshots` rather than erasing them immediately. It does not touch the active server folder or separately managed worlds.

Scheduled expiry runs only when the active version is marked healthy, a verified conventional backup exists, the snapshot is past its retention date, it is not marked permanent, and another usable version remains. The first healthy confirmation offers 7-day, 30-day, permanent, or manual retention; no rollback snapshot is automatically deleted immediately after startup.
