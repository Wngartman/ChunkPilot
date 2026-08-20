import { useEffect, useMemo, useState } from 'react';
import type { PluginConfigFile, PluginInventoryEntry, TextFileContent } from '../../bridge/types';
import { Button, Dialog, EmptyState, SelectInput, StatusBadge, TextInput } from '../../design-system/Primitives';
import { useAppStore } from '../../state/store';
import styles from './PluginConfigEditor.module.css';

type SimpleField = { key: string; value: string; line: number; separator: ':' | '=' };

function parseSimple(content: string, format: PluginConfigFile['format']): SimpleField[] | null {
  if (format === 'json' || format === 'jsonc') return null;
  const fields: SimpleField[] = [];
  const lines = content.split(/\r?\n/);
  for (let line = 0; line < lines.length; line += 1) {
    const value = lines[line];
    if (/^\s*(?:#|;|\/\/|\[|$)/.test(value)) continue;
    const match = /^(\s*)([A-Za-z0-9_.-]+)(\s*)([:=])(\s*)(.*)$/.exec(value);
    if (!match) continue;
    fields.push({ key: match[2], value: match[6], line, separator: match[4] as ':' | '=' });
  }
  return fields.length ? fields : null;
}

function replaceField(content: string, field: SimpleField, next: string): string {
  const ending = content.includes('\r\n') ? '\r\n' : '\n';
  const lines = content.split(/\r?\n/);
  const line = lines[field.line] ?? '';
  const match = /^(\s*[A-Za-z0-9_.-]+\s*[:=]\s*)(.*)$/.exec(line);
  if (!match) return content;
  lines[field.line] = `${match[1]}${next}`;
  return lines.join(ending);
}

export function PluginConfigEditor({ open, onClose, serverId, serverRunning, plugin, kind = 'plugins' }: {
  open: boolean;
  onClose: () => void;
  serverId: string;
  serverRunning: boolean;
  plugin: PluginInventoryEntry | null;
  kind?: 'plugins' | 'mods';
}) {
  const command = useAppStore(state => state.command);
  const [files, setFiles] = useState<PluginConfigFile[]>([]);
  const [selectedPath, setSelectedPath] = useState('');
  const [loaded, setLoaded] = useState<TextFileContent | null>(null);
  const [draft, setDraft] = useState('');
  const [mode, setMode] = useState<'simple' | 'raw'>('simple');
  const [status, setStatus] = useState('');
  const [error, setError] = useState('');
  const selected = files.find(file => file.relativePath === selectedPath) ?? null;
  const fields = useMemo(() => selected ? parseSimple(draft, selected.format) : null, [draft, selected]);

  useEffect(() => {
    if (!open || !plugin) return;
    setFiles([]); setSelectedPath(''); setLoaded(null); setDraft(''); setStatus(''); setError('');
    void command<PluginConfigFile[]>(kind === 'mods' ? 'mods.configFiles' : 'plugins.configFiles', { serverId, relativePath: plugin.relativePath })
      .then(value => { setFiles(value); if (value.length) setSelectedPath(value[0].relativePath); })
      .catch(reason => setError(reason instanceof Error ? reason.message : 'Add-on configuration is unavailable.'));
  }, [open, plugin?.relativePath, serverId, command, kind]);

  useEffect(() => {
    if (!open || !selectedPath) return;
    setLoaded(null); setDraft(''); setStatus(''); setError('');
    void command<TextFileContent>('files.read', { serverId, relativePath: selectedPath })
      .then(value => { setLoaded(value); setDraft(value.content); })
      .catch(reason => setError(reason instanceof Error ? reason.message : 'The configuration file could not be read.'));
  }, [open, selectedPath, serverId, command]);

  const dirty = Boolean(loaded && draft !== loaded.content);
  const save = async () => {
    if (!loaded || !dirty || !plugin) return;
    setStatus(''); setError('');
    try {
      await command(kind === 'mods' ? 'mods.saveConfig' : 'plugins.saveConfig', {
        serverId,
        addonRelativePath: plugin.relativePath,
        file: { ...loaded, content: draft },
        restartIfRunning: serverRunning
      });
      const refreshed = await command<TextFileContent>('files.read', { serverId, relativePath: loaded.relativePath });
      setLoaded(refreshed); setDraft(refreshed.content); setStatus(serverRunning
        ? 'Saved atomically with a recovery copy and safely restarted the server.'
        : 'Saved atomically with a recovery copy. The change applies at the next server start.');
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'The configuration file was not saved.');
    }
  };

  return <Dialog open={open} title={plugin ? `${plugin.name} configuration` : 'Add-on configuration'} wide onClose={onClose} footer={<><Button onClick={onClose}>Close</Button><Button variant="primary" disabled={!dirty} onClick={() => void save()}>{serverRunning ? 'Save and restart' : 'Save configuration'}</Button></>}>
    <div className={styles.editor}>
      <header><div><p>Only bounded text files in exact identity-based configuration paths are shown. Unknown ownership is never guessed or recursively scanned.</p></div><StatusBadge tone={serverRunning ? 'warning' : 'success'}>{serverRunning ? 'Restart required' : 'Safe to edit'}</StatusBadge></header>
      {files.length ? <>
        <div className={styles.toolbar}><SelectInput aria-label="Plugin configuration file" value={selectedPath} onChange={event => setSelectedPath(event.target.value)}>{files.map(file => <option key={file.relativePath} value={file.relativePath}>{file.relativePath}</option>)}</SelectInput><div role="group" aria-label="Editor mode"><Button variant={mode === 'simple' ? 'primary' : 'subtle'} disabled={!fields} onClick={() => setMode('simple')}>Simple</Button><Button variant={mode === 'raw' ? 'primary' : 'subtle'} onClick={() => setMode('raw')}>Raw</Button></div></div>
        {error && <p className={styles.error} role="alert">{error}</p>}{status && <p className={styles.status} role="status">{status}</p>}
        {!loaded ? <EmptyState title="Loading configuration" detail="ChunkPilot is reading the selected file through the authoritative Agent." /> : mode === 'simple' && fields ? <div className={styles.fields}>{fields.map(field => <label key={`${field.line}-${field.key}`}><span><strong>{field.key}</strong><small>{field.separator === ':' ? 'YAML-style value' : 'Property value'} · comments and ordering are preserved</small></span><TextInput value={field.value} onChange={event => setDraft(value => replaceField(value, field, event.target.value))} /></label>)}</div> : <div className={styles.raw}><textarea aria-label={`Raw configuration for ${selected?.name ?? plugin?.name ?? 'plugin'}`} value={draft} onChange={event => setDraft(event.target.value)} spellCheck={false} /><p>{selected?.format === 'json' || selected?.format === 'jsonc' ? 'JSON and JSONC stay in Raw mode so comments, ordering, and unknown structures are not destructively rewritten.' : 'Raw mode preserves the exact text, encoding metadata, line endings, and optimistic-concurrency hash.'}</p></div>}
        {dirty && <div className={styles.dirty} role="status"><span>Unsaved configuration changes</span><Button disabled={!loaded} onClick={() => loaded && setDraft(loaded.content)}>Discard</Button></div>}
      </> : error ? <EmptyState title="Configuration unavailable" detail={error} /> : <EmptyState title="No known configuration files" detail={`This ${kind === 'mods' ? 'mod' : 'plugin'} has no supported configuration file in an exact identity-based path. Use the folder action for ownership-uncertain or nested files.`} />}
    </div>
  </Dialog>;
}
