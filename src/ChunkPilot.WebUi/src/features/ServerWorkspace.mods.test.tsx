// @vitest-environment jsdom
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { BridgeAdapter } from '../bridge/client';
import type { BridgeMethod, ManagedContentOperation, ManagedContentOperationStage, PluginInstallPlan, PluginProject, PluginRelease } from '../bridge/types';
import { NavigationGuardProvider } from '../app/NavigationGuard';
import { FixtureBridge, fixtures } from '../fixtures/catalog';
import { useAppStore } from '../state/store';
import { ServerWorkspace } from './ServerWorkspace';

const calls: { method: BridgeMethod; params: Record<string, unknown> }[] = [];

beforeEach(() => {
  calls.length = 0;
  window.history.replaceState({}, '', '/?fixture=fabric&page=servers&tab=content');
  const fixture = new FixtureBridge('fabric');
  const bridge: BridgeAdapter = {
    request: async <T,>(method: BridgeMethod, params: Record<string, unknown> = {}) => {
      calls.push({ method, params });
      return fixture.request<T>(method, params);
    },
    subscribe: listener => fixture.subscribe(listener),
    dispose: () => fixture.dispose()
  };
  useAppStore.setState({ snapshot: structuredClone(fixtures.fabric), bridge, busy: new Set(),
    pendingOperations: new Map(), completedOperations: new Set(), error: null });
});
afterEach(cleanup);

function contentOperation(
  operationId: string,
  stage: ManagedContentOperationStage,
  overrides: Partial<ManagedContentOperation> = {}
): ManagedContentOperation {
  const terminal = ['Installed', 'Loaded', 'Failed', 'Cancelled'].includes(stage);
  return {
    operationId,
    serverId: fixtures.fabric.servers[0].id,
    kind: 'InstallAddon',
    provider: 'Modrinth',
    projectId: 'lithium',
    versionId: 'lithium-exact',
    displayName: 'Lithium',
    progress: {
      stage,
      message: stage === 'Failed' ? 'The verified install failed safely.' : `${stage} Lithium.`,
      percent: stage === 'Downloading' ? 42 : terminal && stage !== 'Failed' && stage !== 'Cancelled' ? 100 : null,
      bytesTransferred: stage === 'Downloading' ? 420 : null,
      totalBytes: stage === 'Downloading' ? 1_000 : null
    },
    isTerminal: terminal,
    success: terminal ? stage === 'Installed' || stage === 'Loaded' : null,
    isCancellable: !terminal,
    error: stage === 'Failed' ? 'The verified install failed safely.' : stage === 'Cancelled' ? 'The operation was cancelled.' : null,
    startedAtUtc: '2026-08-19T20:00:00Z',
    updatedAtUtc: '2026-08-19T20:00:00Z',
    ...overrides
  };
}

function installContentHarness(initialOperations: ManagedContentOperation[] = []) {
  const fixture = new FixtureBridge('fabric');
  let operations = initialOperations;
  const snapshot = structuredClone(fixtures.fabric);
  snapshot.plugins = [];
  const bridge: BridgeAdapter = {
    request: async <T,>(method: BridgeMethod, params: Record<string, unknown> = {}) => {
      calls.push({ method, params });
      if (method === 'content.operations') return structuredClone(operations) as T;
      if (method === 'mods.install') {
        const accepted = contentOperation(String(params.operationId), 'Queued');
        operations = [accepted, ...operations.filter(item => item.operationId !== accepted.operationId)];
        return structuredClone(accepted) as T;
      }
      if (method === 'content.cancel') {
        operations = operations.map(item => item.operationId === params.operationId
          ? contentOperation(item.operationId, 'Cancelled') : item);
        return { success: true, message: 'Cancellation was requested.' } as T;
      }
      return fixture.request<T>(method, params);
    },
    subscribe: listener => fixture.subscribe(listener),
    dispose: () => fixture.dispose()
  };
  useAppStore.setState({ snapshot, bridge, busy: new Set(), pendingOperations: new Map(), completedOperations: new Set(), error: null });
  return { setOperations: (next: ManagedContentOperation[]) => { operations = next; } };
}

async function selectLithium() {
  fireEvent.click(screen.getByRole('button', { name: 'Browse' }));
  fireEvent.change(screen.getByRole('searchbox', { name: 'Search official Modrinth mods' }),
    { target: { value: 'lithium' } });
  fireEvent.click(screen.getByRole('button', { name: 'Search' }));
  fireEvent.click(await screen.findByRole('button', { name: /Lithium/ }));
  await screen.findByText(/Optional for friends/);
}

async function beginLithiumInstall() {
  fireEvent.click(screen.getByRole('button', { name: 'Install and restart' }));
  fireEvent.click(screen.getByRole('button', { name: 'Apply and restart' }));
  await waitFor(() => expect(calls.filter(call => call.method === 'mods.install')).toHaveLength(1));
}

describe('Fabric mod management', () => {
  it('uses capability-driven Mods navigation and exact Modrinth compatibility filters', async () => {
    const server = useAppStore.getState().snapshot!.servers[0];
    render(<NavigationGuardProvider><ServerWorkspace serverId={server.id} /></NavigationGuardProvider>);

    expect(screen.getByRole('heading', { name: 'Mods' })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Browse' }));
    fireEvent.change(screen.getByRole('searchbox', { name: 'Search official Modrinth mods' }),
      { target: { value: 'lithium' } });
    fireEvent.click(screen.getByRole('button', { name: 'Search' }));
    fireEvent.click(await screen.findByRole('button', { name: /Lithium/ }));

    expect(await screen.findByText(/Optional for friends/)).toBeTruthy();
    expect(calls).toContainEqual({
      method: 'mods.search', params: { serverId: server.id, search: 'lithium', limit: 20, provider: 'Modrinth' }
    });
    expect(calls).toContainEqual({
      method: 'mods.release', params: { serverId: server.id, projectId: 'lithium', provider: 'Modrinth' }
    });
  });

  it('does not expose Plugins for a Fabric capability profile', () => {
    const server = useAppStore.getState().snapshot!.servers[0];
    render(<NavigationGuardProvider><ServerWorkspace serverId={server.id} /></NavigationGuardProvider>);

    expect(screen.getByRole('button', { name: 'Mods' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Plugins' })).toBeNull();
  });

  it('reconciles accepted, progressing, installed, and loaded states for one exact Modrinth release', async () => {
    const harness = installContentHarness();
    vi.spyOn(globalThis.crypto, 'randomUUID').mockReturnValue('11111111-1111-4111-8111-111111111111');
    const server = useAppStore.getState().snapshot!.servers[0];
    render(<NavigationGuardProvider><ServerWorkspace serverId={server.id} /></NavigationGuardProvider>);
    await selectLithium();
    await beginLithiumInstall();

    expect(screen.getByRole('status').textContent).toContain('Queued');
    expect(calls.find(call => call.method === 'mods.install')?.params.operationId)
      .toBe('11111111-1111-4111-8111-111111111111');

    harness.setOperations([contentOperation('11111111-1111-4111-8111-111111111111', 'Downloading')]);
    await waitFor(() => expect(screen.getByRole('status').textContent).toContain('42%'), { timeout: 1_500 });

    harness.setOperations([contentOperation('11111111-1111-4111-8111-111111111111', 'Installed')]);
    await waitFor(() => expect((screen.getByRole('button', { name: 'Installed' }) as HTMLButtonElement).disabled).toBe(true), { timeout: 1_500 });

    const loadedSnapshot = structuredClone(fixtures.fabric);
    loadedSnapshot.revision = 2;
    act(() => useAppStore.getState().applySnapshot(loadedSnapshot));
    expect((screen.getByRole('button', { name: 'Loaded' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('cancels an accepted operation, retries with a new identity, and surfaces a terminal failure', async () => {
    const harness = installContentHarness();
    vi.spyOn(globalThis.crypto, 'randomUUID')
      .mockReturnValueOnce('22222222-2222-4222-8222-222222222222')
      .mockReturnValueOnce('33333333-3333-4333-8333-333333333333');
    const server = useAppStore.getState().snapshot!.servers[0];
    render(<NavigationGuardProvider><ServerWorkspace serverId={server.id} /></NavigationGuardProvider>);
    await selectLithium();
    await beginLithiumInstall();

    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    await waitFor(() => expect(calls).toContainEqual({
      method: 'content.cancel', params: { operationId: '22222222-2222-4222-8222-222222222222' }
    }));
    const retry = await screen.findByRole('button', { name: 'Retry verified release' }, { timeout: 1_500 });
    fireEvent.click(retry);
    fireEvent.click(screen.getByRole('button', { name: 'Apply and restart' }));
    await waitFor(() => expect(calls.filter(call => call.method === 'mods.install')).toHaveLength(2));
    expect(calls.filter(call => call.method === 'mods.install')[1].params.operationId)
      .toBe('33333333-3333-4333-8333-333333333333');

    harness.setOperations([contentOperation('33333333-3333-4333-8333-333333333333', 'Failed')]);
    await waitFor(() => expect(screen.getByRole('status').textContent)
      .toContain('The verified install failed safely.'), { timeout: 1_500 });
    expect((screen.getByRole('button', { name: 'Retry verified release' }) as HTMLButtonElement).disabled).toBe(false);
  });

  it('restores an active operation after the content route remounts and stops the old poller', async () => {
    installContentHarness([contentOperation('44444444-4444-4444-8444-444444444444', 'Downloading')]);
    const server = useAppStore.getState().snapshot!.servers[0];
    const first = render(<NavigationGuardProvider><ServerWorkspace serverId={server.id} /></NavigationGuardProvider>);
    await selectLithium();
    expect(screen.getByRole('status').textContent).toContain('Downloading');

    first.unmount();
    const callsAfterUnmount = calls.filter(call => call.method === 'content.operations').length;
    await new Promise(resolve => window.setTimeout(resolve, 650));
    expect(calls.filter(call => call.method === 'content.operations')).toHaveLength(callsAfterUnmount);

    render(<NavigationGuardProvider><ServerWorkspace serverId={server.id} /></NavigationGuardProvider>);
    await selectLithium();
    expect(screen.getByRole('status').textContent).toContain('Downloading');
    expect(calls.filter(call => call.method === 'mods.install')).toHaveLength(0);
    expect(calls.filter(call => call.method === 'content.operations').length).toBeGreaterThan(callsAfterUnmount);
  });

  it('cancels and fences delayed catalog search, release, and plan state when the selected server changes', async () => {
    const fixture = new FixtureBridge('fabric');
    const current = structuredClone(fixtures.fabric);
    const alpha = current.servers[0];
    alpha.name = 'Alpha Works';
    const beta = { ...structuredClone(alpha), id: 'c4b211ec-8c34-4c93-88a2-4de6930822fd', name: 'Beta Works' };
    current.servers = [alpha, beta];
    current.selectedServerId = alpha.id;
    current.workspace = { serverId: alpha.id, state: 'Ready' };
    current.plugins = [];

    const project = (serverName: string): PluginProject => ({
      provider: 'Modrinth', kind: 'Mod', projectId: `${serverName.toLowerCase()}-project`, slug: `${serverName.toLowerCase()}-project`,
      name: `${serverName} Catalog Mod`, author: 'Fixture Author', summary: `${serverName} scoped result.`, downloads: 42,
      updatedAt: '2026-08-29T12:00:00Z', serverSide: 'required', clientSide: 'optional', clientRequirement: 'ClientOptional'
    });
    const release = (serverName: string): PluginRelease => ({
      provider: 'Modrinth', kind: 'Mod', projectId: `${serverName.toLowerCase()}-project`, versionId: `${serverName.toLowerCase()}-release`,
      versionName: '1.0.0', minecraftVersion: '26.2', loader: 'fabric', releaseChannel: 'release', publishedAt: '2026-08-29T12:00:00Z',
      fileName: `${serverName.toLowerCase()}.jar`, sizeBytes: 1_024, integrity: 'sha512', serverSide: 'required', clientSide: 'optional',
      clientRequirement: 'ClientOptional', dependencies: [{ projectId: 'fixture-library', versionId: 'fixture-library-1', fileName: 'fixture-library.jar', type: 'required' }]
    });
    let alphaSearchCount = 0;
    let alphaSearchSignal: AbortSignal | undefined;
    let betaReleaseSignal: AbortSignal | undefined;
    let alphaPlanSignal: AbortSignal | undefined;
    let resolveAlphaSearch!: (value: PluginProject[]) => void;
    let resolveBetaRelease!: (value: PluginRelease) => void;
    let resolveAlphaPlan!: (value: PluginInstallPlan) => void;
    const delayedBridge: BridgeAdapter = {
      request: async <T,>(method: BridgeMethod, params: Record<string, unknown> = {}, signal?: AbortSignal) => {
        calls.push({ method, params });
        if (method === 'mods.search' && params.serverId === alpha.id && alphaSearchCount++ === 0) {
          alphaSearchSignal = signal;
          return new Promise<T>(resolvePromise => { resolveAlphaSearch = value => resolvePromise(value as T); });
        }
        if (method === 'mods.search' && params.serverId === alpha.id) return [project('Alpha')] as T;
        if (method === 'mods.search' && params.serverId === beta.id) return [project('Beta')] as T;
        if (method === 'mods.release' && params.serverId === beta.id) {
          betaReleaseSignal = signal;
          return new Promise<T>(resolvePromise => { resolveBetaRelease = value => resolvePromise(value as T); });
        }
        if (method === 'mods.release' && params.serverId === alpha.id) return release('Alpha') as T;
        if (method === 'mods.plan' && params.serverId === alpha.id) {
          alphaPlanSignal = signal;
          return new Promise<T>(resolvePromise => { resolveAlphaPlan = value => resolvePromise(value as T); });
        }
        return fixture.request<T>(method, params);
      },
      subscribe: listener => fixture.subscribe(listener),
      dispose: () => fixture.dispose()
    };
    useAppStore.setState({ snapshot: current, bridge: delayedBridge, busy: new Set(), pendingOperations: new Map(), completedOperations: new Set(), error: null });
    const view = render(<NavigationGuardProvider><ServerWorkspace serverId={alpha.id} /></NavigationGuardProvider>);
    const switchTo = (serverId: string) => {
      const next = structuredClone(useAppStore.getState().snapshot!);
      next.selectedServerId = serverId;
      next.workspace = { serverId, state: 'Ready' };
      next.plugins = [];
      act(() => useAppStore.setState({ snapshot: next }));
      view.rerender(<NavigationGuardProvider><ServerWorkspace serverId={serverId} /></NavigationGuardProvider>);
    };

    fireEvent.click(screen.getByRole('button', { name: 'Browse' }));
    fireEvent.change(screen.getByRole('searchbox', { name: 'Search official Modrinth mods' }), { target: { value: 'alpha' } });
    fireEvent.click(screen.getByRole('button', { name: 'Search' }));
    await waitFor(() => expect(alphaSearchSignal).toBeTruthy());
    switchTo(beta.id);
    expect(alphaSearchSignal!.aborted).toBe(true);
    await act(async () => { resolveAlphaSearch([project('Alpha')]); await Promise.resolve(); });
    expect(screen.queryByRole('button', { name: /Alpha Catalog Mod/ })).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Browse' }));
    fireEvent.change(screen.getByRole('searchbox', { name: 'Search official Modrinth mods' }), { target: { value: 'beta' } });
    fireEvent.click(screen.getByRole('button', { name: 'Search' }));
    fireEvent.click(await screen.findByRole('button', { name: /Beta Catalog Mod/ }));
    await waitFor(() => expect(betaReleaseSignal).toBeTruthy());
    switchTo(alpha.id);
    expect(betaReleaseSignal!.aborted).toBe(true);
    await act(async () => { resolveBetaRelease(release('Beta')); await Promise.resolve(); });
    expect(calls.some(call => call.method === 'mods.plan' && call.params.serverId === beta.id)).toBe(false);
    expect(screen.queryByText('beta.jar · 1,024 B')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Browse' }));
    fireEvent.change(screen.getByRole('searchbox', { name: 'Search official Modrinth mods' }), { target: { value: 'alpha' } });
    fireEvent.click(screen.getByRole('button', { name: 'Search' }));
    fireEvent.click(await screen.findByRole('button', { name: /Alpha Catalog Mod/ }));
    await waitFor(() => expect(alphaPlanSignal).toBeTruthy());
    expect(await screen.findByText('alpha.jar · 1,024 B')).toBeTruthy();
    switchTo(beta.id);
    expect(alphaPlanSignal!.aborted).toBe(true);
    await act(async () => { resolveAlphaPlan({ releases: [release('Alpha')], problems: [], canInstall: true }); await Promise.resolve(); });
    expect(screen.queryByText('alpha.jar · 1,024 B')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Install complete verified plan' })).toBeNull();
  });
});
