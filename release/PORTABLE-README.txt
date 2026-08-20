ChunkPilot 1.3.0 Alpha Snapshot — Portable Windows x64
=======================================================

1. Extract the entire ZIP to a normal writable folder.
2. Keep the Agent folder beside ChunkPilot.exe.
3. Run ChunkPilot.exe for the current default interface.
4. Run `ChunkPilot.exe --webui-preview` for the preview WebUI.

The package includes the .NET 10 runtime. Node.js and developer tools are not required.
The WebUI preview requires the Microsoft Edge WebView2 Evergreen Runtime. Most current
Windows 10 and Windows 11 systems already have it; if yours does not, install it from:
https://developer.microsoft.com/microsoft-edge/webview2/

"Portable" describes the application binaries. ChunkPilot intentionally keeps mutable
user data outside this extracted folder:

- application data and backups: %LOCALAPPDATA%\ChunkPilot
- managed servers and worlds: %USERPROFILE%\ChunkPilot\Servers
- imported servers: their original folders

Deleting the extracted application folder does not delete those locations.

This is an unsigned pre-alpha snapshot. Windows SmartScreen may warn before launch.
Verify the ZIP against SHA256SUMS.txt on the GitHub release before extracting it.
