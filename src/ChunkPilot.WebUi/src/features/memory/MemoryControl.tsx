import { useEffect, useId, useState } from 'react';
import { formatGigabytes, formatMemory, hostMemoryWarning, memoryPresets, parseMemory } from './memory';
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
  const [custom, setCustom] = useState(() => formatGigabytes(valueMib));
  const [customMode, setCustomMode] = useState(() => !isPreset);
  const [convertedMib, setConvertedMib] = useState(valueMib);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    setCustom(formatGigabytes(valueMib));
    setConvertedMib(valueMib);
    setError(null);
    setCustomMode(!memoryPresets.includes(valueMib as (typeof memoryPresets)[number]));
  }, [valueMib]);
  const commit = () => {
    const parsed = parseMemory(`${custom} GB`, minimumMib, maximumMib);
    setError(parsed.error);
    if (parsed.valid && parsed.mebibytes != null) {
      onChange(parsed.mebibytes);
      setConvertedMib(parsed.mebibytes);
      setCustom(formatGigabytes(parsed.mebibytes));
    }
  };
  const warning = hostMemoryWarning(valueMib, hostTotalBytes);
  return <div className={styles.control}>
    <select className={styles.select} aria-label={ariaLabel} disabled={disabled} value={customMode ? 'custom' : String(valueMib)} onChange={event => {
      if (event.target.value === 'custom') { setCustomMode(true); setCustom(formatGigabytes(valueMib)); return; }
      const next = Number(event.target.value); setCustomMode(false); setError(null); setCustom(formatGigabytes(next)); setConvertedMib(next); onChange(next);
    }}>
      {memoryPresets.filter(value => value >= minimumMib && value <= maximumMib).map(value => <option value={value} key={value}>{formatMemory(value)}</option>)}
      <option value="custom">Custom…</option>
    </select>
    {customMode && <div className={styles.customRow}>
      <div className={styles.customInput}>
        <input id={customId} className={styles.custom} disabled={disabled} value={custom} inputMode="decimal" placeholder="4" aria-label="Custom memory in gigabytes" aria-describedby={`${customId}-conversion`} aria-invalid={Boolean(error)} onChange={event => setCustom(event.target.value)} onBlur={commit} onKeyDown={event => { if (event.key === 'Enter') { event.preventDefault(); commit(); } }} />
        <span className={styles.unit} aria-hidden="true">GB</span>
      </div>
      <output id={`${customId}-conversion`} className={styles.converted} aria-live="polite">{convertedMib.toLocaleString()} MB</output>
    </div>}
    {error && <p className={styles.error} role="alert">{error}</p>}
    {warning && <p className={styles.warning}>{warning}</p>}
  </div>;
}
