// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
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
afterEach(() => { cleanup(); vi.restoreAllMocks(); });

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
    await waitFor(() => expect(calls.some(call => call.method === 'versions.install' &&
      call.params.serverId === server.id && call.params.targetVersionId === 'release-2.5.0')).toBe(true));
  });

  it('requires one explicit bounded decision per migration conflict and resubmits the fenced review', async () => {
    vi.spyOn(globalThis.crypto, 'randomUUID').mockReturnValue('77777777-7777-4777-8777-777777777777');
    const snapshot = structuredClone(fixtures.modpack);
    const server = snapshot.servers[0];
    snapshot.update = {
      ...snapshot.update!,
      status: 'Update available',
      targetVersionId: 'release-2.5.0',
      latestVersionName: '2.5.0',
      canInstall: true,
      operationState: 'PlanningMigration',
      migrationReview: {
        reviewOperationId: '66666666-6666-4666-8666-666666666666',
        serverId: server.id,
        targetVersionId: 'release-2.5.0',
        conflictCount: 2,
        changeCount: 3,
        conflicts: ['config/a.toml', 'mods/removed.jar'],
        changes: [
          { relativePath: 'config/a.toml', ownership: 'Unknown', change: 'Kept local modification pending review', reason: 'Installed and target values differ.', oldSha256: 'old-a', newSha256: 'new-a', requiresResolution: true },
          { relativePath: 'mods/removed.jar', ownership: 'PackManaged', change: 'Removed from active pack', reason: 'The target pack removed this JAR.', oldSha256: 'old-b', newSha256: '', requiresResolution: true },
          { relativePath: 'mods/new.jar', ownership: 'PackManaged', change: 'Added by new pack', reason: 'The target pack introduced this file.', oldSha256: '', newSha256: 'new-c', requiresResolution: false }
        ],
        truncated: false,
        canResolve: true,
        detail: 'Choose one explicit result for every conflict.'
      }
    };
    useAppStore.setState({ snapshot });
    render(<NavigationGuardProvider><ServerWorkspace serverId={server.id} /></NavigationGuardProvider>);

    expect(screen.getByRole('dialog', { name: 'Review 2 update conflicts' })).toBeTruthy();
    const continueButton = screen.getByRole('button', { name: 'Continue update' });
    expect((continueButton as HTMLButtonElement).disabled).toBe(true);
    fireEvent.change(screen.getByRole('combobox', { name: 'Decision for config/a.toml' }), { target: { value: 'KeepOld' } });
    expect((continueButton as HTMLButtonElement).disabled).toBe(true);
    fireEvent.change(screen.getByRole('combobox', { name: 'Decision for mods/removed.jar' }), { target: { value: 'NewBaseline' } });
    expect((continueButton as HTMLButtonElement).disabled).toBe(false);
    fireEvent.click(continueButton);

    await waitFor(() => expect(calls).toContainEqual({
      method: 'versions.install',
      params: {
        serverId: server.id,
        operationId: '77777777-7777-4777-8777-777777777777',
        targetVersionId: 'release-2.5.0',
        reviewedOperationId: '66666666-6666-4666-8666-666666666666',
        confirmedMigrationWarnings: true,
        migrationResolutions: { 'config/a.toml': 'KeepOld', 'mods/removed.jar': 'NewBaseline' }
      }
    }));
  });
});
