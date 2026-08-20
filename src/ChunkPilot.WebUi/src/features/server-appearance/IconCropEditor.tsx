import { useEffect, useRef, useState } from 'react';
import { Image as ImageIcon, RotateCcw, RotateCw, ZoomIn } from '../../design-system/Icons';
import { Button } from '../../design-system/Primitives';
import { useAppStore } from '../../state/store';
import { normalizedCropRect } from './iconCrop';
import styles from './ServerAppearance.module.css';

interface IconSourceResult { cancelled: boolean; sourceUrl?: string; width?: number; height?: number; fileName?: string; }

function drawCrop(canvas: HTMLCanvasElement, image: HTMLImageElement, zoom: number, panX: number, panY: number, rotation: number, outputSize: number) {
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
  output.clearRect(0, 0, outputSize, outputSize);
  output.drawImage(rotated, crop.x, crop.y, crop.size, crop.size, 0, 0, outputSize, outputSize);
}

export function IconCropEditor({ serverName, savedIconUrl, stagedIconUrl, onStagedIcon }: {
  serverName: string;
  savedIconUrl: string | null;
  stagedIconUrl: string | null;
  onStagedIcon: (value: string | null) => void;
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
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');

  const redraw = () => {
    if (canvas.current && image.current) drawCrop(canvas.current, image.current, zoom, panX, panY, rotation, 384);
  };
  useEffect(redraw, [zoom, panX, panY, rotation, sourceUrl]);

  const choose = async () => {
    setLoading(true); setError('');
    try {
      const selected = await command<IconSourceResult>('appearance.chooseIcon');
      if (selected.cancelled || !selected.sourceUrl) return;
      const next = new window.Image();
      next.onload = () => { image.current = next; setSourceUrl(selected.sourceUrl!); setFileName(selected.fileName ?? 'Selected image'); setZoom(1); setPanX(0); setPanY(0); setRotation(0); };
      next.onerror = () => setError('The selected image preview could not be decoded.');
      next.src = selected.sourceUrl;
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'The image could not be opened.');
    } finally { setLoading(false); }
  };
  const fixtureAutoOpened = useRef(false);
  useEffect(() => {
    const query = new URLSearchParams(window.location.search);
    if (fixtureAutoOpened.current || !query.has('fixture') || query.get('mode') !== 'icon-editor') return;
    fixtureAutoOpened.current = true;
    void choose();
  }, []);

  const apply = () => {
    if (!image.current) return;
    const output = document.createElement('canvas');
    drawCrop(output, image.current, zoom, panX, panY, rotation, 64);
    onStagedIcon(output.toDataURL('image/png'));
    setSourceUrl(null);
    image.current = null;
  };
  const reset = () => { setZoom(1); setPanX(0); setPanY(0); setRotation(0); };
  const current = stagedIconUrl ?? savedIconUrl;

  return <div className={styles.iconEditor}>
    <div className={styles.currentIcon}>
      {current ? <img src={current} alt={`${serverName} server icon`} /> : <div className={styles.iconFallback}><ImageIcon size={24} aria-hidden="true" /></div>}
      <div><strong>Current server icon</strong><span>{stagedIconUrl ? 'New crop ready to save' : savedIconUrl ? 'Saved 64 × 64 PNG' : 'Minecraft default icon'}</span></div>
      <Button onClick={choose} disabled={loading}>{loading ? 'Opening…' : current ? 'Replace image' : 'Choose image'}</Button>
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
        <p className={styles.cropHint}>Drag to frame the square. The saved result is exactly 64 × 64 PNG.</p>
      </div>
      <div className={styles.cropControls}>
        <strong>{fileName}</strong>
        <label><span><ZoomIn size={14} aria-hidden="true" /> Zoom</span><input type="range" min="1" max="8" step="0.05" value={zoom} onChange={event => setZoom(Number(event.target.value))} /></label>
        <div className={styles.rotationControls}><Button icon={<RotateCcw size={14} />} onClick={() => setRotation(value => value - 90)}>Rotate left</Button><Button icon={<RotateCw size={14} />} onClick={() => setRotation(value => value + 90)}>Rotate right</Button></div>
        <Button variant="subtle" onClick={reset}>Reset crop</Button>
        <div className={styles.sizePreviews} aria-label="Icon size previews">{[64, 32, 16].map(size => <div key={size}><canvas ref={node => { if (node && image.current) drawCrop(node, image.current, zoom, panX, panY, rotation, size); }} width={size} height={size} /><span>{size}px</span></div>)}</div>
        <div className={styles.editorActions}><Button onClick={() => { setSourceUrl(null); image.current = null; }}>Cancel</Button><Button variant="primary" onClick={apply}>Use this crop</Button></div>
      </div>
    </div>}
    {error && <p className={styles.inlineError} role="alert">{error}</p>}
  </div>;
}
