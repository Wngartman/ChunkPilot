// @vitest-environment jsdom
import { act, cleanup, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { useConsoleFollow } from './consoleFollow';

let nextFrame = 1;
let frames = new Map<number, FrameRequestCallback>();
beforeEach(() => {
  nextFrame = 1; frames = new Map();
  vi.stubGlobal('requestAnimationFrame', (callback: FrameRequestCallback) => { const id = nextFrame++; frames.set(id, callback); return id; });
  vi.stubGlobal('cancelAnimationFrame', (id: number) => frames.delete(id));
});
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });
function flushFrame() {
  act(() => { const pending = [...frames.values()]; frames.clear(); pending.forEach(callback => callback(0)); });
}

it('coalesces late wrapped-row measurements and aligns using their final height without polling', () => {
  const viewport = { scrollHeight: 180, clientHeight: 80, scrollTop: 100 };
  const align = vi.fn(() => { viewport.scrollTop = viewport.scrollHeight - viewport.clientHeight; });
  const { result } = renderHook(() => useConsoleFollow(align));
  act(() => { result.current.scheduleFollow(); result.current.scheduleFollow(); });
  viewport.scrollHeight = 420;
  act(() => result.current.observeScroll(viewport));
  expect(result.current.following).toBe(true);
  expect(frames.size).toBe(1);
  flushFrame();
  expect(viewport.scrollTop).toBe(340);
  expect(align).toHaveBeenCalledTimes(1);
  flushFrame();
  expect(frames.size).toBe(0);
  expect(align).toHaveBeenCalledTimes(1);
});

it('cancels a queued correction on explicit pause and does not resume on a layout scroll at the bottom', () => {
  const align = vi.fn();
  const { result } = renderHook(() => useConsoleFollow(align));
  act(() => { result.current.scheduleFollow(); result.current.toggleFollow(); });
  act(() => { result.current.scheduleFollow(); result.current.observeScroll({ scrollHeight: 300, scrollTop: 220, clientHeight: 80 }); });
  flushFrame();
  expect(result.current.following).toBe(false);
  expect(align).not.toHaveBeenCalled();
  act(() => result.current.toggleFollow());
  flushFrame();
  expect(result.current.following).toBe(true);
  expect(align).toHaveBeenCalledOnce();
});

it('lets upward user input cancel measurement settling and resumes only when the user reaches the end', () => {
  const align = vi.fn();
  const { result } = renderHook(() => useConsoleFollow(align));
  act(() => { result.current.scheduleFollow(); result.current.pauseForUser(); });
  act(() => result.current.observeScroll({ scrollHeight: 400, scrollTop: 40, clientHeight: 80 }));
  flushFrame();
  expect(result.current.following).toBe(false);
  expect(align).not.toHaveBeenCalled();
  act(() => result.current.observeScroll({ scrollHeight: 400, scrollTop: 320, clientHeight: 80 }));
  flushFrame();
  expect(result.current.following).toBe(true);
  expect(align).toHaveBeenCalledOnce();
});

it('does not undo an upward-input pause on the still-at-bottom event before default scrolling moves away', () => {
  const align = vi.fn();
  const { result } = renderHook(() => useConsoleFollow(align));
  act(() => { result.current.scheduleFollow(); result.current.pauseForUser(); });
  act(() => result.current.observeScroll({ scrollHeight: 400, scrollTop: 320, clientHeight: 80 }));
  act(() => result.current.scheduleFollow());
  flushFrame();
  expect(result.current.following).toBe(false);
  expect(align).not.toHaveBeenCalled();
  act(() => result.current.observeScroll({ scrollHeight: 400, scrollTop: 200, clientHeight: 80 }));
  expect(result.current.following).toBe(false);
  act(() => result.current.observeScroll({ scrollHeight: 400, scrollTop: 320, clientHeight: 80 }));
  flushFrame();
  expect(result.current.following).toBe(true);
  expect(align).toHaveBeenCalledOnce();
});

it('cancels both pending alignment and settle frames on unmount', () => {
  const align = vi.fn();
  const view = renderHook(() => useConsoleFollow(align));
  act(() => view.result.current.scheduleFollow());
  flushFrame();
  expect(frames.size).toBe(1);
  view.unmount();
  expect(frames.size).toBe(0);
});
