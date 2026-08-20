import { connect } from "cloudflare:sockets";
import { handleProbe, type RateLimiter, type TcpConnector } from "./handler.ts";

/**
 * The Cloudflare adapter, and the only file that knows Cloudflare exists.
 *
 * Everything the service decides lives in `handler.ts` and is tested there against a fake connector,
 * so no test ever opens a socket to the public internet.
 */

export interface Env {
  /** Cloudflare Rate Limiting binding. Optional so a local `wrangler dev` without it still runs. */
  PROBE_RATE_LIMITER?: RateLimiter;
}

/**
 * One bounded TCP handshake and nothing else.
 *
 * On success the socket is closed immediately: no bytes are written, no bytes are read, and no
 * Minecraft protocol is spoken. A completed handshake is the entire evidence this service produces,
 * which is what keeps the check independent of the Minecraft version the server happens to run.
 */
const tcpConnector: TcpConnector = async (hostname, port, timeoutMs) => {
  const started = Date.now();
  let socket: ReturnType<typeof connect>;
  try {
    socket = connect({ hostname, port }, { secureTransport: "off", allowHalfOpen: false });
  } catch {
    // The runtime refused to create the socket at all. That is the service failing, not the
    // network answering, and it must never be reported as an unreachable server.
    return { kind: "error", milliseconds: null };
  }

  // Both promises are consumed here so neither can surface as an unhandled rejection.
  const opened = socket.opened.then(
    () => "connected" as const,
    () => "refused" as const,
  );
  socket.closed.catch(() => {
    /* the close reason adds nothing the handshake has not already answered */
  });

  let timer: ReturnType<typeof setTimeout> | undefined;
  const expired = new Promise<"timeout">((resolve) => {
    timer = setTimeout(() => resolve("timeout"), timeoutMs);
  });

  try {
    const outcome = await Promise.race([opened, expired]);
    if (outcome === "connected") return { kind: "connected", milliseconds: Math.max(0, Date.now() - started) };
    return { kind: outcome, milliseconds: null };
  } finally {
    if (timer !== undefined) clearTimeout(timer);
    // Deliberately not awaited: closing a socket that never opened must not become the unbounded
    // wait the timeout above exists to prevent.
    void Promise.resolve(socket.close()).catch(() => {
      /* already closed */
    });
  }
};

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    return handleProbe(request, {
      connect: tcpConnector,
      limiter: env.PROBE_RATE_LIMITER ?? null,
    });
  },
};
