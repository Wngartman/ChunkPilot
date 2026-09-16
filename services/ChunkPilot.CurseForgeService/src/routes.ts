import { ServiceError } from "./contract.ts";

export type Route =
  | { kind: "health" }
  | { kind: "metadata"; path: string; projectId?: number; fileId?: number; shape: "game" | "versions" | "categories" | "search" | "project" | "files" | "file" | "downloadUrl" }
  | { kind: "download"; fileId: number; sourceSha256: string }
  | { kind: "image"; projectId: number };

export function positiveId(value: string): number | null {
  if (!/^[1-9][0-9]{0,15}$/.test(value)) return null;
  const parsed = Number(value);
  return Number.isSafeInteger(parsed) ? parsed : null;
}

function query(url: URL, allowed: readonly string[]): void {
  const seen = new Set<string>();
  for (const [name, value] of url.searchParams) {
    if (!allowed.includes(name) || seen.has(name) || value.length > 256 || /[\u0000-\u001f\u007f]/.test(value))
      throw new ServiceError(400, "invalid_request");
    seen.add(name);
    if (name === "gameId" && value !== "432") throw new ServiceError(400, "minecraft_only");
    if (name === "classId" && !["6", "4471", "12", "5"].includes(value)) throw new ServiceError(400, "invalid_request");
    if (["pageSize", "index", "sortField", "modLoaderType", "categoryId"].includes(name)) {
      if (!/^(0|[1-9][0-9]{0,8})$/.test(value)) throw new ServiceError(400, "invalid_request");
      const n = Number(value);
      if ((name === "pageSize" && (n < 1 || n > 50)) || (name === "index" && n > 10_000) ||
          (name === "sortField" && (n < 1 || n > 12)) || (name === "modLoaderType" && n > 6) ||
          (name === "categoryId" && n < 1)) throw new ServiceError(400, "invalid_request");
    }
    if (name === "sortOrder" && !["asc", "desc"].includes(value)) throw new ServiceError(400, "invalid_request");
    if (name === "sortDescending" && !["true", "false"].includes(value)) throw new ServiceError(400, "invalid_request");
    if (name === "gameVersion" && !/^[A-Za-z0-9._ +\-]{1,64}$/.test(value)) throw new ServiceError(400, "invalid_request");
    if (name === "slug" && !/^[a-z0-9][a-z0-9-]{0,127}$/.test(value)) throw new ServiceError(400, "invalid_request");
  }
}

export function parseRoute(request: Request): Route {
  if (request.method !== "GET") throw new ServiceError(405, "method_not_allowed");
  const url = new URL(request.url);
  if (url.protocol !== "https:" || url.username || url.password || url.hash || url.port ||
      request.url.length > 2048 || /[%\\]/.test(url.pathname)) throw new ServiceError(400, "invalid_request");
  const path = url.pathname;
  if (path === "/health") { query(url, []); return { kind: "health" }; }
  let match = /^\/downloads\/([1-9][0-9]{0,15})$/.exec(path);
  if (match) {
    query(url, ["sourceSha256"]);
    const fileId = positiveId(match[1]!);
    const sourceSha256 = url.searchParams.get("sourceSha256") ?? "";
    if (!fileId || !/^[a-f0-9]{64}$/.test(sourceSha256)) throw new ServiceError(400, "invalid_request");
    return { kind: "download", fileId, sourceSha256 };
  }
  match = /^\/images\/([1-9][0-9]{0,15})$/.exec(path);
  if (match) {
    query(url, []);
    const projectId = positiveId(match[1]!);
    if (!projectId) throw new ServiceError(400, "invalid_request");
    return { kind: "image", projectId };
  }
  if (path === "/v1/games/432") { query(url, []); return { kind: "metadata", path, shape: "game" }; }
  if (path === "/v1/minecraft/version") {
    query(url, ["sortDescending"]);
    return { kind: "metadata", path: path + url.search, shape: "versions" };
  }
  if (path === "/v1/minecraft/modloader") {
    query(url, []);
    return { kind: "metadata", path, shape: "versions" };
  }
  if (path === "/v1/categories" || path === "/v1/mods/search") {
    query(url, path.endsWith("categories") ? ["gameId", "classId"] :
      ["gameId", "classId", "pageSize", "index", "searchFilter", "sortField", "sortOrder", "gameVersion", "modLoaderType", "categoryId", "slug"]);
    if (url.searchParams.get("gameId") !== "432" || !url.searchParams.has("classId")) throw new ServiceError(400, "minecraft_only");
    // Enforce a bounded page even if a caller omits the native client's explicit page size.
    if (path.endsWith("search") && !url.searchParams.has("pageSize")) url.searchParams.set("pageSize", "50");
    return { kind: "metadata", path: path + url.search, shape: path.endsWith("search") ? "search" : "categories" };
  }
  match = /^\/v1\/mods\/([1-9][0-9]{0,15})(?:\/files(?:\/([1-9][0-9]{0,15})(\/download-url)?)?)?$/.exec(path);
  if (!match) throw new ServiceError(404, "route_not_found");
  const projectId = positiveId(match[1]!);
  const fileId = match[2] ? positiveId(match[2]) : undefined;
  if (!projectId || fileId === null) throw new ServiceError(400, "invalid_request");
  const shape = match[3] ? "downloadUrl" : fileId ? "file" : path.endsWith("/files") ? "files" : "project";
  query(url, shape === "files" ? ["gameVersion", "modLoaderType", "pageSize", "index"] : []);
  if (shape === "files" && !url.searchParams.has("pageSize")) url.searchParams.set("pageSize", "50");
  return { kind: "metadata", path: path + url.search, projectId, fileId, shape };
}

const CDN_HOSTS = new Set(["edge.forgecdn.net", "mediafilez.forgecdn.net", "media.forgecdn.net"]);
export function approvedCdn(value: unknown): string {
  if (typeof value !== "string" || value.length > 2048 || value.trim() !== value || /[\u0000-\u001f\u007f\\]/.test(value))
    throw new ServiceError(502, "invalid_upstream_destination");
  let url: URL;
  try { url = new URL(value); } catch { throw new ServiceError(502, "invalid_upstream_destination"); }
  if (url.protocol !== "https:" || !CDN_HOSTS.has(url.hostname) || url.port || url.username || url.password || url.search || url.hash)
    throw new ServiceError(502, "invalid_upstream_destination");
  sourceIdentityText(url.href);
  return url.href;
}

/** Identity only: .NET IdnHost + UnescapeDataString(AbsolutePath). Never download this decoded text. */
export function sourceIdentityText(source: string): string {
  const url = new URL(source);
  let path: string;
  try { path = decodeURIComponent(url.pathname); }
  catch { throw new ServiceError(502, "invalid_upstream_destination"); }
  if (/[\u0000-\u001f\u007f\\]/.test(path)) throw new ServiceError(502, "invalid_upstream_destination");
  return "https://" + url.hostname.toLowerCase() + path;
}
