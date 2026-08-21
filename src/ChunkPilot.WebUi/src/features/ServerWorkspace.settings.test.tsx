// @vitest-environment jsdom
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { NavigationGuardProvider } from '../app/NavigationGuard';
import type { BridgeAdapter } from '../bridge/client';
import type { BridgeMethod } from '../bridge/types';
import { fixtures } from '../fixtures/catalog';
import { useAppStore } from '../state/store';
import { measureConsoleRow, ServerWorkspace } from './ServerWorkspace';

const calls: { method: BridgeMethod; params: Record<string, unknown> }[] = [];
const bridge: BridgeAdapter = {
  request: async <T,>(method: BridgeMethod, params: Record<string, unknown> = {}) => { calls.push({ method, params }); return { accepted: true } as T; },
  subscribe: () => () => undefined,
  dispose: () => undefined
};

beforeEach(() => {
  calls.length = 0;
  window.localStorage.clear();
  const current = structuredClone(fixtures.running);
  useAppStore.setState({ snapshot: current, bridge, busy: new Set(), error: null });
});
afterEach(() => { cleanup(); window.history.replaceState({}, '', '/'); });

function workspace(query: string) {
  window.history.replaceState({}, '', query);
  const server = useAppStore.getState().snapshot!.servers[0];
  const view = render(<NavigationGuardProvider><ServerWorkspace serverId={server.id} /></NavigationGuardProvider>);
  return { server, view };
}

describe('server settings and console acceptance behavior', () => {
  it('rounds scaled console row measurements up and keeps a separation pixel', () => {
    const element = document.createElement('div');
    element.getBoundingClientRect = () => ({ height: 47.2 } as DOMRect);
    Object.defineProperty(element, 'scrollHeight', { value: 61 });
    expect(measureConsoleRow(element)).toBe(62);
  });

  it('uses canonical difficulty options and keeps a legacy value until the user changes it', () => {
    const current = structuredClone(fixtures.running);
    current.serverSettings!.difficulty = 'custom-legacy';
    useAppStore.setState({ snapshot: current });
    workspace('/?tab=settings&settings=Gameplay');
    const difficulty = screen.getByRole('combobox', { name: 'Difficulty' }) as HTMLSelectElement;
    expect(difficulty.value).toBe('custom-legacy');
    expect(screen.getByRole('option', { name: 'Custom value: custom-legacy' })).toBeTruthy();
    fireEvent.change(difficulty, { target: { value: 'hard' } });
    expect(difficulty.value).toBe('hard');
    fireEvent.click(screen.getByRole('button', { name: 'Discard' }));
    expect(difficulty.value).toBe('custom-legacy');
  });

  it('never carries a settings draft into a newly selected server before its snapshot arrives', async () => {
    const current = structuredClone(fixtures.running);
    const first = current.servers[0];
    const second = { ...structuredClone(first), id: '22222222-2222-2222-2222-222222222222', name: first.name };
    const third = { ...structuredClone(first), id: '33333333-3333-3333-3333-333333333333', name: 'Third server' };
    current.servers.push(second, third);
    current.selectedServerId = first.id;
    useAppStore.setState({ snapshot: current });
    window.history.replaceState({}, '', '/?tab=settings&settings=Appearance');
    const view = render(<NavigationGuardProvider><ServerWorkspace serverId={first.id} /></NavigationGuardProvider>);
    fireEvent.click(screen.getByRole('button', { name: 'Raw' }));
    fireEvent.input(screen.getByLabelText('Raw Vanilla MOTD'), { target: { value: 'Unsaved first-server text' } });

    const switched = structuredClone(current);
    switched.selectedServerId = second.id;
    act(() => useAppStore.getState().applySnapshot(switched));
    view.rerender(<NavigationGuardProvider><ServerWorkspace serverId={second.id} /></NavigationGuardProvider>);

    expect(screen.getByText('Settings unavailable')).toBeTruthy();
    expect(screen.queryByDisplayValue('Unsaved first-server text')).toBeNull();

    const authoritativeSecond = structuredClone(switched);
    authoritativeSecond.revision += 1;
    authoritativeSecond.serverSettings = { ...current.serverSettings!, serverId: second.id, name: second.name, motd: 'Second server MOTD' };
    act(() => useAppStore.getState().applySnapshot(authoritativeSecond));
    await waitFor(() => expect(screen.queryByText('Settings unavailable')).toBeNull());
    fireEvent.click(screen.getByRole('button', { name: 'Raw' }));
    expect(screen.getByDisplayValue('Second server MOTD')).toBeTruthy();
    expect(screen.queryByDisplayValue('Unsaved first-server text')).toBeNull();
  });

  it('keeps a delayed save bound to its original server while selection changes', async () => {
    const current = structuredClone(fixtures.running);
    const first = current.servers[0];
    const second = { ...structuredClone(first), id: '22222222-2222-2222-2222-222222222222', name: 'Second server' };
    current.servers.push(second);
    let finishSave!: () => void;
    const savePending = new Promise<void>(resolve => { finishSave = resolve; });
    const delayedBridge: BridgeAdapter = {
      request: async <T,>(method: BridgeMethod, params: Record<string, unknown> = {}) => {
        calls.push({ method, params });
        if (method === 'settings.saveServer') { await savePending; return { accepted: true } as T; }
        return { accepted: true } as T;
      },
      subscribe: () => () => undefined,
      dispose: () => undefined
    };
    useAppStore.setState({ snapshot: current, bridge: delayedBridge });
    window.history.replaceState({}, '', '/?tab=settings&settings=Appearance');
    const view = render(<NavigationGuardProvider><ServerWorkspace serverId={first.id} /></NavigationGuardProvider>);
    fireEvent.click(screen.getByRole('button', { name: 'Raw' }));
    fireEvent.input(screen.getByLabelText('Raw Vanilla MOTD'), { target: { value: 'Saved only for the first server' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
    expect(calls.find(call => call.method === 'settings.saveServer')?.params.serverId).toBe(first.id);

    const switched = structuredClone(current);
    switched.selectedServerId = second.id;
    act(() => useAppStore.getState().applySnapshot(switched));
    view.rerender(<NavigationGuardProvider><ServerWorkspace serverId={second.id} /></NavigationGuardProvider>);
    expect(screen.getByText('Settings unavailable')).toBeTruthy();
    await act(async () => { finishSave(); await savePending; });
    expect(screen.queryByDisplayValue('Saved only for the first server')).toBeNull();
  });

  it('keeps a failed MOTD save editable and dirty for a safe retry', async () => {
    const failingBridge: BridgeAdapter = {
      request: async <T,>(method: BridgeMethod) => {
        if (method === 'settings.saveServer') throw new Error('Synthetic save failure');
        return { accepted: true } as T;
      },
      subscribe: () => () => undefined,
      dispose: () => undefined
    };
    useAppStore.setState({ bridge: failingBridge });
    workspace('/?tab=settings&settings=Appearance');
    fireEvent.click(screen.getByRole('button', { name: 'Raw' }));
    fireEvent.input(screen.getByLabelText('Raw Vanilla MOTD'), { target: { value: 'Keep this failed draft' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));

    await waitFor(() => expect(useAppStore.getState().error).toBe('Synthetic save failure'));
    expect(screen.getByDisplayValue('Keep this failed draft')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Save changes' })).toBeTruthy();
  });

  it('wraps long console lines by default and persists the user toggle', () => {
    const { view } = workspace('/?tab=console');
    const toggle = screen.getByRole('checkbox', { name: 'Wrap long lines' }) as HTMLInputElement;
    expect(toggle.checked).toBe(true);
    expect(view.container.querySelector('[data-wrap]')?.getAttribute('data-wrap')).toBe('true');
    fireEvent.click(toggle);
    expect(toggle.checked).toBe(false);
    expect(window.localStorage.getItem('chunkpilot.console.wrap')).toBe('false');
    expect(view.container.querySelector('[data-wrap]')?.getAttribute('data-wrap')).toBe('false');
  });
});
