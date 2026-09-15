import { BudgetLedger } from "./budget.ts";
import { handleCurseForge } from "./handler.ts";
import { jsonResponse, type Budget, type RateLimiter } from "./contract.ts";

export interface Env {
  CURSEFORGE_API_KEY?: string;
  CURSEFORGE_RATE_LIMITER?: RateLimiter;
  CURSEFORGE_BUDGET?: DurableObjectNamespace;
  DAILY_UPSTREAM_REQUESTS?: string;
  DAILY_UPSTREAM_BYTES?: string;
  MAX_CONCURRENT_OPERATIONS?: string;
}

/** Bound to a distinct SQLite namespace; this object is never routed to the public internet. */
export class CurseForgeBudget {
  readonly ledger: BudgetLedger;
  constructor(state: DurableObjectState, env: Env) {
    this.ledger = new BudgetLedger(state.storage, {
      dailyRequests: Number(env.DAILY_UPSTREAM_REQUESTS),
      dailyBytes: Number(env.DAILY_UPSTREAM_BYTES),
      concurrent: Number(env.MAX_CONCURRENT_OPERATIONS),
    });
  }

  async fetch(request: Request): Promise<Response> {
    try {
      if (request.method !== "POST" || request.headers.get("content-type") !== "application/json") return jsonResponse({ allowed: false }, 400);
      // The adapter produces a tiny fixed document. Never accept an unbounded internal body either.
      const length = request.headers.get("content-length");
      if (length && (!/^[0-9]{1,4}$/.test(length) || Number(length) > 512)) return jsonResponse({ allowed: false }, 400);
      if (!request.body) return jsonResponse({ allowed: false }, 400);
      const reader = request.body.getReader();
      let text = "";
      let bytes = 0;
      const decoder = new TextDecoder("utf-8", { fatal: true, ignoreBOM: false });
      try {
        for (;;) {
          const next = await reader.read();
          if (next.done) break;
          bytes += next.value.byteLength;
          if (bytes > 512) return jsonResponse({ allowed: false }, 400);
          text += decoder.decode(next.value, { stream: true });
        }
        text += decoder.decode();
      } finally { void reader.cancel().catch(() => {}); reader.releaseLock(); }
      const body = JSON.parse(text) as { id?: unknown; expiresAt?: unknown; bytes?: unknown };
      if (typeof body.id !== "string") return jsonResponse({ allowed: false }, 400);
      const route = new URL(request.url).pathname;
      if (route === "/acquire" && typeof body.expiresAt === "number") return jsonResponse({ allowed: this.ledger.acquire(body.id, body.expiresAt, Date.now()) });
      if (route === "/reserve" && typeof body.bytes === "number") return jsonResponse({ allowed: this.ledger.reserve(body.id, body.bytes, Date.now()) });
      if (route === "/release") { this.ledger.release(body.id); return jsonResponse({ allowed: true }); }
      return jsonResponse({ allowed: false }, 400);
    } catch { return jsonResponse({ allowed: false }, 503); }
  }
}

function budgetAdapter(namespace: DurableObjectNamespace): Budget {
  // One name, not an IP/project-derived shard: all Worker locations share the same hard quota.
  const stub = namespace.get(namespace.idFromName("curseforge-global-capacity-v1"));
  const invoke = async (path: string, payload: unknown): Promise<boolean> => {
    const response = await stub.fetch(`https://budget.internal/${path}`, {
      method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(payload),
    });
    if (response.status !== 200) return false;
    return ((await response.json()) as { allowed?: unknown }).allowed === true;
  };
  return {
    acquire: (id, expiresAt) => invoke("acquire", { id, expiresAt }),
    reserve: (id, bytes) => invoke("reserve", { id, bytes }),
    release: async id => { await invoke("release", { id }); },
  };
}

export default {
  fetch(request: Request, env: Env, context: ExecutionContext): Promise<Response> {
    return handleCurseForge(request, {
      key: env.CURSEFORGE_API_KEY,
      limiter: env.CURSEFORGE_RATE_LIMITER,
      budget: env.CURSEFORGE_BUDGET ? budgetAdapter(env.CURSEFORGE_BUDGET) : null,
      fetch: (url, init) => fetch(url, init),
      waitUntil: promise => context.waitUntil(promise),
    });
  },
};
