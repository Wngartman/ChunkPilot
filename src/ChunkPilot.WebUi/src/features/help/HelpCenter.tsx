import { useEffect, useMemo, useState } from 'react';
import { Button, EmptyState, SearchInput, StatusBadge } from '../../design-system/Primitives';
import { useAppStore } from '../../state/store';
import { helpArticles, helpCategories, type HelpArticle, type HelpCategory } from './articles';
import { searchHelpArticles } from './search';
import styles from './HelpCenter.module.css';

export function HelpCenter({ initialArticleId, onDeepLink }: { initialArticleId?: string | null; onDeepLink: (destination: HelpArticle['deepLinks'][number]['destination']) => void }) {
  const bridge = useAppStore(state => state.bridge);
  const [query, setQuery] = useState('');
  const [category, setCategory] = useState<HelpCategory | 'All'>('All');
  const [selectedId, setSelectedId] = useState(initialArticleId ?? helpArticles[0].id);
  useEffect(() => { if (initialArticleId && helpArticles.some(article => article.id === initialArticleId)) setSelectedId(initialArticleId); }, [initialArticleId]);
  const results = useMemo(() => searchHelpArticles(helpArticles, query).filter(article => category === 'All' || article.category === category), [category, query]);
  const selected = helpArticles.find(article => article.id === selectedId) ?? results[0] ?? null;
  const selectArticle = (id: string) => {
    setSelectedId(id);
    const params = new URLSearchParams(window.location.search);
    params.set('page', 'settings'); params.set('category', 'Help & troubleshooting'); params.set('article', id);
    window.history.replaceState({}, '', `${window.location.pathname}?${params}`);
  };
  const openSource = (url: string) => void bridge?.request('help.openExternal', { url }).catch(() => undefined);
  const clearDismissals = () => {
    Object.keys(window.localStorage).filter(key => key.startsWith('chunkpilot.health.dismissed.')).forEach(key => window.localStorage.removeItem(key));
  };
  return <div className={styles.help}>
    <section className={styles.search}><h2>Help & troubleshooting</h2><p>Search symptoms, exact console text, or common names. The guide is bundled and works offline.</p><SearchInput autoFocus value={query} onChange={event => setQuery(event.target.value)} placeholder="Try: failed to bind, not whitelisted, wrong Java…" aria-label="Search help and troubleshooting" /><div className={styles.filters}><button data-selected={category === 'All'} onClick={() => setCategory('All')}>All</button>{helpCategories.map(item => <button key={item} data-selected={category === item} onClick={() => setCategory(item)}>{item}</button>)}</div></section>
    <div className={styles.layout}><nav className={styles.results} aria-label="Help results">{results.length ? results.map(article => <button key={article.id} data-selected={selected?.id === article.id} onClick={() => selectArticle(article.id)}><strong>{article.title}</strong><small>{article.category} · {article.plainLanguage}</small></button>) : <div className={styles.empty}><EmptyState title="No matching help article" detail="Try fewer words, a console phrase, or a broader category. Unknown is not turned into a guessed fix." /></div>}</nav>
      {selected && <article className={styles.article}><StatusBadge tone="info">{selected.category}</StatusBadge><h2>{selected.title}</h2><p>{selected.plainLanguage}</p>
        {selected.exactSignatures.length > 0 && <><h3>Console signatures</h3><div className={styles.signatures}>{selected.exactSignatures.map(value => <code key={value}>{value}</code>)}</div></>}
        <h3>Likely causes</h3><ul>{selected.likelyCauses.map(value => <li key={value}>{value}</li>)}</ul>
        <h3>Safe steps</h3><ol>{selected.safeSteps.map(value => <li key={value}>{value}</li>)}</ol>
        {selected.warnings.map(value => <p className={styles.warning} key={value}><strong>Keep it safe:</strong> {value}</p>)}
        <p className={styles.warning}><strong>When to stop:</strong> {selected.whenToStop}</p>
        <div className={styles.actions}>{selected.deepLinks.map(link => <Button key={`${selected.id}:${link.destination}`} onClick={() => onDeepLink(link.destination)}>{link.label}</Button>)}</div>
        <h3>Related help</h3><div className={styles.related}>{selected.related.map(id => { const related = helpArticles.find(article => article.id === id); return related ? <Button variant="subtle" key={id} onClick={() => selectArticle(id)}>{related.title}</Button> : null; })}</div>
        <h3>Sources</h3><div className={styles.sources}>{selected.sources.map(source => <Button variant="subtle" key={source.url} onClick={() => openSource(source.url)}>{source.title}{source.kind === 'community' ? ' (community)' : ''}</Button>)}</div>
        <p>Reviewed {selected.lastReviewed}. Product steps prefer current ChunkPilot evidence; external documentation opens only when you choose it.</p>
      </article>}
    </div><div><Button variant="subtle" onClick={clearDismissals}>Restore dismissed issue notices</Button></div>
  </div>;
}
