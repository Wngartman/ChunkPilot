- A stale prior-running, crash-recovery, or restart-journal observation can no longer start a server
  after Windows login, App startup, or Agent startup. Only explicit autostart policy or a due
  user-created schedule authorizes startup.
- Manual **Stop server** now suppresses a pending automatic restart immediately and cancels the owned
  operation before waiting for the server's serialized operation queue.
- Stop reconciles the exact owned process and persisted running state before reporting completion. A
  non-cooperative operation produces an actionable bounded failure instead of an indefinite
  `Stopping` state.
