// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { VersionBrowser } from './VersionBrowser';
import type { MinecraftVersionCatalog, MinecraftVersionOption } from './types';

afterEach(() => { cleanup(); window.sessionStorage.clear(); vi.unstubAllGlobals(); });

const option = (id: string, support: MinecraftVersionOption['support'], releaseKind: MinecraftVersionOption['releaseKind'], selectable: boolean): MinecraftVersionOption => ({
  id, label: `Minecraft ${id}`, channel: releaseKind === 'Release' ? 'Stable' : releaseKind === 'Alpha' || releaseKind === 'Beta' ? 'Historic' : 'Snapshot', releaseKind,
  releaseTime: '2026-01-01T00:00:00Z', javaMajor: selectable ? 21 : null, javaSource: selectable ? 'OfficialMetadata' : 'Unknown', support, supportReason: selectable ? 'Ready with stated evidence.' : 'No official server artifact.', selectable,
  hasServerArtifact: selectable, artifactSize: selectable ? 1024 : null, hasIntegrityMetadata: selectable,
  launchProfile: { kind: selectable ? 'ModernEulaNogui' : 'Unknown', arguments: selectable ? 'nogui' : '', requiresEulaFile: selectable, evidence: '' },
  certification: { level: selectable ? 'MetadataValidated' : 'Inventoried', runtimeLaunched: false, readinessConfirmed: false, cleanShutdownConfirmed: false, runtimeValidatedAt: null, limitations: selectable ? ['Runtime proof not recorded.'] : [] },
  capabilities: { serverIcon: selectable, formattedMotd: selectable, playerManagement: selectable, modernServerProperties: selectable, statusQuery: selectable, datapacks: selectable, managedVersionChange: selectable },
  warnings: [], evidence: selectable ? ['Official version record'] : [], provenance: 'Official Mojang version metadata'
});

const catalog: MinecraftVersionCatalog = {
  available: true, message: '', fromCache: false, stale: false, retrievedAt: '2026-01-01T00:00:00Z',
  manifestLatestReleaseId: '26.2', manifestLatestSnapshotId: '26w33a', latestVerifiedReleaseId: '',
  versions: [option('26.2', 'Experimental', 'Release', true), option('1.21.8', 'Experimental', 'Release', true), option('a1.2.6', 'Unavailable', 'Alpha', false)]
};

describe('Minecraft version browser', () => {
  it('selects an exact creatable ID and lets unavailable entries explain themselves without selection', () => {
    const selected = vi.fn();
    render(<VersionBrowser catalog={catalog} value="26.2" onChange={selected} />);
    fireEvent.click(screen.getByRole('button', { name: 'Show all versions' }));
    fireEvent.click(screen.getByRole('option', { name: /1\.21\.8/ }));
    expect(selected).toHaveBeenLastCalledWith(expect.objectContaining({ id: '1.21.8' }));
    fireEvent.click(screen.getByRole('option', { name: /a1\.2\.6/ }));
    expect(selected).toHaveBeenCalledTimes(1);
    expect(screen.getByRole('heading', { name: 'Minecraft a1.2.6' })).toBeTruthy();
    expect(screen.getByText('No official server artifact.')).toBeTruthy();
  });

  it('supports arrow-key movement through virtualized rows', async () => {
    render(<VersionBrowser catalog={catalog} value="26.2" onChange={() => undefined} />);
    const first = screen.getByRole('option', { name: /26\.2/ });
    first.focus(); fireEvent.keyDown(first, { key: 'ArrowDown' });
    await waitFor(() => expect(document.activeElement?.textContent).toContain('1.21.8'));
  });

  it('searches a full-size inventory locally without a provider request', async () => {
    const large: MinecraftVersionCatalog = {
      ...catalog,
      versions: Array.from({ length: 906 }, (_, index) => option(`fixture-${String(index).padStart(3, '0')}`, 'Verified', 'Release', true))
    };
    render(<VersionBrowser catalog={large} value="" onChange={() => undefined} />);
    fireEvent.change(screen.getByRole('searchbox', { name: 'Search Minecraft versions' }), { target: { value: 'fixture-905' } });
    await waitFor(() => expect(screen.getByRole('option', { name: /fixture-905/ })).toBeTruthy());
    expect(screen.getByText('906')).toBeTruthy();
  });

  it('exposes one unmistakable accessible active filter and persists it for the catalog', () => {
    render(<VersionBrowser catalog={catalog} value="26.2" onChange={() => undefined} />);
    const all = screen.getByRole('button', { name: 'Show all versions' });
    fireEvent.click(all);
    expect(all.getAttribute('aria-pressed')).toBe('true');
    expect(all.getAttribute('data-selected')).toBe('true');
    expect(screen.getByRole('button', { name: 'All releases' }).hasAttribute('data-selected')).toBe(false);
    cleanup();
    render(<VersionBrowser catalog={catalog} value="26.2" onChange={() => undefined} />);
    expect(screen.getByRole('button', { name: 'Show all versions' }).getAttribute('aria-pressed')).toBe('true');
  });

  it('shows all stable Minecraft versions by default for Paper', () => {
    render(<VersionBrowser catalog={{ ...catalog, platform: 'Paper' }} value="26.2" onChange={() => undefined} compact />);
    expect(screen.getByRole('button', { name: 'All stable' }).getAttribute('aria-pressed')).toBe('true');
    expect(screen.getAllByRole('option')).toHaveLength(2);
  });

  it('allows an explicitly reviewed historical version to enter the native artifact flow', () => {
    const selected = vi.fn();
    const historical = {
      ...option('b1.8.1', 'Unavailable', 'Beta', false),
      javaMajor: 8,
      javaSource: 'ChunkPilotPolicy' as const,
      launchProfile: {
        kind: 'LegacyNogui', arguments: 'nogui', requiresEulaFile: true,
        evidence: 'Curated exact historical launch profile.'
      }
    };
    render(<VersionBrowser
      catalog={{ ...catalog, versions: [historical] }}
      value=""
      onChange={selected}
      allowUnavailableSelection={version => version.id === 'b1.8.1'}
    />);

    fireEvent.click(screen.getByRole('button', { name: 'Beta' }));
    fireEvent.click(screen.getByRole('option', { name: /b1\.8\.1/ }));

    expect(selected).toHaveBeenCalledWith(expect.objectContaining({ id: 'b1.8.1' }));
  });
});
