import type { BridgeMethod, ServerSummary } from '../bridge/types';

export interface LifecycleAction {
  method: Extract<BridgeMethod, 'servers.start' | 'servers.stop'> | null;
  label: string;
  pending: boolean;
  destructive: boolean;
}

export function lifecycleAction(server: ServerSummary, bridgeBusy: boolean): LifecycleAction {
  if (bridgeBusy) return { method: null, label: 'Working…', pending: true, destructive: false };
  switch (server.state) {
    case 'Running': return { method: 'servers.stop', label: 'Stop server', pending: false, destructive: true };
    case 'Starting': return { method: null, label: 'Starting…', pending: true, destructive: false };
    case 'Stopping': return { method: null, label: 'Stopping…', pending: true, destructive: false };
    case 'Restarting': return { method: null, label: 'Restarting…', pending: true, destructive: false };
    case 'Saving': return { method: null, label: 'Saving…', pending: true, destructive: false };
    case 'BackingUp': return { method: null, label: 'Backing up…', pending: true, destructive: false };
    case 'Restoring': return { method: null, label: 'Restoring…', pending: true, destructive: false };
    default: return { method: 'servers.start', label: 'Start server', pending: false, destructive: false };
  }
}
