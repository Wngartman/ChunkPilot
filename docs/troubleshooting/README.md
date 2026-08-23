# Troubleshooting

Start with the in-app diagnostics and the server's **Console** and **Overview** pages. ChunkPilot records authoritative lifecycle and operation failures without replacing unknown values with success.

- [Known bugs](BUG-REGISTER.md)
- [Product friction register](PRODUCT-FRICTION-REGISTER.md)
- [Backend presentation gaps](WEBUI-BACKEND-GAPS.md)
- [Networking behavior](../operations/NETWORKING.md)
- [Windows Firewall access](../security/WINDOWS-FIREWALL-ACCESS.md)
- [Backup safety](../operations/BACKUP-SAFETY.md)

When reporting a problem, include the ChunkPilot version from **Settings → About**, the operation ID when shown, and a local diagnostic bundle. Bundles are created and stored locally. ChunkPilot redacts recognized passwords, API keys, authorization credentials, and tokens, but deliberately retains useful diagnostic context such as player names, UUIDs, IP addresses, and private file paths. Review the ZIP before sharing it. Do not attach worlds, server JARs, API keys, or other secrets.
