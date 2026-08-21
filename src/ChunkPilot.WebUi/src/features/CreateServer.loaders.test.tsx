// @vitest-environment jsdom
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { BridgeAdapter } from '../bridge/client';
import type { BridgeMethod } from '../bridge/types';
import { FixtureBridge, fixtures } from '../fixtures/catalog';
import { useAppStore } from '../state/store';
import { CreateServerPage } from './CreateServer';

const calls: { method: BridgeMethod; params: Record<string, unknown> }[] = [];

beforeEach(() => {
  calls.length = 0;
  window.sessionStorage.clear();
  useAppStore.setState({ snapshot: structuredClone(fixtures.running), bridge: null, busy: new Set(),
    pendingOperations: new Map(), completedOperations: new Set(), error: null });
});
afterEach(() => { cleanup(); vi.restoreAllMocks(); });

function renderPlatform(platform: 'fabric' | 'quilt' | 'forge' | 'neoforge' | 'legacyfabric' | 'ornithe') {
  window.history.replaceState({}, '', `/?fixture=running&page=create&stage=1&mode=${platform}`);
  const fixture = new FixtureBridge('running');
  const bridge: BridgeAdapter = {
    request: async <T,>(method: BridgeMethod, params: Record<string, unknown> = {}) => {
      calls.push({ method, params });
      return fixture.request<T>(method, params);
    },
    subscribe: listener => fixture.subscribe(listener),
    dispose: () => fixture.dispose()
  };
  useAppStore.setState({ bridge });
  render(<CreateServerPage onDone={() => undefined} />);
}

describe('managed-loader creation', () => {
  it('starts with exactly three intent choices and preserves a disclosed custom loader', async () => {
    window.history.replaceState({}, '', '/?fixture=running&page=create');
    const fixture = new FixtureBridge('running');
    const bridge: BridgeAdapter = {
      request: async <T,>(method: BridgeMethod, params: Record<string, unknown> = {}) => fixture.request<T>(method, params),
      subscribe: listener => fixture.subscribe(listener),
      dispose: () => fixture.dispose()
    };
    useAppStore.setState({ bridge });
    const { container } = render(<CreateServerPage onDone={() => undefined} />);

    expect(container.querySelectorAll('button[aria-pressed]')).toHaveLength(3);
    fireEvent.click(screen.getByRole('button', { name: /Modpacks/ }));
    fireEvent.click(await screen.findByRole('button', { name: /Build a custom modded server/ }));
    fireEvent.change(screen.getByRole('combobox', { name: 'Custom modded server loader' }), { target: { value: 'NeoForge' } });
    fireEvent.click(screen.getByRole('button', { name: /Continue/ }));
    expect(await screen.findByText('Exact NeoForge version')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /Back/ }));
    expect((screen.getByRole('combobox', { name: 'Custom modded server loader' }) as HTMLSelectElement).value).toBe('NeoForge');
  });

  it('loads an exact Fabric Loader and installer selection', async () => {
    renderPlatform('fabric');

    expect(await screen.findByText('Exact Fabric version')).toBeTruthy();
    expect(screen.getByText(/Loader 0.19.3 · installer 1.1.2/)).toBeTruthy();
    await waitFor(() => expect(calls).toContainEqual({
      method: 'creation.loaderBuilds', params: { platform: 'Fabric', versionId: '1.21.8' }
    }));
  });

  it('loads an exact NeoForge installer selection from official metadata', async () => {
    renderPlatform('neoforge');

    expect(await screen.findByText('Exact NeoForge version')).toBeTruthy();
    expect(screen.getByText(/NeoForge 26.2.0.61/)).toBeTruthy();
    expect(screen.getByText('Official provider checksum')).toBeTruthy();
    await waitFor(() => expect(calls).toContainEqual({
      method: 'creation.loaderBuilds', params: { platform: 'NeoForge', versionId: '1.21.8' }
    }));
  });

  it.each([
    ['quilt', 'Quilt', 'Exact Quilt version', /Quilt Loader 0.30.0/],
    ['forge', 'Forge', 'Exact Forge version', /Forge 65.1.0/]
  ] as const)('loads the exact official %s catalog', async (mode, platform, heading, build) => {
    renderPlatform(mode);
    expect(await screen.findByText(heading)).toBeTruthy();
    expect(screen.getByText(build)).toBeTruthy();
    await waitFor(() => expect(calls).toContainEqual({
      method: 'creation.loaderBuilds', params: { platform, versionId: '1.21.8' }
    }));
  });

  it.each(['fabric', 'neoforge'] as const)(
    'keeps one durable operation identity when %s acceptance is lost and creation continues',
    async platform => {
      const operationId = platform === 'fabric'
        ? '55555555-5555-4555-8555-555555555555'
        : '66666666-6666-4666-8666-666666666666';
      vi.spyOn(globalThis.crypto, 'randomUUID').mockReturnValue(operationId);
      window.history.replaceState({}, '', `/?fixture=running&page=create&stage=6&mode=${platform}`);
      const fixture = new FixtureBridge('running');
      const bridge: BridgeAdapter = {
        request: async <T,>(method: BridgeMethod, params: Record<string, unknown> = {}) => {
          calls.push({ method, params });
          if (method === 'creation.begin')
            throw new Error('WebUI request timed out: creation.begin');
          if (method === 'creation.progress')
            return { stage: 'Registering', percent: 0, message: 'Creation was accepted and registration is continuing.', isTerminal: false } as T;
          return fixture.request<T>(method, params);
        },
        subscribe: listener => fixture.subscribe(listener),
        dispose: () => fixture.dispose()
      };
      useAppStore.setState({ bridge, error: null });
      render(<CreateServerPage onDone={() => undefined} />);

      const create = await screen.findByRole('button', { name: 'Create server' }) as HTMLButtonElement;
      await waitFor(() => expect(create.disabled).toBe(false));
      act(() => {
        create.dispatchEvent(new MouseEvent('click', { bubbles: true }));
        create.dispatchEvent(new MouseEvent('click', { bubbles: true }));
      });

      await waitFor(() => expect(calls.filter(call => call.method === 'creation.begin')).toHaveLength(1));
      await waitFor(() => expect(calls.some(call => call.method === 'creation.progress' && call.params.operationId === operationId)).toBe(true));
      expect(calls.find(call => call.method === 'creation.begin')?.params.operationId).toBe(operationId);
      expect(screen.getByText('Registering')).toBeTruthy();
      expect(useAppStore.getState().error).toBeNull();
    }
  );

  it('reattaches to an accepted operation after the creation page is remounted', async () => {
    const operationId = '77777777-7777-4777-8777-777777777777';
    window.sessionStorage.setItem('chunkpilot.creation.operation', operationId);
    window.history.replaceState({}, '', '/?fixture=running&page=create');
    const fixture = new FixtureBridge('running');
    const bridge: BridgeAdapter = {
      request: async <T,>(method: BridgeMethod, params: Record<string, unknown> = {}) => {
        calls.push({ method, params });
        if (method === 'creation.operations') return [{ operationId, revision: 4, stage: 'DownloadingServer', phase: 'MaterializingCandidate', percent: 41, message: 'Downloading the exact server files.', isTerminal: false, success: null }] as T;
        if (method === 'creation.progress') return { operationId, revision: 5, stage: 'VerifyingServerDownload', phase: 'VerifyingCandidate', percent: 57, message: 'Verifying the provider hash.', isTerminal: false, success: null } as T;
        return fixture.request<T>(method, params);
      },
      subscribe: listener => fixture.subscribe(listener),
      dispose: () => fixture.dispose()
    };
    useAppStore.setState({ bridge });

    render(<CreateServerPage onDone={() => undefined} />);

    expect(await screen.findByText('Verifying the provider hash.')).toBeTruthy();
    expect(screen.getByText('Verifying server download')).toBeTruthy();
    expect(screen.getByRole('progressbar', { name: 'Server creation progress' })).toBeTruthy();
    expect(calls.filter(call => call.method === 'creation.begin')).toHaveLength(0);
    expect(calls.some(call => call.method === 'creation.progress' && call.params.operationId === operationId)).toBe(true);
  });
});
