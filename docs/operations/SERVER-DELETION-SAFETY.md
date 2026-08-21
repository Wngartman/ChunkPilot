# Server deletion safety

Delete Server is an Agent-owned, journaled operation. React can request a preflight and choose a mode,
but it cannot remove files, database rows, schedules, firewall rules, or router mappings directly.

Modes:

- **Move to Recovery** is the recommended mode for marker-proven managed servers. It stops the exact
  owned process, withdraws exact-owned Internet exposure, disables schedules, and moves managed data
  under ChunkPilot Recovery with a restore manifest.
- **Remove from ChunkPilot** removes management state only. Source folders, worlds and backups remain.
  This is the only ordinary removal path for imported/by-reference or ownership-uncertain data.
- **Permanently delete** requires a current preflight token, exact server-name confirmation, explicit
  world and backup acknowledgements, and a persistent managed-instance ownership marker.

Deletion never follows links or reparse points and never treats a path under the managed root as proof
by itself. External worlds and backup destinations are protected. Public mappings and exact-owned
Windows Firewall access must reach a truthful bounded cleanup state before registration disappears;
failure leaves the server visible and recoverable. Database cleanup is transactional and occurs only
after process, network, and data phases are terminal. A durable journal resumes or reports interrupted
work idempotently after Agent restart.
