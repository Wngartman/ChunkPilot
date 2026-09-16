// @vitest-environment jsdom
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { NavigationGuardProvider } from '../app/NavigationGuard';
import { fixtures } from '../fixtures/catalog';
import { useAppStore } from '../state/store';
import { ServerWorkspace } from './ServerWorkspace';

const virtual = vi.hoisted(() => ({
  size: 180,
  getTotalSize: vi.fn((): number => virtual.size),
  getVirtualItems: () => [],
  measure: () => undefined,
  measureElement: () => undefined,
  resizeItem: () => undefined,
  scrollToIndex: vi.fn()
}));
vi.mock('@tanstack/react-virtual', () => ({ useVirtualizer: () => virtual }));
let nextFrame = 1;
let frames = new Map<number, FrameRequestCallback>();
function flushFrame() {
  act(() => { const pending = [...frames.values()]; frames.clear(); pending.forEach(callback => callback(0)); });
}
beforeEach(() => {
  nextFrame = 1; frames = new Map(); virtual.size = 180; virtual.scrollToIndex.mockClear();
  vi.stubGlobal('requestAnimationFrame', (callback: FrameRequestCallback) => { const id = nextFrame++; frames.set(id, callback); return id; });
  vi.stubGlobal('cancelAnimationFrame', (id: number) => frames.delete(id));
  window.history.replaceState({}, '', '/?tab=console');
  window.localStorage.clear();
  useAppStore.setState({ snapshot: structuredClone(fixtures.running), bridge: null, busy: new Set(), error: null });
});
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

it('follows same-count buffer appends and late measured height, but an upward wheel cancels the queued correction', () => {
  const current = useAppStore.getState().snapshot!;
  render(<NavigationGuardProvider><ServerWorkspace serverId={current.servers[0].id} /></NavigationGuardProvider>);
  flushFrame(); flushFrame(); virtual.scrollToIndex.mockClear();

  const latest = { ...current.console[current.console.length - 1], sequence: 99999, text: 'New end of bounded buffer' };
  act(() => useAppStore.setState({ snapshot: { ...current, console: [...current.console.slice(1), latest] } }));
  flushFrame();
  expect(virtual.scrollToIndex).toHaveBeenLastCalledWith(current.console.length - 1, { align: 'end' });
  flushFrame(); virtual.scrollToIndex.mockClear();

  virtual.size = 460;
  act(() => useAppStore.setState({ snapshot: { ...useAppStore.getState().snapshot!, revision: current.revision + 1 } }));
  flushFrame();
  expect(virtual.scrollToIndex).toHaveBeenCalledOnce();
  flushFrame(); virtual.scrollToIndex.mockClear();

  virtual.size = 620;
  act(() => useAppStore.setState({ snapshot: { ...useAppStore.getState().snapshot!, revision: current.revision + 2 } }));
  fireEvent.wheel(screen.getByRole('region', { name: 'Console output' }), { deltaY: -100 });
  flushFrame();
  expect(virtual.scrollToIndex).not.toHaveBeenCalled();
  expect(screen.getByRole('button', { name: 'Resume following console' }).getAttribute('aria-pressed')).toBe('false');
});
