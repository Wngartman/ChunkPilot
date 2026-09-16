import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react';
import { CircleHelp, Info, Monitor, Shield, SunMoon } from '../design-system/Icons';
import { Button, SearchInput, Switch, TextInput } from '../design-system/Primitives';
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
  return <div className={page.pageWrap}><header className={page.pageHeader}><div><h1>Settings</h1></div></header><div className={styles.settingsLayout}><div className={styles.settingsNavColumn}><SearchInput value={search} onChange={event => setSearch(event.target.value)} placeholder="Search settings" aria-label="Search settings" /><nav className={styles.settingsNav} aria-label="Settings categories">{visible.map(([name, Icon]) => <button key={name} data-selected={category === name} aria-current={category === name ? 'page' : undefined} onClick={() => setCategory(name)}><Icon size={14} />{name}</button>)}</nav>{visible.length === 0 && <p className={styles.settingsNoResults} role="status">No matching categories</p>}</div><div><h2 className={styles.settingsHeading}>{category}</h2><section className={styles.settingsForm}>
    {category === 'General' && <><Row label="Minimize to notification area" detail="Keep servers running while minimized."><Toggle label="Minimize to notification area" value={draft.minimizeToTray} onChange={value => update('minimizeToTray', value)} /></Row><Row label="Start minimized" detail="Open in the notification area."><Toggle label="Start minimized" value={draft.startMinimized} onChange={value => update('startMinimized', value)} /></Row><Row label="Start with Windows" detail="Open ChunkPilot when you sign in."><Toggle label="Start with Windows" value={draft.startWithWindows} onChange={value => update('startWithWindows', value)} /></Row></>}
    {category === 'General' && <Row label="CurseForge access" detail={applicationService ? 'This build is configured to use the ChunkPilot CurseForge service. No personal setup is needed; availability is checked when you use a CurseForge feature.' : 'Add, replace, or remove your approved API key. Stored securely for this Windows user.'}>{applicationService ? <TextInput value="No API key required" aria-label="CurseForge access" readOnly /> : <CurseForgeSetupButton />}</Row>}
    {category === 'Appearance' && <Row label="Reduced motion" detail="Reduce animations. Also follows your Windows preference."><Toggle label="Reduced motion" value={draft.reducedMotion} onChange={value => update('reducedMotion', value)} /></Row>}
    {category === 'Privacy & diagnostics' && <><Row label="Privacy" detail="ChunkPilot has no analytics, advertising, behavioral tracking, telemetry, or automatic diagnostic upload. Selected provider and download workflows use the Internet, and Players can request official Mojang heads."><TextInput value="Local-first" readOnly /></Row><Row label="CurseForge requests" detail={applicationService ? 'Your selected searches, project/file IDs, artwork, and download requests pass through the ChunkPilot service to CurseForge over HTTPS. The service can see these requests and your public IP address. Your server files and worlds are never uploaded.' : 'CurseForge is contacted only when you request discovery, metadata, downloads, updates, or a provider refresh. Search terms and project/file IDs reach CurseForge over HTTPS; your server files and worlds are never uploaded there.'}><TextInput value="Only when requested" readOnly /></Row><Row label="Diagnostic data" detail="Recognized credentials are redacted and bundles stay local. Player names, IP addresses, UUIDs, and file paths can remain useful evidence, so review a bundle before sharing it."><TextInput value="Stored locally" readOnly /></Row></>}
    {category === 'Help & troubleshooting' && <HelpCenter initialArticleId={initialHelpArticleId} onDeepLink={onHelpDeepLink} />}
    {category === 'About' && <><Row label="ChunkPilot" detail="Local-first native Windows server manager."><TextInput value={snapshot.build.productVersion} readOnly /></Row><Row label="Build" detail={`${snapshot.build.releaseTag} · schema ${snapshot.build.schemaVersion} · ${snapshot.build.architecture}`}><TextInput value={snapshot.build.gitSha} readOnly /></Row><Row label="Built" detail={`Default interface: ${snapshot.build.defaultUi}`}><TextInput value={snapshot.build.buildTimestampUtc} readOnly /></Row><Row label="WebView2 Runtime" detail="The Evergreen Runtime supplies the renderer. Application assets are bundled locally."><TextInput value="Detected by the native host" readOnly /></Row></>}
  </section>{dirty && <div className={styles.sticky} role="status"><span>Unsaved application settings</span><div className={page.actions}><Button onClick={discard}>Discard</Button><Button variant="primary" onClick={() => void command('settings.saveGlobal', { ...draft })}>Save changes</Button></div></div>}</div></div></div>;
}

function Row({ label, detail, children }: { label: string; detail: string; children: ReactNode }) { return <div className={styles.settingRow}><div><strong>{label}</strong><p>{detail}</p></div><div>{children}</div></div>; }
function Toggle({ label, value, onChange }: { label: string; value: boolean; onChange: (value: boolean) => void }) { return <Switch label={label} checked={value} onClick={() => onChange(!value)} />; }
