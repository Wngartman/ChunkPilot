export const API_ORIGIN = "https://api.curseforge.com";
export const PROTOCOL_VERSION = 1;
export const MAX_JSON_BYTES = 8 * 1024 * 1024;
export const MAX_IMAGE_BYTES = 512 * 1024;
export const MAX_FILE_BYTES = 2 * 1024 * 1024 * 1024;
export const METADATA_TIMEOUT_MS = 20_000;
export const DOWNLOAD_TIMEOUT_MS = 30 * 60_000;
export const STREAM_IDLE_TIMEOUT_MS = 30_000;
export const MAX_REDIRECTS = 5;
export const MAX_LEASE_MS = DOWNLOAD_TIMEOUT_MS + 60_000;

export interface RateLimiter { limit(input: { key: string }): Promise<{ success: boolean }> }
export interface Budget {
  acquire(id: string, durationMs: number): Promise<boolean>;
  reserve(id: string, bytes: number): Promise<boolean>;
  release(id: string): Promise<void>;
}
export type Fetcher = (url: string, init: RequestInit) => Promise<Response>;
export interface Dependencies {
  key?: string;
  limiter?: RateLimiter | null;
  budget?: Budget | null;
  fetch: Fetcher;
  waitUntil?: (promise: Promise<unknown>) => void;
}

export class ServiceError extends Error {
  readonly status: number;
  readonly code: string;
  constructor(status: number, code: string) {
    super(code);
    this.status = status;
    this.code = code;
  }
}

export function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: {
    "content-type": "application/json; charset=utf-8",
    "cache-control": "no-store",
    "x-content-type-options": "nosniff",
    "referrer-policy": "no-referrer",
  } });
}
