# Database migrations

Schema version 5 is an additive migration applied with `CREATE TABLE IF NOT EXISTS` and `PRAGMA user_version=5`.

It preserves servers, activity, backups, schedules, settings, EULA records, operations, statistics, instance history, update sources, update history, version snapshots, update preferences, and rollback history from 1.0-1.2.

Version 4 adds capability profiles, quick-start presets, catalog history/favorites, managed Java runtimes and assignments, networking/tunnel/crossplay configuration, global access rules, gamerule profiles, datapack/resource-pack records, automation recipes, share settings, diagnostic history, process identities, UI sessions/close intent, previous running state, and file-operation events.

Version 5 adds `creation_journal`, the durable record of an in-flight managed-server creation, with an index on the canonical destination. One row exists per unfinished creation and is deleted once the operation is finalised; lasting evidence stays in `instance_history`. The row holds identity, canonical destination and staging paths, timestamps, the transaction phase, the evidence flags recovery keys off, the outcome, the recovery attempt count and the planned `ServerDefinition`. It holds no secrets, credentials or payloads. Nothing else changed: no existing table was altered and no data was rewritten, so an older database gains the table on first open.

A row whose `schema_version` is newer than this build understands, or whose payload cannot be parsed, is read as unreadable rather than as absent. It is never acted on, never deleted, and still reserves its destination against a new creation — treating it as "no operation here" would let a later run reuse a folder another build is part-way through owning.

Migration tests create older-schema fixtures, initialize the current store, confirm the prior server row remains byte-for-byte deserializable, and assert the current schema version. No destructive table rebuild is used.
