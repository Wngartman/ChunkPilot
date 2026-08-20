import { describe, expect, it } from 'vitest';
import { formatMemory, hostMemoryWarning, memoryPresets, parseMemory } from './memory';

describe('memory input', () => {
  it('keeps every standard preset exact', () => {
    for (const preset of memoryPresets) expect(parseMemory(formatMemory(preset)).mebibytes).toBe(preset);
  });

  it.each([['4096', 4096], ['4096 MB', 4096], ['4 GB', 4096], ['6.5 GB', 6656], ['768 MiB', 768]])(
    'normalizes %s to the existing MiB authority', (input, expected) => {
      const result = parseMemory(input); expect(result.valid).toBe(true); expect(result.mebibytes).toBe(expected);
    });

  it.each(['', 'lots', '-1 GB', '0', '0.1 GB', '129 GB', '1e4'])('rejects invalid or unsafe input %s', input => {
    expect(parseMemory(input).valid).toBe(false);
  });

  it('warns instead of silently clamping against host memory', () => {
    expect(hostMemoryWarning(14 * 1024, 16 * 1024 ** 3)).toContain('75%');
    expect(hostMemoryWarning(4 * 1024, 16 * 1024 ** 3)).toBeNull();
  });
});
