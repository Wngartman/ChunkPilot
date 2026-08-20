export const motdColors = [
  { code: '0', label: 'Black', hex: '#000000' }, { code: '1', label: 'Dark blue', hex: '#0000aa' },
  { code: '2', label: 'Dark green', hex: '#00aa00' }, { code: '3', label: 'Dark aqua', hex: '#00aaaa' },
  { code: '4', label: 'Dark red', hex: '#aa0000' }, { code: '5', label: 'Dark purple', hex: '#aa00aa' },
  { code: '6', label: 'Gold', hex: '#ffaa00' }, { code: '7', label: 'Gray', hex: '#aaaaaa' },
  { code: '8', label: 'Dark gray', hex: '#555555' }, { code: '9', label: 'Blue', hex: '#5555ff' },
  { code: 'a', label: 'Green', hex: '#55ff55' }, { code: 'b', label: 'Aqua', hex: '#55ffff' },
  { code: 'c', label: 'Red', hex: '#ff5555' }, { code: 'd', label: 'Light purple', hex: '#ff55ff' },
  { code: 'e', label: 'Yellow', hex: '#ffff55' }, { code: 'f', label: 'White', hex: '#ffffff' }
] as const;

export interface MotdStyle {
  color: string | null;
  bold: boolean;
  italic: boolean;
  underline: boolean;
  strike: boolean;
  obfuscated: boolean;
}

export interface MotdRun extends MotdStyle { text: string; }
export interface ParsedMotd { runs: MotdRun[]; visualSafe: boolean; reason: string | null; }
export interface MotdSelection { anchor: number; focus: number; }

export const defaultMotdStyle = (): MotdStyle => ({
  color: null, bold: false, italic: false, underline: false, strike: false, obfuscated: false
});

type MotdColorCode = (typeof motdColors)[number]['code'];
const colorCodes = new Set<string>(motdColors.map(color => color.code));
const styleCode = (style: MotdStyle) => `${style.color ?? '-'}${style.bold ? 'b' : '-'}${style.italic ? 'i' : '-'}${style.underline ? 'u' : '-'}${style.strike ? 's' : '-'}${style.obfuscated ? 'o' : '-'}`;

export function parseMotd(raw: string): ParsedMotd {
  let style = defaultMotdStyle();
  const runs: MotdRun[] = [];
  let buffer = '';
  let visualSafe = true;
  let reason: string | null = null;
  const flush = () => {
    if (!buffer) return;
    runs.push({ ...style, text: buffer });
    buffer = '';
  };
  for (let index = 0; index < raw.length; index++) {
    const character = raw[index];
    if (character !== '§') { buffer += character; continue; }
    if (index + 1 >= raw.length) {
      buffer += character;
      visualSafe = false;
      reason ??= 'A trailing formatting marker can only be edited safely in Raw mode.';
      continue;
    }
    const code = raw[++index].toLowerCase();
    flush();
    if (colorCodes.has(code)) style = { ...defaultMotdStyle(), color: code as MotdColorCode };
    else if (code === 'l') style = { ...style, bold: true };
    else if (code === 'o') style = { ...style, italic: true };
    else if (code === 'n') style = { ...style, underline: true };
    else if (code === 'm') style = { ...style, strike: true };
    else if (code === 'k') style = { ...style, obfuscated: true };
    else if (code === 'r') style = defaultMotdStyle();
    else {
      buffer += `§${raw[index]}`;
      visualSafe = false;
      reason ??= `The saved MOTD uses an unsupported §${raw[index]} sequence. Raw mode preserves it exactly.`;
    }
  }
  flush();
  if ((raw.match(/\n/g) ?? []).length > 1) {
    visualSafe = false;
    reason ??= 'Vanilla server-list MOTDs are limited to two lines in the visual editor. Raw mode preserves additional lines.';
  }
  return { runs, visualSafe, reason };
}

export function serializeMotd(runs: MotdRun[]): string {
  let previous = defaultMotdStyle();
  let output = '';
  for (const run of runs) {
    if (!run.text) continue;
    const next: MotdStyle = { color: run.color, bold: run.bold, italic: run.italic, underline: run.underline, strike: run.strike, obfuscated: run.obfuscated };
    if (styleCode(previous) !== styleCode(next)) {
      output += '§r';
      if (next.color) output += `§${next.color}`;
      if (next.obfuscated) output += '§k';
      if (next.bold) output += '§l';
      if (next.strike) output += '§m';
      if (next.underline) output += '§n';
      if (next.italic) output += '§o';
      previous = next;
    }
    output += run.text;
  }
  return output.startsWith('§r') ? output.slice(2) : output;
}

export function plainMotd(raw: string): string {
  return parseMotd(raw).runs.map(run => run.text).join('');
}

export function validateMotd(raw: string): string | null {
  if (raw.length > 256) return `The formatted MOTD is ${raw.length} characters. Vanilla settings accept at most 256 here.`;
  if (raw.includes('\r')) return 'Use a normal line break instead of a carriage return.';
  if ((raw.match(/\n/g) ?? []).length > 1) return 'Use at most two server-list lines.';
  return null;
}

export function normalizeRuns(runs: MotdRun[]): MotdRun[] {
  const normalized: MotdRun[] = [];
  for (const run of runs) {
    if (!run.text) continue;
    const previous = normalized.at(-1);
    if (previous && styleCode(previous) === styleCode(run)) previous.text += run.text;
    else normalized.push({ ...run });
  }
  return normalized;
}

export function motdText(runs: MotdRun[]): string { return runs.map(run => run.text).join(''); }

export function orderedSelection(selection: MotdSelection): { start: number; end: number } {
  return { start: Math.min(selection.anchor, selection.focus), end: Math.max(selection.anchor, selection.focus) };
}

export function styleAt(runs: MotdRun[], offset: number): MotdStyle {
  let cursor = 0;
  for (const run of runs) {
    const end = cursor + run.text.length;
    if (offset >= cursor && offset <= end) return { color: run.color, bold: run.bold, italic: run.italic, underline: run.underline, strike: run.strike, obfuscated: run.obfuscated };
    cursor = end;
  }
  const last = runs.at(-1);
  return last ? { color: last.color, bold: last.bold, italic: last.italic, underline: last.underline, strike: last.strike, obfuscated: last.obfuscated } : defaultMotdStyle();
}

export function applyFormatting(runs: MotdRun[], selection: MotdSelection, patch: Partial<MotdStyle>): MotdRun[] {
  const { start, end } = orderedSelection(selection);
  if (start === end) return normalizeRuns(runs);
  const output: MotdRun[] = [];
  let cursor = 0;
  for (const run of runs) {
    const runStart = cursor; const runEnd = cursor + run.text.length; cursor = runEnd;
    if (end <= runStart || start >= runEnd) { output.push({ ...run }); continue; }
    const localStart = Math.max(0, start - runStart); const localEnd = Math.min(run.text.length, end - runStart);
    if (localStart > 0) output.push({ ...run, text: run.text.slice(0, localStart) });
    output.push({ ...run, ...patch, text: run.text.slice(localStart, localEnd) });
    if (localEnd < run.text.length) output.push({ ...run, text: run.text.slice(localEnd) });
  }
  return normalizeRuns(output);
}

export function clearFormatting(runs: MotdRun[], selection: MotdSelection): MotdRun[] {
  return applyFormatting(runs, selection, defaultMotdStyle());
}

export function replaceSelection(runs: MotdRun[], selection: MotdSelection, text: string, typingStyle?: MotdStyle): { runs: MotdRun[]; selection: MotdSelection } {
  const { start, end } = orderedSelection(selection);
  const output: MotdRun[] = [];
  const insertStyle = typingStyle ?? styleAt(runs, start);
  let cursor = 0; let inserted = false;
  for (const run of runs) {
    const runStart = cursor; const runEnd = cursor + run.text.length; cursor = runEnd;
    if (runEnd <= start) { output.push({ ...run }); continue; }
    if (!inserted) {
      if (start > runStart) output.push({ ...run, text: run.text.slice(0, start - runStart) });
      if (text) output.push({ ...insertStyle, text });
      inserted = true;
    }
    if (runEnd > end) output.push({ ...run, text: run.text.slice(Math.max(0, end - runStart)) });
  }
  if (!inserted && text) output.push({ ...insertStyle, text });
  const next = start + text.length;
  return { runs: normalizeRuns(output), selection: { anchor: next, focus: next } };
}

export function deleteBackwardRange(runs: MotdRun[], selection: MotdSelection): MotdSelection {
  const ordered = orderedSelection(selection);
  if (ordered.start !== ordered.end || ordered.start === 0) return selection;
  const text = motdText(runs);
  const previousUnit = text.charCodeAt(ordered.start - 1);
  const width = previousUnit >= 0xdc00 && previousUnit <= 0xdfff && ordered.start >= 2 ? 2 : 1;
  return { anchor: Math.max(0, ordered.start - width), focus: ordered.start };
}

export function deleteForwardRange(runs: MotdRun[], selection: MotdSelection): MotdSelection {
  const ordered = orderedSelection(selection); const text = motdText(runs);
  if (ordered.start !== ordered.end || ordered.end >= text.length) return selection;
  const next = text.codePointAt(ordered.end);
  const width = next != null && next > 0xffff ? 2 : 1;
  return { anchor: ordered.start, focus: ordered.end + width };
}
