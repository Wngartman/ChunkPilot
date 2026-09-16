import { describe, expect, it } from 'vitest';
import tokens from './tokens.css?raw';
import shell from '../app/Shell.module.css?raw';
import primitives from './Primitives.module.css?raw';

describe('forced-color inheritance', () => {
  it('does not force selected-control descendants back to automatic canvas colors', () => {
    expect(tokens).not.toMatch(/\*\s*\{\s*forced-color-adjust:\s*auto/);
    expect(tokens).toContain(':root { forced-color-adjust: auto; }');
  });

  it('pairs system highlight foreground and background for selected navigation and primary controls', () => {
    for (const css of [shell, primitives])
      expect(css).toMatch(/forced-color-adjust: none; color: HighlightText; background: Highlight/);
  });
});
