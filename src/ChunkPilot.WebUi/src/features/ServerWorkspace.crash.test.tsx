// @vitest-environment jsdom
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { NavigationGuardProvider } from '../app/NavigationGuard';
import type { BridgeAdapter } from '../bridge/client';
import { fixtures } from '../fixtures/catalog';
import { useAppStore } from '../state/store';
import { ServerWorkspace } from './ServerWorkspace';

const bridge: BridgeAdapter = { request: async <T,>() => ({ accepted: true }) as T, subscribe: () => () => undefined, dispose: () => undefined };

beforeEach(() => {
  window.localStorage.clear();
  window.history.replaceState({}, '', '/?tab=overview');
  useAppStore.setState({ snapshot: structuredClone(fixtures.attention), bridge, busy: new Set(), error: null });
});
afterEach(cleanup);

describe('native server health issues', () => {
  it('shows one evidence-backed issue and routes troubleshooting to its article', () => {
    const server = useAppStore.getState().snapshot!.servers[0];
    const openHelp = vi.fn();
    render(<NavigationGuardProvider><ServerWorkspace serverId={server.id} onOpenHelp={openHelp} /></NavigationGuardProvider>);
    expect(screen.getByText('Port 25565 is already in use')).toBeTruthy();
    expect(screen.getByText(/Evidence: FAILED TO BIND TO PORT/)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Troubleshoot' }));
    expect(openHelp).toHaveBeenCalledWith('port-binding-failed');
  });

  it('dismisses one evidence fingerprint and shows a genuinely new occurrence', () => {
    const server = useAppStore.getState().snapshot!.servers[0];
    render(<NavigationGuardProvider><ServerWorkspace serverId={server.id} /></NavigationGuardProvider>);
    fireEvent.click(screen.getByRole('button', { name: /Dismiss Port 25565/ }));
    expect(screen.queryByText('Port 25565 is already in use')).toBeNull();
    const next = structuredClone(useAppStore.getState().snapshot!);
    next.revision += 1;
    next.issues[0].evidenceFingerprint += ':new-process';
    act(() => useAppStore.getState().applySnapshot(next));
    expect(screen.getByText('Port 25565 is already in use')).toBeTruthy();
  });

  it('shows no health banner for a normal stopped server', () => {
    const current = structuredClone(fixtures.stopped);
    current.issues = [];
    useAppStore.setState({ snapshot: current });
    const server = current.servers[0];
    render(<NavigationGuardProvider><ServerWorkspace serverId={server.id} /></NavigationGuardProvider>);
    expect(screen.queryByLabelText(`Current issues for ${server.name}`)).toBeNull();
  });
});
