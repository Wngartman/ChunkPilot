import { describe, expect, it } from 'vitest';
import { formatGigabytes, formatMemory, hostMemoryWarning, memoryPresets, parseMemory } from './memory';

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

  it.each([['1,5 GB', 'de-DE', 1536], ['4,6 GB', 'fr-FR', 4710], ['4.6 GB', 'en-US', 4710],
    ['1.024 MB', 'de-DE', 1024], ['1,024 MB', 'en-US', 1024], ['1\u202f024 MB', 'fr-FR', 1024]])(
    'reads %s using the %s decimal and grouping policy', (input, locale, expected) => {
      expect(parseMemory(input, 512, 24576, locale).mebibytes).toBe(expected);
    });

  it.each([['1,5 GB', 'en-US'], ['4,6 GB', 'en-US'], ['1,024 GB', 'en-US'],
    ['4.6 GB', 'de-DE'], ['1,2,3 GB', 'de-DE'], ['1.2,3 GB', 'de-DE'], ['1,02 MB', 'en-US']])(
    'never removes ambiguous separators from %s in %s', (input, locale) => {
      expect(parseMemory(input, 512, 24576, locale).valid).toBe(false);
    });

  it.each([512, 513, 1025, 3217, 4711, 10001, 24575])('round-trips exact custom %s MiB without rounding drift', value => {
    for (const locale of ['en-US', 'de-DE', 'fr-FR'])
      expect(parseMemory(`${formatGigabytes(value, locale)} GB`, 512, 24576, locale).mebibytes).toBe(value);
  });
});
