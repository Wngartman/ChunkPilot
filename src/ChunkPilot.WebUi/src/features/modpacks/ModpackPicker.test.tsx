// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import type { BridgeAdapter } from '../../bridge/client';
import type { BridgeMethod, ModpackProject } from '../../bridge/types';
import { FixtureBridge, fixtures } from '../../fixtures/catalog';
import { useAppStore } from '../../state/store';
import { ModpackPicker, type ModpackSelection } from './ModpackPicker';

const calls: { method: BridgeMethod; params: Record<string, unknown> }[] = [];

beforeEach(() => {
  calls.length = 0;
  const fixture = new FixtureBridge('running');
  const bridge: BridgeAdapter = {
    request: async <T,>(method: BridgeMethod, params: Record<string, unknown> = {}) => {
      calls.push({ method, params });
      return fixture.request<T>(method, params);
    },
    subscribe: listener => fixture.subscribe(listener),
    dispose: () => fixture.dispose()
  };
  useAppStore.setState({ snapshot: structuredClone(fixtures.running), bridge, busy: new Set(), pendingOperations: new Map(), completedOperations: new Set(), error: null });
});
afterEach(cleanup);

describe('modpack provider browser', () => {
  it('loads immediately, searches on explicit submit, and selects one exact createable release', async () => {
    let selected: ModpackSelection | null = null;
    const { rerender } = render(<ModpackPicker value={selected} onChange={value => { selected = value; rerender(<ModpackPicker value={selected} onChange={next => { selected = next; }} />); }} />);

    expect(await screen.findByRole('button', { name: /Copper Trails/ })).toBeTruthy();
    fireEvent.change(screen.getByRole('searchbox', { name: 'Search Modrinth modpacks' }), { target: { value: 'copper' } });
    fireEvent.click(screen.getByRole('combobox', { name: 'Minecraft version filter' }));
    fireEvent.click(await screen.findByRole('option', { name: '1.20.1' }));
    fireEvent.click(screen.getByRole('combobox', { name: 'Loader filter' }));
    fireEvent.click(await screen.findByRole('option', { name: 'Forge' }));
    fireEvent.click(screen.getByRole('button', { name: 'Search' }));

    await waitFor(() => expect(calls.some(call => call.method === 'modpacks.search' && call.params.search === 'copper' && call.params.minecraftVersion === '1.20.1' && call.params.loader === 'forge' && call.params.sort === 'Downloads')).toBe(true));
    fireEvent.click(await screen.findByRole('button', { name: /Copper Trails/ }));
    const chosen = selected as unknown as Extract<ModpackSelection, { kind: 'remote' }>;
    expect(chosen.kind).toBe('remote');
    expect(chosen.release.versionId).toBe('fixture-pack-4');
    expect(screen.getByText('SHA-1 + SHA-512')).toBeTruthy();
  });

  it('keeps provider images behind the native bounded image bridge', async () => {
    const imageProject: ModpackProject = {
      provider: 'Modrinth', projectId: 'image-pack', slug: 'image-pack', name: 'Image Pack', author: 'Fixture', summary: 'Image bridge fixture.', downloadCount: 20, updatedAt: null, categories: [], hasImage: true, serverSupport: 'FullyAutomated', clientRequirement: 'Optional', trend: { available: false, detail: 'Collecting trend history.' }, versions: [{ versionId: 'image-release', versionName: '1.0', minecraftVersion: '1.21.8', loader: 'fabric', releaseChannel: 'Stable', publishedAt: null, sizeBytes: 1_000, changelog: '', requiredJavaMajor: 21, hasIntegrity: true, canCreate: true }]
    };
    const bridge = useAppStore.getState().bridge!;
    useAppStore.setState({ bridge: { ...bridge, request: async <T,>(method: BridgeMethod, params: Record<string, unknown> = {}) => {
      calls.push({ method, params });
      if (method === 'modpacks.cache' || method === 'modpacks.search') return { provider: 'Modrinth', state: 'Ready', items: [imageProject], detail: 'Ready.', failedStage: '', retrievedAt: null, fromCache: method === 'modpacks.cache', stale: false } as T;
      if (method === 'modpacks.image') return { dataUrl: 'data:image/png;base64,AA==' } as T;
      return bridge.request<T>(method, params);
    } } });
    const { container } = render(<ModpackPicker value={null} onChange={() => undefined} />);
    expect(await screen.findByRole('button', { name: /Image Pack/ })).toBeTruthy();
    await waitFor(() => expect(calls).toContainEqual({ method: 'modpacks.image', params: { provider: 'Modrinth', projectId: 'image-pack' } }));
    expect(container.querySelector('img')?.getAttribute('src')).toBe('data:image/png;base64,AA==');
  });

  it('shows a connected-settings action instead of an empty CurseForge catalog', async () => {
    let opened = false;
    render(<ModpackPicker value={null} onChange={() => undefined} onOpenProviderSettings={() => { opened = true; }} />);
    fireEvent.click(await screen.findByRole('tab', { name: /CurseForge/ }));
    expect(await screen.findByText('Connect CurseForge')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Open Content sources' }));
    expect(opened).toBe(true);
    expect(screen.queryByText('No matching server pack')).toBeNull();
  });

  it('cancels a superseded provider request and ignores its late response', async () => {
    const firstProject = catalogProject('old-pack', 'Old result');
    const secondProject = catalogProject('new-pack', 'New result');
    let resolveFirst!: (value: unknown) => void;
    let firstSignal: AbortSignal | undefined;
    const firstResult = new Promise(resolve => { resolveFirst = resolve; });
    const fixture = new FixtureBridge('running');
    const bridge: BridgeAdapter = {
      request: async <T,>(method: BridgeMethod, params: Record<string, unknown> = {}, signal?: AbortSignal) => {
        if (method === 'modpacks.cache') return { provider: 'Modrinth', state: 'Empty', items: [], detail: '', failedStage: '', retrievedAt: null, fromCache: true, stale: false } as T;
        if (method === 'modpacks.search' && !params.search) {
          firstSignal = signal;
          return firstResult as Promise<T>;
        }
        if (method === 'modpacks.search') return { provider: 'Modrinth', state: 'Ready', items: [secondProject], detail: 'Ready.', failedStage: '', retrievedAt: null, fromCache: false, stale: false } as T;
        return fixture.request<T>(method, params);
      },
      subscribe: listener => fixture.subscribe(listener),
      dispose: () => fixture.dispose()
    };
    useAppStore.setState({ bridge });
    render(<ModpackPicker value={null} onChange={() => undefined} />);
    await waitFor(() => expect(firstSignal).toBeTruthy());

    fireEvent.change(screen.getByRole('searchbox', { name: 'Search Modrinth modpacks' }), { target: { value: 'new' } });
    fireEvent.click(screen.getByRole('button', { name: 'Search' }));
    expect(await screen.findByRole('button', { name: /New result/ })).toBeTruthy();
    expect(firstSignal?.aborted).toBe(true);

    resolveFirst({ provider: 'Modrinth', state: 'Ready', items: [firstProject], detail: 'Late.', failedStage: '', retrievedAt: null, fromCache: false, stale: false });
    await Promise.resolve();
    expect(screen.queryByRole('button', { name: /Old result/ })).toBeNull();
    expect(screen.getByRole('button', { name: /New result/ })).toBeTruthy();
  });
});

function catalogProject(projectId: string, name: string): ModpackProject {
  return {
    provider: 'Modrinth', projectId, slug: projectId, name, author: 'Fixture', summary: `${name} summary`,
    downloadCount: 1, updatedAt: null, categories: [], hasImage: false, serverSupport: 'FullyAutomated',
    clientRequirement: 'Required', trend: { available: false, detail: '' }, versions: [{
      versionId: `${projectId}-version`, versionName: '1.0', minecraftVersion: '1.21.8', loader: 'fabric',
      releaseChannel: 'Stable', publishedAt: null, sizeBytes: 1_024, changelog: '', requiredJavaMajor: 21,
      hasIntegrity: true, canCreate: true
    }]
  };
}
