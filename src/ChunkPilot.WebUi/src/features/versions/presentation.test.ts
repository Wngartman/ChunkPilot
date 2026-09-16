import { describe, expect, it } from 'vitest';
import { missingInstalledBuildTitle, versionHealthLabel } from './presentation';

describe('installed version presentation', () => {
  it.each([
    ['Healthy', 'Healthy'], ['PendingValidation', 'Pending validation'],
    ['Failed', 'Failed'], ['RolledBack', 'Rolled back'], ['FutureStatus', 'Health not recorded']
  ])('presents %s without a machine-name leak', (raw, label) => {
    expect(versionHealthLabel(raw)).toBe(label);
  });

  it.each(['Fabric', 'Forge', 'NeoForge', 'Quilt', 'Paper'])('does not call an unidentified %s version unsupported', platform => {
    expect(missingInstalledBuildTitle(platform, '')).toBe(`${platform} version not identified`);
    expect(missingInstalledBuildTitle(platform, '  ')).toBe(`${platform} version not identified`);
    expect(missingInstalledBuildTitle(platform, undefined)).toBe(`${platform} version not identified`);
  });

  it('keeps a known but unlisted installed identity exact', () => {
    expect(missingInstalledBuildTitle('Fabric', '0.16.10')).toBe('Fabric 0.16.10 is not in the current inventory');
    expect(missingInstalledBuildTitle('Paper', '123')).toBe('Paper build 123 is not in the current inventory');
  });
});
