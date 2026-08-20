# Beginner quick start

1. Open ChunkPilot and choose **Create Server**.
2. Choose the plain-language starting point:
   - **Vanilla With Friends** for official Minecraft and unmodified clients.
   - **Faster Vanilla** for Paper, Purpur, or a reviewed Fabric performance setup.
   - **Modpack Server** for an exact provider-confirmed server pack.
   - **Plugins and Minigames** for Paper/Purpur and Vanilla clients.
   - **Java and Bedrock Crossplay** for a compatible Paper or Fabric server. ChunkPilot can install verified Geyser/Floodgate packages; authentication and the generated first-run configuration still require review.
   - **Bedrock Dedicated Server** only when an official supported Windows package is available.
   - **Import Existing Server** from the separate import-by-reference action.
   - **Advanced Custom Server** for the complete launch profile.
3. Load or enter the exact Minecraft version. Loader installs also require an exact loader version in **Build** when an older build is wanted.
4. Leave **private managed Java** enabled. ChunkPilot chooses Java 8, 16, 17, or 21 from Minecraft evidence, installs Eclipse Temurin per user, verifies SHA-256, and shows the absolute `java.exe`.
5. Review RAM, port, player count, and the Minecraft EULA checkbox.
6. Install. Files are downloaded or copied into a unique staging folder, validated, and moved into the final folder only when complete.
7. Start the server, open **Share With Friends**, choose a networking method, and run local checks.

Vanilla With Friends enables online mode and the whitelist, limits the initial server to eight players, and creates a daily verified-backup schedule. It does not automatically expose the server to the internet.

If installation fails, the server is not registered. The staging log path shown in the wizard contains the redacted failure details.
