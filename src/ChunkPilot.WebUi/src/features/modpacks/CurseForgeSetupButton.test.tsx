// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { BridgeAdapter } from '../../bridge/client';
import { NavigationGuardProvider } from '../../app/NavigationGuard';
import { fixtures } from '../../fixtures/catalog';
import { useAppStore } from '../../state/store';
import { SettingsPage } from '../SettingsPage';
import { CurseForgeSetupButton } from './CurseForgeSetupButton';

afterEach(cleanup);
function setup(result: unknown) {
  const request = vi.fn().mockResolvedValue(result);
  const bridge: BridgeAdapter = { request, subscribe: () => () => undefined, dispose: () => undefined };
  useAppStore.setState({ snapshot: structuredClone(fixtures.running), bridge, busy: new Set(), error: null });
  return request;
}
describe('native CurseForge setup entry', () => {
  it('is reachable in General settings and sends no credential or file path', async () => {
    const request = setup({ configured: true, cancelled: false });
    const view = render(<NavigationGuardProvider><SettingsPage /></NavigationGuardProvider>);
    fireEvent.click(screen.getByRole('button', { name: 'Set up CurseForge' }));
    expect(await screen.findByText('CurseForge access saved.')).toBeTruthy();
    expect(request).toHaveBeenCalledWith('providers.configureCurseForge', {}, expect.any(AbortSignal));
    expect(view.container.querySelector('input[type="password"]')).toBeNull();
  });
  it('does not refresh or claim a save when native setup is cancelled', async () => {
    setup({ configured: true, cancelled: true });
    const changed = vi.fn();
    render(<CurseForgeSetupButton onConfigured={changed} />);
    fireEvent.click(screen.getByRole('button', { name: 'Set up CurseForge' }));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Set up CurseForge' })).toBeTruthy());
    expect(changed).not.toHaveBeenCalled();
    expect(screen.queryByText('CurseForge access saved.')).toBeNull();
  });
  it('reports removal as removal and refreshes provider state', async () => {
    setup({ configured: false, cancelled: false });
    const changed = vi.fn();
    render(<CurseForgeSetupButton onConfigured={changed} />);
    fireEvent.click(screen.getByRole('button', { name: 'Set up CurseForge' }));
    expect(await screen.findByText('Saved CurseForge access removed.')).toBeTruthy();
    expect(changed).toHaveBeenCalledOnce();
  });
  it('cancels native setup when its owning view unmounts', async () => {
    const request = setup({ configured: false, cancelled: true });
    request.mockImplementation((_method, _params, signal: AbortSignal) => new Promise((_resolve, reject) => {
      signal.addEventListener('abort', () => reject(new Error('cancelled')), { once: true });
    }));
    const view = render(<CurseForgeSetupButton />);
    fireEvent.click(screen.getByRole('button', { name: 'Set up CurseForge' }));
    const signal = request.mock.calls[0][2] as AbortSignal;
    expect(signal.aborted).toBe(false);
    view.unmount();
    expect(signal.aborted).toBe(true);
    await waitFor(() => expect(useAppStore.getState().busy.has('providers.configureCurseForge')).toBe(false));
  });
});
