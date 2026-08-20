import assert from "node:assert/strict";
import { test } from "node:test";
import { API_VERSION, MAX_REQUEST_BYTES, PROBE_PATH, type ProbeResponseBody } from "../src/contract.ts";
import { handleProbe, type ProbeDependencies, type TcpConnector, type TcpConnectResult } from "../src/handler.ts";

const CALLER = "93.184.216.34";
const OTHER = "198.41.128.9";
const REQUEST_ID = "0f1e2d3c4b5a69788796a5b4c3d2e1f0";
const CLOCK = new Date("2026-08-08T19:42:00.000Z");

/** Records every connection attempt so a test can prove that none was made. */
class RecordingConnector {
  readonly attempts: Array<{ hostname: string; port: number; timeoutMs: number }> = [];
  readonly result: TcpConnectResult;
  readonly thrown: Error | null;

  constructor(outcome: TcpConnectResult | Error = { kind: "connected", milliseconds: 42 }) {
    this.thrown = outcome instanceof Error ? outcome : null;
    this.result = outcome instanceof Error ? { kind: "error", milliseconds: null } : outcome;
  }

  readonly connect: TcpConnector = async (hostname, port, timeoutMs) => {
    this.attempts.push({ hostname, port, timeoutMs });
    if (this.thrown !== null) throw this.thrown;
    return this.result;
  };
}

function probeRequest(overrides: Record<string, unknown> = {}, init: RequestInit & { ip?: string | null } = {}) {
  const { ip = CALLER, headers: extraHeaders, ...rest } = init;
  const body =
    "rawBody" in overrides
      ? String(overrides.rawBody)
      : JSON.stringify({ apiVersion: API_VERSION, requestId: REQUEST_ID, expectedAddress: CALLER, port: 25566, ...overrides });
  const headers = new Headers({
    "content-type": "application/json",
    ...(extraHeaders as Record<string, string> | undefined),
  });
  if (ip !== null) headers.set("cf-connecting-ip", ip);
  return new Request(`https://probe.example.workers.dev${PROBE_PATH}`, {
    method: "POST",
    body,
    ...rest,
    headers,
  });
}

async function run(
  request: Request,
  connector = new RecordingConnector(),
  extra: Partial<ProbeDependencies> = {},
): Promise<{ response: Response; body: ProbeResponseBody; connector: RecordingConnector }> {
  const response = await handleProbe(request, { connect: connector.connect, now: () => CLOCK, ...extra });
  return { response, body: (await response.clone().json()) as ProbeResponseBody, connector };
}

// ── The success path ──

test("a matching source and a reachable port answer reachable", async () => {
  const { response, body, connector } = await run(probeRequest());

  assert.equal(response.status, 200);
  assert.equal(body.result, "reachable");
  assert.equal(body.apiVersion, API_VERSION);
  assert.equal(body.requestId, REQUEST_ID);
  assert.equal(body.observedAddress, CALLER);
  assert.equal(body.observedFamily, "ipv4");
  assert.equal(body.port, 25566);
  assert.equal(body.connectMilliseconds, 42);
  assert.equal(body.checkedAt, CLOCK.toISOString());
  assert.deepEqual(connector.attempts, [{ hostname: CALLER, port: 25566, timeoutMs: 5000 }]);
});

test("a refused connection is unreachable and claims no cause", async () => {
  const connector = new RecordingConnector({ kind: "refused", milliseconds: null });
  const { response, body } = await run(probeRequest(), connector);

  assert.equal(response.status, 200);
  assert.equal(body.result, "unreachable");
  assert.equal(body.connectMilliseconds, null);
});

test("a connection that times out is unreachable, not an error", async () => {
  const connector = new RecordingConnector({ kind: "timeout", milliseconds: null });
  const { body } = await run(probeRequest(), connector);

  assert.equal(body.result, "unreachable");
});

test("a runtime that refuses to open the socket is a probe error, never an unreachable server", async () => {
  const connector = new RecordingConnector({ kind: "error", milliseconds: null });
  const { response, body } = await run(probeRequest(), connector);

  assert.equal(response.status, 503);
  assert.equal(body.result, "probe_error");
});

test("an exception thrown by the connector never escapes as a provider message", async () => {
  const connector = new RecordingConnector(new Error("TCP Loop detected"));
  const { body } = await run(probeRequest(), connector);

  assert.equal(body.result, "probe_error");
  assert.equal(JSON.stringify(body).includes("TCP Loop"), false);
});

// ── The anti-scanning boundary ──

test("a source mismatch opens no socket at all", async () => {
  const { response, body, connector } = await run(probeRequest({ expectedAddress: OTHER }));

  assert.equal(response.status, 200);
  assert.equal(body.result, "source_mismatch");
  assert.equal(body.observedAddress, CALLER);
  assert.deepEqual(connector.attempts, []);
});

test("the target is always the observed source, never the address the caller supplied", async () => {
  // Even when the supplied address is itself a perfectly valid public host, it is only ever compared.
  const connector = new RecordingConnector();
  await run(probeRequest({ expectedAddress: OTHER }), connector);
  assert.equal(connector.attempts.length, 0);

  await run(probeRequest({ expectedAddress: CALLER }), connector);
  assert.deepEqual(
    connector.attempts.map((attempt) => attempt.hostname),
    [CALLER],
  );
});

test("there is no hostname, DNS or URL target input", async () => {
  for (const target of ["victim.example.com", "localhost", "https://victim.example.com", "127.0.0.1"]) {
    const { body, connector } = await run(probeRequest({ expectedAddress: target }));
    assert.equal(body.result, "invalid_request", target);
    assert.equal(body.reason, "expected_address", target);
    assert.deepEqual(connector.attempts, [], target);
  }
});

test("a private or reserved expected address is refused before anything is observed", async () => {
  for (const target of ["10.0.0.140", "192.168.1.50", "100.64.0.1", "169.254.1.1", "0.0.0.0", "224.0.0.1"]) {
    const { body, connector } = await run(probeRequest({ expectedAddress: target }));
    assert.equal(body.reason, "expected_address", target);
    assert.deepEqual(connector.attempts, [], target);
  }
});

test("a caller whose own observed address is not a routable IPv4 gets no socket", async () => {
  const { body, connector } = await run(probeRequest({}, { ip: "10.0.0.140" }));

  assert.equal(body.result, "unsupported_address_family");
  assert.deepEqual(connector.attempts, []);
});

// ── Address family ──

test("an IPv6 caller is an address-family mismatch, not an unreachable server", async () => {
  const { response, body, connector } = await run(probeRequest({}, { ip: "2001:db8::1" }));

  assert.equal(response.status, 200);
  assert.equal(body.result, "unsupported_address_family");
  assert.equal(body.observedFamily, "ipv6");
  assert.equal(body.observedAddress, "2001:db8::1");
  assert.deepEqual(connector.attempts, []);
});

test("a missing source address is unsupported rather than assumed", async () => {
  const { body, connector } = await run(probeRequest({}, { ip: null }));

  assert.equal(body.result, "unsupported_address_family");
  assert.equal(body.observedFamily, "unknown");
  assert.deepEqual(connector.attempts, []);
});

test("an IPv4-mapped IPv6 source still matches its IPv4 expectation", async () => {
  const { body, connector } = await run(probeRequest({}, { ip: `::ffff:${CALLER}` }));

  assert.equal(body.result, "reachable");
  assert.deepEqual(
    connector.attempts.map((attempt) => attempt.hostname),
    [CALLER],
  );
});

// ── Request validation ──

test("only POST is answered", async () => {
  for (const method of ["GET", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS"]) {
    const request = new Request(`https://probe.example.workers.dev${PROBE_PATH}`, { method });
    const { response, body, connector } = await run(request);
    assert.equal(response.status, 400, method);
    assert.equal(body.reason, "method", method);
    assert.deepEqual(connector.attempts, [], method);
  }
});

test("only the versioned probe path is answered", async () => {
  for (const path of ["/", "/probe", "/v2/probe", "/v1/probe/extra"]) {
    const request = new Request(`https://probe.example.workers.dev${path}`, {
      method: "POST",
      headers: { "content-type": "application/json", "cf-connecting-ip": CALLER },
      body: "{}",
    });
    const { body } = await run(request);
    assert.equal(body.reason, "path", path);
  }
});

test("only JSON is accepted", async () => {
  for (const type of ["text/plain", "application/x-www-form-urlencoded", "", "application/xml"]) {
    const { body } = await run(probeRequest({}, { headers: { "content-type": type } }));
    assert.equal(body.reason, "content_type", type || "(none)");
  }
});

test("a declared oversized body is refused without being read", async () => {
  const { response, body } = await run(
    probeRequest({}, { headers: { "content-length": String(MAX_REQUEST_BYTES + 1) } }),
  );

  assert.equal(response.status, 400);
  assert.equal(body.reason, "body_too_large");
});

test("an undeclared oversized body is refused while it streams", async () => {
  const oversized = JSON.stringify({
    apiVersion: API_VERSION,
    requestId: REQUEST_ID,
    expectedAddress: CALLER,
    port: 25566,
    padding: "x".repeat(MAX_REQUEST_BYTES * 4),
  });
  const { body, connector } = await run(probeRequest({ rawBody: oversized }));

  assert.equal(body.reason, "body_too_large");
  assert.deepEqual(connector.attempts, []);
});

test("malformed JSON is a typed rejection", async () => {
  for (const raw of ["", "not json", "[]", "null", '"text"', "{"]) {
    const { body } = await run(probeRequest({ rawBody: raw }));
    assert.equal(body.reason, "malformed_json", JSON.stringify(raw));
  }
});

test("a missing or wrong API version is refused", async () => {
  for (const version of [undefined, 0, 2, "1", null]) {
    const { body } = await run(probeRequest({ apiVersion: version }));
    assert.equal(body.reason, "api_version", String(version));
  }
});

test("the correlation id must be 128 bits of lowercase hex", async () => {
  for (const id of [undefined, "", "short", REQUEST_ID.toUpperCase(), `${REQUEST_ID}0`, 12345, "../../etc/passwd"]) {
    const { body } = await run(probeRequest({ requestId: id }));
    assert.equal(body.reason, "request_id", String(id));
  }
});

test("the port must be a whole TCP port, and never port 25", async () => {
  for (const port of [undefined, 0, -1, 65536, 1.5, "25566", null, 25]) {
    const { body, connector } = await run(probeRequest({ port }));
    assert.equal(body.reason, "port", String(port));
    assert.deepEqual(connector.attempts, [], String(port));
  }
});

test("a missing expected address is refused", async () => {
  const { body } = await run(probeRequest({ expectedAddress: undefined }));
  assert.equal(body.reason, "expected_address");
});

test("an invalid request never reflects the caller's address back", async () => {
  const { body } = await run(probeRequest({ port: 0 }));
  assert.equal(body.observedAddress, "");
  assert.equal(body.observedFamily, "unknown");
});

// ── Rate limiting ──

test("the rate limiter refusing a request is typed, and opens no socket", async () => {
  const connector = new RecordingConnector();
  const { response, body } = await run(probeRequest(), connector, {
    limiter: { limit: async () => ({ success: false }) },
  });

  assert.equal(response.status, 429);
  assert.equal(body.result, "rate_limited");
  assert.equal(body.requestId, REQUEST_ID);
  assert.deepEqual(connector.attempts, []);
});

test("the rate limiter is keyed on the observed source address", async () => {
  const keys: string[] = [];
  await run(probeRequest(), new RecordingConnector(), {
    limiter: {
      limit: async ({ key }) => {
        keys.push(key);
        return { success: true };
      },
    },
  });

  assert.deepEqual(keys, [CALLER]);
});

test("a rate limiter that fails is a probe error rather than a free pass", async () => {
  const connector = new RecordingConnector();
  const { body } = await run(probeRequest(), connector, {
    limiter: {
      limit: async () => {
        throw new Error("binding unavailable");
      },
    },
  });

  assert.equal(body.result, "probe_error");
  assert.deepEqual(connector.attempts, []);
});

// ── Response shape ──

test("every response is uncacheable, JSON, and carries no CORS grant", async () => {
  for (const request of [probeRequest(), probeRequest({ port: 0 }), probeRequest({ expectedAddress: OTHER })]) {
    const { response } = await run(request);
    assert.equal(response.headers.get("cache-control"), "no-store");
    assert.equal(response.headers.get("content-type"), "application/json; charset=utf-8");
    assert.equal(response.headers.get("x-content-type-options"), "nosniff");
    assert.equal(response.headers.get("access-control-allow-origin"), null);
  }
});

test("the correlation id is echoed on every result that parsed one", async () => {
  const cases = [probeRequest(), probeRequest({ expectedAddress: OTHER }), probeRequest({}, { ip: "2001:db8::1" })];
  for (const request of cases) {
    const { body } = await run(request);
    assert.equal(body.requestId, REQUEST_ID);
  }
});

/**
 * The desktop client refuses a Reachable answer that does not carry all of these, so they are part of
 * the contract rather than incidental. A change here is a breaking change.
 */
test("a reachable answer always carries the fields the client requires of it", async () => {
  const { response, body } = await run(probeRequest(), new RecordingConnector({ kind: "connected", milliseconds: 0 }));

  assert.equal(response.status, 200);
  assert.equal(body.result, "reachable");
  assert.equal(body.observedFamily, "ipv4");
  assert.equal(body.observedAddress, CALLER);
  assert.equal(typeof body.connectMilliseconds, "number");
  assert.ok(Number.isFinite(body.connectMilliseconds!) && body.connectMilliseconds! >= 0);
});

test("every result is served with exactly the status the contract pairs with it", async () => {
  const expected: Array<[Promise<{ response: Response; body: ProbeResponseBody }>, number]> = [
    [run(probeRequest()), 200],
    [run(probeRequest(), new RecordingConnector({ kind: "refused", milliseconds: null })), 200],
    [run(probeRequest({ expectedAddress: OTHER })), 200],
    [run(probeRequest({}, { ip: "2001:db8::1" })), 200],
    [run(probeRequest({ port: 0 })), 400],
    [run(probeRequest(), new RecordingConnector({ kind: "error", milliseconds: null })), 503],
  ];
  for (const [pending, status] of expected) {
    const { response } = await pending;
    assert.equal(response.status, status);
  }

  const limited = await handleProbe(probeRequest(), {
    connect: new RecordingConnector().connect,
    now: () => CLOCK,
    limiter: { limit: async () => ({ success: false }) },
  });
  assert.equal(limited.status, 429);
});

test("the connect timeout handed to the network is always bounded", async () => {
  const connector = new RecordingConnector();
  await run(probeRequest(), connector);
  await run(probeRequest(), connector, { connectTimeoutMs: 1500 });

  assert.deepEqual(
    connector.attempts.map((attempt) => attempt.timeoutMs),
    [5000, 1500],
  );
  for (const attempt of connector.attempts) assert.ok(attempt.timeoutMs > 0 && attempt.timeoutMs <= 10_000);
});
