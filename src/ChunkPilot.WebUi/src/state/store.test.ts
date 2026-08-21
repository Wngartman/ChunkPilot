import { beforeEach, describe, expect, it } from 'vitest';
import type { BridgeAdapter } from '../bridge/client';
import { fixtures } from '../fixtures/catalog';
import { useAppStore } from './store';

describe('authoritative WebUI store', () => {
  beforeEach(() => useAppStore.setState({ snapshot: null, bridge: null, busy: new Set(), pendingOperations: new Map(), completedOperations: new Set(), error: null }));

  it('rejects a stale snapshot revision', () => {
    const newest = { ...fixtures.running, revision: 20 };
    const stale = { ...fixtures.stopped, revision: 19 };
    useAppStore.getState().applySnapshot(newest);
    useAppStore.getState().applySnapshot(stale);
    expect(useAppStore.getState().snapshot?.servers[0].state).toBe('Running');
  });

  it('tracks a pending command without claiming lifecycle success', async () => {
    let complete!: () => void;
    const request: BridgeAdapter['request'] = <T,>() => new Promise<T>(resolve => {
      complete = () => resolve({ accepted: true, operationId: 'operation-1' } as T);
    });
    const bridge: BridgeAdapter = { request, subscribe: () => () => undefined, dispose: () => undefined };
    useAppStore.getState().setBridge(bridge);
    const operation = useAppStore.getState().command('servers.start', { serverId: 'server-1' });
    expect(useAppStore.getState().busy.has('servers.start')).toBe(true);
    expect(useAppStore.getState().snapshot).toBeNull();
    complete();
    await operation;
    expect(useAppStore.getState().busy.has('servers.start')).toBe(true);
    useAppStore.getState().consumeEvent({ protocolVersion: 1, event: 'operation.completed', revision: 1, payload: { operationId: 'operation-1', method: 'servers.start', success: true } });
    expect(useAppStore.getState().busy.has('servers.start')).toBe(false);
  });

  it('reconciles a late lifecycle failure without submitting a duplicate command', async () => {
    let requests = 0;
    const bridge: BridgeAdapter = {
      request: async <T,>() => { requests += 1; return { accepted: true, operationId: 'operation-2' } as T; },
      subscribe: () => () => undefined,
      dispose: () => undefined
    };
    useAppStore.getState().setBridge(bridge);
    await useAppStore.getState().command('servers.start', { serverId: 'server-1' });
    expect(requests).toBe(1);
    useAppStore.getState().consumeEvent({ protocolVersion: 1, event: 'operation.completed', revision: 2, payload: { operationId: 'operation-2', method: 'servers.start', success: false, error: 'The port is already in use.' } });
    expect(useAppStore.getState().busy.has('servers.start')).toBe(false);
    expect(useAppStore.getState().error).toContain('port');
    expect(requests).toBe(1);
  });

  it('ignores a stale completion for a different operation ID', async () => {
    const bridge: BridgeAdapter = {
      request: async <T,>() => ({ accepted: true, operationId: 'operation-current' } as T),
      subscribe: () => () => undefined,
      dispose: () => undefined
    };
    useAppStore.getState().setBridge(bridge);
    await useAppStore.getState().command('servers.start', { serverId: 'server-1' });

    useAppStore.getState().consumeEvent({ protocolVersion: 1, event: 'operation.completed', revision: 3,
      payload: { operationId: 'operation-old', method: 'servers.start', success: false, error: 'stale' } });

    expect(useAppStore.getState().busy.has('servers.start')).toBe(true);
    expect(useAppStore.getState().error).toBeNull();
  });

  it('reconciles completion that arrives before the acceptance response is consumed', async () => {
    let release!: (value: unknown) => void;
    const bridge: BridgeAdapter = {
      request: <T,>() => new Promise<T>(resolve => { release = resolve as (value: unknown) => void; }),
      subscribe: () => () => undefined,
      dispose: () => undefined
    };
    useAppStore.getState().setBridge(bridge);
    const request = useAppStore.getState().command('servers.start', { serverId: 'server-1' });
    useAppStore.getState().consumeEvent({ protocolVersion: 1, event: 'operation.completed', revision: 4,
      payload: { operationId: 'operation-fast', method: 'servers.start', success: true } });
    release({ accepted: true, operationId: 'operation-fast' });
    await request;

    expect(useAppStore.getState().busy.has('servers.start')).toBe(false);
    expect(useAppStore.getState().pendingOperations.size).toBe(0);
  });

  it('keeps server deletion pending after prompt acceptance until the native operation completes', async () => {
    const bridge: BridgeAdapter = {
      request: async <T,>() => ({ accepted: true, operationId: 'delete-operation' } as T),
      subscribe: () => () => undefined,
      dispose: () => undefined
    };
    useAppStore.getState().setBridge(bridge);

    await useAppStore.getState().command('servers.delete', { serverId: 'server-1' });
    expect(useAppStore.getState().busy.has('servers.delete')).toBe(true);

    useAppStore.getState().consumeEvent({ protocolVersion: 1, event: 'operation.completed', revision: 5,
      payload: { operationId: 'delete-operation', method: 'servers.delete', success: true } });

    expect(useAppStore.getState().busy.has('servers.delete')).toBe(false);
  });

  it('keeps managed-copy conversion pending until verification and registration complete', async () => {
    const bridge: BridgeAdapter = {
      request: async <T,>() => ({ accepted: true, operationId: 'managed-copy-operation' } as T),
      subscribe: () => () => undefined,
      dispose: () => undefined
    };
    useAppStore.getState().setBridge(bridge);

    await useAppStore.getState().command('servers.createManagedCopy', { serverId: 'server-1', preflightToken: 'review' });
    expect(useAppStore.getState().busy.has('servers.createManagedCopy')).toBe(true);

    useAppStore.getState().consumeEvent({ protocolVersion: 1, event: 'operation.completed', revision: 6,
      payload: { operationId: 'managed-copy-operation', method: 'servers.createManagedCopy', success: true } });

    expect(useAppStore.getState().busy.has('servers.createManagedCopy')).toBe(false);
  });

  it('keeps a pack update pending after prompt acceptance until the Agent operation completes', async () => {
    const bridge: BridgeAdapter = {
      request: async <T,>() => ({ accepted: true, operationId: 'update-operation' } as T),
      subscribe: () => () => undefined,
      dispose: () => undefined
    };
    useAppStore.getState().setBridge(bridge);

    await useAppStore.getState().command('versions.install', { serverId: 'server-1', operationId: 'update-operation' });
    expect(useAppStore.getState().busy.has('versions.install')).toBe(true);

    useAppStore.getState().consumeEvent({ protocolVersion: 1, event: 'operation.completed', revision: 6,
      payload: { operationId: 'update-operation', method: 'versions.install', success: false, error: 'Activation failed; rollback completed.' } });

    expect(useAppStore.getState().busy.has('versions.install')).toBe(false);
    expect(useAppStore.getState().error).toContain('rollback');
  });

  it('keeps unknown player and metric values distinct from zero', () => {
    const unknown = fixtures.unknown.servers[0];
    expect(unknown.playersOnline).toBeNull();
    expect(unknown.cpuPercent).toBeNull();
    expect(unknown.memoryBytes).toBeNull();
  });

  it('applies a newer snapshot event and ignores an older event afterward', () => {
    useAppStore.getState().consumeEvent({ protocolVersion: 1, event: 'snapshot.changed', revision: 8, payload: { ...fixtures.running, revision: 8 } });
    useAppStore.getState().consumeEvent({ protocolVersion: 1, event: 'snapshot.changed', revision: 7, payload: { ...fixtures.stopped, revision: 7 } });
    expect(useAppStore.getState().snapshot?.revision).toBe(8);
    expect(useAppStore.getState().snapshot?.servers[0].state).toBe('Running');
  });

  it('exposes a structured command failure while clearing pending state', async () => {
    const bridge: BridgeAdapter = {
      request: async () => { throw new Error('Port 25565 is already in use.'); },
      subscribe: () => () => undefined,
      dispose: () => undefined
    };
    useAppStore.getState().setBridge(bridge);
    await expect(useAppStore.getState().command('servers.start')).rejects.toThrow('Port 25565');
    expect(useAppStore.getState().error).toContain('Port 25565');
    expect(useAppStore.getState().busy.has('servers.start')).toBe(false);
  });
});
