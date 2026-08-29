// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
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
afterEach(cleanup);

describe('CurseForge creation preflight', () => {
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

    await waitFor(() => expect(calls).toContainEqual({
      method: 'modpacks.preflight', params: { projectId: '10', versionId: '111' }
    }));
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
      call.params.projectId === '10' && call.params.versionId === '111')).toBe(true);
  });
});

function renderWithBridge(handler: (
  method: BridgeMethod,
  params: Record<string, unknown>,
  fixture: FixtureBridge
) => Promise<unknown>) {
  const fixture = new FixtureBridge('running');
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
