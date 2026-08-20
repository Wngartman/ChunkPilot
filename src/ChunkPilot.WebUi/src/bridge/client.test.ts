import { afterEach, describe, expect, it } from 'vitest';
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

afterEach(() => { Reflect.deleteProperty(globalThis, 'window'); });

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
    const pending = bridge.request('snapshot.refresh', {}, cancellation.signal);
    const request = native.sent[0] as { id: string };
    cancellation.abort();
    await expect(pending).rejects.toMatchObject({ code: 'cancelled' });
    expect(native.sent).toContainEqual(expect.objectContaining({
      method: 'bridge.cancel', params: { requestId: request.id }
    }));
    native.reply({ protocolVersion: 1, id: request.id, ok: true, result: {} });
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
