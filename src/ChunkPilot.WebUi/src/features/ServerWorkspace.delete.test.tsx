// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import type { BridgeAdapter } from '../bridge/client';
import type { BridgeMethod, ServerDeletionPreflight } from '../bridge/types';
import { fixtures } from '../fixtures/catalog';
import { NavigationGuardProvider } from '../app/NavigationGuard';
import { useAppStore } from '../state/store';
import { ServerWorkspace } from './ServerWorkspace';

const calls: { method: BridgeMethod; params: Record<string, unknown> }[] = [];
let ownershipProven = true;
let canCreateManagedCopy = false;
const bridge: BridgeAdapter = {
  request: async <T,>(method: BridgeMethod, params: Record<string, unknown> = {}) => {
    calls.push({ method, params });
    if (method === 'servers.deletePreflight') return {
      token: 'fixture', serverId: params.serverId,
      serverName: 'Copper Valley', platform: 'Vanilla', version: '1.21.8', state: 'Stopped',
      isManaged: true, ownershipProven,
      ownershipStatus: ownershipProven ? 'ProvenMarker' : 'Ambiguous',
      ownershipDetail: ownershipProven ? 'Persistent marker proven.' : 'No exact ownership marker is present.',
      ownershipEvidence: [{ code: 'persistent-marker', satisfied: ownershipProven, detail: ownershipProven ? 'Marker proven.' : 'Marker missing.' }],
      canCreateManagedCopy, reviewFingerprint: 'fixture-fingerprint',
      managedRoot: 'C:\\Fixture\\Servers\\Copper Valley',
      worldLocation: 'C:\\Fixture\\Servers\\Copper Valley\\world', backupCount: 2,
      managedBackupPaths: ['C:\\Fixture\\Backups\\one.cpb'], protectedExternalPaths: [],
      activeScheduleCount: 1, internetSharingConfigured: false, firewallRemovalRequired: false,
      blockers: ownershipProven ? [] : ['Ownership is not proven.'], expiresAt: new Date(Date.now() + 300_000).toISOString()
    } as ServerDeletionPreflight as T;
    return { accepted: true, operationId: 'delete-operation' } as T;
  },
  subscribe: () => () => undefined,
  dispose: () => undefined
};

beforeEach(() => {
  calls.length = 0; ownershipProven = true; canCreateManagedCopy = false;
  window.history.replaceState({}, '', '/?mode=delete');
  const current = structuredClone(fixtures.running);
  current.servers[0].state = 'Stopped';
  useAppStore.setState({ snapshot: current, bridge, busy: new Set(), error: null });
});
afterEach(cleanup);

function renderWorkspace() {
  const server = useAppStore.getState().snapshot!.servers[0];
  render(<NavigationGuardProvider><ServerWorkspace serverId={server.id} /></NavigationGuardProvider>);
  return server;
}

describe('server deletion', () => {
  it('requires exact permanent-deletion acknowledgements before sending the command', async () => {
    const server = renderWorkspace();
    const dialog = await screen.findByRole('dialog', { name: `Delete ${server.name}?` });
    await waitFor(() => expect(within(dialog).getByRole('radio', { name: /Move to Recovery/ }).getAttribute('aria-checked')).toBe('true'));
    const permanentMode = within(dialog).getByRole('radio', { name: /Permanently delete/ });
    fireEvent.click(permanentMode);
    await waitFor(() => expect(permanentMode.getAttribute('aria-checked')).toBe('true'));
    await waitFor(() => expect(within(dialog).getByRole('button', { name: 'Permanently delete' })).toBeTruthy());
    const submit = within(dialog).getByRole('button', { name: 'Permanently delete' }) as HTMLButtonElement;
    expect(submit.disabled).toBe(true);
    fireEvent.change(within(dialog).getByRole('textbox'), { target: { value: server.name } });
    const checks = within(dialog).getAllByRole('checkbox');
    checks.forEach(check => fireEvent.click(check));
    expect(submit.disabled).toBe(false);
    fireEvent.click(submit);
    expect(calls).toContainEqual({
      method: 'servers.delete',
      params: {
        serverId: server.id,
        preflightToken: 'fixture',
        mode: 'Permanent', confirmationName: server.name,
        acknowledgeWorldDeletion: true, acknowledgeManagedBackupDeletion: true
      }
    });
  });

  it('limits ownership-uncertain servers to removal from ChunkPilot', async () => {
    ownershipProven = false;
    const server = renderWorkspace();
    const dialog = await screen.findByRole('dialog', { name: `Delete ${server.name}?` });
    expect((within(dialog).getByRole('radio', { name: /Move to Recovery/ }) as HTMLButtonElement).disabled).toBe(true);
    expect((within(dialog).getByRole('radio', { name: /Permanently delete/ }) as HTMLButtonElement).disabled).toBe(true);
    await waitFor(() => expect(within(dialog).getByRole('radio', { name: /Remove from ChunkPilot/ }).getAttribute('aria-checked')).toBe('true'));
  });

  it('offers a verified managed copy without enabling deletion of the ambiguous source', async () => {
    ownershipProven = false;
    canCreateManagedCopy = true;
    const server = renderWorkspace();
    const dialog = await screen.findByRole('dialog', { name: `Delete ${server.name}?` });
    expect((within(dialog).getByRole('radio', { name: /Permanently delete/ }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(within(dialog).getByRole('button', { name: 'Create managed copy' }));
    expect(calls).toContainEqual({
      method: 'servers.createManagedCopy',
      params: { serverId: server.id, preflightToken: 'fixture' }
    });
  });
});
