# Paper plugin management

This slice adds first-class plugin inventory and management for servers whose authoritative ecosystem
is Paper, Purpur, Spigot, or Bukkit. The Paper creation and exact-build certification boundary is
documented separately in [PAPER-PLATFORM.md](PAPER-PLATFORM.md).

## Authority and safety

- The Agent owns inventory, installation, enable/disable, and removal. The WebUI cannot read server
  files or install a JAR directly.
- Local selection uses a native Windows file dialog. The Agent inspects bounded metadata first;
  React receives only that sanitized preview plus an opaque, single-use token, display filename, and
  expiry. It never receives the native path, and installation requires a separate confirmation.
- Metadata inspection opens the JAR only as a bounded ZIP. ChunkPilot does not load assemblies,
  instantiate plugin classes, or execute bytecode. JAR size, entry count, metadata size, JSON depth,
  and dependency count are bounded.
- Remove moves only the selected JAR into ChunkPilot Recovery. Generated plugin configuration stays
  in the server's `plugins` directory.
- Common bounded text configuration is discovered only in an exact plugin-ID/name directory. Saves
  use a dedicated Agent command, optimistic-concurrency hashes, atomic replacement, a Recovery copy,
  and safe stop/restart/rollback ownership. React receives only a server-relative path.
- Provider installs retain a bounded hash-keyed provenance record under ChunkPilot's own data root.
  That identity survives enable/disable moves and allows exact compatible update matching without
  guessing from a filename. A changed update filename replaces the prior JAR only after the new file
  is staged, with the prior JAR retained in Recovery.
- Mutating operations execute through the serialized Agent operation queue. A stopped server changes
  immediately. For a running server, the user can explicitly choose **Apply and restart**: the Agent
  confirms a save, stops the exact-owned process, applies the reversible JAR change, waits for full
  readiness, and restores the previous JAR or enabled state if startup fails. WebUI never sequences
  lifecycle commands itself and `/reload` is never used.

## Providers

Modrinth uses the official v2 search and project-version APIs. Search is constrained to server-side
plugin projects, the server's exact Minecraft version, and Paper-compatible loader categories.
Release resolution repeats the exact version/loader checks, accepts only `.jar` files, requires an
official SHA-512 value, and retains declared dependencies. Responses are cached in memory for five
minutes and in a bounded hash-addressed native catalog cache for offline fallback; malformed or
oversized cache entries are ignored. Downloads accept only `https://cdn.modrinth.com`, enforce declared
  size, and verify SHA-512 before installation. Required dependencies are resolved recursively from
  exact compatible provider releases, cycle-checked, shown before mutation, and installed as one
  reversible plan. A missing or ambiguous requirement blocks the plan. Optional integrations and
  load-before hints are informational; incompatibilities block.

Official references:

- <https://docs.modrinth.com/api/operations/searchprojects/>
- <https://docs.modrinth.com/api/operations/getprojectversions/>
- <https://docs.modrinth.com/api/>

Hangar is deliberately reported unavailable. ChunkPilot does not scrape Hangar pages or infer download
links. A future adapter requires a confirmed supported official API and equivalent integrity metadata.

Remote project icons are not rendered. Production WebView CSP permits app-local images only; adding
provider artwork requires a separate bounded native cache and local-origin bridge.

On 2026-08-17, a read-only live provider validation searched the official Modrinth v2 API for
server-side Paper/Bukkit/Spigot releases compatible with Minecraft 26.2 and resolved LuckPerms 5.5.71
to an exact Bukkit-family release with a primary JAR and provider SHA-512. No plugin JAR was downloaded
or executed during that provider validation.

## Current limitations

- Locally installed or sideloaded JAR metadata does not prove a provider project identity, so
  ChunkPilot does not guess updates for those files. Exact update matching is available only for
  provider-installed plugins with retained provenance.
- Runtime load health is derived only from explicit current-session Paper log evidence. An inventory
  entry without a matching enable/failure line is shown as **Unknown** or **Not running** rather than
  Loaded. Disabled and pending-restart states remain separate. Historical load health is not persisted.
- Metadata compatibility is not runtime proof. The Problems view reports unreadable metadata,
  duplicates, obvious ecosystem mismatches, and missing declared dependencies truthfully.
- Configuration editing deliberately covers only bounded YAML, JSON/JSONC, TOML, properties, and
  `.conf` files in exact identity-owned locations. Unknown ownership and arbitrary nested paths stay
  out of the editor and remain accessible through **Plugin folder**. JSON remains raw so comments,
  ordering, and unknown structures are not destructively modelled.
