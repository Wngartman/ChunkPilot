import type { ServerSummary } from '../bridge/types';
import { Button, StatusBadge } from '../design-system/Primitives';
import styles from './ServerWorkspace.module.css';

export function ServerStartupStatus({ server, onConsole }: { server: ServerSummary; onConsole: () => void }) {
  const progress = server.startupProgress;
  if (!progress || progress.serverId !== server.id || !progress.attemptId) return null;
  const starting = ['Starting', 'Restarting', 'Saving', 'Stopping'].includes(server.state);
  const failed = progress.stage === 'Failed' && ['Crashed', 'Stopped'].includes(server.state);
  if (!(progress.isActive && starting || failed)) return null;
  if (progress.stage === 'Ready') return null;
  return <section className={styles.startupStatus} role="status" aria-label="Server startup status">
    <StatusBadge tone={failed ? 'danger' : 'info'}>{progress.title}</StatusBadge>
    <div><p>{progress.detail}</p><small>Attempt started {new Date(progress.startedAt).toLocaleTimeString()}
      {progress.lastOutputAt ? ` · Last server output ${new Date(progress.lastOutputAt).toLocaleTimeString()}` : ' · Waiting for server output'}
      {progress.processId ? ` · Process ${progress.processId}` : ''}</small></div>
    <Button variant="subtle" onClick={onConsole}>View startup console</Button>
  </section>;
}
