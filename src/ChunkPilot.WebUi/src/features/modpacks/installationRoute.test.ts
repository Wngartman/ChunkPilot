import { describe, expect, it } from 'vitest';
import type { ModpackRelease } from '../../bridge/types';
import { modpackRouteLabel } from './installationRoute';

describe('exact native installation route labels', () => {
  it.each([
    ['Unchecked', 'Server setup not checked yet'],
    ['OfficialServerPack', 'Official server pack'],
    ['GeneratedCandidate', 'Build and validate a server locally'],
    ['Unavailable', 'No supportable server setup found']
  ] as const)('uses %s over a contradictory legacy path label', (installationRoute, expected) => {
    const release = { installationRoute, serverPath: 'Official server pack' } as ModpackRelease;
    expect(modpackRouteLabel(release)).toBe(expected);
  });

  it('preserves the existing non-CurseForge route contract', () => {
    expect(modpackRouteLabel({ serverPath: 'Official server pack' } as ModpackRelease)).toBe('Official server pack');
  });
});
