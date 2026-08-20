# Competitor lessons for ChunkPilot 1.3

Research was limited to current public product pages, documentation, repositories, and issue trackers. No competitor source, assets, branding, layouts, private APIs, or internal formats were copied.

## auto-mcs

- Useful idea: creation should begin with the outcome the player wants, while software, networking, backups, and maintenance are managed behind that choice.
- User problem: a beginner knows “Vanilla with friends” or “modpack,” not which loader, Java version, or launch flags to choose.
- Common failure: automation becomes difficult to audit when it hides effective launch and file decisions.
- ChunkPilot decision: quick-start presets produce explicit, reviewable properties and capability evidence; Advanced retains the exact launch profile.
- Not copied: branding, text, interface, internal automation, and implementation.
- Safeguards: preset-output tests, absolute-Java tests, transactional staging, and no partial registration.
- Sources: [auto-mcs server manager](https://www.auto-mcs.com/guides/server-manager), [getting started](https://www.auto-mcs.com/guides/getting-started).

## Fork

- Useful idea: a native desktop manager can keep creation and everyday control approachable.
- User problem: manually locating server JARs and editing scripts is unnecessary friction on Windows.
- Common failure: version/Java choices can still be opaque when a manager assumes prior server knowledge.
- ChunkPilot decision: use a native WPF flow, exact versions, managed Java, plain-language choices, and visible effective paths.
- Not copied: layout, visual assets, wording, or code.
- Safeguards: exact-version filtering, class-file inspection, 64-bit runtime selection, and no system PATH changes.
- Sources: [Fork](https://www.fork.gg/), [Fork documentation](https://www.fork.gg/Docs).

## MC Server Soft

- Useful idea: background ownership, scheduling, and multiple-server control matter in a desktop manager.
- User problem: schedules and long stops must work when the dashboard is not in the foreground.
- Common failure: users report confusion when the manager/UI lifecycle is coupled to the Minecraft process lifecycle.
- ChunkPilot decision: the per-user agent owns servers; UI close is a short intent handoff; exact UI-process death triggers fail-closed public lease revocation and world-safe managed-server shutdown, while a pipe disconnect alone does nothing.
- Not copied: UI, terminology, code, or storage.
- Safeguards: UI-session heartbeat, normal-versus-unexpected intent tests, process identity records, and background safe-stop integration tests.
- Sources: [MC Server Soft docs](https://docs.mcserversoft.com/), [FAQ](https://docs.mcserversoft.com/faq).

## Crafty Controller

- Useful idea: creation templates, backup policy, schedules, and configuration belong in one coherent operating flow.
- User problem: backups and timed actions are often added only after a failure.
- Common failure: incomplete archives or a schedule colliding with lifecycle work can look successful.
- ChunkPilot decision: safe defaults include daily backup and backup-before-update; server operations remain serialized.
- Not copied: browser dashboard, Docker deployment model, UI, or task format.
- Safeguards: `.partial` backup naming, verification before atomic rename, operation journal, and schedule/lifecycle tests.
- Sources: [server creation](https://docs.craftycontrol.com/pages/user-guide/server-creation/minecraft/), [backup manager](https://docs.craftycontrol.com/pages/user-guide/backup-manager/), [task scheduler](https://docs.craftycontrol.com/pages/user-guide/task-scheduler/).

## PufferPanel

- Useful idea: templates and a persistent daemon separate management from a transient client.
- User problem: restart races and process ownership become dangerous after manager restarts.
- Common failure: public issue reports include restart loops and operations racing process exit.
- ChunkPilot decision: lifecycle intent is separate from exit status; manual stop invalidates delayed crash recovery; a safe restart permits one intended start.
- Not copied: daemon/API architecture, templates, web panel, or code.
- Safeguards: per-server operation gate, lifecycle generation, bounded crash attempts, process start-time identity, and PID-reuse rejection.
- Sources: [PufferPanel documentation](https://docs.pufferpanel.com/en/3.x/index.html), [public issue tracker](https://github.com/pufferpanel/pufferpanel/issues).

## MCSManager

- Useful idea: a manager should expose real process state, console output, schedules, and instance-specific configuration.
- User problem: detached or externally launched processes are easy to mistake for controlled processes.
- Common failure: attaching by PID or assuming a launcher still owns a child risks controlling the wrong process.
- ChunkPilot decision: persist PID, start time, executable, working directory, command signature, and relationship evidence; never reattach by PID alone.
- Not copied: panel, protocol, code, templates, or account model.
- Safeguards: strict identity policy, PID-reuse tests, detached/unknown state, and duplicate-start prevention.
- Sources: [MCSManager documentation](https://docs.mcsmanager.com/), [MCSManager repository](https://github.com/MCSManager/MCSManager).

## AMP

- Useful idea: guided application configuration and explicit instance state are valuable for operators.
- User problem: broad control panels can expose irrelevant controls and make a small local server feel like hosting infrastructure.
- Common failure: advanced concepts appear before the user has a working server.
- ChunkPilot decision: central capability profiles hide impossible Java/Bedrock, mod/plugin, world, and update controls; Advanced stays available.
- Not copied: commercial features, deployment system, UI, configuration format, or text.
- Safeguards: capability-policy tests and evidence/reason fields for unavailable features.
- Source: [AMP installation documentation](https://cubecoders.com/amp/install).

## Prism Launcher

- Useful idea: exact versions, per-instance Java, portable imports, and clear troubleshooting make modded Minecraft approachable.
- User problem: “latest” is insufficient when a pack requires an exact loader, game, and pack version.
- Common failure: ZIP imports and Java selection fail when package layout or runtime evidence is assumed.
- ChunkPilot decision: catalog versions remain explicit; Java is per server; archives use traversal-safe unique staging.
- Not copied: launcher UI, instance format, assets, or code.
- Safeguards: exact-version selection, highest class-version scan, safe ZIP tests, and unique operation directories.
- Sources: [ZIP import](https://prismlauncher.org/wiki/help-pages/zip-import/), [troubleshooting](https://prismlauncher.org/wiki/overview/troubleshooting/).

## ATLauncher

- Useful idea: instance creation, content management, and dependency-aware browsing should feel like one workflow.
- User problem: filenames alone cannot reliably identify duplicate or incompatible add-ons.
- Common failure: manual sideloading followed by an update creates duplicate active versions.
- ChunkPilot decision: reconcile provider ID and cryptographic hash before content work; surface sideloaded items and keep replaced files in Recovery.
- Not copied: launcher interface, data formats, branding, provider behavior, or code.
- Safeguards: duplicate-ID/hash tests, client-only exclusion, compatibility states without percentages, and provider-hash verification.
- Sources: [creating an instance](https://wiki.atlauncher.com/getting-started/creating-an-instance/), [managing content](https://wiki.atlauncher.com/getting-started/managing-content/).

## Cross-cutting implementation choices

ChunkPilot intentionally remains a local native application: no browser dashboard, Docker manager, embedded scripting runtime, cloud account, telemetry, or marketplace polling. Provider access is lazy, cache-backed, and limited to the provider selected by the user. The most repeated technical lessons became tests: lifecycle intent, exact versions, Java compatibility, isolated staging, verified archives, bounded console memory, duplicate reconciliation, LAN/public separation, incomplete backup exclusion, Unicode, and schema preservation.
