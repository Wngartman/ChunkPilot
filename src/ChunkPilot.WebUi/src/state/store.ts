import { create } from 'zustand';
import type { BridgeAdapter } from '../bridge/client';
import type { BridgeEvent, BridgeMethod, WebUiSnapshot } from '../bridge/types';

interface AppStore {
  snapshot: WebUiSnapshot | null;
  bridge: BridgeAdapter | null;
  busy: Set<string>;
  pendingOperations: Map<string, string>;
  completedOperations: Set<string>;
  serverSnapshots: Map<string, WebUiSnapshot>;
  cachedPresentationServerId: string | null;
  error: string | null;
  setBridge: (bridge: BridgeAdapter) => void;
  applySnapshot: (snapshot: WebUiSnapshot) => void;
  prepareServerSelection: (serverId: string) => void;
  consumeEvent: (event: BridgeEvent) => void;
  command: <T>(method: BridgeMethod, params?: Record<string, unknown>, signal?: AbortSignal) => Promise<T>;
  clearError: () => void;
}

export const useAppStore = create<AppStore>((set, get) => ({
  snapshot: null,
  bridge: null,
  busy: new Set<string>(),
  pendingOperations: new Map<string, string>(),
  completedOperations: new Set<string>(),
  serverSnapshots: new Map<string, WebUiSnapshot>(),
  cachedPresentationServerId: null,
  error: null,
  setBridge: bridge => set({ bridge }),
  applySnapshot: snapshot => set(state => {
    if (state.snapshot && snapshot.revision < state.snapshot.revision) return state;
    const selectedId = snapshot.selectedServerId;
    if (selectedId && snapshot.workspace?.state === 'Loading' &&
        state.cachedPresentationServerId === selectedId && state.snapshot?.selectedServerId === selectedId) {
      const cached = state.snapshot;
      return {
        snapshot: {
          ...cached,
          revision: snapshot.revision,
          capturedAt: snapshot.capturedAt,
          agentConnected: snapshot.agentConnected,
          appVersion: snapshot.appVersion,
          build: snapshot.build,
          operation: snapshot.operation,
          statusMessage: snapshot.statusMessage,
          host: snapshot.host,
          servers: snapshot.servers,
          activity: snapshot.activity,
          settings: snapshot.settings
        }
      };
    }

    const serverSnapshots = new Map(state.serverSnapshots);
    if (selectedId && snapshot.workspace?.state === 'Ready') {
      serverSnapshots.delete(selectedId);
      serverSnapshots.set(selectedId, snapshot);
      while (serverSnapshots.size > 8) serverSnapshots.delete(serverSnapshots.keys().next().value!);
    }
    return { snapshot, serverSnapshots, cachedPresentationServerId: null };
  }),
  prepareServerSelection: serverId => set(state => {
    const current = state.snapshot;
    const cached = state.serverSnapshots.get(serverId);
    if (!current || !cached) return { cachedPresentationServerId: null };
    const serverSnapshots = new Map(state.serverSnapshots);
    serverSnapshots.delete(serverId);
    serverSnapshots.set(serverId, cached);
    return {
      serverSnapshots,
      cachedPresentationServerId: serverId,
      snapshot: {
        ...cached,
        revision: current.revision,
        capturedAt: current.capturedAt,
        agentConnected: current.agentConnected,
        appVersion: current.appVersion,
        build: current.build,
        operation: current.operation,
        statusMessage: current.statusMessage,
        host: current.host,
        servers: current.servers,
        activity: current.activity,
        settings: current.settings,
        selectedServerId: serverId
      }
    };
  }),
  consumeEvent: event => {
    if (event.event === 'snapshot.changed') get().applySnapshot(event.payload as WebUiSnapshot);
    if (event.event === 'operation.completed') {
      const completion = event.payload as { operationId?: string; method?: string; success?: boolean; error?: string | null };
      if (!completion.method || !completion.operationId) return;
      set(state => {
        const expected = state.pendingOperations.get(completion.method!);
        if (expected && expected !== completion.operationId) return state;
        if (!expected && !state.busy.has(completion.method!)) return state;
        const busy = new Set(state.busy); busy.delete(completion.method!);
        const pendingOperations = new Map(state.pendingOperations); pendingOperations.delete(completion.method!);
        const completedOperations = new Set(state.completedOperations);
        if (!expected) completedOperations.add(completion.operationId!);
        return { busy, pendingOperations, completedOperations,
          error: completion.success === false ? completion.error || 'The operation failed.' : state.error };
      });
    }
  },
  command: async <T,>(method: BridgeMethod, params: Record<string, unknown> = {}, signal?: AbortSignal) => {
    const bridge = get().bridge;
    if (!bridge) throw new Error('Native bridge is unavailable.');
    set(state => ({ busy: new Set(state.busy).add(method), error: null }));
    if (method === 'snapshot.selectServer' && typeof params.serverId === 'string')
      get().prepareServerSelection(params.serverId);
    let keepPending = false;
    try {
      const result = await bridge.request<T>(method, params, signal);
      if (method === 'snapshot.selectServer') {
        const snapshot = result as Partial<WebUiSnapshot> | null;
        if (!snapshot || !Array.isArray(snapshot.servers) || !Number.isFinite(snapshot.revision) ||
            (snapshot.selectedServerId !== null && typeof snapshot.selectedServerId !== 'string'))
          throw new Error('Server details could not be loaded. Try selecting the server again.');
        get().applySnapshot(snapshot as WebUiSnapshot);
      }
      if (method === 'servers.start' || method === 'servers.stop' || method === 'servers.restart' || method === 'servers.delete' || method === 'servers.createManagedCopy' || method === 'versions.install') {
        const accepted = result as { accepted?: boolean; operationId?: string };
        if (accepted?.accepted === true && accepted.operationId) {
          const completedEarly = get().completedOperations.has(accepted.operationId);
          keepPending = !completedEarly;
          set(state => {
            const completedOperations = new Set(state.completedOperations);
            completedOperations.delete(accepted.operationId!);
            const pendingOperations = new Map(state.pendingOperations);
            if (!completedEarly) pendingOperations.set(method, accepted.operationId!);
            return { completedOperations, pendingOperations };
          });
        }
      }
      return result;
    }
    catch (error) { set({ error: error instanceof Error ? error.message : 'The operation failed.' }); throw error; }
    finally { if (!keepPending) set(state => {
      const busy = new Set(state.busy); busy.delete(method);
      const pendingOperations = new Map(state.pendingOperations); pendingOperations.delete(method);
      return { busy, pendingOperations };
    }); }
  },
  clearError: () => set({ error: null })
}));
