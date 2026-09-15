import { afterEach, describe, expect, it, vi } from 'vitest';
import { WebViewBridge, BridgeError, initializeBridge } from './client';

type Handler = (event: MessageEvent) => void;

function host() {
  let handler: Handler | undefined;
  const sent: unknown[] = [];
  const webview = {
    addEventListener: (_: string, value: Handler) => { handler = value; },
    removeEventListener: () => { handler = undefined; },
    postMessage: (value: unknown) => sent.push(value)
  };
  Object.defineProperty(globalThis, 'window', {
    configurable: true,
    value: { chrome: { webview }, setTimeout: globalThis.setTimeout.bind(globalThis), clearTimeout: globalThis.clearTimeout.bind(globalThis) }
  });
  return { sent, reply: (data: unknown) => handler?.({ data } as MessageEvent) };
}

afterEach(() => {
  vi.useRealTimers();
  Reflect.deleteProperty(globalThis, 'window');
});

describe('WebView bridge client', () => {
  it('correlates a response with its request', async () => {
    const native = host();
    const bridge = new WebViewBridge();
    const pending = bridge.request<{ accepted: boolean }>('servers.start', { serverId: 'server-1' });
    const request = native.sent[0] as { id: string };
    native.reply({ protocolVersion: 1, id: request.id, ok: true, result: { accepted: true } });
    await expect(pending).resolves.toEqual({ accepted: true });
    bridge.dispose();
  });

  it('rejects protocol mismatch and timeout', async () => {
    const native = host();
    const mismatch = new WebViewBridge();
    const pending = mismatch.request('snapshot.get');
    native.reply({ protocolVersion: 99, event: 'snapshot.changed', revision: 1, payload: {} });
    await expect(pending).rejects.toMatchObject({ code: 'protocol_mismatch' });
    mismatch.dispose();

    const timeout = new WebViewBridge(5);
    await expect(timeout.request('snapshot.get')).rejects.toMatchObject({ code: 'timeout' });
    expect((native.sent.at(-1) as { method: string; params: { requestId: string } }).method).toBe('bridge.cancel');
    timeout.dispose();
  });

  it.each([
    { method: 'modpacks.preflight' as const, timeoutMs: 30 * 60_000 },
    { method: 'mods.search' as const, timeoutMs: 2 * 60_000 },
    { method: 'mods.release' as const, timeoutMs: 2 * 60_000 },
    { method: 'mods.plan' as const, timeoutMs: 2 * 60_000 },
    { method: 'versions.check' as const, timeoutMs: 2 * 60_000 },
    { method: 'versions.rollback' as const, timeoutMs: 30 * 60_000 },
    { method: 'servers.start' as const, timeoutMs: 30 * 60_000 },
    { method: 'servers.stop' as const, timeoutMs: 10 * 60_000 },
    { method: 'servers.restart' as const, timeoutMs: 30 * 60_000 },
    { method: 'settings.saveServer' as const, timeoutMs: 5 * 60_000 },
    { method: 'backups.create' as const, timeoutMs: 60 * 60_000 },
    { method: 'backups.restore' as const, timeoutMs: 60 * 60_000 },
    { method: 'backups.verify' as const, timeoutMs: 60 * 60_000 },
    { method: 'connectivity.applyBinding' as const, timeoutMs: 30 * 60_000 },
    { method: 'appearance.chooseIcon' as const, timeoutMs: 10 * 60_000 }
  ])('gives $method a bounded long-running timeout', async ({ method, timeoutMs }) => {
    vi.useFakeTimers();
    const native = host();
    const bridge = new WebViewBridge();
    const pending = bridge.request(method);
    const rejection = expect(pending).rejects.toMatchObject({ code: 'timeout' });

    await vi.advanceTimersByTimeAsync(15_000);
    expect(native.sent).toHaveLength(1);

    await vi.advanceTimersByTimeAsync(timeoutMs - 15_000);
    await rejection;
    expect((native.sent.at(-1) as { method: string }).method).toBe('bridge.cancel');
    bridge.dispose();
  });

  it('handshakes before requesting the full snapshot', async () => {
    const methods: string[] = [];
    const adapter = {
      request: async <T,>(method: string) => { methods.push(method); return (method === 'snapshot.get' ? { revision: 1 } : {}) as T; },
      subscribe: () => () => undefined,
      dispose: () => undefined
    };
    await initializeBridge(adapter as never);
    expect(methods).toEqual(['renderer.ready', 'snapshot.get']);
  });

  it('uses structured bridge errors', () => {
    expect(new BridgeError('validation', 'Invalid').code).toBe('validation');
  });

  it('cleans a rejected host send without later reporting a second timeout', async () => {
    vi.useFakeTimers();
    const native = host();
    window.chrome!.webview!.postMessage = () => { throw new Error('Host disconnected'); };
    const bridge = new WebViewBridge();
    await expect(bridge.request('snapshot.get')).rejects.toMatchObject({ code: 'backend_disconnected' });
    await vi.advanceTimersByTimeAsync(60_000);
    expect(native.sent).toHaveLength(0);
    expect(vi.getTimerCount()).toBe(0);
    bridge.dispose();
  });

  it('publishes authoritative events to subscribers', () => {
    const native = host();
    const bridge = new WebViewBridge();
    const received: string[] = [];
    const unsubscribe = bridge.subscribe(event => received.push(event.event));
    native.reply({ protocolVersion: 1, event: 'snapshot.changed', revision: 2, payload: {} });
    expect(received).toEqual(['snapshot.changed']);
    unsubscribe();
    native.reply({ protocolVersion: 1, event: 'snapshot.changed', revision: 3, payload: {} });
    expect(received).toEqual(['snapshot.changed']);
    bridge.dispose();
  });

  it('honours renderer-side cancellation without accepting a late result', async () => {
    const native = host();
    const bridge = new WebViewBridge();
    const cancellation = new AbortController();
    const pending = bridge.request('modpacks.preflight', {}, cancellation.signal);
    const request = native.sent[0] as { id: string };
    cancellation.abort();
    await expect(pending).rejects.toMatchObject({ code: 'cancelled' });
    expect(native.sent).toContainEqual(expect.objectContaining({
      method: 'bridge.cancel', params: { requestId: request.id }
    }));
    native.reply({ protocolVersion: 1, id: request.id, ok: true, result: {} });
    bridge.dispose();
  });

  it('does not send work when its cancellation signal is already aborted', async () => {
    const native = host();
    const bridge = new WebViewBridge();
    const cancellation = new AbortController();
    cancellation.abort();

    await expect(bridge.request('snapshot.refresh', {}, cancellation.signal))
      .rejects.toMatchObject({ code: 'cancelled' });
    expect(native.sent).toEqual([]);
    bridge.dispose();
  });

  it('rejects pending work when the renderer is disposed', async () => {
    host();
    const bridge = new WebViewBridge();
    const pending = bridge.request('snapshot.refresh');
    bridge.dispose();
    await expect(pending).rejects.toMatchObject({ code: 'cancelled' });
  });
});
