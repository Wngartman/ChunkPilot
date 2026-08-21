ChunkPilot 1.3.0 Alpha 5 — Portable Windows x64
================================================

1. Extract the entire ZIP to a normal writable folder.
2. Keep the Agent, Assets, and WebUi folders beside ChunkPilot.exe.
3. Run ChunkPilot.exe.

The package includes the .NET 10 runtime. Node.js and developer tools are not required.
ChunkPilot uses the Microsoft Edge WebView2 Evergreen Runtime. Most current Windows 10
and Windows 11 systems already have it. If it is absent, ChunkPilot presents a native
recovery window with an explicit link to Microsoft's installer.

"Portable" describes the application binaries. ChunkPilot intentionally keeps mutable
user data outside this extracted folder:

- application state and default backups: %LOCALAPPDATA%\ChunkPilot
- managed servers and worlds: %USERPROFILE%\ChunkPilot\Servers
- imported servers: their original folders

Deleting the extracted application folder does not delete those locations.

This alpha is unsigned. Windows SmartScreen may warn before launch. Verify the ZIP
against SHA256SUMS.txt on the GitHub release before extracting it.
