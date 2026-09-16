// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { fixtures } from '../fixtures/catalog';
import { useAppStore } from '../state/store';
import { NavigationGuardProvider } from '../app/NavigationGuard';
import { SettingsPage } from './SettingsPage';

beforeEach(() => {
  window.history.replaceState({}, '', '/?fixture=running&page=settings');
  useAppStore.setState({ snapshot: structuredClone(fixtures.running), busy: new Set(), error: null });
});
afterEach(cleanup);

describe('settings accessibility', () => {
  it('names every preference switch and exposes the selected category', () => {
    render(<NavigationGuardProvider><SettingsPage /></NavigationGuardProvider>);
    for (const name of ['Minimize to notification area', 'Start minimized', 'Start with Windows'])
      expect(screen.getByRole('switch', { name })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'General' }).getAttribute('aria-current')).toBe('page');
    fireEvent.click(screen.getByRole('button', { name: 'Appearance' }));
    expect(screen.getByRole('switch', { name: 'Reduced motion' })).toBeTruthy();
  });

  it('keeps a truthful empty result when no setting category matches', () => {
    render(<NavigationGuardProvider><SettingsPage /></NavigationGuardProvider>);
    fireEvent.change(screen.getByRole('searchbox'), { target: { value: 'no-such-category' } });
    expect(screen.getByRole('status').textContent).toBe('No matching categories');
  });
});
