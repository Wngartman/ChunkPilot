// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { BridgeAdapter } from '../bridge/client';
import type { BridgeMethod } from '../bridge/types';
import { FixtureBridge, fixtures } from '../fixtures/catalog';
import { useAppStore } from '../state/store';
import { CreateServerPage } from './CreateServer';

const calls: { method: BridgeMethod; params: Record<string, unknown> }[] = [];

beforeEach(() => {
  calls.length = 0;
  window.sessionStorage.clear();
  window.history.replaceState({}, '', '/?fixture=running&page=create&stage=4');
  useAppStore.setState({ snapshot: structuredClone(fixtures.running), bridge: null, busy: new Set(),
    pendingOperations: new Map(), completedOperations: new Set(), error: null });
});
afterEach(() => { cleanup(); vi.restoreAllMocks(); });

describe('existing world creation', () => {
  it('reviews a native world without exposing its path and sends only the one-time token', async () => {
    vi.spyOn(globalThis.crypto, 'randomUUID').mockReturnValue('88888888-8888-4888-8888-888888888888');
    const fixture = new FixtureBridge('running');
    const bridge: BridgeAdapter = {
      request: async <T,>(method: BridgeMethod, params: Record<string, unknown> = {}) => {
        calls.push({ method, params });
        if (method === 'creation.chooseWorld') return {
          cancelled: false,
          token: 'opaque-world-token',
          displayName: 'Copper Harbor Backup',
          kind: 'Folder',
          worldName: 'Copper Harbor',
          sourceSizeBytes: 4096,
          expandedSizeBytes: 4096,
          fileCount: 18,
          includesNether: true,
          includesEnd: true,
          expiresAt: '2026-08-21T22:00:00Z'
        } as T;
        return fixture.request<T>(method, params);
      },
      subscribe: listener => fixture.subscribe(listener),
      dispose: () => fixture.dispose()
    };
    useAppStore.setState({ bridge });
    render(<CreateServerPage onDone={() => undefined} />);

    fireEvent.click(screen.getByRole('button', { name: /Upload World/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Choose world folder' }));
    expect(await screen.findByText('Copper Harbor')).toBeTruthy();
    expect(screen.getByText(/Nether \+ End included/)).toBeTruthy();
    expect(calls).toContainEqual({ method: 'creation.chooseWorld', params: { kind: 'folder' } });

    fireEvent.click(screen.getByRole('button', { name: /Continue/ }));
    fireEvent.click(screen.getByRole('button', { name: /Continue/ }));
    expect(await screen.findByText(/Copper Harbor · copied from folder · source preserved/)).toBeTruthy();
    fireEvent.click(screen.getByRole('checkbox'));
    fireEvent.click(screen.getByRole('button', { name: 'Create server' }));

    await waitFor(() => expect(calls.some(call => call.method === 'creation.begin')).toBe(true));
    const begin = calls.find(call => call.method === 'creation.begin')!;
    expect(begin.params.initialWorldToken).toBe('opaque-world-token');
    expect(Object.values(begin.params)).not.toContain('C:\\Users\\someone\\world');
  });
});
