import { describe, expect, it } from 'vitest';
import { releaseKindLabel, versionMatchesFilter, type MinecraftVersionOption } from './types';

const version = (releaseKind: MinecraftVersionOption['releaseKind'], support: MinecraftVersionOption['support']): MinecraftVersionOption => ({
  id: 'fixture', label: 'Minecraft fixture', channel: releaseKind === 'Release' ? 'Stable' : 'Snapshot', releaseKind,
  releaseTime: null, javaMajor: 21, javaSource: 'OfficialMetadata', support, supportReason: '', selectable: support !== 'Unavailable',
  hasServerArtifact: true, artifactSize: 1, hasIntegrityMetadata: true,
  launchProfile: { kind: 'ModernEulaNogui', arguments: 'nogui', requiresEulaFile: true, evidence: '' },
  certification: { level: 'MetadataValidated', runtimeLaunched: false, readinessConfirmed: false, cleanShutdownConfirmed: false, runtimeValidatedAt: null, limitations: [] },
  capabilities: { serverIcon: true, formattedMotd: true, playerManagement: true, modernServerProperties: true, statusQuery: true, datapacks: true, managedVersionChange: true },
  warnings: [], evidence: [], provenance: 'Official Mojang version metadata'
});

describe('Minecraft version browser policy', () => {
  it('defaults to verified stable builds without treating existence as support', () => {
    expect(versionMatchesFilter(version('Release', 'Recommended'), 'verified')).toBe(true);
    expect(versionMatchesFilter(version('Release', 'Verified'), 'verified')).toBe(true);
    expect(versionMatchesFilter(version('Release', 'Unavailable'), 'verified')).toBe(false);
    expect(versionMatchesFilter(version('Snapshot', 'Experimental'), 'verified')).toBe(false);
  });

  it('keeps development and historical channels distinct', () => {
    expect(versionMatchesFilter(version('PreRelease', 'Experimental'), 'development')).toBe(true);
    expect(versionMatchesFilter(version('ReleaseCandidate', 'Experimental'), 'development')).toBe(true);
    expect(versionMatchesFilter(version('Beta', 'Unavailable'), 'beta')).toBe(true);
    expect(versionMatchesFilter(version('Alpha', 'Unavailable'), 'alpha')).toBe(true);
    expect(releaseKindLabel('ExperimentalSnapshot')).toBe('Experimental snapshot');
  });
});
