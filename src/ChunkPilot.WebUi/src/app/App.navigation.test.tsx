// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import App from './App';
import { ViewErrorBoundary } from './ViewErrorBoundary';
import { useAppStore } from '../state/store';

vi.mock('../fixtures/mode', () => ({ isFixtureMode: () => true }));

beforeEach(() => {
  window.history.replaceState({}, '', '/?fixture=several&page=dashboard');
  window.sessionStorage.clear();
  window.scrollTo = vi.fn();
  HTMLElement.prototype.scrollTo = vi.fn();
  useAppStore.setState({ snapshot: null, bridge: null, busy: new Set(), pendingOperations: new Map(), completedOperations: new Set(), error: null });
});
afterEach(() => { cleanup(); vi.restoreAllMocks(); });

describe('whole-app fixture navigation', () => {
  it('opens a server from the dashboard sidebar and keeps the native window controls', async () => {
    render(<App />);
    await screen.findByRole('heading', { name: 'Dashboard' });
    const navigation = screen.getByRole('complementary', { name: 'Primary navigation' });
    fireEvent.click(within(navigation).getByRole('button', { name: /Copper Valley/ }));
    await screen.findByRole('heading', { name: 'Copper Valley' });
    expect(screen.getByRole('button', { name: 'Close' })).toBeTruthy();
    expect(screen.getByRole('navigation', { name: 'Copper Valley navigation' })).toBeTruthy();
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('contains view-render failure without retrying or losing surrounding controls', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => undefined);
    function Broken({ fail }: { fail: boolean }) { if (fail) throw new Error('Synthetic view failure'); return <h1>Recovered view</h1>; }
    const view = render(<><button>Window close</button><ViewErrorBoundary><Broken fail /></ViewErrorBoundary></>);
    expect(screen.getByRole('alert')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Window close' })).toBeTruthy();
    view.rerender(<><button>Window close</button><ViewErrorBoundary><Broken fail={false} /></ViewErrorBoundary></>);
    expect(screen.queryByRole('heading', { name: 'Recovered view' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }));
    await waitFor(() => expect(screen.getByRole('heading', { name: 'Recovered view' })).toBeTruthy());
  });
});
