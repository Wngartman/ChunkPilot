// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { FixtureBridge, fixtures } from '../fixtures/catalog';
import { useAppStore } from '../state/store';
import { CreateServerPage } from './CreateServer';

beforeEach(() => {
  window.sessionStorage.clear();
  window.history.replaceState({}, '', '/?fixture=running&page=create');
  useAppStore.setState({ snapshot: structuredClone(fixtures.running), bridge: new FixtureBridge('running'),
    busy: new Set(), pendingOperations: new Map(), completedOperations: new Set(), error: null });
});
afterEach(cleanup);

describe('creation intent entry', () => {
  it('opens fresh Modpacks at Browse rather than a custom loader', async () => {
    render(<CreateServerPage onDone={() => undefined} />);
    fireEvent.click(screen.getByRole('button', { name: /^Modpacks / }));
    expect(screen.getByRole('button', { name: /^Browse modpacks/ }).getAttribute('data-selected')).toBe('true');
    expect(screen.queryByRole('combobox', { name: 'Custom modded server loader' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: /Continue/ }));
    expect(await screen.findByRole('tab', { name: /Modrinth/ })).toBeTruthy();
  });

  it.each([
    ['Import pack file', /Import ZIP, pack, or JAR/],
    ['Paste provider link', /Resolve/]
  ])('keeps the deliberate %s path after Back and reselecting Modpacks', async (label, action) => {
    render(<CreateServerPage onDone={() => undefined} />);
    fireEvent.click(screen.getByRole('button', { name: /^Modpacks / }));
    fireEvent.click(screen.getByRole('button', { name: new RegExp(`^${label}`) }));
    fireEvent.click(screen.getByRole('button', { name: /Continue/ }));
    expect(await screen.findByRole('button', { name: action })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /Back/ }));
    fireEvent.click(screen.getByRole('button', { name: /^Modpacks / }));
    expect(screen.getByRole('button', { name: new RegExp(`^${label}`) }).getAttribute('data-selected')).toBe('true');
    fireEvent.click(screen.getByRole('button', { name: /Continue/ }));
    expect(await screen.findByRole('button', { name: action })).toBeTruthy();
  });
});
