import { lazy, Suspense, useEffect, useState } from 'react';
import { initializeBridge, WebViewBridge, type BridgeAdapter } from '../bridge/client';
import { isFixtureMode } from '../fixtures/mode';
import { useAppStore } from '../state/store';
import { EmptyState } from '../design-system/Primitives';
import type { ServerSummary } from '../bridge/types';
import { Shell, type GlobalRoute } from './Shell';
import { DashboardPage, ServersPage, ActivityPage, AutomationPage, DesignGalleryPage } from '../features/Pages';
import styles from './App.module.css';
import { NavigationGuardProvider, useGuardedNavigation } from './NavigationGuard';
import { runMeasuredNavigation } from './performance';
import { ViewErrorBoundary } from './ViewErrorBoundary';

const ServerWorkspace = lazy(() => import('../features/ServerWorkspace').then(module => ({ default: module.ServerWorkspace })));
const SettingsPage = lazy(() => import('../features/SettingsPage').then(module => ({ default: module.SettingsPage })));
const CreateServerPage = lazy(() => import('../features/CreateServer').then(module => ({ default: module.CreateServerPage })));

async function createBridge(): Promise<BridgeAdapter> {
  const fixture = new URLSearchParams(window.location.search).get('fixture');
  if (fixture && isFixtureMode()) {
    const { FixtureBridge } = await import('../fixtures/catalog');
    return new FixtureBridge(fixture);
  }
  return new WebViewBridge();
}

export default function App() {
  return <NavigationGuardProvider><AppContent /></NavigationGuardProvider>;
}

function AppContent() {
  const [route, setRoute] = useState<GlobalRoute>(() => (new URLSearchParams(window.location.search).get('page') as GlobalRoute | null) ?? 'dashboard');
  const [serverRouteId, setServerRouteId] = useState<string | null | undefined>(() => {
    const query = new URLSearchParams(window.location.search);
    return isFixtureMode() && query.get('page') === 'servers' && query.get('mode')?.startsWith('library') ? null : undefined;
  });
  const [settingsCategory, setSettingsCategory] = useState(() => {
    const query = new URLSearchParams(window.location.search);
    return query.get('category') ?? query.get('settings') ?? 'General';
  });
  const [helpArticleId, setHelpArticleId] = useState<string | null>(() => new URLSearchParams(window.location.search).get('article'));
  const [initializationError, setInitializationError] = useState('');
  const initialized = useAppStore(state => state.snapshot !== null);
  const snapshot = useAppStore(state => state.snapshot);
  const setBridge = useAppStore(state => state.setBridge);
  const applySnapshot = useAppStore(state => state.applySnapshot);
  const consumeEvent = useAppStore(state => state.consumeEvent);
  const navigate = useGuardedNavigation();
  useEffect(() => {
    let active = true;
    let bridge: BridgeAdapter | undefined;
    let unsubscribe: (() => void) | undefined;
    void createBridge().then(async adapter => {
      // A fixture chunk can finish loading after this view has already closed.
      if (!active) { adapter.dispose(); return; }
      bridge = adapter;
      setBridge(adapter);
      unsubscribe = adapter.subscribe(consumeEvent);
      const initial = await initializeBridge(adapter);
      if (active) applySnapshot(initial);
    }).catch(error => {
      if (active) setInitializationError(error instanceof Error ? error.message : 'ChunkPilot could not initialize the WebUI.');
    });
    return () => { active = false; unsubscribe?.(); bridge?.dispose(); };
  }, [setBridge, applySnapshot, consumeEvent]);
  useEffect(() => {
    const query = new URLSearchParams(window.location.search);
    if (!isFixtureMode() || !query.has('profile') || !('PerformanceObserver' in window)) return;
    let count = 0; let maximum = 0;
    const observer = new PerformanceObserver(list => {
      for (const entry of list.getEntries()) { count += 1; maximum = Math.max(maximum, entry.duration); }
      document.documentElement.dataset.cpLongTaskCount = String(count);
      document.documentElement.dataset.cpLongTaskMaxMs = maximum.toFixed(1);
    });
    try { observer.observe({ type: 'longtask', buffered: true }); }
    catch { return; }
    return () => observer.disconnect();
  }, []);
  if (initializationError) return <EmptyState title="WebUI could not start" detail={initializationError} />;
  if (!initialized) return <div className={styles.startup} role="status">Opening ChunkPilot…</div>;
  const activeServerId = serverRouteId === undefined ? snapshot?.selectedServerId ?? null : serverRouteId;
  const openLibrary = () => {
    navigate(() => {
      runMeasuredNavigation('servers-library', () => {
        setServerRouteId(null);
        setRoute('servers');
        void useAppStore.getState().command('snapshot.selectServer', { serverId: null }).catch(() => undefined);
      });
    });
  };
  const openServer = (server: ServerSummary) => {
    navigate(() => {
      runMeasuredNavigation('server-workspace', () => {
        setServerRouteId(server.id);
        setRoute('servers');
        void useAppStore.getState().command('snapshot.selectServer', { serverId: server.id }).catch(() => {
          setServerRouteId(current => current === server.id ? null : current);
        });
      });
    });
  };
  const selected = Boolean(activeServerId && snapshot?.servers.some(server => server.id === activeServerId));
  const openHelp = (articleId: string) => {
    setHelpArticleId(articleId);
    setSettingsCategory('Help & troubleshooting');
    setRoute('settings');
  };
  const followHelpDeepLink = (destination: 'console' | 'connectivity' | 'players' | 'backups' | 'versions' | 'files') => {
    const selectedId = snapshot?.selectedServerId;
    if (!selectedId) return;
    const params = new URLSearchParams(window.location.search);
    params.set('page', 'servers');
    if (destination === 'connectivity') { params.set('tab', 'settings'); params.set('settings', 'Connectivity'); }
    else { params.set('tab', destination); params.delete('settings'); }
    window.history.replaceState({}, '', `${window.location.pathname}?${params}`);
    setServerRouteId(selectedId);
    setRoute('servers');
  };
  let content = route === 'dashboard' ? <DashboardPage onServers={openLibrary} onOpenServer={openServer} onCreate={() => setRoute('create')} />
    : route === 'servers' ? (selected ? <ServerWorkspace key={activeServerId!} serverId={activeServerId!} onOpenHelp={openHelp} /> : <ServersPage onOpenServer={openServer} onCreate={() => setRoute('create')} />)
    : route === 'activity' ? <ActivityPage />
    : route === 'automation' ? <AutomationPage />
    : route === 'settings' ? <SettingsPage initialCategory={settingsCategory} initialHelpArticleId={helpArticleId} onHelpDeepLink={followHelpDeepLink} />
    : route === 'gallery' ? <DesignGalleryPage />
    : <CreateServerPage onDone={() => { setServerRouteId(undefined); setRoute('servers'); }} onActivity={() => navigate(() => setRoute('activity'))} />;
  return <Shell route={route} activeServerId={activeServerId} onRoute={next => navigate(() => runMeasuredNavigation(next, () => setRoute(next)))} onOpenServer={openServer} onOpenLibrary={openLibrary}><ViewErrorBoundary key={`${route}:${activeServerId ?? ''}`}><Suspense fallback={<div className={styles.routeLoading} role="status">Loading view…</div>}>{content}</Suspense></ViewErrorBoundary></Shell>;
}
