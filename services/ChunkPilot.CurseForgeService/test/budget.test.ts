import assert from "node:assert/strict";
import { DatabaseSync } from "node:sqlite";
import { test } from "node:test";
import { BudgetLedger, type BudgetLimits, type BudgetStorage } from "../src/budget.ts";
import { MAX_LEASE_MS } from "../src/contract.ts";

const NOW = Date.parse("2026-09-15T08:00:00Z");
const ID = "10000000-0000-4000-8000-000000000001";
const ID2 = "10000000-0000-4000-8000-000000000002";
function fixture(limits: BudgetLimits = { dailyRequests: 3, dailyBytes: 1000, concurrent: 1 }) {
  const db = new DatabaseSync(":memory:");
  const storage: BudgetStorage = {
    sql: { exec: (query, ...bindings) => db.prepare(query).all(...bindings) },
    transactionSync(callback) {
      db.exec("BEGIN IMMEDIATE");
      try { const result = callback(); db.exec("COMMIT"); return result; }
      catch (error) { db.exec("ROLLBACK"); throw error; }
    },
  };
  return { db, storage, ledger: new BudgetLedger(storage, limits) };
}

test("global concurrency cannot be reset by a new ledger/Worker instance", () => {
  const f = fixture();
  assert.equal(f.ledger.acquire(ID, NOW + 1000, NOW), true);
  const second = new BudgetLedger(f.storage, f.ledger.limits);
  assert.equal(second.acquire(ID2, NOW + 1000, NOW), false);
  f.ledger.release(ID);
  assert.equal(second.acquire(ID2, NOW + 1000, NOW), true);
  f.db.close();
});

test("requests and bytes reserved atomically without refunds", () => {
  const f = fixture();
  assert.equal(f.ledger.acquire(ID, NOW + 1000, NOW), true);
  assert.equal(f.ledger.reserve(ID, 900, NOW), true);
  assert.equal(f.ledger.reserve(ID, 101, NOW), false);
  assert.equal(f.ledger.reserve(ID, 100, NOW), true);
  assert.equal(f.ledger.reserve(ID, 0, NOW), true);
  assert.equal(f.ledger.reserve(ID, 0, NOW), false);
  f.ledger.release(ID);
  assert.equal(f.ledger.acquire(ID2, NOW + 1000, NOW), true);
  assert.equal(f.ledger.reserve(ID2, 0, NOW), false);
  assert.deepEqual({ ...f.db.prepare("SELECT requests,bytes FROM quota_day").get() }, { requests: 3, bytes: 1000 });
  f.db.close();
});

test("expired, duplicate, unowned and invalid operations fail closed", () => {
  const f = fixture();
  assert.equal(f.ledger.reserve(ID, 1, NOW), false);
  assert.equal(f.ledger.acquire(ID, NOW + MAX_LEASE_MS + 1, NOW), false);
  assert.equal(f.ledger.acquire("client IP or key", NOW + 1000, NOW), false);
  assert.equal(f.ledger.acquire(ID, NOW + 1000, NOW), true);
  assert.equal(f.ledger.acquire(ID, NOW + 1000, NOW), false);
  assert.equal(f.ledger.reserve(ID, -1, NOW), false);
  assert.equal(f.ledger.reserve(ID, Number.MAX_SAFE_INTEGER, NOW), false);
  assert.equal(f.ledger.reserve(ID, 1, NOW + 1000), false);
  assert.equal(f.ledger.acquire(ID2, NOW + 2000, NOW + 1000), true);
  f.db.close();
});

test("UTC reservation day rolls over without retaining old content or request details", () => {
  const f = fixture();
  f.ledger.acquire(ID, NOW + 1000, NOW);
  f.ledger.reserve(ID, 1000, NOW);
  const tomorrow = NOW + 86_400_000;
  assert.equal(f.ledger.acquire(ID2, tomorrow + 1000, tomorrow), true);
  assert.equal(f.ledger.reserve(ID2, 1000, tomorrow), true);
  const rows = f.db.prepare("SELECT * FROM quota_day").all();
  assert.equal(rows.length, 1);
  assert.equal(rows[0]!.day, "2026-09-16");
  const schema = f.db.prepare("SELECT name FROM sqlite_master WHERE type='table'").all();
  assert.deepEqual(schema.map(row => row.name).sort(), ["active_operation", "quota_day"]);
  f.db.close();
});

test("invalid or unbounded quota configuration fails closed", () => {
  for (const limits of [
    { dailyRequests: 0, dailyBytes: 1000, concurrent: 1 },
    { dailyRequests: 2, dailyBytes: Infinity, concurrent: 1 },
    { dailyRequests: 2, dailyBytes: 1000, concurrent: 129 },
    { dailyRequests: NaN, dailyBytes: 1000, concurrent: 1 },
  ]) assert.throws(() => fixture(limits));
});
