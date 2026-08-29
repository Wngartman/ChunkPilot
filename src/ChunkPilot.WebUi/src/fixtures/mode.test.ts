import { describe, expect, it } from 'vitest';
import { isFixtureMode } from './mode';

const location = (value: string) => {
  const url = new URL(value);
  return { hostname: url.hostname, search: url.search } as Pick<Location, 'search' | 'hostname'>;
};

describe('fixture mode boundary', () => {
  it('accepts the dedicated native fixture host', () => {
    expect(isFixtureMode(location('https://fixture.chunkpilot.local/?fixture=running'), false)).toBe(true);
  });

  it('rejects fixture query parameters on the packaged live origin', () => {
    expect(isFixtureMode(location('https://chunkpilot.local/?fixture=running'), false)).toBe(false);
  });

  it('allows localhost fixtures only in a development build', () => {
    expect(isFixtureMode(location('http://127.0.0.1:5173/?fixture=running'), true)).toBe(true);
    expect(isFixtureMode(location('http://127.0.0.1:5173/?fixture=running'), false)).toBe(false);
  });
});
