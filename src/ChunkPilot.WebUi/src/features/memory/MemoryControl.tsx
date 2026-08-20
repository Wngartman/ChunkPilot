import { useEffect, useId, useState } from 'react';
import { formatMemory, hostMemoryWarning, memoryPresets, parseMemory } from './memory';
import styles from './MemoryControl.module.css';

export function MemoryControl({ valueMib, onChange, hostTotalBytes, disabled = false, ariaLabel = 'Memory', minimumMib = 512, maximumMib = 24 * 1024 }: {
  valueMib: number;
  onChange: (valueMib: number) => void;
  hostTotalBytes?: number | null;
  disabled?: boolean;
  ariaLabel?: string;
  minimumMib?: number;
  maximumMib?: number;
}) {
  const customId = useId();
  const isPreset = memoryPresets.includes(valueMib as (typeof memoryPresets)[number]);
  const [custom, setCustom] = useState(() => formatMemory(valueMib));
  const [customMode, setCustomMode] = useState(() => !isPreset);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    setCustom(formatMemory(valueMib));
    setError(null);
    setCustomMode(!memoryPresets.includes(valueMib as (typeof memoryPresets)[number]));
  }, [valueMib]);
  const commit = () => {
    const parsed = parseMemory(custom, minimumMib, maximumMib);
    setError(parsed.error);
    if (parsed.valid && parsed.mebibytes != null) {
      onChange(parsed.mebibytes);
      setCustom(parsed.normalized!);
    }
  };
  const warning = hostMemoryWarning(valueMib, hostTotalBytes);
  return <div className={styles.control}>
    <select className={styles.select} aria-label={ariaLabel} disabled={disabled} value={customMode ? 'custom' : String(valueMib)} onChange={event => {
      if (event.target.value === 'custom') { setCustomMode(true); setCustom(formatMemory(valueMib)); return; }
      const next = Number(event.target.value); setCustomMode(false); setError(null); setCustom(formatMemory(next)); onChange(next);
    }}>
      {memoryPresets.filter(value => value >= minimumMib && value <= maximumMib).map(value => <option value={value} key={value}>{formatMemory(value)}</option>)}
      <option value="custom">Custom…</option>
    </select>
    {customMode && <div className={styles.customRow}>
      <input id={customId} className={styles.custom} disabled={disabled} value={custom} aria-label="Custom memory amount" aria-invalid={Boolean(error)} onChange={event => setCustom(event.target.value)} onBlur={commit} onKeyDown={event => { if (event.key === 'Enter') { event.preventDefault(); commit(); } }} />
    </div>}
    {error && <p className={styles.error} role="alert">{error}</p>}
    {warning && <p className={styles.warning}>{warning}</p>}
  </div>;
}
