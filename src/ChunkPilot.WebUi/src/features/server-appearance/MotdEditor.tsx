import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type CSSProperties, type FormEvent, type MouseEvent, type ReactNode } from 'react';
import { Bold, Clipboard, Code2, Italic, Redo2, RotateCcw, Strikethrough, Underline, Undo2 } from '../../design-system/Icons';
import { Button } from '../../design-system/Primitives';
import {
  applyFormatting, clearFormatting, defaultMotdStyle, deleteBackwardRange, deleteForwardRange,
  motdColors, orderedSelection, parseMotd, replaceSelection, serializeMotd, styleAt, validateMotd,
  type MotdRun, type MotdSelection, type MotdStyle
} from './motd';
import styles from './ServerAppearance.module.css';

const runClass = (run: MotdRun) => [
  run.color ? styles[`mc${run.color}`] : '', run.bold ? styles.mcBold : '', run.italic ? styles.mcItalic : '',
  run.underline ? styles.mcUnderline : '', run.strike ? styles.mcStrike : '', run.obfuscated ? styles.mcObfuscated : ''
].filter(Boolean).join(' ');

const obfuscatedGlyphs = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!?#%&@';
function scramble(text: string, frame: number): string {
  return [...text].map((character, index) => /\s/u.test(character) ? character : obfuscatedGlyphs[(frame * 7 + index * 13 + character.codePointAt(0)!) % obfuscatedGlyphs.length]).join('');
}

function useObfuscationFrame(enabled: boolean): { frame: number; reduced: boolean } {
  const reducedMotionQuery = () => typeof window.matchMedia === 'function' ? window.matchMedia('(prefers-reduced-motion: reduce)') : null;
  const [frame, setFrame] = useState(0);
  const [visible, setVisible] = useState(() => !document.hidden);
  const [reduced, setReduced] = useState(() => reducedMotionQuery()?.matches ?? false);
  useEffect(() => {
    const media = reducedMotionQuery();
    const changed = () => setReduced(media?.matches ?? false);
    const visibility = () => setVisible(!document.hidden);
    media?.addEventListener('change', changed); document.addEventListener('visibilitychange', visibility);
    return () => { media?.removeEventListener('change', changed); document.removeEventListener('visibilitychange', visibility); };
  }, []);
  useEffect(() => {
    if (!enabled || !visible || reduced) { setFrame(0); return; }
    const timer = window.setInterval(() => setFrame(value => (value + 1) % obfuscatedGlyphs.length), 110);
    return () => window.clearInterval(timer);
  }, [enabled, visible, reduced]);
  return { frame, reduced };
}

function FormattedRuns({ runs }: { runs: MotdRun[] }) {
  const obfuscation = useObfuscationFrame(runs.some(run => run.obfuscated && run.text.length > 0));
  return <>{runs.map((run, index) => <span
    key={`${index}-${run.text.length}`}
    className={runClass(run)}
    data-color={run.color ?? undefined}
    data-bold={run.bold || undefined}
    data-italic={run.italic || undefined}
    data-underline={run.underline || undefined}
    data-strike={run.strike || undefined}
    data-obfuscated={run.obfuscated || undefined}
    data-obfuscated-preview={run.obfuscated ? scramble(run.text, obfuscation.reduced ? 0 : obfuscation.frame) : undefined}
    aria-label={run.obfuscated ? 'Obfuscated text' : undefined}
  >{run.text}</span>)}</>;
}

function textOffset(root: HTMLElement, node: Node | null, offset: number): number | null {
  if (!node || !root.contains(node)) return null;
  const range = document.createRange();
  range.selectNodeContents(root);
  try { range.setEnd(node, offset); return range.toString().length; }
  catch { return null; }
}

function captureSelection(root: HTMLElement | null): MotdSelection | null {
  const native = window.getSelection();
  if (!root || !native || !native.rangeCount || !root.contains(native.anchorNode) || !root.contains(native.focusNode)) return null;
  const anchor = textOffset(root, native.anchorNode, native.anchorOffset);
  const focus = textOffset(root, native.focusNode, native.focusOffset);
  return anchor == null || focus == null ? null : { anchor, focus };
}

function pointAt(root: HTMLElement, requested: number): { node: Node; offset: number } {
  const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
  let remaining = Math.max(0, requested); let node: Node | null = walker.nextNode(); let last: Node = root;
  while (node) {
    last = node; const length = node.textContent?.length ?? 0;
    if (remaining <= length) return { node, offset: remaining };
    remaining -= length; node = walker.nextNode();
  }
  return last === root ? { node: root, offset: root.childNodes.length } : { node: last, offset: last.textContent?.length ?? 0 };
}

function restoreSelection(root: HTMLElement | null, selection: MotdSelection | null) {
  if (!root || !selection) return;
  const anchor = pointAt(root, selection.anchor); const focus = pointAt(root, selection.focus);
  const native = window.getSelection(); if (!native) return;
  native.removeAllRanges();
  if (typeof native.setBaseAndExtent === 'function') native.setBaseAndExtent(anchor.node, anchor.offset, focus.node, focus.offset);
  else { const range = document.createRange(); range.setStart(anchor.node, anchor.offset); range.setEnd(focus.node, focus.offset); native.addRange(range); }
}

function toolbarButton(label: string, icon: ReactNode, onApply: () => void, active = false) {
  return <button type="button" aria-label={label} title={label} aria-pressed={active} data-active={active || undefined}
    onMouseDown={event => event.preventDefault()} onClick={onApply}>{icon}</button>;
}

export function MotdEditor({ serverName, serverIconUrl, savedRaw, resetToken, onChange }: {
  serverName: string;
  serverIconUrl: string | null;
  savedRaw: string;
  resetToken: number;
  onChange: (raw: string) => void;
}) {
  const initial = useMemo(() => parseMotd(savedRaw), [savedRaw, resetToken]);
  const [raw, setRaw] = useState(savedRaw);
  const [runs, setRuns] = useState<MotdRun[]>(initial.runs);
  const [mode, setMode] = useState<'visual' | 'raw'>(() => initial.visualSafe ? 'visual' : 'raw');
  const [renderVersion, setRenderVersion] = useState(0);
  const [activeStyle, setActiveStyle] = useState<MotdStyle>(defaultMotdStyle());
  const editor = useRef<HTMLDivElement>(null);
  const semanticSelection = useRef<MotdSelection>({ anchor: 0, focus: 0 });
  const pendingSelection = useRef<MotdSelection | null>(null);
  const typingStyle = useRef<MotdStyle>(defaultMotdStyle());
  const history = useRef<string[]>([savedRaw]);
  const historyIndex = useRef(0);
  const parsed = useMemo(() => parseMotd(raw), [raw]);
  const validation = validateMotd(raw);

  useEffect(() => {
    const next = parseMotd(savedRaw);
    setRaw(savedRaw); setRuns(next.runs); setMode(next.visualSafe ? 'visual' : 'raw'); setRenderVersion(value => value + 1);
    semanticSelection.current = { anchor: 0, focus: 0 }; typingStyle.current = defaultMotdStyle(); setActiveStyle(defaultMotdStyle());
    history.current = [savedRaw]; historyIndex.current = 0;
  }, [savedRaw, resetToken]);

  const rememberSelection = useCallback(() => {
    const next = captureSelection(editor.current);
    if (!next) return;
    semanticSelection.current = next;
    const ordered = orderedSelection(next); const style = styleAt(runs, ordered.start);
    typingStyle.current = style; setActiveStyle(style);
  }, [runs]);

  useEffect(() => {
    const listener = () => rememberSelection();
    document.addEventListener('selectionchange', listener);
    return () => document.removeEventListener('selectionchange', listener);
  }, [rememberSelection]);

  useLayoutEffect(() => {
    if (!pendingSelection.current) return;
    restoreSelection(editor.current, pendingSelection.current);
    pendingSelection.current = null;
  }, [renderVersion, runs]);

  const publish = useCallback((nextRuns: MotdRun[], nextSelection: MotdSelection, addHistory = true) => {
    const nextRaw = serializeMotd(nextRuns);
    setRuns(nextRuns); setRaw(nextRaw); onChange(nextRaw);
    semanticSelection.current = nextSelection; pendingSelection.current = nextSelection; setRenderVersion(value => value + 1);
    typingStyle.current = styleAt(nextRuns, nextSelection.focus); setActiveStyle(typingStyle.current);
    if (addHistory && history.current[historyIndex.current] !== nextRaw) {
      history.current = [...history.current.slice(0, historyIndex.current + 1), nextRaw].slice(-100);
      historyIndex.current = history.current.length - 1;
    }
  }, [onChange]);

  const restoreDraft = (value: string) => {
    const next = parseMotd(value); setRaw(value); setRuns(next.runs); setMode(next.visualSafe ? 'visual' : 'raw'); onChange(value);
    history.current = [...history.current.slice(0, historyIndex.current + 1), value].slice(-100); historyIndex.current = history.current.length - 1;
    semanticSelection.current = { anchor: 0, focus: 0 }; setRenderVersion(version => version + 1);
  };

  const travelHistory = (direction: -1 | 1) => {
    const nextIndex = Math.max(0, Math.min(history.current.length - 1, historyIndex.current + direction));
    if (nextIndex === historyIndex.current) return;
    historyIndex.current = nextIndex; const nextRaw = history.current[nextIndex]; const next = parseMotd(nextRaw);
    setRaw(nextRaw); setRuns(next.runs); onChange(nextRaw); setRenderVersion(value => value + 1);
  };

  const apply = (patch: Partial<MotdStyle> | 'clear') => {
    rememberSelection(); const selection = semanticSelection.current; const { start, end } = orderedSelection(selection);
    if (start === end) {
      const next = patch === 'clear' ? defaultMotdStyle() : { ...typingStyle.current, ...patch };
      typingStyle.current = next; setActiveStyle(next); editor.current?.focus(); restoreSelection(editor.current, selection); return;
    }
    const nextRuns = patch === 'clear' ? clearFormatting(runs, selection) : applyFormatting(runs, selection, patch);
    publish(nextRuns, selection);
  };

  const edit = (selection: MotdSelection, text: string) => {
    const replacement = replaceSelection(runs, selection, text, typingStyle.current);
    publish(replacement.runs, replacement.selection);
  };

  const beforeInput = (event: FormEvent<HTMLDivElement>) => {
    const native = event.nativeEvent as InputEvent; const selection = captureSelection(editor.current) ?? semanticSelection.current;
    if (native.inputType === 'historyUndo') { event.preventDefault(); travelHistory(-1); return; }
    if (native.inputType === 'historyRedo') { event.preventDefault(); travelHistory(1); return; }
    if (native.inputType === 'deleteContentBackward') { event.preventDefault(); edit(deleteBackwardRange(runs, selection), ''); return; }
    if (native.inputType === 'deleteContentForward') { event.preventDefault(); edit(deleteForwardRange(runs, selection), ''); return; }
    if (native.inputType === 'insertParagraph' || native.inputType === 'insertLineBreak') { event.preventDefault(); edit(selection, '\n'); return; }
    if (native.inputType.startsWith('insert') && native.data != null) { event.preventDefault(); edit(selection, native.data); }
  };

  return <div className={styles.motdEditor}>
    <div className={styles.appearanceToolbar}>
      <div className={styles.segmented} aria-label="MOTD editor mode"><button type="button" data-selected={mode === 'visual'} disabled={!parsed.visualSafe} onClick={() => { if (parsed.visualSafe) { setRuns(parsed.runs); setMode('visual'); setRenderVersion(value => value + 1); } }}>Visual</button><button type="button" data-selected={mode === 'raw'} onClick={() => setMode('raw')}>Raw</button></div>
      <div className={styles.editorUtilities}><Button variant="subtle" onClick={() => restoreDraft('')}>New</Button><Button variant="subtle" icon={<RotateCcw size={13} />} onClick={() => restoreDraft(savedRaw)}>Load saved MOTD</Button><Button variant="subtle" icon={<Clipboard size={13} />} onClick={() => void navigator.clipboard.writeText(raw)}>Copy raw</Button></div>
    </div>
    {!parsed.visualSafe && <div className={styles.rawNotice} role="status"><Code2 size={16} aria-hidden="true" /><span>{parsed.reason}</span></div>}
    {mode === 'visual' && parsed.visualSafe ? <>
      <div className={styles.formatBar} aria-label="MOTD formatting toolbar">
        <div className={styles.formatActions}>
          {toolbarButton('Undo', <Undo2 size={15} />, () => travelHistory(-1))}
          {toolbarButton('Redo', <Redo2 size={15} />, () => travelHistory(1))}
          <span />
          {toolbarButton('Bold', <Bold size={15} />, () => apply({ bold: !activeStyle.bold }), activeStyle.bold)}
          {toolbarButton('Italic', <Italic size={15} />, () => apply({ italic: !activeStyle.italic }), activeStyle.italic)}
          {toolbarButton('Underline', <Underline size={15} />, () => apply({ underline: !activeStyle.underline }), activeStyle.underline)}
          {toolbarButton('Strikethrough', <Strikethrough size={15} />, () => apply({ strike: !activeStyle.strike }), activeStyle.strike)}
          {toolbarButton('Obfuscated', <span className={styles.obfuscateButton}>§k</span>, () => apply({ obfuscated: !activeStyle.obfuscated }), activeStyle.obfuscated)}
          {toolbarButton('Clear formatting', <span>§r</span>, () => apply('clear'))}
        </div>
        <div className={styles.colorBar} aria-label="Minecraft colors">{motdColors.map(color => <button type="button" key={color.code} className={styles.colorSwatch} style={{ '--motd-swatch': color.hex } as CSSProperties} aria-label={color.label} title={color.label} aria-pressed={activeStyle.color === color.code} data-active={activeStyle.color === color.code || undefined} onMouseDown={(event: MouseEvent) => event.preventDefault()} onClick={() => apply({ color: color.code })} />)}</div>
      </div>
      <div className={styles.serverListPreview}>
        <div className={styles.previewHeader}><span>Server-list preview</span><span>Approximate Vanilla rendering</span></div>
        <div className={styles.previewBody}>
          {serverIconUrl ? <img src={serverIconUrl} alt="" aria-hidden="true" /> : <div className={styles.previewFallback} aria-hidden="true" />}
          <div className={styles.previewText}><strong>{serverName}</strong><div
            key={renderVersion}
            ref={editor}
            className={styles.richEditor}
            contentEditable
            role="textbox"
            aria-label="Message of the day"
            aria-multiline="true"
            suppressContentEditableWarning
            spellCheck={false}
            onBeforeInput={beforeInput}
            onPaste={event => { event.preventDefault(); edit(captureSelection(editor.current) ?? semanticSelection.current, event.clipboardData.getData('text/plain')); }}
            onKeyUp={rememberSelection}
            onMouseUp={rememberSelection}
            onFocus={rememberSelection}
          ><FormattedRuns runs={runs} /></div></div>
          <span className={styles.previewSignal} aria-hidden="true"><i /><i /><i /><i /></span>
        </div>
      </div>
    </> : <div className={styles.rawEditor}><label htmlFor="motd-raw">Raw Vanilla MOTD</label><textarea id="motd-raw" value={raw} onChange={event => {
      const value = event.target.value; setRaw(value); const next = parseMotd(value); if (next.visualSafe) setRuns(next.runs); onChange(value);
      history.current = [...history.current.slice(0, historyIndex.current + 1), value].slice(-100); historyIndex.current = history.current.length - 1;
    }} spellCheck={false} /><p>Use §0–§f colors, §k/§l/§m/§n/§o styles, §r reset, and one line break. Unsupported input stays exact in Raw mode.</p></div>}
    <div className={styles.motdFooter}><span className={validation ? styles.invalid : ''}>{validation ?? `${raw.length} / 256 formatted characters`}</span><span>MOTD changes require a server restart when it is running.</span></div>
  </div>;
}
