/**
 * The versioned wire contract between ChunkPilot and its external reachability probe.
 *
 * Nothing in this file knows about Cloudflare. It is the shape both sides agree on, and the desktop
 * client validates every field of a response against it before a result is allowed to mean anything.
 */

/** Bumped only for a breaking change. A response carrying another value is rejected by the client. */
export const API_VERSION = 1;

/** The only route the service answers. Anything else is a typed invalid_request. */
export const PROBE_PATH = "/v1/probe";

/** A probe request is four small fields; anything larger is refused before it is parsed. */
export const MAX_REQUEST_BYTES = 512;

/**
 * How long one outbound TCP connection attempt may take. The Workers socket API documents no
 * connection timeout of its own, so the service imposes this one: no probe may wait unbounded.
 */
export const CONNECT_TIMEOUT_MS = 5_000;

/** 128 bits of client randomness, lowercase hex. Correlation only — never a credential. */
export const REQUEST_ID_PATTERN = /^[0-9a-f]{32}$/;

/**
 * Cloudflare prohibits outbound connections to port 25. Refusing it here makes the rejection a typed
 * contract result instead of a provider exception.
 */
export const PROHIBITED_PORTS: readonly number[] = [25];

/** What the service is able to say. Deliberately small, and never a guess at a root cause. */
export type ProbeResultCode =
  | "reachable"
  | "unreachable"
  | "source_mismatch"
  | "unsupported_address_family"
  | "invalid_request"
  | "rate_limited"
  | "probe_error";

/** Why a request was refused before any decision about the network was made. */
export type InvalidRequestReason =
  | "method"
  | "path"
  | "content_type"
  | "body_too_large"
  | "malformed_json"
  | "api_version"
  | "request_id"
  | "expected_address"
  | "port";

export type AddressFamily = "ipv4" | "ipv6" | "unknown";

export interface ProbeRequestBody {
  apiVersion: number;
  requestId: string;
  expectedAddress: string;
  port: number;
}

export interface ProbeResponseBody {
  apiVersion: number;
  /** Echoed back so a late response can never be applied to a newer check. Empty when unparsed. */
  requestId: string;
  result: ProbeResultCode;
  /**
   * The public address the service actually saw this request arrive from — the caller's own address
   * and the only address a socket is ever opened to. Empty when no socket decision was reached.
   */
  observedAddress: string;
  observedFamily: AddressFamily;
  port: number;
  checkedAt: string;
  connectMilliseconds: number | null;
  reason?: InvalidRequestReason;
}

/** HTTP status for each typed result. The body is always the authority; the status mirrors it. */
export function statusForResult(result: ProbeResultCode): number {
  switch (result) {
    case "invalid_request":
      return 400;
    case "rate_limited":
      return 429;
    case "probe_error":
      return 503;
    default:
      return 200;
  }
}
