import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { useVirtualizer } from '@tanstack/react-virtual';
import { Archive, Box, Check, CircleAlert, CircleHelp, CloudOff, Code2, File, Folder, FolderOpen, Globe2, History, MoreHorizontal, Play, RotateCw, Send, Server as ServerIcon, Settings, Share2, ShieldCheck, Square, Terminal, Trash2, Users, Wifi } from '../design-system/Icons';
import type { ConnectivitySnapshot, ManagedContentOperation, PluginInstallPlan, PluginProject, PluginProviderStatus, PluginRelease, ServerDeletionMode, ServerDeletionPreflight, ServerSummary, TextFileContent, UpdateSummary } from '../bridge/types';
import { Button, ConfirmDialog, Dialog, EmptyState, PanelTitle, SearchInput, SelectInput, Sparkline, StatusBadge, Switch, TextInput } from '../design-system/Primitives';
import { ActionMenu } from '../design-system/ActionMenu';
import { useAppStore } from '../state/store';
import { lifecycleAction } from './lifecycle';
import { IconCropEditor } from './server-appearance/IconCropEditor';
import { MotdEditor } from './server-appearance/MotdEditor';
import { validateMotd } from './server-appearance/motd';
import { MemoryControl } from './memory/MemoryControl';
import { VersionBrowser, VersionDetails } from './versions/VersionBrowser';
import type { MinecraftVersionCatalog } from './versions/types';
import appearance from './server-appearance/ServerAppearance.module.css';
import page from './Page.module.css';
import styles from './ServerWorkspace.module.css';
import pluginStyles from './PluginsPage.module.css';
import { useGuardedNavigation, useUnsavedChangesGuard } from '../app/NavigationGuard';
import { runMeasuredNavigation } from '../app/performance';
import { PluginConfigEditor } from './plugin-config/PluginConfigEditor';
import { ConnectionSummary, connectionChoice } from './connectivity/ConnectionSummary';

type Tab = 'overview' | 'console' | 'players' | 'files' | 'content' | 'backups' | 'versions' | 'settings';
interface PaperBuildEvidence {
  id: number;
  label: string;
  channel: 'Stable' | 'Beta' | 'Alpha' | 'Unknown';
  publishedAt: string | null;
  sizeBytes: number | null;
  hasIntegrityMetadata: boolean;
  selectable: boolean;
  support: 'Recommended' | 'Verified' | 'Experimental' | 'Unavailable';
  supportReason: string;
  provenance: string;
  certification: { level: string; runtimeValidatedAt?: string | null; evidence?: string[]; limitations?: string[] };
}
interface PaperBuildEvidenceCatalog {
  available: boolean;
  message: string;
  fromCache: boolean;
  stale: boolean;
  retrievedAt: string | null;
  minecraftVersion: string;
  builds: PaperBuildEvidence[];
}
interface LoaderBuildEvidence {
  id: string;
  label: string;
  loaderVersion: string;
  installerVersion: string;
  channel: 'Stable' | 'Beta' | 'Experimental';
  sizeBytes: number | null;
  hasIntegrityMetadata: boolean;
  selectable: boolean;
  support: 'Recommended' | 'Verified' | 'Experimental' | 'Unavailable';
  supportReason: string;
  provenance: string;
  certification: { level: string; runtimeValidatedAt?: string | null; evidence?: string[]; limitations?: string[] };
}
interface LoaderBuildEvidenceCatalog {
  platform: 'Fabric' | 'Quilt' | 'Forge' | 'NeoForge';
  available: boolean;
  message: string;
  fromCache: boolean;
  stale: boolean;
  retrievedAt: string | null;
  minecraftVersion: string;
  builds: LoaderBuildEvidence[];
}
const bytes = (value: number | null) => value == null ? 'Unavailable' : value >= 1024 ** 3 ? `${(value / 1024 ** 3).toFixed(2)} GB` : value >= 1024 ** 2 ? `${(value / 1024 ** 2).toFixed(1)} MB` : `${value.toLocaleString()} B`;
const tone = (server: ServerSummary) => server.state === 'Running' ? 'success' : server.state === 'Crashed' || server.state === 'Unresponsive' ? 'danger' : ['Starting', 'Restarting'].includes(server.state) ? 'info' : ['BackingUp', 'Restoring'].includes(server.state) ? 'warning' : 'neutral';
const contentLabel = (server: ServerSummary) => server.capabilities.content === 'plugins' ? 'Plugins' : server.capabilities.content === 'mods' ? 'Mods' : server.capabilities.content === 'modpack' ? 'Modpack' : server.capabilities.content === 'datapacks' ? 'Datapacks' : 'Content';
export const measureConsoleRow = (element: Element): number => Math.max(24, Math.ceil(Math.max(element.getBoundingClientRect().height, (element as HTMLElement).scrollHeight)) + 1);

export function ServerWorkspace({ serverId }: { serverId: string }) {
  const snapshot = useAppStore(state => state.snapshot)!;
  const command = useAppStore(state => state.command);
  const busy = useAppStore(state => state.busy);
  const query = new URLSearchParams(window.location.search);
  const requestedTab = query.get('tab') as Tab | null;
  const requestedSettingsCategory = query.get('settings');
  const [tab, setTab] = useState<Tab>(() => requestedTab && ['overview', 'console', 'players', 'files', 'content', 'backups', 'versions', 'settings'].includes(requestedTab) ? requestedTab : 'overview');
  const [settingsCategory, setSettingsCategory] = useState(() => requestedSettingsCategory && ['Appearance', 'General', 'Gameplay', 'Resources', 'Connectivity'].includes(requestedSettingsCategory) ? requestedSettingsCategory : 'Appearance');
  const [menuOpen, setMenuOpen] = useState(() => { const query = new URLSearchParams(window.location.search); return query.get('menu') === 'open' || query.get('mode') === 'menu'; });
  const [shareOpen, setShareOpen] = useState(() => query.get('mode')?.startsWith('share') === true);
  const [deleteOpen, setDeleteOpen] = useState(() => query.get('mode') === 'delete');
  const [deletePreflight, setDeletePreflight] = useState<ServerDeletionPreflight | null>(null);
  const closeShare = useCallback(() => setShareOpen(false), []);
  const navigate = useGuardedNavigation();
  const server = snapshot.servers.find(item => item.id === serverId);
  const selectedConnectivity = server && snapshot.connectivity?.serverId === server.id ? snapshot.connectivity : null;
  useAutomaticConnectivityVerification(server ?? null, selectedConnectivity);
  useEffect(() => { if (server) void command('workspace.load', { serverId: server.id, destination: tab }).catch(() => undefined); }, [tab, server?.id]);
  if (!server) return null;
  const tabs: { id: Tab; label: string; icon: typeof ServerIcon; enabled: boolean }[] = [
    { id: 'overview', label: 'Overview', icon: ServerIcon, enabled: true },
    { id: 'console', label: 'Console', icon: Terminal, enabled: server.capabilities.console },
    { id: 'players', label: 'Players', icon: Users, enabled: server.capabilities.players },
    { id: 'files', label: 'Files', icon: Folder, enabled: server.capabilities.files },
    { id: 'content', label: contentLabel(server), icon: Box, enabled: true },
    { id: 'backups', label: 'Backups', icon: Archive, enabled: server.capabilities.backups },
    { id: 'versions', label: 'Versions', icon: History, enabled: server.capabilities.versions },
    { id: 'settings', label: 'Settings', icon: Settings, enabled: true }
  ];
  const isRunning = server.state === 'Running';
  const lifecyclePending = busy.has('servers.start') || busy.has('servers.stop') || busy.has('servers.restart');
  const lifecycle = lifecycleAction(server, lifecyclePending);
  const runLifecycle = () => { if (lifecycle.method) runMeasuredNavigation('server-lifecycle-ack', () => void command(lifecycle.method!, { serverId: server.id })); };
  const openSettings = (category: string) => navigate(() => runMeasuredNavigation(`server-settings-${category.toLowerCase()}`, () => { setMenuOpen(false); setSettingsCategory(category); setTab('settings'); }));
  const openDelete = () => {
    setMenuOpen(false);
    setDeleteOpen(true);
    setDeletePreflight(null);
  };
  useEffect(() => {
    if (!deleteOpen || deletePreflight || !server) return;
    void command<ServerDeletionPreflight>('servers.deletePreflight', { serverId: server.id })
      .then(setDeletePreflight).catch(() => setDeleteOpen(false));
  }, [deleteOpen, deletePreflight, server?.id]);
  return <div className={styles.workspace}>
    <section className={styles.hero}>
      <div className={styles.heroInner}><div className={styles.heroTop}>
        <div className={styles.identity}><div className={styles.serverIcon}>{server.iconUrl ? <img src={server.iconUrl} alt="" aria-hidden="true" /> : <ServerIcon size={22} />}</div><div><h1>{server.name}</h1><div className={styles.meta}><StatusBadge tone={tone(server)}>{server.state}</StatusBadge><span><Box size={13} />{server.ecosystem}</span>{server.modpack && <span><Archive size={13} />{server.modpack.projectName} · {server.modpack.versionName}</span>}<span><Code2 size={13} />Minecraft {server.minecraftVersion}{server.loaderVersion ? ` · ${server.loaderVersion}` : ''}</span><span><Users size={13} />{server.playersOnline == null ? 'Players unavailable' : `${server.playersOnline}${server.playersMaximum == null ? '' : ` / ${server.playersMaximum}`} online`}</span>{snapshot.connectivity?.serverId === server.id && <span><Wifi size={13} />{snapshot.connectivity.status.title}</span>}</div></div></div>
        <div className={styles.actions}><Button icon={<Share2 size={14} />} onClick={() => setShareOpen(true)}>Share</Button><Button disabled={lifecycle.pending} variant={lifecycle.destructive ? 'danger' : 'primary'} icon={lifecycle.destructive ? <Square size={12} /> : <Play size={13} />} onClick={runLifecycle}>{lifecycle.label}</Button><ActionMenu label={`More actions for ${server.name}`} trigger={<MoreHorizontal size={17} />} open={menuOpen} onOpenChange={next => next ? runMeasuredNavigation('server-actions-menu', () => setMenuOpen(true)) : setMenuOpen(false)} items={[
          ...(isRunning ? [{ label: 'Restart server', icon: <RotateCw size={15} />, onSelect: () => void command('servers.restart', { serverId: server.id }) }] : []),
          { label: 'Open server folder', icon: <FolderOpen size={15} />, onSelect: () => void command('servers.openFolder', { serverId: server.id }) },
          { label: 'Rename server', icon: <Settings size={15} />, onSelect: () => void command('servers.rename', { serverId: server.id }) },
          { label: 'Edit server appearance', icon: <ServerIcon size={15} />, onSelect: () => openSettings('Appearance') },
          { label: 'Delete server…', icon: <Trash2 size={15} />, destructive: true, onSelect: openDelete }
        ]} /></div>
      </div></div>
      <nav className={styles.tabs} aria-label={`${server.name} navigation`}>{tabs.filter(item => item.enabled).map(item => <button className={styles.tab} key={item.id} data-selected={tab === item.id} aria-current={tab === item.id ? 'page' : undefined} onClick={() => navigate(() => runMeasuredNavigation(`server-tab-${item.id}`, () => { setMenuOpen(false); setTab(item.id); }))}><item.icon size={14} />{item.label}</button>)}</nav>
    </section>
    <div className={styles.content}>
      {tab === 'overview' && <Overview server={server} onTab={setTab} onSettings={openSettings} />}
      {tab === 'console' && <ConsolePage server={server} />}
      {tab === 'players' && <PlayersPage server={server} />}
      {tab === 'files' && <FilesPage server={server} />}
      {tab === 'content' && <ContentPage server={server} />}
      {tab === 'backups' && <BackupsPage server={server} />}
      {tab === 'versions' && <VersionsPage server={server} />}
      {tab === 'settings' && <ServerSettingsPage server={server} initialCategory={settingsCategory} />}
    </div>
    <ShareDialog open={shareOpen} onClose={closeShare} server={server} connectivity={snapshot.connectivity?.serverId === server.id ? snapshot.connectivity : null} onManage={() => { closeShare(); openSettings('Connectivity'); }} />
    <DeleteServerDialog open={deleteOpen} preflight={deletePreflight} server={server} onClose={() => setDeleteOpen(false)} />
  </div>;
}

function DeleteServerDialog({ open, preflight, server, onClose }: {
  open: boolean; preflight: ServerDeletionPreflight | null; server: ServerSummary; onClose: () => void;
}) {
  const command = useAppStore(state => state.command);
  const busy = useAppStore(state => state.busy);
  const [mode, setMode] = useState<ServerDeletionMode>('MoveToRecovery');
  const [confirmation, setConfirmation] = useState('');
  const [worldAcknowledged, setWorldAcknowledged] = useState(false);
  const [backupsAcknowledged, setBackupsAcknowledged] = useState(false);
  useEffect(() => {
    if (!open || !preflight) return;
    setMode(preflight.ownershipProven ? 'MoveToRecovery' : 'RemoveFromChunkPilot');
    setConfirmation(''); setWorldAcknowledged(false); setBackupsAcknowledged(false);
  }, [open, preflight?.token]);
  const permanentReady = confirmation === server.name && worldAcknowledged && backupsAcknowledged;
  const canSubmit = preflight && !busy.has('servers.delete') &&
    (mode === 'RemoveFromChunkPilot' || preflight.ownershipProven) &&
    (mode !== 'Permanent' || permanentReady) && !preflight.firewallRemovalRequired;
  const submit = () => {
    if (!preflight || !canSubmit) return;
    void command('servers.delete', {
      serverId: server.id, preflightToken: preflight.token, mode,
      confirmationName: confirmation,
      acknowledgeWorldDeletion: worldAcknowledged,
      acknowledgeManagedBackupDeletion: backupsAcknowledged
    }).then(onClose);
  };
  const createManagedCopy = () => {
    if (!preflight?.canCreateManagedCopy || busy.has('servers.createManagedCopy')) return;
    void command('servers.createManagedCopy', {
      serverId: server.id,
      preflightToken: preflight.token
    }).then(onClose);
  };
  return <Dialog open={open} title={`Delete ${server.name}?`} wide onClose={onClose} footer={<><Button onClick={onClose}>Cancel</Button><Button variant="danger" disabled={!canSubmit} icon={<Trash2 size={14} />} onClick={submit}>{mode === 'Permanent' ? 'Permanently delete' : mode === 'MoveToRecovery' ? 'Move to Recovery' : 'Remove from ChunkPilot'}</Button></>}>
    {!preflight ? <div className={styles.deletionLoading}>Reviewing ownership, operations, schedules, backups, and networking…</div> : <div className={styles.deletionDialog}>
      <div className={styles.deletionSummary}><div><span>Server</span><strong>{preflight.serverName}</strong></div><div><span>Platform</span><strong>{preflight.platform} {preflight.version}</strong></div><div><span>State</span><strong>{preflight.state}</strong></div><div><span>Active schedules</span><strong>{preflight.activeScheduleCount}</strong></div><div><span>Backups</span><strong>{preflight.backupCount}</strong></div><div><span>Internet sharing</span><strong>{preflight.internetSharingConfigured ? 'Configured' : 'Not active'}</strong></div></div>
      <div className={styles.deletionPaths}><span>Server root</span><code>{preflight.managedRoot}</code><span>World location</span><code>{preflight.worldLocation}</code></div>
      <div className={styles.ownershipEvidence}>
        <strong>{preflight.ownershipProven ? 'Deletion ownership proven' : 'Ownership is uncertain'}</strong>
        <p>{preflight.ownershipDetail}</p>
        <details><summary>Ownership evidence</summary>{preflight.ownershipEvidence.map(item => <div key={item.code} data-satisfied={item.satisfied}><span>{item.satisfied ? 'Proven' : 'Not proven'}</span><p>{item.detail}</p></div>)}</details>
        {!preflight.ownershipProven && preflight.canCreateManagedCopy && <div className={styles.managedCopyAction}><div><strong>Create a verified managed copy</strong><p>ChunkPilot copies and verifies every file into a new owned folder, then transfers this registration. The original folder is never changed or claimed.</p></div><Button variant="primary" disabled={busy.has('servers.createManagedCopy')} onClick={createManagedCopy}>{busy.has('servers.createManagedCopy') ? 'Copying…' : 'Create managed copy'}</Button></div>}
      </div>
      {preflight.blockers.length > 0 && <div className={styles.deletionBlockers}><strong>Needs attention</strong>{preflight.blockers.map(item => <p key={item}>{item}</p>)}</div>}
      <div className={styles.deletionModes} role="radiogroup" aria-label="Deletion method">
        <button role="radio" aria-checked={mode === 'MoveToRecovery'} disabled={!preflight.ownershipProven} onClick={() => setMode('MoveToRecovery')}><span><strong>Move to Recovery — recommended</strong><small>Stops the server, withdraws owned Internet access, disables schedules, and moves owned data to a recoverable folder.</small></span></button>
        <button role="radio" aria-checked={mode === 'RemoveFromChunkPilot'} onClick={() => setMode('RemoveFromChunkPilot')}><span><strong>Remove from ChunkPilot</strong><small>Removes management state only. Source folders, worlds, and backup files are left unchanged.</small></span></button>
        <button role="radio" aria-checked={mode === 'Permanent'} disabled={!preflight.ownershipProven} onClick={() => setMode('Permanent')}><span><strong>Permanently delete</strong><small>Available only for marker-proven ChunkPilot-managed data. External or ownership-uncertain paths are never deleted.</small></span></button>
      </div>
      {preflight.firewallRemovalRequired && <div className={styles.deletionBlockers}><strong>Windows Firewall access must be removed first</strong><p>Open Connectivity, remove the exact ChunkPilot-owned rule, then review deletion again. The server remains registered until Windows confirms removal.</p></div>}
      {preflight.protectedExternalPaths.length > 0 && <div className={styles.deletionProtected}><strong>Protected external paths</strong>{preflight.protectedExternalPaths.map(path => <code key={path}>{path}</code>)}</div>}
      {mode === 'Permanent' && <div className={styles.permanentConfirmation}><label><span>Type <strong>{server.name}</strong></span><TextInput value={confirmation} onChange={event => setConfirmation(event.target.value)} /></label><label><input type="checkbox" checked={worldAcknowledged} onChange={event => setWorldAcknowledged(event.target.checked)} /> Permanently delete the marker-proven world inside this server root.</label><label><input type="checkbox" checked={backupsAcknowledged} onChange={event => setBackupsAcknowledged(event.target.checked)} /> Permanently delete the {preflight.managedBackupPaths.length} managed backup files listed by ChunkPilot.</label></div>}
    </div>}
  </Dialog>;
}

function ShareDialog({ open, onClose, server, connectivity, onManage }: {
  open: boolean; onClose: () => void; server: ServerSummary; connectivity: ConnectivitySnapshot | null; onManage: () => void;
}) {
  return <Dialog open={open} title={`Share ${server.name}`} wide onClose={onClose} footer={<><Button onClick={onClose}>Close</Button><Button variant="primary" onClick={onManage}>Manage connectivity</Button></>}>
    {connectivity ? <div className={styles.shareSheet}>
      <ConnectionSummary server={server} connectivity={connectivity} showAll />
      <div className={styles.shareFacts}><div><span>Server type</span><strong>{server.ecosystem}</strong></div><div><span>Minecraft version</span><strong>{server.minecraftVersion}</strong></div><div><span>Server state</span><strong>{server.state}</strong></div></div>
    </div> : <EmptyState title="Connection state unavailable" detail="ChunkPilot has not received authoritative networking state for this server." />}
  </Dialog>;
}

function UpdateInstallDialog({ open, onClose, server, update }: {
  open: boolean; onClose: () => void; server: ServerSummary; update: UpdateSummary;
}) {
  const command = useAppStore(state => state.command);
  const busy = useAppStore(state => state.busy.has('versions.install'));
  const target = update.latestVersionName ?? update.targetVersionId ?? 'selected update';
  const published = update.targetPublishedAt ? new Date(update.targetPublishedAt).toLocaleString() : 'Not provided';
  const compatibility = update.compatibilityReasons?.length ? update.compatibilityReasons : ['No compatibility changes were reported by the provider.'];
  const confirm = () => {
    onClose();
    void command('versions.install', { serverId: server.id, operationId: crypto.randomUUID() });
  };
  return <Dialog open={open} title={`Install ${target}?`} wide onClose={onClose} footer={<><Button onClick={onClose}>Cancel</Button><Button variant="primary" disabled={busy} onClick={confirm}>{busy ? 'Starting…' : 'Install update'}</Button></>}>
    <div className={styles.updateProposal}>
      <dl>
        <div><dt>Server</dt><dd>{server.name}</dd></div>
        <div><dt>Installed</dt><dd>{update.installedVersionName ?? 'Unavailable'}</dd></div>
        <div><dt>Target</dt><dd>{target}</dd></div>
        <div><dt>Provider</dt><dd>{update.provider ?? 'Unavailable'}</dd></div>
        <div><dt>Published</dt><dd>{published}</dd></div>
        <div><dt>Download</dt><dd>{bytes(update.downloadSizeBytes ?? null)}</dd></div>
        <div><dt>Minecraft</dt><dd>{update.minecraftVersion ?? server.minecraftVersion}</dd></div>
        <div><dt>Platform</dt><dd>{update.loader ?? server.ecosystem}{update.loaderVersion ? ` ${update.loaderVersion}` : ''}</dd></div>
      </dl>
      <section><strong>Compatibility</strong><ul>{compatibility.map(reason => <li key={reason}>{reason}</li>)}</ul></section>
      <p>ChunkPilot will save and stop the owned process when needed, create and verify a rollback snapshot, stage and verify the package, switch atomically, start for validation, and roll back if activation fails.</p>
    </div>
  </Dialog>;
}

function Overview({ server, onTab, onSettings }: { server: ServerSummary; onTab: (tab: Tab) => void; onSettings: (category: string) => void }) {
  const snapshot = useAppStore(state => state.snapshot)!;
  const command = useAppStore(state => state.command);
  const running = server.state === 'Running';
  const connectivity = snapshot.connectivity?.serverId === server.id ? snapshot.connectivity : null;
  const memoryPercent = server.memoryBytes == null || !server.maximumMemoryBytes ? null : server.memoryBytes / server.maximumMemoryBytes * 100;
  return <>
    {server.crashAnalysis && ['Crashed', 'Unresponsive'].includes(server.state)
      ? <CrashAnalysisPanel server={server} onConsole={() => onTab('console')} />
      : server.lastError && <div className={styles.attentionBanner}><span /><div><strong>Server needs attention</strong><p>{server.lastError}</p></div><Button onClick={() => onTab('console')}>Open console</Button></div>}
    <div className={styles.overviewWorkbench}>
      <section className={styles.performanceSurface}><PanelTitle title="Live performance" meta={running ? 'Current session · 15 min' : 'Server stopped'} /><div className={styles.performance}>{server.cpuPercent == null || server.samples.length < 2 ? <EmptyState title="No performance data" detail={running ? 'ChunkPilot is waiting for enough real samples.' : 'Start the server to collect performance data.'} /> : <><div className={styles.performanceHead}><strong>{server.cpuPercent.toFixed(1)}%</strong><span>CPU now · {bytes(server.memoryBytes)} memory</span></div><Sparkline values={server.samples.map(sample => sample.cpuPercent)} /><div className={styles.resourceBar}><span>Memory</span><i><b style={{ width: `${Math.min(100, memoryPercent ?? 0)}%` }} /></i><strong>{memoryPercent == null ? 'Unavailable' : `${memoryPercent.toFixed(0)}%`}</strong></div></>}</div></section>
      <aside className={styles.statusRail}>
        <div className={styles.statusItem}><header><Wifi size={15} /><span>Joinability</span></header><strong>{connectivity?.status.title ?? 'Not established'}</strong><p>{connectivity?.status.detail ?? 'No authoritative connection state is available.'}</p></div>
        <div className={styles.statusItem}><header><Users size={15} /><span>Players</span></header><strong>{server.playersOnline == null ? 'Unknown' : `${server.playersOnline}${server.playersMaximum == null ? '' : ` of ${server.playersMaximum}`} online`}</strong><p>{server.playerStatus?.detail ?? (server.playersOnline == null ? 'The server has not reported a count; unknown is not shown as zero.' : 'Reported by the server')}</p></div>
        <div className={styles.statusItem}><header><ShieldCheck size={15} /><span>World protection</span></header><strong>{server.lastBackupAt ? 'Recovery point verified' : 'Backup not confirmed'}</strong><p>{server.lastBackupAt ? new Date(server.lastBackupAt).toLocaleString() : 'Create a backup before risky changes.'}</p></div>
        <div className={styles.statusItem}><header><History size={15} /><span>Version</span></header><strong>{server.ecosystem} {server.minecraftVersion}</strong><p>{server.loaderVersion ? `Loader ${server.loaderVersion}` : 'Installed version'}</p></div>
      </aside>
    </div>
    <div className={styles.overviewLower}>
      <section className={styles.panel}><PanelTitle title="Recent console" action={<Button variant="subtle" onClick={() => onTab('console')}>Open console</Button>} /><div className={styles.consolePreview}>{snapshot.console.length ? snapshot.console.slice(-7).map(line => <div key={line.sequence}><span>{new Date(line.timestamp).toLocaleTimeString()} </span>{line.text}</div>) : <p>No console output is available.</p>}</div></section>
      <section className={styles.panel}><PanelTitle title="How to join" /><div className={styles.connectionPanel}><ConnectionSummary server={server} connectivity={connectivity} compact onManage={() => onSettings('Connectivity')} /></div></section>
    </div>
  </>;
}

function CrashAnalysisPanel({ server, onConsole }: { server: ServerSummary; onConsole: () => void }) {
  const command = useAppStore(state => state.command);
  const busy = useAppStore(state => state.busy);
  const [open, setOpen] = useState(() => new URLSearchParams(window.location.search).get('mode') === 'crash-details');
  const report = server.crashAnalysis!;
  const confidence = report.confidence.replace(/([a-z])([A-Z])/g, '$1 $2');
  const canRetry = server.state === 'Crashed' || server.state === 'Stopped';
  return <>
    <section className={styles.crashPanel} aria-labelledby={`crash-${report.reportId}`}>
      <CircleAlert size={21} aria-hidden="true" />
      <div className={styles.crashSummary}>
        <div><StatusBadge tone={report.confidence === 'Unknown' ? 'warning' : 'danger'}>{confidence}</StatusBadge><span>{new Date(report.analyzedAt).toLocaleString()}</span></div>
        <strong id={`crash-${report.reportId}`}>{report.title}</strong>
        <p>{report.summary}</p>
      </div>
      <div className={styles.crashActions}><Button onClick={onConsole}>Open console</Button><Button variant="primary" onClick={() => setOpen(true)}>View analysis</Button></div>
    </section>
    <Dialog open={open} wide title="Crash analysis" onClose={() => setOpen(false)} footer={<>
      <Button onClick={() => setOpen(false)}>Close</Button>
      <Button disabled={busy.has('diagnostics.bundle')} onClick={() => void command('diagnostics.bundle', { serverId: server.id })}>Create support bundle</Button>
      <Button variant="primary" disabled={!canRetry || busy.has('servers.start')} onClick={() => { setOpen(false); void command('servers.start', { serverId: server.id }); }}>Retry start</Button>
    </>}>
      <div className={styles.crashDetails}>
        <div className={styles.crashDetailGrid}>
          <Detail label="Confidence" value={confidence} />
          <Detail label="Exit code" value={report.exitCode == null ? 'Unavailable' : String(report.exitCode)} />
          <Detail label="Server" value={report.serverIdentity} />
          <Detail label="Runtime" value={report.runtimeIdentity} />
          <Detail label="Reached readiness" value={report.reachedReadiness ? 'Yes' : 'No'} />
          <Detail label="Active operation" value={report.activeOperation ?? 'None recorded'} />
        </div>
        <section><header><h3>Local evidence</h3><Button variant="subtle" onClick={() => void command('diagnostics.openLogs', { serverId: server.id })}>Open logs</Button></header>
          {report.evidence.length ? <div className={styles.crashEvidence}>{report.evidence.map(item => <div key={`${item.source}:${item.excerpt}`}><span>{item.source}</span><code>{item.excerpt}</code></div>)}</div> : <p>No bounded log excerpt identified a reliable cause.</p>}
        </section>
        <section><h3>Recommended next steps</h3>{report.recommendedSteps.length ? <ol className={styles.crashSteps}>{report.recommendedSteps.map(step => <li key={step}>{step}</li>)}</ol> : <p>Review the console and local logs before retrying. ChunkPilot did not identify a safe automatic repair.</p>}</section>
      </div>
    </Dialog>
  </>;
}

function ConsolePage({ server }: { server: ServerSummary }) {
  const snapshot = useAppStore(state => state.snapshot)!; const command = useAppStore(state => state.command);
  const [search, setSearch] = useState(''); const [entry, setEntry] = useState(''); const [follow, setFollow] = useState(true);
  const [wrap, setWrap] = useState(() => new URLSearchParams(window.location.search).get('mode') !== 'console-unwrapped' && window.localStorage.getItem('chunkpilot.console.wrap') !== 'false');
  const lines = useMemo(() => snapshot.console.filter(line => line.text.toLowerCase().includes(search.toLowerCase())), [snapshot.console, search]);
  const parentRef = useRef<HTMLDivElement>(null); const virtual = useVirtualizer({ count: lines.length, getScrollElement: () => parentRef.current, estimateSize: () => wrap ? 36 : 24, measureElement: measureConsoleRow, overscan: 18 });
  const measureRow = useCallback((element: HTMLDivElement | null) => {
    if (!element || !wrap) return;
    virtual.measureElement(element);
    const index = Number(element.dataset.index);
    if (!Number.isInteger(index)) return;
    requestAnimationFrame(() => {
      if (element.isConnected) virtual.resizeItem(index, measureConsoleRow(element));
    });
  }, [virtual, wrap]);
  useEffect(() => { window.localStorage.setItem('chunkpilot.console.wrap', String(wrap)); virtual.measure(); }, [wrap, virtual]);
  useEffect(() => {
    const viewport = parentRef.current;
    if (!wrap || !viewport || typeof ResizeObserver === 'undefined') return;
    let frame = 0;
    const resizeVisibleRows = () => {
      cancelAnimationFrame(frame);
      frame = requestAnimationFrame(() => viewport.querySelectorAll<HTMLDivElement>('[data-index][data-wrap="true"]').forEach(element => {
        const index = Number(element.dataset.index);
        if (Number.isInteger(index)) virtual.resizeItem(index, measureConsoleRow(element));
      }));
    };
    const observer = new ResizeObserver(resizeVisibleRows);
    observer.observe(viewport);
    resizeVisibleRows();
    return () => { observer.disconnect(); cancelAnimationFrame(frame); };
  }, [wrap, virtual]);
  useEffect(() => { if (follow && lines.length) virtual.scrollToIndex(lines.length - 1, { align: 'end' }); }, [lines.length, follow]);
  const send = () => { const value = entry.trim(); if (!value) return; setEntry(''); void command('console.send', { serverId: server.id, command: value }); };
  return <section className={styles.consolePage}><div className={styles.consoleToolbar}><SearchInput value={search} onChange={event => setSearch(event.target.value)} placeholder="Search console" aria-label="Search console" /><StatusBadge tone={server.state === 'Running' ? 'success' : 'neutral'}>{server.state === 'Running' ? 'Connected' : 'Stopped'}</StatusBadge><span /><label className={styles.wrapToggle}><input type="checkbox" checked={wrap} onChange={event => setWrap(event.target.checked)} />Wrap long lines</label><Button variant="subtle" onClick={() => setFollow(value => !value)}>{follow ? 'Following output' : 'Follow paused'}</Button></div><div className={styles.consoleViewport} data-wrap={wrap} ref={parentRef} onScroll={() => { const el = parentRef.current; if (el) setFollow(el.scrollHeight - el.scrollTop - el.clientHeight < 24); }}><div style={{ height: virtual.getTotalSize(), position: 'relative' }}>{virtual.getVirtualItems().map(row => { const line = lines[row.index]; const level = line.stream.toUpperCase(); return <div key={line.sequence} ref={wrap ? measureRow : undefined} data-index={row.index} data-wrap={wrap} className={styles.consoleLine} style={{ ...(wrap ? {} : { height: row.size }), transform: `translateY(${row.start}px)` }}><span className={styles.consoleTime}>{new Date(line.timestamp).toLocaleTimeString()}</span><span className={styles.consoleLevel} data-level={level}>{level}</span><span className={styles.consoleText}>{line.text}</span></div>; })}</div></div><form className={styles.commandBar} onSubmit={event => { event.preventDefault(); send(); }}><TextInput value={entry} onChange={event => setEntry(event.target.value)} placeholder={server.state === 'Running' ? 'Enter a server command' : 'Start the server to send commands'} disabled={server.state !== 'Running'} aria-label="Server command" /><Button variant="primary" icon={<Send size={14} />} disabled={server.state !== 'Running' || !entry.trim()}>Send</Button></form></section>;
}

function PlayersPage({ server }: { server: ServerSummary }) {
  const snapshot = useAppStore(state => state.snapshot)!; const command = useAppStore(state => state.command); const busy = useAppStore(state => state.busy); const [search, setSearch] = useState('');
  const [newPlayer, setNewPlayer] = useState('');
  const [pending, setPending] = useState<{ player: string; action: string; label: string; detail: string } | null>(null);
  const players = snapshot.players.filter(player => player.name.toLowerCase().includes(search.toLowerCase()));
  const access = snapshot.playerAccess?.serverId === server.id ? snapshot.playerAccess : null;
  const canModerate = Boolean(access?.serverRunning);
  const moderate = (playerName: string, action: string) => void command('players.moderate', { serverId: server.id, playerName, action });
  const setWhitelist = (enabled: boolean) => void command('players.setWhitelist', { serverId: server.id, enabled });
  const addAllowlist = () => { const playerName = newPlayer.trim(); if (!playerName) return; void command('players.addAllowlist', { serverId: server.id, playerName }).then(() => setNewPlayer('')); };
  const confirm = (player: string, action: string, label: string, detail: string) => setPending({ player, action, label, detail });
  const evidence = server.playerStatus?.detail ?? 'ChunkPilot has not received player-status evidence for this server yet.';
  const emptyTitle = snapshot.players.length ? 'No matching players' : server.playersOnline === 0 && server.playerStatus?.exact ? 'No players are online or known yet' : 'Player information unavailable';
  const emptyDetail = snapshot.players.length ? 'Try a different player name.' : server.state !== 'Running' ? 'Start the server to refresh live player status. Saved allowlist, operator, and ban records remain visible when available.' : evidence;
  return <div className={styles.playerWorkspace}>
    <section className={styles.playerSummary}>
      <div><span>Online now</span><strong>{server.playersOnline == null ? 'Unknown' : server.playersMaximum == null ? server.playersOnline : `${server.playersOnline} / ${server.playersMaximum}`}</strong><small>{evidence}</small></div>
      <div><div className={styles.playerSummaryHeading}><span>Allowlist</span>{access?.supportsAllowlist && <Switch checked={access.whitelistEnabled} label={access.whitelistEnabled ? 'Turn allowlist off' : 'Turn allowlist on'} disabled={busy.has('players.setWhitelist')} onClick={() => setWhitelist(!access.whitelistEnabled)} />}</div><strong>{access ? access.whitelistEnabled ? 'On' : 'Off' : 'Unavailable'}</strong><small>{access?.supportsAllowlist ? server.state === 'Running' ? 'Changes apply immediately.' : 'Changes apply the next time the server starts.' : 'Support has not been confirmed for this server.'}</small></div>
      <div><span>Management</span><strong>{canModerate ? 'Available' : server.state === 'Running' ? 'Unavailable' : 'Server stopped'}</strong><small>{access?.capabilityKnown ? 'Actions use the authoritative server console and access files.' : 'ChunkPilot is still identifying this imported server.'}</small></div>
    </section>
    <section className={styles.panel}>
      <div className={styles.playerToolbar}><SearchInput value={search} onChange={event => setSearch(event.target.value)} placeholder="Search players" aria-label="Search players" />{access?.supportsAllowlist && <form onSubmit={event => { event.preventDefault(); addAllowlist(); }}><TextInput value={newPlayer} maxLength={16} onChange={event => setNewPlayer(event.target.value)} placeholder="Minecraft player name" aria-label="Minecraft player name" /><Button variant="primary" disabled={!canModerate || !newPlayer.trim() || busy.has('players.addAllowlist')}>{busy.has('players.addAllowlist') ? 'Adding…' : 'Add to allowlist'}</Button></form>}</div>
      {access?.error && <div className={styles.playerError} role="alert">{access.error}</div>}
      {players.length ? <table className={styles.table}><thead><tr><th>Player</th><th>Status</th><th>Allowlist</th><th>Role</th><th aria-label="Actions" /></tr></thead><tbody>{players.map(player => <tr key={player.name}><td><strong>{player.name}</strong></td><td><StatusBadge tone={player.banned ? 'danger' : player.online ? 'success' : 'neutral'}>{player.banned ? 'Banned' : player.online ? 'Online' : 'Known player'}</StatusBadge></td><td>{player.allowlisted ? 'Allowed' : 'Not allowed'}</td><td>{player.operator ? 'Operator' : 'Player'}</td><td><div className={styles.tableActions}>{access?.supportsAllowlist && <Button variant="subtle" disabled={!canModerate} onClick={() => player.allowlisted ? confirm(player.name, 'RemoveFromWhitelist', 'Remove access', `${player.name} will no longer be able to join an allowlist-only server.`) : moderate(player.name, 'AddToWhitelist')}>{player.allowlisted ? 'Remove allowlist' : 'Allowlist'}</Button>}<ActionMenu label={`Moderation actions for ${player.name}`} trigger={<MoreHorizontal size={16} />} items={[
        ...(access?.supportsOperators ? [{ label: player.operator ? 'Remove operator' : 'Make operator', icon: <ShieldCheck size={15} />, disabled: !canModerate, onSelect: () => player.operator ? confirm(player.name, 'RemoveOperator', 'Remove operator', `${player.name} will lose operator permissions.`) : moderate(player.name, 'GrantOperator') }] : []),
        ...(player.online ? [{ label: 'Kick', icon: <Square size={13} />, disabled: !canModerate, onSelect: () => confirm(player.name, 'Kick', 'Kick player', `${player.name} will be disconnected from the running server.`) }] : []),
        ...(access?.supportsPlayerBans ? [{ label: player.banned ? 'Pardon' : 'Ban', icon: player.banned ? <Check size={15} /> : <Trash2 size={15} />, disabled: !canModerate, destructive: !player.banned, onSelect: () => player.banned ? moderate(player.name, 'Pardon') : confirm(player.name, 'Ban', 'Ban player', `${player.name} will be banned and disconnected.`) }] : [])
      ]} /></div></td></tr>)}</tbody></table> : <EmptyState title={emptyTitle} detail={emptyDetail} />}
      <ConfirmDialog open={pending !== null} title={pending?.label ?? ''} detail={pending?.detail ?? ''} confirmLabel={pending?.label ?? 'Confirm'} destructive onCancel={() => setPending(null)} onConfirm={() => { if (pending) moderate(pending.player, pending.action); setPending(null); }} />
    </section>
  </div>;
}

function FilesPage({ server }: { server: ServerSummary }) {
  const snapshot = useAppStore(state => state.snapshot)!; const command = useAppStore(state => state.command); const [search, setSearch] = useState('');
  const [loaded, setLoaded] = useState<TextFileContent | null>(null); const [draft, setDraft] = useState(''); const [loading, setLoading] = useState('');
  const files = snapshot.files.filter(file => file.name.toLowerCase().includes(search.toLowerCase()));
  const open = (file: (typeof files)[number]) => { setLoaded(null); setDraft(''); if (file.kind === 'folder') { void command('files.navigate', { serverId: server.id, relativePath: file.relativePath }); return; } if (file.kind !== 'editable') return; setLoading(file.relativePath); void command<TextFileContent>('files.read', { serverId: server.id, relativePath: file.relativePath }).then(value => { setLoaded(value); setDraft(value.content); }).finally(() => setLoading('')); };
  const save = () => { if (!loaded || draft === loaded.content) return; void command('files.write', { serverId: server.id, file: { ...loaded, content: draft } }).then(() => setLoaded({ ...loaded, content: draft })); };
  const parent = snapshot.currentFolder.includes('/') ? snapshot.currentFolder.slice(0, snapshot.currentFolder.lastIndexOf('/')) : '';
  return <div className={styles.fileLayout}><section className={styles.panel}><div className={styles.pathBar}><div className={page.actions}>{snapshot.currentFolder && <Button variant="subtle" onClick={() => void command('files.navigate', { serverId: server.id, relativePath: parent })}>Up</Button>}<code>{snapshot.currentFolder || 'Server folder'}</code></div><div className={page.actions}><SearchInput value={search} onChange={event => setSearch(event.target.value)} placeholder="Search files" aria-label="Search files" /><Button icon={<FolderOpen size={14} />} onClick={() => void command('servers.openFolder', { serverId: server.id })}>Explorer</Button></div></div>{files.length ? <table className={styles.table}><thead><tr><th>Name</th><th>Type</th><th>Size</th><th>Modified</th></tr></thead><tbody>{files.map(file => <tr key={file.relativePath} className={file.relativePath === loaded?.relativePath ? styles.selectedRow : undefined} onDoubleClick={() => open(file)}><td><button className={styles.fileButton} onClick={() => open(file)}><span className={page.identity}>{file.kind === 'folder' ? <Folder size={16} color="var(--cp-warning)" /> : <File size={16} />}<strong>{file.name}</strong></span></button></td><td>{file.kind === 'folder' ? 'Folder' : file.kind === 'editable' ? 'Editable text' : file.kind === 'too-large' ? 'Use Explorer' : 'Binary'}</td><td>{bytes(file.sizeBytes)}</td><td>{file.modifiedAt ? new Date(file.modifiedAt).toLocaleString() : 'Unavailable'}</td></tr>)}</tbody></table> : <EmptyState title="No files to show" detail="No safe file entries match this folder and search." />}</section><section className={styles.fileEditor}><PanelTitle title={loaded?.relativePath ?? (loading ? 'Loading file…' : 'Text editor')} meta={loaded ? `${loaded.encodingName}${loaded.hasBom ? ' · BOM' : ''}` : 'Select an editable text file'} />{loaded ? <><textarea aria-label={`Edit ${loaded.relativePath}`} value={draft} onChange={event => setDraft(event.target.value)} spellCheck={false} /><footer><span>{draft === loaded.content ? 'No unsaved changes' : 'Unsaved changes'}</span><div className={page.actions}><Button disabled={draft === loaded.content} onClick={() => setDraft(loaded.content)}>Discard</Button><Button variant="primary" disabled={draft === loaded.content} onClick={save}>Save file</Button></div></footer></> : <EmptyState title={loading ? 'Loading file' : 'No file selected'} detail="Choose a safe text file to inspect or edit. ChunkPilot confines changes to this server and writes them atomically." />}</section></div>;
}

function ContentPage({ server }: { server: ServerSummary }) {
  const kind = server.capabilities.content;
  if (kind === 'unsupported') return <section className={styles.panel}><div className={styles.unsupported}><CloudOff size={28} color="var(--cp-text-muted)" /><h2>Content management unavailable</h2><p>ChunkPilot has not confirmed a supported content model for this server. No catalog or installation state is being invented.</p></div></section>;
  if (kind === 'plugins') return <AddonsPage server={server} kind="plugins" />;
  if (kind === 'mods') return <AddonsPage server={server} kind="mods" />;
  if (kind === 'modpack') return <ModpackPage server={server} />;
  const label = contentLabel(server);
  return <section className={styles.panel}><PanelTitle title={label} meta="Installed content" /><div className={styles.unsupported}><Box size={28} color="var(--cp-accent)" /><h2>No installed {label.toLowerCase()} reported</h2><p>This destination adapts to the server capability. Remote discovery is not shown because this build has no authoritative provider catalog connected to the WebUI.</p></div></section>;
}

function ModpackPage({ server }: { server: ServerSummary }) {
  const snapshot = useAppStore(state => state.snapshot)!;
  const command = useAppStore(state => state.command);
  const busy = useAppStore(state => state.busy);
  const update = snapshot.update;
  const [showInstall, setShowInstall] = useState(false);
  const pack = server.modpack;
  const providerUpdates = pack?.provider === 'Modrinth' || pack?.provider === 'CurseForge';
  const versionBusy = ['versions.check', 'versions.install', 'versions.rollback', 'versions.cancel'].some(method => busy.has(method));
  if (!pack || !update?.sourceLinked)
    return <section className={styles.panel}><EmptyState title="Pack identity unavailable" detail="ChunkPilot has not established an exact provider project and release for this server. Individual mods remain visible in the managed content inventory, but pack-level updates are disabled until identity is proven." /></section>;
  return <div className={styles.versionStack}>
    <section className={styles.panel}>
      <div className={styles.pathBar}>
        <div><strong>{pack.projectName}</strong> <span className={page.muted}>· {pack.provider} · {pack.versionName}</span></div>
        <div className={page.actions}>
          {providerUpdates && update.cancellable && <Button variant="subtle" disabled={busy.has('versions.cancel')} onClick={() => void command('versions.cancel', { serverId: server.id })}>Cancel safely</Button>}
          {providerUpdates && update.canInstall && <Button variant="primary" disabled={versionBusy} onClick={() => setShowInstall(true)}>Install pack {update.latestVersionName ?? 'update'}</Button>}
          {providerUpdates && <Button icon={<RotateCw size={14} />} disabled={versionBusy} onClick={() => void command('versions.check', { serverId: server.id })}>{busy.has('versions.check') ? 'Checking…' : 'Check pack release'}</Button>}
        </div>
      </div>
      <div className={styles.catalogPolicy}>
        <div><span>Installed release</span><strong>{update.installedVersionName || pack.versionName}</strong></div>
        <div><span>Platform</span><strong>{update.loader || server.ecosystem}{update.loaderVersion ? ` ${update.loaderVersion}` : ''}</strong></div>
        <p>Minecraft {update.minecraftVersion || server.minecraftVersion} · {update.releaseChannel || 'Channel unavailable'}. Pack updates compare exact provider releases and never update constituent mods independently.</p>
      </div>
      <div className={styles.updateSummary}>
        <div><StatusBadge tone={providerUpdates && update.canInstall ? 'warning' : providerUpdates ? 'success' : 'neutral'}>{providerUpdates ? update.status : 'Local pack'}</StatusBadge><strong>{providerUpdates ? update.detail : 'Provider updates are unavailable until this local archive is linked to an exact project release.'}</strong><span>{providerUpdates ? update.checkedAt ? `Last checked ${new Date(update.checkedAt).toLocaleString()}` : 'Not checked yet' : 'The inspected archive remains the installed baseline.'}</span></div>
        {update.operationPercent != null && <div className={styles.updateProgress} aria-label={`Pack update progress ${update.operationPercent.toFixed(0)} percent`}><i><b style={{ width: `${Math.max(0, Math.min(100, update.operationPercent))}%` }} /></i><span>{update.operationStep || update.operationState} · {update.operationPercent.toFixed(0)}%</span>{update.operationDetail && update.operationDetail !== update.operationStep && <small>{update.operationDetail}</small>}</div>}
      </div>
      <UpdateInstallDialog open={showInstall} onClose={() => setShowInstall(false)} server={server} update={update} />
    </section>
    <section className={styles.versionEvidence}>
      <header><div><strong>Pack ownership</strong><p>ChunkPilot owns the exact pack release as one recovery-backed unit. Files added outside the pack remain user-owned; changed pack-managed files are reviewed during update migration.</p></div><StatusBadge tone="info">Exact release linked</StatusBadge></header>
      <dl className={styles.evidenceGrid}><div><dt>Provider</dt><dd>{pack.provider}</dd></div><div><dt>Project</dt><dd>{pack.projectName}</dd></div><div><dt>Release ID</dt><dd>{pack.versionId}</dd></div><div><dt>Project ID</dt><dd>{pack.projectId}</dd></div><div><dt>Update model</dt><dd>Whole pack release</dd></div></dl>
      <p className={styles.contextNote}>Use Versions for verified recovery snapshots and rollback history. Use Mods to inspect pack-managed and user-added JAR inventory only; ordinary per-mod update is not applied to this linked pack.</p>
    </section>
  </div>;
}

type AddonSection = 'installed' | 'browse' | 'updates' | 'problems';
type PluginUpdateMatch = { pluginPath: string; currentVersionId: string; release: PluginRelease | null; error?: string };
type LocalPluginSelection = {
  cancelled: boolean;
  token?: string;
  fileName?: string;
  expiresAt?: string;
  plugin?: {
    name: string;
    version: string;
    id: string;
    loader: string;
    sizeBytes: number;
    dependencies: string[];
    compatibility: 'Compatible' | 'LikelyCompatible' | 'Incompatible' | 'Unknown';
    compatibilityReason: string;
    clientRequirement: 'ServerOnly' | 'ClientOptional' | 'ClientAndServer' | 'ClientOnly' | 'Unknown';
  };
};

function AddonsPage({ server, kind }: { server: ServerSummary; kind: 'plugins' | 'mods' }) {
  const snapshot = useAppStore(state => state.snapshot)!;
  const command = useAppStore(state => state.command);
  const bridge = useAppStore(state => state.bridge);
  const busy = useAppStore(state => state.busy);
  const [section, setSection] = useState<AddonSection>(() => {
    const requested = new URLSearchParams(window.location.search).get('mode')?.replace(`${kind}-`, '') as AddonSection | undefined;
    return requested && ['installed', 'browse', 'updates', 'problems'].includes(requested) ? requested : 'installed';
  });
  const [providers, setProviders] = useState<PluginProviderStatus[]>([]);
  const [query, setQuery] = useState('');
  const [results, setResults] = useState<PluginProject[]>([]);
  const [selected, setSelected] = useState<PluginProject | null>(null);
  const [release, setRelease] = useState<PluginRelease | null>(null);
  const [installPlan, setInstallPlan] = useState<PluginInstallPlan | null>(null);
  const [browseError, setBrowseError] = useState('');
  const [updates, setUpdates] = useState<PluginUpdateMatch[]>([]);
  const [updatesLoading, setUpdatesLoading] = useState(false);
  const [pending, setPending] = useState<{ kind: 'remove' | 'toggle'; path: string; name: string; enabled?: boolean } | null>(null);
  const [pendingInstall, setPendingInstall] = useState<PluginRelease | null>(null);
  const [pendingDependencyBatch, setPendingDependencyBatch] = useState(false);
  const [contentOperations, setContentOperations] = useState<ManagedContentOperation[]>([]);
  const [contentPollEpoch, setContentPollEpoch] = useState(0);
  const [localSelection, setLocalSelection] = useState<LocalPluginSelection | null>(null);
  const [configPlugin, setConfigPlugin] = useState<(typeof snapshot.plugins)[number] | null>(null);
  const isMod = kind === 'mods';
  const singular = isMod ? 'mod' : 'plugin';
  const title = isMod ? 'Mods' : 'Plugins';
  const methods = isMod ? {
    providers: 'mods.providers', search: 'mods.search', release: 'mods.release', plan: 'mods.plan', chooseLocal: 'mods.chooseLocal',
    installLocal: 'mods.installLocal', install: 'mods.install', installPlan: 'mods.installPlan', setEnabled: 'mods.setEnabled', remove: 'mods.remove',
    openFolder: 'mods.openFolder', configFiles: 'mods.configFiles'
  } as const : {
    providers: 'plugins.providers', search: 'plugins.search', release: 'plugins.release', plan: 'plugins.plan', chooseLocal: 'plugins.chooseLocal',
    installLocal: 'plugins.installLocal', install: 'plugins.install', installPlan: 'plugins.installPlan', setEnabled: 'plugins.setEnabled', remove: 'plugins.remove',
    openFolder: 'plugins.openFolder', configFiles: 'plugins.configFiles'
  } as const;
  const stopped = server.state === 'Stopped';
  const running = server.state === 'Running';
  const canApply = stopped || running;
  const hasActiveContentOperation = contentOperations.some(operation => !operation.isTerminal);
  const hasActiveContentOperationRef = useRef(hasActiveContentOperation);
  hasActiveContentOperationRef.current = hasActiveContentOperation;
  const operationBusy = [...busy].some(method => method.startsWith(`${kind}.`)) || hasActiveContentOperation;
  const updateIdentityKey = useMemo(() => snapshot.plugins
    .filter(plugin => plugin.provider === 'Modrinth' && plugin.providerProjectId)
    .slice(0, 100)
    .map(plugin => `${plugin.relativePath}|${plugin.providerProjectId}|${plugin.providerVersionId ?? ''}`)
    .sort()
    .join('\n'), [snapshot.plugins]);
  useEffect(() => { void command<PluginProviderStatus[]>(methods.providers, { serverId: server.id }).then(setProviders).catch(() => undefined); }, [command, server.id, methods.providers]);
  useEffect(() => {
    let active = true;
    let timer = 0;
    if (!bridge) return () => { active = false; };
    const poll = () => void bridge.request<ManagedContentOperation[]>('content.operations', { serverId: server.id })
      .then(next => {
        if (!active) return;
        const operations = Array.isArray(next) ? next : [];
        setContentOperations(operations);
        if (operations.some(operation => !operation.isTerminal))
          timer = window.setTimeout(poll, 500);
      })
      .catch(() => { if (active && hasActiveContentOperationRef.current) timer = window.setTimeout(poll, 500); });
    poll();
    return () => { active = false; window.clearTimeout(timer); };
  }, [bridge, server.id, contentPollEpoch]);
  useEffect(() => {
    if (section !== 'updates') return;
    let active = true;
    const identified = snapshot.plugins
      .filter(plugin => plugin.provider === 'Modrinth' && plugin.providerProjectId)
      .slice(0, 100);
    setUpdatesLoading(true);
    void (async () => {
      const next: PluginUpdateMatch[] = [];
      for (const plugin of identified) {
        try {
          const latest = await command<PluginRelease | null>(methods.release, {
            serverId: server.id,
            projectId: plugin.providerProjectId
          });
          next.push({ pluginPath: plugin.relativePath, currentVersionId: plugin.providerVersionId ?? '', release: latest });
        } catch (error) {
          next.push({ pluginPath: plugin.relativePath, currentVersionId: plugin.providerVersionId ?? '', release: null,
            error: error instanceof Error ? error.message : 'Update lookup failed.' });
        }
        if (!active) return;
      }
      if (active) setUpdates(next);
    })().finally(() => { if (active) setUpdatesLoading(false); });
    return () => { active = false; };
  }, [section, server.id, updateIdentityKey, command, methods.release]);
  const search = () => {
    setBrowseError(''); setSelected(null); setRelease(null); setInstallPlan(null);
    void command<PluginProject[]>(methods.search, { serverId: server.id, search: query, limit: 20 })
      .then(setResults).catch(error => setBrowseError(error instanceof Error ? error.message : `${title} search is unavailable.`));
  };
  const choose = (project: PluginProject) => {
    setSelected(project); setRelease(null); setInstallPlan(null); setBrowseError('');
    void command<PluginRelease | null>(methods.release, { serverId: server.id, projectId: project.projectId })
      .then(resolved => {
        setRelease(resolved);
        if (resolved)
          void command<PluginInstallPlan>(methods.plan, {
            serverId: server.id, projectId: resolved.projectId, versionId: resolved.versionId
          }).then(setInstallPlan).catch(error => setBrowseError(
            error instanceof Error ? error.message : 'The dependency plan could not be resolved.'));
      }).catch(error => setBrowseError(error instanceof Error ? error.message : 'No compatible release could be resolved.'));
  };
  const chooseLocal = () => {
    void command<LocalPluginSelection>(methods.chooseLocal, { serverId: server.id }).then(selection => {
      if (!selection.cancelled && selection.token && selection.plugin)
        setLocalSelection(selection);
    });
  };
  const installLocal = () => {
    if (!localSelection?.token) return;
    const token = localSelection.token;
    setLocalSelection(null);
    void command(methods.installLocal, { serverId: server.id, token, restartIfRunning: running });
  };
  const installRemote = () => {
    if (!pendingInstall) return;
    const selectedRelease = pendingInstall;
    setPendingInstall(null);
    const operationId = crypto.randomUUID();
    void command<ManagedContentOperation>(methods.install, {
      serverId: server.id,
      projectId: selectedRelease.projectId,
      versionId: selectedRelease.versionId,
      restartIfRunning: running,
      operationId
    }).then(operation => {
      setContentOperations(current => [operation, ...current.filter(item => item.operationId !== operation.operationId)]);
      setContentPollEpoch(value => value + 1);
    });
  };
  const dependencyIsInstalled = (dependency: PluginRelease['dependencies'][number]) => snapshot.plugins.some(plugin =>
    (dependency.projectId.length > 0 && (plugin.id.localeCompare(dependency.projectId, undefined, { sensitivity: 'accent' }) === 0 ||
      plugin.providerProjectId?.localeCompare(dependency.projectId, undefined, { sensitivity: 'accent' }) === 0)) ||
    (dependency.fileName.length > 0 && plugin.fileName.localeCompare(dependency.fileName, undefined, { sensitivity: 'accent' }) === 0));
  const requiredDependencies = release?.dependencies.filter(item => item.type === 'required') ?? [];
  const unresolvedRequiredDependencies = requiredDependencies.filter(dependency => !dependencyIsInstalled(dependency));
  const installDependency = (dependency: PluginRelease['dependencies'][number]) => {
    if (!dependency.projectId || !dependency.versionId) {
      setBrowseError(`The provider did not supply an exact installable release for ${dependency.fileName || dependency.projectId || 'this dependency'}.`);
      return;
    }
    void command<ManagedContentOperation>(methods.install, { serverId: server.id, projectId: dependency.projectId, versionId: dependency.versionId, restartIfRunning: running, operationId: crypto.randomUUID() })
      .then(operation => {
        setContentOperations(current => [operation, ...current.filter(item => item.operationId !== operation.operationId)]);
        setContentPollEpoch(value => value + 1);
      });
  };
  const installAllDependencies = async () => {
    setPendingDependencyBatch(false);
    if (!release) return;
    const operation = await command<ManagedContentOperation>(methods.installPlan, {
      serverId: server.id, projectId: release.projectId, versionId: release.versionId,
      restartIfRunning: running, operationId: crypto.randomUUID()
    });
    setContentOperations(current => [operation, ...current.filter(item => item.operationId !== operation.operationId)]);
    setContentPollEpoch(value => value + 1);
  };
  const cancelContentOperation = (operationId: string) => void command('content.cancel', { operationId })
    .then(() => setContentPollEpoch(value => value + 1));
  const operationFor = (projectId: string, versionId?: string) => contentOperations.find(operation =>
    operation.projectId === projectId && (!versionId || operation.versionId === versionId));
  const installedFor = (projectId: string, versionId?: string) => snapshot.plugins.find(plugin =>
    plugin.provider === 'Modrinth' && plugin.providerProjectId === projectId &&
    (!versionId || plugin.providerVersionId === versionId));
  const projectState = (projectId: string, versionId?: string): { label: string; tone: 'neutral' | 'info' | 'success' | 'warning' | 'danger' } | null => {
    const operation = operationFor(projectId, versionId);
    if (operation && (!operation.isTerminal || operation.progress.stage === 'Failed' || operation.progress.stage === 'Cancelled'))
      return { label: operation.progress.stage === 'PendingRestart' ? 'Installed · restart required' : operation.progress.stage.replace(/([a-z])([A-Z])/g, '$1 $2'), tone: operation.progress.stage === 'Failed' ? 'danger' : operation.progress.stage === 'Cancelled' ? 'warning' : 'info' };
    const installed = installedFor(projectId, versionId);
    if (installed)
      return { label: installed.loadState === 'Loaded' ? 'Loaded' : installed.loadState === 'Failed' ? 'Failed' : installed.enabled ? 'Installed' : 'Disabled', tone: installed.loadState === 'Loaded' ? 'success' : installed.loadState === 'Failed' ? 'danger' : installed.enabled ? 'success' : 'neutral' };
    if (operation?.isTerminal && operation.success)
      return { label: operation.progress.stage === 'Loaded' ? 'Loaded' : 'Installed', tone: 'success' };
    return null;
  };
  const problems = snapshot.plugins.flatMap(plugin => {
    const issues: string[] = [];
    if (plugin.duplicateId) issues.push('Another installed JAR declares the same plugin ID.');
    if (plugin.compatibility === 'Incompatible') issues.push(plugin.compatibilityReason);
    if (plugin.compatibility === 'Unknown') issues.push(plugin.compatibilityReason);
    if (plugin.loadState === 'Failed') issues.push(plugin.loadEvidence);
    const declared = plugin.dependencyDetails.length ? plugin.dependencyDetails : plugin.dependencies.map(id => ({ id, kind: 'Required' as const }));
    for (const dependency of declared.filter(item => item.kind === 'Required'))
      if (!snapshot.plugins.some(candidate => candidate.id.localeCompare(dependency.id, undefined, { sensitivity: 'accent' }) === 0))
        issues.push(`Required dependency ${dependency.id} is not present in the current inventory.`);
    return issues.map(issue => ({ plugin: plugin.name, issue }));
  });
  const optionalDependencies = snapshot.plugins.flatMap(plugin => {
    const declared = plugin.dependencyDetails.length ? plugin.dependencyDetails : [];
    return declared.filter(item => item.kind === 'Optional' && !snapshot.plugins.some(candidate => candidate.id.localeCompare(item.id, undefined, { sensitivity: 'accent' }) === 0))
      .map(item => ({ plugin: plugin.name, dependency: item.id }));
  });
  const confirm = () => {
    if (!pending) return;
    const request = pending.kind === 'remove'
      ? command(methods.remove, { serverId: server.id, relativePath: pending.path, restartIfRunning: running })
      : command(methods.setEnabled, { serverId: server.id, relativePath: pending.path, enabled: pending.enabled, restartIfRunning: running });
    void request.finally(() => setPending(null));
  };
  const selectedOperation = release ? operationFor(release.projectId, release.versionId) : null;
  const selectedInstalled = release ? installedFor(release.projectId, release.versionId) : null;
  const selectedState = release ? projectState(release.projectId, release.versionId) : null;
  return <div className={pluginStyles.pluginLayout}>
    <section className={styles.panel}>
      <div className={pluginStyles.pluginHeader}><div><h2>{title}</h2><p>{isMod ? `${server.ecosystem} server extensions filtered to exact Minecraft and loader compatibility.` : 'Paper-compatible server extensions.'} The Agent stages every change and owns any required safe restart.</p></div><div className={page.actions}><Button variant="subtle" icon={<FolderOpen size={14} />} onClick={() => void command(methods.openFolder, { serverId: server.id })}>{isMod ? 'Mod folder' : 'Plugin folder'}</Button><Button disabled={!canApply || operationBusy} onClick={chooseLocal}>Install local JAR</Button></div></div>
      <nav className={pluginStyles.pluginTabs} aria-label={`${title} sections`}>{(['installed', 'browse', 'updates', 'problems'] as const).map(value => <button key={value} data-selected={section === value} aria-pressed={section === value} onClick={() => setSection(value)}>{value[0].toUpperCase() + value.slice(1)}{value === 'installed' ? ` ${snapshot.plugins.length}` : value === 'problems' && problems.length ? ` ${problems.length}` : ''}</button>)}</nav>
      {running && <div className={styles.contextNote}>Applying a {singular} change will save and stop the server, make the reversible change, then validate a full restart. If startup fails, ChunkPilot restores the previous JAR.</div>}
      {!canApply && <div className={styles.contextNote}>Wait for the current server operation to finish before changing {kind}. Browsing remains available.</div>}
      {section === 'installed' && (snapshot.plugins.length ? <table className={`${styles.table} ${pluginStyles.pluginResponsiveTable} ${pluginStyles.pluginInstalledTable}`}><thead><tr><th>{isMod ? 'Mod' : 'Plugin'}</th><th>Version</th><th>JAR state</th><th>Load health</th><th>Compatibility</th><th /></tr></thead><tbody>{snapshot.plugins.map(plugin => <tr key={plugin.relativePath}><td><strong>{plugin.name}</strong><small className={styles.cellMeta}>{plugin.fileName} · {bytes(plugin.sizeBytes)}</small></td><td>{plugin.version}</td><td><StatusBadge tone={plugin.enabled ? 'success' : 'neutral'}>{plugin.enabled ? 'Active' : 'Disabled'}</StatusBadge></td><td><StatusBadge tone={plugin.loadState === 'Failed' ? 'danger' : plugin.loadState === 'Loaded' ? 'success' : plugin.loadState === 'Pending' ? 'info' : 'neutral'} title={plugin.loadEvidence}>{plugin.loadState}</StatusBadge></td><td><StatusBadge tone={plugin.compatibility === 'Incompatible' ? 'danger' : plugin.compatibility === 'Unknown' ? 'warning' : 'success'}>{plugin.compatibility}</StatusBadge></td><td><div className={styles.tableActions}><Button variant="subtle" onClick={() => setConfigPlugin(plugin)}>Configure</Button><Button variant="subtle" disabled={!canApply || operationBusy} onClick={() => setPending({ kind: 'toggle', path: plugin.relativePath, name: plugin.name, enabled: !plugin.enabled })}>{plugin.enabled ? 'Disable' : 'Enable'}</Button><Button variant="subtle" disabled={!canApply || operationBusy} onClick={() => setPending({ kind: 'remove', path: plugin.relativePath, name: plugin.name })}>Remove</Button></div></td></tr>)}</tbody></table> : <EmptyState title={`No ${singular} JARs found`} detail={`Install a local JAR or browse compatible releases from an official provider. ChunkPilot inspects metadata without executing ${singular} code.`} />)}
      {section === 'browse' && <div className={pluginStyles.pluginBrowser}>
        <div className={pluginStyles.pluginSearch}><SearchInput value={query} onChange={event => setQuery(event.target.value)} onKeyDown={event => { if (event.key === 'Enter') search(); }} placeholder={`Search official Modrinth ${kind}`} aria-label={`Search official Modrinth ${kind}`} /><Button variant="primary" disabled={busy.has(methods.search)} onClick={search}>{busy.has(methods.search) ? 'Searching…' : 'Search'}</Button></div>
        <div className={pluginStyles.providerStrip}>{providers.map(provider => <span key={provider.provider}><StatusBadge tone={provider.available ? 'success' : 'neutral'}>{provider.provider}</StatusBadge>{provider.detail}</span>)}</div>
        {browseError && <div className={styles.contextNote}>{browseError}</div>}
        <div className={pluginStyles.pluginResults}>{results.length ? results.map(project => { const state = projectState(project.projectId); return <button key={project.projectId} data-selected={selected?.projectId === project.projectId} aria-pressed={selected?.projectId === project.projectId} onClick={() => choose(project)}><span className={pluginStyles.pluginMonogram} aria-hidden="true">{project.name.slice(0, 2).toUpperCase()}</span><span><span className={pluginStyles.pluginProjectTitle}><strong>{project.name}</strong>{state && <StatusBadge tone={state.tone}>{state.label}</StatusBadge>}</span><small>{project.author} · {project.downloads == null ? 'Downloads unavailable' : `${project.downloads.toLocaleString()} downloads`}{isMod ? ` · ${clientRequirementLabel(project.clientRequirement)}` : ''}</small><p>{project.summary}</p></span></button>; }) : <EmptyState title="Search the official Modrinth catalog" detail={`Results are filtered to server-capable ${server.ecosystem} releases for exact Minecraft ${server.minecraftVersion}. Client-only projects are excluded.`} />}</div>
        {selected && <aside className={pluginStyles.pluginRelease}><header><div><strong>{selected.name}</strong><p>{selected.summary}</p></div>{release && <StatusBadge tone={release.integrity === 'sha512' ? 'success' : 'danger'}>{release.integrity === 'sha512' ? 'SHA-512' : 'No verified hash'}</StatusBadge>}</header>{release ? <><dl><div><dt>Release</dt><dd>{release.versionName}</dd></div><div><dt>Compatibility</dt><dd>Minecraft {release.minecraftVersion} · {release.loader}</dd></div>{isMod && <div><dt>Friends</dt><dd>{clientRequirementLabel(release.clientRequirement)}</dd></div>}<div><dt>File</dt><dd>{release.fileName} · {bytes(release.sizeBytes)}</dd></div><div><dt>Dependencies</dt><dd>{release.dependencies.length ? `${requiredDependencies.length} required · ${release.dependencies.length} total` : 'None declared'}</dd></div></dl>
          {unresolvedRequiredDependencies.length > 0 && <div className={styles.contextNote}><strong>Required dependencies</strong><p>ChunkPilot resolves the exact compatible dependency graph before changing files. The Agent applies it as one reversible plan.</p><div className={page.actions}>{unresolvedRequiredDependencies.map(item => <Button key={`${item.projectId}-${item.versionId}`} variant="subtle" disabled={!item.projectId || !item.versionId || operationBusy} onClick={() => installDependency(item)}>Install {item.fileName || item.projectId || 'dependency'}</Button>)}<Button disabled={operationBusy || !installPlan?.canInstall} title={installPlan?.problems?.join(' ') || undefined} onClick={() => setPendingDependencyBatch(true)}>Install complete verified plan</Button></div></div>}
          {selectedOperation && <div className={pluginStyles.operationState} role="status"><div><StatusBadge tone={selectedState?.tone ?? 'info'}>{selectedState?.label ?? selectedOperation.progress.stage}</StatusBadge>{selectedOperation.progress.percent != null && <span>{Math.round(selectedOperation.progress.percent)}%</span>}</div><p>{selectedOperation.error ?? selectedOperation.progress.message}</p>{selectedOperation.progress.totalBytes != null && <small>{bytes(selectedOperation.progress.bytesTransferred ?? 0)} / {bytes(selectedOperation.progress.totalBytes)}</small>}{!selectedOperation.isTerminal && selectedOperation.isCancellable && <Button variant="subtle" onClick={() => cancelContentOperation(selectedOperation.operationId)}>Cancel</Button>}</div>}
          <Button variant="primary" disabled={!canApply || release.integrity !== 'sha512' || operationBusy || unresolvedRequiredDependencies.length > 0 || release.clientRequirement === 'ClientOnly' || Boolean(selectedInstalled) || selectedOperation?.success === true} onClick={() => setPendingInstall(release)}>{selectedInstalled ? selectedInstalled.loadState === 'Loaded' ? 'Loaded' : 'Installed' : selectedOperation?.success === true ? selectedOperation.progress.stage === 'Loaded' ? 'Loaded' : 'Installed' : selectedOperation?.progress.stage === 'Failed' || selectedOperation?.progress.stage === 'Cancelled' ? 'Retry verified release' : running ? 'Install and restart' : 'Install verified release'}</Button></> : <p className={page.muted}>Resolving an exact release for Minecraft {server.minecraftVersion} and {server.ecosystem}…</p>}</aside>}
      </div>}
      {section === 'updates' && (updatesLoading ? <div className={pluginStyles.pluginNotice}><History size={24} /><div><h3>Checking exact provider releases…</h3><p>Only provider-installed {kind} with persisted identity are checked. Local JARs are never guessed.</p></div></div> : updates.length ? <table className={`${styles.table} ${pluginStyles.pluginResponsiveTable} ${pluginStyles.pluginUpdatesTable}`}><thead><tr><th>{isMod ? 'Mod' : 'Plugin'}</th><th>Installed</th><th>Compatible release</th><th>Status</th><th /></tr></thead><tbody>{updates.map(match => { const plugin = snapshot.plugins.find(item => item.relativePath === match.pluginPath); const available = Boolean(match.release && match.release.versionId !== match.currentVersionId); const dependencyBlocked = match.release?.dependencies.some(item => item.type === 'required' && !dependencyIsInstalled(item)) ?? false; return plugin ? <tr key={match.pluginPath}><td><strong>{plugin.name}</strong><small className={styles.cellMeta}>{plugin.installSource} · Minecraft {server.minecraftVersion}</small></td><td>{plugin.version}</td><td>{match.release?.versionName ?? 'Unavailable'}</td><td><StatusBadge tone={match.error || dependencyBlocked ? 'warning' : available ? 'info' : 'success'}>{match.error ? 'Check failed' : dependencyBlocked ? 'Needs dependency' : available ? 'Update available' : 'Current'}</StatusBadge></td><td><Button variant="subtle" disabled={!canApply || !available || operationBusy || dependencyBlocked} title={dependencyBlocked ? 'Install the required dependencies first.' : match.error} onClick={() => match.release && setPendingInstall(match.release)}>{available ? running ? 'Update and restart' : 'Update' : 'Up to date'}</Button></td></tr> : null; })}</tbody></table> : <div className={pluginStyles.pluginNotice}><History size={24} /><div><h3>No provider identity to match</h3><p>Install a {singular} from Browse to retain its exact provider project and release. Local and sideloaded JARs remain unmanaged because ChunkPilot will not guess their source.</p></div></div>)}
      {section === 'problems' && <>{problems.length ? <div className={pluginStyles.problemList}>{problems.map((problem, index) => <div key={`${problem.plugin}-${index}`}><CircleHelp size={17} /><span><strong>{problem.plugin}</strong><p>{problem.issue}</p></span></div>)}</div> : <EmptyState title="No inventory problems detected" detail={`This confirms only bounded metadata, duplicate checks, and required dependencies. It is not proof that every ${singular} will run correctly.`} />}{optionalDependencies.length > 0 && <details className={styles.inventory}><summary>Optional integrations not installed ({optionalDependencies.length})</summary><div className={pluginStyles.problemList}>{optionalDependencies.map(item => <div key={`${item.plugin}-${item.dependency}`}><CircleHelp size={17} /><span><strong>{item.plugin}</strong><p>Optional integration {item.dependency} is not installed. This does not block the {singular}.</p></span></div>)}</div></details>}</>}
    </section>
    <ConfirmDialog open={localSelection !== null} title={`Install ${localSelection?.plugin?.name ?? localSelection?.fileName ?? `local ${singular}`}?`} detail={localSelection?.plugin ? `${localSelection.plugin.version} · ${localSelection.plugin.loader || 'Loader metadata unavailable'} · ${bytes(localSelection.plugin.sizeBytes)}. ${isMod ? `${clientRequirementLabel(localSelection.plugin.clientRequirement)}. ` : ''}${localSelection.plugin.dependencies.length ? `Declares dependencies: ${localSelection.plugin.dependencies.join(', ')}. ` : ''}${localSelection.plugin.compatibilityReason}${running ? ' The running server will save, stop, and restart.' : ''}` : `ChunkPilot could not inspect ${singular} metadata.`} confirmLabel={running ? 'Install and restart' : `Install ${singular}`} onCancel={() => setLocalSelection(null)} onConfirm={installLocal} />
    <ConfirmDialog open={pendingInstall !== null} title={`Apply ${pendingInstall?.versionName ?? `${singular} release`}?`} detail={pendingInstall ? `${pendingInstall.fileName} · ${bytes(pendingInstall.sizeBytes)} · Minecraft ${pendingInstall.minecraftVersion} · ${pendingInstall.loader}. SHA-512 verification is required before activation.${isMod ? ` ${clientRequirementLabel(pendingInstall.clientRequirement)}.` : ''}${running ? ' The running server will save, stop, apply the reversible change, and restart.' : ''}` : ''} confirmLabel={running ? 'Apply and restart' : `Apply ${singular} change`} onCancel={() => setPendingInstall(null)} onConfirm={installRemote} />
    <ConfirmDialog open={pendingDependencyBatch} title={`Install ${singular} and required dependencies?`} detail={`${installPlan?.releases?.length ?? 0} exact provider file${installPlan?.releases?.length === 1 ? '' : 's'} will be hash-verified and applied as one reversible Agent operation: ${installPlan?.releases?.map(item => item.fileName).join(', ') || 'plan unavailable'}.${running ? ' The server will save, stop, and restart once after the full plan is applied.' : ''}`} confirmLabel={running ? 'Install plan and restart' : 'Install verified plan'} onCancel={() => setPendingDependencyBatch(false)} onConfirm={() => void installAllDependencies()} />
    <ConfirmDialog open={pending !== null} title={pending?.kind === 'remove' ? `Remove ${pending.name}?` : `${pending?.enabled ? 'Enable' : 'Disable'} ${pending?.name}?`} detail={pending?.kind === 'remove' ? `The JAR moves to ChunkPilot Recovery. Its configuration stays in the server folder.${running ? ' The server will save, stop, and restart; a failed start restores the JAR.' : ''}` : `The JAR moves between active and disabled storage.${running ? ' The server will save, stop, and restart; a failed start restores the previous state.' : ''}`} confirmLabel={pending?.kind === 'remove' ? running ? 'Remove and restart' : 'Move to Recovery' : running ? `${pending?.enabled ? 'Enable' : 'Disable'} and restart` : pending?.enabled ? `Enable ${singular}` : `Disable ${singular}`} destructive={pending?.kind === 'remove'} onCancel={() => setPending(null)} onConfirm={confirm} />
    <PluginConfigEditor open={configPlugin !== null} onClose={() => setConfigPlugin(null)} serverId={server.id} serverRunning={running} plugin={configPlugin} kind={kind} />
  </div>;
}

function BackupsPage({ server }: { server: ServerSummary }) {
  const snapshot = useAppStore(state => state.snapshot)!; const command = useAppStore(state => state.command); const busy = useAppStore(state => state.busy);
  const backupBusy = ['backups.create', 'backups.restore', 'backups.verify'].some(method => busy.has(method));
  return <section className={styles.panel}><div className={styles.pathBar}><div><strong>Recovery points</strong> <span className={page.muted}>· {snapshot.backups.length} complete</span></div><Button disabled={backupBusy} variant="primary" icon={<Archive size={14} />} onClick={() => void command('backups.create', { serverId: server.id })}>{busy.has('backups.create') ? 'Creating backup…' : 'Create backup'}</Button></div><div className={styles.contextNote}>{server.state === 'Stopped' ? 'The server is stopped. Verified recovery points can be restored.' : 'Stop the server before restoring. Creating and verifying backups remains available.'}</div>{snapshot.backups.length ? <table className={styles.table}><thead><tr><th>Created</th><th>Description</th><th>Size</th><th>Verification</th><th /></tr></thead><tbody>{snapshot.backups.map(backup => <tr key={backup.id}><td>{new Date(backup.createdAt).toLocaleString()}</td><td>{backup.description || backup.source}</td><td>{bytes(backup.sizeBytes)}</td><td><StatusBadge tone={backup.verified ? 'success' : 'warning'}>{backup.verified ? 'Verified' : 'Not verified'}</StatusBadge></td><td><div className={styles.tableActions}><Button disabled={backupBusy} variant="subtle" onClick={() => void command('backups.verify', { serverId: server.id, backupId: backup.id })}>Verify</Button><Button disabled={backupBusy || server.state !== 'Stopped' || !backup.verified} variant="subtle" title={!backup.verified ? 'Verify this backup before restoring it.' : server.state !== 'Stopped' ? 'Stop the server before restoring.' : undefined} onClick={() => void command('backups.restore', { serverId: server.id, backupId: backup.id })}>Restore</Button></div></td></tr>)}</tbody></table> : <EmptyState title="No complete backup found" detail="ChunkPilot will not claim this world is protected until a backup completes and verifies." action={<Button disabled={backupBusy} variant="primary" onClick={() => void command('backups.create', { serverId: server.id })}>Create backup</Button>} />}</section>;
}

function VersionsPage({ server }: { server: ServerSummary }) {
  const snapshot = useAppStore(state => state.snapshot)!;
  const command = useAppStore(state => state.command);
  const [showInstall, setShowInstall] = useState(false);
  const busy = useAppStore(state => state.busy);
  const update = snapshot.update;
  const isPaper = server.capabilities.versioning === 'paper';
  const loaderPlatform = server.capabilities.versioning === 'fabric' ? 'Fabric' :
    server.capabilities.versioning === 'quilt' ? 'Quilt' :
      server.capabilities.versioning === 'forge' ? 'Forge' :
        server.capabilities.versioning === 'neoforge' ? 'NeoForge' : null;
  const isLoader = loaderPlatform !== null;
  const [catalog, setCatalog] = useState<MinecraftVersionCatalog | null>(null);
  const [paperBuilds, setPaperBuilds] = useState<PaperBuildEvidenceCatalog | null>(null);
  const [loaderBuilds, setLoaderBuilds] = useState<LoaderBuildEvidenceCatalog | null>(null);
  const [catalogError, setCatalogError] = useState('');
  useEffect(() => {
    let active = true;
    const applyCatalog = (result: MinecraftVersionCatalog) => { if (active) setCatalog(result); };
    const platform = isPaper ? 'Paper' : loaderPlatform ?? 'Vanilla';
    void command<MinecraftVersionCatalog>('creation.catalog', { platform, includeSnapshots: true }).then(result => {
      applyCatalog(result);
      if (result.stale && active) {
        void command<MinecraftVersionCatalog>('creation.catalog', { platform, includeSnapshots: true, forceRefresh: true })
          .then(applyCatalog)
          .catch(() => { /* the last-known-good inventory remains visible */ });
      }
    }).catch(error => { if (active) setCatalogError(error instanceof Error ? error.message : 'Version inventory unavailable.'); });
    return () => { active = false; };
  }, [command, isPaper, loaderPlatform]);
  useEffect(() => {
    if (!isPaper) { setPaperBuilds(null); return; }
    let active = true;
    void command<PaperBuildEvidenceCatalog>('creation.paperBuilds', { versionId: server.minecraftVersion })
      .then(result => { if (active) setPaperBuilds(result); })
      .catch(error => { if (active) setCatalogError(error instanceof Error ? error.message : 'Paper build inventory unavailable.'); });
    return () => { active = false; };
  }, [command, isPaper, server.minecraftVersion]);
  useEffect(() => {
    if (!loaderPlatform) { setLoaderBuilds(null); return; }
    let active = true;
    void command<LoaderBuildEvidenceCatalog>('creation.loaderBuilds', {
      platform: loaderPlatform,
      versionId: server.minecraftVersion
    }).then(result => { if (active) setLoaderBuilds(result); })
      .catch(error => { if (active) setCatalogError(error instanceof Error ? error.message : `${loaderPlatform} inventory unavailable.`); });
    return () => { active = false; };
  }, [command, loaderPlatform, server.minecraftVersion]);
  const versionBusy = ['versions.check', 'versions.install', 'versions.rollback', 'versions.verify', 'versions.cancel'].some(method => busy.has(method));
  const installedEvidence = catalog?.versions.find(version => version.id === server.minecraftVersion) ?? null;
  const latestStable = catalog?.versions.find(version => version.id === catalog.manifestLatestReleaseId) ?? null;
  const buildNumber = Number(server.loaderVersion);
  const installedPaperBuild = isPaper && Number.isInteger(buildNumber)
    ? paperBuilds?.builds.find(build => build.id === buildNumber) ?? null
    : null;
  const installedLoaderBuild = isLoader
    ? loaderBuilds?.builds.find(build => build.loaderVersion === server.loaderVersion) ?? null
    : null;
  return <div className={styles.versionStack}>
    <section className={styles.panel}>
      <div className={styles.pathBar}>
        <div><strong>Versions and updates</strong> <span className={page.muted}>· installed Minecraft {server.minecraftVersion}{isPaper && server.loaderVersion ? ` · Paper build ${server.loaderVersion}` : isLoader && server.loaderVersion ? ` · ${loaderPlatform} ${server.loaderVersion}` : ''}</span></div>
        <div className={page.actions}>
          {update?.cancellable && <Button variant="subtle" disabled={busy.has('versions.cancel')} onClick={() => void command('versions.cancel', { serverId: server.id })}>Cancel safely</Button>}
          {update?.canInstall && <Button variant="primary" disabled={versionBusy} onClick={() => setShowInstall(true)}>Install {update.latestVersionName ?? 'update'}</Button>}
          <Button icon={<RotateCw size={14} />} disabled={versionBusy} onClick={() => void command('versions.check', { serverId: server.id })}>{busy.has('versions.check') ? 'Checking…' : 'Check for updates'}</Button>
        </div>
      </div>
      {catalog && (isPaper
        ? <div className={styles.catalogPolicy}>
            <div><span>Installed Paper build</span><strong>{server.loaderVersion || 'Unavailable'}</strong></div>
            <div><span>Exact build evidence</span><strong>{installedPaperBuild?.support ?? 'Unavailable'}</strong></div>
            <p>{installedPaperBuild?.supportReason ?? paperBuilds?.message ?? 'PaperMC does not currently establish this exact installed build.'} Build updates and Minecraft-version upgrades remain separate operations.</p>
          </div>
        : isLoader
          ? <div className={styles.catalogPolicy}>
              <div><span>Installed loader</span><strong>{server.loaderVersion ? `${loaderPlatform} ${server.loaderVersion}` : 'Unavailable'}</strong></div>
              <div><span>Exact combination evidence</span><strong>{installedLoaderBuild?.support ?? 'Unavailable'}</strong></div>
              <p>{installedLoaderBuild?.supportReason ?? loaderBuilds?.message ?? `The official ${loaderPlatform} inventory does not currently establish this exact loader combination.`} Loader updates and Minecraft-version upgrades remain separate recovery-backed operations.</p>
            </div>
          : <div className={styles.catalogPolicy}>
            <div><span>Latest official stable</span><strong>{catalog.manifestLatestReleaseId || 'Unavailable'}</strong></div>
            <div><span>Latest runtime-verified stable</span><strong>{catalog.latestVerifiedReleaseId || 'None in this build'}</strong></div>
            <p>{latestStable?.supportReason ?? 'The inventory does not establish an applicable stable target.'} The catalog never installs an update automatically.</p>
          </div>)}
      {update && <div className={styles.updateSummary}>
        <div><StatusBadge tone={update.canInstall ? 'warning' : update.sourceLinked ? 'success' : 'neutral'}>{update.status}</StatusBadge><strong>{update.detail}</strong><span>{update.checkedAt ? `Last checked ${new Date(update.checkedAt).toLocaleString()}` : update.sourceLinked ? 'Not checked yet' : 'No authoritative update source is linked.'}</span></div>
        {update.operationPercent != null && <div className={styles.updateProgress} aria-label={`Update progress ${update.operationPercent.toFixed(0)} percent`}><i><b style={{ width: `${Math.max(0, Math.min(100, update.operationPercent))}%` }} /></i><span>{update.operationStep || update.operationState} · {update.operationPercent.toFixed(0)}%</span>{update.operationDetail && update.operationDetail !== update.operationStep && <small>{update.operationDetail}</small>}</div>}
      </div>}
      {update && <UpdateInstallDialog open={showInstall} onClose={() => setShowInstall(false)} server={server} update={update} />}
      {snapshot.versions.length ? <table className={styles.table}><thead><tr><th>Version</th><th>Platform</th><th>Installed</th><th>Snapshot</th><th>Status</th><th /></tr></thead><tbody>{snapshot.versions.map(version => <tr key={version.id}><td><strong>{version.version}</strong><small className={styles.cellMeta}>{version.health}</small></td><td>{version.platform}</td><td>{version.installedAt ? new Date(version.installedAt).toLocaleDateString() : 'Unavailable'}</td><td>{version.snapshotSizeBytes > 0 ? bytes(version.snapshotSizeBytes) : version.active ? 'Active files' : 'Unavailable'}{version.includesWorldData && <small className={styles.cellMeta}>World data included</small>}</td><td><StatusBadge tone={version.active ? 'success' : version.rollbackReady ? 'info' : version.verified ? 'neutral' : 'warning'}>{version.active ? 'Active' : version.rollbackReady ? 'Rollback ready' : version.verified ? 'Verified' : 'Unverified'}</StatusBadge></td><td><div className={styles.tableActions}><Button disabled={versionBusy} variant="subtle" onClick={() => void command('versions.verify', { serverId: server.id, versionId: version.id })}>Verify</Button>{version.rollbackReady && <Button disabled={versionBusy} variant="subtle" onClick={() => void command('versions.rollback', { serverId: server.id, versionId: version.id })}>Roll back</Button>}</div></td></tr>)}</tbody></table> : <EmptyState title="No rollback snapshots recorded" detail="The current installed version is shown above. ChunkPilot will list verified version snapshots here after an update or rollback creates them." />}
    </section>
    <section className={styles.versionEvidence}>
      <header><div><strong>{isPaper ? 'Installed Paper evidence' : isLoader ? `Installed ${loaderPlatform} evidence` : 'Installed-version evidence'}</strong><p>Support is based on official metadata, artifact integrity, Java requirements, and exact runtime evidence—not age alone.</p></div><StatusBadge tone={(isPaper ? installedPaperBuild?.selectable : isLoader ? installedLoaderBuild?.selectable : installedEvidence?.selectable) ? 'success' : 'neutral'}>{isPaper ? installedPaperBuild?.support ?? 'Unavailable' : isLoader ? installedLoaderBuild?.support ?? 'Unavailable' : installedEvidence?.support ?? 'Unavailable'}</StatusBadge></header>
      {isPaper
        ? installedPaperBuild
          ? <dl className={styles.evidenceGrid}><div><dt>Exact build</dt><dd>Paper {server.minecraftVersion} build {installedPaperBuild.id}</dd></div><div><dt>Channel</dt><dd>{installedPaperBuild.channel}</dd></div><div><dt>Integrity</dt><dd>{installedPaperBuild.hasIntegrityMetadata ? 'Official SHA-256 and size' : 'Incomplete'}</dd></div><div><dt>Certification</dt><dd>{installedPaperBuild.certification.level}</dd></div><div><dt>Source</dt><dd>{installedPaperBuild.provenance}</dd></div></dl>
          : <EmptyState title={`Paper build ${server.loaderVersion || 'unknown'} is not in the current inventory`} detail={catalogError || paperBuilds?.message || 'Refresh the official PaperMC catalog to resolve this installed build.'} />
        : isLoader
          ? installedLoaderBuild
            ? <dl className={styles.evidenceGrid}><div><dt>Exact combination</dt><dd>Minecraft {server.minecraftVersion} · {loaderPlatform} {installedLoaderBuild.loaderVersion}</dd></div><div><dt>Installer/launcher</dt><dd>{installedLoaderBuild.installerVersion || 'Provider default'}</dd></div><div><dt>Integrity</dt><dd>{installedLoaderBuild.hasIntegrityMetadata ? 'Provider hash verified' : loaderPlatform === 'Fabric' ? 'Official generated server launcher endpoint' : 'Incomplete'}</dd></div><div><dt>Certification</dt><dd>{installedLoaderBuild.certification.level}</dd></div><div><dt>Source</dt><dd>{installedLoaderBuild.provenance}</dd></div></dl>
            : <EmptyState title={`${loaderPlatform} ${server.loaderVersion || 'unknown'} is not in the current inventory`} detail={catalogError || loaderBuilds?.message || `Refresh the official ${loaderPlatform} catalog to resolve this installed build.`} />
          : installedEvidence
          ? <VersionDetails version={installedEvidence} />
          : <EmptyState title={`Minecraft ${server.minecraftVersion} is not in the current inventory`} detail={catalogError || catalog?.message || 'Refresh the official catalog to resolve this installed version.'} />}
    </section>
    {catalog && <details className={styles.inventory}><summary>Browse the complete official {isPaper ? 'PaperMC' : loaderPlatform ?? 'Minecraft'} inventory ({catalog.versions.length.toLocaleString()} versions)</summary><div><VersionBrowser catalog={catalog} value={server.minecraftVersion} readonly compact />{isLoader && loaderBuilds && <div className={styles.catalogPolicy}><div><span>Exact builds for {server.minecraftVersion}</span><strong>{loaderBuilds.builds.length.toLocaleString()}</strong></div><div><span>Installed</span><strong>{server.loaderVersion || 'Unavailable'}</strong></div><p>{loaderBuilds.stale ? 'Showing the last-known-good offline cache. ' : ''}Only builds bound to this exact Minecraft version are shown.</p></div>}</div></details>}
  </div>;
}

function ServerSettingsPage({ server, initialCategory }: { server: ServerSummary; initialCategory: string }) {
  const authoritative = useAppStore(state => state.snapshot!.serverSettings); const command = useAppStore(state => state.command);
  const [category, setCategory] = useState(initialCategory);
  const [baseline, setBaseline] = useState(authoritative);
  const [draft, setDraft] = useState<typeof authoritative>(() => authoritative == null ? null : new URLSearchParams(window.location.search).has('dirty') ? { ...authoritative, motd: `${authoritative.motd} Ready for Friday.` } : { ...authoritative });
  const [stagedIcon, setStagedIcon] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [editorReset, setEditorReset] = useState(0);
  useEffect(() => setCategory(initialCategory), [initialCategory]);
  const dirtySettings = JSON.stringify(draft) !== JSON.stringify(baseline);
  const dirty = dirtySettings || stagedIcon !== null;
  useEffect(() => {
    if (!dirty && authoritative) { setBaseline(authoritative); setDraft({ ...authoritative }); }
  }, [authoritative, dirty]);
  if (!draft || !baseline) return <EmptyState title="Settings unavailable" detail="ChunkPilot has not received an authoritative settings snapshot for this server." />;
  const categories = ['Appearance', 'General', 'Gameplay', 'Resources', 'Connectivity'];
  const update = <K extends keyof typeof draft>(key: K, value: (typeof draft)[K]) => setDraft(valueDraft => valueDraft ? { ...valueDraft, [key]: value } : valueDraft);
  const motdError = validateMotd(draft.motd);
  const discard = useCallback(() => { setDraft({ ...baseline }); setStagedIcon(null); setEditorReset(value => value + 1); }, [baseline]);
  useUnsavedChangesGuard(dirty, discard, 'Your server icon, MOTD, or settings changes have not been saved.');
  const save = async () => {
    if (motdError) return;
    setSaving(true);
    try {
      await command('settings.saveServer', { serverId: server.id, ...draft, iconPngBase64: stagedIcon?.split(',', 2)[1] ?? null });
      setBaseline({ ...draft }); setStagedIcon(null); setEditorReset(value => value + 1);
    } finally { setSaving(false); }
  };
  return <div className={styles.settingsLayout}><nav className={styles.settingsNav} aria-label="Server settings categories">{categories.map(item => <button key={item} data-selected={category === item} onClick={() => setCategory(item)}>{item}</button>)}</nav><div>
    {category === 'Appearance' ? <div className={appearance.appearanceStack}><section className={appearance.appearancePanel}><div className={appearance.appearanceIntro}><div><h2>Server appearance</h2><p>Set the icon and two-line message players see in Minecraft's multiplayer list. Changes stay local until you save.</p></div><StatusBadge tone={server.state === 'Running' && draft.motd !== baseline.motd ? 'warning' : 'neutral'}>{server.state === 'Running' && draft.motd !== baseline.motd ? 'Restart required' : 'Vanilla server list'}</StatusBadge></div><IconCropEditor serverName={server.name} savedIconUrl={server.iconUrl} stagedIconUrl={stagedIcon} onStagedIcon={setStagedIcon} /><MotdEditor serverName={draft.name} serverIconUrl={stagedIcon ?? server.iconUrl} savedRaw={baseline.motd} resetToken={editorReset} onChange={value => update('motd', value)} /></section></div> : <section className={styles.settingsForm}>
      {category === 'General' && <><Setting label="Server name" detail="Renaming changes only ChunkPilot's display name; it never renames the folder or world."><div className={page.actions}><TextInput value={draft.name} readOnly /><Button onClick={() => void command('servers.rename', { serverId: server.id })}>Rename</Button></div></Setting><Setting label="Server port" detail="Changing the port does not create firewall or router access."><TextInput type="number" min={1} max={65535} value={draft.port} onChange={event => update('port', Number(event.target.value))} /></Setting></>}
      {category === 'Gameplay' && <><Setting label="Maximum players" detail="The slot limit reported by the server."><TextInput type="number" min={1} max={1000} value={draft.maximumPlayers} onChange={event => update('maximumPlayers', Number(event.target.value))} /></Setting><Setting label="Difficulty" detail="Applied through server.properties and may require restart."><SelectInput aria-label="Difficulty" value={draft.difficulty} onChange={event => update('difficulty', event.target.value)}>{!['peaceful', 'easy', 'normal', 'hard'].includes(draft.difficulty) && <option value={draft.difficulty}>Custom value: {draft.difficulty || '(empty)'}</option>}<option value="peaceful">Peaceful</option><option value="easy">Easy</option><option value="normal">Normal</option><option value="hard">Hard</option></SelectInput></Setting><Setting label="Player versus player" detail="Allow players to damage one another."><Toggle value={draft.pvp} onChange={value => update('pvp', value)} /></Setting></>}
      {category === 'Resources' && <><Setting label="Memory" detail="Most small Vanilla servers run well with 2–4 GB. Choose a preset or enter an exact amount."><MemoryControl valueMib={draft.maximumRamMb} onChange={value => update('maximumRamMb', value)} hostTotalBytes={useAppStore.getState().snapshot?.host.totalMemoryBytes} ariaLabel="Maximum server memory" /></Setting><Setting label="Advanced" detail="Initial memory controls Java's starting heap. It must not exceed maximum memory."><details className={styles.resourceAdvanced}><summary>Initial memory</summary><MemoryControl valueMib={draft.minimumRamMb} onChange={value => update('minimumRamMb', value)} hostTotalBytes={null} ariaLabel="Initial server memory" minimumMib={256} maximumMib={draft.maximumRamMb} /></details></Setting></>}
      {category === 'Connectivity' && <ConnectivitySettings server={server} />}
    </section>}
    {dirty && <div className={styles.sticky} role="status"><span>{motdError ?? (server.state === 'Running' && draft.motd !== baseline.motd ? 'Unsaved changes · restart required for MOTD' : 'Unsaved server settings')}</span><div className={page.actions}><Button disabled={saving} onClick={discard}>Discard</Button><Button variant="primary" disabled={saving || Boolean(motdError)} onClick={() => void save()}>{saving ? 'Saving…' : 'Save changes'}</Button></div></div>}
  </div></div>;
}

function ConnectivitySettings({ server }: { server: ServerSummary }) {
  const connectivity = useAppStore(state => state.snapshot?.connectivity);
  const command = useAppStore(state => state.command);
  const busy = useAppStore(state => state.busy);
  const [confirmStop, setConfirmStop] = useState(false);
  const [confirmRemove, setConfirmRemove] = useState(false);
  if (!connectivity || connectivity.serverId !== server.id)
    return <EmptyState title="Connectivity unavailable" detail="ChunkPilot has not received authoritative networking state for this server." />;
  const modes: { id: ConnectivitySnapshot['mode']; title: string; detail: string; icon: typeof Wifi }[] = [
    { id: 'HomeNetwork', title: 'LAN', detail: 'People on the same Wi-Fi or wired network.', icon: Wifi },
    { id: 'PortForwarding', title: 'Internet', detail: 'Friends outside your home, after deliberate setup.', icon: Globe2 }
  ];
  const setMode = (mode: ConnectivitySnapshot['mode']) => void command('connectivity.setMode', { serverId: server.id, mode });
  const focusConsent = (id: string) => document.getElementById(id)?.focus();
  const advanceInternetSetup = async () => {
    if (connectivity.mode !== 'PortForwarding') {
      await command('connectivity.setMode', { serverId: server.id, mode: 'PortForwarding' });
      return;
    }
    if (!connectivity.firewall.configured) {
      if (connectivity.firewall.consentRequired) { focusConsent('firewall-consent'); return; }
      if (connectivity.firewall.primaryAction) { await command('connectivity.firewall.primary', { serverId: server.id }); return; }
    }
    if (!connectivity.router.enabled) {
      if (connectivity.router.consentRequired) { focusConsent('router-consent'); return; }
      if (connectivity.router.canRetryCleanup) { await command('connectivity.router.retry', { serverId: server.id }); return; }
      if (connectivity.router.canCheck) { await command('connectivity.router.check', { serverId: server.id }); return; }
    }
    if (server.state !== 'Running') { await command('servers.start', { serverId: server.id }); return; }
    // Outside-in verification starts from the automatic effect once all prerequisites are true.
  };
  const setupBusy = connectivity.router.busy || connectivity.firewall.busy || connectivity.external.busy ||
    busy.has('connectivity.setMode') || busy.has('connectivity.router.check') || busy.has('connectivity.firewall.primary') ||
    busy.has('servers.start') || busy.has('connectivity.external.check');
  const joining = connectionChoice(server, connectivity);
  const firewallReady = connectivity.firewall.configured || Boolean(connectivity.addresses.publicVerified);
  const mainAction = connectivity.addresses.publicVerified ? null
    : connectivity.mode !== 'PortForwarding' ? 'Set up Internet access'
    : !connectivity.firewall.configured ? connectivity.firewall.consentRequired ? 'Review Windows approval' : 'Continue Windows setup'
    : !connectivity.router.enabled ? connectivity.router.consentRequired ? 'Review router approval' : 'Continue router setup'
    : server.state !== 'Running' ? 'Start server'
    : null;
  const setupSteps = [
    { label: 'Allow through Windows', done: firewallReady, active: !firewallReady && (connectivity.firewall.busy || connectivity.firewall.consentRequired), state: connectivity.firewall.configured ? 'Ready' : connectivity.addresses.publicVerified ? 'Allowed' : connectivity.firewall.busy ? 'In progress' : connectivity.firewall.consentRequired ? 'Approval needed' : 'Not set up' },
    { label: 'Set up router', done: connectivity.router.enabled, active: connectivity.router.busy || connectivity.router.consentRequired, state: connectivity.router.enabled ? 'Ready' : connectivity.router.busy ? 'In progress' : connectivity.router.consentRequired ? 'Approval needed' : 'Not set up' },
    { label: 'Start server', done: server.state === 'Running', active: server.state === 'Starting', state: server.state === 'Running' ? 'Running' : server.state === 'Starting' ? 'Starting' : 'Server stopped' },
    { label: 'Check connection', done: Boolean(connectivity.addresses.publicVerified), active: connectivity.external.busy, state: connectivity.addresses.publicVerified ? 'Confirmed' : connectivity.external.busy ? 'Checking' : connectivity.external.phase === 'Unreachable' ? 'Could not connect' : connectivity.external.canCheck ? 'Runs automatically' : 'Waiting for prerequisites' }
  ];
  return <div className={styles.connectivityPage}>
    <header className={styles.connectivityHeader}><div><h2>Connectivity</h2><p>Choose whether people join from your home network or from anywhere on the Internet.</p></div><StatusBadge tone={joining.tone}>{joining.badge}</StatusBadge></header>
    <div className={styles.modeGrid} aria-label="Connection method">{modes.map(mode => <button key={mode.id} data-selected={connectivity.mode === mode.id} onClick={() => setMode(mode.id)} disabled={busy.has('connectivity.setMode')}><mode.icon size={17} /><span><strong>{mode.title}</strong><small>{mode.detail}</small></span><i aria-hidden="true" /></button>)}</div>
    <div className={styles.connectivityStatus} data-tone={joining.tone}><div><strong>{joining.badge}</strong><p>{joining.explanation}</p></div><span>{joining.audience === 'internet' ? 'Internet' : joining.audience === 'home' ? 'LAN' : 'This computer'}</span></div>
    <section className={styles.internetPrompt}><Globe2 size={20} /><div><strong>{connectivity.addresses.publicVerified ? 'Friends can join over the Internet' : connectivity.mode === 'PortForwarding' ? 'Finish Internet setup' : 'Let friends outside your home join'}</strong><p>ChunkPilot guides the Windows and router setup, starts the server when needed, and checks the connection automatically. Nothing is exposed without your approval.</p></div>{mainAction ? <Button variant="primary" disabled={setupBusy} onClick={() => void advanceInternetSetup()}>{setupBusy ? 'Working…' : mainAction}</Button> : <StatusBadge tone={connectivity.addresses.publicVerified ? 'success' : connectivity.external.busy ? 'info' : 'neutral'}>{connectivity.addresses.publicVerified ? 'Confirmed' : connectivity.external.busy ? 'Checking automatically' : 'Waiting to check automatically'}</StatusBadge>}</section>
    {connectivity.mode === 'PortForwarding' && <div className={styles.setupStepper} aria-label="Internet setup progress">{setupSteps.map((step, index) => <div key={step.label} data-complete={step.done || undefined} data-active={step.active || undefined}><span>{step.done ? <Check size={12} /> : index + 1}</span><div><strong>{step.label}</strong><small>{step.state}</small></div></div>)}</div>}
    <section className={styles.addressSection}><div className={styles.sectionHeading}><div><h3>How people connect</h3><p>The recommended address changes with the audience you selected.</p></div></div><ConnectionSummary server={server} connectivity={connectivity} showAll /></section>
    {connectivity.mode === 'PortForwarding' && <>
      {connectivity.firewall.consentRequired && <div id="firewall-consent" tabIndex={-1} className={styles.inlineConsent}><strong>{connectivity.firewall.consentTitle}</strong><p>{connectivity.firewall.consentMessage}</p><div className={page.actions}><Button onClick={() => void command('connectivity.firewall.cancelConsent', { serverId: server.id })}>Cancel</Button><Button variant="primary" onClick={() => void command('connectivity.firewall.confirm', { serverId: server.id, confirmed: true })}>Continue to Windows approval</Button></div></div>}
      {connectivity.router.consentRequired && <div id="router-consent" tabIndex={-1} className={styles.inlineConsent}><strong>Open this server port on your router?</strong>{connectivity.router.consentPoints.map(point => <p key={point}>{point}</p>)}<div className={page.actions}><Button onClick={() => void command('connectivity.router.cancelConsent', { serverId: server.id })}>Not now</Button><Button variant="primary" onClick={() => void command('connectivity.router.confirm', { serverId: server.id, confirmed: true })}>Turn on Internet hosting</Button></div></div>}
      {connectivity.external.phase === 'Unreachable' && <div className={styles.verificationWarning}><strong>This address may not work yet</strong><p>The background connection check could not reach this server. Keep the address while you review the likely failure stage.</p><div className={page.actions}><Button onClick={() => void command('connectivity.external.check', { serverId: server.id })}>Try again</Button><Button variant="subtle" onClick={() => document.querySelector<HTMLDetailsElement>(`.${styles.networkDetails}`)?.setAttribute('open', '')}>View details</Button></div></div>}
      <details className={styles.networkDetails}><summary>Advanced networking actions and details</summary>
        <section className={styles.connectivitySection}><div className={styles.sectionHeading}><div><h3>Windows Firewall</h3><p>One exact ChunkPilot-owned rule for this server.</p></div><StatusBadge tone={connectivity.firewall.tone}>{connectivity.firewall.badge}</StatusBadge></div><div className={styles.connectionBody}><div><strong>{connectivity.firewall.title}</strong><p>{connectivity.firewall.summary}</p></div><div className={page.actions}>{connectivity.firewall.canCancel && <Button onClick={() => void command('connectivity.firewall.cancel', { serverId: server.id })}>Cancel</Button>}{connectivity.firewall.secondaryAction && <Button onClick={() => void command('connectivity.firewall.secondary', { serverId: server.id })}>{connectivity.firewall.secondaryAction}</Button>}{connectivity.firewall.primaryAction && <Button onClick={() => void command('connectivity.firewall.primary', { serverId: server.id })}>{connectivity.firewall.primaryAction}</Button>}{connectivity.firewall.canRemove && <Button variant="danger" onClick={() => setConfirmRemove(true)}>Remove rule</Button>}</div></div></section>
        <section className={styles.connectivitySection}><div className={styles.sectionHeading}><div><h3>Automatic router setup</h3><p>Only the exact mapping owned by this server.</p></div><StatusBadge tone={connectivity.router.tone}>{connectivity.router.badge}</StatusBadge></div><div className={styles.connectionBody}><div><strong>{connectivity.router.title}</strong><p>{connectivity.router.summary}</p>{connectivity.router.upstreamNotice && <p className={styles.warningCopy}>{connectivity.router.upstreamNotice}</p>}</div><div className={page.actions}>{connectivity.router.canCancel && <Button onClick={() => void command('connectivity.router.cancel', { serverId: server.id })}>Cancel</Button>}{connectivity.router.canRetryCleanup && <Button onClick={() => void command('connectivity.router.retry', { serverId: server.id })}>Retry cleanup</Button>}{connectivity.router.canCheck && <Button onClick={() => void command('connectivity.router.check', { serverId: server.id })}>Check router</Button>}{connectivity.router.canStop && <Button variant="danger" onClick={() => setConfirmStop(true)}>Stop sharing</Button>}</div></div></section>
        <section className={styles.connectivitySection}><div className={styles.sectionHeading}><div><h3>Internet verification</h3><p>The only evidence used for “Friends can join.”</p></div><StatusBadge tone={connectivity.external.tone}>{connectivity.external.badge}</StatusBadge></div><div className={styles.connectionBody}><div><strong>{connectivity.external.title}</strong><p>{connectivity.external.summary}</p></div><div className={page.actions}>{connectivity.external.canCancel ? <Button onClick={() => void command('connectivity.external.cancel', { serverId: server.id })}>Cancel check</Button> : <Button disabled={!connectivity.external.canCheck} onClick={() => void command('connectivity.external.check', { serverId: server.id })}>Recheck</Button>}</div></div></section>
        <div className={styles.detailGrid}><Detail label="Router method" value={connectivity.router.mechanism} /><Detail label="Transport" value={connectivity.router.transport} /><Detail label="Gateway" value={connectivity.router.gateway} /><Detail label="Internal endpoint" value={connectivity.router.internalEndpoint} /><Detail label="External port" value={connectivity.router.externalPort} /><Detail label="Lease" value={connectivity.router.lease} /><Detail label="Router address class" value={connectivity.router.addressClass} /><Detail label="Router last checked" value={connectivity.router.lastChecked} /><Detail label="Firewall network" value={connectivity.firewall.network} /><Detail label="Firewall profile" value={connectivity.firewall.profile} /><Detail label="Firewall target" value={connectivity.firewall.port} /><Detail label="Firewall last checked" value={connectivity.firewall.lastChecked} /><Detail label="Externally observed" value={connectivity.external.observedAddress} /><Detail label="Router reported" value={connectivity.external.routerAddress} /><Detail label="Outside-in timing" value={connectivity.external.connectTime} /><Detail label="Outside-in checked" value={connectivity.external.checkedAt} /></div><p>{connectivity.router.detail}</p><p>{connectivity.firewall.detail}</p><p>{connectivity.external.detail}</p></details>
    </>}
    <ConfirmDialog open={confirmStop} title="Stop Internet sharing?" detail="ChunkPilot will remove only the router mapping it can prove it owns. The server and Windows Firewall configuration remain unchanged." confirmLabel="Stop sharing" destructive onCancel={() => setConfirmStop(false)} onConfirm={() => { setConfirmStop(false); void command('connectivity.router.stop', { serverId: server.id, confirmed: true }); }} />
    <ConfirmDialog open={confirmRemove} title="Remove Windows Firewall access?" detail="ChunkPilot will remove only the exact firewall rule it can prove it created. The server folder and router setup remain unchanged." confirmLabel="Remove rule" destructive onCancel={() => setConfirmRemove(false)} onConfirm={() => { setConfirmRemove(false); void command('connectivity.firewall.remove', { serverId: server.id, confirmed: true }); }} />
  </div>;
}

function useAutomaticConnectivityVerification(server: ServerSummary | null, connectivity: ConnectivitySnapshot | null) {
  const command = useAppStore(state => state.command);
  const attempted = useRef('');
  useEffect(() => {
    if (!server || !connectivity || connectivity.mode !== 'PortForwarding' || !connectivity.router.enabled ||
        server.state !== 'Running' || connectivity.addresses.publicVerified || connectivity.external.busy ||
        !connectivity.external.canCheck || !['NotChecked', 'Stale'].includes(connectivity.external.phase)) return;
    const key = `${server.id}:${connectivity.external.phase}:${connectivity.router.externalPort}`;
    if (attempted.current === key) return;
    attempted.current = key;
    void command('connectivity.external.check', { serverId: server.id }).catch(() => undefined);
  }, [command, connectivity, server]);
}

function clientRequirementLabel(value: PluginProject['clientRequirement'] | PluginRelease['clientRequirement'] | undefined): string {
  switch (value) {
    case 'ClientAndServer': return 'Friends need this mod too';
    case 'ClientOptional': return 'Optional for friends';
    case 'ServerOnly': return 'Server only';
    case 'ClientOnly': return 'Client only — cannot install here';
    default: return 'Client requirement unknown';
  }
}

function Detail({ label, value }: { label: string; value: string }) { return <div><span>{label}</span><strong>{value || 'Unavailable'}</strong></div>; }

function Setting({ label, detail, children }: { label: string; detail: string; children: ReactNode }) { return <div className={styles.settingRow}><div><strong>{label}</strong><p>{detail}</p></div><div>{children}</div></div>; }
function Toggle({ value, onChange }: { value: boolean; onChange: (value: boolean) => void }) { return <button className={page.toggle} data-checked={value} role="switch" aria-checked={value} onClick={() => onChange(!value)} />; }
