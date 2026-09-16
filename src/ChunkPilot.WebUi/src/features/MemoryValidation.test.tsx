// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { NavigationGuardProvider } from '../app/NavigationGuard';
import type { BridgeAdapter } from '../bridge/client';
import type { BridgeMethod } from '../bridge/types';
import { FixtureBridge, fixtures } from '../fixtures/catalog';
import { useAppStore } from '../state/store';
import { CreateServerPage } from './CreateServer';
import { ServerWorkspace } from './ServerWorkspace';

const calls: { method: BridgeMethod; params: Record<string, unknown> }[] = [];
beforeEach(() => {
  calls.length = 0;
  window.sessionStorage.clear();
  const fixture = new FixtureBridge('running');
  const bridge: BridgeAdapter = {
    request: async <T,>(method: BridgeMethod, params: Record<string, unknown> = {}) => {
      calls.push({ method, params });
      return fixture.request<T>(method, params);
    },
    subscribe: listener => fixture.subscribe(listener),
    dispose: () => fixture.dispose()
  };
  useAppStore.setState({ snapshot: structuredClone(fixtures.running), bridge, busy: new Set(),
    pendingOperations: new Map(), completedOperations: new Set(), error: null });
});
afterEach(() => { cleanup(); window.history.replaceState({}, '', '/'); });

function customMaximum() {
  fireEvent.change(screen.getByLabelText('Maximum server memory'), { target: { value: 'custom' } });
  return screen.getByLabelText('Custom memory in gigabytes');
}

function settings() {
  window.history.replaceState({}, '', '/?tab=settings&settings=Resources');
  const server = useAppStore.getState().snapshot!.servers[0];
  render(<NavigationGuardProvider><ServerWorkspace serverId={server.id} /></NavigationGuardProvider>);
}

describe('memory submission guards', () => {
  it('blocks both Continue and forward stage shortcuts for invalid creation memory', () => {
    window.history.replaceState({}, '', '/?fixture=running&page=create&stage=2');
    render(<CreateServerPage onDone={() => undefined} />);
    fireEvent.change(customMaximum(), { target: { value: '0' } });
    expect((screen.getByRole('button', { name: /Continue/ }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(screen.getByRole('button', { name: 'Step 6: Connectivity' }));
    expect(screen.getByRole('heading', { name: 'Performance' })).toBeTruthy();
    expect(calls.some(call => call.method === 'creation.begin')).toBe(false);
  });

  it('uses the backend minimum of 512 MiB for initial creation memory', () => {
    window.history.replaceState({}, '', '/?fixture=running&page=create&stage=2');
    render(<CreateServerPage onDone={() => undefined} />);
    fireEvent.change(screen.getByLabelText('Initial server memory'), { target: { value: 'custom' } });
    fireEvent.change(screen.getByLabelText('Custom memory in gigabytes'), { target: { value: '0.25' } });
    expect(screen.getByRole('alert').textContent).toContain('512');
    expect((screen.getByRole('button', { name: /Continue/ }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.change(screen.getByLabelText('Custom memory in gigabytes'), { target: { value: '0.5' } });
    expect((screen.getByRole('button', { name: /Continue/ }) as HTMLButtonElement).disabled).toBe(false);
  });

  it('keeps invalid settings dirty, blocks Save, and discards an unchanged numeric draft', () => {
    settings();
    fireEvent.change(customMaximum(), { target: { value: 'invalid' } });
    expect((screen.getByRole('button', { name: 'Save changes' }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
    expect(calls.some(call => call.method === 'settings.saveServer')).toBe(false);
    fireEvent.click(screen.getByRole('button', { name: 'Discard' }));
    expect(screen.queryByLabelText('Custom memory in gigabytes')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Save changes' })).toBeNull();
  });

  it('submits the most recent valid custom amount without depending on blur', async () => {
    settings();
    fireEvent.change(customMaximum(), { target: { value: '3.1416' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
    await waitFor(() => expect(calls.some(call => call.method === 'settings.saveServer')).toBe(true));
    expect(calls.find(call => call.method === 'settings.saveServer')!.params.maximumRamMb).toBe(3217);
  });

  it('blocks saving an initial heap greater than a newly lowered maximum', () => {
    const snapshot = structuredClone(fixtures.running);
    snapshot.serverSettings!.minimumRamMb = 2048;
    snapshot.serverSettings!.maximumRamMb = 4096;
    useAppStore.setState({ snapshot });
    settings();
    fireEvent.change(screen.getByLabelText('Maximum server memory'), { target: { value: '1024' } });
    expect((screen.getByRole('button', { name: 'Save changes' }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.change(screen.getByLabelText('Initial server memory'), { target: { value: '1024' } });
    expect((screen.getByRole('button', { name: 'Save changes' }) as HTMLButtonElement).disabled).toBe(false);
  });
});
