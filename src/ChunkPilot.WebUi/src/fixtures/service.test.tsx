// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import type { WebUiSnapshot } from '../bridge/types';
import { NavigationGuardProvider } from '../app/NavigationGuard';
import { ModpackPicker } from '../features/modpacks/ModpackPicker';
import { SettingsPage } from '../features/SettingsPage';
import { useAppStore } from '../state/store';
import { FixtureBridge } from './catalog';

afterEach(cleanup);

it.each(['curseforge-service', 'curseforge-service-unavailable', 'curseforge-service-rate-limited'])('renders the shared %s fixture with no personal-key setup', async name => {
  window.history.replaceState({}, '', '/');
  const bridge = new FixtureBridge(name);
  const snapshot = await bridge.request<WebUiSnapshot>('snapshot.get');
  useAppStore.setState({ bridge, snapshot, busy: new Set(), error: null });
  expect(snapshot.build.curseForgeApplicationService).toBe(true);
  const settings = render(<NavigationGuardProvider><SettingsPage /></NavigationGuardProvider>);
  expect(screen.getByDisplayValue('No API key required')).toBeTruthy();
  expect(screen.queryByRole('button', { name: 'Set up CurseForge' })).toBeNull();
  settings.unmount();
  const picker = render(<ModpackPicker value={null} onChange={() => undefined} />);
  fireEvent.click(await screen.findByRole('tab', { name: /CurseForge/ }));
  if (name === 'curseforge-service') expect(await screen.findByRole('button', { name: /Copper Trails/ })).toBeTruthy();
  else expect(await screen.findByRole('button', { name: 'Retry' })).toBeTruthy();
  expect(screen.queryByRole('button', { name: 'Set up CurseForge' })).toBeNull();
  expect(picker.container.textContent).not.toMatch(/credential is missing|Connect your approved|obtain.*key/i);
  expect(picker.container.querySelector('input[type="password"]')).toBeNull();
});
