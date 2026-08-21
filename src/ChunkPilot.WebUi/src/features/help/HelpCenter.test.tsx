// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { BridgeAdapter } from '../../bridge/client';
import type { BridgeMethod } from '../../bridge/types';
import { fixtures } from '../../fixtures/catalog';
import { useAppStore } from '../../state/store';
import { HelpCenter } from './HelpCenter';
import { helpArticles } from './articles';
import { searchHelpArticles } from './search';

const calls: unknown[] = [];
const bridge: BridgeAdapter = { request: async <T,>(method: BridgeMethod, params: Record<string, unknown> = {}) => { calls.push({ method, params }); return { accepted: true } as T; }, subscribe: () => () => undefined, dispose: () => undefined };
beforeEach(() => { calls.length = 0; useAppStore.setState({ snapshot: structuredClone(fixtures.running), bridge }); });
afterEach(cleanup);

describe('offline Help Center', () => {
  it('covers every required symptom family with meaningful structured content', () => {
    const allowedHosts = new Set(['www.minecraft.net', 'docs.papermc.io', 'docs.fabricmc.net', 'docs.neoforged.net', 'support.modrinth.com', 'learn.microsoft.com', 'docs.oracle.com']);
    const articleIds = new Set(helpArticles.map(article => article.id));
    const requiredCategories = new Set(['Getting started', 'Startup', 'Java', 'Networking', 'Players', 'Performance', 'Worlds', 'Plugins', 'Mods & modpacks', 'Backups & recovery']);
    expect(helpArticles.length).toBeGreaterThanOrEqual(25);
    expect(articleIds.size).toBe(helpArticles.length);
    expect(new Set(helpArticles.map(article => article.category))).toEqual(requiredCategories);
    for (const article of helpArticles) {
      expect(article.id).toMatch(/^[a-z0-9]+(?:-[a-z0-9]+)*$/);
      expect(article.plainLanguage.length).toBeGreaterThan(20);
      expect(article.likelyCauses.length).toBeGreaterThan(0);
      expect(article.exactSignatures.length).toBeGreaterThan(0);
      expect(article.safeSteps.length).toBeGreaterThanOrEqual(3);
      expect(article.warnings.length).toBeGreaterThan(0);
      expect(article.whenToStop.length).toBeGreaterThan(10);
      expect(article.sources.length).toBeGreaterThan(0);
      expect(article.lastReviewed).toBe('2026-08-21');
      for (const related of article.related) expect(articleIds.has(related)).toBe(true);
      for (const source of article.sources) { const url = new URL(source.url); expect(url.protocol).toBe('https:'); expect(allowedHosts.has(url.host)).toBe(true); }
    }
  });

  it('finds aliases and exact error text without a network request', () => {
    expect(searchHelpArticles(helpArticles, 'allow list')[0].id).toBe('whitelist-denied');
    expect(searchHelpArticles(helpArticles, 'FAILED TO BIND TO PORT')[0].id).toBe('port-binding-failed');
    expect(searchHelpArticles(helpArticles, 'cgnat')[0].id).toBe('cgnat-or-double-nat');
  });

  it('shows no-results honestly and opens allowlisted sources through the native bridge', () => {
    render(<HelpCenter initialArticleId="java-runtime-mismatch" onDeepLink={vi.fn()} />);
    fireEvent.click(screen.getByRole('button', { name: /Oracle UnsupportedClassVersionError/ }));
    expect(calls).toContainEqual({ method: 'help.openExternal', params: { url: 'https://docs.oracle.com/en/java/javase/17/docs/api/java.base/java/lang/UnsupportedClassVersionError.html' } });
    fireEvent.change(screen.getByLabelText('Search help and troubleshooting'), { target: { value: 'definitely-not-a-real-server-symptom' } });
    expect(screen.getByText('No matching help article')).toBeTruthy();
  });
});
