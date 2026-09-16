import { useEffect, useId, useRef, useState } from 'react';
import { formatGigabytes, formatMemory, hostMemoryWarning, memoryPresets, parseMemory } from './memory';
import styles from './MemoryControl.module.css';

export function MemoryControl({ valueMib, onChange, onValidityChange, hostTotalBytes, disabled = false, ariaLabel = 'Memory', minimumMib = 512, maximumMib = 24 * 1024 }: {
  valueMib: number;
  onChange: (valueMib: number) => void;
  onValidityChange?: (valid: boolean) => void;
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
  const emittedMib = useRef<number | null>(null);
  useEffect(() => {
    if (emittedMib.current === valueMib) { emittedMib.current = null; return; }
    setCustom(formatGigabytes(valueMib));
    setConvertedMib(valueMib);
    setError(null);
    setCustomMode(!memoryPresets.includes(valueMib as (typeof memoryPresets)[number]));
  }, [valueMib]);
  useEffect(() => {
    const parsed = parseMemory(customMode ? `${custom} GB` : `${valueMib} MB`, minimumMib, maximumMib);
    setError(parsed.error);
    onValidityChange?.(parsed.valid);
  }, [custom, customMode, valueMib, minimumMib, maximumMib, onValidityChange]);
  const changeCustom = (text: string) => {
    setCustom(text);
    const parsed = parseMemory(`${text} GB`, minimumMib, maximumMib);
    setError(parsed.error);
    onValidityChange?.(parsed.valid);
    if (parsed.valid && parsed.mebibytes != null) {
      emittedMib.current = parsed.mebibytes;
      setConvertedMib(parsed.mebibytes);
      onChange(parsed.mebibytes);
    }
  };
  const commit = () => {
    const parsed = parseMemory(`${custom} GB`, minimumMib, maximumMib);
    setError(parsed.error);
    onValidityChange?.(parsed.valid);
    if (parsed.valid && parsed.mebibytes != null) {
      emittedMib.current = parsed.mebibytes;
      onChange(parsed.mebibytes);
      setConvertedMib(parsed.mebibytes);
      setCustom(formatGigabytes(parsed.mebibytes));
    }
  };
  const warning = hostMemoryWarning(valueMib, hostTotalBytes);
  return <div className={styles.control}>
    <select className={styles.select} aria-label={ariaLabel} disabled={disabled} value={customMode ? 'custom' : String(valueMib)} onChange={event => {
      if (event.target.value === 'custom') { setCustomMode(true); setCustom(formatGigabytes(valueMib)); return; }
      const next = Number(event.target.value); setCustomMode(false); setError(null); setCustom(formatGigabytes(next)); setConvertedMib(next); onValidityChange?.(true); emittedMib.current = next; onChange(next);
    }}>
      {memoryPresets.filter(value => value >= minimumMib && value <= maximumMib).map(value => <option value={value} key={value}>{formatMemory(value)}</option>)}
      <option value="custom">Custom…</option>
    </select>
    {customMode && <div className={styles.customRow}>
      <div className={styles.customInput}>
        <input id={customId} className={styles.custom} disabled={disabled} value={custom} inputMode="decimal" placeholder="4" aria-label="Custom memory in gigabytes" aria-describedby={`${customId}-conversion`} aria-invalid={Boolean(error)} onChange={event => changeCustom(event.target.value)} onBlur={commit} onKeyDown={event => { if (event.key === 'Enter') { event.preventDefault(); commit(); } }} />
        <span className={styles.unit} aria-hidden="true">GB</span>
      </div>
      <output id={`${customId}-conversion`} className={styles.converted} aria-live="polite">{convertedMib.toLocaleString()} MB</output>
    </div>}
    {error && <p className={styles.error} role="alert">{error}</p>}
    {warning && <p className={styles.warning}>{warning}</p>}
  </div>;
}
