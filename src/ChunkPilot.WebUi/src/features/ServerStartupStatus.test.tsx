// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fixtures } from '../fixtures/catalog';
import type { ServerStartupProgress } from '../bridge/types';
import { ServerStartupStatus } from './ServerStartupStatus';

afterEach(() => { cleanup(); vi.restoreAllMocks(); });
const progress: ServerStartupProgress = {
  serverId: fixtures.running.servers[0].id, attemptId: 'attempt-one', stage: 'PreparingWorld',
  title: 'Preparing the world', detail: 'The server reported world preparation; readiness is not confirmed.',
  startedAt: '2026-09-14T21:00:00Z', updatedAt: '2026-09-14T21:00:10Z', lastOutputAt: '2026-09-14T21:00:10Z',
  processId: 1234, isActive: true
};

describe('ordinary startup evidence', () => {
  it('formats native timestamps in explicit 12-hour time regardless of the system preference', () => {
    const time = vi.spyOn(Date.prototype, 'toLocaleTimeString');
    render(<ServerStartupStatus server={{ ...fixtures.running.servers[0], state: 'Starting', startupProgress: progress }} onConsole={() => undefined} />);
    expect(time).toHaveBeenCalledWith([], { hour: 'numeric', minute: '2-digit', second: '2-digit', hour12: true });
  });

  it('shows native detail and a console action without a fabricated percentage or ETA', () => {
    const onConsole = vi.fn();
    render(<ServerStartupStatus server={{ ...fixtures.running.servers[0], state: 'Starting', startupProgress: progress }} onConsole={onConsole} />);
    expect(screen.getByText(progress.title)).toBeTruthy();
    expect(screen.getByText(progress.detail)).toBeTruthy();
    expect(screen.queryByRole('progressbar')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'View startup console' }));
    expect(onConsole).toHaveBeenCalledOnce();
  });

  it.each(['Stopped', 'Running'] as const)('does not keep a stale active phase visible when the server is %s', state => {
    render(<ServerStartupStatus server={{ ...fixtures.running.servers[0], state, startupProgress: progress }} onConsole={() => undefined} />);
    expect(screen.queryByRole('status')).toBeNull();
  });

  it('rejects a progress record from another server during a switch', () => {
    render(<ServerStartupStatus server={{ ...fixtures.running.servers[0], state: 'Starting', startupProgress: { ...progress, serverId: 'other' } }} onConsole={() => undefined} />);
    expect(screen.queryByRole('status')).toBeNull();
  });

  it('keeps the native failure reason available after an unsuccessful start', () => {
    render(<ServerStartupStatus server={{ ...fixtures.running.servers[0], state: 'Crashed', startupProgress: { ...progress, stage: 'Failed', title: 'Startup failed', detail: 'The process exited before readiness.', isActive: false } }} onConsole={() => undefined} />);
    expect(screen.getByText('The process exited before readiness.')).toBeTruthy();
  });
});
