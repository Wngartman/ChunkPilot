import { useEffect, useRef, useState } from 'react';
import { Clipboard, Globe2, Monitor, Wifi } from '../../design-system/Icons';
import { Button, StatusBadge } from '../../design-system/Primitives';
import type { ConnectivitySnapshot, ServerSummary } from '../../bridge/types';
import { useAppStore } from '../../state/store';
import styles from './ConnectionSummary.module.css';

export type AddressKind = 'local' | 'lan' | 'router' | 'last' | 'public';

export interface ConnectionChoice {
  audience: 'computer' | 'home' | 'internet';
  label: string;
  address: string | null;
  kind: AddressKind | null;
  badge: string;
  tone: 'neutral' | 'info' | 'success' | 'warning';
  explanation: string;
}

export function connectionChoice(server: ServerSummary): ConnectionChoice {
  // The Agent/native policy owns inference. A late selected-server enrichment cannot replace a card's facts.
  const summary = server.connection?.serverId === server.id ? server.connection : null;
  if (summary) return { ...summary, address: summary.address ?? null, kind: summary.kind ?? null };
  return {
    audience: 'computer', label: 'Connection status', address: null, kind: null,
    badge: 'Connection not verified', tone: 'neutral', explanation: 'Waiting for native server connection evidence.'
  };
}

export function ConnectionSummary({ server, connectivity, compact = false, showAll = false, onManage }: {
  server: ServerSummary;
  connectivity: ConnectivitySnapshot | null;
  compact?: boolean;
  showAll?: boolean;
  onManage?: () => void;
}) {
  const command = useAppStore(state => state.command);
  const [copied, setCopied] = useState<AddressKind | null>(null);
  const copiedTimer = useRef(0);
  const detailsRef = useRef<HTMLDetailsElement>(null);
  useEffect(() => () => window.clearTimeout(copiedTimer.current), []);
  useEffect(() => { if (showAll && detailsRef.current) detailsRef.current.open = true; }, [showAll]);
  const choice = connectionChoice(server);
  const evidence = server.connection?.serverId === server.id ? server.connection : null;
  const localAddress = evidence?.localAddress ?? (server.state === 'Stopped' ? evidence?.configuredLocalAddress : null) ?? null;
  const lanAddress = evidence?.lanAddress ?? (server.state === 'Stopped' ? evidence?.configuredLanAddress : null) ?? null;
  const Icon = choice.audience === 'internet' ? Globe2 : choice.audience === 'home' ? Wifi : Monitor;
  const copy = (kind: AddressKind) => void command('connectivity.copyAddress', { serverId: server.id, kind }).then(() => {
    setCopied(kind);
    window.clearTimeout(copiedTimer.current);
    copiedTimer.current = window.setTimeout(() => setCopied(null), 1600);
  });
  const copyLabel = choice.audience === 'internet' ? 'Copy friend address' : choice.audience === 'home' ? 'Copy LAN address' : 'Copy local address';
  return <div className={styles.summary} data-compact={compact || undefined}>
    <div className={styles.primary}>
      <Icon size={18} aria-hidden="true" />
      <div className={styles.primaryCopy}><span>{choice.label}</span><code>{choice.address ?? 'Not available yet'}</code><p>{choice.explanation}</p></div>
      <div className={styles.primaryActions}>
        <StatusBadge tone={choice.tone}>{choice.badge}</StatusBadge>
        {choice.kind && choice.address && <Button icon={<Clipboard size={14} />} onClick={() => copy(choice.kind!)}>{copied === choice.kind ? 'Copied' : copyLabel}</Button>}
        {onManage && <Button variant={choice.audience === 'internet' && choice.tone !== 'success' ? 'primary' : 'subtle'} onClick={onManage}>{choice.audience === 'internet' && choice.tone !== 'success' ? 'Set up Internet access' : 'Manage connectivity'}</Button>}
      </div>
    </div>
    {!compact && connectivity?.serverId === server.id && <details ref={detailsRef} className={styles.otherAddresses}>
      <summary>{server.state === 'Stopped' ? 'Configured addresses — server stopped' : 'Observed addresses'}</summary>
      <div className={styles.addressRows}>
        <Address label="This computer" detail={server.state === 'Stopped' ? 'Configured, not currently available' : 'Exact-owned local listener'} value={localAddress} onCopy={() => copy('local')} />
        <Address label="Same home network" detail={server.state === 'Stopped' ? 'Configured, not currently available' : 'Listener evidence only; another device has not been tested'} value={lanAddress} onCopy={() => copy('lan')} />
      </div>
      {choice.audience === 'internet' && <details className={styles.loopbackNote}><summary>Why might the Internet address not work on this PC?</summary><p>Some routers do not let devices inside the home connect through the public Internet address. Use This computer or the home-network address while at home. Friends outside your home should still use the Internet address.</p></details>}
    </details>}
  </div>;
}

function Address({ label, detail, value, onCopy }: { label: string; detail: string; value: string | null; onCopy: () => void }) {
  return <div><span><strong>{label}</strong><small>{detail}</small></span><code>{value ?? 'Unavailable'}</code><Button variant="subtle" disabled={!value} onClick={onCopy}>Copy</Button></div>;
}
