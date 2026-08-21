import { useCallback, useMemo, useState, type ReactNode } from 'react';
import { Info, Monitor, Shield, SunMoon } from '../design-system/Icons';
import { Button, SearchInput, TextInput } from '../design-system/Primitives';
import { useAppStore } from '../state/store';
import page from './Page.module.css';
import styles from './ServerWorkspace.module.css';
import { useUnsavedChangesGuard } from '../app/NavigationGuard';

const categories = [
  ['General', Monitor], ['Appearance', SunMoon], ['Privacy & diagnostics', Shield], ['About', Info]
] as const;

export function SettingsPage({ initialCategory = 'General' }: { initialCategory?: string }) {
  const initial = useAppStore(state => state.snapshot!.settings); const command = useAppStore(state => state.command);
  const [draft, setDraft] = useState(() => new URLSearchParams(window.location.search).has('dirty') ? { ...initial, minimizeToTray: !initial.minimizeToTray } : initial); const [category, setCategory] = useState(initialCategory); const [search, setSearch] = useState('');
  const dirty = JSON.stringify(draft) !== JSON.stringify(initial);
  const discard = useCallback(() => setDraft(initial), [initial]);
  useUnsavedChangesGuard(dirty, discard, 'Your application settings have not been saved.');
  const visible = useMemo(() => categories.filter(([name]) => name.toLowerCase().includes(search.toLowerCase())), [search]);
  const update = <K extends keyof typeof draft>(key: K, value: (typeof draft)[K]) => setDraft(current => ({ ...current, [key]: value }));
  return <div className={page.pageWrap}><header className={page.pageHeader}><div><h1>Settings</h1><p>Application preferences. Settings for a specific server stay in its workspace.</p></div></header><div className={styles.settingsLayout}><div className={styles.settingsNavColumn}><SearchInput value={search} onChange={event => setSearch(event.target.value)} placeholder="Search settings" aria-label="Search settings" /><nav className={styles.settingsNav} aria-label="Settings categories">{visible.map(([name, Icon]) => <button key={name} data-selected={category === name} onClick={() => setCategory(name)}><Icon size={14} />{name}</button>)}</nav></div><div><section className={styles.settingsForm}>
    {category === 'General' && <><Row label="Minimize to notification area" detail="Keep ChunkPilot and managed servers running when the window is minimized."><Toggle value={draft.minimizeToTray} onChange={value => update('minimizeToTray', value)} /></Row><Row label="Start minimized" detail="Open ChunkPilot in the notification area when Windows starts it."><Toggle value={draft.startMinimized} onChange={value => update('startMinimized', value)} /></Row><Row label="Start with Windows" detail="Register the current ChunkPilot application for this Windows user."><Toggle value={draft.startWithWindows} onChange={value => update('startWithWindows', value)} /></Row></>}
    {category === 'Appearance' && <Row label="Reduced motion" detail="Remove non-essential interface transitions. Windows preferences are also respected."><Toggle value={draft.reducedMotion} onChange={value => update('reducedMotion', value)} /></Row>}
    {category === 'Privacy & diagnostics' && <><Row label="Privacy" detail="ChunkPilot is local-first. No telemetry or server data leaves this computer by default."><TextInput value="Local only" readOnly /></Row><Row label="Diagnostic data" detail="Secrets are redacted from logs and diagnostic bundles."><TextInput value="Stored locally" readOnly /></Row></>}
    {category === 'About' && <><Row label="ChunkPilot" detail="Native Windows server manager with a WebView2 React preview."><TextInput value="Version 1.3.0 · WebUI 1.0 preview" readOnly /></Row><Row label="WebView2 Runtime" detail="The Evergreen Runtime supplies the renderer. Production assets remain bundled locally."><TextInput value="Detected by the native host" readOnly /></Row></>}
  </section>{dirty && <div className={styles.sticky} role="status"><span>Unsaved application settings</span><div className={page.actions}><Button onClick={discard}>Discard</Button><Button variant="primary" onClick={() => void command('settings.saveGlobal', { ...draft })}>Save changes</Button></div></div>}</div></div></div>;
}

function Row({ label, detail, children }: { label: string; detail: string; children: ReactNode }) { return <div className={styles.settingRow}><div><strong>{label}</strong><p>{detail}</p></div><div>{children}</div></div>; }
function Toggle({ value, onChange }: { value: boolean; onChange: (value: boolean) => void }) { return <button className={page.toggle} data-checked={value} role="switch" aria-checked={value} onClick={() => onChange(!value)} />; }
