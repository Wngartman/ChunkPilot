// @vitest-environment jsdom
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { NavigationGuardProvider } from '../app/NavigationGuard';
import type { BridgeAdapter } from '../bridge/client';
import type { BridgeMethod, TextFileContent } from '../bridge/types';
import { fixtures } from '../fixtures/catalog';
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
