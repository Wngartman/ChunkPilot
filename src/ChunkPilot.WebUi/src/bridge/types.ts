export const protocolVersion = 1 as const;

export type ServerState = 'Stopped' | 'Starting' | 'Running' | 'Saving' | 'Stopping' | 'Restarting' | 'BackingUp' | 'Restoring' | 'Crashed' | 'Unresponsive' | 'Unknown';

export interface MetricSample {
  at: string;
  cpuPercent: number;
  memoryBytes: number;
}

export type CrashConfidence = 'Unknown' | 'Possible' | 'HighlyLikely' | 'Confirmed';
export interface CrashAnalysis {
  reportId: string;
  analyzedAt: string;
  exitCode: number | null;
  code: string;
  title: string;
  summary: string;
  confidence: CrashConfidence;
  reachedReadiness: boolean;
  serverIdentity: string;
  runtimeIdentity: string;
  activeOperation: string | null;
  evidence: { source: string; excerpt: string }[];
  recommendedSteps: string[];
  safeActions: { code: string; label: string; detail: string }[];
}

export interface ServerSummary {
  id: string;
  name: string;
  state: ServerState;
  gameKind: 'Minecraft' | 'Terraria';
  ecosystem: string;
  minecraftVersion: string;
  loaderVersion?: string;
  port: number;
  managed: boolean;
  playersOnline: number | null;
  playersMaximum: number | null;
  playerStatus: {
    online: number | null;
    maximum: number | null;
    source: 'ModernStatus' | 'LegacyExtendedStatus' | 'LegacySimpleStatus' | 'Query' | 'ConsoleList' | 'ConsoleRoster' | 'LastExactStatus' | 'Waiting' | 'StatusCheckFailed' | 'Unsupported';
    exact: boolean;
    checkedAt: string;
    detail: string;
  } | null;
  uptimeSeconds: number | null;
  cpuPercent: number | null;
  memoryBytes: number | null;
  maximumMemoryBytes: number;
  localAddress: string;
  lanAddress: string | null;
  connectionMode: 'HomeNetwork' | 'PortForwarding';
  publicAddress: string | null;
  publicAddressKind: 'verified' | 'router' | 'last' | null;
  publicAddressObservedAt: string | null;
  publicReachability: 'confirmed' | 'not-confirmed' | 'unavailable';
  lastBackupAt: string | null;
  lastError: string | null;
  crashAnalysis: CrashAnalysis | null;
  iconUrl: string | null;
  modpack: { provider: string; projectId: string; projectName: string; versionId: string; versionName: string } | null;
  samples: MetricSample[];
  capabilities: {
    console: boolean;
    players: boolean;
    files: boolean;
    content: 'datapacks' | 'plugins' | 'mods' | 'modpack' | 'unsupported';
    versioning: 'vanilla' | 'paper' | 'fabric' | 'quilt' | 'forge' | 'neoforge' | 'unsupported';
    backups: boolean;
    versions: boolean;
  };
}

export type ServerDeletionMode = 'RemoveFromChunkPilot' | 'MoveToRecovery' | 'Permanent';
export interface ServerDeletionPreflight {
  token: string;
  serverId: string;
  serverName: string;
  platform: string;
  version: string;
  state: ServerState;
  isManaged: boolean;
  ownershipProven: boolean;
  ownershipStatus: 'External' | 'ProvenMarker' | 'ReconciledCreationEvidence' | 'Ambiguous';
  ownershipDetail: string;
  ownershipEvidence: { code: string; satisfied: boolean; detail: string }[];
  canCreateManagedCopy: boolean;
  reviewFingerprint: string;
  managedRoot: string;
  worldLocation: string;
  backupCount: number;
  managedBackupPaths: string[];
  protectedExternalPaths: string[];
  activeScheduleCount: number;
  internetSharingConfigured: boolean;
  firewallRemovalRequired: boolean;
  blockers: string[];
  expiresAt: string;
}

export interface ConsoleEntry { sequence: number; timestamp: string; stream: string; text: string; }
export interface PlayerEntry { name: string; uuid: string | null; online: boolean; allowlisted: boolean; operator: boolean; banned: boolean; }
export interface ServerHealthIssue {
  issueId: string;
  serverId: string;
  articleId: string;
  category: 'startup' | 'runtime' | 'networking' | 'performance' | 'content';
  severity: 'warning' | 'error';
  title: string;
  summary: string;
  evidenceSummary: string;
  firstObservedAt: string;
  lastObservedAt: string;
  evidenceFingerprint: string;
  primaryAction: 'openConsole' | 'openConnectivity' | 'openHelp';
  dismissible: boolean;
}
export interface FileEntry { name: string; relativePath: string; kind: 'folder' | 'editable' | 'binary' | 'too-large'; sizeBytes: number | null; modifiedAt: string | null; }
export type ContentDependencyKind = 'Required' | 'Optional' | 'LoadBefore' | 'Incompatible' | 'Embedded' | 'Unknown';
export interface ContentDependency { id: string; kind: ContentDependencyKind; }
export interface PluginInventoryEntry { name: string; fileName: string; relativePath: string; version: string; id: string; loader: string; sizeBytes: number; modifiedAt: string; enabled: boolean; duplicateId: boolean; dependencies: string[]; dependencyDetails: ContentDependency[]; compatibility: 'Compatible' | 'LikelyCompatible' | 'Incompatible' | 'Unknown'; compatibilityReason: string; loadState: 'Loaded' | 'Failed' | 'Pending' | 'Disabled' | 'Not running' | 'Unknown'; loadEvidence: string; installSource: string; provider?: 'Modrinth' | 'Hangar' | null; providerProjectId?: string; providerVersionId?: string; sha256: string; clientRequirement?: 'ServerOnly' | 'ClientOptional' | 'ClientAndServer' | 'ClientOnly' | 'Unknown'; }
export interface PluginProviderStatus { provider: 'Modrinth' | 'Hangar'; available: boolean; detail: string; }
export interface PluginProject { provider: 'Modrinth'; kind: 'Plugin' | 'Mod'; projectId: string; slug: string; name: string; author: string; summary: string; downloads: number | null; updatedAt: string | null; serverSide: string; clientSide: string; clientRequirement: 'ServerOnly' | 'ClientOptional' | 'ClientAndServer' | 'ClientOnly' | 'Unknown'; }
export interface PluginDependency { projectId: string; versionId: string; fileName: string; type: 'required' | 'optional' | 'incompatible' | 'embedded'; }
export interface PluginRelease { provider: 'Modrinth'; kind: 'Plugin' | 'Mod'; projectId: string; versionId: string; versionName: string; minecraftVersion: string; loader: string; releaseChannel: string; publishedAt: string; fileName: string; sizeBytes: number; integrity: 'sha512' | 'unavailable'; serverSide: string; clientSide: string; clientRequirement: 'ServerOnly' | 'ClientOptional' | 'ClientAndServer' | 'ClientOnly' | 'Unknown'; dependencies: PluginDependency[]; }
export interface PluginInstallPlan { releases: PluginRelease[]; problems: string[]; canInstall: boolean; }
export type ManagedContentOperationStage = 'Queued' | 'ResolvingDependencies' | 'Downloading' | 'Verifying' | 'InspectingMetadata' | 'Staging' | 'Installing' | 'PendingRestart' | 'Installed' | 'Loaded' | 'Failed' | 'Cancelled';
export interface ManagedContentOperation {
  operationId: string;
  serverId: string;
  kind: 'InstallAddon' | 'InstallAddonPlan' | 'UpdateAddon' | 'RemoveAddon' | 'InstallPack' | 'UpdatePack';
  provider: string;
  projectId: string;
  versionId: string;
  displayName: string;
  progress: { stage: ManagedContentOperationStage; message: string; percent: number | null; bytesTransferred: number | null; totalBytes: number | null };
  isTerminal: boolean;
  success: boolean | null;
  isCancellable: boolean;
  error: string | null;
  startedAtUtc: string;
  updatedAtUtc: string;
}
export interface PluginConfigFile { relativePath: string; name: string; sizeBytes: number; modifiedAt: string; format: 'yml' | 'yaml' | 'json' | 'jsonc' | 'toml' | 'properties' | 'conf'; }
export interface ModpackRelease { versionId: string; versionName: string; minecraftVersion: string; loader: string; releaseChannel: 'Stable' | 'Beta' | 'Alpha'; publishedAt: string | null; sizeBytes: number | null; changelog: string; requiredJavaMajor: number; hasIntegrity: boolean; canCreate: boolean; limitation?: string; }
export type ModpackProvider = 'Modrinth' | 'CurseForge';
export type ModpackCatalogLoadState = 'Ready' | 'Empty' | 'OfflineCache' | 'AuthenticationRequired' | 'RateLimited' | 'Failed';
export interface ModpackProject { provider: ModpackProvider; projectId: string; slug: string; name: string; author: string; summary: string; downloadCount: number | null; updatedAt: string | null; categories: string[]; hasImage: boolean; serverSupport: string; clientRequirement: string; trend: { available: boolean; detail: string }; versions: ModpackRelease[]; }
export interface ModpackCatalogResult { provider: ModpackProvider; state: ModpackCatalogLoadState; items: ModpackProject[]; detail: string; failedStage: string; retrievedAt: string | null; fromCache: boolean; stale: boolean; }
export interface ModpackProviderStatus { provider: ModpackProvider; available: boolean; detail: string; }
export type CatalogGameVersionKind = 'Release' | 'Snapshot' | 'Beta' | 'Alpha' | 'Unknown';
export interface ModpackGameVersion { versionId: string; kind: CatalogGameVersionKind; publishedAt: string | null; isMajor: boolean; }
export interface ModpackVersionInventory { provider: ModpackProvider; state: ModpackCatalogLoadState; versions: ModpackGameVersion[]; detail: string; failedStage: string; retrievedAt: string | null; fromCache: boolean; stale: boolean; }
export interface ResolvedModpackLink { canonicalUrl: string; exactRelease: boolean; project: ModpackProject; release: ModpackRelease; detail: string; }
export interface LocalModpackSelection {
  cancelled: boolean;
  token?: string;
  fileName?: string;
  expiresAt?: string;
  managementMode?: 'ManagedCopy' | 'ByReference';
  launchRelativePath?: string;
  inspection?: {
    sourceKind: 'ModrinthPack' | 'CurseForgePack' | 'ServerArchive' | 'ServerJar' | 'ServerFolder';
    name: string;
    summary: string;
    minecraftVersion: string;
    loader: string;
    loaderVersion: string;
    requiredJavaMajor: number;
    requiredServerFiles: number;
    optionalServerFiles: number;
    excludedClientFiles: number;
    indexedServerBytes: number;
    sourceSizeBytes: number;
    expandedSizeBytes: number;
    fileCount: number;
    modCount: number;
    pluginCount: number;
    containsWorld: boolean;
    serverRoot: string;
    launchCandidates: string[];
    canReference: boolean;
    canCreate: boolean;
    limitation: string;
  };
}
export interface TextFileContent { relativePath: string; content: string; encodingName: string; hasBom: boolean; lineEnding: string; loadedSha256: string; loadedLastWriteAt: string | null; }
export interface ScheduleEntry { id: string; serverId: string; name: string; action: string; kind: string; intervalMinutes: number; at: string; cron: string; command: string; enabled: boolean; nextRunAt: string | null; lastRunAt: string | null; backupBeforeRestart: boolean; restartCountdownSeconds: number; }
export interface BackupEntry { id: string; createdAt: string; description: string; sizeBytes: number; verified: boolean; source: string; }
export interface VersionEntry { id: string; version: string; platform: string; installedAt: string | null; active: boolean; verified: boolean; health: string; snapshotSizeBytes: number; includesWorldData: boolean; rollbackReady: boolean; }
export interface UpdateSummary { status: string; detail: string; sourceLinked: boolean; provider: string | null; projectId: string | null; projectName: string | null; installedVersionId: string | null; installedVersionName: string | null; releaseChannel: string | null; minecraftVersion: string | null; loader: string | null; loaderVersion: string | null; checkedAt: string | null; targetVersionId?: string | null; latestVersionName: string | null; targetPublishedAt?: string | null; downloadSizeBytes?: number | null; compatibilityReasons?: string[]; compatibility: string | null; canInstall: boolean; operationState: string | null; operationStep?: string | null; operationDetail?: string | null; operationPercent: number | null; cancellable: boolean; }
export interface ActivityEntry { id: number; timestamp: string; serverId: string | null; serverName: string; action: string; result: string; error: string | null; durationMs: number; }

export type ConnectivityMode = 'ThisComputerOnly' | 'HomeNetwork' | 'PortForwarding' | 'ConfigureLater';
export type SemanticTone = 'neutral' | 'info' | 'success' | 'warning' | 'danger';

export interface ConnectivitySnapshot {
  serverId: string;
  mode: ConnectivityMode;
  modeTitle: string;
  modeSummary: string;
  status: { title: string; detail: string; tone: SemanticTone };
  addresses: {
    local: string;
    lan: string | null;
    publicVerified: string | null;
    routerReported: string | null;
    publicVerifiedAt: string | null;
    lastKnownPublic: string | null;
    lastKnownPublicAt: string | null;
  };
  router: {
    phase: string;
    title: string;
    summary: string;
    badge: string;
    tone: SemanticTone;
    busy: boolean;
    enabled: boolean;
    consentRequired: boolean;
    consentPoints: string[];
    canCheck: boolean;
    canEnable: boolean;
    canStop: boolean;
    canCancel: boolean;
    canRetryCleanup: boolean;
    routerReportedCaveat: string;
    upstreamNotice: string | null;
    mechanism: string;
    transport: string;
    gateway: string;
    internalEndpoint: string;
    externalPort: string;
    lease: string;
    lastChecked: string;
    addressClass: string;
    detail: string;
  };
  firewall: {
    phase: string;
    title: string;
    summary: string;
    badge: string;
    tone: SemanticTone;
    busy: boolean;
    configured: boolean;
    consentRequired: boolean;
    consentTitle: string;
    consentMessage: string;
    primaryAction: string | null;
    secondaryAction: string | null;
    canRemove: boolean;
    canCancel: boolean;
    network: string;
    port: string;
    profile: string;
    enabled: string;
    lastChecked: string;
    detail: string;
  };
  external: {
    phase: string;
    blocker: string;
    title: string;
    summary: string;
    badge: string;
    tone: SemanticTone;
    busy: boolean;
    canCheck: boolean;
    canCancel: boolean;
    firstUseNotice: string | null;
    verifiedEndpoint: string | null;
    verifiedAt: string | null;
    checkedAt: string;
    observedAddress: string;
    routerAddress: string;
    port: string;
    connectTime: string;
    addressComparison: string | null;
    upstreamAssessment: string | null;
    detail: string;
  };
}

export interface WebUiSnapshot {
  revision: number;
  capturedAt: string;
  agentConnected: boolean;
  appVersion: string;
  build: {
    productVersion: string;
    releaseTag: string;
    gitSha: string;
    buildTimestampUtc: string;
    schemaVersion: string;
    architecture: string;
    defaultUi: string;
  };
  selectedServerId: string | null;
  operation: { method: string; serverId: string | null; message: string } | null;
  statusMessage: string | null;
  host: {
    cpuPercent: number | null;
    usedMemoryBytes: number | null;
    totalMemoryBytes: number | null;
    freeDiskBytes: number | null;
    totalDiskBytes: number | null;
    cpuModel: string | null;
  };
  servers: ServerSummary[];
  connectivity: ConnectivitySnapshot | null;
  playerAccess: {
    serverId: string;
    serverRunning: boolean;
    whitelistEnabled: boolean;
    supportsAllowlist: boolean;
    supportsOperators: boolean;
    supportsPlayerBans: boolean;
    supportsIpBans: boolean;
    capabilityKnown: boolean;
    error: string | null;
  } | null;
  issues: ServerHealthIssue[];
  console: ConsoleEntry[];
  players: PlayerEntry[];
  files: FileEntry[];
  plugins: PluginInventoryEntry[];
  currentFolder: string;
  schedules: ScheduleEntry[];
  backups: BackupEntry[];
  versions: VersionEntry[];
  update: UpdateSummary | null;
  activity: ActivityEntry[];
  settings: {
    minimizeToTray: boolean;
    startMinimized: boolean;
    startWithWindows: boolean;
    reducedMotion: boolean;
  };
  serverSettings: {
    serverId: string;
    name: string;
    motd: string;
    port: number;
    maximumPlayers: number;
    difficulty: string;
    gameMode: string;
    pvp: boolean;
    allowlist: boolean;
    minimumRamMb: number;
    maximumRamMb: number;
    runInBackground: boolean;
  } | null;
}

export type BridgeMethod =
  | 'renderer.ready' | 'snapshot.get' | 'snapshot.selectServer' | 'snapshot.refresh' | 'bridge.cancel'
  | 'window.drag' | 'window.minimize' | 'window.toggleMaximize' | 'window.close'
  | 'servers.start' | 'servers.stop' | 'servers.restart' | 'servers.openFolder'
  | 'servers.deletePreflight' | 'servers.delete' | 'servers.createManagedCopy'
  | 'diagnostics.openLogs' | 'diagnostics.bundle'
  | 'help.openExternal'
  | 'servers.import' | 'servers.rename' | 'servers.changeIcon'
  | 'appearance.chooseIcon'
  | 'plugins.openFolder' | 'plugins.chooseLocal' | 'plugins.installLocal' | 'plugins.providers' | 'plugins.search' | 'plugins.release'
  | 'plugins.install' | 'plugins.plan' | 'plugins.installPlan' | 'plugins.setEnabled' | 'plugins.remove' | 'plugins.configFiles' | 'plugins.saveConfig'
  | 'mods.openFolder' | 'mods.chooseLocal' | 'mods.installLocal' | 'mods.providers' | 'mods.search' | 'mods.release'
  | 'mods.install' | 'mods.plan' | 'mods.installPlan' | 'mods.setEnabled' | 'mods.remove' | 'mods.configFiles' | 'mods.saveConfig'
  | 'content.operations' | 'content.cancel'
  | 'modpacks.providers' | 'modpacks.versions' | 'modpacks.cache' | 'modpacks.search' | 'modpacks.resolveLink' | 'modpacks.image' | 'modpacks.chooseLocal'
  | 'console.send' | 'workspace.load' | 'files.openFolder' | 'files.navigate' | 'files.read' | 'files.write'
  | 'backups.create' | 'backups.restore' | 'backups.verify'
  | 'players.moderate' | 'players.addAllowlist' | 'players.setWhitelist' | 'players.head' | 'schedules.upsert' | 'schedules.delete' | 'settings.saveGlobal' | 'settings.saveServer'
  | 'connectivity.copyAddress' | 'connectivity.open' | 'connectivity.setMode'
  | 'connectivity.router.check' | 'connectivity.router.confirm' | 'connectivity.router.cancelConsent' | 'connectivity.router.stop' | 'connectivity.router.cancel' | 'connectivity.router.retry'
  | 'connectivity.external.check' | 'connectivity.external.cancel'
  | 'connectivity.firewall.primary' | 'connectivity.firewall.secondary' | 'connectivity.firewall.confirm' | 'connectivity.firewall.cancelConsent' | 'connectivity.firewall.remove' | 'connectivity.firewall.cancel'
  | 'versions.check' | 'versions.install' | 'versions.rollback' | 'versions.verify' | 'versions.cancel'
  | 'creation.catalog' | 'creation.paperBuilds' | 'creation.loaderBuilds' | 'creation.previewDestination' | 'creation.chooseFolder' | 'creation.chooseWorld' | 'creation.chooseLegacyArtifact' | 'creation.begin' | 'creation.operations' | 'creation.progress' | 'creation.cancel';

export interface BridgeRequest {
  protocolVersion: typeof protocolVersion;
  id: string;
  method: BridgeMethod;
  params: Record<string, unknown>;
}

export interface BridgeResponse {
  protocolVersion: number;
  id: string;
  ok: boolean;
  result?: unknown;
  error?: { code: string; message: string; details?: string };
}

export interface BridgeEvent {
  protocolVersion: number;
  event: 'snapshot.changed' | 'operation.progress' | 'operation.completed' | 'renderer.reload';
  revision: number;
  payload: unknown;
}
