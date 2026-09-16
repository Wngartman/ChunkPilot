// @vitest-environment jsdom
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import type { BridgeAdapter } from '../bridge/client';
import type { BridgeMethod } from '../bridge/types';
import { fixtures } from '../fixtures/catalog';
import { NavigationGuardProvider } from '../app/NavigationGuard';
import { useAppStore } from '../state/store';
import { ServerWorkspace } from './ServerWorkspace';

const calls: { method: BridgeMethod; params: Record<string, unknown> }[] = [];
const bridge: BridgeAdapter = {
  request: async <T,>(method: BridgeMethod, params: Record<string, unknown> = {}) => {
    calls.push({ method, params });
    if (method === 'creation.loaderBuilds' || method === 'creation.paperBuilds') return { available: false, builds: [], message: 'Synthetic catalog unavailable.' } as T;
    if (method === 'creation.catalog') return {
      available: false,
      message: 'Catalog not needed by this rollback test.',
      retrievedAt: null,
      fromCache: false,
      stale: false,
      manifestLatestReleaseId: '',
      manifestLatestSnapshotId: '',
      latestVerifiedReleaseId: '',
      versions: []
    } as T;
    return { accepted: true } as T;
  },
  subscribe: () => () => undefined,
  dispose: () => undefined
};

beforeEach(() => {
  calls.length = 0;
  window.history.replaceState({}, '', '/?tab=versions');
  const current = structuredClone(fixtures.running);
  current.servers[0].state = 'Stopped';
  useAppStore.setState({ snapshot: current, bridge, busy: new Set(), error: null });
});
afterEach(cleanup);

function renderVersions() {
  const snapshot = useAppStore.getState().snapshot!;
  const server = snapshot.servers[0];
  const target = snapshot.versions.find(version => version.rollbackReady)!;
  render(<NavigationGuardProvider><ServerWorkspace serverId={server.id} /></NavigationGuardProvider>);
  return { server, target };
}

function renderPendingValidation() {
  const current = structuredClone(fixtures.running);
  const server = current.servers[0];
  const active = current.versions.find(version => version.active)!;
  active.id = '1e01c49c-566b-45cc-a662-0c42f19363ce';
  active.health = 'PendingValidation';
  current.update!.status = 'Pending validation';
  current.update!.pendingValidation = {
    serverId: server.id,
    versionId: active.id,
    versionName: active.version
  };
  useAppStore.setState({ snapshot: current, bridge, busy: new Set(), error: null });
  const rendered = render(<NavigationGuardProvider><ServerWorkspace serverId={server.id} /></NavigationGuardProvider>);
  return { ...rendered, server, active };
}

describe('server version rollback', () => {
  it('shows readable version health without changing its machine value', () => {
    const snapshot = useAppStore.getState().snapshot!;
    snapshot.versions[0].health = 'RolledBack';
    snapshot.versions[1].health = 'PendingValidation';
    renderVersions();
    expect(screen.getByText('Rolled back')).toBeTruthy();
    expect(screen.getByText('Pending validation')).toBeTruthy();
    expect(screen.queryByText('RolledBack')).toBeNull();
    expect(screen.queryByText('PendingValidation')).toBeNull();
    expect(useAppStore.getState().snapshot!.versions[0].health).toBe('RolledBack');
  });

  it.each(['Fabric', 'Paper'])('describes missing %s identity without declaring an installed version unsupported', async platform => {
    const snapshot = useAppStore.getState().snapshot!;
    snapshot.servers[0].ecosystem = platform;
    snapshot.servers[0].capabilities.versioning = platform === 'Paper' ? 'paper' : 'fabric';
    snapshot.servers[0].loaderVersion = '';
    renderVersions();
    await waitFor(() => expect(calls.some(call => call.method === (platform === 'Paper' ? 'creation.paperBuilds' : 'creation.loaderBuilds'))).toBe(true));
    expect(screen.getByRole('heading', { name: `${platform} version not identified` })).toBeTruthy();
    expect(screen.getByText('The exact installed version was not recorded, so catalog support cannot be matched.')).toBeTruthy();
    expect(screen.queryByText(/unknown is not in the current inventory/)).toBeNull();
  });

  it('does not dispatch rollback until the in-product confirmation is accepted', () => {
    renderVersions();

    fireEvent.click(screen.getByRole('button', { name: 'Roll back' }));
    const confirmation = screen.getByRole('alertdialog', { name: 'Roll back to 1.21.7?' });
    expect(within(confirmation).getByText(/preserve the current installation in a safety snapshot/i)).toBeTruthy();
    expect(calls.filter(call => call.method === 'versions.rollback')).toEqual([]);

    fireEvent.click(within(confirmation).getByRole('button', { name: 'Cancel' }));
    expect(screen.queryByRole('alertdialog')).toBeNull();
    expect(calls.filter(call => call.method === 'versions.rollback')).toEqual([]);
  });

  it('sends the exact server and snapshot identities with explicit confirmation', () => {
    const { server, target } = renderVersions();

    fireEvent.click(screen.getByRole('button', { name: 'Roll back' }));
    fireEvent.click(within(screen.getByRole('alertdialog')).getByRole('button', {
      name: 'Roll back server version'
    }));

    expect(calls).toContainEqual({
      method: 'versions.rollback',
      params: { serverId: server.id, versionId: target.id, confirmed: true }
    });
    expect(screen.queryByRole('alertdialog')).toBeNull();
  });
});

describe('pending update validation', () => {
  it('withdraws confirmation when the pending version changes instead of approving a different update', () => {
    renderPendingValidation();
    fireEvent.click(screen.getByRole('button', { name: 'Mark update healthy' }));
    expect(screen.getByRole('alertdialog')).toBeTruthy();
    const changed = structuredClone(useAppStore.getState().snapshot!);
    changed.update!.pendingValidation = { ...changed.update!.pendingValidation!, versionId: 'different-version', versionName: 'Different release' };
    act(() => useAppStore.setState({ snapshot: changed }));
    expect(screen.queryByRole('alertdialog')).toBeNull();
    expect(calls.some(call => call.method === 'versions.markHealthy')).toBe(false);
  });

  it('shows no healthy action without an exact authoritative pending-validation identity', () => {
    renderVersions();
    expect(screen.queryByRole('button', { name: 'Mark update healthy' })).toBeNull();

    cleanup();
    const current = structuredClone(fixtures.running);
    current.update!.pendingValidation = {
      serverId: 'c81f398a-e3f3-4566-bebf-64efcf6238c6',
      versionId: '6b065d8b-64b7-48b3-a990-128211611436',
      versionName: 'Stale other-server update'
    };
    useAppStore.setState({ snapshot: current, bridge, busy: new Set(), error: null });
    render(<NavigationGuardProvider><ServerWorkspace serverId={current.servers[0].id} /></NavigationGuardProvider>);
    expect(screen.queryByRole('button', { name: 'Mark update healthy' })).toBeNull();
  });

  it('requires confirmation and sends the exact pending server and version identities', () => {
    const { server, active } = renderPendingValidation();

    fireEvent.click(screen.getByRole('button', { name: 'Mark update healthy' }));
    const confirmation = screen.getByRole('alertdialog', { name: `Mark ${active.version} healthy?` });
    expect(within(confirmation).getByText(/keep the previous rollback snapshot for 30 days/i)).toBeTruthy();
    expect(calls.some(call => call.method === 'versions.markHealthy')).toBe(false);

    fireEvent.click(within(confirmation).getByRole('button', { name: 'Mark update healthy' }));
    expect(calls).toContainEqual({
      method: 'versions.markHealthy',
      params: { serverId: server.id, versionId: active.id, confirmed: true }
    });
  });

  it('cancels an in-flight validation request when its server workspace closes', async () => {
    let requestSignal: AbortSignal | undefined;
    const cancellableBridge: BridgeAdapter = {
      ...bridge,
      request: async <T,>(method: BridgeMethod, params: Record<string, unknown> = {}, signal?: AbortSignal) => {
        if (method !== 'versions.markHealthy') return bridge.request<T>(method, params, signal);
        calls.push({ method, params });
        requestSignal = signal;
        return new Promise<T>((_resolve, reject) => {
          signal?.addEventListener('abort', () => reject(new Error('cancelled')), { once: true });
        });
      }
    };
    const current = structuredClone(fixtures.running);
    const server = current.servers[0];
    const active = current.versions.find(version => version.active)!;
    current.update!.pendingValidation = {
      serverId: server.id,
      versionId: active.id,
      versionName: active.version
    };
    useAppStore.setState({ snapshot: current, bridge: cancellableBridge, busy: new Set(), error: null });
    const rendered = render(<NavigationGuardProvider><ServerWorkspace serverId={server.id} /></NavigationGuardProvider>);

    fireEvent.click(screen.getByRole('button', { name: 'Mark update healthy' }));
    fireEvent.click(within(screen.getByRole('alertdialog')).getByRole('button', { name: 'Mark update healthy' }));
    await waitFor(() => expect(requestSignal).toBeDefined());

    rendered.unmount();
    expect(requestSignal?.aborted).toBe(true);
  });
});
