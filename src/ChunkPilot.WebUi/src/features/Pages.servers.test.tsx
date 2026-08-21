// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { BridgeAdapter } from '../bridge/client';
import type { BridgeMethod } from '../bridge/types';
import { fixtures } from '../fixtures/catalog';
import { useAppStore } from '../state/store';
import { ServersPage } from './Pages';

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
  useAppStore.setState({ snapshot: structuredClone(fixtures.running), bridge, busy: new Set(), error: null });
});
afterEach(cleanup);

describe('Servers library joining addresses', () => {
  it('copies the home-network address without opening the server workspace', () => {
    const open = vi.fn();
    render(<ServersPage onOpenServer={open} onCreate={() => undefined} />);
    fireEvent.click(screen.getByRole('button', { name: 'Copy home address' }));
    const server = useAppStore.getState().snapshot!.servers[0];
    expect(calls).toContainEqual({ method: 'connectivity.copyAddress', params: { serverId: server.id, kind: 'lan' } });
    expect(open).not.toHaveBeenCalled();
  });

  it('never promotes an unverified router address to the Internet copy action', () => {
    const current = structuredClone(fixtures.running);
    current.connectivity!.mode = 'PortForwarding';
    current.connectivity!.addresses.routerReported = '203.0.113.24:25565';
    current.connectivity!.addresses.publicVerified = null;
    current.connectivity!.router.enabled = true;
    current.connectivity!.firewall.configured = true;
    current.servers[0].publicReachability = 'not-confirmed';
    useAppStore.setState({ snapshot: current });
    render(<ServersPage onOpenServer={() => undefined} onCreate={() => undefined} />);
    expect(screen.getByText('203.0.113.24:25565')).toBeTruthy();
    expect(screen.getByText('Internet sharing configured')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Copy Internet address' })).toBeTruthy();
    expect(screen.queryByText('Friends can join')).toBeNull();
  });

  it('uses durable per-server Internet evidence when no workspace is selected', () => {
    const current = structuredClone(fixtures.several);
    current.selectedServerId = null;
    current.connectivity = null;
    current.servers[0] = {
      ...current.servers[0],
      connectionMode: 'PortForwarding',
      publicAddress: '203.0.113.24:25565',
      publicAddressKind: 'router',
      publicAddressObservedAt: '2026-08-14T16:42:00-06:00',
      publicReachability: 'not-confirmed'
    };
    useAppStore.setState({ snapshot: current });
    render(<ServersPage onOpenServer={() => undefined} onCreate={() => undefined} />);
    expect(screen.getByText('203.0.113.24:25565')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Copy Internet address' }));
    expect(calls).toContainEqual({
      method: 'connectivity.copyAddress',
      params: { serverId: current.servers[0].id, kind: 'router' }
    });
    expect(screen.queryByText('Friends can join')).toBeNull();
  });
});
