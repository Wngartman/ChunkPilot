export type MinecraftSupportTier = 'Recommended' | 'Verified' | 'Experimental' | 'Unavailable';
export type MinecraftReleaseKind = 'Release' | 'Snapshot' | 'PreRelease' | 'ReleaseCandidate' | 'ExperimentalSnapshot' | 'Beta' | 'Alpha' | 'Unknown';

export interface MinecraftVersionCapabilities {
  serverIcon: boolean;
  formattedMotd: boolean;
  playerManagement: boolean;
  modernServerProperties: boolean;
  statusQuery: boolean;
  datapacks: boolean;
  managedVersionChange: boolean;
}

export interface MinecraftVersionOption {
  id: string;
  label: string;
  channel: 'Stable' | 'Snapshot' | 'Historic';
  releaseKind: MinecraftReleaseKind;
  releaseTime: string | null;
  javaMajor: number | null;
  javaSource: 'Unknown' | 'OfficialMetadata' | 'ChunkPilotPolicy';
  support: MinecraftSupportTier;
  supportReason: string;
  selectable: boolean;
  hasServerArtifact: boolean;
  artifactSize: number | null;
  hasIntegrityMetadata: boolean;
  launchProfile: { kind: string; arguments: string; requiresEulaFile: boolean; evidence: string };
  certification: {
    level: 'Inventoried' | 'MetadataValidated' | 'RuntimeCertified' | 'Failed';
    runtimeLaunched: boolean;
    readinessConfirmed: boolean;
    cleanShutdownConfirmed: boolean;
    runtimeValidatedAt: string | null;
    limitations: string[];
  };
  capabilities: MinecraftVersionCapabilities;
  warnings: string[];
  evidence: string[];
  provenance: string;
}

export interface MinecraftVersionCatalog {
  platform?: 'Vanilla' | 'Paper';
  available: boolean;
  message: string;
  fromCache: boolean;
  stale: boolean;
  retrievedAt: string | null;
  manifestLatestReleaseId: string;
  manifestLatestSnapshotId: string;
  latestVerifiedReleaseId: string;
  versions: MinecraftVersionOption[];
}

export type VersionFilter = 'recommended' | 'verified' | 'stable' | 'development' | 'beta' | 'alpha' | 'experimental' | 'unavailable' | 'all';

export function versionMatchesFilter(version: MinecraftVersionOption, filter: VersionFilter): boolean {
  if (filter === 'recommended') return version.support === 'Recommended';
  if (filter === 'verified') return version.releaseKind === 'Release' && (version.support === 'Recommended' || version.support === 'Verified');
  if (filter === 'stable') return version.releaseKind === 'Release';
  if (filter === 'development') return ['Snapshot', 'PreRelease', 'ReleaseCandidate', 'ExperimentalSnapshot'].includes(version.releaseKind);
  if (filter === 'beta') return version.releaseKind === 'Beta';
  if (filter === 'alpha') return version.releaseKind === 'Alpha';
  if (filter === 'experimental') return version.support === 'Experimental';
  if (filter === 'unavailable') return version.support === 'Unavailable';
  return true;
}

export function releaseKindLabel(kind: MinecraftReleaseKind): string {
  return ({ Release: 'Release', Snapshot: 'Snapshot', PreRelease: 'Pre-release', ReleaseCandidate: 'Release candidate', ExperimentalSnapshot: 'Experimental snapshot', Beta: 'Beta', Alpha: 'Alpha', Unknown: 'Unknown build' } as const)[kind];
}

export function supportTone(support: MinecraftSupportTier): 'success' | 'info' | 'warning' | 'neutral' {
  return support === 'Recommended' ? 'success' : support === 'Verified' ? 'info' : support === 'Experimental' ? 'warning' : 'neutral';
}
