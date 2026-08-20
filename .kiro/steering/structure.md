---
inclusion: always
---
# UI structure rules
Shared visual resources belong under `src/ChunkPilot.App/Themes`; reusable controls/templates precede page-specific composition. The shell owns navigation, responsive mode, global status, toasts, and command palette hosts. Pages are destination-focused and must not duplicate styles or icon mappings. Keep primary content vertically navigable without horizontal scrolling or nested main scroll viewers.
