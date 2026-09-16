// @vitest-environment jsdom
import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { FixtureBridge, fixtures } from '../fixtures/catalog';
import { useAppStore } from '../state/store';
import { AutomationPage } from './Pages';

afterEach(cleanup);

it.each([['04:00', '4:00 AM'], ['12:30', '12:30 PM'], ['00:15', '12:15 AM'], ['21:45:00', '9:45 PM']])('shows schedule time %s in 12-hour form without changing the stored schedule', (stored, label) => {
  const snapshot = structuredClone(fixtures.running);
  snapshot.schedules[0].at = stored;
  useAppStore.setState({ snapshot, bridge: new FixtureBridge('running'), busy: new Set(), error: null });
  render(<AutomationPage />);
  expect(screen.getByText(`Daily · ${label}`)).toBeTruthy();
  expect(useAppStore.getState().snapshot!.schedules[0].at).toBe(stored);
});
