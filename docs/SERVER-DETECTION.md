# Server detection

Import is a read-only, bounded inspection. ChunkPilot prioritizes root-level scripts, jars, argument files, and known configuration locations. It excludes world region data, libraries, logs, backups, caches, mods, plugins, and source-control data from broad candidate traversal.

Detection recognizes common Vanilla, Paper, Purpur, Spigot/Bukkit, Fabric, Quilt, Forge, and NeoForge signals while retaining manual profiles for custom executables. Scripts are parsed as text and never executed during detection. Parsed evidence includes Java path, quoted arguments, argument files, environment variables, working directory changes, nested scripts, and detaching `start` usage.

Every candidate has a confidence score, reason, parsed command, working directory, and warnings. The user must confirm the first import; uncertainty is not silently resolved.

Java discovery examines the selected script, `JAVA_HOME`, `PATH`, applicable registry keys, common installation locations, and bundled runtimes. Calling `java -version` is limited to discovered executables and does not alter the server.

Tests assert that detection does not modify the scanned fixture. Any separately authorized real-server validation is read-only; automated tests never use a real user server folder.
