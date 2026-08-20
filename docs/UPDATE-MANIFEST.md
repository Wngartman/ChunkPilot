# Direct update manifest

The Direct Manifest adapter reads a small JSON document over HTTPS. The manifest URL and every download URL must use HTTPS. `packId` must match the linked source when the link specifies one.

```json
{
  "packId": "example-server-pack",
  "versions": [
    {
      "packId": "example-server-pack",
      "versionId": "2026.07.24-1",
      "versionName": "2.4.0",
      "releaseChannel": "stable",
      "publishedAt": "2026-07-24T18:00:00Z",
      "minecraftVersion": "1.21.1",
      "loader": "NeoForge",
      "loaderVersion": "21.1.172",
      "requiredJavaMajor": 21,
      "downloadUrl": "https://downloads.example.net/example-server-2.4.0.zip",
      "fileSize": 734003200,
      "sha256": "lowercase-or-uppercase-64-hex-character-sha256",
      "fileName": "example-server-2.4.0.zip",
      "changelog": "Updated the pack and server scripts.",
      "migrationNotes": "Back up before opening an existing world. Config format changed.",
      "packageType": "zip"
    }
  ]
}
```

Required per version:

- `versionId`
- `downloadUrl`
- `sha256`

Supported `releaseChannel` values are `stable`, `beta`, and `alpha`. `packageType` is `zip` or `jar`. Publication times use ISO 8601. `fileSize` is bytes. `requiredJavaMajor` is zero or omitted when unknown.

The server package must contain a controllable launch script or recognizable server JAR. A client-only modpack archive is not a server package. ChunkPilot downloads to local cache, checks the declared SHA-256 before extraction, calculates its own SHA-256 for history, and rejects a manifest whose `packId` does not match the link.

For custom feeds, publish this same format at a stable HTTPS URL and link it as **DirectManifest**. Redirects are handled by the system HTTP client, but the final package URL still must be HTTPS.
