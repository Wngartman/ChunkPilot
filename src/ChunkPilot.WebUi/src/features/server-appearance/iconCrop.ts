export interface CropRect { x: number; y: number; size: number; width: number; height: number; }

export function normalizedCropRect(width: number, height: number, zoom: number, panX: number, panY: number, rotation: number): CropRect {
  if (!Number.isFinite(width) || !Number.isFinite(height) || width < 1 || height < 1)
    throw new RangeError('An icon source must contain pixels.');
  const turns = ((Math.round(rotation / 90) % 4) + 4) % 4;
  const rotatedWidth = turns % 2 === 0 ? width : height;
  const rotatedHeight = turns % 2 === 0 ? height : width;
  const safeZoom = Math.min(8, Math.max(1, zoom));
  const size = Math.min(rotatedWidth, rotatedHeight) / safeZoom;
  const availableX = Math.max(0, rotatedWidth - size);
  const availableY = Math.max(0, rotatedHeight - size);
  const x = ((Math.min(1, Math.max(-1, panX)) + 1) / 2) * availableX;
  const y = ((Math.min(1, Math.max(-1, panY)) + 1) / 2) * availableY;
  return { x, y, size, width: rotatedWidth, height: rotatedHeight };
}
