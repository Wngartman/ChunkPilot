import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react';
import { CircleHelp, Info, Monitor, Shield, SunMoon } from '../design-system/Icons';
import { Button, SearchInput, TextInput } from '../design-system/Primitives';
import { useAppStore } from '../state/store';
import page from './Page.module.css';
import styles from './ServerWorkspace.module.css';
import { useUnsavedChangesGuard } from '../app/NavigationGuard';
import { HelpCenter } from './help/HelpCenter';
import type { HelpArticle } from './help/articles';
import { CurseForgeSetupButton } from './modpacks/CurseForgeSetupButton';

const categories = [
  ['General', Monitor], ['Appearance', SunMoon], ['Privacy & diagnostics', Shield], ['Help & troubleshooting', CircleHelp], ['About', Info]
] as const;

export function SettingsPage({ initialCategory = 'General', initialHelpArticleId, onHelpDeepLink = () => undefined }: { initialCategory?: string; initialHelpArticleId?: string | null; onHelpDeepLink?: (destination: HelpArticle['deepLinks'][number]['destination']) => void }) {
  const snapshot = useAppStore(state => state.snapshot!); const initial = snapshot.settings; const command = useAppStore(state => state.command);
  const applicationService = snapshot.build.curseForgeApplicationService === true;
  const [draft, setDraft] = useState(() => new URLSearchParams(window.location.search).has('dirty') ? { ...initial, minimizeToTray: !initial.minimizeToTray } : initial); const [category, setCategory] = useState(initialCategory); const [search, setSearch] = useState('');
  const dirty = JSON.stringify(draft) !== JSON.stringify(initial);
  useEffect(() => setCategory(initialCategory), [initialCategory]);
  const discard = useCallback(() => setDraft(initial), [initial]);
  useUnsavedChangesGuard(dirty, discard, 'Your application settings have not been saved.');
  const visible = useMemo(() => categories.filter(([name]) => name.toLowerCase().includes(search.toLowerCase())), [search]);
  const update = <K extends keyof typeof draft>(key: K, value: (typeof draft)[K]) => setDraft(current => ({ ...current, [key]: value }));
  return <div className={page.pageWrap}><header className={page.pageHeader}><div><h1>Settings</h1><p>Application preferences. Settings for a specific server stay in its workspace.</p></div></header><div className={styles.settingsLayout}><div className={styles.settingsNavColumn}><SearchInput value={search} onChange={event => setSearch(event.target.value)} placeholder="Search settings" aria-label="Search settings" /><nav className={styles.settingsNav} aria-label="Settings categories">{visible.map(([name, Icon]) => <button key={name} data-selected={category === name} onClick={() => setCategory(name)}><Icon size={14} />{name}</button>)}</nav></div><div><section className={styles.settingsForm}>
    {category === 'General' && <><Row label="Minimize to notification area" detail="Keep ChunkPilot and managed servers running when the window is minimized."><Toggle value={draft.minimizeToTray} onChange={value => update('minimizeToTray', value)} /></Row><Row label="Start minimized" detail="Open ChunkPilot in the notification area when Windows starts it."><Toggle value={draft.startMinimized} onChange={value => update('startMinimized', value)} /></Row><Row label="Start with Windows" detail="Register the current ChunkPilot application for this Windows user."><Toggle value={draft.startWithWindows} onChange={value => update('startWithWindows', value)} /></Row></>}
    {category === 'General' && <Row label="CurseForge access" detail={applicationService ? 'This build is configured to use the ChunkPilot CurseForge service. No personal setup is needed; availability is checked when you use a CurseForge feature.' : 'Connect your own approved API access in a native password window. The key is encrypted for this Windows user; setup also lets you replace or remove it.'}>{applicationService ? <TextInput value="No API key required" aria-label="CurseForge access" readOnly /> : <CurseForgeSetupButton />}</Row>}
    {category === 'Appearance' && <Row label="Reduced motion" detail="Remove non-essential interface transitions. Windows preferences are also respected."><Toggle value={draft.reducedMotion} onChange={value => update('reducedMotion', value)} /></Row>}
    {category === 'Privacy & diagnostics' && <><Row label="Privacy" detail="ChunkPilot has no analytics, advertising, behavioral tracking, telemetry, or automatic diagnostic upload. Selected provider and download workflows use the Internet, and Players can request official Mojang heads."><TextInput value="Local-first" readOnly /></Row><Row label="CurseForge requests" detail={applicationService ? 'Your selected searches, project/file IDs, artwork, and download requests pass through the ChunkPilot service to CurseForge over HTTPS. The service can see these requests and your public IP address. Your server files and worlds are never uploaded.' : 'CurseForge is contacted only when you request discovery, metadata, downloads, updates, or a provider refresh. Search terms and project/file IDs reach CurseForge over HTTPS; your server files and worlds are never uploaded there.'}><TextInput value="Only when requested" readOnly /></Row><Row label="Diagnostic data" detail="Recognized credentials are redacted and bundles stay local. Player names, IP addresses, UUIDs, and file paths can remain useful evidence, so review a bundle before sharing it."><TextInput value="Stored locally" readOnly /></Row></>}
    {category === 'Help & troubleshooting' && <HelpCenter initialArticleId={initialHelpArticleId} onDeepLink={onHelpDeepLink} />}
    {category === 'About' && <><Row label="ChunkPilot" detail="Local-first native Windows server manager."><TextInput value={snapshot.build.productVersion} readOnly /></Row><Row label="Build" detail={`${snapshot.build.releaseTag} · schema ${snapshot.build.schemaVersion} · ${snapshot.build.architecture}`}><TextInput value={snapshot.build.gitSha} readOnly /></Row><Row label="Built" detail={`Default interface: ${snapshot.build.defaultUi}`}><TextInput value={snapshot.build.buildTimestampUtc} readOnly /></Row><Row label="WebView2 Runtime" detail="The Evergreen Runtime supplies the renderer. Application assets are bundled locally."><TextInput value="Detected by the native host" readOnly /></Row></>}
  </section>{dirty && <div className={styles.sticky} role="status"><span>Unsaved application settings</span><div className={page.actions}><Button onClick={discard}>Discard</Button><Button variant="primary" onClick={() => void command('settings.saveGlobal', { ...draft })}>Save changes</Button></div></div>}</div></div></div>;
}

function Row({ label, detail, children }: { label: string; detail: string; children: ReactNode }) { return <div className={styles.settingRow}><div><strong>{label}</strong><p>{detail}</p></div><div>{children}</div></div>; }
function Toggle({ value, onChange }: { value: boolean; onChange: (value: boolean) => void }) { return <button className={page.toggle} data-checked={value} role="switch" aria-checked={value} onClick={() => onChange(!value)} />; }
