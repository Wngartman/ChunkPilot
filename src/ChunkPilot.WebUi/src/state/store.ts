import { create } from 'zustand';
import type { BridgeAdapter } from '../bridge/client';
import type { BridgeEvent, BridgeMethod, WebUiSnapshot } from '../bridge/types';

interface AppStore {
  snapshot: WebUiSnapshot | null;
  bridge: BridgeAdapter | null;
  busy: Set<string>;
  pendingOperations: Map<string, string>;
  completedOperations: Set<string>;
  error: string | null;
  setBridge: (bridge: BridgeAdapter) => void;
  applySnapshot: (snapshot: WebUiSnapshot) => void;
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
  error: null,
  setBridge: bridge => set({ bridge }),
  applySnapshot: snapshot => set(state => state.snapshot && snapshot.revision < state.snapshot.revision ? state : { snapshot }),
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
    let keepPending = false;
    try {
      const result = await bridge.request<T>(method, params, signal);
      if (method === 'servers.start' || method === 'servers.stop' || method === 'servers.restart' || method === 'servers.delete') {
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
