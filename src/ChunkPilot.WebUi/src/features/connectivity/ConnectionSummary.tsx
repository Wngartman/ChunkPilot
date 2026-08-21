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

export function connectionChoice(server: ServerSummary, connectivity?: ConnectivitySnapshot | null): ConnectionChoice {
  if (connectivity?.mode === 'PortForwarding') {
    if (connectivity.addresses.publicVerified) return {
      audience: 'internet', label: 'Share with friends', address: connectivity.addresses.publicVerified,
      kind: 'public', badge: 'Connection confirmed', tone: 'success',
      explanation: `Send this address to friends outside your home.${connectivity.external.checkedAt ? ` Last checked ${connectivity.external.checkedAt}.` : ''}`
    };
    if (connectivity.addresses.routerReported) return {
      audience: 'internet', label: 'Share with friends', address: connectivity.addresses.routerReported,
      kind: 'router', badge: connectivity.external.busy ? 'Checking connection' : 'Still checking', tone: connectivity.external.busy ? 'info' : 'warning',
      explanation: 'This is the most likely address for your friends. ChunkPilot is checking it in the background.'
    };
    if (connectivity.addresses.lastKnownPublic) return {
      audience: 'internet', label: 'Share with friends', address: connectivity.addresses.lastKnownPublic,
      kind: 'last', badge: 'Last used', tone: 'warning',
      explanation: `This was the last known Internet address${connectivity.addresses.lastKnownPublicAt ? ` on ${new Date(connectivity.addresses.lastKnownPublicAt).toLocaleString()}` : ''}. It may have changed, so ChunkPilot needs to check it again.`
    };
    return {
      audience: 'internet', label: 'Share with friends', address: null, kind: null,
      badge: connectivity.external.busy ? 'Checking connection' : 'Setup incomplete', tone: connectivity.external.busy ? 'info' : 'warning',
      explanation: 'Finish the guided setup. ChunkPilot will check the connection automatically when the server and network are ready.'
    };
  }
  if (server.connectionMode === 'PortForwarding') {
    if (server.publicReachability === 'confirmed' && server.publicAddress) return {
      audience: 'internet', label: 'Share with friends', address: server.publicAddress, kind: 'public',
      badge: 'Connection confirmed', tone: 'success',
      explanation: 'Send this address to friends outside your home.'
    };
    if (server.publicAddress) return {
      audience: 'internet', label: 'Share with friends', address: server.publicAddress,
      kind: server.publicAddressKind === 'router' ? 'router' : 'last',
      badge: server.publicAddressKind === 'router' ? 'Still checking' : 'Last used', tone: 'warning',
      explanation: server.publicAddressKind === 'router'
        ? 'This is the most likely address for your friends. It has not passed an outside-in check.'
        : `This was the last known Internet address${server.publicAddressObservedAt ? ` on ${new Date(server.publicAddressObservedAt).toLocaleString()}` : ''}. It may have changed.`
    };
    return {
      audience: 'internet', label: 'Share with friends', address: null, kind: null,
      badge: 'Setup incomplete', tone: 'warning',
      explanation: 'Open connectivity settings to finish setup and check the connection.'
    };
  }
  const lan = connectivity?.addresses.lan ?? server.lanAddress;
  if (lan) return {
    audience: 'home', label: 'Share on your LAN', address: lan, kind: 'lan', badge: 'Home network', tone: 'info',
    explanation: 'Give this address to people connected to the same Wi-Fi or wired network. It will not work over the Internet.'
  };
  return {
    audience: 'computer', label: 'This computer only', address: connectivity?.addresses.local ?? server.localAddress,
    kind: 'local', badge: 'Only this PC', tone: 'neutral', explanation: 'Use this only from Minecraft running on this computer.'
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
  const choice = connectionChoice(server, connectivity);
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
    {!compact && connectivity && <details ref={detailsRef} className={styles.otherAddresses}>
      <summary>Other addresses</summary>
      <div className={styles.addressRows}>
        <Address label="This computer" detail="Minecraft running on this PC" value={connectivity.addresses.local} onCopy={() => copy('local')} />
        <Address label="Same home network" detail="Another PC on the same Wi-Fi or wired network" value={connectivity.addresses.lan} onCopy={() => copy('lan')} />
      </div>
      {choice.audience === 'internet' && <details className={styles.loopbackNote}><summary>Why might the Internet address not work on this PC?</summary><p>Some routers do not let devices inside the home connect through the public Internet address. Use This computer or the home-network address while at home. Friends outside your home should still use the Internet address.</p></details>}
    </details>}
  </div>;
}

function Address({ label, detail, value, onCopy }: { label: string; detail: string; value: string | null; onCopy: () => void }) {
  return <div><span><strong>{label}</strong><small>{detail}</small></span><code>{value ?? 'Unavailable'}</code><Button variant="subtle" disabled={!value} onClick={onCopy}>Copy</Button></div>;
}
