import { useEffect, useMemo, useRef, useState } from 'react';
import { useVirtualizer } from '@tanstack/react-virtual';
import { Box, Clipboard, File, Image, Info, Link, Search } from '../../design-system/Icons';
import { Button, Combobox, StatusBadge, TextInput } from '../../design-system/Primitives';
import { useAppStore } from '../../state/store';
import type {
  LocalModpackSelection, ModpackCatalogResult, ModpackProject, ModpackProvider,
  ModpackProviderStatus, ModpackRelease, ModpackVersionInventory, ResolvedModpackLink
} from '../../bridge/types';
import styles from './ModpackPicker.module.css';

export type ModpackSelection =
  | { kind: 'remote'; project: ModpackProject; release: ModpackRelease }
  | { kind: 'local'; local: LocalModpackSelection };

type BrowserState = 'Uninitialized' | 'Loading cache' | 'Loading provider' | 'Ready' | 'Refreshing' |
  'Empty' | 'Offline cache' | 'Authentication required' | 'Rate limited' | 'Failed' | 'Cancelled';
type InitialMode = 'Browse' | 'Link' | 'Import' | 'Custom';
interface Query { search: string; minecraftVersion: string; loader: string; category: string; sort: string; }

const emptyQuery: Query = { search: '', minecraftVersion: '', loader: '', category: '', sort: 'Downloads' };

export function ModpackPicker({ value, initialMode = 'Browse', onChange, onOpenProviderSettings: _onOpenProviderSettings }: {
  value: ModpackSelection | null;
  initialMode?: InitialMode;
  onChange: (selection: ModpackSelection | null) => void;
  onOpenProviderSettings?: () => void;
}) {
  if (initialMode === 'Link') return <ProviderLinkPicker value={value} onChange={onChange} />;
  if (initialMode === 'Import') return <LocalPackImport value={value} onChange={onChange} />;
  return <BrowseModpackPicker value={value} onChange={onChange} />;
}

function BrowseModpackPicker({ value, onChange }: {
  value: ModpackSelection | null;
  onChange: (selection: ModpackSelection | null) => void;
}) {
  const bridge = useAppStore(state => state.bridge);
  const command = useAppStore(state => state.command);
  const [provider, setProvider] = useState<ModpackProvider>('Modrinth');
  const [draft, setDraft] = useState<Query>(emptyQuery);
  const [query, setQuery] = useState<Query>(emptyQuery);
  const [queryRevision, setQueryRevision] = useState(0);
  const [projects, setProjects] = useState<ModpackProject[]>([]);
  const [state, setState] = useState<BrowserState>('Uninitialized');
  const [detail, setDetail] = useState('');
  const [failedStage, setFailedStage] = useState('');
  const [providerStatuses, setProviderStatuses] = useState<ModpackProviderStatus[]>([]);
  const [providerVersions, setProviderVersions] = useState<ModpackVersionInventory | null>(null);
  const generation = useRef(0);
  const resultsRef = useRef<HTMLDivElement>(null);
  const onChangeRef = useRef(onChange);
  const valueRef = useRef(value);
  onChangeRef.current = onChange;
  valueRef.current = value;

  useEffect(() => {
    if (!bridge) return;
    let active = true;
    void bridge.request<ModpackProviderStatus[]>('modpacks.providers').then(result => {
      if (active) setProviderStatuses(result);
    }).catch(() => undefined);
    return () => { active = false; };
  }, [bridge]);

  useEffect(() => {
    if (!bridge) return;
    let active = true;
    const controller = new AbortController();
    const apply = (inventory: ModpackVersionInventory) => {
      if (!active) return;
      setProviderVersions(inventory);
      setDraft(current => current.minecraftVersion && inventory.versions.length > 0 &&
        !inventory.versions.some(version => version.versionId === current.minecraftVersion)
        ? { ...current, minecraftVersion: '' } : current);
    };
    void (async () => {
      const cached = await bridge.request<ModpackVersionInventory>('modpacks.versions',
        { provider, cacheOnly: true }, controller.signal);
      if (cached.versions.length) apply(cached);
      const current = await bridge.request<ModpackVersionInventory>('modpacks.versions',
        { provider, cacheOnly: false }, controller.signal);
      apply(current);
    })().catch(() => { if (active) setProviderVersions(null); });
    return () => { active = false; controller.abort(); };
  }, [bridge, provider]);

  useEffect(() => {
    if (!bridge) return;
    const requestGeneration = ++generation.current;
    const controller = new AbortController();
    const parameters = { provider, ...query, limit: 20, includeExperimental: false };
    const apply = (result: ModpackCatalogResult) => {
      if (requestGeneration !== generation.current) return;
      setProjects(result.items);
      setDetail(result.detail);
      setFailedStage(result.failedStage);
      setState(toBrowserState(result));
      reconcileSelection(result.items, provider, valueRef.current, onChangeRef.current);
    };
    const run = async () => {
      setFailedStage('');
      setState('Loading cache');
      try {
        const cached = await bridge.request<ModpackCatalogResult>('modpacks.cache', parameters, controller.signal);
        if (requestGeneration !== generation.current) return;
        if (cached.items.length) {
          apply(cached);
          setState('Refreshing');
        } else {
          setState('Loading provider');
        }
        const current = await bridge.request<ModpackCatalogResult>('modpacks.search', parameters, controller.signal);
        apply(current);
      } catch (reason) {
        if (requestGeneration !== generation.current) return;
        if (controller.signal.aborted) {
          setState('Cancelled');
          return;
        }
        setState('Failed');
        setDetail(reason instanceof Error ? reason.message : `${provider} could not load its catalog.`);
        setFailedStage('provider request');
      }
    };
    void run();
    return () => { generation.current += 1; controller.abort(); };
    // queryRevision deliberately retriggers an identical query after Retry.
  }, [bridge, provider, query, queryRevision]);

  const selectedProject = value?.kind === 'remote' && value.project.provider === provider ? value.project : null;
  const releaseOptions = useMemo(() => selectedProject?.versions ?? [], [selectedProject]);
  const versionOptions = useMemo(() => {
    const official = providerVersions?.versions ?? [];
    if (official.length) return official.map(version => ({
      value: version.versionId,
      label: version.kind === 'Release' ? version.versionId : `${version.versionId} · ${version.kind}`
    }));
    return Array.from(new Set(projects.flatMap(project =>
      project.versions.map(release => release.minecraftVersion)).filter(Boolean)))
      .map(version => ({ value: version, label: version }));
  }, [projects, providerVersions]);
  const virtual = useVirtualizer({
    count: projects.length,
    getScrollElement: () => resultsRef.current,
    estimateSize: () => 77,
    overscan: 8,
    initialRect: { width: 620, height: 390 }
  });
  const applySearch = () => setQuery({
    search: draft.search.trim(), minecraftVersion: draft.minecraftVersion.trim(), loader: draft.loader,
    category: draft.category, sort: draft.sort
  });
  const chooseLocal = () => {
    void command<LocalModpackSelection>('modpacks.chooseLocal').then(local => {
      if (!local.cancelled && local.inspection?.canCreate) onChange({ kind: 'local', local });
    });
  };
  const status = providerStatuses.find(item => item.provider === provider);
  const pending = state === 'Loading cache' || state === 'Loading provider';
  const refreshing = state === 'Refreshing';

  return <section className={styles.root} aria-label="Modpack catalog">
    <div className={styles.providerBar}>
      <div className={styles.providerTabs} role="tablist" aria-label="Modpack providers">
        {(['Modrinth', 'CurseForge'] as const).map(item => <button key={item} type="button" role="tab"
          aria-selected={provider === item} data-selected={provider === item}
          onClick={() => { setProvider(item); if (value?.kind === 'remote' && value.project.provider !== item) onChange(null); }}>
          {item}{providerStatuses.length > 0 && <span aria-hidden="true" data-ready={providerStatuses.find(statusItem => statusItem.provider === item)?.available || undefined} />}
        </button>)}
      </div>
      <Button icon={<File size={14} />} onClick={chooseLocal}>Import pack</Button>
    </div>
    <form className={styles.toolbar} onSubmit={event => { event.preventDefault(); applySearch(); }}>
      <TextInput type="search" value={draft.search}
        onChange={event => setDraft(current => ({ ...current, search: event.target.value }))}
        placeholder={`Search ${provider} modpacks`}
        aria-label={`Search ${provider} modpacks`} />
      <Combobox value={draft.minecraftVersion} onChange={minecraftVersion => setDraft(current => ({ ...current, minecraftVersion }))}
        options={[{ value: '', label: 'Any Minecraft version' }, ...versionOptions]} ariaLabel="Minecraft version filter" searchable />
      <Combobox value={draft.loader} onChange={loader => setDraft(current => ({ ...current, loader }))} ariaLabel="Loader filter"
        options={[{ value: '', label: 'All loaders' }, { value: 'fabric', label: 'Fabric' }, { value: 'quilt', label: 'Quilt' }, { value: 'forge', label: 'Forge' }, { value: 'neoforge', label: 'NeoForge' }]} />
      <Combobox value={draft.category} onChange={category => setDraft(current => ({ ...current, category }))} ariaLabel="Category filter"
        options={[{ value: '', label: 'All categories' }, { value: 'adventure', label: 'Adventure' }, { value: 'magic', label: 'Magic' }, { value: 'technology', label: 'Technology' }, { value: 'optimization', label: 'Optimization' }, { value: 'multiplayer', label: 'Multiplayer' }]} />
      <Combobox value={draft.sort} onChange={sort => setDraft(current => ({ ...current, sort }))} ariaLabel="Sort modpacks"
        options={[{ value: 'Downloads', label: 'Popular' }, { value: 'Updated', label: 'Recently updated' }, { value: 'Newest', label: 'Newest' }, { value: 'Relevance', label: 'Relevance' }]} />
      <Button variant="primary" icon={<Search size={14} />} type="submit">Search</Button>
    </form>
    <div className={styles.trendNote}><Info size={13} /><span>{status?.detail ?? `${provider} provider status is loading.`}</span></div>
    {state === 'Authentication required' && <div className={styles.connectState} role="status"><Box size={22} /><div><strong>CurseForge activation in progress</strong><span>CurseForge integration is being activated for ChunkPilot. Modrinth and local pack import remain available.</span></div></div>}
    {(state === 'Failed' || state === 'Rate limited') && <div className={styles.error} role="alert"><strong>{state === 'Rate limited' ? `${provider} rate limit active` : `${provider} catalog unavailable`}</strong><span>{detail}</span><Button onClick={() => setQueryRevision(revision => revision + 1)}>Retry</Button>{failedStage && <details><summary>Technical details</summary><code>Failed stage: {failedStage}</code></details>}</div>}
    {state === 'Offline cache' && <div className={styles.cacheNotice} role="status">{detail}</div>}
    <div className={styles.layout}>
      <div ref={resultsRef} className={styles.list} aria-busy={pending || refreshing}>
        {pending && !projects.length && <LoadingSkeleton provider={provider} />}
        {!pending && state === 'Empty' && <div className={styles.loading}><Box size={24} /><strong>No matching server pack</strong><span>Clear a filter or search another provider.</span></div>}
        {projects.length > 0 && projects.length <= 20 && projects.map(project => <ProjectRow key={`${project.provider}:${project.projectId}`}
          project={project} selected={selectedProject?.projectId === project.projectId} onSelect={() => {
            const release = project.versions.find(item => item.canCreate) ?? project.versions[0];
            onChange(release ? { kind: 'remote', project, release } : null);
          }} />)}
        {projects.length > 20 && <div className={styles.virtualContent} style={{ height: virtual.getTotalSize() }}>
          {virtual.getVirtualItems().map(row => {
            const project = projects[row.index];
            return <button key={`${project.provider}:${project.projectId}`} type="button" className={`${styles.project} ${styles.virtualProject}`}
              style={{ transform: `translateY(${row.start}px)` }} data-index={row.index} ref={virtual.measureElement}
              data-selected={selectedProject?.projectId === project.projectId || undefined} onClick={() => {
                const release = project.versions.find(item => item.canCreate) ?? project.versions[0];
                onChange(release ? { kind: 'remote', project, release } : null);
              }}>
              <PackImage project={project} />
              <span className={styles.projectCopy}><strong>{project.name}</strong><small>{project.author} · {project.downloadCount?.toLocaleString() ?? 'Downloads unavailable'} downloads</small><span>{project.summary}</span></span>
              <StatusBadge tone={project.versions.some(release => release.canCreate) ? 'success' : 'warning'}>
                {project.serverSupport === 'FullyAutomated' ? 'Server pack' : project.serverSupport === 'AutomatedWithReview' ? 'Validation required' : project.serverSupport}
              </StatusBadge>
            </button>;
          })}
        </div>}
      </div>
      <aside className={styles.detail}>
        {value?.kind === 'local' && value.local.inspection ? <>
          <div className={styles.detailTitle}><div className={styles.fallback}><File size={22} /></div><div><h3>{value.local.inspection.name}</h3><p>{value.local.fileName} · local pack</p></div></div>
          <p>{value.local.inspection.summary || 'No pack summary was provided.'}</p>
          <dl><div><dt>Minecraft</dt><dd>{value.local.inspection.minecraftVersion}</dd></div><div><dt>Loader</dt><dd>{value.local.inspection.loader} {value.local.inspection.loaderVersion}</dd></div><div><dt>Server files</dt><dd>{value.local.inspection.requiredServerFiles} required · {value.local.inspection.excludedClientFiles} client-only excluded</dd></div></dl>
          <StatusBadge tone="warning">On-demand validation</StatusBadge>
        </> : selectedProject ? <>
          <div className={styles.detailTitle}><PackImage project={selectedProject} large /><div><h3>{selectedProject.name}</h3><p>by {selectedProject.author} · {selectedProject.provider}</p></div></div>
          <p>{selectedProject.summary}</p>
          <label className={styles.releaseLabel}>Exact release<Combobox value={value?.kind === 'remote' ? value.release.versionId : ''} onChange={versionId => {
            const release = releaseOptions.find(item => item.versionId === versionId);
            if (release) onChange({ kind: 'remote', project: selectedProject, release });
          }} ariaLabel="Exact modpack release" options={releaseOptions.map(release => ({ value: release.versionId, label: `${release.versionName} · Minecraft ${release.minecraftVersion} · ${release.loader}` }))} /></label>
          {value?.kind === 'remote' && <dl><div><dt>Release</dt><dd>{value.release.releaseChannel}</dd></div><div><dt>Integrity</dt><dd>{value.release.hasIntegrity ? value.project.provider === 'Modrinth' ? 'SHA-1 + SHA-512' : 'Provider SHA-1' : 'Unavailable'}</dd></div><div><dt>Size</dt><dd>{value.release.sizeBytes ? `${(value.release.sizeBytes / 1024 / 1024).toFixed(1)} MB` : 'Unavailable'}</dd></div></dl>}
          {value?.kind === 'remote' && !value.release.canCreate && <div className={styles.releaseLimitation} role="status"><strong>Creation unavailable</strong><span>{value.release.limitation || 'This exact release does not have a complete managed server path.'}</span></div>}
          <StatusBadge tone={value?.kind === 'remote' && value.release.canCreate ? 'warning' : 'neutral'}>{value?.kind === 'remote' && value.release.canCreate ? 'Validated during creation' : 'Browse only'}</StatusBadge>
        </> : <div className={styles.empty}><Box size={24} /><strong>{pending ? `Loading ${provider}` : 'Select a modpack'}</strong><span>{pending ? 'Fetching compatible server-pack releases.' : `Choose an exact ${provider} release or import a local pack.`}</span></div>}
      </aside>
    </div>
  </section>;
}

function ProviderLinkPicker({ value, onChange }: {
  value: ModpackSelection | null;
  onChange: (selection: ModpackSelection | null) => void;
}) {
  const bridge = useAppStore(state => state.bridge);
  const [url, setUrl] = useState('');
  const [pending, setPending] = useState(false);
  const [error, setError] = useState('');
  const [detail, setDetail] = useState('');
  const request = useRef<AbortController | null>(null);
  const remote = value?.kind === 'remote' ? value : null;

  useEffect(() => () => request.current?.abort(), []);
  const paste = async () => {
    try {
      const text = await navigator.clipboard.readText();
      if (text) setUrl(text.trim());
    } catch {
      setError('Clipboard access is unavailable. Paste the link into the field instead.');
    }
  };
  const resolve = async () => {
    request.current?.abort();
    const controller = new AbortController();
    request.current = controller;
    setPending(true); setError(''); setDetail('');
    try {
      if (!bridge) throw new Error('ChunkPilot is still connecting to the native host.');
      const result = await bridge.request<ResolvedModpackLink>('modpacks.resolveLink', { url: url.trim() }, controller.signal);
      if (controller.signal.aborted) return;
      onChange({ kind: 'remote', project: result.project, release: result.release });
      setUrl(result.canonicalUrl);
      setDetail(result.detail);
    } catch (reason) {
      if (!controller.signal.aborted)
        setError(reason instanceof Error ? reason.message : 'The provider link could not be resolved.');
    } finally {
      if (!controller.signal.aborted) setPending(false);
    }
  };

  return <section className={styles.directRoot} aria-label="Paste provider link">
    <header><Link size={22} /><div><h3>Paste provider link</h3><p>Use an official Modrinth or CurseForge modpack project link. Exact release links stay exact.</p></div></header>
    <form className={styles.pasteRow} onSubmit={event => { event.preventDefault(); void resolve(); }}>
      <TextInput type="url" value={url} autoFocus onChange={event => setUrl(event.target.value)}
        placeholder="modrinth.com/modpack/…" aria-label="Provider project link" />
      <Button type="button" icon={<Clipboard size={14} />} onClick={() => void paste()}>Paste</Button>
      <Button type="submit" variant="primary" icon={<Search size={14} />} disabled={!url.trim() || pending}>{pending ? 'Resolving…' : 'Resolve'}</Button>
    </form>
    <p className={styles.supportedSources}>Supported: Modrinth modpack project and exact-version links. CurseForge links are recognized and will activate after ChunkPilot receives approved application access.</p>
    {error && <div className={styles.error} role="alert"><strong>Could not resolve link</strong><span>{error}</span></div>}
    {remote && <article className={styles.resolvedLink} aria-label="Resolved modpack release">
      <PackImage project={remote.project} large />
      <div><span className={styles.eyebrow}>{remote.project.provider} · resolved release</span><h3>{remote.project.name}</h3><p>{remote.release.versionName}</p><small>Minecraft {remote.release.minecraftVersion} · {remote.release.loader} · {remote.release.releaseChannel}</small>{detail && <span role="status">{detail}</span>}</div>
      <StatusBadge tone={remote.release.canCreate ? 'success' : 'warning'}>{remote.release.canCreate ? 'Ready for review' : 'Unavailable'}</StatusBadge>
    </article>}
  </section>;
}

function LocalPackImport({ value, onChange }: {
  value: ModpackSelection | null;
  onChange: (selection: ModpackSelection | null) => void;
}) {
  const command = useAppStore(state => state.command);
  const [error, setError] = useState('');
  const choose = (kind: 'file' | 'folder') => {
    setError('');
    void command<LocalModpackSelection>('modpacks.chooseLocal', { kind }).then(local => {
      if (local.cancelled) return;
      if (!local.inspection?.canCreate) {
        setError(local.inspection?.limitation || 'This source does not contain a complete supported server path.');
        return;
      }
      onChange({ kind: 'local', local: {
        ...local,
        managementMode: 'ManagedCopy',
        launchRelativePath: local.inspection.launchCandidates.length === 1
          ? local.inspection.launchCandidates[0] : undefined
      } });
    }).catch(reason => setError(reason instanceof Error ? reason.message : 'The server source could not be inspected.'));
  };
  const local = value?.kind === 'local' ? value.local : null;
  const updateLocal = (changes: Partial<LocalModpackSelection>) => {
    if (local) onChange({ kind: 'local', local: { ...local, ...changes } });
  };
  return <section className={styles.directRoot} aria-label="Import local server">
    <header><File size={22} /><div><h3>Import server or modpack</h3><p>Choose a ZIP, .mrpack, server JAR, or complete folder. ChunkPilot inspects it before copying or running anything.</p></div></header>
    <div className={styles.importActions}>
      <Button variant="primary" icon={<File size={14} />} onClick={() => choose('file')}>Import ZIP, pack, or JAR</Button>
      <Button icon={<File size={14} />} onClick={() => choose('folder')}>Import server folder</Button>
    </div>
    {error && <div className={styles.error} role="alert"><strong>Import unavailable</strong><span>{error}</span></div>}
    {local?.inspection && <>
      <article className={styles.resolvedLink}><span className={styles.fallback}><File size={22} /></span><div><span className={styles.eyebrow}>{local.inspection.sourceKind} · reviewed locally</span><h3>{local.inspection.name}</h3><p>{local.fileName}</p><small>Minecraft {local.inspection.minecraftVersion} · {local.inspection.loader} {local.inspection.loaderVersion}</small></div><StatusBadge tone="success">Ready for review</StatusBadge></article>
      <div className={styles.importReview}>
        <dl><div><dt>Contents</dt><dd>{local.inspection.fileCount.toLocaleString()} files · {(local.inspection.expandedSizeBytes / 1024 / 1024).toFixed(1)} MB</dd></div><div><dt>Server root</dt><dd>{local.inspection.serverRoot}</dd></div><div><dt>World data</dt><dd>{local.inspection.containsWorld ? 'Present — preserved' : 'Not detected'}</dd></div><div><dt>Content</dt><dd>{local.inspection.modCount} mods · {local.inspection.pluginCount} plugins</dd></div></dl>
        {local.inspection.launchCandidates.length > 1 && <label>Server launcher<Combobox ariaLabel="Server launcher" value={local.launchRelativePath ?? ''} onChange={launchRelativePath => updateLocal({ launchRelativePath })} options={[{ value: '', label: 'Choose a launcher' }, ...local.inspection.launchCandidates.map(candidate => ({ value: candidate, label: candidate }))]} /></label>}
        {local.inspection.sourceKind === 'ServerFolder' && local.inspection.canReference && <fieldset className={styles.managementModes}><legend>File management</legend><button type="button" data-selected={local.managementMode !== 'ByReference'} onClick={() => updateLocal({ managementMode: 'ManagedCopy' })}><strong>Managed copy</strong><small>Recommended. Source files remain unchanged; ChunkPilot owns the new copy and can recover it.</small></button><button type="button" data-selected={local.managementMode === 'ByReference'} onClick={() => updateLocal({ managementMode: 'ByReference' })}><strong>By reference</strong><small>Keep and run the original folder in place. ChunkPilot will not own or delete it.</small></button></fieldset>}
      </div>
    </>}
  </section>;
}

function toBrowserState(result: ModpackCatalogResult): BrowserState {
  const states: Record<ModpackCatalogResult['state'], BrowserState> = {
    Ready: 'Ready', Empty: 'Empty', OfflineCache: 'Offline cache',
    AuthenticationRequired: 'Authentication required', RateLimited: 'Rate limited', Failed: 'Failed'
  };
  return states[result.state];
}

function reconcileSelection(projects: ModpackProject[], provider: ModpackProvider,
  value: ModpackSelection | null, onChange: (selection: ModpackSelection | null) => void) {
  if (value?.kind === 'local') return;
  const current = value?.kind === 'remote' && value.project.provider === provider
    ? projects.find(project => project.projectId === value.project.projectId) : undefined;
  const currentRelease = current && value?.kind === 'remote'
    ? current.versions.find(release => release.versionId === value.release.versionId) : undefined;
  if (current && currentRelease) {
    if (current !== value?.project || currentRelease !== value?.release)
      onChange({ kind: 'remote', project: current, release: currentRelease });
    return;
  }
  const first = projects.map(project => ({ project, release: project.versions.find(release => release.canCreate) ?? project.versions[0] }))
    .find(item => item.release);
  onChange(first?.release ? { kind: 'remote', project: first.project, release: first.release } : null);
}

function LoadingSkeleton({ provider }: { provider: ModpackProvider }) {
  return <div className={styles.skeletons} role="status" aria-label={`Loading ${provider} modpacks`}>
    {Array.from({ length: 5 }, (_, index) => <div key={index}><span /><p><i /><i /></p></div>)}
  </div>;
}

function ProjectRow({ project, selected, onSelect }: { project: ModpackProject; selected: boolean; onSelect: () => void }) {
  return <button type="button" className={styles.project} data-selected={selected || undefined} onClick={onSelect}>
    <PackImage project={project} />
    <span className={styles.projectCopy}><strong>{project.name}</strong><small>{project.author} · {project.downloadCount?.toLocaleString() ?? 'Downloads unavailable'} downloads</small><span>{project.summary}</span></span>
    <StatusBadge tone={project.versions.some(release => release.canCreate) ? 'success' : 'warning'}>
      {project.serverSupport === 'FullyAutomated' ? 'Server pack' : project.serverSupport === 'AutomatedWithReview' ? 'Validation required' : project.serverSupport}
    </StatusBadge>
  </button>;
}

function PackImage({ project, large = false }: { project: ModpackProject; large?: boolean }) {
  const bridge = useAppStore(state => state.bridge);
  const [source, setSource] = useState('');
  useEffect(() => {
    let active = true;
    if (!project.hasImage || !bridge) return;
    void bridge.request<{ dataUrl: string | null }>('modpacks.image', { provider: project.provider, projectId: project.projectId })
      .then(result => { if (active && result.dataUrl) setSource(result.dataUrl); })
      .catch(() => undefined);
    return () => { active = false; };
  }, [bridge, project.hasImage, project.projectId, project.provider]);
  return <span className={`${styles.art} ${large ? styles.artLarge : ''}`}>{source ? <img src={source} alt="" loading="lazy" /> : <Image size={large ? 26 : 18} aria-hidden="true" />}</span>;
}
