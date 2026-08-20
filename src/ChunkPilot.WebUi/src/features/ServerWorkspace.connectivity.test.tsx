// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
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
    return { accepted: true } as T;
  },
  subscribe: () => () => undefined,
  dispose: () => undefined
};

beforeEach(() => {
  calls.length = 0;
  window.history.replaceState({}, '', '/');
  const current = structuredClone(fixtures.running);
  useAppStore.setState({ snapshot: current, bridge, busy: new Set(), error: null });
});
afterEach(cleanup);

function workspace() {
  const server = useAppStore.getState().snapshot!.servers[0];
  render(<NavigationGuardProvider><ServerWorkspace serverId={server.id} /></NavigationGuardProvider>);
  return server;
}

describe('server connectivity presentation', () => {
  it('opens the Share dialog and copies the authoritative home-network address', async () => {
    const server = workspace();
    fireEvent.click(screen.getByRole('button', { name: 'Share' }));
    const dialog = screen.getByRole('dialog', { name: `Share ${server.name}` });
    expect(within(dialog).getByText('Available on your home network')).toBeTruthy();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Copy' }));
    expect(calls).toContainEqual({ method: 'connectivity.copyAddress', params: { serverId: server.id, kind: 'lan' } });
    fireEvent.keyDown(window, { key: 'Escape' });
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('offers plain-language connection modes and persists the selected mode', () => {
    window.history.replaceState({}, '', '/?tab=settings&settings=Connectivity');
    const server = workspace();
    expect(screen.getByRole('button', { name: /Local only/ })).toBeTruthy();
    expect(screen.getByRole('button', { name: /LAN/ }).getAttribute('data-selected')).toBe('true');
    expect(screen.getByRole('button', { name: /Internet hosting/ })).toBeTruthy();
    expect(screen.getByRole('button', { name: /Configure later/ })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /Internet hosting/ }));
    expect(calls).toContainEqual({ method: 'connectivity.setMode', params: { serverId: server.id, mode: 'PortForwarding' } });
  });

  it('uses a verified public endpoint only when outside-in evidence exists', () => {
    const current = structuredClone(fixtures.running);
    current.connectivity!.mode = 'PortForwarding';
    current.connectivity!.modeTitle = 'Internet hosting';
    current.connectivity!.status = { title: 'Friends can join', detail: 'Outside-in verified.', tone: 'success' };
    current.connectivity!.addresses.publicVerified = '203.0.113.24:25565';
    current.connectivity!.external.checkedAt = 'Today at 4:42 PM';
    current.servers[0].publicAddress = '203.0.113.24:25565';
    current.servers[0].publicReachability = 'confirmed';
    useAppStore.setState({ snapshot: current });
    const server = workspace();
    fireEvent.click(screen.getByRole('button', { name: 'Share' }));
    const dialog = screen.getByRole('dialog', { name: `Share ${server.name}` });
    expect(within(dialog).getByText('Verified Internet address')).toBeTruthy();
    expect(within(dialog).getByText('203.0.113.24:25565')).toBeTruthy();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Copy' }));
    expect(calls).toContainEqual({ method: 'connectivity.copyAddress', params: { serverId: server.id, kind: 'public' } });
  });

  it('shows and copies an active router-reported address without claiming it is verified', () => {
    const current = structuredClone(fixtures.running);
    current.connectivity!.mode = 'PortForwarding';
    current.connectivity!.modeTitle = 'Internet hosting';
    current.connectivity!.status = { title: 'Internet access not verified', detail: 'Verification is pending.', tone: 'neutral' };
    current.connectivity!.router.enabled = true;
    current.connectivity!.router.phase = 'Active';
    current.connectivity!.addresses.routerReported = '203.0.113.24:25565';
    current.connectivity!.addresses.publicVerified = null;
    current.connectivity!.external.phase = 'ProbeUnavailable';
    current.connectivity!.external.canCheck = false;
    useAppStore.setState({ snapshot: current });
    const server = workspace();
    fireEvent.click(screen.getByRole('button', { name: 'Share' }));
    const dialog = screen.getByRole('dialog', { name: `Share ${server.name}` });
    expect(within(dialog).getByText('Public address — unverified')).toBeTruthy();
    expect(within(dialog).queryByText('Friends can join')).toBeNull();
    expect(within(dialog).getByText(/most likely address/i)).toBeTruthy();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Copy' }));
    expect(calls).toContainEqual({ method: 'connectivity.copyAddress', params: { serverId: server.id, kind: 'router' } });
  });

  it('presents a four-step beginner flow and automatically verifies an eligible running server', async () => {
    const current = structuredClone(fixtures.running);
    current.connectivity!.mode = 'PortForwarding';
    current.connectivity!.modeTitle = 'Internet hosting';
    current.connectivity!.router.enabled = true;
    current.connectivity!.router.phase = 'Active';
    current.connectivity!.firewall.configured = true;
    current.connectivity!.external.phase = 'NotChecked';
    current.connectivity!.external.canCheck = true;
    current.connectivity!.external.busy = false;
    useAppStore.setState({ snapshot: current });
    window.history.replaceState({}, '', '/?tab=settings&settings=Connectivity');
    const server = workspace();
    const progress = screen.getByLabelText('Internet setup progress');
    expect(within(progress).getByText('Windows Firewall')).toBeTruthy();
    expect(within(progress).getByText('Automatic router setup')).toBeTruthy();
    expect(within(progress).getByText('Internet verification')).toBeTruthy();
    await screen.findByRole('button', { name: 'Verify now' });
    expect(calls).toContainEqual({ method: 'connectivity.external.check', params: { serverId: server.id } });
  });
});
