import { useEffect, useMemo, useRef, useState } from 'react';
import { useVirtualizer } from '@tanstack/react-virtual';
import { SearchInput, StatusBadge } from '../../design-system/Primitives';
import { Check, Server } from '../../design-system/Icons';
import { releaseKindLabel, supportTone, versionMatchesFilter, type MinecraftVersionCatalog, type MinecraftVersionOption, type VersionFilter } from './types';
import styles from './VersionBrowser.module.css';

const filters: { id: VersionFilter; label: string }[] = [
  { id: 'recommended', label: 'Recommended' },
  { id: 'verified', label: 'Verified stable' },
  { id: 'stable', label: 'All releases' },
  { id: 'development', label: 'Development' },
  { id: 'beta', label: 'Beta' },
  { id: 'alpha', label: 'Alpha' },
  { id: 'experimental', label: 'Experimental' },
  { id: 'unavailable', label: 'Unavailable' },
  { id: 'all', label: 'Show all versions' }
];

export function VersionBrowser({ catalog, value, onChange, readonly = false, compact = false, allowUnavailableSelection }: {
  catalog: MinecraftVersionCatalog;
  value: string;
  onChange?: (version: MinecraftVersionOption) => void;
  readonly?: boolean;
  compact?: boolean;
  allowUnavailableSelection?: (version: MinecraftVersionOption) => boolean;
}) {
  const [search, setSearch] = useState('');
  const filterStorageKey = `chunkpilot.versions.filter.${catalog.platform ?? 'Vanilla'}`;
  const [filter, setFilter] = useState<VersionFilter>(() => {
    const saved = window.sessionStorage.getItem(filterStorageKey) as VersionFilter | null;
    if (saved && filters.some(item => item.id === saved)) return saved;
    if (catalog.platform === 'Paper') return 'stable';
    return catalog.versions.some(version => versionMatchesFilter(version, 'verified')) ? 'verified' : 'stable';
  });
  useEffect(() => window.sessionStorage.setItem(filterStorageKey, filter), [filterStorageKey, filter]);
  const [inspectionId, setInspectionId] = useState(value);
  const [profileResult, setProfileResult] = useState('');
  const profile = new URLSearchParams(window.location.search).has('profile');
  const measure = (label: string, action: () => void) => {
    if (!profile) { action(); return; }
    const started = globalThis.performance.now(); action();
    globalThis.requestAnimationFrame(() => setProfileResult(`${label}: ${(globalThis.performance.now() - started).toFixed(1)} ms`));
  };
  const activeId = inspectionId || value;
  const selected = catalog.versions.find(version => version.id === activeId) ?? null;
  const filtered = useMemo(() => {
    const query = search.trim().toLocaleLowerCase();
    return catalog.versions.filter(version => versionMatchesFilter(version, filter) && (!query || `${version.id} ${releaseKindLabel(version.releaseKind)} ${version.support} ${version.releaseTime ? new Date(version.releaseTime).getFullYear() : ''}`.toLocaleLowerCase().includes(query)));
  }, [catalog.versions, filter, search]);
  const scroller = useRef<HTMLDivElement>(null);
  const virtual = useVirtualizer({ count: filtered.length, getScrollElement: () => scroller.current, estimateSize: () => 56, overscan: 12, initialRect: { width: 600, height: 410 } });
  const measuredRows = virtual.getVirtualItems();
  const rows = measuredRows.length || typeof ResizeObserver !== 'undefined'
    ? measuredRows
    : filtered.map((_, index) => ({ index, start: index * 56, size: 56, end: (index + 1) * 56, key: index, lane: 0 }));
  return <div className={styles.browser} data-compact={compact || undefined}>
    <div className={styles.tools}>
      <SearchInput value={search} onChange={event => measure('search', () => setSearch(event.target.value))} placeholder="Search all official versions" aria-label="Search Minecraft versions" />
      <div className={styles.filters} role="group" aria-label="Version category">{filters.map(item => <button type="button" key={item.id} aria-pressed={filter === item.id} data-selected={filter === item.id || undefined} onClick={() => measure('filter', () => setFilter(item.id))}>{item.id === 'stable' && catalog.platform === 'Paper' ? 'All stable' : item.label}</button>)}</div>
    </div>
    <div className={styles.catalogMeta} role="status">
      <span><strong>{catalog.versions.length.toLocaleString()}</strong> official versions · <strong>{filtered.length.toLocaleString()}</strong> shown</span>
      <span>{catalog.fromCache ? catalog.stale ? 'Saved catalog · refresh pending' : 'Saved catalog' : catalog.platform === 'Paper' ? 'Official PaperMC metadata' : 'Official Mojang metadata'}{catalog.retrievedAt ? ` · ${new Date(catalog.retrievedAt).toLocaleDateString()}` : ''}</span>
    </div>
    {catalog.message && <p className={styles.catalogMessage}>{catalog.message}</p>}
    <div className={styles.grid}>
      <div ref={scroller} className={styles.list} role="listbox" aria-label="Minecraft versions" aria-activedescendant={selected ? versionDomId(selected.id) : undefined}>
        <div className={styles.virtualSpace} style={{ height: virtual.getTotalSize() }}>
          {rows.map(row => { const version = filtered[row.index]; const active = version.id === value; const inspected = version.id === activeId; const canSelect = version.selectable || allowUnavailableSelection?.(version) === true; return <button
            type="button"
            id={versionDomId(version.id)}
            role="option"
            aria-selected={active}
            key={version.id}
            className={styles.row}
            data-selected={active || undefined}
            data-inspected={inspected && !active || undefined}
            data-unavailable={!canSelect || undefined}
            title={!version.selectable ? `${version.id}: ${version.supportReason}` : undefined}
            style={{ transform: `translateY(${row.start}px)`, height: row.size }}
            onClick={() => measure('selection', () => { setInspectionId(version.id); if (!readonly && canSelect) onChange?.(version); })}
            onKeyDown={event => {
              if (event.key !== 'ArrowDown' && event.key !== 'ArrowUp') return;
              event.preventDefault();
              const nextIndex = Math.max(0, Math.min(filtered.length - 1, row.index + (event.key === 'ArrowDown' ? 1 : -1)));
              virtual.scrollToIndex(nextIndex, { align: 'auto' });
              window.requestAnimationFrame(() => document.getElementById(versionDomId(filtered[nextIndex].id))?.focus());
            }}
          >
            <span className={styles.rowIcon}>{active ? <Check size={14} /> : <Server size={14} />}</span>
            <span className={styles.rowText}><strong>{version.id}</strong><small>{releaseKindLabel(version.releaseKind)} · {version.javaMajor ? `Java ${version.javaMajor}` : 'Java unknown'}</small></span>
            <StatusBadge tone={supportTone(version.support)}>{version.support}</StatusBadge>
          </button>; })}
        </div>
        {!filtered.length && <div className={styles.noResults}><strong>No matching version</strong><span>Try a different search or category.</span></div>}
      </div>
      <VersionDetails version={selected} />
    </div>
    {profile && <output className={styles.profileOutput} aria-label="Version interaction timing">{profileResult || 'No version interaction measured'}</output>}
  </div>;
}

function versionDomId(id: string): string { return `version-${id.replace(/[^a-zA-Z0-9_-]/g, '-')}`; }

export function VersionDetails({ version }: { version: MinecraftVersionOption | null }) {
  if (!version) return <aside className={styles.details}><div className={styles.detailEmpty}><Server size={22} /><strong>Choose a Minecraft version</strong><p>The list starts with stable releases. Select a build to see whether its artifact, Java requirement, launch profile, and certification evidence are complete.</p></div></aside>;
  return <aside className={styles.details} aria-label={`Minecraft ${version.id} details`}>
    <header><div><span>{releaseKindLabel(version.releaseKind)}</span><h3>Minecraft {version.id}</h3></div><StatusBadge tone={supportTone(version.support)}>{version.support}</StatusBadge></header>
    <p className={styles.reason}>{version.supportReason}</p>
    <dl>
      <div><dt>Released</dt><dd>{version.releaseTime ? new Date(version.releaseTime).toLocaleDateString() : 'Unavailable'}</dd></div>
      <div><dt>Java</dt><dd>{version.javaMajor ? `Java ${version.javaMajor}` : 'Unknown'}{version.javaSource === 'ChunkPilotPolicy' ? ' · compatibility rule' : version.javaSource === 'OfficialMetadata' ? ' · official metadata' : ''}</dd></div>
      <div><dt>Server download</dt><dd>{version.launchProfile.kind === 'PaperNogui' ? 'Choose an exact build below' : version.hasServerArtifact ? version.artifactSize ? `${(version.artifactSize / 1024 / 1024).toFixed(1)} MB` : 'Available' : 'Not published'}</dd></div>
      <div><dt>Integrity</dt><dd>{version.launchProfile.kind === 'PaperNogui' ? 'Verified after exact build selection' : version.hasIntegrityMetadata ? 'Official SHA-1 and size' : 'Incomplete'}</dd></div>
      <div><dt>Launch profile</dt><dd>{version.launchProfile.kind === 'Unknown' ? 'Not established' : version.launchProfile.kind === 'PaperNogui' ? 'Paper dedicated server' : version.launchProfile.kind === 'ModernEulaNogui' ? 'Modern dedicated server' : 'Legacy dedicated server'}</dd></div>
      <div><dt>Certification</dt><dd>{version.certification.level === 'RuntimeCertified' ? 'Runtime certified' : version.certification.level === 'MetadataValidated' ? 'Metadata validated' : version.certification.level}</dd></div>
    </dl>
    {version.evidence.length > 0 && <div className={styles.evidence}><strong>Verified evidence</strong>{version.evidence.map(item => <span key={item}><Check size={12} />{item}</span>)}</div>}
    {version.warnings.length > 0 && <div className={styles.warnings}>{version.warnings.map(item => <p key={item}>{item}</p>)}</div>}
    {version.certification.limitations.length > 0 && <div className={styles.warnings}>{version.certification.limitations.map(item => <p key={item}>{item}</p>)}</div>}
    <small className={styles.provenance}>{version.provenance}</small>
  </aside>;
}
