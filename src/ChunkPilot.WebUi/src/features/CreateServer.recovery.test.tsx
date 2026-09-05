// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, expect, it } from 'vitest';
import type { BridgeAdapter } from '../bridge/client';
import type { BridgeMethod } from '../bridge/types';
import { FixtureBridge, fixtures } from '../fixtures/catalog';
import { useAppStore } from '../state/store';
import { CreateServerPage } from './CreateServer';

const operationId = '09fd8686-573c-44dd-b4bf-aa16de957e0e';
const failed = { operationId, isTerminal: true, success: false, canRetry: true, canDiscard: true,
  retryGeneration: 1, retainedInputBytes: 1024, percent: 84, message: 'Startup check failed safely',
  outcome: 'StagingResumable', updatedAt: new Date().toISOString() };
beforeEach(() => {
  window.sessionStorage.clear();
  window.history.replaceState({}, '', '/?fixture=running&page=create');
  useAppStore.setState({ snapshot: structuredClone(fixtures.running), bridge: null, busy: new Set(),
    pendingOperations: new Map(), completedOperations: new Set(), error: null });
});
afterEach(cleanup);

function show(state: Record<string, unknown>, recover?: (method: BridgeMethod, params: Record<string, unknown>) => Promise<unknown>) {
  const fixture = new FixtureBridge('running');
  const bridge: BridgeAdapter = {
    request: async <T,>(method: BridgeMethod, params: Record<string, unknown> = {}) => {
      if (method === 'creation.operations') return [state] as T;
      if (method === 'creation.progress') return state as T;
      if (method === 'creation.retry' || method === 'creation.discard') return await recover?.(method, params) as T;
      return fixture.request<T>(method, params);
    }, subscribe: listener => fixture.subscribe(listener), dispose: () => fixture.dispose()
  };
  useAppStore.setState({ bridge });
  render(<CreateServerPage onDone={() => undefined} onActivity={() => undefined} />);
}

it('restores terminal retained input and fences duplicate retry clicks with the native generation', async () => {
  const calls: Record<string, unknown>[] = [];
  show(failed, async (_, params) => { calls.push(params); return new Promise(() => undefined); });
  const button = await screen.findByRole('button', { name: 'Retry from verified input' });
  fireEvent.click(button);
  fireEvent.click(button);
  expect(calls).toEqual([{ operationId, retryGeneration: 1 }]);
  expect((button as HTMLButtonElement).disabled).toBe(true);
  expect(screen.getByRole('button', { name: 'View Activity' })).toBeTruthy();
});

it('discards only through the native operation identity and removes the retained actions', async () => {
  const calls: Record<string, unknown>[] = [];
  show(failed, async (_, params) => { calls.push(params); return { accepted: true }; });
  fireEvent.click(await screen.findByRole('button', { name: 'Discard retained download' }));
  await waitFor(() => expect(screen.queryByRole('button', { name: 'Retry from verified input' })).toBeNull());
  expect(calls).toEqual([{ operationId, retryGeneration: 1 }]);
  expect(window.sessionStorage.getItem('chunkpilot.creation.operation')).toBeNull();
});

it('shows elapsed startup without invented percent and warns despite fresh repeated log output', async () => {
  show({ ...failed, isTerminal: false, canRetry: false, canDiscard: false, isIndeterminate: true,
    stageElapsedSeconds: 125, lastMeaningfulStatus: 'Preparing spawn', secondsSinceMeaningfulUpdate: 120,
    newLogOutputObserved: true, recentStatus: 'Repeated warning', updatedAt: new Date().toISOString() });
  expect(await screen.findByText('Startup elapsed 2m 5s')).toBeTruthy();
  expect(screen.queryByText('84%')).toBeNull();
  expect(screen.getByText(/Repeated log output does not extend/)).toBeTruthy();
});
