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
const discoveryPageSize = 50;
const sessionResultLimit = 200;

interface ProviderSession {
  draft: Query;
  query: Query;
  revision: number;
  loadedKey: string;
  projects: ModpackProject[];
  state: BrowserState;
  detail: string;
  failedStage: string;
  versions: ModpackVersionInventory | null;
  canLoadMore: boolean;
  nextIndex: number;
  totalCount: number | null;
  loadingMore: boolean;
  paginationError: string;
  selectedProjectId: string | null;
  selection: Extract<ModpackSelection, { kind: 'remote' }> | null;
  detailState: 'idle' | 'loading' | 'failed';
  detailError: string;
}

function createProviderSession(): ProviderSession {
  return {
    draft: { ...emptyQuery }, query: { ...emptyQuery }, revision: 0, loadedKey: '', projects: [],
    state: 'Uninitialized', detail: '', failedStage: '', versions: null, canLoadMore: false,
    nextIndex: 0, totalCount: null, loadingMore: false, paginationError: '', selectedProjectId: null,
    selection: null, detailState: 'idle', detailError: ''
  };
}

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
  const [sessions, setSessions] = useState<Record<ModpackProvider, ProviderSession>>(() => ({
    Modrinth: createProviderSession(), CurseForge: createProviderSession()
  }));
  const [providerStatuses, setProviderStatuses] = useState<ModpackProviderStatus[]>([]);
  const session = sessions[provider];
  const sessionsRef = useRef(sessions);
  sessionsRef.current = sessions;
  const generation = useRef(0);
  const detailGeneration = useRef(0);
  const searchRequest = useRef<AbortController | null>(null);
  const paginationRequest = useRef<AbortController | null>(null);
  const detailRequest = useRef<AbortController | null>(null);
  const resultsRef = useRef<HTMLDivElement>(null);
  const scrollPositions = useRef<Record<ModpackProvider, number>>({ Modrinth: 0, CurseForge: 0 });
  const onChangeRef = useRef(onChange);
  const valueRef = useRef(value);
  onChangeRef.current = onChange;
  valueRef.current = value;

  useEffect(() => {
    if (value?.kind !== 'remote' || value.project.provider !== provider) return;
    const reviewedProject = {
      ...value.project,
      versions: value.project.versions.map(candidate =>
        candidate.versionId === value.release.versionId ? value.release : candidate)
    };
    setSessions(current => {
      const active = current[provider];
      if (active.selectedProjectId !== reviewedProject.projectId) return current;
      const tracked = active.selection?.release;
      if (tracked?.versionId === value.release.versionId && tracked.canCreate === value.release.canCreate &&
          tracked.preflightState === value.release.preflightState && tracked.limitation === value.release.limitation)
        return current;
      return {
        ...current,
        [provider]: {
          ...active,
          projects: active.projects.map(project => project.projectId === reviewedProject.projectId ? reviewedProject : project),
          selection: { kind: 'remote', project: reviewedProject, release: value.release }
        }
      };
    });
  }, [provider, value]);

  const updateSession = (target: ModpackProvider, update: (current: ProviderSession) => ProviderSession) => {
    setSessions(current => ({ ...current, [target]: update(current[target]) }));
  };

  const resetResultScroll = (target: ModpackProvider) => {
    scrollPositions.current[target] = 0;
    if (target === provider && resultsRef.current) resultsRef.current.scrollTop = 0;
  };

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
    if (sessionsRef.current[provider].versions) return;
    let active = true;
    const controller = new AbortController();
    const apply = (inventory: ModpackVersionInventory) => {
      if (!active) return;
      updateSession(provider, current => ({ ...current, versions: inventory }));
    };
    void (async () => {
      if (provider === 'Modrinth') {
        try {
          const cached = await bridge.request<ModpackVersionInventory>('modpacks.versions',
            { provider, cacheOnly: true }, controller.signal);
          if (cached.versions.length) apply(cached);
        } catch {
          if (controller.signal.aborted) return;
        }
      }
      const current = await bridge.request<ModpackVersionInventory>('modpacks.versions',
        { provider, cacheOnly: false }, controller.signal);
      apply(current);
    })().catch(() => undefined);
    return () => { active = false; controller.abort(); };
  }, [bridge, provider]);

  const queryKey = `${session.query.search}\u0000${session.query.minecraftVersion}\u0000${session.query.loader}\u0000${session.query.category}\u0000${session.query.sort}`;
  useEffect(() => {
    if (!bridge) return;
    const requestKey = `${queryKey}\u0000${session.revision}`;
    if (sessionsRef.current[provider].loadedKey === requestKey) return;
    const requestGeneration = ++generation.current;
    const controller = new AbortController();
    searchRequest.current?.abort();
    searchRequest.current = controller;
    const parameters = { provider, ...sessionsRef.current[provider].query, limit: discoveryPageSize, includeExperimental: false };
    const apply = (result: ModpackCatalogResult, complete: boolean) => {
      if (requestGeneration !== generation.current || result.provider !== provider) return;
      const items = sortLoadedProjects(
        deduplicateProjects(result.items.filter(item => item.provider === provider)).slice(0, sessionResultLimit),
        sessionsRef.current[provider].query.sort);
      updateSession(provider, current => ({
        ...current,
        projects: items,
        state: complete ? toBrowserState(result) : 'Refreshing',
        detail: result.detail,
        failedStage: result.failedStage,
        nextIndex: Number.isInteger(result.nextIndex) ? result.nextIndex : result.items.length,
        canLoadMore: Boolean(result.hasMore ?? result.items.length === discoveryPageSize) && items.length < sessionResultLimit,
        totalCount: typeof result.totalCount === 'number' ? result.totalCount : null,
        loadedKey: complete ? requestKey : current.loadedKey,
        paginationError: ''
      }));
    };
    const run = async () => {
      updateSession(provider, current => ({
        ...current,
        state: current.projects.length ? 'Refreshing' : provider === 'Modrinth' ? 'Loading cache' : 'Loading provider',
        failedStage: '', paginationError: '', loadingMore: false
      }));
      try {
        if (provider === 'Modrinth') {
          try {
            const cached = await bridge.request<ModpackCatalogResult>('modpacks.cache', parameters, controller.signal);
            if (requestGeneration !== generation.current) return;
            if (cached.items.length) apply(cached, false);
            else updateSession(provider, current => ({ ...current, state: 'Loading provider' }));
          } catch {
            if (controller.signal.aborted || requestGeneration !== generation.current) return;
            updateSession(provider, current => ({ ...current, state: 'Loading provider' }));
          }
        }
        const current = await bridge.request<ModpackCatalogResult>('modpacks.search', parameters, controller.signal);
        apply(current, true);
      } catch (reason) {
        if (requestGeneration !== generation.current) return;
        if (controller.signal.aborted) return;
        updateSession(provider, current => ({
          ...current, state: 'Failed',
          detail: reason instanceof Error ? reason.message : `${provider} could not load its catalog.`,
          failedStage: 'provider request'
        }));
      } finally {
        if (searchRequest.current === controller) searchRequest.current = null;
      }
    };
    void run();
    return () => {
      generation.current += 1;
      controller.abort();
      paginationRequest.current?.abort();
      detailRequest.current?.abort();
    };
  }, [bridge, provider, queryKey, session.revision]);

  useEffect(() => {
    const trimmed = session.draft.search.trim();
    if (trimmed === session.query.search) return;
    const timer = window.setTimeout(() => {
      resetResultScroll(provider);
      updateSession(provider, current => ({
        ...current, query: { ...current.draft, search: current.draft.search.trim() }, revision: current.revision + 1,
        loadedKey: '', selectedProjectId: null, selection: null, detailState: 'idle', detailError: ''
      }));
      if (valueRef.current?.kind !== 'local') onChangeRef.current(null);
    }, 275);
    return () => window.clearTimeout(timer);
  }, [provider, session.draft.search, session.query.search]);

  useEffect(() => {
    const frame = window.requestAnimationFrame(() => {
      if (resultsRef.current) resultsRef.current.scrollTop = scrollPositions.current[provider];
    });
    return () => window.cancelAnimationFrame(frame);
  }, [provider]);

  const selectedProject = session.projects.find(project => project.projectId === session.selectedProjectId) ??
    session.selection?.project ??
    (value?.kind === 'remote' && value.project.provider === provider ? value.project : null);
  const releaseOptions = useMemo(() => selectedProject?.versions ?? [], [selectedProject]);
  const versionOptions = useMemo(() => {
    const official = session.versions?.versions ?? [];
    if (official.length) return official.map(version => ({
      value: version.versionId,
      label: version.kind === 'Release' ? version.versionId : `${version.versionId} · ${version.kind}`
    }));
    return Array.from(new Set(session.projects.flatMap(project =>
      project.versions.map(release => release.minecraftVersion)).filter(Boolean)))
      .map(version => ({ value: version, label: version }));
  }, [session.projects, session.versions]);
  const virtual = useVirtualizer({
    count: session.projects.length,
    getScrollElement: () => resultsRef.current,
    estimateSize: () => 77,
    overscan: 8,
    initialRect: { width: 620, height: 390 }
  });
  const applySearch = () => {
    resetResultScroll(provider);
    updateSession(provider, current => ({
      ...current,
      query: {
        search: current.draft.search.trim(), minecraftVersion: current.draft.minecraftVersion.trim(),
        loader: current.draft.loader, category: current.draft.category, sort: current.draft.sort
      },
      revision: current.revision + 1, loadedKey: '', selectedProjectId: null, selection: null,
      detailState: 'idle', detailError: ''
    }));
    if (valueRef.current?.kind !== 'local') onChangeRef.current(null);
  };
  const clearFilters = () => {
    resetResultScroll(provider);
    updateSession(provider, current => ({
      ...current, draft: { ...emptyQuery }, query: { ...emptyQuery }, revision: current.revision + 1,
      loadedKey: '', selectedProjectId: null, selection: null, detailState: 'idle', detailError: ''
    }));
    if (valueRef.current?.kind !== 'local') onChangeRef.current(null);
  };
  const switchProvider = (nextProvider: ModpackProvider) => {
    if (nextProvider === provider) return;
    if (resultsRef.current) scrollPositions.current[provider] = resultsRef.current.scrollTop;
    setProvider(nextProvider);
    onChangeRef.current(sessionsRef.current[nextProvider].selection);
  };
  const chooseLocal = () => {
    void command<LocalModpackSelection>('modpacks.chooseLocal').then(local => {
      if (!local.cancelled && local.inspection?.canCreate) onChange({ kind: 'local', local });
    });
  };
  const loadMore = async () => {
    const active = sessionsRef.current[provider];
    if (!bridge || paginationRequest.current || active.loadingMore || !active.canLoadMore || active.projects.length >= sessionResultLimit) return;
    const requestGeneration = generation.current;
    const controller = new AbortController();
    paginationRequest.current = controller;
    updateSession(provider, current => ({ ...current, loadingMore: true, paginationError: '' }));
    try {
      const requestedIndex = active.nextIndex;
      const result = await bridge.request<ModpackCatalogResult>('modpacks.search',
        { provider, ...active.query, limit: discoveryPageSize, index: requestedIndex, includeExperimental: false }, controller.signal);
      if (requestGeneration !== generation.current || result.provider !== provider) return;
      updateSession(provider, current => {
        const merged = sortLoadedProjects(
          deduplicateProjects([...current.projects, ...result.items.filter(item => item.provider === provider)])
            .slice(0, sessionResultLimit), current.query.sort);
        return {
          ...current, projects: merged,
          nextIndex: Number.isInteger(result.nextIndex) ? result.nextIndex : requestedIndex + result.items.length,
          canLoadMore: Boolean(result.hasMore ?? result.items.length === discoveryPageSize) && merged.length < sessionResultLimit,
          totalCount: typeof result.totalCount === 'number' ? result.totalCount : current.totalCount,
          detail: result.detail, failedStage: result.failedStage,
          state: result.state === 'Ready' || current.projects.length ? 'Ready' : toBrowserState(result)
        };
      });
    } catch (reason) {
      if (requestGeneration === generation.current && !controller.signal.aborted)
        updateSession(provider, current => ({
          ...current,
          paginationError: reason instanceof Error ? reason.message : `${provider} could not load the next page.`
        }));
    } finally {
      if (paginationRequest.current === controller) {
        paginationRequest.current = null;
        if (requestGeneration === generation.current)
          updateSession(provider, current => ({ ...current, loadingMore: false }));
      }
    }
  };
  const selectProject = async (project: ModpackProject) => {
    detailRequest.current?.abort();
    const targetGeneration = ++detailGeneration.current;
    const replacingSelection = sessionsRef.current[provider].selection?.project.projectId !== project.projectId;
    updateSession(provider, current => ({
      ...current, selectedProjectId: project.projectId,
      selection: replacingSelection ? null : current.selection,
      detailState: 'loading', detailError: ''
    }));
    if (replacingSelection) onChangeRef.current(null);
    if (project.serverPathChecked !== false && project.versions.length) {
      const release = project.versions.find(item => item.canCreate) ?? project.versions[0];
      const selection = release ? { kind: 'remote' as const, project, release } : null;
      updateSession(provider, current => ({ ...current, selection, detailState: 'idle' }));
      onChangeRef.current(selection);
      return;
    }
    if (!bridge) return;
    const controller = new AbortController();
    detailRequest.current = controller;
    try {
      const resolved = await bridge.request<ModpackProject>('modpacks.project',
        { provider, projectId: project.projectId }, controller.signal);
      if (controller.signal.aborted || targetGeneration !== detailGeneration.current ||
          resolved.provider !== provider || resolved.projectId !== project.projectId) return;
      const release = resolved.versions.find(item => item.canCreate) ?? resolved.versions[0];
      const selection = release ? { kind: 'remote' as const, project: resolved, release } : null;
      updateSession(provider, current => ({
        ...current,
        projects: current.projects.map(item => item.projectId === resolved.projectId ? resolved : item),
        selectedProjectId: resolved.projectId, selection, detailState: 'idle', detailError: ''
      }));
      onChangeRef.current(selection);
    } catch (reason) {
      if (!controller.signal.aborted && targetGeneration === detailGeneration.current)
        updateSession(provider, current => ({
          ...current, detailState: 'failed',
          detailError: reason instanceof Error ? reason.message : 'Exact project details are unavailable.'
        }));
    } finally {
      if (detailRequest.current === controller) detailRequest.current = null;
    }
  };
  const chooseRelease = (release: ModpackRelease) => {
    if (!selectedProject) return;
    const selection = { kind: 'remote' as const, project: selectedProject, release };
    updateSession(provider, current => ({ ...current, selection }));
    onChangeRef.current(selection);
  };
  const status = providerStatuses.find(item => item.provider === provider);
  const pending = session.state === 'Loading cache' || session.state === 'Loading provider';
  const refreshing = session.state === 'Refreshing';
  const filtersActive = Object.entries(session.draft).some(([key, item]) => key === 'sort' ? item !== 'Downloads' : Boolean(item));
  const resultSummary = session.totalCount === null
    ? `Showing ${session.projects.length.toLocaleString()} pack${session.projects.length === 1 ? '' : 's'}`
    : `Showing ${session.projects.length.toLocaleString()} of ${session.totalCount.toLocaleString()} packs`;

  return <section className={styles.root} aria-label="Modpack catalog">
    <div className={styles.providerBar}>
      <div className={styles.providerTabs} role="tablist" aria-label="Modpack providers">
        {(['Modrinth', 'CurseForge'] as const).map(item => <button key={item} type="button" role="tab"
          aria-selected={provider === item} data-selected={provider === item}
          onClick={() => switchProvider(item)}>
          {item}{providerStatuses.length > 0 && <span aria-hidden="true" data-ready={providerStatuses.find(statusItem => statusItem.provider === item)?.available || undefined} />}
        </button>)}
      </div>
      <Button icon={<File size={14} />} onClick={chooseLocal}>Import pack</Button>
    </div>
    <form className={styles.toolbar} onSubmit={event => { event.preventDefault(); applySearch(); }}>
      <TextInput type="search" value={session.draft.search}
        onChange={event => updateSession(provider, current => ({ ...current, draft: { ...current.draft, search: event.target.value } }))}
        placeholder={`Search ${provider} modpacks`}
        aria-label={`Search ${provider} modpacks`} />
      <Combobox value={session.draft.minecraftVersion} onChange={minecraftVersion => updateSession(provider, current => ({ ...current, draft: { ...current.draft, minecraftVersion } }))}
        options={[{ value: '', label: 'Any Minecraft version' }, ...versionOptions]} ariaLabel="Minecraft version filter" searchable />
      <Combobox value={session.draft.loader} onChange={loader => updateSession(provider, current => ({ ...current, draft: { ...current.draft, loader } }))} ariaLabel="Loader filter"
        options={[{ value: '', label: 'All loaders' }, { value: 'fabric', label: 'Fabric' }, { value: 'quilt', label: 'Quilt' }, { value: 'forge', label: 'Forge' }, { value: 'neoforge', label: 'NeoForge' }]} />
      <Combobox value={session.draft.category} onChange={category => updateSession(provider, current => ({ ...current, draft: { ...current.draft, category } }))} ariaLabel="Category filter"
        options={[{ value: '', label: 'All categories' }, { value: 'adventure', label: 'Adventure' }, { value: 'magic', label: 'Magic' }, { value: 'technology', label: 'Technology' }, { value: 'optimization', label: 'Optimization' }, { value: 'multiplayer', label: 'Multiplayer' }]} />
      <Combobox value={session.draft.sort} onChange={sort => updateSession(provider, current => ({ ...current, draft: { ...current.draft, sort } }))} ariaLabel="Sort modpacks"
        options={[{ value: 'Downloads', label: 'Popular' }, { value: 'Updated', label: 'Recently updated' }, { value: 'Newest', label: 'Newest releases' }, { value: 'Name', label: 'Name' }, { value: 'Relevance', label: 'Relevance' }]} />
      <Button variant="primary" icon={<Search size={14} />} type="submit">Search</Button>
      {filtersActive && <Button type="button" onClick={clearFilters}>Clear filters</Button>}
    </form>
    <div className={styles.trendNote}><Info size={13} /><span>{status?.detail ?? `${provider} provider status is loading.`}</span></div>
    {session.state === 'Authentication required' && <div className={styles.connectState} role="status"><Box size={22} /><div><strong>CurseForge unavailable</strong><span>This development candidate has no approved native CurseForge credential. Modrinth and local pack import remain available.</span></div></div>}
    {(session.state === 'Failed' || session.state === 'Rate limited') && <div className={styles.error} role="alert"><strong>{session.state === 'Rate limited' ? `${provider} rate limit active` : `${provider} catalog unavailable`}</strong><span>{session.detail}</span><Button onClick={() => updateSession(provider, current => ({ ...current, revision: current.revision + 1, loadedKey: '' }))}>Retry</Button>{session.failedStage && <details><summary>Technical details</summary><code>Failed stage: {session.failedStage}</code></details>}</div>}
    {session.state === 'Offline cache' && <div className={styles.cacheNotice} role="status">{session.detail}</div>}
    {(session.projects.length > 0 || refreshing) && <div className={styles.resultsSummary} role="status" aria-live="polite"><strong>{resultSummary}</strong><span>{refreshing ? `Refreshing ${provider}…` : session.projects.length >= sessionResultLimit && session.canLoadMore === false ? 'Session limit reached — narrow the filters to continue.' : 'Exact server setup is checked when you open a pack.'}</span></div>}
    <div className={styles.layout}>
      <div ref={resultsRef} className={styles.list} aria-busy={pending || refreshing} onScroll={event => {
        const element = event.currentTarget;
        scrollPositions.current[provider] = element.scrollTop;
        if (element.scrollHeight - element.scrollTop - element.clientHeight < 180) void loadMore();
      }}>
        {pending && !session.projects.length && <LoadingSkeleton provider={provider} />}
        {!pending && session.state === 'Empty' && <div className={styles.loading}><Box size={24} /><strong>No matching modpacks</strong><span>Clear a filter or search another provider.</span></div>}
        {session.projects.length > 0 && session.projects.length <= 20 && session.projects.map(project => <ProjectRow key={`${project.provider}:${project.projectId}`}
          project={project} selected={selectedProject?.projectId === project.projectId} onSelect={() => void selectProject(project)} />)}
        {session.projects.length > 20 && <div className={styles.virtualContent} style={{ height: virtual.getTotalSize() }}>
          {virtual.getVirtualItems().map(row => {
            const project = session.projects[row.index];
            return <button key={`${project.provider}:${project.projectId}`} type="button" className={`${styles.project} ${styles.virtualProject}`}
              style={{ transform: `translateY(${row.start}px)` }} data-index={row.index} ref={virtual.measureElement}
              data-selected={selectedProject?.projectId === project.projectId || undefined} onClick={() => void selectProject(project)}>
              <PackImage project={project} />
              <span className={styles.projectCopy}><strong>{project.name}</strong><small>{project.author} · {project.downloadCount?.toLocaleString() ?? 'Downloads unavailable'} downloads</small><span>{project.summary}</span></span>
              <StatusBadge tone={projectStatus(project).tone}>{projectStatus(project).label}</StatusBadge>
            </button>;
          })}
        </div>}
        {session.canLoadMore && <div className={styles.loadMore}><Button disabled={session.loadingMore}
          onClick={() => void loadMore()}>{session.loadingMore ? 'Loading more…' : 'Load more'}</Button></div>}
        {session.paginationError && <div className={styles.paginationError} role="alert"><span>{session.paginationError}</span><Button onClick={() => void loadMore()}>Retry page</Button></div>}
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
          {session.detailState === 'loading' && <div className={styles.detailProgress} role="status">Checking exact releases and server setup…</div>}
          {session.detailState === 'failed' && <div className={styles.releaseLimitation} role="alert"><strong>Project details unavailable</strong><span>{session.detailError}</span><Button onClick={() => void selectProject(selectedProject)}>Retry details</Button></div>}
          {releaseOptions.length > 0 && <label className={styles.releaseLabel}>Exact release<Combobox value={session.selection?.release.versionId ?? ''} onChange={versionId => {
            const release = releaseOptions.find(item => item.versionId === versionId);
            if (release) chooseRelease(release);
          }} ariaLabel="Exact modpack release" options={releaseOptions.map(release => ({ value: release.versionId, label: `${release.versionName} · Minecraft ${release.minecraftVersion} · ${release.loader}` }))} /></label>}
          {session.selection && <><dl><div><dt>Release</dt><dd>{session.selection.release.releaseChannel}</dd></div><div><dt>Server path</dt><dd>{session.selection.release.serverPath ?? 'No supportable server setup found'}</dd></div><div><dt>Integrity</dt><dd>{session.selection.release.hasIntegrity ? session.selection.project.provider === 'Modrinth' ? 'SHA-1 + SHA-512' : 'Provider SHA-1 + local SHA-256 after download' : 'Unavailable'}</dd></div><div><dt>Size</dt><dd>{session.selection.release.sizeBytes ? `${(session.selection.release.sizeBytes / 1024 / 1024).toFixed(1)} MB` : 'Unavailable'}</dd></div><div><dt>Published</dt><dd>{session.selection.release.publishedAt ? new Date(session.selection.release.publishedAt).toLocaleDateString() : 'Unavailable'}</dd></div></dl>{session.selection.release.changelog && <details><summary>Release notes</summary><p>{session.selection.release.changelog}</p></details>}</>}
          {session.selection && !session.selection.release.canCreate && <div className={styles.releaseLimitation} role="status"><strong>Creation unavailable</strong><span>{session.selection.release.limitation || 'This exact release does not have a complete managed server path.'}</span></div>}
          {selectedProject.serverPathChecked === false && session.detailState !== 'failed' && <StatusBadge tone="neutral">Server setup not checked yet</StatusBadge>}
          {selectedProject.serverPathChecked !== false && <StatusBadge tone={session.selection?.release.canCreate ? 'warning' : 'neutral'}>{session.selection?.release.canCreate ? 'Validated during creation' : 'Browse only'}</StatusBadge>}
        </> : <div className={styles.empty}><Box size={24} /><strong>{pending ? `Loading ${provider}` : 'Select a modpack'}</strong><span>{pending ? 'Fetching provider discovery results.' : `Open a ${provider} pack to check its exact releases and server setup.`}</span></div>}
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
    <p className={styles.supportedSources}>Supported: official Modrinth and CurseForge modpack project links and exact-release links. CurseForge resolution requires the approved native credential.</p>
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

function deduplicateProjects(projects: ModpackProject[]): ModpackProject[] {
  const seen = new Set<string>();
  return projects.filter(project => {
    const key = `${project.provider}:${project.projectId}`;
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  });
}

function sortLoadedProjects(projects: ModpackProject[], sort: string): ModpackProject[] {
  return sort === 'Name'
    ? [...projects].sort((left, right) => left.name.localeCompare(right.name, undefined, { sensitivity: 'base' }))
    : projects;
}

function projectStatus(project: ModpackProject): { tone: 'neutral' | 'success' | 'warning'; label: string } {
  if (project.serverPathChecked === false)
    return { tone: 'neutral', label: 'Server setup not checked yet' };
  if (project.serverSupport === 'FullyAutomated')
    return { tone: 'success', label: 'Official server pack' };
  if (project.serverSupport === 'AutomatedWithReview')
    return { tone: 'warning', label: 'ChunkPilot can evaluate this release' };
  return { tone: 'neutral', label: 'No supportable server setup found' };
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
    <StatusBadge tone={projectStatus(project).tone}>{projectStatus(project).label}</StatusBadge>
  </button>;
}

const packImageSources = new Map<string, string>();
interface SharedPackImageRequest {
  controller: AbortController;
  promise: Promise<string | null>;
  subscribers: number;
  settled: boolean;
}
const packImageRequests = new Map<string, SharedPackImageRequest>();

function acquirePackImageRequest(
  key: string,
  load: (signal: AbortSignal) => Promise<string | null>
): { promise: Promise<string | null>; release: () => void } {
  let shared = packImageRequests.get(key);
  if (!shared) {
    const controller = new AbortController();
    shared = { controller, subscribers: 0, settled: false, promise: Promise.resolve(null) };
    const created = shared;
    created.promise = load(controller.signal)
      .then(dataUrl => {
        if (!dataUrl) return null;
        if (packImageSources.size >= 64)
          packImageSources.delete(packImageSources.keys().next().value ?? '');
        packImageSources.set(key, dataUrl);
        return dataUrl;
      })
      .finally(() => {
        created.settled = true;
        if (packImageRequests.get(key) === created)
          packImageRequests.delete(key);
      });
    packImageRequests.set(key, created);
    shared = created;
  }

  shared.subscribers++;
  let released = false;
  return {
    promise: shared.promise,
    release: () => {
      if (released) return;
      released = true;
      shared!.subscribers = Math.max(0, shared!.subscribers - 1);
      if (shared!.subscribers !== 0 || shared!.settled) return;
      if (packImageRequests.get(key) === shared)
        packImageRequests.delete(key);
      shared!.controller.abort();
    }
  };
}

export function PackImage({ project, large = false }: { project: ModpackProject; large?: boolean }) {
  const bridge = useAppStore(state => state.bridge);
  const key = `${project.provider}:${project.projectId}`;
  const [image, setImage] = useState(() => ({ key, source: packImageSources.get(key) ?? '' }));
  const source = image.key === key ? image.source : '';
  useEffect(() => {
    let active = true;
    const cached = packImageSources.get(key);
    setImage({ key, source: cached ?? '' });
    if (!project.hasImage || !bridge) return;
    if (cached) {
      return;
    }
    const request = acquirePackImageRequest(key, signal =>
      bridge.request<{ dataUrl: string | null }>('modpacks.image',
        { provider: project.provider, projectId: project.projectId }, signal)
        .then(result => result.dataUrl));
    void request.promise.then(dataUrl => {
      if (!dataUrl) return;
      if (active) setImage({ key, source: dataUrl });
    })
      .catch(() => undefined);
    return () => {
      active = false;
      request.release();
    };
  }, [bridge, key, project.hasImage, project.projectId, project.provider]);
  return <span className={`${styles.art} ${large ? styles.artLarge : ''}`}>{source ? <img src={source} alt="" loading="lazy" /> : <Image size={large ? 26 : 18} aria-hidden="true" />}</span>;
}
