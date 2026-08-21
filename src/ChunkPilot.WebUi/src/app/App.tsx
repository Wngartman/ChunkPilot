import { lazy, Suspense, useEffect, useMemo, useState } from 'react';
import { initializeBridge, WebViewBridge, type BridgeAdapter } from '../bridge/client';
import { FixtureBridge } from '../fixtures/catalog';
import { useAppStore } from '../state/store';
import { EmptyState } from '../design-system/Primitives';
import type { ServerSummary } from '../bridge/types';
import { Shell, type GlobalRoute } from './Shell';
import { DashboardPage, ServersPage, ActivityPage, AutomationPage, DesignGalleryPage } from '../features/Pages';
import styles from './App.module.css';
import { NavigationGuardProvider, useGuardedNavigation } from './NavigationGuard';
import { runMeasuredNavigation } from './performance';

const ServerWorkspace = lazy(() => import('../features/ServerWorkspace').then(module => ({ default: module.ServerWorkspace })));
const SettingsPage = lazy(() => import('../features/SettingsPage').then(module => ({ default: module.SettingsPage })));
const CreateServerPage = lazy(() => import('../features/CreateServer').then(module => ({ default: module.CreateServerPage })));

function createBridge(): BridgeAdapter {
  const fixture = new URLSearchParams(window.location.search).get('fixture');
  return fixture ? new FixtureBridge(fixture) : new WebViewBridge();
}

export default function App() {
  return <NavigationGuardProvider><AppContent /></NavigationGuardProvider>;
}

function AppContent() {
  const [route, setRoute] = useState<GlobalRoute>(() => (new URLSearchParams(window.location.search).get('page') as GlobalRoute | null) ?? 'dashboard');
  const [serverRouteId, setServerRouteId] = useState<string | null | undefined>(undefined);
  const [settingsCategory] = useState(() => {
    const query = new URLSearchParams(window.location.search);
    return query.get('category') ?? query.get('settings') ?? 'General';
  });
  const [initializationError, setInitializationError] = useState('');
  const initialized = useAppStore(state => state.snapshot !== null);
  const snapshot = useAppStore(state => state.snapshot);
  const bridge = useMemo(createBridge, []);
  const setBridge = useAppStore(state => state.setBridge);
  const applySnapshot = useAppStore(state => state.applySnapshot);
  const consumeEvent = useAppStore(state => state.consumeEvent);
  const navigate = useGuardedNavigation();
  useEffect(() => {
    setBridge(bridge);
    const unsubscribe = bridge.subscribe(consumeEvent);
    void initializeBridge(bridge).then(applySnapshot).catch(error => setInitializationError(error instanceof Error ? error.message : 'ChunkPilot could not initialize the WebUI.'));
    return () => { unsubscribe(); bridge.dispose(); };
  }, [bridge, setBridge, applySnapshot, consumeEvent]);
  useEffect(() => {
    if (!initialized) return;
    const timer = window.setTimeout(() => {
      void Promise.all([import('../features/ServerWorkspace'), import('../features/SettingsPage'), import('../features/CreateServer')]);
    }, 50);
    return () => window.clearTimeout(timer);
  }, [initialized]);
  useEffect(() => {
    const query = new URLSearchParams(window.location.search);
    if (!query.has('fixture') || !query.has('profile') || !('PerformanceObserver' in window)) return;
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
  let content = route === 'dashboard' ? <DashboardPage onServers={openLibrary} onOpenServer={openServer} onCreate={() => setRoute('create')} />
    : route === 'servers' ? (selected ? <ServerWorkspace serverId={activeServerId!} /> : <ServersPage onOpenServer={openServer} onCreate={() => setRoute('create')} />)
    : route === 'activity' ? <ActivityPage />
    : route === 'automation' ? <AutomationPage />
    : route === 'settings' ? <SettingsPage initialCategory={settingsCategory} />
    : route === 'gallery' ? <DesignGalleryPage />
    : <CreateServerPage onDone={() => { setServerRouteId(undefined); setRoute('servers'); }} />;
  return <Shell route={route} activeServerId={activeServerId} onRoute={next => navigate(() => runMeasuredNavigation(next, () => setRoute(next)))} onOpenServer={openServer} onOpenLibrary={openLibrary}><Suspense fallback={<div className={styles.routeLoading}>Loading view…</div>}>{content}</Suspense></Shell>;
}
