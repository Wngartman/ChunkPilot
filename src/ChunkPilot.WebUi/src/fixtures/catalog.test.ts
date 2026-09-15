import { describe, expect, it } from 'vitest';
import { fixtures } from './catalog';

describe('native-evidence review fixtures', () => {
  it('provides a server-bound native startup record without making the server ready', () => {
    const server = fixtures.starting.servers[0];
    expect(server.state).toBe('Starting');
    expect(server.startupProgress).toMatchObject({ serverId: server.id, stage: 'PreparingWorld', isActive: true });
    expect(server.connection.listenerVerified).toBe(false);
    expect(fixtures.starting.playerAccess?.canManageWhileStopped).toBe(false);
  });

  it('provides stopped access capability without fabricated online players or running state', () => {
    expect(fixtures.stopped.playerAccess).toMatchObject({ serverRunning: false, canManageWhileStopped: true });
    expect(fixtures.stopped.playerAccess?.accessAvailabilityDetail).toContain('locally known Java UUID');
    expect(fixtures.stopped.players.some(player => player.online)).toBe(false);
  });
});
