import { useEffect, useRef, useState } from 'react';
import { Image as ImageIcon, RotateCcw, RotateCw, ZoomIn } from '../../design-system/Icons';
import { Button } from '../../design-system/Primitives';
import { useAppStore } from '../../state/store';
import { defaultIconRecipe, normalizedCropRect, type IconEdit, type IconRecipe } from './iconCrop';
import { isFixtureMode } from '../../fixtures/mode';
import styles from './ServerAppearance.module.css';

interface IconSourceResult { cancelled?: boolean; token?: string; sourceUrl?: string; width?: number; height?: number; fileName?: string; recipe?: IconRecipe; originalAvailable?: boolean; detail?: string; }

function drawCrop(canvas: HTMLCanvasElement, image: HTMLImageElement, zoom: number, panX: number, panY: number, rotation: number, outputSize: number, brightness = 1, contrast = 1, saturation = 1) {
  const turns = ((Math.round(rotation / 90) % 4) + 4) % 4;
  const rotated = document.createElement('canvas');
  rotated.width = turns % 2 === 0 ? image.naturalWidth : image.naturalHeight;
  rotated.height = turns % 2 === 0 ? image.naturalHeight : image.naturalWidth;
  const context = rotated.getContext('2d', { alpha: true });
  if (!context) throw new Error('The icon preview could not be rendered.');
  context.translate(rotated.width / 2, rotated.height / 2);
  context.rotate(turns * Math.PI / 2);
  context.drawImage(image, -image.naturalWidth / 2, -image.naturalHeight / 2);
  const crop = normalizedCropRect(image.naturalWidth, image.naturalHeight, zoom, panX, panY, rotation);
  canvas.width = outputSize;
  canvas.height = outputSize;
  const output = canvas.getContext('2d', { alpha: true });
  if (!output) throw new Error('The icon preview could not be rendered.');
  output.imageSmoothingEnabled = false;
  output.filter = `brightness(${brightness}) contrast(${contrast}) saturate(${saturation})`;
  output.clearRect(0, 0, outputSize, outputSize);
  output.drawImage(rotated, crop.x, crop.y, crop.size, crop.size, 0, 0, outputSize, outputSize);
}

export function IconCropEditor({ serverId, serverName, savedIconUrl, stagedIconUrl, onStagedIcon, resetToken = 0 }: {
  serverId: string;
  serverName: string;
  savedIconUrl: string | null;
  stagedIconUrl: string | null;
  onStagedIcon: (value: string | null, edit?: IconEdit | null) => void;
  resetToken?: number;
}) {
  const command = useAppStore(state => state.command);
  const canvas = useRef<HTMLCanvasElement>(null);
  const image = useRef<HTMLImageElement | null>(null);
  const drag = useRef<{ x: number; y: number; panX: number; panY: number } | null>(null);
  const [sourceUrl, setSourceUrl] = useState<string | null>(null);
  const [fileName, setFileName] = useState('');
  const [zoom, setZoom] = useState(1);
  const [panX, setPanX] = useState(0);
  const [panY, setPanY] = useState(0);
  const [rotation, setRotation] = useState(0);
  const [brightness, setBrightness] = useState(1);
  const [contrast, setContrast] = useState(1);
  const [saturation, setSaturation] = useState(1);
  const [sourceDetail, setSourceDetail] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const requestGeneration = useRef(0);
  const activeSource = useRef<IconSourceResult | null>(null);
  const appliedSource = useRef<IconSourceResult | null>(null);
  useEffect(() => {
    requestGeneration.current += 1;
    setSourceUrl(null); image.current = null; activeSource.current = null; appliedSource.current = null;
    setLoading(false); setError('');
    return () => { requestGeneration.current += 1; };
  }, [serverId, resetToken]);

  const redraw = () => {
    if (canvas.current && image.current) drawCrop(canvas.current, image.current, zoom, panX, panY, rotation, 384, brightness, contrast, saturation);
  };
  useEffect(redraw, [zoom, panX, panY, rotation, sourceUrl, brightness, contrast, saturation]);

  const choose = async (existing = false) => {
    const generation = ++requestGeneration.current;
    setLoading(true); setError('');
    try {
      const selected = existing && stagedIconUrl && appliedSource.current ? appliedSource.current :
        await command<IconSourceResult>(existing ? 'appearance.editIcon' : 'appearance.chooseIcon', { serverId });
      if (generation !== requestGeneration.current) return;
      if (selected.cancelled || !selected.sourceUrl) return;
      const next = new window.Image();
      next.onload = () => {
        if (generation !== requestGeneration.current) return;
        image.current = next; activeSource.current = selected; setSourceUrl(selected.sourceUrl!);
        setFileName(selected.fileName ?? 'Selected image'); setSourceDetail(selected.detail ?? 'Edits are staged until you save.');
        const recipe = selected.recipe ?? defaultIconRecipe;
        setZoom(recipe.zoom); setPanX(recipe.panX); setPanY(recipe.panY); setRotation(recipe.rotation);
        setBrightness(recipe.brightness); setContrast(recipe.contrast); setSaturation(recipe.saturation);
        setLoading(false);
      };
      next.onerror = () => { if (generation === requestGeneration.current) { setError('The selected image preview could not be decoded.'); setLoading(false); } };
      next.src = selected.sourceUrl;
    } catch (reason) {
      if (generation === requestGeneration.current) setError(reason instanceof Error ? reason.message : 'The image could not be opened.');
    } finally { if (generation === requestGeneration.current) setLoading(false); }
  };
  const fixtureAutoOpened = useRef(false);
  useEffect(() => {
    const query = new URLSearchParams(window.location.search);
    if (fixtureAutoOpened.current || !isFixtureMode() || query.get('mode') !== 'icon-editor') return;
    fixtureAutoOpened.current = true;
    void choose();
  }, []);

  const apply = () => {
    if (!image.current) return;
    const output = document.createElement('canvas');
    drawCrop(output, image.current, zoom, panX, panY, rotation, 64, brightness, contrast, saturation);
    const recipe = { zoom, panX, panY, rotation, brightness, contrast, saturation };
    const selected = activeSource.current;
    appliedSource.current = selected ? { ...selected, recipe } : null;
    onStagedIcon(output.toDataURL('image/png'), selected?.token ? { token: selected.token, recipe } : null);
    setSourceUrl(null);
    image.current = null;
  };
  const reset = () => { setZoom(1); setPanX(0); setPanY(0); setRotation(0); setBrightness(1); setContrast(1); setSaturation(1); };
  const current = stagedIconUrl ?? savedIconUrl;

  return <div className={styles.iconEditor}>
    <div className={styles.currentIcon}>
      {current ? <img src={current} alt={`${serverName} server icon`} /> : <div className={styles.iconFallback}><ImageIcon size={24} aria-hidden="true" /></div>}
      <div><strong>Current server icon</strong><span>{stagedIconUrl ? 'New crop ready to save' : savedIconUrl ? 'Saved 64 × 64 PNG' : 'Minecraft default icon'}</span></div>
      <div className={styles.editorActions}>{current && <Button onClick={() => void choose(true)} disabled={loading}>Edit server icon</Button>}<Button onClick={() => void choose()} disabled={loading}>{loading ? 'Opening…' : current ? 'Replace image' : 'Choose image'}</Button></div>
    </div>
    {sourceUrl && <div className={styles.cropWorkbench}>
      <div>
        <canvas
          ref={canvas}
          className={styles.cropCanvas}
          role="img"
          tabIndex={0}
          aria-label="Server icon crop. Drag or use arrow keys to reposition. Plus and minus change zoom."
          onPointerDown={event => { drag.current = { x: event.clientX, y: event.clientY, panX, panY }; event.currentTarget.setPointerCapture(event.pointerId); }}
          onPointerMove={event => { if (!drag.current) return; setPanX(Math.max(-1, Math.min(1, drag.current.panX - (event.clientX - drag.current.x) / 160))); setPanY(Math.max(-1, Math.min(1, drag.current.panY - (event.clientY - drag.current.y) / 160))); }}
          onPointerUp={event => { drag.current = null; event.currentTarget.releasePointerCapture(event.pointerId); }}
          onPointerCancel={() => { drag.current = null; }}
          onLostPointerCapture={() => { drag.current = null; }}
          onKeyDown={event => {
            const step = event.shiftKey ? .12 : .035;
            if (event.key === 'ArrowLeft') setPanX(value => Math.max(-1, value - step));
            else if (event.key === 'ArrowRight') setPanX(value => Math.min(1, value + step));
            else if (event.key === 'ArrowUp') setPanY(value => Math.max(-1, value - step));
            else if (event.key === 'ArrowDown') setPanY(value => Math.min(1, value + step));
            else if (event.key === '+' || event.key === '=') setZoom(value => Math.min(8, value + .25));
            else if (event.key === '-') setZoom(value => Math.max(1, value - .25));
            else if (event.key === 'Home') reset(); else return;
            event.preventDefault();
          }}
        />
        <p className={styles.cropHint}>Drag to frame the square. Save renders the final 64 × 64 PNG natively. {sourceDetail}</p>
      </div>
      <div className={styles.cropControls}>
        <strong>{fileName}</strong>
        <label><span><ZoomIn size={14} aria-hidden="true" /> Zoom</span><input type="range" min="1" max="8" step="0.05" value={zoom} onChange={event => setZoom(Number(event.target.value))} /></label>
        <label><span>Brightness</span><input type="range" min="0.5" max="1.5" step="0.05" value={brightness} onChange={event => setBrightness(Number(event.target.value))} /></label>
        <label><span>Contrast</span><input type="range" min="0.5" max="1.5" step="0.05" value={contrast} onChange={event => setContrast(Number(event.target.value))} /></label>
        <label><span>Saturation</span><input type="range" min="0" max="2" step="0.05" value={saturation} onChange={event => setSaturation(Number(event.target.value))} /></label>
        <div className={styles.rotationControls}><Button icon={<RotateCcw size={14} />} onClick={() => setRotation(value => value - 90)}>Rotate left</Button><Button icon={<RotateCw size={14} />} onClick={() => setRotation(value => value + 90)}>Rotate right</Button></div>
        <Button variant="subtle" onClick={reset}>Reset crop and adjustments</Button>
        <div className={styles.sizePreviews} aria-label="Icon size previews">{[64, 32, 16].map(size => <div key={size}><canvas ref={node => { if (node && image.current) drawCrop(node, image.current, zoom, panX, panY, rotation, size, brightness, contrast, saturation); }} width={size} height={size} /><span>{size}px</span></div>)}</div>
        <div className={styles.editorActions}><Button onClick={() => { setSourceUrl(null); image.current = null; }}>Cancel</Button><Button variant="primary" onClick={apply}>Use this crop</Button></div>
      </div>
    </div>}
    {error && <p className={styles.inlineError} role="alert">{error}</p>}
  </div>;
}
