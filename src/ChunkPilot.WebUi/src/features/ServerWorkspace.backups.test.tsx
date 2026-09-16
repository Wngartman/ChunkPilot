// @vitest-environment jsdom
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, expect, it } from 'vitest';
import type { BridgeAdapter } from '../bridge/client';
import type { BridgeMethod } from '../bridge/types';
import { NavigationGuardProvider } from '../app/NavigationGuard';
import { fixtures } from '../fixtures/catalog';
import { useAppStore } from '../state/store';
import { ServerWorkspace } from './ServerWorkspace';

let calls: BridgeMethod[];
let resolveCreate: (value: unknown) => void;
let rejectCreate: (reason: Error) => void;
const bridge: BridgeAdapter = {
  request: <T,>(method: BridgeMethod) => {
    calls.push(method);
    if (method === 'backups.create') return new Promise<T>((resolve, reject) => {
      resolveCreate = value => resolve(value as T); rejectCreate = reject;
    });
    return Promise.resolve({} as T);
  },
  subscribe: () => () => undefined,
  dispose: () => undefined
};
beforeEach(() => {
  calls = [];
  window.history.replaceState({}, '', '/?tab=backups');
  const current = structuredClone(fixtures.stopped);
  current.backups = [];
  useAppStore.setState({ snapshot: current, bridge, busy: new Set(), error: null });
});
afterEach(cleanup);
function openBackups() {
  const current = useAppStore.getState().snapshot!;
  render(<NavigationGuardProvider><ServerWorkspace serverId={current.servers[0].id} /></NavigationGuardProvider>);
}

it.each([0, 1])('keeps both create entries disabled and consistently labeled while entry %i is pending', async index => {
  openBackups();
  const buttons = screen.getAllByRole<HTMLButtonElement>('button', { name: 'Create backup' });
  expect(buttons).toHaveLength(2);
  fireEvent.click(buttons[index]);
  const pending = screen.getAllByRole<HTMLButtonElement>('button', { name: 'Creating backup…' });
  expect(pending).toHaveLength(2);
  for (const button of pending) { expect(button.disabled).toBe(true); fireEvent.click(button); }
  expect(calls.filter(method => method === 'backups.create')).toHaveLength(1);
  await act(async () => resolveCreate({}));
  expect(screen.getAllByRole<HTMLButtonElement>('button', { name: 'Create backup' }).every(button => !button.disabled)).toBe(true);
});

it.each(['backups.create', 'backups.restore', 'backups.verify'] as const)('blocks every backup action during %s', method => {
  const current = structuredClone(fixtures.stopped);
  current.backups = [{ ...current.backups[0], verified: true }];
  useAppStore.setState({ snapshot: current, busy: new Set([method]) });
  openBackups();
  for (const name of [method === 'backups.create' ? 'Creating backup…' : 'Create backup', 'Verify', 'Restore']) {
    const button = screen.getByRole<HTMLButtonElement>('button', { name });
    expect(button.disabled).toBe(true);
    fireEvent.click(button);
  }
  expect(calls.filter(value => value.startsWith('backups.'))).toEqual([]);
});

it('restores both create entries after a failed request while retaining the shared error', async () => {
  openBackups();
  fireEvent.click(screen.getAllByRole('button', { name: 'Create backup' })[1]);
  await act(async () => rejectCreate(new Error('Synthetic backup failure')));
  expect(useAppStore.getState().error).toBe('Synthetic backup failure');
  expect(screen.getAllByRole<HTMLButtonElement>('button', { name: 'Create backup' }).every(button => !button.disabled)).toBe(true);
});
