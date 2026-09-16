import { MAX_FILE_BYTES, MAX_LEASE_MS } from "./contract.ts";

export interface SqlCursor extends Iterable<Record<string, unknown>> {}
export interface SqlDatabase { exec(query: string, ...bindings: Array<string | number | null>): SqlCursor }
export interface BudgetStorage { sql: SqlDatabase; transactionSync<T>(callback: () => T): T }
export interface BudgetLimits { dailyRequests: number; dailyBytes: number; concurrent: number }
const ID = /^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/;

/** The singleton stores only aggregate reservation counters and random expiring operation IDs.
 * No IP addresses, searches, project/file identities, responses or credentials are stored. */
export class BudgetLedger {
  readonly storage: BudgetStorage;
  readonly limits: BudgetLimits;
  constructor(storage: BudgetStorage, limits: BudgetLimits) {
    this.storage = storage;
    this.limits = limits;
    if (!Number.isSafeInteger(limits.dailyRequests) || limits.dailyRequests < 1 || limits.dailyRequests > 1_000_000 ||
        !Number.isSafeInteger(limits.dailyBytes) || limits.dailyBytes < 1 || limits.dailyBytes > 1024 ** 4 ||
        !Number.isSafeInteger(limits.concurrent) || limits.concurrent < 1 || limits.concurrent > 128) throw new Error("Invalid service capacity configuration.");
    storage.sql.exec("CREATE TABLE IF NOT EXISTS quota_day (day TEXT PRIMARY KEY, requests INTEGER NOT NULL, bytes INTEGER NOT NULL)");
    storage.sql.exec("CREATE TABLE IF NOT EXISTS active_operation (id TEXT PRIMARY KEY, expires INTEGER NOT NULL)");
  }

  acquireDuration(id: string, durationMs: number, now: number): boolean {
    if (!Number.isSafeInteger(durationMs) || durationMs < 1 || durationMs > MAX_LEASE_MS ||
        !Number.isSafeInteger(now + durationMs)) return false;
    return this.acquire(id, now + durationMs, now);
  }

  acquire(id: string, expiresAt: number, now: number): boolean {
    if (!ID.test(id) || !Number.isSafeInteger(now) || !Number.isSafeInteger(expiresAt) || expiresAt <= now || expiresAt > now + MAX_LEASE_MS) return false;
    return this.storage.transactionSync(() => {
      const sql = this.storage.sql;
      sql.exec("DELETE FROM active_operation WHERE expires <= ?", now);
      const [count] = [...sql.exec("SELECT COUNT(*) AS count FROM active_operation")];
      if (Number(count?.count) >= this.limits.concurrent || [...sql.exec("SELECT id FROM active_operation WHERE id = ?", id)].length) return false;
      sql.exec("INSERT INTO active_operation (id, expires) VALUES (?, ?)", id, expiresAt);
      return true;
    });
  }

  reserve(id: string, bytes: number, now: number): boolean {
    if (!ID.test(id) || !Number.isSafeInteger(bytes) || bytes < 0 || bytes > MAX_FILE_BYTES || !Number.isSafeInteger(now)) return false;
    const day = new Date(now).toISOString().slice(0, 10);
    return this.storage.transactionSync(() => {
      const sql = this.storage.sql;
      sql.exec("DELETE FROM active_operation WHERE expires <= ?", now);
      if (![...sql.exec("SELECT id FROM active_operation WHERE id = ?", id)].length) return false;
      // Old aggregate days contain no audit details and have no reason to persist.
      sql.exec("DELETE FROM quota_day WHERE day < ?", day);
      sql.exec("INSERT OR IGNORE INTO quota_day (day, requests, bytes) VALUES (?, 0, 0)", day);
      const [row] = [...sql.exec("SELECT requests, bytes FROM quota_day WHERE day = ?", day)];
      if (!row || Number(row.requests) >= this.limits.dailyRequests || Number(row.bytes) + bytes > this.limits.dailyBytes) return false;
      sql.exec("UPDATE quota_day SET requests = requests + 1, bytes = bytes + ? WHERE day = ?", bytes, day);
      return true;
    });
  }

  release(id: string): void {
    if (ID.test(id)) this.storage.sql.exec("DELETE FROM active_operation WHERE id = ?", id);
  }
}
