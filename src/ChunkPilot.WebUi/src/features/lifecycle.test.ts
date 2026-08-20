import { describe, expect, it } from 'vitest';
import type { ServerSummary } from '../bridge/types';
import { lifecycleAction } from './lifecycle';

const server = (state: ServerSummary['state']) => ({ state } as ServerSummary);

describe('lifecycleAction', () => {
  it('never offers Start while a stop is still authoritative', () => {
    expect(lifecycleAction(server('Stopping'), false)).toMatchObject({ method: null, label: 'Stopping…', pending: true });
  });

  it('keeps running and stopped actions truthful', () => {
    expect(lifecycleAction(server('Running'), false)).toMatchObject({ method: 'servers.stop', destructive: true });
    expect(lifecycleAction(server('Stopped'), false)).toMatchObject({ method: 'servers.start', destructive: false });
  });
});
