// @vitest-environment jsdom
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { NavigationGuardProvider } from '../app/NavigationGuard';
import type { BridgeAdapter } from '../bridge/client';
import type { BridgeMethod, TextFileContent } from '../bridge/types';
import { FixtureBridge, fixtures } from '../fixtures/catalog';
import { useAppStore } from '../state/store';
import { ServerWorkspace } from './ServerWorkspace';

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>(complete => { resolve = complete; });
  return { promise, resolve };
}

beforeEach(() => {
  window.history.replaceState({}, '', '/?tab=files');
});
afterEach(cleanup);

describe('server file workspace', () => {
  it('ignores a pending child-folder read after navigating Up', async () => {
    const pending = deferred<TextFileContent>();
    const calls: { method: BridgeMethod; params: Record<string, unknown> }[] = [];
    const bridge: BridgeAdapter = {
      request: async <T,>(method: BridgeMethod, params: Record<string, unknown> = {}) => {
        calls.push({ method, params });
        if (method === 'files.read') return await pending.promise as T;
        return { accepted: true } as T;
      },
      subscribe: () => () => undefined,
      dispose: () => undefined
    };
    const current = structuredClone(fixtures.running);
    current.currentFolder = 'config';
    current.files = [{ name: 'example.properties', relativePath: 'config/example.properties', kind: 'editable', sizeBytes: 120, modifiedAt: null }];
    useAppStore.setState({ snapshot: current, bridge, busy: new Set(), error: null });
    render(<NavigationGuardProvider><ServerWorkspace serverId={current.servers[0].id} /></NavigationGuardProvider>);
    fireEvent.click(screen.getByRole('button', { name: 'example.properties' }));
    expect(screen.getByText('Loading file')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Up' }));
    expect(calls).toContainEqual({ method: 'files.navigate', params: { serverId: current.servers[0].id, relativePath: '' } });
    await act(async () => {
      pending.resolve({ relativePath: 'config/example.properties', content: 'motd=Late child read', encodingName: 'utf-8', hasBom: false, lineEnding: '\n', loadedSha256: 'child-hash', loadedLastWriteAt: null });
      await pending.promise;
    });
    expect(screen.queryByRole('textbox', { name: 'Edit config/example.properties' })).toBeNull();
    expect(screen.queryByDisplayValue('motd=Late child read')).toBeNull();
    expect(screen.queryByText('Loading file')).toBeNull();
    expect(screen.getByText('No file selected')).toBeTruthy();
  });

  it('focuses a loaded editor and preserves its unsaved draft when leaving is cancelled', async () => {
    const current = structuredClone(fixtures.running);
    useAppStore.setState({ snapshot: current, bridge: new FixtureBridge('running'), busy: new Set(), error: null });
    render(<NavigationGuardProvider><ServerWorkspace serverId={current.servers[0].id} /></NavigationGuardProvider>);
    fireEvent.click(screen.getByRole('button', { name: 'server.properties' }));
    const editor = await screen.findByRole('textbox', { name: 'Edit server.properties' });
    await waitFor(() => expect(document.activeElement).toBe(editor));
    fireEvent.change(editor, { target: { value: 'motd=Unsaved local draft' } });
    fireEvent.click(screen.getByRole('button', { name: 'whitelist.json' }));
    expect(await screen.findByRole('alertdialog')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(screen.getByDisplayValue('motd=Unsaved local draft')).toBeTruthy();
    expect(screen.queryByRole('textbox', { name: 'Edit whitelist.json' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'whitelist.json' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Discard and leave' }));
    expect(await screen.findByRole('textbox', { name: 'Edit whitelist.json' })).toBeTruthy();
  });

  it('does not let a late file read replace the newer selected file', async () => {
    const properties = deferred<TextFileContent>();
    const whitelist = deferred<TextFileContent>();
    const bridge: BridgeAdapter = {
      request: async <T,>(method: BridgeMethod, params: Record<string, unknown> = {}) => {
        if (method === 'files.read') {
          return await (params.relativePath === 'server.properties' ? properties.promise : whitelist.promise) as T;
        }
        return { accepted: true } as T;
      },
      subscribe: () => () => undefined,
      dispose: () => undefined
    };
    const current = structuredClone(fixtures.running);
    useAppStore.setState({ snapshot: current, bridge, busy: new Set(), error: null });
    const server = current.servers[0];
    render(<NavigationGuardProvider><ServerWorkspace serverId={server.id} /></NavigationGuardProvider>);

    fireEvent.click(screen.getByRole('button', { name: 'server.properties' }));
    fireEvent.click(screen.getByRole('button', { name: 'whitelist.json' }));
    whitelist.resolve({ relativePath: 'whitelist.json', content: '["FixturePlayer"]', encodingName: 'utf-8', hasBom: false, lineEnding: '\n', loadedSha256: 'whitelist-hash', loadedLastWriteAt: null });
    await waitFor(() => expect(screen.getByLabelText('Edit whitelist.json')).toBeTruthy());

    await act(async () => {
      properties.resolve({ relativePath: 'server.properties', content: 'motd=Late stale file', encodingName: 'utf-8', hasBom: false, lineEnding: '\n', loadedSha256: 'properties-hash', loadedLastWriteAt: null });
      await properties.promise;
    });

    expect(screen.getByLabelText('Edit whitelist.json')).toBeTruthy();
    expect(screen.queryByLabelText('Edit server.properties')).toBeNull();
    expect(screen.queryByDisplayValue('motd=Late stale file')).toBeNull();
  });
});
