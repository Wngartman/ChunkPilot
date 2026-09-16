import { useCallback, useEffect, useRef, useState } from 'react';

/** Coalesces measurement-driven corrections; never polls or overrides an explicit pause. */
export function useConsoleFollow(alignToEnd: () => void) {
  const [following, setFollowing] = useState(true);
  const current = useRef(true);
  const manualPause = useRef(false);
  const align = useRef(alignToEnd);
  align.current = alignToEnd;
  const frame = useRef(0);
  const settledFrame = useRef(0);
  const settling = useRef(false);

  const cancel = useCallback(() => {
    cancelAnimationFrame(frame.current);
    cancelAnimationFrame(settledFrame.current);
    frame.current = 0;
    settledFrame.current = 0;
    settling.current = false;
  }, []);

  const scheduleFollow = useCallback(() => {
    if (!current.current) return;
    settling.current = true;
    if (frame.current) return;
    frame.current = requestAnimationFrame(() => {
      frame.current = 0;
      if (!current.current) return;
      align.current();
      cancelAnimationFrame(settledFrame.current);
      settledFrame.current = requestAnimationFrame(() => {
        settledFrame.current = 0;
        if (!frame.current) settling.current = false;
      });
    });
  }, []);

  const pauseForUser = useCallback(() => {
    current.current = false;
    setFollowing(false);
    cancel();
  }, [cancel]);

  const toggleFollow = useCallback(() => {
    if (current.current) {
      manualPause.current = true;
      pauseForUser();
    } else {
      manualPause.current = false;
      current.current = true;
      setFollowing(true);
      scheduleFollow();
    }
  }, [pauseForUser, scheduleFollow]);

  const observeScroll = useCallback((viewport: Pick<HTMLElement, 'scrollHeight' | 'scrollTop' | 'clientHeight'>) => {
    // Row measurements and our own end alignment can emit scroll events while settling.
    // Explicit wheel/key/touch upward input cancels settling synchronously instead.
    if (settling.current) return;
    const atEnd = viewport.scrollHeight - viewport.scrollTop - viewport.clientHeight < 24;
    if (!atEnd) pauseForUser();
    else if (!manualPause.current && !current.current) {
      current.current = true;
      setFollowing(true);
      scheduleFollow();
    }
  }, [pauseForUser, scheduleFollow]);

  useEffect(() => cancel, [cancel]);
  return { following, scheduleFollow, toggleFollow, observeScroll, pauseForUser };
}
