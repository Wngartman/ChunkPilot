// @vitest-environment jsdom
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { BridgeAdapter } from '../bridge/client';
import type { BridgeMethod, ModpackProject, ModpackRelease } from '../bridge/types';
import { FixtureBridge, fixtures } from '../fixtures/catalog';
import { useAppStore } from '../state/store';
import { CreateServerPage } from './CreateServer';

const calls: { method: BridgeMethod; params: Record<string, unknown> }[] = [];

beforeEach(() => {
  calls.length = 0;
  window.sessionStorage.clear();
  window.history.replaceState({}, '', '/?fixture=running&page=create&stage=1&mode=modpack');
  useAppStore.setState({ snapshot: structuredClone(fixtures.running), bridge: null, busy: new Set(),
    pendingOperations: new Map(), completedOperations: new Set(), error: null });
});
afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe('CurseForge creation preflight', () => {
  it('keeps the shared fixture release intact through its real preflight path', async () => {
    renderWithBridge((method, params, fixture) => fixture.request(method, params), 'curseforge');
    fireEvent.click(await screen.findByRole('tab', { name: /CurseForge/ }));
    fireEvent.click(await screen.findByRole('button', { name: /Copper Trails/ }));
    await waitFor(() => expect(calls.some(call => call.method === 'modpacks.preflight')).toBe(true));
    await waitFor(() => expect((screen.getByRole('button', { name: /Continue/ }) as HTMLButtonElement).disabled).toBe(false));
    expect(screen.getByRole('combobox', { name: 'Exact modpack release' }).textContent).toContain('4.2.0');
    expect(screen.queryByText(/undefined/)).toBeNull();
    expect(calls.find(call => call.method === 'modpacks.preflight')!.params).toMatchObject({
      projectId: 'fixture-pack', versionId: 'fixture-pack-4'
    });
  });

  it('preflights the exact numbered large-catalog fixture release and rejects another release', async () => {
    const fixture = new FixtureBridge('curseforge-many');
    const release = await fixture.request<ModpackRelease>('modpacks.preflight', {
      projectId: 'fixture-pack-001', versionId: 'fixture-pack-001-release'
    });
    expect(release).toMatchObject({ versionId: 'fixture-pack-001-release', versionName: '4.0.1',
      minecraftVersion: '1.21.8', preflightState: 'Ready', canCreate: true });
    await expect(fixture.request('modpacks.preflight', {
      projectId: 'fixture-pack-001', versionId: 'fixture-pack-002-release'
    })).rejects.toThrow('exact fixture release');
  });

  it.each(['malformed', 'mismatched'] as const)('preserves release labels and blocks creation for a %s preflight response', async kind => {
    renderWithBridge(async (method, params, fixture) => {
      if (method === 'modpacks.cache') return catalogResult([]);
      if (method === 'modpacks.search' && params.provider === 'CurseForge')
        return catalogResult([curseForgeProject()]);
      if (method === 'modpacks.preflight') return kind === 'malformed' ? { accepted: true }
        : { ...curseForgeProject().versions[0], versionId: 'another-release', preflightState: 'Ready', canCreate: true };
      return fixture.request(method, params);
    });
    fireEvent.click(await screen.findByRole('tab', { name: /CurseForge/ }));
    fireEvent.click(await screen.findByRole('button', { name: /Exact Loader Pack/ }));
    expect(await screen.findByText(/native CurseForge review returned an invalid or mismatched release/)).toBeTruthy();
    expect(screen.getByRole('combobox', { name: 'Exact modpack release' }).textContent).toContain('1.0.0');
    expect(screen.queryByText(/undefined/)).toBeNull();
    expect((screen.getByRole('button', { name: /Continue/ }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('keeps an exact release blocked until native manifest inspection establishes the loader version', async () => {
    let complete!: (release: ModpackRelease) => void;
    const preflight = new Promise<ModpackRelease>(resolve => { complete = resolve; });
    renderWithBridge(async (method, params, fixture) => {
      if (method === 'modpacks.cache') return catalogResult([]);
      if (method === 'modpacks.search' && params.provider === 'CurseForge')
        return catalogResult([curseForgeProject()]);
      if (method === 'modpacks.preflight') return preflight;
      return fixture.request(method, params);
    });

    fireEvent.click(await screen.findByRole('tab', { name: /CurseForge/ }));
    fireEvent.click(await screen.findByRole('button', { name: /Exact Loader Pack/ }));

    await waitFor(() => expect(calls.some(call => call.method === 'modpacks.preflight')).toBe(true));
    const preflightCall = calls.find(call => call.method === 'modpacks.preflight')!;
    expect(preflightCall.params).toMatchObject({
      projectId: '10', versionId: '111', modpackSelectionMethod: 'Browse'
    });
    expect(preflightCall.params.reviewId).toEqual(expect.any(String));
    expect(calls.findIndex(call => call.method === 'modpacks.invalidatePreflight')).toBeLessThan(
      calls.findIndex(call => call.method === 'modpacks.preflight'));
    expect((screen.getByRole('button', { name: /Continue/ }) as HTMLButtonElement).disabled).toBe(true);

    complete({ ...curseForgeProject().versions[0], canCreate: true, preflightState: 'Ready',
      preflightDetail: 'Verified exact manifest.', loader: 'Forge', loaderVersion: '47.3.0', limitation: '' });

    await waitFor(() => expect((screen.getByRole('button', { name: /Continue/ }) as HTMLButtonElement).disabled).toBe(false));
    fireEvent.click(screen.getByRole('button', { name: /Continue/ }));
    fireEvent.click(screen.getByRole('button', { name: /Continue/ }));
    fireEvent.click(screen.getByRole('button', { name: /Continue/ }));
    await waitFor(() => expect(screen.getByText(/ChunkPilot managed servers|This destination is available/)).toBeTruthy());
    fireEvent.click(screen.getByRole('button', { name: /Continue/ }));
    fireEvent.click(screen.getByRole('button', { name: /Continue/ }));

    expect(await screen.findByText(/Minecraft 1\.20\.1 · Forge 47\.3\.0/)).toBeTruthy();
    fireEvent.click(screen.getByRole('checkbox', { name: /I understand this exact pack release/ }));
    fireEvent.click(screen.getByRole('checkbox', { name: /Minecraft EULA/ }));
    fireEvent.click(screen.getByRole('button', { name: /Create server/ }));

    await waitFor(() => expect(calls.some(call => call.method === 'creation.begin')).toBe(true));
    const creationCall = calls.find(call => call.method === 'creation.begin')!;
    expect(creationCall.params.modpackReviewId).toBe(preflightCall.params.reviewId);
    expect(creationCall.params.modpackSelectionMethod).toBe('Browse');
    expect(creationCall.params.operationId).not.toBe(preflightCall.params.reviewId);
  });

  it('keeps an unsupported manifest blocked with the native preflight reason', async () => {
    renderWithBridge(async (method, params, fixture) => {
      if (method === 'modpacks.cache') return catalogResult([]);
      if (method === 'modpacks.search' && params.provider === 'CurseForge')
        return catalogResult([curseForgeProject()]);
      if (method === 'modpacks.preflight') return {
        ...curseForgeProject().versions[0], canCreate: false, preflightState: 'Unsupported',
        preflightDetail: 'The manifest uses an unsupported loader.',
        limitation: 'The manifest uses an unsupported loader.'
      } satisfies ModpackRelease;
      return fixture.request(method, params);
    });

    fireEvent.click(await screen.findByRole('tab', { name: /CurseForge/ }));
    fireEvent.click(await screen.findByRole('button', { name: /Exact Loader Pack/ }));

    expect(await screen.findByText('The manifest uses an unsupported loader.')).toBeTruthy();
    expect((screen.getByRole('button', { name: /Continue/ }) as HTMLButtonElement).disabled).toBe(true);
    expect(calls.filter(call => call.method === 'modpacks.preflight').every(call =>
      call.params.projectId === '10' && call.params.versionId === '111' &&
      call.params.modpackSelectionMethod === 'Browse' && typeof call.params.reviewId === 'string')).toBe(true);
  });

  it('reuses the same unexpired exact review after Modrinth navigation and filter changes', async () => {
    renderWithBridge(async (method, params, fixture) => {
      if (method === 'modpacks.cache') return catalogResult([]);
      if (method === 'modpacks.search' && params.provider === 'CurseForge')
        return catalogResult([curseForgeProject()]);
      if (method === 'modpacks.preflight') return {
        ...curseForgeProject().versions[0], canCreate: true, preflightState: 'Ready',
        preflightDetail: 'Verified exact manifest.', loaderVersion: '47.3.0', limitation: ''
      } satisfies ModpackRelease;
      return fixture.request(method, params);
    });

    fireEvent.click(await screen.findByRole('tab', { name: /CurseForge/ }));
    fireEvent.click(await screen.findByRole('button', { name: /Exact Loader Pack/ }));
    await waitFor(() => expect(calls.filter(call => call.method === 'modpacks.preflight')).toHaveLength(1));
    await waitFor(() => expect((screen.getByRole('button', { name: /Continue/ }) as HTMLButtonElement).disabled).toBe(false));
    const firstReviewId = calls.find(call => call.method === 'modpacks.preflight')!.params.reviewId;
    const invalidations = calls.filter(call => call.method === 'modpacks.invalidatePreflight').length;

    fireEvent.click(screen.getByRole('tab', { name: /Modrinth/ }));
    fireEvent.change(screen.getByRole('searchbox', { name: /Search Modrinth modpacks/ }), {
      target: { value: 'technology' }
    });
    fireEvent.click(screen.getByRole('button', { name: /^Search$/ }));
    await waitFor(() => expect(calls.some(call => call.method === 'modpacks.search' &&
      call.params.provider === 'Modrinth' && call.params.search === 'technology')).toBe(true));
    expect(calls.filter(call => call.method === 'modpacks.invalidatePreflight')).toHaveLength(invalidations);
    fireEvent.click(screen.getByRole('tab', { name: /CurseForge/ }));

    await waitFor(() => expect((screen.getByRole('button', { name: /Continue/ }) as HTMLButtonElement).disabled).toBe(false));
    expect(calls.filter(call => call.method === 'modpacks.preflight')).toHaveLength(1);
    expect(calls.filter(call => call.method === 'modpacks.invalidatePreflight')).toHaveLength(invalidations);

    for (let index = 0; index < 5; index += 1) {
      fireEvent.click(screen.getByRole('button', { name: /Continue/ }));
      if (index === 2)
        await waitFor(() => expect(screen.getByText(/ChunkPilot managed servers|This destination is available/)).toBeTruthy());
    }
    fireEvent.click(screen.getByRole('checkbox', { name: /I understand this exact pack release/ }));
    fireEvent.click(screen.getByRole('checkbox', { name: /Minecraft EULA/ }));
    fireEvent.click(screen.getByRole('button', { name: /Create server/ }));
    await waitFor(() => expect(calls.some(call => call.method === 'creation.begin')).toBe(true));
    expect(calls.find(call => call.method === 'creation.begin')!.params.modpackReviewId).toBe(firstReviewId);
  });

  it('binds a resolved provider link to the Link review method', async () => {
    const project = curseForgeProject();
    let completeInvalidation!: () => void;
    const invalidation = new Promise<void>(resolve => { completeInvalidation = resolve; });
    renderWithBridge(async (method, params, fixture) => {
      if (method === 'modpacks.invalidatePreflight') return invalidation;
      if (method === 'modpacks.resolveLink') return {
        canonicalUrl: 'https://www.curseforge.com/minecraft/modpacks/exact-loader-pack',
        exactRelease: false,
        project,
        release: project.versions[0],
        detail: 'Resolved the newest release.'
      };
      if (method === 'modpacks.preflight') return {
        ...project.versions[0], canCreate: true, preflightState: 'Ready',
        preflightDetail: 'Verified exact manifest.', loaderVersion: '47.3.0', limitation: ''
      } satisfies ModpackRelease;
      return fixture.request(method, params);
    });

    fireEvent.click(screen.getByRole('button', { name: /Back/ }));
    fireEvent.click(screen.getByRole('button', { name: /Paste provider link/ }));
    fireEvent.click(screen.getByRole('button', { name: /Continue/ }));
    fireEvent.change(await screen.findByRole('textbox', { name: /Provider project link/ }), {
      target: { value: 'https://www.curseforge.com/minecraft/modpacks/exact-loader-pack' }
    });
    fireEvent.click(screen.getByRole('button', { name: /^Resolve$/ }));

    await waitFor(() => expect(calls.some(call => call.method === 'modpacks.resolveLink')).toBe(true));
    expect(calls.filter(call => call.method === 'modpacks.preflight')).toHaveLength(0);
    completeInvalidation();
    await waitFor(() => expect(calls.some(call => call.method === 'modpacks.preflight')).toBe(true));
    expect(calls.find(call => call.method === 'modpacks.preflight')!.params).toMatchObject({
      projectId: '10', versionId: '111', modpackSelectionMethod: 'Link', reviewId: expect.any(String)
    });
  });

  it('does not expire evidence after creation has synchronously claimed the review', async () => {
    const timers = vi.spyOn(window, 'setTimeout');
    renderWithBridge(async (method, params, fixture) => {
      if (method === 'modpacks.cache') return catalogResult([]);
      if (method === 'modpacks.search' && params.provider === 'CurseForge')
        return catalogResult([curseForgeProject()]);
      if (method === 'modpacks.preflight') return {
        ...curseForgeProject().versions[0], canCreate: true, preflightState: 'Ready',
        preflightDetail: 'Verified exact manifest.', loaderVersion: '47.3.0', limitation: ''
      } satisfies ModpackRelease;
      return fixture.request(method, params);
    });

    fireEvent.click(await screen.findByRole('tab', { name: /CurseForge/ }));
    fireEvent.click(await screen.findByRole('button', { name: /Exact Loader Pack/ }));
    await waitFor(() => expect((screen.getByRole('button', { name: /Continue/ }) as HTMLButtonElement).disabled).toBe(false));
    await waitFor(() => expect(timers.mock.calls.some(([, delay]) => Number(delay) > 800_000)).toBe(true));
    const expiryCallback = timers.mock.calls.find(([, delay]) => Number(delay) > 800_000)?.[0];
    expect(typeof expiryCallback).toBe('function');

    for (let index = 0; index < 5; index += 1) {
      fireEvent.click(screen.getByRole('button', { name: /Continue/ }));
      if (index === 2)
        await waitFor(() => expect(screen.getByText(/ChunkPilot managed servers|This destination is available/)).toBeTruthy());
    }
    fireEvent.click(screen.getByRole('checkbox', { name: /I understand this exact pack release/ }));
    fireEvent.click(screen.getByRole('checkbox', { name: /Minecraft EULA/ }));
    const invalidations = calls.filter(call => call.method === 'modpacks.invalidatePreflight').length;
    fireEvent.click(screen.getByRole('button', { name: /Create server/ }));
    act(() => { if (typeof expiryCallback === 'function') expiryCallback(); });

    await waitFor(() => expect(calls.some(call => call.method === 'creation.begin')).toBe(true));
    expect(calls.filter(call => call.method === 'modpacks.invalidatePreflight')).toHaveLength(invalidations);
    expect(calls.filter(call => call.method === 'modpacks.preflight')).toHaveLength(1);
  });

  it('refreshes a stale ready review before creation when the expiry timer was throttled', async () => {
    renderWithBridge(async (method, params, fixture) => {
      if (method === 'modpacks.cache') return catalogResult([]);
      if (method === 'modpacks.search' && params.provider === 'CurseForge')
        return catalogResult([curseForgeProject()]);
      if (method === 'modpacks.preflight') return {
        ...curseForgeProject().versions[0], canCreate: true, preflightState: 'Ready',
        preflightDetail: 'Verified exact manifest.', loaderVersion: '47.3.0', limitation: ''
      } satisfies ModpackRelease;
      return fixture.request(method, params);
    });

    fireEvent.click(await screen.findByRole('tab', { name: /CurseForge/ }));
    fireEvent.click(await screen.findByRole('button', { name: /Exact Loader Pack/ }));
    await waitFor(() => expect((screen.getByRole('button', { name: /Continue/ }) as HTMLButtonElement).disabled).toBe(false));
    const firstReviewId = calls.find(call => call.method === 'modpacks.preflight')!.params.reviewId;

    for (let index = 0; index < 5; index += 1) {
      fireEvent.click(screen.getByRole('button', { name: /Continue/ }));
      if (index === 2)
        await waitFor(() => expect(screen.getByText(/ChunkPilot managed servers|This destination is available/)).toBeTruthy());
    }
    fireEvent.click(screen.getByRole('checkbox', { name: /I understand this exact pack release/ }));
    fireEvent.click(screen.getByRole('checkbox', { name: /Minecraft EULA/ }));
    const invalidations = calls.filter(call => call.method === 'modpacks.invalidatePreflight').length;
    const currentTime = Date.now();
    vi.spyOn(Date, 'now').mockReturnValue(currentTime + 15 * 60_000);
    fireEvent.click(screen.getByRole('button', { name: /Create server/ }));

    await waitFor(() => expect(calls.filter(call => call.method === 'modpacks.preflight')).toHaveLength(2));
    expect(calls.some(call => call.method === 'creation.begin')).toBe(false);
    expect(calls.filter(call => call.method === 'modpacks.invalidatePreflight')).toHaveLength(invalidations + 1);
    expect(calls.filter(call => call.method === 'modpacks.preflight')[1].params.reviewId).not.toBe(firstReviewId);
  });
});

function renderWithBridge(handler: (
  method: BridgeMethod,
  params: Record<string, unknown>,
  fixture: FixtureBridge
) => Promise<unknown>, fixtureName = 'running') {
  const fixture = new FixtureBridge(fixtureName);
  const bridge: BridgeAdapter = {
    request: async <T,>(method: BridgeMethod, params: Record<string, unknown> = {}) => {
      calls.push({ method, params });
      return await handler(method, params, fixture) as T;
    },
    subscribe: listener => fixture.subscribe(listener),
    dispose: () => fixture.dispose()
  };
  useAppStore.setState({ bridge });
  render(<CreateServerPage onDone={() => undefined} />);
}

function catalogResult(items: ModpackProject[]) {
  return { provider: 'CurseForge', state: items.length ? 'Ready' : 'Empty', items,
    detail: items.length ? 'Ready.' : 'No cached results.', failedStage: '', retrievedAt: null,
    fromCache: !items.length, stale: false, nextIndex: items.length, hasMore: false };
}

function curseForgeProject(): ModpackProject {
  return {
    provider: 'CurseForge', projectId: '10', slug: 'exact-loader-pack', name: 'Exact Loader Pack',
    author: 'Fixture', summary: 'Requires native client-manifest inspection.', downloadCount: 1,
    updatedAt: null, categories: ['technology'], hasImage: false, serverSupport: 'FullyAutomated',
    clientRequirement: 'MatchingPackRequired', trend: { available: false, detail: '' }, versions: [{
      versionId: '111', versionName: '1.0.0', minecraftVersion: '1.20.1', loader: 'Forge',
      releaseChannel: 'Stable', publishedAt: null, sizeBytes: 2_048, changelog: '', requiredJavaMajor: 17,
      hasIntegrity: true, canCreate: false, preflightState: 'Required',
      preflightDetail: 'Exact client manifest required.', serverPath: 'Official server pack',
      limitation: 'Inspecting the exact client manifest is required before this release can be created.'
    }]
  };
}
