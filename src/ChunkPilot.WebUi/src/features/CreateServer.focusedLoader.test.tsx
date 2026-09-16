// @vitest-environment jsdom
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { BridgeAdapter } from '../bridge/client';
import type { BridgeMethod } from '../bridge/types';
import { FixtureBridge, fixtures } from '../fixtures/catalog';
import { useAppStore } from '../state/store';
import { CreateServerPage } from './CreateServer';

interface Build { id: string; label: string; loaderVersion: string; selectable: boolean; hasIntegrityMetadata: boolean; canResolveIntegrity?: boolean; supportReason: string; }
interface Catalog { platform: string; minecraftVersion: string; available: boolean; builds: Build[]; }
beforeEach(() => {
  window.history.replaceState({}, '', '/?fixture=running&page=create&stage=1&mode=neoforge');
  window.sessionStorage.clear();
  useAppStore.setState({ snapshot: structuredClone(fixtures.running), bridge: null, busy: new Set(), pendingOperations: new Map(), completedOperations: new Set(), error: null });
});
afterEach(() => { cleanup(); vi.restoreAllMocks(); });

async function setup(canResolve = true) {
  const fixture = new FixtureBridge('running');
  const original = await fixture.request<Catalog>('creation.loaderBuilds', { platform: 'NeoForge', versionId: '1.21.8' });
  const builds = Array.from({ length: 60 }, (_, index) => ({ ...original.builds[0],
    id: `build-${index}`, label: `NeoForge 21.1.${index}`, loaderVersion: `21.1.${index}`,
    selectable: index < 48, hasIntegrityMetadata: index < 48, canResolveIntegrity: index >= 48 && canResolve,
    supportReason: index >= 48 ? 'Verify the official checksum for this exact build.' : 'Verified fixture build.' }));
  const catalog = { ...original, builds };
  let resolve!: (value: Catalog) => void;
  const pending = new Promise<Catalog>(complete => { resolve = complete; });
  let signal: AbortSignal | undefined;
  const request = vi.fn(async <T,>(method: BridgeMethod, params: Record<string, unknown> = {}, cancellation?: AbortSignal): Promise<T> => {
    if (method !== 'creation.loaderBuilds') return fixture.request<T>(method, params);
    if (params.loaderVersion) { signal = cancellation; return pending as Promise<T>; }
    return catalog as T;
  });
  const bridge: BridgeAdapter = { request: <T,>(method: BridgeMethod, params?: Record<string, unknown>, signal?: AbortSignal) => request(method, params, signal) as Promise<T>, subscribe: listener => fixture.subscribe(listener), dispose: () => fixture.dispose() };
  useAppStore.setState({ bridge });
  const view = render(<CreateServerPage onDone={() => undefined} />);
  await screen.findByRole('radio', { name: /NeoForge 21.1.0 ·/ });
  return { view, request, catalog, resolve, signal: () => signal };
}

// Each case drives the full 48-row chooser through several async transitions. Hosted Windows
// runners can exceed Vitest's five-second case budget; this is not a product response-time SLA.
describe('focused official loader verification', { timeout: 20_000 }, () => {
  it('does not offer verification when the native provider did not authorize it', async () => {
    await setup(false);
    fireEvent.change(screen.getByRole('searchbox', { name: 'Search exact NeoForge versions' }), { target: { value: '21.1.59' } });
    expect((screen.getByRole('radio', { name: /NeoForge 21.1.59/ }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('makes older builds discoverable beyond the first 48 and enables Continue only after exact verification', async () => {
    const fixture = await setup();
    expect(screen.getAllByRole('radio').length).toBeLessThanOrEqual(48);
    fireEvent.change(screen.getByRole('searchbox', { name: 'Search exact NeoForge versions' }), { target: { value: '21.1.59' } });
    fireEvent.click(screen.getByRole('radio', { name: /NeoForge 21.1.59/ }));
    expect(screen.getByText('Verifying official checksum…')).toBeTruthy();
    expect((screen.getByRole('button', { name: /Continue/ }) as HTMLButtonElement).disabled).toBe(true);
    expect(fixture.request).toHaveBeenCalledWith('creation.loaderBuilds',
      { platform: 'NeoForge', versionId: '1.21.8', loaderVersion: '21.1.59' }, expect.any(AbortSignal));
    await act(async () => fixture.resolve({ ...fixture.catalog, builds: fixture.catalog.builds.map(build => build.id === 'build-59'
      ? { ...build, selectable: true, hasIntegrityMetadata: true, canResolveIntegrity: false } : build) }));
    await waitFor(() => expect((screen.getByRole('button', { name: /Continue/ }) as HTMLButtonElement).disabled).toBe(false));
    expect(screen.getByRole('radio', { name: /NeoForge 21.1.59/ }).getAttribute('aria-checked')).toBe('true');
  });

  it.each(['checksum-unavailable', 'wrong-minecraft', 'wrong-loader'])('fails closed on %s while retaining the chooser', async failure => {
    const fixture = await setup();
    fireEvent.change(screen.getByRole('searchbox', { name: 'Search exact NeoForge versions' }), { target: { value: '21.1.59' } });
    fireEvent.click(screen.getByRole('radio', { name: /NeoForge 21.1.59/ }));
    await act(async () => fixture.resolve({ ...fixture.catalog,
      minecraftVersion: failure === 'wrong-minecraft' ? '1.20.1' : fixture.catalog.minecraftVersion,
      builds: fixture.catalog.builds.map(build => build.id === 'build-59' ? { ...build,
        loaderVersion: failure === 'wrong-loader' ? 'wrong-loader' : build.loaderVersion,
        selectable: failure !== 'checksum-unavailable', hasIntegrityMetadata: failure !== 'checksum-unavailable'
      } : build) }));
    await screen.findByRole('alert');
    expect((screen.getByRole('button', { name: /Continue/ }) as HTMLButtonElement).disabled).toBe(true);
    expect(screen.getByRole('radio', { name: /NeoForge 21.1.59/ })).toBeTruthy();
  });

  it('aborts an in-flight focused request when the wizard is closed', async () => {
    const fixture = await setup();
    fireEvent.change(screen.getByRole('searchbox', { name: 'Search exact NeoForge versions' }), { target: { value: '21.1.59' } });
    fireEvent.click(screen.getByRole('radio', { name: /NeoForge 21.1.59/ }));
    fixture.view.unmount();
    expect(fixture.signal()?.aborted).toBe(true);
    await act(async () => fixture.resolve(fixture.catalog));
    expect(useAppStore.getState().error).toBeNull();
  });

  it('ignores an older verification response after another build is selected', async () => {
    const fixture = await setup();
    const search = screen.getByRole('searchbox', { name: 'Search exact NeoForge versions' });
    fireEvent.change(search, { target: { value: '21.1.59' } });
    fireEvent.click(screen.getByRole('radio', { name: /NeoForge 21.1.59/ }));
    fireEvent.change(search, { target: { value: '21.1.0' } });
    fireEvent.click(screen.getByRole('radio', { name: /NeoForge 21.1.0 ·/ }));
    expect(fixture.signal()?.aborted).toBe(true);
    await act(async () => fixture.resolve(fixture.catalog));
    expect(screen.getByRole('radio', { name: /NeoForge 21.1.0 ·/ }).getAttribute('aria-checked')).toBe('true');
    expect(screen.queryByRole('alert')).toBeNull();
  });
});
