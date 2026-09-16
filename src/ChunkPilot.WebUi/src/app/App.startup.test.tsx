// @vitest-environment jsdom
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import type { ReactNode } from 'react';
import type { BridgeRequest } from '../bridge/types';
import { useAppStore } from '../state/store';
import App from './App';

const state = vi.hoisted(() => ({
  fixtureAllowed: false,
  fixtureLoads: 0,
  fixtureGate: null as Promise<void> | null,
  fixtureNames: [] as string[],
  fixtureMethods: [] as string[],
  fixtureDispose: vi.fn(),
  routeLoads: { servers: 0, settings: 0, create: 0 },
  snapshot: { revision: 1, selectedServerId: null, servers: [] }
}));
vi.mock('../fixtures/mode', () => ({ isFixtureMode: () => state.fixtureAllowed }));
vi.mock('../fixtures/catalog', async () => {
  state.fixtureLoads += 1;
  await state.fixtureGate;
  return { FixtureBridge: class {
    constructor(name: string) { state.fixtureNames.push(name); }
    async request(method: string) { state.fixtureMethods.push(method); return method === 'snapshot.get' ? state.snapshot : {}; }
    subscribe() { return () => undefined; }
    dispose() { state.fixtureDispose(); }
  } };
});
vi.mock('../features/ServerWorkspace', () => { state.routeLoads.servers += 1; return { ServerWorkspace: () => <h1>Server workspace</h1> }; });
vi.mock('../features/SettingsPage', () => { state.routeLoads.settings += 1; return { SettingsPage: () => <h1>Settings view</h1> }; });
vi.mock('../features/CreateServer', () => { state.routeLoads.create += 1; return { CreateServerPage: () => <h1>Create view</h1> }; });
vi.mock('../features/Pages', () => ({
  DashboardPage: () => <h1>Dashboard view</h1>, ServersPage: () => <h1>Server library</h1>,
  ActivityPage: () => null, AutomationPage: () => null, DesignGalleryPage: () => null
}));
vi.mock('./Shell', () => ({ Shell: ({ children, onRoute }: { children: ReactNode; onRoute: (route: string) => void }) =>
  <main><button onClick={() => onRoute('settings')}>Open settings</button>{children}</main> }));

let sent: BridgeRequest[];
let handlers: Set<(event: MessageEvent) => void>;
async function flush() { await act(async () => { await Promise.resolve(); }); }
async function reply(method: string, result: unknown) {
  const request = sent.find(item => item.method === method)!;
  await act(async () => {
    for (const handler of handlers) handler({ data: { protocolVersion: 1, id: request.id, ok: true, result } } as MessageEvent);
  });
}
beforeEach(() => {
  vi.useFakeTimers();
  state.fixtureAllowed = false; state.fixtureGate = null;
  state.fixtureNames = []; state.fixtureMethods = []; state.fixtureDispose.mockClear();
  sent = []; handlers = new Set();
  vi.stubGlobal('chrome', { webview: {
    addEventListener: (_: string, handler: (event: MessageEvent) => void) => handlers.add(handler),
    removeEventListener: (_: string, handler: (event: MessageEvent) => void) => handlers.delete(handler),
    postMessage: (request: BridgeRequest) => sent.push(request)
  } });
  window.history.replaceState({}, '', '/?fixture=running&page=dashboard');
  useAppStore.setState({ snapshot: null, bridge: null, busy: new Set(), pendingOperations: new Map(),
    completedOperations: new Set(), serverSnapshots: new Map(), cachedPresentationServerId: null, error: null });
});
afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.useRealTimers(); });

it('keeps fixture data and unused pages unloaded during native startup, and preserves the handshake order', async () => {
  render(<App />);
  await flush();
  expect(state.fixtureLoads).toBe(0);
  expect(sent.map(item => item.method)).toEqual(['renderer.ready']);
  await reply('renderer.ready', {});
  expect(sent.map(item => item.method)).toEqual(['renderer.ready', 'snapshot.get']);
  await reply('snapshot.get', state.snapshot);
  expect(screen.getByRole('heading', { name: 'Dashboard view' })).toBeTruthy();
  await act(async () => { await vi.advanceTimersByTimeAsync(1_000); });
  expect(state.fixtureLoads).toBe(0);
  expect(state.routeLoads).toEqual({ servers: 0, settings: 0, create: 0 });
  fireEvent.click(screen.getByRole('button', { name: 'Open settings' }));
  await flush();
  expect(screen.getByRole('heading', { name: 'Settings view' })).toBeTruthy();
  expect(state.routeLoads).toEqual({ servers: 0, settings: 1, create: 0 });
});

it('disposes a pending native initialization without applying its late snapshot', async () => {
  const view = render(<App />);
  await flush();
  await reply('renderer.ready', {});
  expect(sent.at(-1)?.method).toBe('snapshot.get');
  view.unmount();
  expect(handlers.size).toBe(0);
  await reply('snapshot.get', state.snapshot);
  expect(useAppStore.getState().snapshot).toBeNull();
});

it('disposes a fixture adapter loaded after unmount without starting its handshake', async () => {
  state.fixtureAllowed = true;
  let release!: () => void;
  state.fixtureGate = new Promise<void>(resolve => { release = resolve; });
  const view = render(<App />);
  await flush();
  expect(state.fixtureLoads).toBe(1);
  view.unmount();
  await act(async () => { release(); });
  await flush();
  expect(state.fixtureNames).toEqual(['running']);
  expect(state.fixtureDispose).toHaveBeenCalledOnce();
  expect(state.fixtureMethods).toEqual([]);
  expect(sent).toEqual([]);
  expect(useAppStore.getState().snapshot).toBeNull();
});

it('initializes the allowed fixture harness through the same ready-then-snapshot contract', async () => {
  state.fixtureAllowed = true;
  render(<App />);
  await flush();
  expect(state.fixtureNames).toEqual(['running']);
  expect(state.fixtureMethods).toEqual(['renderer.ready', 'snapshot.get']);
  expect(sent).toEqual([]);
  expect(useAppStore.getState().snapshot).toEqual(state.snapshot);
  expect(screen.getByRole('heading', { name: 'Dashboard view' })).toBeTruthy();
});
