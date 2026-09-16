import assert from "node:assert/strict";
import { test } from "node:test";
import { approvedCdn, parseRoute, sourceIdentityText } from "../src/routes.ts";

const request = (path: string) => new Request("https://gateway.example" + path);
for (const path of [
  "/health", "/v1/games/432", "/v1/minecraft/version?sortDescending=true", "/v1/minecraft/modloader",
  "/v1/categories?gameId=432&classId=4471",
  "/v1/mods/search?gameId=432&classId=4471&pageSize=50&index=50&searchFilter=All%20the%20Mods&sortField=6&sortOrder=desc&gameVersion=1.21.1&modLoaderType=6&categoryId=4472",
  "/v1/mods/search?gameId=432&classId=6&slug=example-mod", "/v1/mods/123",
  "/v1/mods/123/files?gameVersion=1.21.1&modLoaderType=6&pageSize=50&index=0",
  "/v1/mods/123/files/456", "/v1/mods/123/files/456/download-url", "/images/123",
  "/downloads/456?sourceSha256=" + "a".repeat(64),
]) test("allowlisted native route " + path, () => assert.doesNotThrow(() => parseRoute(request(path))));

for (const path of [
  "/v1/games/1", "/v1/mods", "/v1/mods/files", "/v1/mods/1/description", "/v1/mods/0",
  "/v1/mods/9007199254740992", "/v1/mods/123?url=https://evil.example", "/images/123?url=https://evil.example",
  "/v1/mods/search?gameId=1&classId=6", "/v1/mods/search?gameId=432&classId=7",
  "/v1/mods/search?gameId=432&classId=6&pageSize=100",
  "/v1/mods/search?gameId=432&classId=6&pageSize=0", "/v1/mods/search?gameId=432&classId=6&index=10001",
  "/v1/mods/search?gameId=432&classId=6&gameId=432", "/v1/mods/search?gameId=432&classId=6&sortOrder=other",
  "/v1/mods/search?gameId=432&classId=6&searchFilter=%00", "/v1/mods/1%2ffiles/2",
  "/v1/minecraft/version?sortDescending=yes", "/downloads/456", "/downloads/456?sourceSha256=" + "A".repeat(64),
  "/health?key=x", "/v1/mods/search?gameId=432&classId=6&slug=UpperCase",
]) test("reject unapproved route " + path, () => assert.throws(() => parseRoute(request(path))));

test("methods and input fragments are rejected", () => {
  assert.throws(() => parseRoute(new Request("https://gateway.example/v1/games/432", { method: "POST", body: "{}" })));
  assert.throws(() => parseRoute(request("/health#ignored")));
});

for (const url of [
  "http://edge.forgecdn.net/x", "https://edge.forgecdn.net.evil.example/x", "https://forgecdn.net/x",
  "https://unreviewed.forgecdn.net/x", "https://edge.forgecdn.net:444/x", "https://a:b@edge.forgecdn.net/x",
  "https://edge.forgecdn.net/x?key=x", "https://edge.forgecdn.net/x#fragment", "https://127.0.0.1/x",
  "https://edge.forgecdn.net./x", "https://edge.forgecdn.net/%00", "https://edge.forgecdn.net/%zz",
  "https://edge.forgecdn.net/%E9", "https://edge.forgecdn.net/x\\y",
]) test("reject unapproved CDN URL " + url, () => assert.throws(() => approvedCdn(url)));

test("standard CDN hosts, HTTPS443 and equivalent escaped filename identities", () => {
  assert.equal(approvedCdn("https://edge.forgecdn.net:443/files/1/2/Test Pack.zip"), "https://edge.forgecdn.net/files/1/2/Test%20Pack.zip");
  for (const host of ["edge", "media", "mediafilez"]) assert.doesNotThrow(() => approvedCdn("https://" + host + ".forgecdn.net/files/1/2/file.zip"));
  for (const [left, right] of [["A", "%41"], ["Test Pack", "Test%20Pack"], ["é", "%C3%A9"]])
    assert.equal(sourceIdentityText("https://edge.forgecdn.net/" + left + ".zip"), sourceIdentityText("https://edge.forgecdn.net/" + right + ".zip"));
  assert.equal(sourceIdentityText("https://edge.forgecdn.net/%2520.zip"), "https://edge.forgecdn.net/%20.zip");
});
