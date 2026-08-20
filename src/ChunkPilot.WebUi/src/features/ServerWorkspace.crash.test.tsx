// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { NavigationGuardProvider } from '../app/NavigationGuard';
import type { BridgeAdapter } from '../bridge/client';
import type { BridgeMethod } from '../bridge/types';
import { fixtures } from '../fixtures/catalog';
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
  window.history.replaceState({}, '', '/?tab=overview');
  useAppStore.setState({ snapshot: structuredClone(fixtures.attention), bridge, busy: new Set(), error: null });
});
afterEach(cleanup);

describe('automatic crash analysis', () => {
  it('shows bounded local evidence without claiming a regex match is confirmed', () => {
    const server = useAppStore.getState().snapshot!.servers[0];
    render(<NavigationGuardProvider><ServerWorkspace serverId={server.id} /></NavigationGuardProvider>);

    expect(screen.getByText('Highly Likely')).toBeTruthy();
    expect(screen.queryByText('Confirmed')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'View analysis' }));
    const dialog = screen.getByRole('dialog', { name: 'Crash analysis' });
    expect(within(dialog).getByText('Local evidence')).toBeTruthy();
    expect(within(dialog).getByText('Console tail')).toBeTruthy();
    expect(within(dialog).getByText('Latest log')).toBeTruthy();
    expect(within(dialog).getByText(/FAILED TO BIND TO PORT/)).toBeTruthy();
  });

  it('routes only allowlisted safe recovery actions through the bridge', () => {
    const server = useAppStore.getState().snapshot!.servers[0];
    render(<NavigationGuardProvider><ServerWorkspace serverId={server.id} /></NavigationGuardProvider>);
    fireEvent.click(screen.getByRole('button', { name: 'View analysis' }));
    const dialog = screen.getByRole('dialog', { name: 'Crash analysis' });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Open logs' }));
    fireEvent.click(within(dialog).getByRole('button', { name: 'Create support bundle' }));
    fireEvent.click(within(dialog).getByRole('button', { name: 'Retry start' }));

    expect(calls).toContainEqual({ method: 'diagnostics.openLogs', params: { serverId: server.id } });
    expect(calls).toContainEqual({ method: 'diagnostics.bundle', params: { serverId: server.id } });
    expect(calls).toContainEqual({ method: 'servers.start', params: { serverId: server.id } });
  });
});
