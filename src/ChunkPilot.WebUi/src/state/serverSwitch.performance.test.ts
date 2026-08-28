import { beforeEach, describe, expect, it } from 'vitest';
import type { BridgeAdapter } from '../bridge/client';
import type { BridgeMethod, WebUiSnapshot } from '../bridge/types';
import { fixtures } from '../fixtures/catalog';
import { useAppStore } from './store';

describe('server switch performance evidence', () => {
  beforeEach(() => useAppStore.setState({
    snapshot: null,
    bridge: null,
    busy: new Set(),
    pendingOperations: new Map(),
    completedOperations: new Set(),
    serverSnapshots: new Map(),
    cachedPresentationServerId: null,
    error: null
  }));

  it('measures 30 warm, 10 cold, and 20 rapid identity-safe selections', async () => {
    const alpha = selectedSnapshot('alpha', 500, 'Alpha player', 'Ready');
    const bravo = selectedSnapshot('bravo', 501, 'Bravo player', 'Ready');
    const allServers = [alpha.servers[0], bravo.servers[0]];
    alpha.servers = allServers;
    bravo.servers = allServers;
    useAppStore.getState().applySnapshot(alpha);
    useAppStore.getState().applySnapshot(bravo);

    const warm: number[] = [];
    for (let index = 0; index < 30; index += 1) {
      const serverId = index % 2 === 0 ? 'alpha' : 'bravo';
      const started = performance.now();
      useAppStore.getState().prepareServerSelection(serverId);
      warm.push(performance.now() - started);
      expect(useAppStore.getState().snapshot?.selectedServerId).toBe(serverId);
      expect(useAppStore.getState().snapshot?.players[0]?.name).toBe(serverId === 'alpha' ? 'Alpha player' : 'Bravo player');
    }

    let revision = 600;
    let nativeSelectionRequests = 0;
    let remoteProviderRequests = 0;
    const coldBridge: BridgeAdapter = {
      request: async <T,>(method: BridgeMethod, params?: Record<string, unknown>) => {
        if (method !== 'snapshot.selectServer') remoteProviderRequests += 1;
        else nativeSelectionRequests += 1;
        const serverId = String(params?.serverId);
        return selectedSnapshot(serverId, ++revision, '', 'Loading') as T;
      },
      subscribe: () => () => undefined,
      dispose: () => undefined
    };
    useAppStore.getState().setBridge(coldBridge);
    const cold: number[] = [];
    for (let index = 0; index < 10; index += 1) {
      const serverId = `cold-${index}`;
      const started = performance.now();
      await useAppStore.getState().command('snapshot.selectServer', { serverId });
      cold.push(performance.now() - started);
      expect(useAppStore.getState().snapshot?.selectedServerId).toBe(serverId);
    }

    const deferred: Array<{ resolve: (snapshot: WebUiSnapshot) => void; serverId: string; revision: number }> = [];
    const rapidBridge: BridgeAdapter = {
      request: <T,>(method: BridgeMethod, params?: Record<string, unknown>) => {
        if (method !== 'snapshot.selectServer') remoteProviderRequests += 1;
        else nativeSelectionRequests += 1;
        const serverId = String(params?.serverId);
        const assignedRevision = ++revision;
        return new Promise<T>(resolve => deferred.push({
          resolve: resolve as (snapshot: WebUiSnapshot) => void,
          serverId,
          revision: assignedRevision
        }));
      },
      subscribe: () => () => undefined,
      dispose: () => undefined
    };
    useAppStore.getState().setBridge(rapidBridge);
    const rapidStarted = performance.now();
    const rapidRequests = Array.from({ length: 20 }, (_, index) =>
      useAppStore.getState().command('snapshot.selectServer', { serverId: `rapid-${index}` }));
    for (const request of [...deferred].reverse())
      request.resolve(selectedSnapshot(request.serverId, request.revision, '', 'Loading'));
    await Promise.all(rapidRequests);
    const rapid = performance.now() - rapidStarted;

    expect(useAppStore.getState().snapshot?.selectedServerId).toBe('rapid-19');
    expect(nativeSelectionRequests).toBe(30);
    expect(remoteProviderRequests).toBe(0);
    console.info(`SERVER_SWITCH_METRICS=${JSON.stringify({
      warmCachedMs: summarize(warm),
      coldAcknowledgementMs: summarize(cold),
      rapid20TotalMs: Number(rapid.toFixed(3)),
      nativeSelectionRequests,
      remoteProviderRequests
    })}`);
  });
});

function selectedSnapshot(
  serverId: string,
  revision: number,
  playerName: string,
  state: 'Loading' | 'Ready'
): WebUiSnapshot {
  const snapshot = structuredClone(fixtures.running);
  snapshot.revision = revision;
  snapshot.selectedServerId = serverId;
  snapshot.workspace = { serverId, state };
  snapshot.servers = [{ ...snapshot.servers[0], id: serverId, name: `Server ${serverId}` }];
  snapshot.players = playerName.length === 0 ? [] : [{
    name: playerName,
    uuid: `${serverId}-uuid`,
    online: true,
    allowlisted: true,
    operator: false,
    banned: false
  }];
  return snapshot;
}

function summarize(values: number[]) {
  const sorted = [...values].sort((left, right) => left - right);
  const percentile = (value: number) => sorted[Math.min(sorted.length - 1, Math.ceil(sorted.length * value) - 1)];
  return {
    p50: Number(percentile(0.5).toFixed(3)),
    p95: Number(percentile(0.95).toFixed(3)),
    max: Number(sorted.at(-1)!.toFixed(3))
  };
}
