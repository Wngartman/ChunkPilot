import { useEffect, useId, useRef, type ButtonHTMLAttributes, type InputHTMLAttributes, type ReactNode, type SelectHTMLAttributes } from 'react';
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
