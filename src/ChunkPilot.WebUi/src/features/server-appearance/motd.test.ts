import { describe, expect, it } from 'vitest';
import { applyFormatting, clearFormatting, deleteBackwardRange, deleteForwardRange, motdText, parseMotd, plainMotd, replaceSelection, serializeMotd, validateMotd } from './motd';

describe('Vanilla MOTD formatting', () => {
  it('round-trips supported formatting and two lines semantically', () => {
    const raw = '§a§lCopper Valley§r\n§7Build together §b今日';
    const parsed = parseMotd(raw);
    expect(parsed.visualSafe).toBe(true);
    expect(plainMotd(serializeMotd(parsed.runs))).toBe('Copper Valley\nBuild together 今日');
    expect(parseMotd(serializeMotd(parsed.runs)).runs).toEqual(parsed.runs);
  });

  it('preserves unknown input by requiring raw mode', () => {
    const parsed = parseMotd('Keep §x§1§2§3§4§5§6 this');
    expect(parsed.visualSafe).toBe(false);
    expect(parsed.reason).toContain('Raw mode preserves it exactly');
  });

  it('validates the two-line and length contracts without rejecting Unicode', () => {
    expect(validateMotd('One\nTwo')).toBeNull();
    expect(validateMotd('世界へようこそ')).toBeNull();
    expect(validateMotd('One\nTwo\nThree')).toContain('two');
    expect(validateMotd('x'.repeat(257))).toContain('257');
  });

  it('formats only the stable semantic selection across style and line boundaries', () => {
    const parsed = parseMotd('Copper §aValley\nBuild');
    const formatted = applyFormatting(parsed.runs, { anchor: 3, focus: 15 }, { bold: true, underline: true });
    expect(motdText(formatted)).toBe('Copper Valley\nBuild');
    const roundTrip = parseMotd(serializeMotd(formatted));
    expect(roundTrip.runs).toEqual(formatted);
    expect(roundTrip.runs.some(run => run.text.includes('\n') && run.bold)).toBe(true);
  });

  it('clears formatting without changing text or adjacent formatting', () => {
    const parsed = parseMotd('§aGreen §lBold§r plain');
    const cleared = clearFormatting(parsed.runs, { anchor: 6, focus: 10 });
    expect(motdText(cleared)).toBe('Green Bold plain');
    const boldOffset = motdText(cleared).indexOf('Bold');
    let cursor = 0; const containing = cleared.find(run => { const contains = boldOffset >= cursor && boldOffset < cursor + run.text.length; cursor += run.text.length; return contains; });
    expect(containing?.bold).toBe(false);
    expect(cleared[0].color).toBe('a');
  });

  it('replaces text while preserving Unicode and the insertion style', () => {
    const parsed = parseMotd('§bHello 世界');
    const replacement = replaceSelection(parsed.runs, { anchor: 6, focus: 8 }, '朋友');
    expect(motdText(replacement.runs)).toBe('Hello 朋友');
    expect(replacement.runs.every(run => run.color === 'b')).toBe(true);
  });

  it('deletes complete Unicode code points rather than half a surrogate pair', () => {
    const parsed = parseMotd('A😀B');
    expect(deleteBackwardRange(parsed.runs, { anchor: 3, focus: 3 })).toEqual({ anchor: 1, focus: 3 });
    expect(deleteForwardRange(parsed.runs, { anchor: 1, focus: 1 })).toEqual({ anchor: 1, focus: 3 });
  });

  it('never simplifies unsupported input during parse and raw fallback', () => {
    const raw = '§x§1§2§3§4§5§6Gradient\n§f世界';
    const parsed = parseMotd(raw);
    expect(parsed.visualSafe).toBe(false);
    expect(raw).toBe('§x§1§2§3§4§5§6Gradient\n§f世界');
  });

  it('round-trips literal backslashes, empty lines, and surrounding spaces', () => {
    for (const raw of ['Path \\server\\world', 'First\n', '\nSecond', '  padded  ']) {
      const parsed = parseMotd(raw);
      expect(parsed.visualSafe).toBe(true);
      expect(serializeMotd(parsed.runs)).toBe(raw);
    }
  });

  it('supports every Vanilla style on an exact selected range', () => {
    const selection = { anchor: 1, focus: 5 };
    const formatted = applyFormatting(parseMotd('abcdef').runs, selection, {
      color: 'd', bold: true, italic: true, underline: true, strike: true, obfuscated: true
    });
    const reparsed = parseMotd(serializeMotd(formatted));
    const selected = reparsed.runs.find(run => run.text === 'bcde');
    expect(selected).toMatchObject({ color: 'd', bold: true, italic: true, underline: true, strike: true, obfuscated: true });
    expect(motdText(reparsed.runs)).toBe('abcdef');
  });
});
