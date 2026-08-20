import { describe, expect, it } from 'vitest';
import { normalizedCropRect } from './iconCrop';

describe('server icon crop geometry', () => {
  it('centers a square crop and respects zoom and pan', () => {
    expect(normalizedCropRect(800, 400, 1, 0, 0, 0)).toEqual({ x: 200, y: 0, size: 400, width: 800, height: 400 });
    expect(normalizedCropRect(800, 400, 2, 1, -1, 0)).toEqual({ x: 600, y: 0, size: 200, width: 800, height: 400 });
  });

  it('uses rotated dimensions and clamps untrusted controls', () => {
    expect(normalizedCropRect(800, 400, 99, 9, -9, 90)).toEqual({ x: 350, y: 0, size: 50, width: 400, height: 800 });
  });

  it('rejects an invalid source', () => {
    expect(() => normalizedCropRect(0, 64, 1, 0, 0, 0)).toThrow('contain pixels');
  });
});
