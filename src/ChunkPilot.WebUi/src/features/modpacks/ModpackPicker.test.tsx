// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import type { BridgeAdapter } from '../../bridge/client';
import type { BridgeMethod, ModpackCatalogResult, ModpackProject } from '../../bridge/types';
import { FixtureBridge, fixtures } from '../../fixtures/catalog';
import { useAppStore } from '../../state/store';
import { ModpackPicker, PackImage, type ModpackSelection } from './ModpackPicker';

const calls: { method: BridgeMethod; params: Record<string, unknown> }[] = [];

beforeEach(() => {
  calls.length = 0;
  window.history.replaceState({}, '', '/');
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
  it('uses roving tab focus and the standard provider-tab keyboard commands', async () => {
    render(<ModpackPicker value={null} onChange={() => undefined} />);
    const modrinth = await screen.findByRole('tab', { name: /Modrinth/ });
    const curseForge = screen.getByRole('tab', { name: /CurseForge/ });

    expect(modrinth.tabIndex).toBe(0);
    expect(curseForge.tabIndex).toBe(-1);
    modrinth.focus();

    fireEvent.keyDown(modrinth, { key: 'ArrowLeft' });
    await waitFor(() => expect(curseForge.getAttribute('aria-selected')).toBe('true'));
    expect(document.activeElement).toBe(curseForge);
    expect(curseForge.tabIndex).toBe(0);
    expect(modrinth.tabIndex).toBe(-1);
    expect(screen.getByRole('tabpanel').getAttribute('aria-labelledby')).toBe(curseForge.id);

    fireEvent.keyDown(curseForge, { key: 'Home' });
    await waitFor(() => expect(modrinth.getAttribute('aria-selected')).toBe('true'));
    expect(document.activeElement).toBe(modrinth);

    fireEvent.keyDown(modrinth, { key: 'End' });
    await waitFor(() => expect(curseForge.getAttribute('aria-selected')).toBe('true'));
    expect(document.activeElement).toBe(curseForge);

    fireEvent.keyDown(curseForge, { key: 'ArrowRight' });
    await waitFor(() => expect(modrinth.getAttribute('aria-selected')).toBe('true'));
    expect(document.activeElement).toBe(modrinth);
  });

  it('preselects a provider only inside an explicit fixture origin', async () => {
    window.history.replaceState({}, '', '/?fixture=curseforge&provider=CurseForge');
    const fixture = new FixtureBridge('curseforge');
    useAppStore.setState({ bridge: fixture });
    const { unmount } = render(<ModpackPicker value={null} onChange={() => undefined} />);

    expect((await screen.findByRole('tab', { name: /CurseForge/ })).getAttribute('aria-selected')).toBe('true');
    expect(await screen.findByRole('searchbox', { name: 'Search CurseForge modpacks' })).toBeTruthy();
    expect(await screen.findByRole('button', { name: /Copper Trails/ })).toBeTruthy();

    unmount();
    window.history.replaceState({}, '', '/?provider=CurseForge');
    useAppStore.setState({ bridge: new FixtureBridge('curseforge') });
    render(<ModpackPicker value={null} onChange={() => undefined} />);
    expect((await screen.findByRole('tab', { name: /Modrinth/ })).getAttribute('aria-selected')).toBe('true');
    expect(screen.getByRole('searchbox', { name: 'Search Modrinth modpacks' })).toBeTruthy();
  });

  it('paginates the deterministic 150-pack fixture and exposes every server-path state', async () => {
    window.history.replaceState({}, '', '/?fixture=curseforge-many&provider=CurseForge');
    const fixture = new FixtureBridge('curseforge-many');
    const firstPage = await fixture.request<ModpackCatalogResult>('modpacks.search',
      { provider: 'CurseForge', limit: 50 });
    expect(firstPage.items.map(project => project.serverSupport)).toEqual(expect.arrayContaining([
      'FullyAutomated', 'AutomatedWithReview', 'Unsupported'
    ]));
    expect(firstPage.items.map(project => project.versions[0]?.serverPath)).toEqual(expect.arrayContaining([
      'Official server pack',
      'ChunkPilot can generate and validate a server candidate',
      'No supportable server setup found'
    ]));
    useAppStore.setState({ bridge: fixture });
    render(<ModpackPicker value={null} onChange={() => undefined} />);

    expect(await screen.findByText('Showing 50 of 150 packs')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Load more' }));
    expect(await screen.findByText('Showing 100 of 150 packs')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Load more' }));
    expect(await screen.findByText('Showing 150 of 150 packs')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Load more' })).toBeNull();
  });

  it('preselects deterministic unavailable and rate-limited CurseForge states', async () => {
    window.history.replaceState({}, '', '/?fixture=curseforge-unavailable&provider=CurseForge');
    useAppStore.setState({ bridge: new FixtureBridge('curseforge-unavailable') });
    const { unmount } = render(<ModpackPicker value={null} onChange={() => undefined} />);
    expect(await screen.findByText('CurseForge unavailable')).toBeTruthy();

    unmount();
    window.history.replaceState({}, '', '/?fixture=curseforge-rate-limited&provider=CurseForge');
    useAppStore.setState({ bridge: new FixtureBridge('curseforge-rate-limited') });
    render(<ModpackPicker value={null} onChange={() => undefined} />);
    expect(await screen.findByText('CurseForge rate limit active')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Retry' })).toBeTruthy();
  });

  it('loads immediately, searches on explicit submit, and selects one exact createable release', async () => {
    let selected: ModpackSelection | null = null;
    const { rerender } = render(<ModpackPicker value={selected} onChange={value => { selected = value; rerender(<ModpackPicker value={selected} onChange={next => { selected = next; }} />); }} />);

    expect(await screen.findByRole('button', { name: /Copper Trails/ })).toBeTruthy();
    expect(calls.some(call => call.method === 'modpacks.search' && call.params.limit === 50)).toBe(true);
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

  it('continues to the live Modrinth catalog and inventory when optional cache reads fail', async () => {
    const fixture = new FixtureBridge('running');
    const liveProject = catalogProject('live-after-cache', 'Live after cache');
    const bridge: BridgeAdapter = {
      request: async <T,>(method: BridgeMethod, params: Record<string, unknown> = {}) => {
        calls.push({ method, params });
        if (method === 'modpacks.cache') throw new Error('Optional cache unavailable.');
        if (method === 'modpacks.versions' && params.cacheOnly === true) throw new Error('Optional inventory cache unavailable.');
        if (method === 'modpacks.versions') return {
          provider: 'Modrinth', state: 'Ready', versions: [
            { versionId: '1.20.1', kind: 'Release', publishedAt: null, isMajor: false }
          ], detail: 'Live inventory.', failedStage: '', retrievedAt: null, fromCache: false, stale: false
        } as T;
        if (method === 'modpacks.search') return {
          provider: 'Modrinth', state: 'Ready', items: [liveProject], detail: 'Live catalog.', failedStage: '',
          retrievedAt: null, fromCache: false, stale: false, nextIndex: 1, hasMore: false, totalCount: 1
        } as T;
        return fixture.request<T>(method, params);
      },
      subscribe: listener => fixture.subscribe(listener), dispose: () => fixture.dispose()
    };
    useAppStore.setState({ bridge });
    render(<ModpackPicker value={null} onChange={() => undefined} />);

    expect(await screen.findByRole('button', { name: /Live after cache/ })).toBeTruthy();
    await waitFor(() => expect(calls).toContainEqual({ method: 'modpacks.versions', params: { provider: 'Modrinth', cacheOnly: false } }));
    fireEvent.click(screen.getByRole('combobox', { name: 'Minecraft version filter' }));
    expect(await screen.findByRole('option', { name: '1.20.1' })).toBeTruthy();
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

  it('shares thumbnail work while mounted and cancels it only after the final subscriber leaves', async () => {
    const project: ModpackProject = {
      provider: 'Modrinth', projectId: 'shared-cancellable-image', slug: 'shared-cancellable-image',
      name: 'Shared image', author: 'Fixture', summary: 'Image request lifetime fixture.',
      downloadCount: 1, updatedAt: null, categories: [], hasImage: true,
      serverSupport: 'FullyAutomated', clientRequirement: 'Optional',
      trend: { available: false, detail: 'Collecting trend history.' }, versions: []
    };
    let imageCalls = 0;
    let imageSignal: AbortSignal | undefined;
    const fixture = new FixtureBridge('running');
    const bridge: BridgeAdapter = {
      request: async <T,>(method: BridgeMethod, params: Record<string, unknown> = {}, signal?: AbortSignal) => {
        calls.push({ method, params });
        if (method !== 'modpacks.image') return fixture.request<T>(method, params);
        imageCalls++;
        imageSignal = signal;
        return new Promise<T>((_resolve, reject) => {
          signal?.addEventListener('abort', () => reject(new DOMException('cancelled', 'AbortError')), { once: true });
        });
      },
      subscribe: listener => fixture.subscribe(listener),
      dispose: () => fixture.dispose()
    };
    useAppStore.setState({ bridge });

    const { rerender, unmount } = render(<><PackImage project={project} /><PackImage project={project} large /></>);
    await waitFor(() => expect(imageCalls).toBe(1));

    rerender(<PackImage project={project} />);
    expect(imageSignal?.aborted).toBe(false);

    unmount();
    await waitFor(() => expect(imageSignal?.aborted).toBe(true));
    expect(imageCalls).toBe(1);
  });

  it('never carries a previous project image into newly selected details', async () => {
    const first = { ...catalogProject('detail-image-first', 'First image pack'), hasImage: true };
    const second = { ...catalogProject('detail-image-second', 'Second imageless pack'), hasImage: false };
    const fixture = new FixtureBridge('running');
    const bridge: BridgeAdapter = {
      request: async <T,>(method: BridgeMethod, params: Record<string, unknown> = {}) => {
        if (method === 'modpacks.cache') return { provider: 'Modrinth', state: 'Empty', items: [], detail: '', failedStage: '', retrievedAt: null, fromCache: true, stale: false } as T;
        if (method === 'modpacks.search') return { provider: 'Modrinth', state: 'Ready', items: [first, second], detail: 'Ready.', failedStage: '', retrievedAt: null, fromCache: false, stale: false, nextIndex: 2, hasMore: false, totalCount: 2 } as T;
        if (method === 'modpacks.image') return { dataUrl: 'data:image/png;base64,FIRST' } as T;
        return fixture.request<T>(method, params);
      },
      subscribe: listener => fixture.subscribe(listener), dispose: () => fixture.dispose()
    };
    useAppStore.setState({ bridge });
    const { container } = render(<ModpackPicker value={null} onChange={() => undefined} />);
    fireEvent.click(await screen.findByRole('button', { name: /First image pack/ }));
    await waitFor(() => expect(container.querySelector('aside img')?.getAttribute('src')).toBe('data:image/png;base64,FIRST'));

    fireEvent.click(screen.getByRole('button', { name: /Second imageless pack/ }));

    expect(screen.getByRole('heading', { name: 'Second imageless pack' })).toBeTruthy();
    expect(container.querySelector('aside img')).toBeNull();
  });

  it('opens native CurseForge setup without renderer credential input and refreshes discovery', async () => {
    render(<ModpackPicker value={null} onChange={() => undefined} />);
    fireEvent.click(await screen.findByRole('tab', { name: /CurseForge/ }));
    expect(await screen.findByText('CurseForge unavailable')).toBeTruthy();
    expect(screen.getAllByText(/native setup window/).length).toBeGreaterThan(0);
    expect(screen.queryByText(/API key/i)).toBeNull();
    expect(screen.queryByText('No matching server pack')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Set up CurseForge' }));
    expect(await screen.findByRole('button', { name: /Copper Trails/ })).toBeTruthy();
    expect(calls).toContainEqual({ method: 'providers.configureCurseForge', params: {} });
    expect(screen.queryByText('CurseForge unavailable')).toBeNull();
  });

  it('resolves a pasted Modrinth project link in place and preserves the exact reviewed release', async () => {
    let selected: ModpackSelection | null = null;
    const { rerender } = render(<ModpackPicker initialMode="Link" value={selected} onChange={value => {
      selected = value;
      rerender(<ModpackPicker initialMode="Link" value={selected} onChange={next => { selected = next; }} />);
    }} />);
    const url = `${'https:'}//modrinth.com/modpack/fixture-pack/version/fixture-pack-4`;
    fireEvent.change(screen.getByRole('textbox', { name: 'Provider project link' }), { target: { value: url } });
    fireEvent.click(screen.getByRole('button', { name: 'Resolve' }));

    expect(await screen.findByText('Ready for review')).toBeTruthy();
    expect(calls).toContainEqual({ method: 'modpacks.resolveLink', params: { url } });
    const resolved = selected as unknown as Extract<ModpackSelection, { kind: 'remote' }>;
    expect(resolved.release.versionId).toBe('fixture-pack-4');
    expect(screen.getByText('Resolved the exact release from the provider link.')).toBeTruthy();
  });

  it('recognizes CurseForge links but exposes no user credential prompt', async () => {
    render(<ModpackPicker initialMode="Link" value={null} onChange={() => undefined} />);
    fireEvent.change(screen.getByRole('textbox', { name: 'Provider project link' }), {
      target: { value: `${'https:'}//www.curseforge.com/minecraft/modpacks/statech-industry-2` }
    });
    fireEvent.click(screen.getByRole('button', { name: 'Resolve' }));

    expect(await screen.findByText(/native CurseForge credential is missing/)).toBeTruthy();
    expect(screen.queryByText(/API key/i)).toBeNull();
  });

  it('reviews a native folder token and keeps copy versus by-reference explicit', async () => {
    const fixture = new FixtureBridge('running');
    useAppStore.setState({ bridge: {
      request: async <T,>(method: BridgeMethod, params: Record<string, unknown> = {}) => {
        calls.push({ method, params });
        if (method === 'modpacks.chooseLocal') return {
          cancelled: false, token: 'opaque-import-token', fileName: 'Existing server', expiresAt: '2026-08-21T00:05:00Z',
          inspection: { sourceKind: 'ServerFolder', name: 'Existing server', summary: 'Reviewed.', minecraftVersion: '1.20.1', loader: 'Forge', loaderVersion: '47.3.0', requiredJavaMajor: 17, requiredServerFiles: 42, optionalServerFiles: 0, excludedClientFiles: 0, indexedServerBytes: 2_000_000, sourceSizeBytes: 2_000_000, expandedSizeBytes: 2_000_000, fileCount: 42, modCount: 7, pluginCount: 0, containsWorld: true, serverRoot: '.', launchCandidates: ['run/server.jar'], canReference: true, canCreate: true, limitation: '' }
        } as T;
        return fixture.request<T>(method, params);
      },
      subscribe: listener => fixture.subscribe(listener), dispose: () => fixture.dispose()
    } });
    let selected: ModpackSelection | null = null;
    const { rerender } = render(<ModpackPicker initialMode="Import" value={selected} onChange={value => {
      selected = value;
      rerender(<ModpackPicker initialMode="Import" value={selected} onChange={next => { selected = next; }} />);
    }} />);
    fireEvent.click(screen.getByRole('button', { name: 'Import server folder' }));
    expect(await screen.findByText('42 files · 1.9 MB')).toBeTruthy();
    expect(screen.getByText('Present — preserved')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /By reference/ }));
    const local = selected as unknown as Extract<ModpackSelection, { kind: 'local' }>;
    expect(local.local.managementMode).toBe('ByReference');
    expect(local.local.launchRelativePath).toBe('run/server.jar');
    expect(calls).toContainEqual({ method: 'modpacks.chooseLocal', params: { kind: 'folder' } });
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

  it('never relabels stale rows while switching providers and restores the prior provider session without refetching', async () => {
    const fixture = new FixtureBridge('running');
    let resolveCurseForge!: (value: unknown) => void;
    const curseForgeResult = new Promise(resolve => { resolveCurseForge = resolve; });
    const bridge: BridgeAdapter = {
      request: async <T,>(method: BridgeMethod, params: Record<string, unknown> = {}) => {
        calls.push({ method, params });
        if (method === 'modpacks.cache') return { provider: 'Modrinth', state: 'Empty', items: [], detail: '', failedStage: '', retrievedAt: null, fromCache: true, stale: false, nextIndex: 0, hasMore: false } as T;
        if (method === 'modpacks.search' && params.provider === 'CurseForge') return curseForgeResult as Promise<T>;
        if (method === 'modpacks.search') return { provider: 'Modrinth', state: 'Ready', items: [catalogProject('modrinth-only', 'Modrinth only')], detail: 'Ready.', failedStage: '', retrievedAt: null, fromCache: false, stale: false, nextIndex: 1, hasMore: false, totalCount: 1 } as T;
        return fixture.request<T>(method, params);
      },
      subscribe: listener => fixture.subscribe(listener), dispose: () => fixture.dispose()
    };
    useAppStore.setState({ bridge });
    render(<ModpackPicker value={null} onChange={() => undefined} />);

    expect(await screen.findByRole('button', { name: /Modrinth only/ })).toBeTruthy();
    fireEvent.click(screen.getByRole('tab', { name: /CurseForge/ }));
    expect(screen.queryByRole('button', { name: /Modrinth only/ })).toBeNull();
    resolveCurseForge({ provider: 'CurseForge', state: 'Ready', items: [catalogProject('curseforge-only', 'CurseForge only', 'CurseForge')], detail: 'Ready.', failedStage: '', retrievedAt: null, fromCache: false, stale: false, nextIndex: 1, hasMore: false, totalCount: 1 });
    expect(await screen.findByRole('button', { name: /CurseForge only/ })).toBeTruthy();

    fireEvent.click(screen.getByRole('tab', { name: /Modrinth/ }));
    expect(screen.getByRole('button', { name: /Modrinth only/ })).toBeTruthy();
    expect(calls.filter(call => call.method === 'modpacks.search' && call.params.provider === 'Modrinth')).toHaveLength(1);
  });

  it('renders shallow cards before exact project calls and resolves server setup only after selection', async () => {
    const fixture = new FixtureBridge('running');
    const shallow: ModpackProject = {
      ...catalogProject('shallow-pack', 'Shallow pack', 'CurseForge'),
      serverPathChecked: false,
      serverSupport: 'Unsupported',
      versions: []
    };
    const resolved: ModpackProject = {
      ...catalogProject('shallow-pack', 'Shallow pack', 'CurseForge'),
      serverPathChecked: true,
      serverSupport: 'FullyAutomated'
    };
    const bridge: BridgeAdapter = {
      request: async <T,>(method: BridgeMethod, params: Record<string, unknown> = {}) => {
        calls.push({ method, params });
        if (method === 'modpacks.search' && params.provider === 'CurseForge') return { provider: 'CurseForge', state: 'Ready', items: [shallow], detail: 'Ready.', failedStage: '', retrievedAt: null, fromCache: false, stale: false, nextIndex: 1, hasMore: false, totalCount: 1 } as T;
        if (method === 'modpacks.project') return resolved as T;
        if (method === 'modpacks.cache') return { provider: 'Modrinth', state: 'Empty', items: [], detail: '', failedStage: '', retrievedAt: null, fromCache: true, stale: false, nextIndex: 0, hasMore: false } as T;
        if (method === 'modpacks.search') return { provider: 'Modrinth', state: 'Empty', items: [], detail: '', failedStage: '', retrievedAt: null, fromCache: false, stale: false, nextIndex: 0, hasMore: false } as T;
        return fixture.request<T>(method, params);
      },
      subscribe: listener => fixture.subscribe(listener), dispose: () => fixture.dispose()
    };
    useAppStore.setState({ bridge });
    render(<ModpackPicker value={null} onChange={() => undefined} />);
    fireEvent.click(await screen.findByRole('tab', { name: /CurseForge/ }));

    const card = await screen.findByRole('button', { name: /Shallow pack/ });
    expect(screen.getByText('Server setup not checked yet')).toBeTruthy();
    expect(calls.some(call => call.method === 'modpacks.project')).toBe(false);
    fireEvent.click(card);
    await waitFor(() => expect(calls).toContainEqual({ method: 'modpacks.project', params: { provider: 'CurseForge', projectId: 'shallow-pack' } }));
    expect(await screen.findByText('Official server pack')).toBeTruthy();
  });

  it('reflects the parent-owned CurseForge preflight result in the selected project details', async () => {
    const fixture = new FixtureBridge('running');
    const shallow: ModpackProject = {
      ...catalogProject('preflight-pack', 'Preflight pack', 'CurseForge'),
      serverPathChecked: false,
      versions: []
    };
    const requiredRelease = {
      ...catalogProject('preflight-pack', 'Preflight pack', 'CurseForge').versions[0],
      canCreate: false,
      preflightState: 'Required' as const,
      limitation: 'Inspecting the exact client manifest is required before this release can be created.'
    };
    const resolved: ModpackProject = {
      ...shallow,
      serverPathChecked: true,
      serverSupport: 'FullyAutomated',
      versions: [requiredRelease]
    };
    let selected: ModpackSelection | null = null;
    const bridge: BridgeAdapter = {
      request: async <T,>(method: BridgeMethod, params: Record<string, unknown> = {}) => {
        if (method === 'modpacks.search' && params.provider === 'CurseForge') return { provider: 'CurseForge', state: 'Ready', items: [shallow], detail: 'Ready.', failedStage: '', retrievedAt: null, fromCache: false, stale: false, nextIndex: 1, hasMore: false, totalCount: 1 } as T;
        if (method === 'modpacks.project') return resolved as T;
        if (method === 'modpacks.cache') return { provider: 'Modrinth', state: 'Empty', items: [], detail: '', failedStage: '', retrievedAt: null, fromCache: true, stale: false, nextIndex: 0, hasMore: false } as T;
        if (method === 'modpacks.search') return { provider: 'Modrinth', state: 'Empty', items: [], detail: '', failedStage: '', retrievedAt: null, fromCache: false, stale: false, nextIndex: 0, hasMore: false } as T;
        return fixture.request<T>(method, params);
      },
      subscribe: listener => fixture.subscribe(listener), dispose: () => fixture.dispose()
    };
    useAppStore.setState({ bridge });
    const { rerender } = render(<ModpackPicker value={null} onChange={value => { selected = value; }} />);
    fireEvent.click(await screen.findByRole('tab', { name: /CurseForge/ }));
    fireEvent.click(await screen.findByRole('button', { name: /Preflight pack/ }));
    await waitFor(() => expect(selected).not.toBeNull());

    const failedRelease = { ...requiredRelease, preflightState: 'Failed' as const, limitation: 'The verified client archive could not be downloaded.' };
    const failedSelection: ModpackSelection = {
      kind: 'remote',
      project: { ...resolved, versions: [failedRelease] },
      release: failedRelease
    };
    rerender(<ModpackPicker value={failedSelection} onChange={value => { selected = value; }} />);

    expect(await screen.findByText('The verified client archive could not be downloaded.')).toBeTruthy();
    expect(screen.queryByText(requiredRelease.limitation)).toBeNull();
  });

  it('clears the prior createable selection while a different shallow project is unresolved', async () => {
    const fixture = new FixtureBridge('running');
    const first = catalogProject('first-pack', 'First pack', 'CurseForge');
    const second: ModpackProject = {
      ...catalogProject('second-pack', 'Second pack', 'CurseForge'),
      serverPathChecked: false,
      serverSupport: 'Unsupported',
      versions: []
    };
    let resolveSecond!: (project: ModpackProject) => void;
    const secondDetail = new Promise<ModpackProject>(resolve => { resolveSecond = resolve; });
    const bridge: BridgeAdapter = {
      request: async <T,>(method: BridgeMethod, params: Record<string, unknown> = {}) => {
        if (method === 'modpacks.search' && params.provider === 'CurseForge') return {
          provider: 'CurseForge', state: 'Ready', items: [first, second], detail: 'Ready.', failedStage: '',
          retrievedAt: null, fromCache: false, stale: false, nextIndex: 2, hasMore: false, totalCount: 2
        } as T;
        if (method === 'modpacks.project' && params.projectId === 'second-pack') return secondDetail as Promise<T>;
        if (method === 'modpacks.cache') return { provider: 'Modrinth', state: 'Empty', items: [], detail: '', failedStage: '', retrievedAt: null, fromCache: true, stale: false, nextIndex: 0, hasMore: false } as T;
        if (method === 'modpacks.search') return { provider: 'Modrinth', state: 'Empty', items: [], detail: '', failedStage: '', retrievedAt: null, fromCache: false, stale: false, nextIndex: 0, hasMore: false } as T;
        return fixture.request<T>(method, params);
      },
      subscribe: listener => fixture.subscribe(listener), dispose: () => fixture.dispose()
    };
    useAppStore.setState({ bridge });
    let selected: ModpackSelection | null = null;
    render(<ModpackPicker value={null} onChange={value => { selected = value; }} />);
    fireEvent.click(await screen.findByRole('tab', { name: /CurseForge/ }));
    fireEvent.click(await screen.findByRole('button', { name: /First pack/ }));
    await waitFor(() => expect(selected).not.toBeNull());

    fireEvent.click(screen.getByRole('button', { name: /Second pack/ }));

    expect(selected).toBeNull();
    expect(screen.getByText('Checking exact releases and server setup…')).toBeTruthy();
    resolveSecond({ ...second, serverPathChecked: true, serverSupport: 'Unsupported', versions: [] });
    await waitFor(() => expect(screen.queryByText('Checking exact releases and server setup…')).toBeNull());
    expect(selected).toBeNull();
  });

  it('loads the next provider page with an exact index and keeps the first page', async () => {
    const fixture = new FixtureBridge('running');
    const first = Array.from({ length: 50 }, (_, index) => catalogProject(`pack-${index}`, `Pack ${index}`));
    const bridge: BridgeAdapter = {
      request: async <T,>(method: BridgeMethod, params: Record<string, unknown> = {}) => {
        calls.push({ method, params });
        if (method === 'modpacks.cache') return { provider: 'Modrinth', state: 'Empty', items: [], detail: '', failedStage: '', retrievedAt: null, fromCache: true, stale: false } as T;
        if (method === 'modpacks.search' && params.index === 50) return { provider: 'Modrinth', state: 'Ready', items: [catalogProject('pack-50', 'Pack 50')], detail: 'Ready.', failedStage: '', retrievedAt: null, fromCache: false, stale: false, nextIndex: 51, hasMore: false, totalCount: 51 } as T;
        if (method === 'modpacks.search') return { provider: 'Modrinth', state: 'Ready', items: first, detail: 'Ready.', failedStage: '', retrievedAt: null, fromCache: false, stale: false, nextIndex: 50, hasMore: true, totalCount: 51 } as T;
        return fixture.request<T>(method, params);
      },
      subscribe: listener => fixture.subscribe(listener),
      dispose: () => fixture.dispose()
    };
    useAppStore.setState({ bridge });
    render(<ModpackPicker value={null} onChange={() => undefined} />);

    fireEvent.click(await screen.findByRole('button', { name: 'Load more' }));

    await waitFor(() => expect(calls.some(call => call.method === 'modpacks.search' && call.params.index === 50)).toBe(true));
    expect(screen.getByText('Showing 51 of 51 packs')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Load more' })).toBeNull();
  });

  it('coalesces repeated near-end signals into one exact next-page request', async () => {
    const fixture = new FixtureBridge('running');
    const first = Array.from({ length: 50 }, (_, index) => catalogProject(`guard-pack-${index}`, `Guard Pack ${index}`));
    let resolveNext!: (value: unknown) => void;
    const nextPage = new Promise(resolve => { resolveNext = resolve; });
    const bridge: BridgeAdapter = {
      request: async <T,>(method: BridgeMethod, params: Record<string, unknown> = {}) => {
        calls.push({ method, params });
        if (method === 'modpacks.cache') return { provider: 'Modrinth', state: 'Empty', items: [], detail: '', failedStage: '', retrievedAt: null, fromCache: true, stale: false } as T;
        if (method === 'modpacks.search' && params.index === 50) return nextPage as Promise<T>;
        if (method === 'modpacks.search') return { provider: 'Modrinth', state: 'Ready', items: first, detail: 'Ready.', failedStage: '', retrievedAt: null, fromCache: false, stale: false, nextIndex: 50, hasMore: true, totalCount: 100 } as T;
        return fixture.request<T>(method, params);
      },
      subscribe: listener => fixture.subscribe(listener), dispose: () => fixture.dispose()
    };
    useAppStore.setState({ bridge });
    const { container } = render(<ModpackPicker value={null} onChange={() => undefined} />);
    await screen.findByText('Showing 50 of 100 packs');
    const resultList = container.querySelector('[aria-busy]') as HTMLDivElement;

    fireEvent.scroll(resultList);
    fireEvent.scroll(resultList);
    fireEvent.scroll(resultList);

    await waitFor(() => expect(calls.filter(call => call.method === 'modpacks.search' && call.params.index === 50)).toHaveLength(1));
    resolveNext({ provider: 'Modrinth', state: 'Ready', items: [catalogProject('guard-pack-50', 'Guard Pack 50')], detail: 'Ready.', failedStage: '', retrievedAt: null, fromCache: false, stale: false, nextIndex: 51, hasMore: false, totalCount: 51 });
    expect(await screen.findByText('Showing 51 of 51 packs')).toBeTruthy();
  });

  it('resets a deep result scroll before issuing a new query', async () => {
    const fixture = new FixtureBridge('running');
    const first = Array.from({ length: 50 }, (_, index) => catalogProject(`pack-${index}`, `Pack ${index}`));
    const bridge: BridgeAdapter = {
      request: async <T,>(method: BridgeMethod, params: Record<string, unknown> = {}) => {
        calls.push({ method, params });
        if (method === 'modpacks.cache') return { provider: 'Modrinth', state: 'Empty', items: [], detail: '', failedStage: '', retrievedAt: null, fromCache: true, stale: false } as T;
        if (method === 'modpacks.search' && params.search === 'technology') return { provider: 'Modrinth', state: 'Ready', items: first, detail: 'Ready.', failedStage: '', retrievedAt: null, fromCache: false, stale: false, nextIndex: 50, hasMore: true, totalCount: 500 } as T;
        if (method === 'modpacks.search') return { provider: 'Modrinth', state: 'Ready', items: first, detail: 'Ready.', failedStage: '', retrievedAt: null, fromCache: false, stale: false, nextIndex: 50, hasMore: true, totalCount: 500 } as T;
        return fixture.request<T>(method, params);
      },
      subscribe: listener => fixture.subscribe(listener), dispose: () => fixture.dispose()
    };
    useAppStore.setState({ bridge });
    const { container } = render(<ModpackPicker value={null} onChange={() => undefined} />);
    await screen.findByText('Showing 50 of 500 packs');
    const resultList = container.querySelector('[aria-busy]') as HTMLDivElement;
    resultList.scrollTop = 2_000;
    fireEvent.scroll(resultList);

    fireEvent.change(screen.getByRole('searchbox', { name: 'Search Modrinth modpacks' }), { target: { value: 'technology' } });
    fireEvent.click(screen.getByRole('button', { name: 'Search' }));

    await waitFor(() => expect(calls.some(call => call.method === 'modpacks.search' && call.params.search === 'technology')).toBe(true));
    expect(resultList.scrollTop).toBe(0);
    expect(calls.filter(call => call.method === 'modpacks.search' && call.params.search === 'technology')).toHaveLength(1);
  });

  it('keeps provider pagination available when policy filtering underfills a raw page', async () => {
    const fixture = new FixtureBridge('running');
    const bridge: BridgeAdapter = {
      request: async <T,>(method: BridgeMethod, params: Record<string, unknown> = {}) => {
        calls.push({ method, params });
        if (method === 'modpacks.cache') return { provider: 'CurseForge', state: 'Empty', items: [], detail: '', failedStage: '', retrievedAt: null, fromCache: false, stale: false, nextIndex: 0, hasMore: false } as T;
        if (method === 'modpacks.search' && params.index === 2) return { provider: 'CurseForge', state: 'Ready', items: [catalogProject('accepted-later', 'Accepted later', 'CurseForge')], detail: 'Ready.', failedStage: '', retrievedAt: null, fromCache: false, stale: false, nextIndex: 4, hasMore: false } as T;
        if (method === 'modpacks.search') return { provider: 'CurseForge', state: 'Ready', items: [catalogProject('accepted-first', 'Accepted first', 'CurseForge')], detail: 'Ready.', failedStage: '', retrievedAt: null, fromCache: false, stale: false, nextIndex: 2, hasMore: true } as T;
        return fixture.request<T>(method, params);
      },
      subscribe: listener => fixture.subscribe(listener),
      dispose: () => fixture.dispose()
    };
    useAppStore.setState({ bridge });
    render(<ModpackPicker value={null} onChange={() => undefined} />);
    fireEvent.click(await screen.findByRole('tab', { name: /CurseForge/ }));

    fireEvent.click(await screen.findByRole('button', { name: 'Load more' }));

    await waitFor(() => expect(calls.some(call => call.method === 'modpacks.search' && call.params.index === 2)).toBe(true));
    expect(screen.getByRole('button', { name: /Accepted first/ })).toBeTruthy();
    expect(screen.getByRole('button', { name: /Accepted later/ })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Load more' })).toBeNull();
  });
});

function catalogProject(projectId: string, name: string, provider: ModpackProject['provider'] = 'Modrinth'): ModpackProject {
  return {
    provider, projectId, slug: projectId, name, author: 'Fixture', summary: `${name} summary`,
    downloadCount: 1, updatedAt: null, categories: [], hasImage: false, serverSupport: 'FullyAutomated',
    clientRequirement: 'Required', trend: { available: false, detail: '' }, versions: [{
      versionId: `${projectId}-version`, versionName: '1.0', minecraftVersion: '1.21.8', loader: 'fabric',
      releaseChannel: 'Stable', publishedAt: null, sizeBytes: 1_024, changelog: '', requiredJavaMajor: 21,
      hasIntegrity: true, canCreate: true
    }]
  };
}
