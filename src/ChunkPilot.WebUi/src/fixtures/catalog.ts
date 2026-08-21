import type { BridgeAdapter } from '../bridge/client';
import type { BridgeEvent, BridgeMethod, ConnectivitySnapshot, ServerSummary, WebUiSnapshot } from '../bridge/types';

const now = '2026-08-14T16:42:00-06:00';
const gib = 1024 ** 3;

function catalogVersion(id: string, releaseKind: string, support: string, javaMajor: number | null, selectable = true) {
  const channel = releaseKind === 'Release' ? 'Stable' : releaseKind === 'Alpha' || releaseKind === 'Beta' ? 'Historic' : 'Snapshot';
  const available = selectable;
  return {
    id, label: `Minecraft ${id}`, channel, releaseKind, releaseTime: now, javaMajor,
    javaSource: javaMajor ? 'OfficialMetadata' : 'Unknown', support,
    supportReason: support === 'Recommended' ? 'Latest stable release with complete official metadata and a resolved managed launch profile.' : support === 'Verified' ? 'Stable release with complete official metadata and a resolved managed launch profile.' : support === 'Experimental' ? 'This official build has not been runtime-certified by this ChunkPilot build.' : 'ChunkPilot has not established a safe managed launch profile for this historical build.',
    selectable, hasServerArtifact: available, artifactSize: available ? 55_400_000 : null,
    hasIntegrityMetadata: available, launchProfile: { kind: available ? 'ModernEulaNogui' : 'Unknown', arguments: available ? 'nogui' : '', requiresEulaFile: available, evidence: available ? 'Resolved managed launch profile.' : 'Launch behavior unavailable.' },
    certification: { level: support === 'Recommended' || support === 'Verified' ? 'RuntimeCertified' : available ? 'MetadataValidated' : 'Inventoried', runtimeLaunched: support === 'Recommended' || support === 'Verified', readinessConfirmed: support === 'Recommended' || support === 'Verified', cleanShutdownConfirmed: support === 'Recommended' || support === 'Verified', runtimeValidatedAt: support === 'Recommended' || support === 'Verified' ? now : null, limitations: support === 'Experimental' ? ['This exact version has not been isolated-runtime certified by this ChunkPilot build.'] : [] },
    capabilities: { serverIcon: available, formattedMotd: available, playerManagement: available, modernServerProperties: available, statusQuery: available, datapacks: releaseKind === 'Release', managedVersionChange: available },
    warnings: support === 'Experimental' ? ['This is an in-development build. Worlds made on it may not open in a later release.'] : [],
    evidence: available ? ['Official dedicated server artifact', 'Official SHA-1 integrity metadata', `Java ${javaMajor} (OfficialMetadata)`, 'Managed launch profile: ModernEulaNogui'] : [],
    provenance: 'Official Mojang version metadata'
  };
}

const fixtureCatalogVersions = [
  catalogVersion('1.21.8', 'Release', 'Recommended', 21),
  catalogVersion('1.21.7', 'Release', 'Verified', 21),
  catalogVersion('1.20.6', 'Release', 'Verified', 21),
  catalogVersion('1.20.4', 'Release', 'Verified', 17),
  catalogVersion('26w33a', 'Snapshot', 'Experimental', 21),
  catalogVersion('1.21.9-pre2', 'PreRelease', 'Experimental', 21),
  catalogVersion('1.21.9-rc1', 'ReleaseCandidate', 'Experimental', 21),
  {
    ...catalogVersion('b1.8.1', 'Beta', 'Unavailable', 8, false),
    javaSource: 'ChunkPilotPolicy',
    supportReason: 'Mojang no longer publishes an official dedicated-server artifact for this version. A reviewed user-owned server JAR is required.',
    launchProfile: { kind: 'LegacyNogui', arguments: 'nogui', requiresEulaFile: true, evidence: 'Curated exact historical launch profile.' },
    capabilities: { serverIcon: false, formattedMotd: false, playerManagement: true, modernServerProperties: false, statusQuery: true, datapacks: false, managedVersionChange: false },
    evidence: ['Curated Java 8 policy', 'Curated headless launch profile', 'User-supplied artifact required']
  },
  catalogVersion('b1.7.3', 'Beta', 'Unavailable', null, false),
  catalogVersion('a1.2.6', 'Alpha', 'Unavailable', null, false),
  ...Array.from({ length: 897 }, (_, index) => catalogVersion(
    `fixture-${String(index + 1).padStart(3, '0')}`,
    index % 5 === 0 ? 'Snapshot' : 'Release',
    index % 5 === 0 ? 'Experimental' : 'Verified',
    index % 7 === 0 ? 17 : 21
  ))
];

function server(overrides: Partial<ServerSummary> = {}): ServerSummary {
  return {
    id: '8bb67c1f-6eb4-45a7-bb41-c97da6be0f42',
    name: 'Copper Valley',
    state: 'Running',
    gameKind: 'Minecraft',
    ecosystem: 'Vanilla',
    minecraftVersion: '1.21.8',
    port: 25565,
    managed: true,
    playersOnline: 4,
    playersMaximum: 12,
    playerStatus: {
      online: 4,
      maximum: 12,
      source: 'ModernStatus',
      exact: true,
      checkedAt: now,
      detail: 'Exact count from the modern Minecraft status protocol.'
    },
    uptimeSeconds: 12_842,
    cpuPercent: 18.4,
    memoryBytes: 3.18 * gib,
    maximumMemoryBytes: 6 * gib,
    localAddress: 'localhost:25565',
    lanAddress: '192.168.1.42:25565',
    connectionMode: 'HomeNetwork',
    publicAddress: null,
    publicAddressKind: null,
    publicAddressObservedAt: null,
    publicReachability: 'not-confirmed',
    lastBackupAt: '2026-08-14T14:10:00-06:00',
    lastError: null,
    crashAnalysis: null,
    iconUrl: './brand/chunkpilot-64.png',
    modpack: null,
    samples: Array.from({ length: 42 }, (_, index) => ({
      at: new Date(Date.parse(now) - (41 - index) * 15_000).toISOString(),
      cpuPercent: 10 + Math.sin(index / 4) * 6 + (index % 7),
      memoryBytes: (2.6 + index / 80 + Math.sin(index / 7) * .08) * gib
    })),
    capabilities: { console: true, players: true, files: true, content: 'datapacks', versioning: 'vanilla', backups: true, versions: true },
    ...overrides
  };
}

function connectivity(selected: ServerSummary | null): ConnectivitySnapshot | null {
  if (!selected) return null;
  return {
    serverId: selected.id,
    mode: 'HomeNetwork',
    modeTitle: 'Home network',
    modeSummary: 'People on this Wi-Fi or wired network can use the LAN address when Windows allows it.',
    status: { title: 'Available on your home network', detail: `People on this network can use ${selected.lanAddress ?? 'the LAN address'} when Windows allows it.`, tone: 'info' },
    addresses: { local: selected.localAddress, lan: selected.lanAddress, publicVerified: null, routerReported: null, publicVerifiedAt: null, lastKnownPublic: null, lastKnownPublicAt: null },
    router: {
      phase: 'Off', title: 'Internet hosting is off', summary: 'Nothing has been opened on the router for this server.', badge: 'Off', tone: 'neutral', busy: false, enabled: false,
      consentRequired: false, consentPoints: [], canCheck: true, canEnable: false, canStop: false, canCancel: false, canRetryCleanup: false,
      routerReportedCaveat: 'A router-reported address is not proof that friends can connect.', upstreamNotice: null,
      mechanism: 'Not checked', transport: 'TCP', gateway: 'Not identified', internalEndpoint: selected.lanAddress ?? 'Not established', externalPort: '—', lease: 'None', lastChecked: 'Never', addressClass: 'Unknown', detail: 'No router operation has run for this server.'
    },
    firewall: {
      phase: 'NeedsPermission', title: 'Windows Firewall permission needed', summary: 'Windows may block other computers on this network until you approve one exact rule.', badge: 'Action needed', tone: 'warning', busy: false, configured: false,
      consentRequired: false, consentTitle: 'Allow this server through Windows Firewall?', consentMessage: 'Windows will show an administrator prompt. ChunkPilot will create one inbound TCP rule for this exact server Java executable, port, and current network profile.',
      primaryAction: 'Allow through firewall', secondaryAction: 'Check again', canRemove: false, canCancel: false,
      network: 'Home network', port: `TCP ${selected.port}`, profile: 'Private', enabled: 'Enabled', lastChecked: 'Today at 4:42 PM', detail: 'No firewall mutation has run in this fixture.'
    },
    external: {
      phase: 'Ineligible', blocker: 'DirectInternetOff', title: 'Outside-in check not ready', summary: 'Turn on Internet hosting before checking whether friends can join.', badge: 'Not ready', tone: 'neutral', busy: false, canCheck: false, canCancel: false,
      firstUseNotice: null, verifiedEndpoint: null, verifiedAt: null, checkedAt: 'Not checked', observedAddress: 'Not reported', routerAddress: 'Not reported', port: 'Not established', connectTime: 'Not measured', addressComparison: null, upstreamAssessment: null, detail: 'No external check has run for this server.'
    }
  };
}

function snapshot(servers: ServerSummary[]): WebUiSnapshot {
  const selected = servers[0] ?? null;
  return {
    revision: 1,
    capturedAt: now,
    agentConnected: true,
    appVersion: '1.3.0',
    build: { productVersion: '1.3.0-alpha.4+fixture', releaseTag: 'v1.3.0-alpha.4', gitSha: 'fixture', buildTimestampUtc: '2026-08-20T00:00:00Z', schemaVersion: '6', architecture: 'x64', defaultUi: 'WebUI' },
    selectedServerId: selected?.id ?? null,
    operation: null,
    statusMessage: null,
    host: { cpuPercent: 31.6, usedMemoryBytes: 21.3 * gib, totalMemoryBytes: 64 * gib, freeDiskBytes: 612 * gib, totalDiskBytes: 1.81 * 1024 * gib, cpuModel: 'AMD Ryzen 9 7950X3D' },
    servers,
    connectivity: connectivity(selected),
    playerAccess: selected?.gameKind === 'Minecraft' ? {
      serverId: selected.id,
      serverRunning: selected.state === 'Running',
      whitelistEnabled: true,
      supportsAllowlist: true,
      supportsOperators: true,
      supportsPlayerBans: true,
      supportsIpBans: true,
      capabilityKnown: true,
      error: null
    } : null,
    issues: selected?.crashAnalysis ? [{
      issueId: `crash:${selected.crashAnalysis.code}`,
      serverId: selected.id,
      articleId: selected.crashAnalysis.code === 'port.conflict' ? 'port-binding-failed' : 'server-stopped-unexpectedly',
      category: 'startup', severity: 'error', title: selected.crashAnalysis.title,
      summary: selected.crashAnalysis.summary,
      evidenceSummary: selected.crashAnalysis.evidence[0]?.excerpt ?? selected.crashAnalysis.summary,
      firstObservedAt: selected.crashAnalysis.analyzedAt, lastObservedAt: selected.crashAnalysis.analyzedAt,
      evidenceFingerprint: `${selected.crashAnalysis.reportId}:${selected.crashAnalysis.code}`,
      primaryAction: 'openConsole', dismissible: true
    }] : [],
    console: Array.from({ length: 240 }, (_, index) => ({
      sequence: index + 1,
      timestamp: new Date(Date.parse(now) - (239 - index) * 1_150).toISOString(),
      stream: index % 29 === 0 ? 'WARN' : 'INFO',
      text: index % 47 === 0
        ? 'WARNING: A restricted method in java.lang.System has been called by com.sun.jna.Native in an unnamed module (file:/C:/Users/fixture/ChunkPilot/Servers/Northern-Works/libraries/net/java/dev/jna/jna/5.17.0/jna-5.17.0.jar)'
        : index % 41 === 0
          ? 'WARNING: sun.misc.Unsafe::objectFieldOffset has been called by org.joml.MemUtil$MemUtilUnsafe and will be removed in a future release'
          : index % 29 === 0
            ? '[Server thread/WARN]: CopperValley moved too quickly! 8.21, 64.0, -142.8'
            : `[Server thread/INFO]: ${index % 9 === 0 ? 'Saved the game' : `Tick ${18_200 + index} completed`}`
    })),
    players: [
      { name: 'MapleRook', uuid: '069a79f4-44e9-4726-a5be-fca90e38aaf5', online: true, allowlisted: true, operator: true, banned: false },
      { name: 'CinderFox', uuid: null, online: true, allowlisted: true, operator: false, banned: false },
      { name: 'GlassBadger', uuid: null, online: true, allowlisted: false, operator: false, banned: false },
      { name: 'NorthSignal', uuid: null, online: true, allowlisted: true, operator: false, banned: false },
      { name: 'OldQuartz', uuid: null, online: false, allowlisted: true, operator: false, banned: false }
    ],
    files: [
      { name: 'world', relativePath: 'world', kind: 'folder', sizeBytes: null, modifiedAt: now },
      { name: 'logs', relativePath: 'logs', kind: 'folder', sizeBytes: null, modifiedAt: now },
      { name: 'backups', relativePath: 'backups', kind: 'folder', sizeBytes: null, modifiedAt: now },
      { name: 'server.properties', relativePath: 'server.properties', kind: 'editable', sizeBytes: 1380, modifiedAt: now },
      { name: 'whitelist.json', relativePath: 'whitelist.json', kind: 'editable', sizeBytes: 642, modifiedAt: now },
      { name: 'server.jar', relativePath: 'server.jar', kind: 'binary', sizeBytes: 53_420_918, modifiedAt: '2026-08-11T09:10:00-06:00' },
      ...Array.from({ length: 34 }, (_, index) => ({ name: `region-${index.toString().padStart(2, '0')}.mca`, relativePath: `world/region/r.${index}.0.mca`, kind: 'binary' as const, sizeBytes: 1_024_000 + index * 83_200, modifiedAt: now }))
    ],
    plugins: selected?.capabilities.content === 'plugins' ? [
      { name: 'LuckPerms', fileName: 'LuckPerms-Bukkit-5.4.153.jar', relativePath: 'plugins/LuckPerms-Bukkit-5.4.153.jar', version: '5.4.153', id: 'LuckPerms', loader: 'Bukkit', sizeBytes: 1_462_900, modifiedAt: now, enabled: true, duplicateId: false, dependencies: [], dependencyDetails: [], compatibility: 'LikelyCompatible' as const, compatibilityReason: 'Plugin metadata matches this Paper-compatible server. Exact Minecraft support is provider-declared.', loadState: 'Loaded' as const, loadEvidence: 'The current fixture log contains an explicit enable line.', installSource: 'Modrinth', provider: 'Modrinth' as const, providerProjectId: 'luckperms', providerVersionId: 'fixture-old', sha256: 'fixture-sha256' },
      { name: 'Vault', fileName: 'Vault.jar', relativePath: 'plugins/Vault.jar', version: '1.7.3', id: 'Vault', loader: 'Bukkit', sizeBytes: 276_840, modifiedAt: now, enabled: false, duplicateId: false, dependencies: [], dependencyDetails: [], compatibility: 'LikelyCompatible' as const, compatibilityReason: 'Plugin metadata matches this Paper-compatible server.', loadState: 'Disabled' as const, loadEvidence: 'The JAR is in disabled plugin storage.', installSource: 'Local file', sha256: 'fixture-sha256-2' }
    ] : selected?.capabilities.content === 'mods' ? [
      { name: 'Lithium', fileName: 'lithium-0.18.1.jar', relativePath: 'mods/lithium-0.18.1.jar', version: '0.18.1', id: 'lithium', loader: selected.ecosystem, sizeBytes: 880_000, modifiedAt: now, enabled: true, duplicateId: false, dependencies: [], dependencyDetails: [], compatibility: 'Compatible' as const, compatibilityReason: `Exact ${selected.ecosystem} and Minecraft ${selected.minecraftVersion} metadata match.`, clientRequirement: 'ClientOptional' as const, loadState: 'Loaded' as const, loadEvidence: 'The fixture contains an explicit loader discovery line.', installSource: 'Modrinth', provider: 'Modrinth' as const, providerProjectId: 'lithium', providerVersionId: 'lithium-exact', sha256: 'fixture-mod-sha256' },
      { name: 'Fixture Library', fileName: 'fixture-library.jar', relativePath: 'mods/fixture-library.jar', version: '2.0.0', id: 'fixture-library', loader: selected.ecosystem, sizeBytes: 240_000, modifiedAt: now, enabled: false, duplicateId: false, dependencies: [], dependencyDetails: [], compatibility: 'Compatible' as const, compatibilityReason: `Exact ${selected.ecosystem} metadata match.`, clientRequirement: 'ClientAndServer' as const, loadState: 'Disabled' as const, loadEvidence: 'The JAR is in disabled mod storage.', installSource: 'Local file', sha256: 'fixture-mod-sha256-2' }
    ] : [],
    currentFolder: '',
    schedules: selected ? [{ id: 'schedule-1', serverId: selected.id, name: 'Nightly verified backup', action: 'Backup', kind: 'Daily', intervalMinutes: 1440, at: '04:00', cron: '', command: '', enabled: true, nextRunAt: '2026-08-15T04:00:00-06:00', lastRunAt: '2026-08-14T04:00:00-06:00', backupBeforeRestart: false, restartCountdownSeconds: 60 }] : [],
    backups: [
      { id: 'b-1', createdAt: '2026-08-14T14:10:00-06:00', description: 'Before datapack change', sizeBytes: 1.84 * gib, verified: true, source: 'Manual' },
      { id: 'b-2', createdAt: '2026-08-13T04:00:00-06:00', description: 'Nightly backup', sizeBytes: 1.79 * gib, verified: true, source: 'Schedule' },
      { id: 'b-3', createdAt: '2026-08-12T04:00:00-06:00', description: 'Nightly backup', sizeBytes: 1.75 * gib, verified: true, source: 'Schedule' }
    ],
    versions: [
      { id: 'v-1', version: '1.21.8', platform: 'Vanilla', installedAt: '2026-08-11T09:10:00-06:00', active: true, verified: true, health: 'Healthy', snapshotSizeBytes: 0, includesWorldData: true, rollbackReady: false },
      { id: 'v-0', version: '1.21.7', platform: 'Vanilla', installedAt: '2026-07-18T18:42:00-06:00', active: false, verified: true, health: 'Healthy', snapshotSizeBytes: 1.72 * gib, includesWorldData: true, rollbackReady: true }
    ],
    update: { status: 'Up to date', detail: 'Minecraft 1.21.8 is the current linked Vanilla release.', sourceLinked: true, provider: 'DirectManifest', projectId: 'minecraft', projectName: 'Minecraft', installedVersionId: '1.21.8', installedVersionName: 'Minecraft 1.21.8', releaseChannel: 'Stable', minecraftVersion: '1.21.8', loader: 'Vanilla', loaderVersion: '', checkedAt: '2026-08-14T15:30:00-06:00', latestVersionName: 'Minecraft 1.21.8', compatibility: 'Compatible', canInstall: false, operationState: null, operationPercent: null, cancellable: false },
    activity: [
      { id: 1, timestamp: '2026-08-14T14:10:00-06:00', serverId: selected?.id ?? null, serverName: selected?.name ?? '', action: 'Backup', result: 'Completed', error: null, durationMs: 18_430 },
      { id: 2, timestamp: '2026-08-14T13:58:00-06:00', serverId: selected?.id ?? null, serverName: selected?.name ?? '', action: 'Start', result: 'Running', error: null, durationMs: 9_821 },
      { id: 3, timestamp: '2026-08-13T22:16:00-06:00', serverId: selected?.id ?? null, serverName: selected?.name ?? '', action: 'Safe stop', result: 'Stopped', error: null, durationMs: 4_204 }
    ],
    settings: { minimizeToTray: false, startMinimized: false, startWithWindows: false, reducedMotion: false },
    serverSettings: selected ? { serverId: selected.id, name: selected.name, motd: 'A quiet place to build.', port: selected.port, maximumPlayers: selected.playersMaximum ?? 20, difficulty: 'normal', gameMode: 'survival', pvp: true, allowlist: true, minimumRamMb: 1024, maximumRamMb: selected.maximumMemoryBytes / 1024 / 1024, runInBackground: true } : null
  };
}

const stopped = server({ id: '4ac49bc1-b30e-4caa-99ca-52968f40b214', name: 'Maple Ridge', state: 'Stopped', playersOnline: null, playersMaximum: null, uptimeSeconds: null, cpuPercent: null, memoryBytes: null, samples: [], publicReachability: 'unavailable' });
const attention = server({ id: '76267eec-dcab-42a5-b456-f6f268dd08f3', name: 'Redstone Lab', state: 'Crashed', ecosystem: 'Paper', minecraftVersion: '1.21.8', playersOnline: null, playersMaximum: null, uptimeSeconds: null, cpuPercent: null, memoryBytes: null, samples: [], lastBackupAt: null, lastError: 'Startup stopped because port 25565 is already in use.', crashAnalysis: {
  reportId: 'd2ddaf32-a467-4fd3-b118-6f1acde63af4', analyzedAt: now, exitCode: 1, code: 'port.conflict',
  title: 'Port 25565 is already in use', summary: 'Another process is already listening on the port this server needs.',
  confidence: 'HighlyLikely', reachedReadiness: false, serverIdentity: 'Paper 1.21.8', runtimeIdentity: 'java.exe (Managed)', activeOperation: 'Start',
  evidence: [
    { source: 'Console tail', excerpt: 'FAILED TO BIND TO PORT: Address already in use' },
    { source: 'Latest log', excerpt: 'Perhaps a server is already running on that port?' }
  ],
  recommendedSteps: ['Stop the other ChunkPilot server that uses this port, if one is running.', 'Choose an unused server port in Connectivity settings.', 'Retry the server start.'],
  safeActions: [
    { code: 'open-console', label: 'Open console', detail: 'Review the surrounding server output.' },
    { code: 'open-logs', label: 'Open logs', detail: 'Open the server log folder.' },
    { code: 'support-bundle', label: 'Create support bundle', detail: 'Create a redacted local diagnostic bundle.' },
    { code: 'retry-start', label: 'Retry start', detail: 'Retry through the authoritative lifecycle path.' }
  ]
}, capabilities: { console: true, players: true, files: true, content: 'plugins', versioning: 'paper', backups: true, versions: true } });
const paperPlugins = server({ id: '4a8841bf-b7fc-450a-a9aa-9d2137b75bdc', name: 'Paper Workshop', state: 'Stopped', ecosystem: 'Paper', minecraftVersion: '1.21.8', playersOnline: null, playersMaximum: 20, uptimeSeconds: null, cpuPercent: null, memoryBytes: null, samples: [], capabilities: { console: true, players: true, files: true, content: 'plugins', versioning: 'paper', backups: true, versions: true } });
const fabric = server({ id: '246848c2-e51f-4815-9527-d2966ab4acf4', name: 'Northern Works', ecosystem: 'Fabric', loaderVersion: '0.19.3', minecraftVersion: '26.2', port: 25567, playersOnline: 1, playersMaximum: 8, capabilities: { console: true, players: true, files: true, content: 'mods', versioning: 'fabric', backups: true, versions: true } });
const neoForge = server({ id: 'c65f04c4-bfb7-41cf-90f5-347a60e65f5c', name: 'Foundry Reach', ecosystem: 'NeoForge', loaderVersion: '26.2.0.61', minecraftVersion: '26.2', port: 25568, playersOnline: null, playersMaximum: 8, capabilities: { console: true, players: true, files: true, content: 'mods', versioning: 'neoforge', backups: true, versions: true } });
const modpack = server({ id: 'd8a2328a-524d-4b93-a329-10d9bed223d2', name: 'Adventure Ridge', ecosystem: 'Fabric', loaderVersion: '0.19.3', minecraftVersion: '1.21.8', port: 25569, playersOnline: null, playersMaximum: 8, modpack: { provider: 'Modrinth', projectId: 'adventure-ridge', projectName: 'Adventure Ridge Pack', versionId: 'adventure-ridge-2.4.1', versionName: '2.4.1' }, capabilities: { console: true, players: true, files: true, content: 'modpack', versioning: 'fabric', backups: true, versions: true } });
const importedMinecraft = server({ id: 'f0f9b0b5-94fd-4a0d-a612-562101170b0f', name: 'Imported Mystery Server', ecosystem: 'Custom', minecraftVersion: 'Unknown', managed: false, playersOnline: null, playersMaximum: null, playerStatus: { online: null, maximum: null, source: 'Unsupported', exact: false, checkedAt: now, detail: 'ChunkPilot has not identified a compatible live-status strategy yet. Saved access records remain authoritative when present.' }, capabilities: { console: true, players: true, files: true, content: 'unsupported', versioning: 'unsupported', backups: true, versions: true } });
const terraria = server({ id: 'e8cb796d-66fd-4b54-a40d-cd5266926e4a', name: 'Terraria Preview', gameKind: 'Terraria', ecosystem: 'Custom', minecraftVersion: 'Not applicable', playersOnline: null, playersMaximum: null, playerStatus: null, capabilities: { console: true, players: false, files: true, content: 'unsupported', versioning: 'unsupported', backups: true, versions: true } });
const modpackSnapshot = snapshot([modpack]);
modpackSnapshot.update = { status: 'Up to date', detail: 'Adventure Ridge Pack 2.4.1 is the current exact Modrinth release.', sourceLinked: true, provider: 'Modrinth', projectId: 'adventure-ridge', projectName: 'Adventure Ridge Pack', installedVersionId: 'adventure-ridge-2.4.1', installedVersionName: '2.4.1', releaseChannel: 'Stable', minecraftVersion: '1.21.8', loader: 'Fabric', loaderVersion: '0.19.3', checkedAt: '2026-08-14T15:30:00-06:00', latestVersionName: '2.4.1', compatibility: 'Compatible', canInstall: false, operationState: null, operationPercent: null, cancellable: false };

export const fixtures: Record<string, WebUiSnapshot> = {
  zero: snapshot([]),
  stopped: snapshot([stopped]),
  running: snapshot([server()]),
  plugins: snapshot([paperPlugins]),
  several: snapshot([server(), stopped, attention, fabric, neoForge]),
  fabric: snapshot([fabric]),
  neoforge: snapshot([neoForge]),
  modpack: modpackSnapshot,
  imported: snapshot([importedMinecraft]),
  terraria: snapshot([terraria]),
  attention: snapshot([attention]),
  starting: snapshot([server({ state: 'Starting', playersOnline: null, playersMaximum: null, uptimeSeconds: null, cpuPercent: null, memoryBytes: null, samples: [] })]),
  unknown: snapshot([server({ playersOnline: null, playersMaximum: null, cpuPercent: null, memoryBytes: null, samples: [], publicReachability: 'unavailable', lastBackupAt: null })])
};

export class FixtureBridge implements BridgeAdapter {
  private listeners = new Set<(event: BridgeEvent) => void>();
  private current: WebUiSnapshot;
  private curseForgeConfigured = false;
  constructor(name = 'several') {
    this.current = structuredClone(fixtures[name] ?? fixtures.several);
    const mode = new URLSearchParams(window.location.search).get('mode');
    if (mode === 'library-public' && this.current.servers.length > 0) {
      const target = this.current.servers[0];
      this.current.servers[0] = {
        ...target,
        connectionMode: 'PortForwarding',
        publicAddress: '203.0.113.24:25565',
        publicAddressKind: 'router',
        publicAddressObservedAt: now,
        publicReachability: 'not-confirmed'
      };
    }
    if (this.current.serverSettings && mode === 'motd-rich') this.current.serverSettings.motd = '§b§lCopper Valley §r§7— §aOnline\n§eBuild together §6★ §f世界';
    if (this.current.serverSettings && mode === 'motd-raw') this.current.serverSettings.motd = '§x§1§2§3§4§5§6Unknown gradient\n§fPreserved in raw mode';
    if (this.current.connectivity && mode === 'connectivity-local') {
      this.current.connectivity.mode = 'ThisComputerOnly'; this.current.connectivity.modeTitle = 'Local only';
      this.current.connectivity.status = { title: 'Local only', detail: 'Only this PC can join. Nothing is exposed to the network.', tone: 'neutral' };
    }
    if (this.current.connectivity && mode === 'connectivity-pending') {
      this.current.connectivity.mode = 'PortForwarding'; this.current.connectivity.modeTitle = 'Internet hosting';
      this.current.connectivity.status = { title: 'Setting up Internet access', detail: 'ChunkPilot is waiting for the router operation to finish.', tone: 'info' };
      this.current.connectivity.router = { ...this.current.connectivity.router, phase: 'Creating', title: 'Setting up Internet access', summary: 'The Agent is asking the router to map this server port.', badge: 'Working', tone: 'info', busy: true, canCheck: false, canCancel: true };
    }
    if (this.current.connectivity && (mode === 'connectivity-unverified' || mode === 'share-unverified')) {
      this.current.connectivity.mode = 'PortForwarding'; this.current.connectivity.modeTitle = 'Internet hosting';
      this.current.connectivity.status = { title: 'Internet sharing configured', detail: 'ChunkPilot owns the Windows Firewall rule and router mapping for this server. This setup state is not proof that a friend can connect.', tone: 'success' };
      this.current.connectivity.addresses.routerReported = '203.0.113.24:25565';
      this.current.connectivity.router = { ...this.current.connectivity.router, phase: 'Active', title: 'Router ready', summary: 'The router reports an active mapping for this server.', badge: 'Ready', tone: 'success', enabled: true, canCheck: false, canStop: true, externalPort: '25565' };
      this.current.connectivity.firewall = { ...this.current.connectivity.firewall, phase: 'Configured', title: 'Windows Firewall ready', summary: 'ChunkPilot owns one exact rule for this server.', badge: 'Ready', tone: 'success', configured: true, primaryAction: null };
      this.current.connectivity.external = { ...this.current.connectivity.external, phase: 'Eligible', blocker: 'None', title: 'Optional diagnostic not run', summary: 'Owned setup is ready. An outside-in check is available only from Advanced diagnostics.', badge: 'Optional', tone: 'neutral', busy: false, canCheck: true, canCancel: false, routerAddress: '203.0.113.24', port: 'TCP 25565' };
    }
    if (this.current.connectivity && (mode === 'connectivity-public' || mode === 'share-public')) {
      this.current.connectivity.mode = 'PortForwarding'; this.current.connectivity.modeTitle = 'Internet hosting';
      this.current.connectivity.status = { title: 'Friends can join', detail: 'An outside-in check reached 203.0.113.24:25565.', tone: 'success' };
      this.current.connectivity.addresses.publicVerified = '203.0.113.24:25565'; this.current.connectivity.addresses.routerReported = '203.0.113.24:25565'; this.current.connectivity.addresses.publicVerifiedAt = now;
      this.current.connectivity.router = { ...this.current.connectivity.router, phase: 'Active', title: 'Internet hosting is active', summary: 'The router reports an active mapping for this server.', badge: 'Active', tone: 'success', enabled: true, canCheck: false, canStop: true, externalPort: '25565' };
      this.current.connectivity.external = { ...this.current.connectivity.external, phase: 'Reachable', blocker: 'None', title: 'Friends can join', summary: 'An outside-in service connected to this exact endpoint.', badge: 'Verified', tone: 'success', canCheck: true, verifiedEndpoint: '203.0.113.24:25565', verifiedAt: now, checkedAt: 'Today at 4:42 PM', observedAddress: '203.0.113.24', routerAddress: '203.0.113.24', port: 'TCP 25565', connectTime: '42 ms' };
      this.current.servers = this.current.servers.map(item => item.id === this.current.selectedServerId ? { ...item, connectionMode: 'PortForwarding', publicAddress: '203.0.113.24:25565', publicAddressKind: 'verified', publicAddressObservedAt: now, publicReachability: 'confirmed' } : item);
    }
    if (this.current.connectivity && mode === 'share-last') {
      this.current.connectivity.mode = 'PortForwarding'; this.current.connectivity.modeTitle = 'Internet hosting';
      this.current.connectivity.status = { title: 'Connection needs a new check', detail: 'The last checked address may have changed.', tone: 'warning' };
      this.current.connectivity.addresses.publicVerified = null; this.current.connectivity.addresses.routerReported = null;
      this.current.connectivity.addresses.lastKnownPublic = '198.51.100.14:25565'; this.current.connectivity.addresses.lastKnownPublicAt = '2026-08-13T16:42:00-06:00';
      this.current.connectivity.external = { ...this.current.connectivity.external, phase: 'Stale', title: 'Connection needs a new check', summary: 'The last checked Internet address is now stale.', badge: 'Stale', tone: 'warning', busy: false, canCheck: true, verifiedEndpoint: '', verifiedAt: null };
    }
    if (this.current.connectivity && mode === 'connectivity-failure') {
      this.current.connectivity.mode = 'PortForwarding'; this.current.connectivity.modeTitle = 'Internet hosting';
      this.current.connectivity.status = { title: 'Needs attention', detail: 'The outside-in check could not reach this server.', tone: 'warning' };
      this.current.connectivity.external = { ...this.current.connectivity.external, phase: 'Unreachable', blocker: 'None', title: 'Could not reach this server', summary: 'The outside-in check could not establish a TCP connection.', badge: 'Needs attention', tone: 'warning', canCheck: true, checkedAt: 'Today at 4:42 PM' };
    }
  }

  async request<T>(method: BridgeMethod, params: Record<string, unknown> = {}): Promise<T> {
    if (method === 'snapshot.get' || method === 'renderer.ready') return (method === 'snapshot.get' ? this.current : { ready: true }) as T;
    if (method === 'players.head') return {
      serverId: params.serverId,
      uuid: params.uuid,
      imageUrl: params.uuid === '069a79f4-44e9-4726-a5be-fca90e38aaf5'
        ? 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAYAAADED76LAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAAwSURBVChTY3DVVv6PDzPAGFtbklEwXAG6BDqGK7BNrIMLIrOJNwEXRlGAzcEETQAAaGmXQ0M6gQQAAAAASUVORK5CYII='
        : null
    } as T;
    if (method === 'creation.catalog' && params.platform === 'Paper') return { platform: 'Paper', available: true, message: 'Deterministic PaperMC fixture inventory.', retrievedAt: now, fromCache: true, stale: false, manifestLatestReleaseId: '', manifestLatestSnapshotId: '', latestVerifiedReleaseId: '', versions: fixtureCatalogVersions.filter(version => version.releaseKind === 'Release').slice(0, 18).map(version => ({ ...version, support: 'Experimental', supportReason: 'Exact build metadata is available, but this Paper version is not runtime-certified.', selectable: true, hasServerArtifact: false, hasIntegrityMetadata: false, launchProfile: { ...version.launchProfile, kind: 'PaperNogui', arguments: '--nogui' }, certification: { level: 'MetadataValidated', runtimeLaunched: false, readinessConfirmed: false, cleanShutdownConfirmed: false, runtimeValidatedAt: null, limitations: ['This exact Paper version has not been isolated-runtime certified by this ChunkPilot build.'] }, provenance: 'Official PaperMC Fill v3 fixture' })) } as T;
    if (method === 'creation.catalog') return { available: true, message: 'Deterministic 906-entry fixture inventory for local performance and visual review.', retrievedAt: now, fromCache: true, stale: false, manifestLatestReleaseId: '1.21.8', manifestLatestSnapshotId: '26w33a', latestVerifiedReleaseId: '1.21.8', versions: fixtureCatalogVersions } as T;
    if (method === 'creation.paperBuilds') return { available: true, message: '', retrievedAt: now, fromCache: true, stale: false, minecraftVersion: String(params.versionId), builds: [132, 131, 130, 129].map(id => ({ id, label: `Build ${id}`, channel: 'Stable', publishedAt: now, sizeBytes: 54846016, selectable: true, supportReason: 'Exact stable PaperMC build.', provenance: 'Official PaperMC Fill v3 fixture' })) } as T;
    if (method === 'creation.loaderBuilds') {
      const platform = String(params.platform);
      const build = platform === 'Fabric' ? ['0.19.3', '1.1.2', 'Loader 0.19.3 · installer 1.1.2']
        : platform === 'Quilt' ? ['0.30.0', '0.15.1', 'Quilt Loader 0.30.0']
          : platform === 'Forge' ? ['65.1.0', '65.1.0', 'Forge 65.1.0']
            : platform === 'NeoForge' ? ['26.2.0.61', '26.2.0.61', 'NeoForge 26.2.0.61']
              : ['catalog-only', '', `${platform} catalog-only`];
      const selectable = platform !== 'LegacyFabric' && platform !== 'Ornithe';
      const certified = ['Fabric', 'NeoForge', 'Forge', 'Quilt'].includes(platform);
      return { platform, available: true, message: '', fromCache: true, stale: false, retrievedAt: now, minecraftVersion: String(params.versionId), builds: [{ id: build[0], label: build[2], loaderVersion: build[0], installerVersion: build[1], channel: 'Stable', sizeBytes: null, hasIntegrityMetadata: true, selectable, support: certified ? 'Recommended' : selectable ? 'Experimental' : 'Unavailable', supportReason: certified ? 'Exact recommended combination passed runtime certification.' : selectable ? 'Exact official combination is available for isolated review.' : 'Typed historical installation is not enabled.', provenance: 'Official loader fixture', certification: { level: certified ? 'RuntimeCertified' : selectable ? 'MetadataValidated' : 'Inventoried', evidence: ['Exact provider fixture'], limitations: certified ? [] : selectable ? ['Runtime certification is pending.'] : ['Creation unavailable.'] } }] } as T;
    }
    if (method === 'creation.previewDestination') return { available: true, path: `C:\\ChunkPilot Servers\\${String(params.name ?? 'New Server').replaceAll(' ', '-')}`, message: 'This destination is available.' } as T;
    if (method === 'creation.chooseFolder') return { path: 'D:\\Minecraft Servers' } as T;
    if (method === 'creation.begin') return { operationId: params.operationId, accepted: true } as T;
    if (method === 'creation.operations') return [] as T;
    if (method === 'creation.progress') return { stage: 'Preparing', percent: 34, message: 'Preparing the managed server safely.', isTerminal: false } as T;
    if (method === 'creation.cancel') return { accepted: true, operationId: params.operationId } as T;
    if (method === 'content.operations') return [] as T;
    if (method === 'content.cancel') return { success: true, message: 'Cancellation was requested.' } as T;
    if (method === 'servers.deletePreflight') return {
      token: 'fixture', serverId: String(params.serverId),
      serverName: this.current.servers.find(item => item.id === params.serverId)?.name ?? 'Copper Valley',
      platform: this.current.servers.find(item => item.id === params.serverId)?.ecosystem ?? 'Vanilla',
      version: this.current.servers.find(item => item.id === params.serverId)?.minecraftVersion ?? '1.21.8',
      state: this.current.servers.find(item => item.id === params.serverId)?.state ?? 'Stopped',
      isManaged: true, ownershipProven: true, ownershipStatus: 'ProvenMarker',
      ownershipDetail: 'Persistent marker proven (CreationTransaction).',
      ownershipEvidence: [{ code: 'persistent-marker', satisfied: true, detail: 'Persistent marker proven.' }],
      canCreateManagedCopy: false, reviewFingerprint: 'fixture-fingerprint',
      managedRoot: 'C:\\Users\\Example\\ChunkPilot\\Servers\\Copper-Valley',
      worldLocation: 'C:\\Users\\Example\\ChunkPilot\\Servers\\Copper-Valley\\world',
      backupCount: 3,
      managedBackupPaths: ['C:\\Users\\Example\\AppData\\Local\\ChunkPilot\\Backups\\copper-1.cpb'],
      protectedExternalPaths: [], activeScheduleCount: 1,
      internetSharingConfigured: false, firewallRemovalRequired: false, blockers: [],
      expiresAt: new Date(Date.now() + 300_000).toISOString()
    } as T;
    if (method === 'servers.delete') return { accepted: true, operationId: 'fixture-delete-operation' } as T;
    if (method === 'servers.createManagedCopy') return { accepted: true, operationId: 'fixture-copy-operation' } as T;
    if (method === 'appearance.chooseIcon') return { cancelled: false, sourceUrl: './fixtures/icon-source.png', width: 256, height: 256, fileName: 'ChunkPilot-256.png' } as T;
    if (method === 'plugins.providers') return [
      { provider: 'Modrinth', available: true, detail: 'Official API available.' },
      { provider: 'Hangar', available: false, detail: 'Unavailable; ChunkPilot does not scrape.' }
    ] as T;
    if (method === 'plugins.search') return [{ provider: 'Modrinth', projectId: 'fixture-tools', slug: 'fixture-tools', name: 'Fixture Tools', author: 'ChunkPilot fixture', summary: 'Deterministic Paper utilities for visual review.', downloads: 42_100, updatedAt: now, serverSide: 'required' }] as T;
    if (method === 'plugins.release') return { provider: 'Modrinth', projectId: String(params.projectId), versionId: 'fixture-current', versionName: '5.5.0', minecraftVersion: '1.21.8', loader: 'paper', releaseChannel: 'release', publishedAt: now, fileName: 'LuckPerms-Bukkit-5.5.0.jar', sizeBytes: 1_500_000, integrity: 'sha512', dependencies: [] } as T;
    if (method === 'plugins.plan') return { canInstall: true, problems: [], releases: [] } as T;
    if (method === 'mods.providers') return [{ provider: 'Modrinth', available: true, detail: 'Official API available.' }] as T;
    if (method === 'mods.search') return [{ provider: 'Modrinth', kind: 'Mod', projectId: 'lithium', slug: 'lithium', name: 'Lithium', author: 'CaffeineMC', summary: 'Server performance improvements with exact loader filtering.', downloads: 22_400_000, updatedAt: now, serverSide: 'required', clientSide: 'optional', clientRequirement: 'ClientOptional' }] as T;
    if (method === 'modpacks.providers') return [{ provider: 'Modrinth', available: true, detail: 'Official Modrinth API is available.' }, { provider: 'CurseForge', available: this.curseForgeConfigured, detail: this.curseForgeConfigured ? 'Approved fixture access is active.' : 'CurseForge integration is being activated for ChunkPilot.' }] as T;
    if (method === 'modpacks.versions') {
      const provider = params.provider === 'CurseForge' ? 'CurseForge' : 'Modrinth';
      if (provider === 'CurseForge' && !this.curseForgeConfigured) return { provider, state: 'AuthenticationRequired', versions: [], detail: 'CurseForge integration is being activated for ChunkPilot.', failedStage: 'activation', retrievedAt: null, fromCache: false, stale: false } as T;
      return { provider, state: 'Ready', versions: [
        { versionId: '1.21.8', kind: 'Release', publishedAt: now, isMajor: true },
        { versionId: '1.20.1', kind: 'Release', publishedAt: '2023-06-12T00:00:00Z', isMajor: false },
        { versionId: '26w33a', kind: 'Snapshot', publishedAt: '2026-08-13T12:00:00Z', isMajor: false },
        { versionId: 'b1.8.1', kind: 'Beta', publishedAt: '2011-09-19T00:00:00Z', isMajor: false },
        { versionId: 'a1.2.6', kind: 'Alpha', publishedAt: '2010-12-03T00:00:00Z', isMajor: false }
      ], detail: 'Fixture official provider inventory.', failedStage: '', retrievedAt: now, fromCache: params.cacheOnly === true, stale: false } as T;
    }
    if (method === 'modpacks.cache' || method === 'modpacks.search') {
      const provider = params.provider === 'CurseForge' ? 'CurseForge' : 'Modrinth';
      const item = { provider, projectId: 'fixture-pack', slug: 'fixture-pack', name: 'Copper Trails', author: 'ChunkPilot fixture', summary: 'A deterministic server-capable fixture pack for visual review.', downloadCount: 1_240_000, updatedAt: now, categories: ['fabric', 'adventure'], hasImage: false, serverSupport: 'AutomatedWithReview', clientRequirement: 'MatchingPackRequired', trend: { available: false, detail: 'No local period snapshot history exists yet.' }, versions: [{ versionId: 'fixture-pack-4', versionName: '4.2.0', minecraftVersion: '1.21.8', loader: 'fabric', releaseChannel: 'Stable', publishedAt: now, sizeBytes: 1_240_000, changelog: 'Fixture release.', requiredJavaMajor: 21, hasIntegrity: true, canCreate: true }] };
      if (provider === 'CurseForge' && !this.curseForgeConfigured) return { provider, state: 'AuthenticationRequired', items: [], detail: 'CurseForge integration is being activated for ChunkPilot.', failedStage: 'activation', retrievedAt: null, fromCache: false, stale: false } as T;
      return { provider, state: 'Ready', items: [item], detail: 'Fixture catalog ready.', failedStage: '', retrievedAt: now, fromCache: method === 'modpacks.cache', stale: false } as T;
    }
    if (method === 'modpacks.resolveLink') {
      const url = String(params.url ?? '');
      if (url.includes('curseforge.com')) throw new Error('CurseForge integration is being activated for ChunkPilot. Modrinth links and local pack imports are available now.');
      const release = { versionId: 'fixture-pack-4', versionName: '4.2.0', minecraftVersion: '1.21.8', loader: 'fabric', releaseChannel: 'Stable', publishedAt: now, sizeBytes: 1_240_000, changelog: 'Fixture release.', requiredJavaMajor: 21, hasIntegrity: true, canCreate: true };
      const project = { provider: 'Modrinth', projectId: 'fixture-pack', slug: 'fixture-pack', name: 'Copper Trails', author: 'ChunkPilot fixture', summary: 'A deterministic server-capable fixture pack for visual review.', downloadCount: 1_240_000, updatedAt: now, categories: ['fabric', 'adventure'], hasImage: false, serverSupport: 'AutomatedWithReview', clientRequirement: 'MatchingPackRequired', trend: { available: false, detail: 'No local period snapshot history exists yet.' }, versions: [release] };
      return { canonicalUrl: `${'https:'}//modrinth.com/modpack/fixture-pack`, exactRelease: url.includes('/version/'), project, release, detail: url.includes('/version/') ? 'Resolved the exact release from the provider link.' : 'Selected the newest stable server-capable release.' } as T;
    }
    if (method === 'modpacks.image') return { dataUrl: null } as T;
    if (method === 'modpacks.chooseLocal') return { cancelled: true } as T;
    if (method === 'creation.chooseLegacyArtifact') return {
      cancelled: false,
      token: 'fixture-legacy-artifact-token',
      fileName: 'minecraft_server.b1.8.1.jar',
      minecraftVersion: 'b1.8.1',
      sizeBytes: 1_465_312,
      sha256: 'f'.repeat(64),
      matchesOfficialHash: false,
      identityEvidence: 'Mojang publishes no official server hash for this target. The file remains user-supplied and must pass isolated runtime validation.',
      expiresAt: new Date(Date.now() + 300_000).toISOString()
    } as T;
    if (method === 'mods.release') return { provider: 'Modrinth', kind: 'Mod', projectId: String(params.projectId), versionId: 'lithium-exact', versionName: '0.18.1', minecraftVersion: '26.2', loader: this.current.servers[0]?.ecosystem === 'NeoForge' ? 'neoforge' : 'fabric', releaseChannel: 'release', publishedAt: now, fileName: 'lithium-0.18.1.jar', sizeBytes: 880_000, integrity: 'sha512', serverSide: 'required', clientSide: 'optional', clientRequirement: 'ClientOptional', dependencies: [] } as T;
    if (method === 'mods.plan') return { canInstall: true, problems: [], releases: [] } as T;
    if (method === 'plugins.configFiles') return [{ relativePath: 'plugins/LuckPerms/config.yml', name: 'config.yml', format: 'yaml', sizeBytes: 2840, modifiedAt: now }] as T;
    if (method === 'mods.configFiles') return [{ relativePath: 'config/lithium.properties', name: 'lithium.properties', format: 'properties', sizeBytes: 940, modifiedAt: now }] as T;
    if (method === 'plugins.chooseLocal') return { cancelled: false, token: 'fixture-local-token', fileName: 'FixtureLocal.jar', expiresAt: now, plugin: { name: 'Fixture Local', version: '1.0.0', id: 'FixtureLocal', loader: 'Bukkit', sizeBytes: 48_200, dependencies: ['Vault'], compatibility: 'LikelyCompatible', compatibilityReason: 'Plugin metadata matches this Paper-compatible server.' } } as T;
    if (method === 'files.read') return { relativePath: String(params.relativePath), content: String(params.relativePath).includes('lithium') ? '# Lithium server settings\nmixin.world=false\nchunk.update=true\n' : 'server: Copper Valley\nverbose: false\nlog-notify: true\n', encodingName: 'utf-8', hasBom: false, lineEnding: '\r\n', loadedSha256: 'fixture-sha256', loadedLastWriteAt: now } as T;
    if (method === 'snapshot.selectServer') this.current = { ...this.current, revision: this.current.revision + 1, selectedServerId: typeof params.serverId === 'string' ? params.serverId : null };
    if (method === 'settings.saveServer') {
      const selectedId = this.current.selectedServerId;
      this.current = {
        ...this.current,
        revision: this.current.revision + 1,
        serverSettings: this.current.serverSettings ? {
          ...this.current.serverSettings,
          motd: String(params.motd ?? this.current.serverSettings.motd),
          port: Number(params.port ?? this.current.serverSettings.port),
          maximumPlayers: Number(params.maximumPlayers ?? this.current.serverSettings.maximumPlayers),
          difficulty: String(params.difficulty ?? this.current.serverSettings.difficulty),
          gameMode: String(params.gameMode ?? this.current.serverSettings.gameMode),
          pvp: Boolean(params.pvp), allowlist: Boolean(params.allowlist),
          minimumRamMb: Number(params.minimumRamMb ?? this.current.serverSettings.minimumRamMb),
          maximumRamMb: Number(params.maximumRamMb ?? this.current.serverSettings.maximumRamMb)
        } : null,
        servers: this.current.servers.map(item => item.id === selectedId ? {
          ...item,
          iconUrl: typeof params.iconPngBase64 === 'string' && params.iconPngBase64.length > 0
            ? `data:image/png;base64,${params.iconPngBase64}` : item.iconUrl
        } : item)
      };
    }
    if (method === 'connectivity.setMode' && this.current.connectivity) {
      const modeValue = String(params.mode) as ConnectivitySnapshot['mode'];
      const title = modeValue === 'ThisComputerOnly' ? 'Local only' : modeValue === 'HomeNetwork' ? 'Home network' : modeValue === 'PortForwarding' ? 'Internet hosting' : 'Configure later';
      this.current = { ...this.current, revision: this.current.revision + 1, connectivity: { ...this.current.connectivity, mode: modeValue, modeTitle: title } };
    }
    if (method === 'servers.start' || method === 'servers.stop' || method === 'servers.restart') {
      const state = method === 'servers.stop' ? 'Stopping' : method === 'servers.restart' ? 'Restarting' : 'Starting';
      this.current = { ...this.current, revision: this.current.revision + 1, servers: this.current.servers.map(item => item.id === params.serverId ? { ...item, state } : item) };
    }
    const event: BridgeEvent = { protocolVersion: 1, event: 'snapshot.changed', revision: this.current.revision, payload: this.current };
    this.listeners.forEach(listener => listener(event));
    return { accepted: true, operationId: `fixture-${Date.now()}` } as T;
  }
  subscribe(listener: (event: BridgeEvent) => void): () => void { this.listeners.add(listener); return () => this.listeners.delete(listener); }
  dispose(): void { this.listeners.clear(); }
}
