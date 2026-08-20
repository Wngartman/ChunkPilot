import type { AddressFamily } from "./contract.ts";

/**
 * Address parsing and classification.
 *
 * This is the anti-SSRF layer's arithmetic. IPv4 is parsed strictly — exactly four decimal octets,
 * no leading zeros, no shorthand — because the lenient forms (`010.0.0.1`, `0x7f.1`, `2130706433`)
 * are the classic way a private target is smuggled past a textual check. Anything that is not the
 * one canonical form is not an IPv4 address here.
 */

/** Mirrors ChunkPilot's own IPv4 classification so both sides agree on what "public" means. */
export type Ipv4Classification =
  | "globally_routable"
  | "private_use"
  | "shared_address_space"
  | "loopback"
  | "link_local"
  | "documentation"
  | "reserved";

export interface NormalizedAddress {
  family: AddressFamily;
  /** Canonical text, or "" when nothing usable was supplied. */
  value: string;
  /** Present only for IPv4. */
  classification: Ipv4Classification | null;
}

const UNKNOWN: NormalizedAddress = { family: "unknown", value: "", classification: null };

/**
 * Normalizes one address as it arrives from a header or a JSON field.
 *
 * An IPv4-mapped IPv6 form (`::ffff:203.0.113.7`) is reduced to its IPv4 value, because that is the
 * same host reached over the same address family; every other IPv6 text stays IPv6.
 */
export function normalizeAddress(raw: string | null | undefined): NormalizedAddress {
  if (typeof raw !== "string") return UNKNOWN;
  let text = raw.trim();
  if (text.length === 0 || text.length > 45) return UNKNOWN;
  if (text.startsWith("[") && text.endsWith("]")) text = text.slice(1, -1);
  if (text.length === 0) return UNKNOWN;

  const mapped = /^::ffff:(\d{1,3}(?:\.\d{1,3}){3})$/i.exec(text);
  if (mapped) text = mapped[1]!;

  const octets = parseIpv4Octets(text);
  if (octets) {
    return {
      family: "ipv4",
      value: octets.join("."),
      classification: classifyIpv4(octets),
    };
  }
  if (isIpv6(text)) return { family: "ipv6", value: text.toLowerCase(), classification: null };
  return UNKNOWN;
}

/** True only for an IPv4 address nothing locally contradicts as an internet address. */
export function isGloballyRoutableIpv4(address: NormalizedAddress): boolean {
  return address.family === "ipv4" && address.classification === "globally_routable";
}

function parseIpv4Octets(text: string): [number, number, number, number] | null {
  const parts = text.split(".");
  if (parts.length !== 4) return null;
  const octets: number[] = [];
  for (const part of parts) {
    // Strict: 1-3 digits, and no leading zero unless the octet is exactly "0".
    if (!/^\d{1,3}$/.test(part)) return null;
    if (part.length > 1 && part.startsWith("0")) return null;
    const value = Number(part);
    if (value > 255) return null;
    octets.push(value);
  }
  return octets as [number, number, number, number];
}

/**
 * Deliberately conservative: an address only classifies as globally routable when it falls in none
 * of the documented non-internet ranges.
 */
function classifyIpv4(octets: [number, number, number, number]): Ipv4Classification {
  const [a, b, c] = octets;
  if (a === 0) return "reserved"; // 0.0.0.0/8
  if (a === 127) return "loopback";
  if (a === 10) return "private_use"; // RFC 1918
  if (a === 172 && b >= 16 && b <= 31) return "private_use"; // RFC 1918
  if (a === 192 && b === 168) return "private_use"; // RFC 1918
  if (a === 100 && b >= 64 && b <= 127) return "shared_address_space"; // RFC 6598, carrier-grade NAT
  if (a === 169 && b === 254) return "link_local";
  if (a === 192 && b === 0 && c === 2) return "documentation"; // RFC 5737
  if (a === 198 && b === 51 && c === 100) return "documentation";
  if (a === 203 && b === 0 && c === 113) return "documentation";
  if (a === 198 && (b === 18 || b === 19)) return "reserved"; // benchmarking
  if (a === 192 && b === 0 && c === 0) return "reserved"; // IETF protocol assignments
  if (a >= 224) return "reserved"; // multicast and above
  return "globally_routable";
}

function isIpv6(text: string): boolean {
  if (!text.includes(":")) return false;
  if (!/^[0-9a-f:.%]+$/i.test(text)) return false;
  const withoutZone = text.split("%")[0]!;
  const groups = withoutZone.split(":");
  return groups.length >= 3 && groups.length <= 9;
}
