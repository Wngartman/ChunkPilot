import {
  API_VERSION,
  CONNECT_TIMEOUT_MS,
  MAX_REQUEST_BYTES,
  PROBE_PATH,
  PROHIBITED_PORTS,
  REQUEST_ID_PATTERN,
  statusForResult,
  type AddressFamily,
  type InvalidRequestReason,
  type ProbeRequestBody,
  type ProbeResponseBody,
  type ProbeResultCode,
} from "./contract.ts";
import { isGloballyRoutableIpv4, normalizeAddress, type NormalizedAddress } from "./addresses.ts";

/**
 * The whole service, with the network and the clock injected.
 *
 * ## The one security invariant
 *
 * The destination of every outbound socket is the address Cloudflare observed this HTTPS request
 * arriving from. `expectedAddress` is compared with it and is *never* used as a target: if the two
 * differ, no socket is opened at all. There is no hostname input, no DNS input, no redirect target
 * and no way for a caller to name a host it is not already speaking from. That is what stops this
 * from being a public port scanner, and it is a property of the control flow below rather than of a
 * blocklist.
 */

/** What one bounded TCP connection attempt learned. Never an exception message. */
export interface TcpConnectResult {
  /** `connected` and `refused`/`timeout` are answers about the network. `error` is the service. */
  kind: "connected" | "refused" | "timeout" | "error";
  milliseconds: number | null;
}

export interface TcpConnector {
  (hostname: string, port: number, timeoutMs: number): Promise<TcpConnectResult>;
}

/** The shape of Cloudflare's Rate Limiting binding, narrowed to what this service uses. */
export interface RateLimiter {
  limit(options: { key: string }): Promise<{ success: boolean }>;
}

export interface ProbeDependencies {
  connect: TcpConnector;
  limiter?: RateLimiter | null;
  now?: () => Date;
  connectTimeoutMs?: number;
}

export async function handleProbe(request: Request, deps: ProbeDependencies): Promise<Response> {
  const now = deps.now ?? (() => new Date());
  const checkedAt = () => now().toISOString();

  if (request.method !== "POST") return invalid("method", "", 0, checkedAt());

  let path: string;
  try {
    path = new URL(request.url).pathname;
  } catch {
    return invalid("path", "", 0, checkedAt());
  }
  if (path !== PROBE_PATH) return invalid("path", "", 0, checkedAt());

  const contentType = (request.headers.get("content-type") ?? "").split(";")[0]!.trim().toLowerCase();
  if (contentType !== "application/json") return invalid("content_type", "", 0, checkedAt());

  const declaredLength = Number(request.headers.get("content-length") ?? "");
  if (Number.isFinite(declaredLength) && declaredLength > MAX_REQUEST_BYTES)
    return invalid("body_too_large", "", 0, checkedAt());

  const body = await readBounded(request, MAX_REQUEST_BYTES);
  if (body === null) return invalid("body_too_large", "", 0, checkedAt());

  const parsed = parseRequestBody(body);
  if ("reason" in parsed)
    return invalid(parsed.reason, parsed.requestId, parsed.port, checkedAt());
  const probe = parsed.value;

  // The trusted source of truth for who is calling. Cloudflare sets this header at its own edge for
  // every request that reaches a Worker, so — unlike an origin behind a CDN — there is no path by
  // which a caller can supply it. Everything below derives the target from this value alone.
  const observed = normalizeAddress(request.headers.get("cf-connecting-ip"));

  if (deps.limiter) {
    let allowed: boolean;
    try {
      const outcome = await deps.limiter.limit({ key: observed.value.length > 0 ? observed.value : "unknown" });
      allowed = outcome.success;
    } catch {
      // A limiter that cannot answer must not become a free pass, and must not be reported as a
      // network result either.
      return respond(
        {
          result: "probe_error",
          requestId: probe.requestId,
          observedAddress: "",
          observedFamily: observed.family,
          port: probe.port,
        },
        checkedAt(),
      );
    }
    if (!allowed)
      return respond(
        {
          result: "rate_limited",
          requestId: probe.requestId,
          observedAddress: "",
          observedFamily: observed.family,
          port: probe.port,
        },
        checkedAt(),
      );
  }

  // An IPv6 caller, a caller behind something that presents a non-routable source, or an address
  // this service cannot parse. None of them says anything about the Minecraft server, so none of
  // them is reported as unreachable, and no socket is opened.
  if (!isGloballyRoutableIpv4(observed))
    return respond(
      {
        result: "unsupported_address_family",
        requestId: probe.requestId,
        observedAddress: observed.family === "ipv6" ? observed.value : "",
        observedFamily: observed.family,
        port: probe.port,
      },
      checkedAt(),
    );

  // The gate. Not a filter on the target — the decision whether a target exists at all.
  if (observed.value !== probe.expectedAddress)
    return respond(
      {
        result: "source_mismatch",
        requestId: probe.requestId,
        observedAddress: observed.value,
        observedFamily: observed.family,
        port: probe.port,
      },
      checkedAt(),
    );

  const timeoutMs = deps.connectTimeoutMs ?? CONNECT_TIMEOUT_MS;
  let attempt: TcpConnectResult;
  try {
    attempt = await deps.connect(observed.value, probe.port, timeoutMs);
  } catch {
    attempt = { kind: "error", milliseconds: null };
  }

  const result: ProbeResultCode =
    attempt.kind === "connected" ? "reachable" : attempt.kind === "error" ? "probe_error" : "unreachable";
  return respond(
    {
      result,
      requestId: probe.requestId,
      observedAddress: observed.value,
      observedFamily: observed.family,
      port: probe.port,
      connectMilliseconds: attempt.kind === "connected" ? attempt.milliseconds : null,
    },
    checkedAt(),
  );
}

type ParsedRequest =
  | { value: ProbeRequestBody }
  | { reason: InvalidRequestReason; requestId: string; port: number };

/**
 * Strict field validation. Every rejection names one bounded reason; none of them describes the
 * network, because at this point nothing about the network has been looked at.
 */
export function parseRequestBody(text: string): ParsedRequest {
  let raw: unknown;
  try {
    raw = JSON.parse(text);
  } catch {
    return { reason: "malformed_json", requestId: "", port: 0 };
  }
  if (raw === null || typeof raw !== "object" || Array.isArray(raw))
    return { reason: "malformed_json", requestId: "", port: 0 };
  const candidate = raw as Record<string, unknown>;

  if (candidate.apiVersion !== API_VERSION) return { reason: "api_version", requestId: "", port: 0 };

  const requestId = candidate.requestId;
  if (typeof requestId !== "string" || !REQUEST_ID_PATTERN.test(requestId))
    return { reason: "request_id", requestId: "", port: 0 };

  const port = candidate.port;
  if (
    typeof port !== "number" ||
    !Number.isInteger(port) ||
    port < 1 ||
    port > 65535 ||
    PROHIBITED_PORTS.includes(port)
  )
    return { reason: "port", requestId, port: 0 };

  const expected = normalizeAddress(
    typeof candidate.expectedAddress === "string" ? candidate.expectedAddress : null,
  );
  if (!isGloballyRoutableIpv4(expected)) return { reason: "expected_address", requestId, port };

  return { value: { apiVersion: API_VERSION, requestId, expectedAddress: expected.value, port } };
}

/** Reads at most `max` bytes and refuses anything longer, rather than buffering what arrives. */
async function readBounded(request: Request, max: number): Promise<string | null> {
  if (request.body === null) return "";
  const reader = request.body.getReader();
  const chunks: Uint8Array[] = [];
  let total = 0;
  try {
    for (;;) {
      const { done, value } = await reader.read();
      if (done) break;
      if (value) {
        total += value.byteLength;
        if (total > max) return null;
        chunks.push(value);
      }
    }
  } finally {
    try {
      reader.releaseLock();
    } catch {
      /* the stream is already finished */
    }
  }
  const buffer = new Uint8Array(total);
  let offset = 0;
  for (const chunk of chunks) {
    buffer.set(chunk, offset);
    offset += chunk.byteLength;
  }
  return new TextDecoder().decode(buffer);
}

function invalid(
  reason: InvalidRequestReason,
  requestId: string,
  port: number,
  checkedAt: string,
): Response {
  return respond(
    { result: "invalid_request", requestId, observedAddress: "", observedFamily: "unknown", port, reason },
    checkedAt,
  );
}

function respond(
  fields: {
    result: ProbeResultCode;
    requestId: string;
    observedAddress: string;
    observedFamily: AddressFamily;
    port: number;
    connectMilliseconds?: number | null;
    reason?: InvalidRequestReason;
  },
  checkedAt: string,
): Response {
  const body: ProbeResponseBody = {
    apiVersion: API_VERSION,
    requestId: fields.requestId,
    result: fields.result,
    observedAddress: fields.observedAddress,
    observedFamily: fields.observedFamily,
    port: fields.port,
    checkedAt,
    connectMilliseconds: fields.connectMilliseconds ?? null,
  };
  if (fields.reason) body.reason = fields.reason;
  return new Response(JSON.stringify(body), {
    status: statusForResult(fields.result),
    headers: {
      "content-type": "application/json; charset=utf-8",
      // Point-in-time evidence must never be served from a cache, anywhere.
      "cache-control": "no-store",
      "x-content-type-options": "nosniff",
      // No Access-Control-Allow-Origin: the desktop application is the client, not a browser.
    },
  });
}

export { normalizeAddress };
export type { NormalizedAddress };
