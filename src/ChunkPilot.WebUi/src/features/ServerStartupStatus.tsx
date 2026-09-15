import type { ServerSummary } from '../bridge/types';
import { Button, StatusBadge } from '../design-system/Primitives';
import styles from './ServerWorkspace.module.css';

const displayTime = (value: string) => new Date(value).toLocaleTimeString([], { hour: 'numeric', minute: '2-digit', second: '2-digit', hour12: true });

export function ServerStartupStatus({ server, onConsole }: { server: ServerSummary; onConsole: () => void }) {
  const progress = server.startupProgress;
  if (!progress || progress.serverId !== server.id || !progress.attemptId) return null;
  const starting = ['Starting', 'Restarting', 'Saving', 'Stopping'].includes(server.state);
  const failed = progress.stage === 'Failed' && ['Crashed', 'Stopped'].includes(server.state);
  if (!(progress.isActive && starting || failed)) return null;
  if (progress.stage === 'Ready') return null;
  return <section className={styles.startupStatus} role="status" aria-label="Server startup status">
    <StatusBadge tone={failed ? 'danger' : 'info'}>{progress.title}</StatusBadge>
    <div><p>{progress.detail}</p><small>Attempt started {displayTime(progress.startedAt)}
      {progress.lastOutputAt ? ` · Last server output ${displayTime(progress.lastOutputAt)}` : ' · Waiting for server output'}
      {progress.processId ? ` · Process ${progress.processId}` : ''}</small></div>
    <Button variant="subtle" onClick={onConsole}>View startup console</Button>
  </section>;
}
