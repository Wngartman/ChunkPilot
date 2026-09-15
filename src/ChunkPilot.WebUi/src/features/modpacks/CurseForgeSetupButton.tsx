import { useEffect, useRef, useState } from 'react';
import type { CurseForgeConfigurationResult } from '../../bridge/types';
import { Button } from '../../design-system/Primitives';
import { useAppStore } from '../../state/store';

/** Opens native password input; only configuration state crosses the renderer bridge. */
export function CurseForgeSetupButton({ onConfigured }: { onConfigured?: () => void }) {
  const command = useAppStore(state => state.command);
  const busy = useAppStore(state => state.busy.has('providers.configureCurseForge'));
  const [message, setMessage] = useState('');
  const mounted = useRef(true);
  const request = useRef<AbortController | null>(null);
  useEffect(() => { mounted.current = true; return () => { mounted.current = false; request.current?.abort(); }; }, []);
  const configure = async () => {
    if (request.current) return;
    const controller = new AbortController();
    request.current = controller;
    setMessage('');
    try {
      const result = await command<CurseForgeConfigurationResult>('providers.configureCurseForge', {}, controller.signal);
      if (!mounted.current || result.cancelled) return;
      setMessage(result.configured ? 'CurseForge access saved.' : 'Saved CurseForge access removed.');
      onConfigured?.();
    } catch {
      if (mounted.current) setMessage('CurseForge setup could not open. Check the native service and try again.');
    } finally {
      if (request.current === controller) request.current = null;
    }
  };
  return <div><Button disabled={busy} onClick={() => void configure()}>{busy ? 'Native setup open…' : 'Set up CurseForge'}</Button>{message && <p role="status">{message}</p>}</div>;
}
