// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { NavigationGuardProvider } from '../app/NavigationGuard';
import type { BridgeAdapter } from '../bridge/client';
import type { BridgeMethod } from '../bridge/types';
import { FixtureBridge, fixtures } from '../fixtures/catalog';
import { useAppStore } from '../state/store';
import { ServerWorkspace } from './ServerWorkspace';

const calls: { method: BridgeMethod; params: Record<string, unknown> }[] = [];

beforeEach(() => {
  calls.length = 0;
  window.history.replaceState({}, '', '/?fixture=modpack&page=servers&tab=content');
  const fixture = new FixtureBridge('modpack');
  const bridge: BridgeAdapter = {
    request: async <T,>(method: BridgeMethod, params: Record<string, unknown> = {}) => {
      calls.push({ method, params });
      return fixture.request<T>(method, params);
    },
    subscribe: listener => fixture.subscribe(listener),
    dispose: () => fixture.dispose()
  };
  useAppStore.setState({ snapshot: structuredClone(fixtures.modpack), bridge, busy: new Set(), pendingOperations: new Map(), completedOperations: new Set(), error: null });
});
afterEach(cleanup);

describe('installed modpack workspace', () => {
  it('does not offer the already-installed release even if a stale renderer flag says it is installable', () => {
    const snapshot = structuredClone(fixtures.modpack);
    snapshot.update = { ...snapshot.update!, status: 'Up to date', canInstall: true };
    useAppStore.setState({ snapshot });
    const server = snapshot.servers[0];

    render(<NavigationGuardProvider><ServerWorkspace serverId={server.id} /></NavigationGuardProvider>);

    expect(screen.getByText('Up to date')).toBeTruthy();
    expect(screen.queryByRole('button', { name: /^Install pack / })).toBeNull();
  });

  it('uses exact pack identity and whole-pack update actions', async () => {
    const server = useAppStore.getState().snapshot!.servers[0];
    render(<NavigationGuardProvider><ServerWorkspace serverId={server.id} /></NavigationGuardProvider>);

    expect(screen.getByRole('button', { name: 'Modpack' })).toBeTruthy();
    expect(screen.getAllByText('Adventure Ridge Pack').length).toBeGreaterThan(0);
    expect(screen.getByText('Whole pack release')).toBeTruthy();
    expect(screen.getByText(/never update constituent mods independently/)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Check pack release' }));
    await waitFor(() => expect(calls).toContainEqual({ method: 'versions.check', params: { serverId: server.id } }));
    expect(calls.some(call => call.method.startsWith('mods.install'))).toBe(false);
  });

  it('confirms an update in the WebUI and keeps native execution deferred', async () => {
    const snapshot = structuredClone(fixtures.modpack);
    snapshot.update = {
      ...snapshot.update!,
      status: 'Update available',
      detail: 'A compatible exact provider release is available.',
      latestVersionName: '2.5.0',
      targetVersionId: 'release-2.5.0',
      targetPublishedAt: '2026-08-20T12:00:00Z',
      downloadSizeBytes: 31_457_280,
      compatibilityReasons: ['Exact Minecraft and loader versions match.'],
      canInstall: true
    };
    useAppStore.setState({ snapshot });
    const server = snapshot.servers[0];
    render(<NavigationGuardProvider><ServerWorkspace serverId={server.id} /></NavigationGuardProvider>);

    fireEvent.click(screen.getByRole('button', { name: 'Install pack 2.5.0' }));
    expect(screen.getByRole('dialog', { name: 'Install 2.5.0?' })).toBeTruthy();
    expect(screen.getByText('Exact Minecraft and loader versions match.')).toBeTruthy();
    expect(calls.some(call => call.method === 'versions.install')).toBe(false);

    fireEvent.click(screen.getByRole('button', { name: 'Install update' }));
    await waitFor(() => expect(calls.some(call => call.method === 'versions.install' && call.params.serverId === server.id)).toBe(true));
  });
});
