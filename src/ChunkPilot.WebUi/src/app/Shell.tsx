import { useLayoutEffect, useRef, type ReactNode } from 'react';
import { Activity, History as CalendarClock, Gauge, Minus, Plus, Server, Settings, Square, X } from '../design-system/Icons';
import type { BridgeMethod, ServerSummary } from '../bridge/types';
import { useAppStore } from '../state/store';
import styles from './Shell.module.css';

export type GlobalRoute = 'dashboard' | 'servers' | 'automation' | 'activity' | 'settings' | 'create' | 'gallery';

interface ShellProps {
  route: GlobalRoute;
  activeServerId: string | null;
  onRoute: (route: GlobalRoute) => void;
  onOpenServer: (server: ServerSummary) => void;
  onOpenLibrary: () => void;
  children: ReactNode;
}

export function Shell({ route, activeServerId, onRoute, onOpenServer, onOpenLibrary, children }: ShellProps) {
  const workspace = useRef<HTMLElement>(null);
  const snapshot = useAppStore(state => state.snapshot);
  const command = useAppStore(state => state.command);
  const error = useAppStore(state => state.error);
  const clearError = useAppStore(state => state.clearError);
  const nav = [
    { id: 'dashboard' as const, label: 'Dashboard', icon: Gauge },
    { id: 'servers' as const, label: 'Servers', icon: Server },
    { id: 'automation' as const, label: 'Automation', icon: CalendarClock },
    { id: 'activity' as const, label: 'Activity', icon: Activity },
    { id: 'settings' as const, label: 'Settings', icon: Settings }
  ];
  const windowCommand = (method: BridgeMethod) => { void command(method).catch(() => undefined); };
  useLayoutEffect(() => {
    window.scrollTo(0, 0);
    workspace.current?.scrollTo(0, 0);
  }, [route]);
  const selectedServer = snapshot?.servers.find(server => server.id === activeServerId);
  const title = selectedServer && route === 'servers' ? selectedServer.name : route === 'create' ? 'Create server' : route === 'gallery' ? 'Design gallery' : nav.find(item => item.id === route)?.label ?? 'ChunkPilot';
  return <div className={styles.shell}>
    <header className={styles.titlebar}>
      <div className={styles.brand}><img className={styles.mark} src="./brand/chunkpilot-24.png" width="24" height="24" alt="" aria-hidden="true" /><span>ChunkPilot</span></div>
      <div className={styles.dragRegion} onPointerDown={event => { if (event.button === 0) windowCommand('window.drag'); }} onDoubleClick={() => windowCommand('window.toggleMaximize')}><span>{title}</span></div>
      <div className={styles.windowControls} aria-label="Window controls">
        <button className={styles.windowControl} aria-label="Minimize" onClick={() => windowCommand('window.minimize')}><Minus size={16} /></button>
        <button className={styles.windowControl} aria-label="Maximize or restore" onClick={() => windowCommand('window.toggleMaximize')}><Square size={12} /></button>
        <button className={styles.windowControl} aria-label="Close" onClick={() => windowCommand('window.close')}><X size={16} /></button>
      </div>
    </header>
    <aside className={styles.sidebar} aria-label="Primary navigation">
      <nav className={styles.navList}>{nav.map(item => { const selected = route === item.id && (item.id !== 'servers' || activeServerId === null); return <button key={item.id} className={styles.navItem} data-selected={selected} aria-current={selected ? 'page' : undefined} title={item.label} onClick={() => item.id === 'servers' ? onOpenLibrary() : onRoute(item.id)}><item.icon size={17} aria-hidden="true" /><span>{item.label}</span></button>; })}</nav>
      {snapshot && snapshot.servers.length > 0 && <>
        <div className={styles.navSection}>Your servers</div>
        <div className={`${styles.navList} ${styles.serverList}`}>{snapshot.servers.map(server => <button key={server.id} className={styles.navItem} data-selected={activeServerId === server.id && route === 'servers'} aria-current={activeServerId === server.id && route === 'servers' ? 'page' : undefined} title={server.name} onClick={() => onOpenServer(server)}><Server size={16} aria-hidden="true" /><span>{server.name}</span><i className={styles.serverDot} data-state={server.state} aria-label={server.state} /></button>)}</div>
      </>}
      <button className={styles.navItem} data-selected={route === 'create'} title="Create server" onClick={() => onRoute('create')}><Plus size={17} aria-hidden="true" /><span>Create server</span></button>
      <div className={styles.sidebarFooter}><span className={styles.agentState} data-connected={snapshot?.agentConnected}><i />{snapshot?.agentConnected ? 'ChunkPilot ready' : 'Service unavailable'}</span><small>ChunkPilot {snapshot?.appVersion ?? '1.3.0'}</small></div>
    </aside>
    <main ref={workspace} className={styles.workspace}><div className={styles.page}>{children}</div></main>
    {error && <div className={styles.errorBar} role="alert"><span>{error}</span><button className={styles.windowControl} aria-label="Dismiss error" onClick={clearError}><X size={16} /></button></div>}
  </div>;
}
