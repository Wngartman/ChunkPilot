// @vitest-environment jsdom
import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { fixtures } from '../../fixtures/catalog';
import { ConnectionSummary, connectionChoice } from './ConnectionSummary';
afterEach(cleanup);

describe('native connection summary boundary', () => {
  it('renders loopback evidence without promoting saved LAN intent or the host LAN address', () => {
    const snapshot = structuredClone(fixtures.running);
    const server = snapshot.servers[0];
    server.connection = { ...server.connection, audience: 'computer', label: 'Join on this computer',
      badge: 'Only on this computer', address: '127.0.0.1:25586', kind: 'local',
      localAddress: '127.0.0.1:25586', lanAddress: null, configuredLanAddress: null,
      requestedAudience: 'HomeNetwork', requestedAudienceNotApplied: true,
      explanation: 'LAN access is selected but not applied: the saved binding is local-only.' };
    expect(connectionChoice(server).address).toBe('127.0.0.1:25586');
    render(<ConnectionSummary server={server} connectivity={null} />);
    expect(screen.getByText('Only on this computer')).toBeTruthy();
    expect(screen.queryByText(server.lanAddress!)).toBeNull();
    expect(screen.queryByRole('button', { name: 'Copy LAN address' })).toBeNull();
    expect(screen.getByRole('button', { name: 'Copy local address' })).toBeTruthy();
  });

  it.each(['Server stopped', 'Server starting', 'Connection not verified'])('renders %s exactly as native state', badge => {
    const snapshot = structuredClone(fixtures.running);
    const server = snapshot.servers[0];
    server.connection = { ...server.connection, badge, address: null, kind: null, listenerVerified: false };
    // Stale detail evidence for even the same server cannot override its current card contract.
    snapshot.connectivity!.connection = { ...server.connection, badge: 'Connection confirmed', address: '203.0.113.1:25565', kind: 'public' };
    render(<ConnectionSummary server={server} connectivity={snapshot.connectivity} compact />);
    expect(screen.getByText(badge)).toBeTruthy();
    expect(screen.queryByText('Connection confirmed')).toBeNull();
    expect(screen.queryByRole('button', { name: /Copy/ })).toBeNull();
  });

  it('rejects missing or foreign-server native evidence instead of using legacy addresses', () => {
    const server = structuredClone(fixtures.running.servers[0]);
    server.connection.serverId = 'another-server';
    expect(connectionChoice(server).badge).toBe('Connection not verified');
    expect(connectionChoice(server).address).toBeNull();
  });

  it('never renders another server\'s other-address details', () => {
    const snapshot = structuredClone(fixtures.running);
    snapshot.connectivity!.serverId = 'another-server';
    render(<ConnectionSummary server={snapshot.servers[0]} connectivity={snapshot.connectivity} showAll />);
    expect(screen.queryByText('Observed addresses')).toBeNull();
  });
});
