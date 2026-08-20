import { protocolVersion, type BridgeEvent, type BridgeMethod, type BridgeRequest, type BridgeResponse, type WebUiSnapshot } from './types';

type EventListener = (event: BridgeEvent) => void;

export class BridgeError extends Error {
  constructor(public readonly code: string, message: string, public readonly details?: string) { super(message); }
}

export class WebViewBridge {
  private pending = new Map<string, { resolve: (value: unknown) => void; reject: (reason: unknown) => void; timer: number }>();
  private listeners = new Set<EventListener>();
  private sequence = 0;
  private readonly onMessage = (event: MessageEvent) => this.receive(event.data);

  constructor(private readonly timeoutMs = 15_000) {
    window.chrome?.webview?.addEventListener('message', this.onMessage);
  }

  isAvailable(): boolean { return Boolean(window.chrome?.webview); }

  async request<T>(method: BridgeMethod, params: Record<string, unknown> = {}, signal?: AbortSignal): Promise<T> {
    if (!window.chrome?.webview) throw new BridgeError('backend_disconnected', 'The native ChunkPilot host is unavailable.');
    const id = `web-${Date.now().toString(36)}-${++this.sequence}`;
    const message: BridgeRequest = { protocolVersion, id, method, params };
    return new Promise<T>((resolve, reject) => {
      const timer = window.setTimeout(() => {
        this.pending.delete(id);
        this.cancelNative(id);
        reject(new BridgeError('timeout', 'ChunkPilot did not answer in time.'));
      }, this.timeoutMs);
      const abort = () => {
        window.clearTimeout(timer);
        this.pending.delete(id);
        this.cancelNative(id);
        reject(new BridgeError('cancelled', 'The operation was cancelled.'));
      };
      signal?.addEventListener('abort', abort, { once: true });
      this.pending.set(id, {
        resolve: value => { signal?.removeEventListener('abort', abort); resolve(value as T); },
        reject: reason => { signal?.removeEventListener('abort', abort); reject(reason); },
        timer
      });
      window.chrome!.webview!.postMessage(message);
    });
  }

  private cancelNative(requestId: string): void {
    window.chrome?.webview?.postMessage({
      protocolVersion,
      id: `cancel-${Date.now().toString(36)}-${++this.sequence}`,
      method: 'bridge.cancel',
      params: { requestId }
    } satisfies BridgeRequest);
  }

  subscribe(listener: EventListener): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  dispose(): void {
    window.chrome?.webview?.removeEventListener('message', this.onMessage);
    for (const item of this.pending.values()) {
      window.clearTimeout(item.timer);
      item.reject(new BridgeError('cancelled', 'The renderer was disposed.'));
    }
    this.pending.clear();
    this.listeners.clear();
  }

  private receive(raw: unknown): void {
    if (!raw || typeof raw !== 'object') return;
    const envelope = raw as Partial<BridgeResponse & BridgeEvent>;
    if (envelope.protocolVersion !== protocolVersion) {
      for (const item of this.pending.values()) {
        window.clearTimeout(item.timer);
        item.reject(new BridgeError('protocol_mismatch', 'ChunkPilot and the WebUI use different bridge versions.'));
      }
      this.pending.clear();
      return;
    }
    if (typeof envelope.id === 'string') {
      const item = this.pending.get(envelope.id);
      if (!item) return;
      this.pending.delete(envelope.id);
      window.clearTimeout(item.timer);
      if (envelope.ok) item.resolve(envelope.result);
      else item.reject(new BridgeError(envelope.error?.code ?? 'internal', envelope.error?.message ?? 'The request failed.', envelope.error?.details));
      return;
    }
    if (typeof envelope.event === 'string') this.listeners.forEach(listener => listener(envelope as BridgeEvent));
  }
}

export interface BridgeAdapter {
  request<T>(method: BridgeMethod, params?: Record<string, unknown>, signal?: AbortSignal): Promise<T>;
  subscribe(listener: EventListener): () => void;
  dispose(): void;
}

export async function initializeBridge(adapter: BridgeAdapter): Promise<WebUiSnapshot> {
  await adapter.request('renderer.ready', { renderedAt: new Date().toISOString() });
  return adapter.request<WebUiSnapshot>('snapshot.get');
}
