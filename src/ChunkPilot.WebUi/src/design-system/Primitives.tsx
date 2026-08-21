import { useEffect, useId, useMemo, useRef, useState, type ButtonHTMLAttributes, type InputHTMLAttributes, type ReactNode, type SelectHTMLAttributes } from 'react';
import { createPortal } from 'react-dom';
import { Search, ServerOff } from './Icons';
import styles from './Primitives.module.css';

export function Button({ variant = 'secondary', icon, className = '', children, ...props }: ButtonHTMLAttributes<HTMLButtonElement> & { variant?: 'primary' | 'secondary' | 'danger' | 'subtle'; icon?: ReactNode }) {
  return <button className={`${styles.button} ${variant === 'primary' ? styles.primary : variant === 'danger' ? styles.danger : variant === 'subtle' ? styles.subtle : ''} ${className}`} {...props}>{icon}{children}</button>;
}

export function IconButton({ label, children, ...props }: ButtonHTMLAttributes<HTMLButtonElement> & { label: string }) {
  return <button className={`${styles.button} ${styles.subtle} ${styles.iconButton}`} aria-label={label} title={label} {...props}>{children}</button>;
}

export function StatusBadge({ tone = 'neutral', children, dot = true, title }: { tone?: 'success' | 'warning' | 'danger' | 'info' | 'neutral'; children: ReactNode; dot?: boolean; title?: string }) {
  const toneClass = tone === 'danger' ? styles.dangerTone : styles[tone];
  return <span className={`${styles.badge} ${toneClass}`} title={title}>{dot && <span className={styles.dot} aria-hidden="true" />}{children}</span>;
}

export function SearchInput(props: InputHTMLAttributes<HTMLInputElement>) {
  return <label className={styles.search}><Search size={15} aria-hidden="true" /><span className="sr-only">Search</span><input className={styles.input} type="search" {...props} /></label>;
}

export function TextInput(props: InputHTMLAttributes<HTMLInputElement>) { return <input className={styles.input} {...props} />; }

export function SelectInput(props: SelectHTMLAttributes<HTMLSelectElement>) { return <select className={`${styles.input} ${styles.select}`} {...props} />; }

export interface ComboboxOption { value: string; label: string; disabled?: boolean; }

/** Select-only, portal-hosted combobox for WebView-safe provider/version/filter menus. */
export function Combobox({ value, options, onChange, ariaLabel, placeholder = 'Select', disabled = false, searchable = false, className = '' }: {
  value: string; options: ComboboxOption[]; onChange: (value: string) => void; ariaLabel: string;
  placeholder?: string; disabled?: boolean; searchable?: boolean; className?: string;
}) {
  const id = useId();
  const trigger = useRef<HTMLButtonElement>(null);
  const list = useRef<HTMLDivElement>(null);
  const search = useRef<HTMLInputElement>(null);
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState('');
  const [active, setActive] = useState(() => Math.max(0, options.findIndex(option => option.value === value)));
  const [position, setPosition] = useState({ left: 0, top: 0, width: 220, maxHeight: 280 });
  const selected = options.find(option => option.value === value);
  const filtered = useMemo(() => {
    const normalized = query.trim().toLowerCase();
    return normalized ? options.filter(option => option.label.toLowerCase().includes(normalized)) : options;
  }, [options, query]);

  useEffect(() => {
    if (!open) return;
    const updatePosition = () => {
      const rect = trigger.current?.getBoundingClientRect();
      if (!rect) return;
      const margin = 8; const gap = 5; const desired = Math.min(320, Math.max(rect.width, 180));
      const width = Math.min(desired, window.innerWidth - margin * 2);
      const below = window.innerHeight - rect.bottom - margin - gap;
      const above = rect.top - margin - gap;
      const useAbove = below < 180 && above > below;
      const maxHeight = Math.max(96, Math.min(320, useAbove ? above : below));
      setPosition({
        left: Math.min(Math.max(margin, rect.left), window.innerWidth - width - margin),
        top: useAbove ? Math.max(margin, rect.top - maxHeight - gap) : rect.bottom + gap,
        width,
        maxHeight
      });
    };
    updatePosition();
    window.addEventListener('resize', updatePosition);
    window.addEventListener('scroll', updatePosition, true);
    const close = (event: PointerEvent) => {
      const target = event.target as Node;
      if (!trigger.current?.contains(target) && !list.current?.contains(target)) setOpen(false);
    };
    document.addEventListener('pointerdown', close);
    return () => {
      window.removeEventListener('resize', updatePosition);
      window.removeEventListener('scroll', updatePosition, true);
      document.removeEventListener('pointerdown', close);
    };
  }, [open]);

  useEffect(() => {
    if (!open) return;
    setQuery('');
    const index = filtered.findIndex(option => option.value === value && !option.disabled);
    setActive(index >= 0 ? index : Math.max(0, filtered.findIndex(option => !option.disabled)));
    window.setTimeout(() => searchable
      ? search.current?.focus()
      : list.current?.querySelector<HTMLElement>('[data-active="true"]')?.focus(), 0);
  }, [open, searchable, value]);

  useEffect(() => {
    if (!open) return;
    const index = Math.max(0, filtered.findIndex(option => !option.disabled));
    setActive(index);
  }, [filtered, open]);

  const move = (direction: 1 | -1) => {
    if (!filtered.length) return;
    let next = active;
    do next = (next + direction + filtered.length) % filtered.length;
    while (filtered[next]?.disabled && next !== active);
    setActive(next);
    window.setTimeout(() => list.current?.querySelector<HTMLElement>(`[data-index="${next}"]`)?.focus(), 0);
  };
  const choose = (option: ComboboxOption) => {
    if (option.disabled) return;
    onChange(option.value); setOpen(false); trigger.current?.focus();
  };
  const onKeyDown = (event: React.KeyboardEvent) => {
    if (event.key === 'Escape') { event.preventDefault(); setOpen(false); trigger.current?.focus(); return; }
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault(); if (!open) setOpen(true); else move(event.key === 'ArrowDown' ? 1 : -1); return;
    }
    if (open && (event.key === 'Enter' || event.key === ' ')) {
      event.preventDefault(); const option = filtered[active]; if (option) choose(option);
    }
  };

  return <>
    <button ref={trigger} type="button" className={`${styles.input} ${styles.comboboxTrigger} ${className}`}
      role="combobox" aria-label={ariaLabel} aria-controls={`${id}-listbox`} aria-expanded={open}
      aria-haspopup="listbox" disabled={disabled} onKeyDown={onKeyDown} onClick={() => setOpen(current => !current)}>
      <span>{selected?.label ?? placeholder}</span><span className={styles.comboboxChevron} aria-hidden="true" />
    </button>
    {open && createPortal(<div ref={list} className={styles.comboboxPopover}
      style={{ left: position.left, top: position.top, width: position.width, maxHeight: position.maxHeight }} onKeyDown={onKeyDown}>
      {searchable && <input ref={search} type="search" className={`${styles.input} ${styles.comboboxSearch}`}
        value={query} onChange={event => setQuery(event.target.value)} aria-label={`Search ${ariaLabel.toLowerCase()}`} />}
      <div id={`${id}-listbox`} role="listbox" aria-label={ariaLabel} className={styles.comboboxList}>
      {filtered.map((option, index) => <button key={option.value || `empty-${index}`} type="button" role="option"
        aria-selected={option.value === value} aria-disabled={option.disabled || undefined} tabIndex={index === active ? 0 : -1}
        data-active={index === active} data-index={index} disabled={option.disabled}
        className={styles.comboboxOption} onMouseMove={() => !option.disabled && setActive(index)} onClick={() => choose(option)}>
        <span>{option.label}</span>{option.value === value && <span className={styles.comboboxCheck} aria-hidden="true">✓</span>}
      </button>)}
      {!filtered.length && <div className={styles.comboboxEmpty}>No matching options</div>}
      </div>
    </div>, document.body)}
  </>;
}

export function EmptyState({ title, detail, action }: { title: string; detail: string; action?: ReactNode }) {
  return <div className={styles.empty}><div><div className={styles.emptyIcon}><ServerOff size={24} aria-hidden="true" /></div><h2>{title}</h2><p>{detail}</p>{action}</div></div>;
}

export function Metric({ label, value, detail }: { label: string; value: string; detail?: string }) {
  return <div className={styles.metric}><div className={styles.metricLabel}>{label}</div><div className={styles.metricValue}>{value}</div>{detail && <div className={styles.metricDetail}>{detail}</div>}</div>;
}

export function PanelTitle({ title, meta, action }: { title: string; meta?: string; action?: ReactNode }) {
  return <header className={styles.panelTitle}><h2>{title}</h2>{action ?? (meta && <span>{meta}</span>)}</header>;
}

export function Sparkline({ values, color = 'var(--cp-accent)' }: { values: number[]; color?: string }) {
  if (values.length < 2) return null;
  const min = Math.min(...values); const max = Math.max(...values); const span = Math.max(1, max - min);
  const points = values.map((value, index) => `${(index / (values.length - 1)) * 100},${54 - ((value - min) / span) * 46}`).join(' ');
  return <svg className={styles.spark} viewBox="0 0 100 58" preserveAspectRatio="none" role="img" aria-label={`Trend from ${values[0].toFixed(1)} to ${values.at(-1)?.toFixed(1)}`}><polyline fill="none" stroke={color} strokeWidth="1.4" vectorEffect="non-scaling-stroke" points={points} /></svg>;
}

export function Dialog({ open, title, children, footer, wide = false, onClose }: {
  open: boolean; title: string; children: ReactNode; footer?: ReactNode; wide?: boolean; onClose: () => void;
}) {
  const titleId = useId();
  const dialogRef = useRef<HTMLElement>(null);
  const previousFocus = useRef<HTMLElement | null>(null);
  useEffect(() => {
    if (!open) return;
    previousFocus.current = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    const focusTimer = window.setTimeout(() => dialogRef.current?.querySelector<HTMLElement>('button, [href], input, select, textarea, [tabindex]:not([tabindex="-1"])')?.focus(), 0);
    const escape = (event: KeyboardEvent) => { if (event.key === 'Escape') { event.preventDefault(); onClose(); } };
    window.addEventListener('keydown', escape);
    return () => { window.clearTimeout(focusTimer); window.removeEventListener('keydown', escape); previousFocus.current?.focus(); };
  }, [open, onClose]);
  if (!open) return null;
  return createPortal(<div className={styles.dialogBackdrop} onMouseDown={event => { if (event.target === event.currentTarget) onClose(); }}>
    <section ref={dialogRef} className={`${styles.dialog} ${wide ? styles.dialogWide : ''}`} role="dialog" aria-modal="true" aria-labelledby={titleId} onKeyDown={event => {
      if (event.key !== 'Tab') return;
      const controls = Array.from(dialogRef.current?.querySelectorAll<HTMLElement>('button:not(:disabled), [href], input:not(:disabled), select:not(:disabled), textarea:not(:disabled), [tabindex]:not([tabindex="-1"])') ?? []);
      if (!controls.length) return;
      const first = controls[0]; const last = controls[controls.length - 1];
      if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); }
      else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
    }}>
      <h2 id={titleId}>{title}</h2><div className={styles.dialogBody}>{children}</div>
      {footer && <div className={styles.dialogActions}>{footer}</div>}
    </section>
  </div>, document.body);
}

export function ConfirmDialog({ open, title, detail, confirmLabel, destructive = false, onConfirm, onCancel }: {
  open: boolean; title: string; detail: string; confirmLabel: string; destructive?: boolean; onConfirm: () => void; onCancel: () => void;
}) {
  const dialogRef = useRef<HTMLElement>(null);
  useEffect(() => {
    if (!open) return;
    const escape = (event: KeyboardEvent) => { if (event.key === 'Escape') { event.preventDefault(); onCancel(); } };
    window.addEventListener('keydown', escape);
    return () => window.removeEventListener('keydown', escape);
  }, [open, onCancel]);
  if (!open) return null;
  return <div className={styles.dialogBackdrop} onMouseDown={event => { if (event.target === event.currentTarget) onCancel(); }}>
    <section ref={dialogRef} className={styles.dialog} role="alertdialog" aria-modal="true" aria-labelledby="confirm-title" aria-describedby="confirm-detail" onKeyDown={event => {
      if (event.key !== 'Tab') return;
      const controls = Array.from(dialogRef.current?.querySelectorAll<HTMLElement>('button:not(:disabled), [href], input:not(:disabled), select:not(:disabled), textarea:not(:disabled)') ?? []);
      if (!controls.length) return;
      const first = controls[0]; const last = controls[controls.length - 1];
      if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); }
      else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
    }}>
      <h2 id="confirm-title">{title}</h2><p id="confirm-detail">{detail}</p>
      <div className={styles.dialogActions}><Button autoFocus onClick={onCancel}>Cancel</Button><Button variant={destructive ? 'danger' : 'primary'} onClick={onConfirm}>{confirmLabel}</Button></div>
    </section>
  </div>;
}
