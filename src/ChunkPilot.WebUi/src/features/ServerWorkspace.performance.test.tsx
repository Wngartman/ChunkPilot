// @vitest-environment jsdom
import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { BridgeAdapter } from '../bridge/client';
import type { BridgeMethod } from '../bridge/types';
import { NavigationGuardProvider } from '../app/NavigationGuard';
import { fixtures } from '../fixtures/catalog';
import { useAppStore } from '../state/store';
import { ServerWorkspace } from './ServerWorkspace';

const calls: BridgeMethod[] = [];
const bridge: BridgeAdapter = {
  request: async <T,>(method: BridgeMethod) => { calls.push(method); return {} as T; },
  subscribe: () => () => undefined, dispose: () => undefined
};
beforeEach(() => { calls.length = 0; window.history.replaceState({}, '', '/?tab=players'); });
afterEach(() => { cleanup(); vi.restoreAllMocks(); window.localStorage.clear(); Reflect.deleteProperty(HTMLElement.prototype, 'scrollTo'); });

describe('bounded workspace rendering', () => {
  it.each([
    ['Starting', 'Server starting', 'Waiting for the server to become ready and report real samples.'],
    ['Stopped', 'Server stopped', 'Start the server to collect performance data.'],
    ['Restarting', 'Server restarting', 'Waiting for the restart to finish and real samples to arrive.'],
    ['Stopping', 'Server stopping', 'No performance samples are available while the server stops.'],
    ['Crashed', 'Server crashed', 'The server exited unexpectedly. Review the console before starting it again.'],
    ['Unresponsive', 'Server unresponsive', 'The server is not responding; no performance samples are available.'],
    ['Unknown', 'Server state unknown', 'ChunkPilot has not confirmed the server state or received enough real samples.']
  ] as const)('describes %s without inventing performance or a stopped state', (state, status, detail) => {
    window.history.replaceState({}, '', '/?tab=overview');
    const current = structuredClone(fixtures.starting);
    current.servers[0].state = state;
    current.servers[0].cpuPercent = null;
    current.servers[0].samples = [];
    useAppStore.setState({ snapshot: current, bridge, busy: new Set(), error: null });
    render(<NavigationGuardProvider><ServerWorkspace serverId={current.servers[0].id} /></NavigationGuardProvider>);
    const surface = screen.getByText('Live performance').closest('section')!;
    expect(within(surface).getByText(status)).toBeTruthy();
    expect(within(surface).getByText(detail)).toBeTruthy();
    expect(within(surface).getByText('No performance data')).toBeTruthy();
    if (state !== 'Stopped') {
      expect(within(surface).queryByText('Server stopped')).toBeNull();
      expect(within(surface).queryByText('Start the server to collect performance data.')).toBeNull();
    }
    if (state === 'Starting') expect(screen.getByText('Preparing the world')).toBeTruthy();
  });

  it('keeps a 2000-line native-sized console buffer virtualized while searching and appending', () => {
    window.history.replaceState({}, '', '/?tab=console&mode=console-unwrapped');
    vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockReturnValue(540);
    vi.spyOn(HTMLElement.prototype, 'offsetWidth', 'get').mockReturnValue(1000);
    Object.defineProperty(HTMLElement.prototype, 'scrollTo', { configurable: true, value: () => undefined });
    const current = structuredClone(fixtures.running);
    current.console = Array.from({ length: 2000 }, (_, index) => ({ sequence: index + 1,
      timestamp: '2026-09-14T21:00:00Z', stream: 'INFO', text: `Line ${index}: ${'bounded fixture console output '.repeat(12)}` }));
    useAppStore.setState({ snapshot: current, bridge, busy: new Set(), error: null });
    const started = performance.now();
    const view = render(<NavigationGuardProvider><ServerWorkspace serverId={current.servers[0].id} /></NavigationGuardProvider>);
    const rows = view.container.querySelectorAll('[data-index]').length;
    console.info(`CONSOLE_RENDER_METRICS=${JSON.stringify({ bufferedLines: 2000, renderMs: Number((performance.now() - started).toFixed(3)), renderedRows: rows })}`);
    expect(rows).toBeGreaterThan(0);
    expect(rows).toBeLessThan(100);
    const updated = { ...current, console: [...current.console.slice(1), { sequence: 2001, timestamp: '2026-09-14T21:00:01Z', stream: 'INFO', text: 'Exact newest retained line' }] };
    act(() => useAppStore.setState({ snapshot: updated }));
    fireEvent.change(screen.getByRole('searchbox', { name: 'Search console' }), { target: { value: 'Exact newest' } });
    expect(screen.getByText('Exact newest retained line')).toBeTruthy();
    expect(view.container.querySelectorAll('[data-index]').length).toBe(1);
    expect(calls).toEqual(['workspace.load']);
  });

  it('measures a 1000-player roster without contacting a remote provider', () => {
    const current = structuredClone(fixtures.running);
    current.players = Array.from({ length: 1000 }, (_, index) => ({
      name: `Player${String(index).padStart(4, '0')}`, uuid: `00000000-0000-0000-0000-${String(index).padStart(12, '0')}`,
      online: index < 12, allowlisted: true, operator: index === 0, banned: false
    }));
    useAppStore.setState({ snapshot: current, bridge, busy: new Set(), error: null });
    const started = performance.now();
    const view = render(<NavigationGuardProvider><ServerWorkspace serverId={current.servers[0].id} /></NavigationGuardProvider>);
    const elapsed = performance.now() - started;
    const rows = view.container.querySelectorAll('tbody > tr').length;
    const heads = calls.filter(method => method === 'players.head').length;
    console.info(`LARGE_ROSTER_METRICS=${JSON.stringify({ rosterSize: 1000, renderMs: Number(elapsed.toFixed(3)), renderedRows: rows, headRequests: heads })}`);
    expect(rows).toBe(50);
    expect(heads).toBe(50);
    expect(calls.every(method => method === 'workspace.load' || method === 'players.head')).toBe(true);
    fireEvent.click(screen.getByRole('button', { name: 'Next players' }));
    expect(screen.getByText('Player0050')).toBeTruthy();
    expect(screen.queryByText('Player0000')).toBeNull();
    expect(view.container.querySelectorAll('tbody > tr').length).toBe(50);
    fireEvent.change(screen.getByRole('searchbox', { name: 'Search players' }), { target: { value: 'Player0999' } });
    expect(screen.getByText('Player0999')).toBeTruthy();
    expect(view.container.querySelectorAll('tbody > tr').length).toBe(1);
    expect(screen.queryByRole('button', { name: 'Next players' })).toBeNull();
  });
});
