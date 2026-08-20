import { useEffect, useMemo, useRef, useState } from 'react';
import { useVirtualizer } from '@tanstack/react-virtual';
import { Box, File, Image, Info, Search } from '../../design-system/Icons';
import { Button, SelectInput, StatusBadge, TextInput } from '../../design-system/Primitives';
import { useAppStore } from '../../state/store';
import type {
  LocalModpackSelection, ModpackCatalogResult, ModpackProject, ModpackProvider,
  ModpackProviderStatus, ModpackRelease
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

export function ModpackPicker({ value, initialMode = 'Browse', onChange, onOpenProviderSettings }: {
  value: ModpackSelection | null;
  initialMode?: InitialMode;
  onChange: (selection: ModpackSelection | null) => void;
  onOpenProviderSettings?: () => void;
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
  const versionOptions = useMemo(() => Array.from(new Set(projects.flatMap(project =>
    project.versions.map(release => release.minecraftVersion)).filter(Boolean))), [projects]);
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
      <TextInput type="search" value={draft.search} autoFocus={initialMode === 'Link'}
        onChange={event => setDraft(current => ({ ...current, search: event.target.value }))}
        placeholder={initialMode === 'Link' ? `Paste a ${provider} project link` : `Search ${provider} modpacks`}
        aria-label={`Search ${provider} modpacks`} />
      <TextInput value={draft.minecraftVersion} list="modpack-minecraft-versions"
        onChange={event => setDraft(current => ({ ...current, minecraftVersion: event.target.value }))}
        aria-label="Minecraft version filter" placeholder="Any Minecraft version" />
      <datalist id="modpack-minecraft-versions">{versionOptions.map(version => <option key={version} value={version} />)}</datalist>
      <SelectInput value={draft.loader} onChange={event => setDraft(current => ({ ...current, loader: event.target.value }))} aria-label="Loader filter">
        <option value="">All loaders</option><option value="fabric">Fabric</option><option value="quilt">Quilt</option><option value="forge">Forge</option><option value="neoforge">NeoForge</option>
      </SelectInput>
      <SelectInput value={draft.category} onChange={event => setDraft(current => ({ ...current, category: event.target.value }))} aria-label="Category filter">
        <option value="">All categories</option><option value="adventure">Adventure</option><option value="magic">Magic</option><option value="technology">Technology</option><option value="optimization">Optimization</option><option value="multiplayer">Multiplayer</option>
      </SelectInput>
      <SelectInput value={draft.sort} onChange={event => setDraft(current => ({ ...current, sort: event.target.value }))} aria-label="Sort modpacks">
        <option value="Downloads">Popular</option><option value="Updated">Recently updated</option><option value="Newest">Newest</option><option value="Relevance">Relevance</option>
      </SelectInput>
      <Button variant="primary" icon={<Search size={14} />} type="submit">Search</Button>
    </form>
    <div className={styles.trendNote}><Info size={13} /><span>{status?.detail ?? `${provider} provider status is loading.`}</span></div>
    {state === 'Authentication required' && <div className={styles.connectState} role="status"><Box size={22} /><div><strong>Connect CurseForge</strong><span>{detail || 'Browsing and provider links need your own CurseForge API key. Local pack import remains available.'}</span></div><Button variant="primary" onClick={onOpenProviderSettings}>Open Content sources</Button></div>}
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
          <label className={styles.releaseLabel}>Exact release<SelectInput value={value?.kind === 'remote' ? value.release.versionId : ''} onChange={event => {
            const release = releaseOptions.find(item => item.versionId === event.target.value);
            if (release) onChange({ kind: 'remote', project: selectedProject, release });
          }}>{releaseOptions.map(release => <option key={release.versionId} value={release.versionId}>{release.versionName} · Minecraft {release.minecraftVersion} · {release.loader}</option>)}</SelectInput></label>
          {value?.kind === 'remote' && <dl><div><dt>Release</dt><dd>{value.release.releaseChannel}</dd></div><div><dt>Integrity</dt><dd>{value.release.hasIntegrity ? value.project.provider === 'Modrinth' ? 'SHA-1 + SHA-512' : 'Provider SHA-1' : 'Unavailable'}</dd></div><div><dt>Size</dt><dd>{value.release.sizeBytes ? `${(value.release.sizeBytes / 1024 / 1024).toFixed(1)} MB` : 'Unavailable'}</dd></div></dl>}
          {value?.kind === 'remote' && !value.release.canCreate && <div className={styles.releaseLimitation} role="status"><strong>Creation unavailable</strong><span>{value.release.limitation || 'This exact release does not have a complete managed server path.'}</span></div>}
          <StatusBadge tone={value?.kind === 'remote' && value.release.canCreate ? 'warning' : 'neutral'}>{value?.kind === 'remote' && value.release.canCreate ? 'Validated during creation' : 'Browse only'}</StatusBadge>
        </> : <div className={styles.empty}><Box size={24} /><strong>{pending ? `Loading ${provider}` : 'Select a modpack'}</strong><span>{pending ? 'Fetching compatible server-pack releases.' : `Choose an exact ${provider} release or import a local pack.`}</span></div>}
      </aside>
    </div>
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
