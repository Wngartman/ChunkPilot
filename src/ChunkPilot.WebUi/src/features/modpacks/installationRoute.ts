import type { ModpackRelease } from '../../bridge/types';

export function modpackRouteLabel(release: ModpackRelease): string {
  switch (release.installationRoute) {
    case 'OfficialServerPack': return 'Official server pack';
    case 'GeneratedCandidate': return 'Build and validate a server locally';
    case 'Unavailable': return 'No supportable server setup found';
    case 'Unchecked': return 'Server setup not checked yet';
    default: return release.serverPath ?? 'Server setup not checked yet';
  }
}
