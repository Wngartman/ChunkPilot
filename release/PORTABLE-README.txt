ChunkPilot 1.0.0 — Portable Windows x64
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

CurseForge is optional. Open Settings > Set up CurseForge and enter your own approved
API key in the native Windows dialog. The key is protected for your Windows user;
no shared developer key is included. Removing it does not remove installed servers.
Author download restrictions still apply.

When updating portable binaries, close ChunkPilot and let its servers finish saving
and stopping, then extract the complete new ZIP to a new folder. Do not move or
delete your server or user-data folders. To upgrade an installed alpha instead, run
the new installer over the existing installation without uninstalling first.
The visible product version is 1.0.0; Windows file/installer version 1.3.1 preserves
upgrade order from earlier 1.3.0-alpha installers. The database remains at schema 6.

The binaries are unsigned. Windows SmartScreen may warn before launch. Verify the
ZIP against SHA256SUMS.txt on the GitHub release before extracting it. Checksums
verify integrity, not publisher trust.
