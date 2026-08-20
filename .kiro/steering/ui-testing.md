---
inclusion: always
---
# UI validation contract
For UI changes run `git diff --check`, the relevant unit/integration tests, and a Release build. Use only synthetic fixtures, temporary roots, fake agents, and mocked providers. At milestones run isolated packaged startup and WM_CLOSE smoke, verify intended synthetic Agent shutdown, unrelated Agent survival, no invisible App, and cleanup. Treat compositor screenshots as invalid unless the captured window is identified as ChunkPilot.
