import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { ConfirmDialog } from '../design-system/Primitives';

interface Blocker { discard: () => void; detail: string; }
interface GuardContextValue {
  register: (blocker: Blocker) => () => void;
  navigate: (action: () => void) => void;
}

const GuardContext = createContext<GuardContextValue | null>(null);

export function NavigationGuardProvider({ children }: { children: ReactNode }) {
  const [blocker, setBlocker] = useState<Blocker | null>(null);
  const [pending, setPending] = useState<(() => void) | null>(null);
  const register = useCallback((next: Blocker) => {
    setBlocker(next);
    return () => setBlocker(current => current === next ? null : current);
  }, []);
  const navigate = useCallback((action: () => void) => {
    if (!blocker) { action(); return; }
    setPending(() => action);
  }, [blocker]);
  useEffect(() => {
    if (!blocker) return;
    const beforeUnload = (event: BeforeUnloadEvent) => { event.preventDefault(); event.returnValue = ''; };
    window.addEventListener('beforeunload', beforeUnload);
    return () => window.removeEventListener('beforeunload', beforeUnload);
  }, [blocker]);
  const value = useMemo(() => ({ register, navigate }), [register, navigate]);
  return <GuardContext.Provider value={value}>{children}<ConfirmDialog
    open={pending !== null}
    title="Discard unsaved changes?"
    detail={blocker?.detail ?? 'This view has changes that have not been saved.'}
    confirmLabel="Discard and leave"
    destructive
    onCancel={() => setPending(null)}
    onConfirm={() => {
      const action = pending;
      blocker?.discard();
      setBlocker(null);
      setPending(null);
      action?.();
    }}
  /></GuardContext.Provider>;
}

export function useGuardedNavigation() {
  const context = useContext(GuardContext);
  if (!context) throw new Error('NavigationGuardProvider is missing.');
  return context.navigate;
}

export function useUnsavedChangesGuard(active: boolean, discard: () => void, detail: string) {
  const context = useContext(GuardContext);
  if (!context) throw new Error('NavigationGuardProvider is missing.');
  const register = context.register;
  useEffect(() => active ? register({ discard, detail }) : undefined, [active, register, discard, detail]);
}
