// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
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
