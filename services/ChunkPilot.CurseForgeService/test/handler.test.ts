import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { test } from "node:test";
import { API_ORIGIN, MAX_FILE_BYTES, MAX_IMAGE_BYTES, MAX_JSON_BYTES, METADATA_TIMEOUT_MS, type Budget, type Dependencies } from "../src/contract.ts";
import { handleCurseForge } from "../src/handler.ts";
import { sourceIdentityText } from "../src/routes.ts";

const KEY = "synthetic-credential-only-for-offline-fixtures-23957";
const SOURCE = "https://edge.forgecdn.net/files/1/456/Test%20Pack.zip";
const PROJECT = { id: 123, gameId: 432, isAvailable: true, allowModDistribution: true, logo: { thumbnailUrl: "https://media.forgecdn.net/avatars/1/2/logo.png" } };
const FILE = { id: 1456, modId: 123, gameId: 432, isAvailable: true, fileStatus: 4, fileLength: 3,
  downloadUrl: SOURCE, hashes: [{ algo: 1, value: "a".repeat(40) }] };
const sourceHash = (source = SOURCE) => createHash("sha256").update(sourceIdentityText(source)).digest("hex");
const downloadPath = (hash = sourceHash()) => "/downloads/1456?sourceSha256=" + hash;
const json = (value: unknown, headers: Record<string, string> = {}) => new Response(JSON.stringify(value), { headers: { "content-type": "application/json", ...headers } });
const request = (path: string, init: RequestInit = {}) => new Request("https://gateway.example" + path, { ...init,
  headers: { "cf-connecting-ip": "203.0.113.7", "x-api-key": "untrusted-caller-key", "authorization": "untrusted", "cookie": "untrusted", ...(init.headers as Record<string, string>) } });

function fixture(options: { project?: unknown; file?: unknown; image?: Response; cdn?: () => Response; respond?: (url: string, init: RequestInit) => Promise<Response> | Response } = {}) {
  const calls: Array<{ url: string; init: RequestInit }> = [];
  const reserves: number[] = [];
  const releases: string[] = [];
  const lifetimes: Promise<unknown>[] = [];
  const clients: string[] = [];
  const leases: Array<{ id: string; expires: number }> = [];
  const budget: Budget = {
    acquire: async (id, expires) => { leases.push({ id, expires }); return true; },
    reserve: async (_id, bytes) => { reserves.push(bytes); return true; },
    release: async id => { releases.push(id); },
  };
  const deps: Dependencies = {
    key: KEY, budget, now: () => 1000,
    limiter: { limit: async ({ key }) => { clients.push(key); return { success: true }; } },
    waitUntil: promise => { lifetimes.push(promise); },
    fetch: async (url, init) => {
      calls.push({ url, init });
      if (options.respond) return options.respond(url, init);
      if (url === API_ORIGIN + "/v1/mods/files") return json({ data: [options.file ?? FILE] });
      if (url === API_ORIGIN + "/v1/mods/123") return json({ data: options.project ?? PROJECT });
      if (url === API_ORIGIN + "/v1/mods/123/files/1456/download-url") return json({ data: SOURCE });
      if (url === API_ORIGIN + "/v1/mods/123/files/1456") return json({ data: options.file ?? FILE });
      if (url === API_ORIGIN + "/v1/mods/123/files?pageSize=50") return json({ data: [options.file ?? FILE] });
      if (url === API_ORIGIN + "/v1/games/432") return json({ data: { id: 432 } });
      if (url.startsWith(API_ORIGIN + "/v1/minecraft/version")) return json({ data: [{ versionString: "1.21.1" }] });
      if (url.startsWith(API_ORIGIN + "/v1/mods/search")) return json({ data: [options.project ?? PROJECT], pagination: { resultCount: 1 } });
      if (url.startsWith("https://media.forgecdn.net/")) return options.image ?? new Response("PNG", { headers: { "content-type": "image/png" } });
      return options.cdn?.() ?? new Response("ZIP", { headers: { "content-length": "3", "content-type": "application/zip", "x-api-key": KEY, "set-cookie": KEY } });
    },
  };
  return { deps, calls, reserves, releases, lifetimes, clients, leases };
}
function assertNoSecret(response: Response, text: string): void {
  assert.equal(text.includes(KEY), false);
  assert.equal(JSON.stringify([...response.headers]).includes(KEY), false);
  assert.equal(response.headers.get("cache-control"), "no-store");
}

test("metadata preserves official sources but forwards only fixed headers and retains quota release", async () => {
  const f = fixture();
  const response = await handleCurseForge(request("/v1/mods/123/files/1456"), f.deps);
  assert.equal(response.status, 200);
  const text = await response.text();
  assert.equal(JSON.parse(text).data.downloadUrl, SOURCE);
  assertNoSecret(response, text);
  assert.deepEqual(f.reserves, [MAX_JSON_BYTES, MAX_JSON_BYTES]);
  assert.deepEqual(f.clients, ["203.0.113.7"]);
  for (const call of f.calls) {
    const headers = new Headers(call.init.headers);
    assert.equal(headers.get("x-api-key"), KEY);
    assert.equal(headers.has("authorization"), false);
    assert.equal(headers.has("cookie"), false);
    assert.equal(headers.has("cf-connecting-ip"), false);
    assert.equal(call.init.redirect, "manual");
  }
  await Promise.all(f.lifetimes);
  assert.equal(f.releases.length, 1);
  assert.equal(f.leases[0]!.expires, 1000 + METADATA_TIMEOUT_MS + 60_000);
});

test("health is secret-free and does no API/quota work", async () => {
  for (const configured of [true, false]) {
    const f = fixture();
    if (!configured) f.deps.key = undefined;
    const response = await handleCurseForge(request("/health"), f.deps);
    const text = await response.text();
    assertNoSecret(response, text);
    assert.equal(JSON.parse(text).configured, configured);
    assert.equal(response.status, configured ? 200 : 503);
    assert.equal(f.calls.length + f.clients.length + f.leases.length, 0);
  }
});

for (const missing of ["key", "limiter", "budget"] as const) test("missing " + missing + " fails closed", async () => {
  const f = fixture();
  delete f.deps[missing];
  const response = await handleCurseForge(request("/v1/games/432"), f.deps);
  assert.equal(response.status, 503);
  assert.equal(f.calls.length, 0);
});

test("client identity, limiter, global concurrency and byte quota prevent upstream calls", async () => {
  const noIp = fixture();
  const noIpRequest = request("/v1/games/432"); noIpRequest.headers.delete("cf-connecting-ip");
  assert.equal((await handleCurseForge(noIpRequest, noIp.deps)).status, 403);
  assert.equal(noIp.calls.length, 0);
  for (const failure of ["limiter", "acquire", "reserve"]) {
    const f = fixture();
    if (failure === "limiter") f.deps.limiter = { limit: async () => ({ success: false }) };
    if (failure === "acquire") f.deps.budget!.acquire = async () => false;
    if (failure === "reserve") f.deps.budget!.reserve = async () => false;
    assert.equal((await handleCurseForge(request("/v1/games/432"), f.deps)).status, 429);
    assert.equal(f.calls.length, 0);
  }
});

for (const status of [301, 302, 401, 403, 404, 429, 500]) test("upstream status " + status + " never relays headers or exception bodies", async () => {
  const f = fixture({ respond: () => new Response(KEY, { status, headers: { location: "https://evil.example/" + KEY, "x-api-key": KEY } }) });
  const response = await handleCurseForge(request("/v1/games/432"), f.deps);
  assert.notEqual(response.status, 200);
  assertNoSecret(response, await response.text());
  assert.equal(f.calls.length, 1);
});

for (const value of [{ data: { id: 432, description: KEY } }, { data: { id: 432, [KEY]: "value" } }])
  test("successful metadata echo is rejected", async () => {
    const f = fixture({ respond: () => json(value) });
    const response = await handleCurseForge(request("/v1/games/432"), f.deps);
    assert.equal(response.status, 502);
    assertNoSecret(response, await response.text());
  });

test("wrong JSON type, oversized, compressed, malformed and truncated metadata are blocked", async () => {
  const responses = [
    new Response("<html>" + KEY, { headers: { "content-type": "text/html" } }),
    new Response("{}", { headers: { "content-type": "application/json", "content-length": String(MAX_JSON_BYTES + 1) } }),
    new Response("{}", { headers: { "content-type": "application/json", "content-length": "20" } }),
    new Response("{}", { headers: { "content-type": "application/json", "content-encoding": "gzip" } }),
    new Response("not json", { headers: { "content-type": "application/json" } }),
    new Response("x".repeat(MAX_JSON_BYTES + 1), { headers: { "content-type": "application/json" } }),
  ];
  for (const upstream of responses) {
    const f = fixture({ respond: () => upstream });
    const response = await handleCurseForge(request("/v1/games/432"), f.deps);
    assert.equal(response.status, 502);
    assertNoSecret(response, await response.text());
  }
});

test("exact download resolves authoritative batch + Minecraft project, pins source, streams exact bytes", async () => {
  const f = fixture();
  const response = await handleCurseForge(request(downloadPath()), f.deps);
  assert.equal(response.status, 200);
  assert.equal(await response.text(), "ZIP");
  assertNoSecret(response, "ZIP");
  assert.equal(response.headers.get("content-length"), "3");
  assert.equal(response.headers.has("set-cookie"), false);
  assert.equal(f.calls[0]!.url, API_ORIGIN + "/v1/mods/files");
  assert.equal(f.calls[0]!.init.method, "POST");
  assert.equal(f.calls[0]!.init.body, JSON.stringify({ fileIds: [1456] }));
  assert.deepEqual(f.reserves, [MAX_JSON_BYTES, MAX_JSON_BYTES, 3]);
  await Promise.all(f.lifetimes);
  assert.equal(f.releases.length, 1);
});

test("source mismatch stops before any CDN request", async () => {
  const f = fixture();
  const response = await handleCurseForge(request(downloadPath("a".repeat(64))), f.deps);
  assert.equal(response.status, 409);
  assert.equal(f.calls.length, 2);
});

for (const change of [
  { id: 999 }, { modId: 999 }, { gameId: 1 }, { isAvailable: false },
  { fileLength: MAX_FILE_BYTES + 1 }, { fileLength: 0 }, { hashes: [] },
  { hashes: [{ algo: 1, value: "not a hash" }] }, { downloadUrl: "https://evil.example/file.zip" },
]) test("download refuses invalid file " + JSON.stringify(change), async () => {
  const f = fixture({ file: { ...FILE, ...change } });
  const response = await handleCurseForge(request(downloadPath()), f.deps);
  assert.notEqual(response.status, 200);
  assert.equal(f.calls.some(call => !call.url.startsWith(API_ORIGIN)), false);
});

for (const change of [{ id: 999 }, { gameId: 1 }, { isAvailable: false }, { allowModDistribution: false }, { allowModDistribution: null }])
  test("author and project restrictions honored " + JSON.stringify(change), async () => {
    const f = fixture({ project: { ...PROJECT, ...change } });
    const response = await handleCurseForge(request(downloadPath()), f.deps);
    assert.notEqual(response.status, 200);
    assert.equal(f.calls.length, 2);
  });

test("null download URL uses only exact official fallback after distribution approval", async () => {
  const f = fixture({ file: { ...FILE, downloadUrl: null } });
  const response = await handleCurseForge(request(downloadPath()), f.deps);
  assert.equal(await response.text(), "ZIP");
  assert.equal(f.calls[2]!.url, API_ORIGIN + "/v1/mods/123/files/1456/download-url");
  const denied = fixture({ file: { ...FILE, downloadUrl: null }, project: { ...PROJECT, allowModDistribution: false } });
  assert.equal((await handleCurseForge(request(downloadPath()), denied.deps)).status, 403);
  assert.equal(denied.calls.length, 2);
});

test("metadata download-url endpoint also observes current distribution denial", async () => {
  const f = fixture({ project: { ...PROJECT, allowModDistribution: false } });
  assert.equal((await handleCurseForge(request("/v1/mods/123/files/1456/download-url"), f.deps)).status, 403);
  assert.equal(f.calls.length, 1);
});

test("CDN redirects are manual, allowlisted, bounded and separately quota charged", async () => {
  let redirects = 0;
  const f = fixture({ cdn: () => ++redirects <= 5 ? new Response(null, { status: 302, headers: { location: "https://mediafilez.forgecdn.net/files/1/456/file.zip" } }) : new Response("ZIP") });
  const response = await handleCurseForge(request(downloadPath()), f.deps);
  assert.equal(await response.text(), "ZIP");
  assert.equal(f.reserves.length, 8);
  const loop = fixture({ cdn: () => new Response(KEY, { status: 302, headers: { location: "/loop.zip" } }) });
  const blocked = await handleCurseForge(request(downloadPath()), loop.deps);
  assert.equal(blocked.status, 502);
  assert.equal(loop.calls.length, 8);
  assertNoSecret(blocked, await blocked.text());
  const foreign = fixture({ cdn: () => new Response(null, { status: 302, headers: { location: "https://evil.example/file.zip" } }) });
  assert.equal((await handleCurseForge(request(downloadPath()), foreign.deps)).status, 502);
  assert.equal(foreign.calls.length, 3);
});

test("download declared and actual length mismatches interrupt without completing", async () => {
  for (const text of ["ZI", "ZIPX"]) {
    const f = fixture({ cdn: () => new Response(text) });
    const response = await handleCurseForge(request(downloadPath()), f.deps);
    await assert.rejects(response.arrayBuffer());
    await Promise.all(f.lifetimes);
    assert.equal(f.releases.length, 1);
  }
  const f = fixture({ cdn: () => new Response("ZIP", { headers: { "content-length": "2" } }) });
  assert.equal((await handleCurseForge(request(downloadPath()), f.deps)).status, 502);
});

test("image is size/type bounded, author checked, has no upstream headers and rejects key echo", async () => {
  const valid = fixture();
  const good = await handleCurseForge(request("/images/123"), valid.deps);
  assert.equal(await good.text(), "PNG");
  assert.deepEqual(valid.reserves, [MAX_JSON_BYTES, MAX_IMAGE_BYTES]);
  for (const image of [
    new Response(KEY, { headers: { "content-type": "image/png" } }),
    new Response("image", { headers: { "content-type": "image/svg+xml" } }),
    new Response("image", { headers: { "content-type": "image/png", "content-length": String(MAX_IMAGE_BYTES + 1) } }),
    new Response("x".repeat(MAX_IMAGE_BYTES + 1), { headers: { "content-type": "image/png" } }),
  ]) {
    const f = fixture({ image });
    const response = await handleCurseForge(request("/images/123"), f.deps);
    assert.equal(response.status, 502);
    assertNoSecret(response, await response.text());
  }
});

test("key split across streamed chunks is never emitted, and quota cleanup is retained", async () => {
  const prefix = "safe-prefix-".repeat(10);
  const chunks = [prefix + KEY.slice(0, 11), KEY.slice(11) + "tail"];
  let index = 0;
  const f = fixture({ file: { ...FILE, fileLength: chunks.join("").length }, cdn: () => new Response(new ReadableStream({
    pull(controller) { if (index < chunks.length) controller.enqueue(new TextEncoder().encode(chunks[index++])); else controller.close(); },
  })) });
  const response = await handleCurseForge(request(downloadPath()), f.deps);
  const reader = response.body!.getReader();
  let received = "";
  await assert.rejects(async () => { for (;;) { const next = await reader.read(); if (next.done) return; received += new TextDecoder().decode(next.value); } });
  assert.equal(received.includes(KEY), false);
  assert.equal(received.includes(KEY.slice(0, 11)), false);
  await Promise.all(f.lifetimes);
  assert.equal(f.releases.length, 1);
});

test("client cancellation propagates to upstream fetch and releases lease", async () => {
  let canceled = false;
  const f = fixture({ file: { ...FILE, fileLength: 1000 }, cdn: () => new Response(new ReadableStream({
    pull() {}, cancel() { canceled = true; },
  })) });
  const response = await handleCurseForge(request(downloadPath()), f.deps);
  await response.body!.cancel();
  await Promise.all(f.lifetimes);
  assert.equal(canceled, true);
  assert.equal(f.releases.length, 1);
  assert.equal(f.calls.at(-1)!.init.signal!.aborted, true);
});

test("metadata header timeout aborts upstream and does not leak thrown details", async t => {
  t.mock.timers.enable({ apis: ["setTimeout"] });
  let entered!: () => void;
  const started = new Promise<void>(resolve => { entered = resolve; });
  const f = fixture({ respond: () => { entered(); return new Promise<Response>(() => {}); } });
  const result = handleCurseForge(request("/v1/games/432"), f.deps);
  await started;
  t.mock.timers.tick(METADATA_TIMEOUT_MS + 1);
  const response = await result;
  assert.equal(response.status, 504);
  assertNoSecret(response, await response.text());
  assert.equal(f.calls[0]!.init.signal!.aborted, true);
  await Promise.all(f.lifetimes);
});

test("late successful quota acquisition after caller cancellation is released", async () => {
  let resolveAcquisition!: (value: boolean) => void;
  let acquisitionEntered!: () => void;
  const entered = new Promise<void>(resolve => { acquisitionEntered = resolve; });
  const f = fixture();
  f.deps.budget!.acquire = () => { acquisitionEntered(); return new Promise<boolean>(resolve => { resolveAcquisition = resolve; }); };
  const controller = new AbortController();
  const pending = handleCurseForge(request("/v1/games/432", { signal: controller.signal }), f.deps);
  await entered; controller.abort();
  const response = await pending;
  assert.equal(response.status, 504);
  resolveAcquisition(true);
  await Promise.all(f.lifetimes);
  assert.equal(f.releases.length, 1);
  assert.equal(f.calls.length, 0);
});

test("acquisition success and cancellation in the same turn cannot leak a quota lease", async () => {
  let resolveAcquisition!: (value: boolean) => void;
  let acquisitionEntered!: () => void;
  const entered = new Promise<void>(resolve => { acquisitionEntered = resolve; });
  const f = fixture();
  f.deps.budget!.acquire = () => { acquisitionEntered(); return new Promise<boolean>(resolve => { resolveAcquisition = resolve; }); };
  const controller = new AbortController();
  const pending = handleCurseForge(request("/v1/games/432", { signal: controller.signal }), f.deps);
  await entered;
  controller.abort();
  resolveAcquisition(true);
  const response = await pending;
  assert.equal(response.status, 504);
  await Promise.all(f.lifetimes);
  assert.equal(f.releases.length, 1);
  assert.equal(f.calls.length, 0);
});
