// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import type { BridgeAdapter } from '../bridge/client';
import type { BridgeMethod, PluginInventoryEntry } from '../bridge/types';
import { fixtures } from '../fixtures/catalog';
import { NavigationGuardProvider } from '../app/NavigationGuard';
import { useAppStore } from '../state/store';
import { ServerWorkspace } from './ServerWorkspace';

const calls: { method: BridgeMethod; params: Record<string, unknown> }[] = [];
const bridge: BridgeAdapter = {
  request: async <T,>(method: BridgeMethod, params: Record<string, unknown> = {}) => {
    calls.push({ method, params });
    if (method === 'plugins.providers') return [
      { provider: 'Modrinth', available: true, detail: 'Official API available.' },
      { provider: 'Hangar', available: false, detail: 'Unavailable; ChunkPilot does not scrape.' }
    ] as T;
    if (method === 'plugins.search') return [{ provider: 'Modrinth', projectId: 'fixture', slug: 'fixture', name: 'Fixture Tools', author: 'Author', summary: 'Paper server utilities.', downloads: 1200, updatedAt: '2026-08-17T12:00:00Z', serverSide: 'required' }] as T;
    if (method === 'plugins.release') return { provider: 'Modrinth', projectId: 'fixture', versionId: 'release-1', versionName: '2.0', minecraftVersion: '1.21.8', loader: 'paper', releaseChannel: 'release', publishedAt: '2026-08-17T12:00:00Z', fileName: 'FixtureTools.jar', sizeBytes: 1500, integrity: 'sha512', dependencies: [{ projectId: 'vault', versionId: '', fileName: '', type: 'required' }] } as T;
    if (method === 'plugins.plan') return { canInstall: true, problems: [], releases: [
      { provider: 'Modrinth', projectId: 'vault', versionId: 'vault-1', versionName: '1.0', minecraftVersion: '1.21.8', loader: 'paper', releaseChannel: 'release', publishedAt: '2026-08-17T12:00:00Z', fileName: 'Vault.jar', sizeBytes: 900, integrity: 'sha512', dependencies: [] },
      { provider: 'Modrinth', projectId: 'fixture', versionId: 'release-1', versionName: '2.0', minecraftVersion: '1.21.8', loader: 'paper', releaseChannel: 'release', publishedAt: '2026-08-17T12:00:00Z', fileName: 'FixtureTools.jar', sizeBytes: 1500, integrity: 'sha512', dependencies: [] }
    ] } as T;
    if (method === 'plugins.install' || method === 'plugins.installPlan') return {
      operationId: String(params.operationId), serverId: String(params.serverId),
      kind: method === 'plugins.installPlan' ? 'InstallAddonPlan' : 'InstallAddon', provider: 'Modrinth',
      projectId: String(params.projectId), versionId: String(params.versionId), displayName: 'Fixture Tools',
      progress: { stage: 'Queued', message: 'Queued.', percent: null, bytesTransferred: null, totalBytes: null },
      isTerminal: false, success: null, isCancellable: true, error: null,
      startedAtUtc: '2026-08-17T12:00:00Z', updatedAtUtc: '2026-08-17T12:00:00Z'
    } as T;
    if (method === 'plugins.chooseLocal') return { cancelled: false, token: 'opaque-token', fileName: 'LocalFixture.jar', expiresAt: '2026-08-17T12:05:00Z', plugin: { name: 'Local Fixture', version: '1.2.0', id: 'LocalFixture', loader: 'Bukkit', sizeBytes: 1450, dependencies: ['Vault'], compatibility: 'LikelyCompatible', compatibilityReason: 'Metadata matches Paper.' } } as T;
    if (method === 'plugins.configFiles') return [{ relativePath: 'plugins/Duplicate/config.yml', name: 'config.yml', sizeBytes: 45, modifiedAt: '2026-08-17T12:00:00Z', format: 'yml' }] as T;
    if (method === 'files.read') return { relativePath: 'plugins/Duplicate/config.yml', content: '# keep this comment\nenabled: true\nlimit: 5\n', encodingName: 'utf-8', hasBom: false, lineEnding: '\n', loadedSha256: 'config-hash', loadedLastWriteAt: '2026-08-17T12:00:00Z' } as T;
    if (method === 'creation.catalog') return { platform: 'Paper', available: true, message: '', fromCache: true, stale: false, retrievedAt: '2026-08-17T12:00:00Z', manifestLatestReleaseId: '', manifestLatestSnapshotId: '', latestVerifiedReleaseId: '1.21.8', versions: [{ id: '1.21.8', label: '1.21.8', channel: 'Stable', releaseKind: 'Release', releaseTime: null, javaMajor: 21, javaSource: 'ChunkPilotPolicy', support: 'Verified', supportReason: 'Certified Paper version.', selectable: true, hasServerArtifact: false, artifactSize: null, hasIntegrityMetadata: false, launchProfile: { kind: 'PaperNogui', arguments: '--nogui', requiresEulaFile: true, evidence: 'Paper launch.' }, capabilities: {}, certification: { level: 'RuntimeCertified', evidence: [], limitations: [] }, warnings: [], evidence: [], provenance: 'Official PaperMC' }] } as T;
    if (method === 'creation.paperBuilds') return { available: true, message: '', fromCache: true, stale: false, retrievedAt: '2026-08-17T12:00:00Z', minecraftVersion: '1.21.8', builds: [{ id: 112, label: 'Build 112', channel: 'Stable', publishedAt: '2026-08-17T12:00:00Z', sizeBytes: 1000, hasIntegrityMetadata: true, selectable: true, support: 'Verified', supportReason: 'Exact build passed.', provenance: 'Official PaperMC', certification: { level: 'RuntimeCertified', evidence: [], limitations: [] } }] } as T;
    return { accepted: true } as T;
  },
  subscribe: () => () => undefined,
  dispose: () => undefined
};

beforeEach(() => {
  calls.length = 0;
  window.history.replaceState({}, '', '/?tab=content');
  const current = structuredClone(fixtures.several);
  const paper = current.servers.find(server => server.capabilities.content === 'plugins')!;
  paper.state = 'Stopped';
  current.selectedServerId = paper.id;
  current.plugins = [{ name: 'Duplicate', fileName: 'Duplicate.jar', relativePath: 'plugins/Duplicate.jar', version: '1.0', id: 'Duplicate', loader: 'Bukkit', sizeBytes: 1200, modifiedAt: '2026-08-17T12:00:00Z', enabled: true, duplicateId: true, dependencies: ['Vault'], dependencyDetails: [{ id: 'Vault', kind: 'Required' }], compatibility: 'LikelyCompatible', compatibilityReason: 'Metadata matches Paper.', loadState: 'Unknown', loadEvidence: 'No explicit current-session load evidence.', installSource: 'Local file', sha256: 'fixture' } satisfies PluginInventoryEntry];
  useAppStore.setState({ snapshot: current, bridge, busy: new Set(), error: null });
});
afterEach(cleanup);

function workspace() {
  const paper = useAppStore.getState().snapshot!.servers.find(server => server.capabilities.content === 'plugins')!;
  render(<NavigationGuardProvider><ServerWorkspace serverId={paper.id} /></NavigationGuardProvider>);
  return paper;
}

describe('Paper plugin management', () => {
  it('shows installed inventory and capability-specific problems', async () => {
    workspace();
    expect(screen.getByRole('heading', { name: 'Plugins' })).toBeTruthy();
    expect(screen.getByText('Duplicate.jar · 1,200 B')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /Problems/ }));
    expect(screen.getByText(/same plugin ID/)).toBeTruthy();
    expect(screen.getByText(/Required dependency Vault/)).toBeTruthy();
  });

  it('blocks an exact Modrinth release with unresolved required dependencies', async () => {
    const paper = workspace();
    fireEvent.click(screen.getByRole('button', { name: 'Browse' }));
    fireEvent.change(screen.getByRole('searchbox', { name: 'Search official Modrinth plugins' }), { target: { value: 'tools' } });
    fireEvent.click(screen.getByRole('button', { name: 'Search' }));
    fireEvent.click(await screen.findByRole('button', { name: /Fixture Tools/ }));
    expect(await screen.findByText('SHA-512')).toBeTruthy();
    expect(screen.queryByRole('img')).toBeNull();
    const install = screen.getByRole('button', { name: 'Install verified release' }) as HTMLButtonElement;
    expect(install.disabled).toBe(true);
    expect(screen.getByText('Required dependencies')).toBeTruthy();
    expect(screen.getByRole('button', { name: /Install vault/ })).toBeTruthy();
    const plan = screen.getByRole('button', { name: 'Install complete verified plan' }) as HTMLButtonElement;
    await waitFor(() => expect(plan.disabled).toBe(false));
    fireEvent.click(plan);
    expect(screen.getByRole('alertdialog').textContent).toContain('Vault.jar');
    fireEvent.click(screen.getByRole('button', { name: 'Install verified plan' }));
    expect(calls).toContainEqual({ method: 'plugins.search', params: { serverId: paper.id, search: 'tools', limit: 20 } });
    expect(calls).toContainEqual({ method: 'plugins.installPlan', params: expect.objectContaining({
      serverId: paper.id, projectId: 'fixture', versionId: 'release-1', restartIfRunning: false,
      operationId: expect.any(String)
    }) });
  });

  it('previews local metadata and waits for confirmation before installing an opaque token', async () => {
    const paper = workspace();
    const choose = screen.getByRole('button', { name: 'Install local JAR' }) as HTMLButtonElement;
    await waitFor(() => expect(choose.disabled).toBe(false));
    fireEvent.click(choose);
    expect(await screen.findByText(/1.2.0 · Bukkit/)).toBeTruthy();
    expect(calls.some(call => call.method === 'plugins.installLocal')).toBe(false);

    fireEvent.click(screen.getByRole('button', { name: 'Install plugin' }));

    expect(calls).toContainEqual({ method: 'plugins.installLocal', params: { serverId: paper.id, token: 'opaque-token', restartIfRunning: false } });
    expect(JSON.stringify(calls)).not.toContain('C:\\');
  });

  it('opens a path-confined config editor and preserves the authoritative write contract', async () => {
    const paper = workspace();
    fireEvent.click(screen.getByRole('button', { name: 'Configure' }));
    expect(await screen.findByRole('dialog', { name: 'Duplicate configuration' })).toBeTruthy();
    const enabled = await screen.findByDisplayValue('true');
    fireEvent.change(enabled, { target: { value: 'false' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save configuration' }));
    await waitFor(() => expect(calls.some(call => call.method === 'plugins.saveConfig')).toBe(true));
    const write = calls.find(call => call.method === 'plugins.saveConfig')!;
    expect(write.params.serverId).toBe(paper.id);
    expect(JSON.stringify(write.params)).toContain('# keep this comment');
    expect(JSON.stringify(write.params)).not.toContain('D:\\');
  });

  it('saves running-server configuration only through the safe restart contract', async () => {
    const current = structuredClone(useAppStore.getState().snapshot!);
    const paper = current.servers.find(server => server.capabilities.content === 'plugins')!;
    paper.state = 'Running';
    useAppStore.setState({ snapshot: current });
    workspace();

    fireEvent.click(screen.getByRole('button', { name: 'Configure' }));
    const enabled = await screen.findByDisplayValue('true');
    fireEvent.change(enabled, { target: { value: 'false' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save and restart' }));

    await waitFor(() => expect(calls).toContainEqual({
      method: 'plugins.saveConfig',
      params: {
        serverId: paper.id,
        addonRelativePath: 'plugins/Duplicate.jar',
        file: expect.objectContaining({ relativePath: 'plugins/Duplicate/config.yml' }),
        restartIfRunning: true
      }
    }));
  });

  it('does not count optional plugin integrations as blocking problems', () => {
    const current = structuredClone(useAppStore.getState().snapshot!);
    current.plugins = [{ ...current.plugins[0], duplicateId: false, dependencies: ['BlueMap'], dependencyDetails: [{ id: 'BlueMap', kind: 'Optional' }] }];
    useAppStore.setState({ snapshot: current });
    workspace();
    fireEvent.click(screen.getByRole('button', { name: /Problems/ }));
    expect(screen.getByText('No inventory problems detected')).toBeTruthy();
    expect(screen.getByText(/Optional integrations not installed/)).toBeTruthy();
  });

  it('matches provider-installed identity and offers an exact compatible update', async () => {
    const current = structuredClone(useAppStore.getState().snapshot!);
    current.plugins = [
      { ...current.plugins[0], name: 'Fixture Tools', id: 'FixtureTools', version: '1.0', provider: 'Modrinth', providerProjectId: 'fixture', providerVersionId: 'release-0', installSource: 'Modrinth' },
      { ...current.plugins[0], name: 'Vault', id: 'vault', fileName: 'Vault.jar', relativePath: 'plugins/Vault.jar', duplicateId: false, dependencies: [], dependencyDetails: [] }
    ];
    useAppStore.setState({ snapshot: current });
    const paper = workspace();

    fireEvent.click(screen.getByRole('button', { name: 'Updates' }));
    const update = await screen.findByRole('button', { name: 'Update' });
    await waitFor(() => expect((update as HTMLButtonElement).disabled).toBe(false));
    fireEvent.click(update);
    fireEvent.click(screen.getByRole('button', { name: 'Apply plugin change' }));

    expect(calls).toContainEqual({ method: 'plugins.release', params: { serverId: paper.id, projectId: 'fixture' } });
    expect(calls).toContainEqual({ method: 'plugins.install', params: expect.objectContaining({
      serverId: paper.id, projectId: 'fixture', versionId: 'release-1', restartIfRunning: false,
      operationId: expect.any(String)
    }) });
  });

  it('makes a running-server restart consequence explicit and sends one authoritative apply request', async () => {
    const current = structuredClone(useAppStore.getState().snapshot!);
    const paper = current.servers.find(server => server.capabilities.content === 'plugins')!;
    paper.state = 'Running';
    current.plugins = [{ ...current.plugins[0], duplicateId: false, dependencies: [], dependencyDetails: [] }];
    useAppStore.setState({ snapshot: current });
    workspace();

    expect(screen.getByText(/save and stop the server/)).toBeTruthy();
    const disable = screen.getByRole('button', { name: 'Disable' }) as HTMLButtonElement;
    await waitFor(() => expect(disable.disabled).toBe(false));
    fireEvent.click(disable);
    expect(screen.getByRole('alertdialog').textContent).toMatch(/restores the previous state/i);
    fireEvent.click(screen.getByRole('button', { name: 'Disable and restart' }));

    expect(calls).toContainEqual({
      method: 'plugins.setEnabled',
      params: { serverId: paper.id, relativePath: 'plugins/Duplicate.jar', enabled: false, restartIfRunning: true }
    });
  });

  it('uses PaperMC version and exact-build evidence on the Paper Versions page', async () => {
    window.history.replaceState({}, '', '/?tab=versions');
    const current = structuredClone(useAppStore.getState().snapshot!);
    const paper = current.servers.find(server => server.capabilities.content === 'plugins')!;
    paper.loaderVersion = '112';
    useAppStore.setState({ snapshot: current });

    workspace();

    expect(await screen.findByText('Paper 1.21.8 build 112')).toBeTruthy();
    expect(screen.getByText(/Exact build passed/)).toBeTruthy();
    expect(calls).toContainEqual({ method: 'creation.catalog', params: { platform: 'Paper', includeSnapshots: true } });
    expect(calls).toContainEqual({ method: 'creation.paperBuilds', params: { versionId: '1.21.8' } });
  });
});
