// @vitest-environment jsdom
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { MotdEditor } from './MotdEditor';
import { parseMotd } from './motd';

afterEach(() => { cleanup(); vi.useRealTimers(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });

describe('MOTD visual editor', () => {
  it('does not mark the settings draft dirty merely by rendering or switching modes', () => {
    const changed = vi.fn();
    render(<MotdEditor serverName="Test" serverIconUrl={null} savedRaw={'Hello\nWorld'} resetToken={0} onChange={changed} />);
    fireEvent.click(screen.getByRole('button', { name: 'Raw' }));
    fireEvent.click(screen.getByRole('button', { name: 'Visual' }));
    fireEvent.focus(screen.getByRole('textbox', { name: 'Message of the day' }));
    expect(changed).not.toHaveBeenCalled();
  });

  it('applies formatting to the selected range and preserves it when switching to raw mode', () => {
    const changed = vi.fn();
    render(<MotdEditor serverName="Test" serverIconUrl={null} savedRaw={'Copper Valley\nBuild'} resetToken={0} onChange={changed} />);
    const editor = screen.getByRole('textbox', { name: 'Message of the day' });
    const text = editor.firstChild?.firstChild;
    expect(text).toBeTruthy();
    const selection = window.getSelection()!;
    const range = document.createRange();
    range.setStart(text!, 0); range.setEnd(text!, 6);
    selection.removeAllRanges(); selection.addRange(range);
    fireEvent.mouseUp(editor);
    fireEvent.click(screen.getByRole('button', { name: 'Bold' }));
    expect(changed).toHaveBeenLastCalledWith(expect.stringContaining('§lCopper'));
    fireEvent.click(screen.getByRole('button', { name: 'Raw' }));
    expect((screen.getByLabelText('Raw Vanilla MOTD') as HTMLTextAreaElement).value).toContain('§lCopper');
  });

  it('keeps unknown sequences exact and starts in raw mode', () => {
    const raw = '§x§1§2§3§4§5§6Gradient\n§f世界';
    const changed = vi.fn();
    render(<MotdEditor serverName="Test" serverIconUrl={null} savedRaw={raw} resetToken={0} onChange={changed} />);
    expect((screen.getByLabelText('Raw Vanilla MOTD') as HTMLTextAreaElement).value).toBe(raw);
    expect((screen.getByRole('button', { name: 'Visual' }) as HTMLButtonElement).disabled).toBe(true);
    expect(changed).not.toHaveBeenCalled();
  });

  it('keeps semantic selection through toolbar focus and can undo and redo formatting', () => {
    const changed = vi.fn();
    render(<MotdEditor serverName="Test" serverIconUrl={null} savedRaw={'One\nTwo'} resetToken={0} onChange={changed} />);
    const editor = screen.getByRole('textbox', { name: 'Message of the day' });
    const text = editor.firstChild?.firstChild;
    const selection = window.getSelection()!; const range = document.createRange();
    range.setStart(text!, 1); range.setEnd(text!, 6); selection.removeAllRanges(); selection.addRange(range);
    fireEvent.mouseUp(editor);
    fireEvent.mouseDown(screen.getByRole('button', { name: 'Underline' }));
    fireEvent.click(screen.getByRole('button', { name: 'Underline' }));
    expect(changed).toHaveBeenLastCalledWith(expect.stringContaining('§n'));
    fireEvent.click(screen.getByRole('button', { name: 'Undo' }));
    expect(changed).toHaveBeenLastCalledWith('One\nTwo');
    fireEvent.click(screen.getByRole('button', { name: 'Redo' }));
    expect(changed).toHaveBeenLastCalledWith(expect.stringContaining('§n'));
  });

  it('preserves focus, selection, and the chosen color through repeated formatting and replacement typing', () => {
    const changed = vi.fn();
    render(<MotdEditor serverName="Test" serverIconUrl={null} savedRaw={'Color target'} resetToken={0} onChange={changed} />);
    const originalEditor = screen.getByRole('textbox', { name: 'Message of the day' });
    const text = originalEditor.firstChild?.firstChild;
    expect(text).toBeTruthy();
    originalEditor.focus();
    const selection = window.getSelection()!;
    const selectTarget = () => {
      const currentText = screen.getByRole('textbox', { name: 'Message of the day' }).firstChild?.firstChild;
      const range = document.createRange();
      range.setStart(currentText!, 0); range.setEnd(currentText!, 5);
      selection.removeAllRanges(); selection.addRange(range);
      fireEvent.mouseUp(screen.getByRole('textbox', { name: 'Message of the day' }));
    };
    selectTarget();

    for (const color of ['Red', 'Blue', 'Green', 'Aqua']) {
      const button = screen.getByRole('button', { name: color });
      fireEvent.mouseDown(button);
      fireEvent.click(button);
      expect(screen.getByRole('textbox', { name: 'Message of the day' })).toBe(originalEditor);
      expect(document.activeElement).toBe(originalEditor);
    }

    fireEvent(originalEditor, new InputEvent('beforeinput', {
      bubbles: true, cancelable: true, inputType: 'insertText', data: 'Fresh'
    }));

    const latest = changed.mock.calls.at(-1)?.[0] as string;
    const parsed = parseMotd(latest);
    expect(parsed.runs.map(run => run.text).join('')).toBe('Fresh target');
    expect(parsed.runs.find(run => run.text === 'Fresh')?.color).toBe('b');
    expect(document.activeElement).toBe(originalEditor);

    const yellow = screen.getByRole('button', { name: 'Yellow' });
    fireEvent.mouseDown(yellow);
    fireEvent.click(yellow);
    fireEvent(originalEditor, new InputEvent('beforeinput', {
      bubbles: true, cancelable: true, inputType: 'insertText', data: '!'
    }));
    const continued = parseMotd(changed.mock.calls.at(-1)?.[0] as string);
    expect(continued.runs.map(run => run.text).join('')).toBe('Fresh! target');
    expect(continued.runs.find(run => run.text === '!')?.color).toBe('e');

    selectTarget();
    fireEvent.mouseDown(yellow);
    fireEvent.click(yellow);
    expect(screen.getByRole('button', { name: 'Yellow' }).getAttribute('aria-pressed')).toBe('true');
  });

  it('renders all legacy color controls visibly with accessible selected state', () => {
    render(<MotdEditor serverName="Test" serverIconUrl={null} savedRaw={'Color'} resetToken={0} onChange={() => undefined} />);
    const colors = ['Black', 'Dark blue', 'Dark green', 'Dark aqua', 'Dark red', 'Dark purple', 'Gold', 'Gray', 'Dark gray', 'Blue', 'Green', 'Aqua', 'Red', 'Light purple', 'Yellow', 'White'];
    for (const name of colors) expect(screen.getByRole('button', { name }).getAttribute('style')).toContain('--motd-swatch:');
    fireEvent.click(screen.getByRole('button', { name: 'Aqua' }));
    expect(screen.getByRole('button', { name: 'Aqua' }).getAttribute('aria-pressed')).toBe('true');
  });

  it('keeps obfuscated semantic text stable while the visual mask changes', () => {
    const changed = vi.fn();
    render(<MotdEditor serverName="Test" serverIconUrl={null} savedRaw={'§kSecret'} resetToken={0} onChange={changed} />);
    const masked = screen.getByLabelText('Obfuscated text');
    expect(masked.textContent).toBe('Secret');
    expect(masked.getAttribute('data-obfuscated-preview')).toHaveLength(6);
    expect(changed).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Raw' }));
    expect((screen.getByLabelText('Raw Vanilla MOTD') as HTMLTextAreaElement).value).toBe('§kSecret');
  });

  it('can select obfuscated text and turn obfuscation off without losing the range', () => {
    const changed = vi.fn();
    render(<MotdEditor serverName="Test" serverIconUrl={null} savedRaw={'§kSecret'} resetToken={0} onChange={changed} />);
    const editor = screen.getByRole('textbox', { name: 'Message of the day' });
    const text = editor.firstChild?.firstChild;
    expect(text?.textContent).toBe('Secret');
    const selection = window.getSelection()!; const range = document.createRange();
    range.setStart(text!, 0); range.setEnd(text!, 6); selection.removeAllRanges(); selection.addRange(range);
    fireEvent.mouseUp(editor);
    fireEvent.click(screen.getByRole('button', { name: 'Obfuscated' }));
    expect(changed).toHaveBeenLastCalledWith('Secret');
  });

  it('keeps a Unicode selection anchored to the stable text node across obfuscation frames', () => {
    vi.useFakeTimers();
    vi.spyOn(document, 'hidden', 'get').mockReturnValue(false);
    vi.stubGlobal('matchMedia', vi.fn(() => ({
      matches: false, media: '(prefers-reduced-motion: reduce)', onchange: null,
      addEventListener: vi.fn(), removeEventListener: vi.fn(), addListener: vi.fn(), removeListener: vi.fn(), dispatchEvent: vi.fn()
    })));
    const changed = vi.fn();
    render(<MotdEditor serverName="Test" serverIconUrl={null} savedRaw={'§kA😀B'} resetToken={0} onChange={changed} />);
    const editor = screen.getByRole('textbox', { name: 'Message of the day' });
    const masked = screen.getByLabelText('Obfuscated text');
    const text = masked.firstChild!;
    const preview = masked.getAttribute('data-obfuscated-preview');
    const selection = window.getSelection()!;
    const range = document.createRange();
    range.setStart(text, 1); range.setEnd(text, 3);
    selection.removeAllRanges(); selection.addRange(range);
    fireEvent.mouseUp(editor);

    act(() => vi.advanceTimersByTime(220));

    expect(masked.firstChild).toBe(text);
    expect(masked.textContent).toBe('A😀B');
    expect(masked.getAttribute('data-obfuscated-preview')).not.toBe(preview);
    expect(selection.anchorNode).toBe(text);
    expect(selection.anchorOffset).toBe(1);
    expect(selection.focusNode).toBe(text);
    expect(selection.focusOffset).toBe(3);

    fireEvent.click(screen.getByRole('button', { name: 'Obfuscated' }));
    const raw = changed.mock.calls.at(-1)?.[0] as string;
    const runs = parseMotd(raw).runs;
    expect(runs.map(run => run.text).join('')).toBe('A😀B');
    expect(runs.find(run => run.text.includes('😀'))?.obfuscated).toBe(false);
  });

  it('honors reduced motion and removes media, visibility, and interval work on cleanup', () => {
    vi.useFakeTimers();
    vi.spyOn(document, 'hidden', 'get').mockReturnValue(false);
    const addMedia = vi.fn(); const removeMedia = vi.fn();
    let reduced = true;
    vi.stubGlobal('matchMedia', vi.fn(() => ({
      get matches() { return reduced; }, media: '(prefers-reduced-motion: reduce)', onchange: null,
      addEventListener: addMedia, removeEventListener: removeMedia,
      addListener: vi.fn(), removeListener: vi.fn(), dispatchEvent: vi.fn()
    })));
    const addVisibility = vi.spyOn(document, 'addEventListener');
    const removeVisibility = vi.spyOn(document, 'removeEventListener');
    const interval = vi.spyOn(window, 'setInterval');
    const clearInterval = vi.spyOn(window, 'clearInterval');

    const reducedView = render(<MotdEditor serverName="Test" serverIconUrl={null} savedRaw={'§k世界😀'} resetToken={0} onChange={() => undefined} />);
    const preview = screen.getByLabelText('Obfuscated text').getAttribute('data-obfuscated-preview');
    act(() => vi.advanceTimersByTime(550));
    expect(screen.getByLabelText('Obfuscated text').getAttribute('data-obfuscated-preview')).toBe(preview);
    expect(interval).not.toHaveBeenCalled();
    reducedView.unmount();
    expect(removeMedia).toHaveBeenCalledWith('change', expect.any(Function));
    expect(removeVisibility).toHaveBeenCalledWith('visibilitychange', expect.any(Function));

    reduced = false;
    const animatedView = render(<MotdEditor serverName="Test" serverIconUrl={null} savedRaw={'§k世界😀'} resetToken={0} onChange={() => undefined} />);
    expect(addMedia).toHaveBeenCalledWith('change', expect.any(Function));
    expect(addVisibility).toHaveBeenCalledWith('visibilitychange', expect.any(Function));
    const activeIntervalCount = interval.mock.calls.length;
    expect(activeIntervalCount).toBeGreaterThan(0);
    animatedView.unmount();
    expect(clearInterval).toHaveBeenCalledTimes(activeIntervalCount);
    expect(vi.getTimerCount()).toBe(0);
  });
});
