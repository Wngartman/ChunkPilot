import assert from "node:assert/strict";
import { test } from "node:test";
import { isGloballyRoutableIpv4, normalizeAddress } from "../src/addresses.ts";

test("a canonical dotted quad is IPv4", () => {
  const address = normalizeAddress("93.184.216.34");
  assert.equal(address.family, "ipv4");
  assert.equal(address.value, "93.184.216.34");
  assert.equal(address.classification, "globally_routable");
  assert.ok(isGloballyRoutableIpv4(address));
});

test("an IPv4-mapped IPv6 source is reduced to the same IPv4 host", () => {
  const address = normalizeAddress("::ffff:93.184.216.34");
  assert.equal(address.family, "ipv4");
  assert.equal(address.value, "93.184.216.34");
});

test("a bracketed IPv6 literal stays IPv6", () => {
  const address = normalizeAddress("[2001:db8::1]");
  assert.equal(address.family, "ipv6");
  assert.equal(address.value, "2001:db8::1");
  assert.equal(isGloballyRoutableIpv4(address), false);
});

/**
 * The lenient IPv4 forms are exactly how a private target is smuggled past a textual check, so none
 * of them is an address here.
 */
for (const smuggled of [
  "010.0.0.1",
  "0x7f.0.0.1",
  "2130706433",
  "127.1",
  "1.2.3.4.5",
  "1.2.3",
  "1.2.3.256",
  " 1.2.3.4 extra",
  "example.com",
  "93.184.216.34:80",
]) {
  test(`"${smuggled}" is not accepted as an IPv4 address`, () => {
    const address = normalizeAddress(smuggled);
    assert.equal(isGloballyRoutableIpv4(address), false);
  });
}

test("every documented non-internet IPv4 range is refused", () => {
  const cases: Array<[string, string]> = [
    ["10.0.0.140", "private_use"],
    ["172.16.4.1", "private_use"],
    ["192.168.1.50", "private_use"],
    ["100.64.0.1", "shared_address_space"],
    ["127.0.0.1", "loopback"],
    ["169.254.10.1", "link_local"],
    ["192.0.2.1", "documentation"],
    ["198.51.100.1", "documentation"],
    ["203.0.113.7", "documentation"],
    ["198.18.0.1", "reserved"],
    ["192.0.0.1", "reserved"],
    ["0.0.0.0", "reserved"],
    ["224.0.0.1", "reserved"],
    ["255.255.255.255", "reserved"],
  ];
  for (const [text, expected] of cases) {
    const address = normalizeAddress(text);
    assert.equal(address.family, "ipv4", text);
    assert.equal(address.classification, expected, text);
    assert.equal(isGloballyRoutableIpv4(address), false, text);
  }
});

test("nothing at all is an unknown address rather than a default", () => {
  for (const empty of [null, undefined, "", "   "]) {
    const address = normalizeAddress(empty);
    assert.equal(address.family, "unknown");
    assert.equal(address.value, "");
  }
});
