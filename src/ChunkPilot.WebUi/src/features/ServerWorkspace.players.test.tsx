// @vitest-environment jsdom
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import userEvent from '@testing-library/user-event';
import type { BridgeAdapter } from '../bridge/client';
import type { BridgeMethod } from '../bridge/types';
import { NavigationGuardProvider } from '../app/NavigationGuard';
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
  window.history.replaceState({}, '', '/?tab=players');
  useAppStore.setState({ snapshot: structuredClone(fixtures.running), bridge, busy: new Set(), error: null });
});
afterEach(cleanup);

function renderWorkspace() {
  const server = useAppStore.getState().snapshot!.servers[0];
  render(<NavigationGuardProvider><ServerWorkspace serverId={server.id} /></NavigationGuardProvider>);
  return server;
}

describe('Minecraft players workspace', () => {
  it('never renders the previous server roster while a new authoritative selection is pending', () => {
    const current = structuredClone(fixtures.running);
    const previous = current.servers[0];
    const next = structuredClone(previous);
    next.id = 'server-b';
    next.name = 'Server B';
    current.servers.push(next);
    current.selectedServerId = previous.id;
    current.players[0].name = 'PreviousServerPlayer';
    useAppStore.setState({ snapshot: current });

    render(<NavigationGuardProvider><ServerWorkspace serverId={next.id} /></NavigationGuardProvider>);

    expect(screen.getByRole('status').textContent).toContain('Opening Server B');
    expect(screen.queryByText('PreviousServerPlayer')).toBeNull();
    expect(calls.some(call => call.method === 'workspace.load' && call.params.serverId === next.id)).toBe(false);

    const authoritative = structuredClone(current);
    authoritative.revision += 1;
    authoritative.selectedServerId = next.id;
    authoritative.playerAccess = { ...authoritative.playerAccess!, serverId: next.id };
    authoritative.players = [{ ...authoritative.players[0], name: 'ServerBPlayer' }];
    act(() => useAppStore.getState().applySnapshot(authoritative));

    expect(screen.getByText('ServerBPlayer')).toBeTruthy();
    expect(screen.queryByText('PreviousServerPlayer')).toBeNull();
  });

  it('remains available to a Minecraft server whose ecosystem is unknown or custom', () => {
    const current = structuredClone(fixtures.running);
    current.servers[0].ecosystem = 'Custom';
    current.servers[0].gameKind = 'Minecraft';
    current.servers[0].capabilities.players = true;
    useAppStore.setState({ snapshot: current });
    renderWorkspace();
    expect(screen.getByRole('button', { name: 'Players' }).getAttribute('aria-current')).toBe('page');
    expect(screen.getByText('MapleRook')).toBeTruthy();
  });

  it('uses player-facing Whitelist terminology while preserving the authoritative bridge command', () => {
    const server = renderWorkspace();
    fireEvent.change(screen.getByLabelText('Minecraft player name'), { target: { value: 'NewPlayer' } });
    fireEvent.click(screen.getAllByRole('button', { name: 'Add to whitelist' })[0]);
    expect(calls).toContainEqual({ method: 'players.addAllowlist', params: { serverId: server.id, playerName: 'NewPlayer' } });
    expect(screen.queryByText(/allowlist/i)).toBeNull();
  });

  it('changes the server-wide allowlist through the authoritative bridge command', () => {
    const server = renderWorkspace();
    fireEvent.click(screen.getByRole('switch', { name: 'Turn whitelist off' }));
    expect(calls).toContainEqual({ method: 'players.setWhitelist', params: { serverId: server.id, enabled: false } });
  });

  it('portals player moderation actions outside the clipped table surface', async () => {
    const user = userEvent.setup();
    renderWorkspace();
    const table = screen.getByRole('table');
    await user.click(screen.getByRole('button', { name: 'Moderation actions for MapleRook' }));
    const item = await screen.findByRole('menuitem', { name: 'Remove operator' });
    expect(item).toBeTruthy();
    expect(table.contains(item)).toBe(false);
  });

  it('renders a native-fetched authoritative player head and leaves missing UUIDs on a local fallback', async () => {
    const imageUrl = 'data:image/png;base64,aGVhZA==';
    const headBridge: BridgeAdapter = {
      ...bridge,
      request: async <T,>(method: BridgeMethod, params: Record<string, unknown> = {}) => {
        calls.push({ method, params });
        if (method === 'players.head') return { serverId: params.serverId, uuid: params.uuid, imageUrl } as T;
        return { accepted: true } as T;
      }
    };
    useAppStore.setState({ bridge: headBridge });
    renderWorkspace();
    await waitFor(() => expect(document.querySelector(`img[src="${imageUrl}"]`)).toBeTruthy());
    expect(calls.filter(call => call.method === 'players.head')).toHaveLength(1);
    expect(screen.getByText('CI')).toBeTruthy();
  });

  it('keeps known access records visible while stopped and never turns unknown into zero', () => {
    const current = structuredClone(fixtures.running);
    current.servers[0].state = 'Stopped';
    current.servers[0].playersOnline = null;
    current.servers[0].playersMaximum = null;
    current.servers[0].playerStatus = { online: null, maximum: null, source: 'StatusCheckFailed', exact: false, checkedAt: '2026-08-14T16:42:00-06:00', detail: 'Live status is unavailable while the server is stopped.' };
    current.playerAccess!.serverRunning = false;
    useAppStore.setState({ snapshot: current });
    renderWorkspace();
    expect(screen.getByText('Unknown')).toBeTruthy();
    expect(screen.getByText('MapleRook')).toBeTruthy();
    expect((screen.getAllByRole('button', { name: 'Add to whitelist' })[0] as HTMLButtonElement).disabled).toBe(true);
  });

  it('does not expose Minecraft player controls for Terraria', () => {
    const current = structuredClone(fixtures.running);
    current.servers[0].gameKind = 'Terraria';
    current.servers[0].capabilities.players = false;
    current.playerAccess = null;
    useAppStore.setState({ snapshot: current });
    window.history.replaceState({}, '', '/');
    renderWorkspace();
    expect(screen.queryByRole('button', { name: 'Players' })).toBeNull();
  });

  it('keeps the stable Players route while an authoritative reconnect snapshot changes status', () => {
    renderWorkspace();
    expect(screen.getByRole('button', { name: 'Players' }).getAttribute('aria-current')).toBe('page');
    const next = structuredClone(fixtures.running);
    next.revision += 1;
    next.servers[0].state = 'Stopped';
    next.servers[0].playersOnline = null;
    next.servers[0].playerStatus = { online: null, maximum: null, source: 'StatusCheckFailed', exact: false, checkedAt: '2026-08-14T16:43:00-06:00', detail: 'The Agent reconnected while the server was stopped.' };
    next.playerAccess!.serverRunning = false;
    act(() => useAppStore.getState().applySnapshot(next));
    expect(screen.getByRole('button', { name: 'Players' }).getAttribute('aria-current')).toBe('page');
    expect(screen.getByText('Server stopped')).toBeTruthy();
    expect(screen.getByText('MapleRook')).toBeTruthy();
  });
});
