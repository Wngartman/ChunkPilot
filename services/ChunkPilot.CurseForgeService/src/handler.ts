import {
  API_ORIGIN, DOWNLOAD_TIMEOUT_MS, MAX_FILE_BYTES, MAX_IMAGE_BYTES, MAX_JSON_BYTES,
  MAX_LEASE_MS, MAX_REDIRECTS, METADATA_TIMEOUT_MS, PROTOCOL_VERSION,
  STREAM_IDLE_TIMEOUT_MS, ServiceError, jsonResponse, type Dependencies,
} from "./contract.ts";
import { approvedCdn, parseRoute, sourceIdentityText, type Route } from "./routes.ts";

type RecordValue = Record<string, unknown>;
function object(value: unknown): RecordValue {
  if (value === null || typeof value !== "object" || Array.isArray(value)) throw new ServiceError(502, "invalid_upstream_response");
  return value as RecordValue;
}
function data(value: unknown): unknown { return object(value).data; }

function scope(parent: AbortSignal, milliseconds: number) {
  const controller = new AbortController();
  const abort = () => controller.abort();
  parent.addEventListener("abort", abort, { once: true });
  if (parent.aborted) abort();
  const timer = setTimeout(abort, milliseconds);
  return { signal: controller.signal, abort, stopTimer() { clearTimeout(timer); }, dispose() { clearTimeout(timer); parent.removeEventListener("abort", abort); } };
}

/** Race explicitly as well as supplying fetch's signal: cancellation also bounds fixture/custom streams. */
async function bounded<T>(work: Promise<T>, signal: AbortSignal, milliseconds = METADATA_TIMEOUT_MS): Promise<T> {
  if (signal.aborted) { void work.catch(() => {}); throw new ServiceError(504, "upstream_timeout"); }
  let timer: ReturnType<typeof setTimeout> | undefined;
  let stop: (() => void) | undefined;
  const canceled = new Promise<never>((_, reject) => {
    stop = () => reject(new ServiceError(504, "upstream_timeout"));
    signal.addEventListener("abort", stop, { once: true });
    timer = setTimeout(stop, milliseconds);
  });
  try { return await Promise.race([work, canceled]); }
  finally { if (timer) clearTimeout(timer); if (stop) signal.removeEventListener("abort", stop); }
}

function discard(response: Response): void { void response.body?.cancel().catch(() => {}); }

/** Never relay provider error bodies, headers, request headers, or thrown exception text. */
function checkStatus(response: Response): void {
  if (response.status === 200) return;
  discard(response);
  if (response.status === 404) throw new ServiceError(404, "content_unavailable");
  if (response.status === 429) throw new ServiceError(429, "provider_rate_limited");
  if (response.status === 401 || response.status === 403) throw new ServiceError(503, "provider_unavailable");
  throw new ServiceError(502, "upstream_failure");
}

function contentLength(response: Response, maximum: number): number | null {
  const encoding = response.headers.get("content-encoding");
  if (encoding && encoding !== "identity") throw new ServiceError(502, "invalid_upstream_response");
  const text = response.headers.get("content-length");
  if (text === null) return null;
  if (!/^(0|[1-9][0-9]{0,15})$/.test(text)) throw new ServiceError(502, "invalid_upstream_response");
  const size = Number(text);
  if (!Number.isSafeInteger(size) || size > maximum) throw new ServiceError(502, "upstream_too_large");
  return size;
}

async function readBytes(response: Response, maximum: number, signal: AbortSignal): Promise<Uint8Array> {
  let reader: ReadableStreamDefaultReader<Uint8Array> | undefined;
  try {
    const expected = contentLength(response, maximum);
    if (!response.body) throw new ServiceError(502, "invalid_upstream_response");
    reader = response.body.getReader();
    const chunks: Uint8Array[] = [];
    let size = 0;
    for (;;) {
      const part = await bounded(reader.read(), signal);
      if (part.done) break;
      size += part.value.byteLength;
      if (size > maximum) throw new ServiceError(502, "upstream_too_large");
      chunks.push(part.value);
    }
    if (expected !== null && expected !== size) throw new ServiceError(502, "upstream_truncated");
    const bytes = new Uint8Array(size);
    let offset = 0;
    for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength; }
    return bytes;
  } finally {
    if (reader) { void reader.cancel().catch(() => {}); reader.releaseLock(); }
    else discard(response);
  }
}

function validateNoSecret(value: unknown, key: string): void {
  const stack: Array<{ value: unknown; depth: number }> = [{ value, depth: 0 }];
  let nodes = 0;
  while (stack.length) {
    const next = stack.pop()!;
    if (++nodes > 200_000 || next.depth > 64) throw new ServiceError(502, "invalid_upstream_response");
    if (typeof next.value === "string" && next.value.includes(key)) throw new ServiceError(502, "invalid_upstream_response");
    if (next.value !== null && typeof next.value === "object") {
      for (const [name, entry] of Object.entries(next.value)) {
        if (name.includes(key)) throw new ServiceError(502, "invalid_upstream_response");
        stack.push({ value: entry, depth: next.depth + 1 });
      }
    }
  }
}

/** Linear-time byte matcher; state spans chunks without retaining file content. */
class SecretMatcher {
  readonly needle: Uint8Array;
  readonly prefix: number[];
  matched = 0;
  constructor(key: string) {
    this.needle = new TextEncoder().encode(key);
    this.prefix = new Array<number>(this.needle.length).fill(0);
    for (let i = 1, j = 0; i < this.needle.length; i++) {
      while (j && this.needle[i] !== this.needle[j]) j = this.prefix[j - 1]!;
      if (this.needle[i] === this.needle[j]) j++;
      this.prefix[i] = j;
    }
  }
  inspect(bytes: Uint8Array): void {
    for (const value of bytes) {
      while (this.matched && value !== this.needle[this.matched]) this.matched = this.prefix[this.matched - 1]!;
      if (value === this.needle[this.matched]) this.matched++;
      if (this.matched === this.needle.length) throw new ServiceError(502, "invalid_upstream_response");
    }
  }
}

function minecraftProject(value: unknown, id?: number, distribution = false): RecordValue {
  const project = object(value);
  if (!Number.isSafeInteger(project.id) || Number(project.id) <= 0 || project.gameId !== 432 || (id && project.id !== id))
    throw new ServiceError(502, "unexpected_project_identity");
  if (distribution && (project.isAvailable !== true || project.allowModDistribution !== true))
    throw new ServiceError(403, "author_distribution_unavailable");
  return project;
}

function minecraftFile(value: unknown, projectId: number, fileId?: number): RecordValue {
  const file = object(value);
  if (!Number.isSafeInteger(file.id) || Number(file.id) <= 0 || file.gameId !== 432 || file.modId !== projectId || (fileId && file.id !== fileId))
    throw new ServiceError(502, "unexpected_file_identity");
  return file;
}

function availableFile(value: unknown, fileId: number): RecordValue {
  const file = object(value);
  if (!Number.isSafeInteger(file.modId) || Number(file.modId) <= 0) throw new ServiceError(502, "unexpected_file_identity");
  minecraftFile(file, Number(file.modId), fileId);
  if (file.isAvailable !== true) throw new ServiceError(403, "content_unavailable");
  if (!Number.isSafeInteger(file.fileLength) || Number(file.fileLength) <= 0 || Number(file.fileLength) > MAX_FILE_BYTES)
    throw new ServiceError(502, "upstream_too_large");
  if (!Array.isArray(file.hashes) || !file.hashes.some(h => {
    const hash = object(h);
    return hash.algo === 1 && typeof hash.value === "string" && /^[a-fA-F0-9]{40}$/.test(hash.value);
  })) throw new ServiceError(502, "unverified_file");
  return file;
}

function upstreamHeaders(key: string, json: boolean): Record<string, string> {
  return { "x-api-key": key, "accept": json ? "application/json" : "*/*", "accept-encoding": "identity",
    "user-agent": "ChunkPilot-CurseForgeService/1.0" };
}

/** Each upstream request reserves its full byte ceiling before any network request. No refunds: a
 * crashed Worker cannot accidentally release consumed capacity or reset the hard daily bound. */
function gateway(deps: Dependencies, lease: string, signal: AbortSignal) {
  const reserve = async (bytes: number) => {
    if (!await bounded(deps.budget!.reserve(lease, bytes), signal)) throw new ServiceError(429, "service_capacity_reached");
  };
  const api = async (path: string, payload?: unknown): Promise<unknown> => {
    await reserve(MAX_JSON_BYTES);
    const headers = upstreamHeaders(deps.key!, true);
    if (payload !== undefined) headers["content-type"] = "application/json";
    const local = scope(signal, METADATA_TIMEOUT_MS);
    try {
      const response = await bounded(deps.fetch(API_ORIGIN + path, {
        method: payload === undefined ? "GET" : "POST", headers, redirect: "manual", signal: local.signal,
        ...(payload === undefined ? {} : { body: JSON.stringify(payload) }),
      }), local.signal);
      if (response.redirected || (response.url && response.url !== API_ORIGIN + path)) { discard(response); throw new ServiceError(502, "upstream_redirect_rejected"); }
      checkStatus(response);
      const contentType = response.headers.get("content-type")?.split(";", 1)[0]?.trim().toLowerCase();
      if (contentType !== "application/json") { discard(response); throw new ServiceError(502, "invalid_upstream_response"); }
      const bytes = await readBytes(response, MAX_JSON_BYTES, local.signal);
      let value: unknown;
      try { value = JSON.parse(new TextDecoder("utf-8", { fatal: true, ignoreBOM: false }).decode(bytes)); }
      catch { throw new ServiceError(502, "invalid_upstream_response"); }
      validateNoSecret(value, deps.key!);
      return value;
    } finally { local.dispose(); }
  };
  const cdn = async (initial: string, ceiling: number): Promise<Response> => {
    let current = approvedCdn(initial);
    for (let redirects = 0; ; redirects++) {
      await reserve(ceiling);
      const local = scope(signal, METADATA_TIMEOUT_MS);
      let response: Response;
      try { response = await bounded(deps.fetch(current, {
        method: "GET", headers: upstreamHeaders(deps.key!, false), redirect: "manual", signal: local.signal,
      }), local.signal); }
      catch (error) { local.abort(); local.dispose(); throw error; }
      // Header deadline ends here; the same request signal remains attached to the whole-operation
      // deadline/client cancellation for the streamed body. Parent abort is a once-only listener.
      local.stopTimer();
      if (response.redirected || (response.url && response.url !== current)) { discard(response); throw new ServiceError(502, "upstream_redirect_rejected"); }
      if ([301, 302, 303, 307, 308].includes(response.status)) {
        const location = response.headers.get("location");
        discard(response);
        local.abort(); local.dispose();
        if (!location || redirects >= MAX_REDIRECTS) throw new ServiceError(502, "upstream_redirect_rejected");
        let next: string;
        try { next = new URL(location, current).href; } catch { throw new ServiceError(502, "invalid_upstream_destination"); }
        current = approvedCdn(next);
        continue;
      }
      checkStatus(response);
      return response;
    }
  };
  return { api, cdn };
}

async function metadata(route: Extract<Route, { kind: "metadata" }>, api: ReturnType<typeof gateway>["api"]): Promise<Response> {
  // File endpoints do not encode their game. Resolve their exact project before exposing them.
  if (route.projectId && route.shape !== "project") minecraftProject(data(await api(`/v1/mods/${route.projectId}`)), route.projectId, route.shape === "downloadUrl");
  const result = await api(route.path);
  const value = data(result);
  switch (route.shape) {
    case "game": if (object(value).id !== 432) throw new ServiceError(502, "unexpected_project_identity"); break;
    case "versions": if (!Array.isArray(value)) throw new ServiceError(502, "invalid_upstream_response"); break;
    case "project": minecraftProject(value, route.projectId); break;
    case "search":
    case "categories":
      if (!Array.isArray(value)) throw new ServiceError(502, "invalid_upstream_response");
      for (const item of value) {
        if (route.shape === "search") minecraftProject(item);
        else if (object(item).gameId !== 432) throw new ServiceError(502, "unexpected_project_identity");
      }
      break;
    case "files":
      if (!Array.isArray(value) || value.length > 50) throw new ServiceError(502, "invalid_upstream_response");
      for (const item of value) minecraftFile(item, route.projectId!);
      break;
    case "file": minecraftFile(value, route.projectId!, route.fileId); break;
    case "downloadUrl": if (value !== null) approvedCdn(value); break;
  }
  // Keep authoritative CurseForge URLs/identities unchanged for the native verification boundary.
  return jsonResponse(result);
}

function streamFile(response: Response, expected: number, signal: AbortSignal, key: string, finish: () => void): Response {
  try {
    const length = contentLength(response, expected);
    if (length !== null && length !== expected) throw new ServiceError(502, "upstream_truncated");
    if (!response.body) throw new ServiceError(502, "invalid_upstream_response");
    const reader = response.body.getReader();
    let count = 0;
    let ended = false;
    let held = new Uint8Array(0);
    const matcher = new SecretMatcher(key);
    const end = () => { if (!ended) { ended = true; signal.removeEventListener("abort", end); void reader.cancel().catch(() => {}); finish(); } };
    signal.addEventListener("abort", end, { once: true });
    const stream = new ReadableStream<Uint8Array>({
      async pull(controller) {
        try {
          const next = await bounded(reader.read(), signal, STREAM_IDLE_TIMEOUT_MS);
          if (next.done) {
            if (count !== expected) throw new ServiceError(502, "upstream_truncated");
            if (held.byteLength) controller.enqueue(held);
            end(); controller.close(); return;
          }
          count += next.value.byteLength;
          if (count > expected) throw new ServiceError(502, "upstream_too_large");
          matcher.inspect(next.value);
          // A key prefix must not reach a caller before a later chunk reveals a full key match.
          const joined = new Uint8Array(held.byteLength + next.value.byteLength);
          joined.set(held); joined.set(next.value, held.byteLength);
          const emit = Math.max(0, joined.byteLength - (matcher.needle.byteLength - 1));
          held = joined.slice(emit);
          if (emit) controller.enqueue(joined.slice(0, emit));
        } catch { end(); controller.error(new Error("CurseForge download interrupted; verification required.")); }
      },
      cancel() { end(); },
    }, { highWaterMark: 1 });
    return new Response(stream, { headers: { "content-type": "application/octet-stream", "content-length": String(expected),
      "cache-control": "no-store", "x-content-type-options": "nosniff", "referrer-policy": "no-referrer" } });
  } catch (error) { discard(response); throw error; }
}

export async function handleCurseForge(request: Request, deps: Dependencies): Promise<Response> {
  let finish: (() => void) | undefined;
  let streaming = false;
  try {
    const route = parseRoute(request);
    const configured = !!deps.key?.trim() && deps.key.length <= 4096 && !/[\r\n\u0000]/.test(deps.key) && !!deps.limiter && !!deps.budget;
    if (route.kind === "health") return jsonResponse({ service: "chunkpilot-curseforge", protocolVersion: PROTOCOL_VERSION, configured }, configured ? 200 : 503);
    if (!configured) throw new ServiceError(503, "service_not_configured");
    // Cloudflare overwrites this header; there is no alternate caller-controlled fallback.
    const caller = request.headers.get("cf-connecting-ip");
    if (!caller || caller.length > 64 || !/^[a-fA-F0-9:.]+$/.test(caller)) throw new ServiceError(403, "client_identity_unavailable");
    const duration = route.kind === "download" ? DOWNLOAD_TIMEOUT_MS : METADATA_TIMEOUT_MS;
    const operation = scope(request.signal, duration);
    const lease = crypto.randomUUID();
    let acquired = false;
    let finished = false;
    const release = () => {
      const cleanup = bounded(deps.budget!.release(lease), new AbortController().signal, 5000).catch(() => {});
      deps.waitUntil?.(cleanup);
    };
    finish = () => {
      if (finished) return;
      finished = true;
      operation.abort(); operation.dispose();
      if (acquired) {
        // Cloudflare must retain the release after response completion or a client disconnect.
        release();
      }
    };
    if (!(await bounded(deps.limiter!.limit({ key: caller }), operation.signal)).success) throw new ServiceError(429, "client_rate_limited");
    const now = deps.now?.() ?? Date.now();
    const acquisition = deps.budget!.acquire(lease, now + Math.min(MAX_LEASE_MS, duration + 60_000)).then(allowed => {
      // Record ownership independently of the cancellable await. A same-turn abort can reject that
      // await before this callback runs, while finish has not yet run; either ordering must release.
      acquired = allowed;
      if (acquired && finished) release();
      return allowed;
    });
    deps.waitUntil?.(bounded(acquisition, new AbortController().signal, 25_000).catch(() => {}));
    await bounded(acquisition, operation.signal);
    if (!acquired) throw new ServiceError(429, "service_concurrency_reached");
    const { api, cdn } = gateway(deps, lease, operation.signal);
    if (route.kind === "metadata") return await metadata(route, api);
    if (route.kind === "image") {
      const project = minecraftProject(data(await api(`/v1/mods/${route.projectId}`)), route.projectId, true);
      const logo = object(project.logo);
      const url = approvedCdn(logo.thumbnailUrl ?? logo.url);
      const response = await cdn(url, MAX_IMAGE_BYTES);
      const type = response.headers.get("content-type")?.split(";", 1)[0]?.trim().toLowerCase();
      if (!type || !["image/png", "image/jpeg", "image/webp", "image/gif"].includes(type)) { discard(response); throw new ServiceError(502, "invalid_upstream_image"); }
      const bytes = await readBytes(response, MAX_IMAGE_BYTES, operation.signal);
      new SecretMatcher(deps.key!).inspect(bytes);
      return new Response(bytes.buffer as ArrayBuffer, { headers: { "content-type": type, "content-length": String(bytes.byteLength),
        "cache-control": "no-store", "x-content-type-options": "nosniff", "referrer-policy": "no-referrer" } });
    }
    const files = data(await api("/v1/mods/files", { fileIds: [route.fileId] }));
    if (!Array.isArray(files) || files.length !== 1) throw new ServiceError(404, "content_unavailable");
    const file = availableFile(files[0], route.fileId);
    minecraftProject(data(await api(`/v1/mods/${file.modId}`)), Number(file.modId), true);
    // The exact official endpoint is the only permitted fallback. Never reconstruct a CDN URL.
    let downloadUrl = file.downloadUrl;
    if (downloadUrl === null || downloadUrl === undefined || downloadUrl === "")
      downloadUrl = data(await api(`/v1/mods/${file.modId}/files/${route.fileId}/download-url`));
    if (downloadUrl === null || downloadUrl === undefined || downloadUrl === "") throw new ServiceError(403, "author_distribution_unavailable");
    const source = approvedCdn(downloadUrl);
    const digest = new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(sourceIdentityText(source))));
    const sourceHash = Array.from(digest, x => x.toString(16).padStart(2, "0")).join("");
    if (sourceHash !== route.sourceSha256) throw new ServiceError(409, "source_identity_changed");
    const response = await cdn(source, Number(file.fileLength));
    const result = streamFile(response, Number(file.fileLength), operation.signal, deps.key!, finish);
    streaming = true;
    return result;
  } catch (error) {
    const known = error instanceof ServiceError ? error : new ServiceError(502, "service_unavailable");
    const response = jsonResponse({ error: known.code, protocolVersion: PROTOCOL_VERSION }, known.status);
    if (known.code === "client_rate_limited" || known.code === "service_concurrency_reached") response.headers.set("retry-after", "5");
    return response;
  } finally { if (!streaming) finish?.(); }
}
