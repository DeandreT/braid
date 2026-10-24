// Explicit small subnets for test-owned Docker networks, chosen outside the daemon's default pools.
// A network created without a subnet takes a whole /16 or /20 from those pools, which a few concurrent
// suites exhaust. The default is the upper half of the RFC 2544 benchmarking block: neither the pools
// nor private LANs, CGNAT/Tailscale (100.64.0.0/10) or the common fake-IP proxy range use it.
import { randomInt } from "node:crypto";
import net from "node:net";

export const testSubnetRangeVariable = "WEFT_TEST_SUBNET_RANGE";
export const defaultTestSubnetRange = "198.19.0.0/16";
export const testSubnetPrefixLength = 28;
export const maximumSubnetAttempts = 32;

export class TestSubnetsExhausted extends Error {
  constructor(range, attempts) {
    super(
      `No free /${testSubnetPrefixLength} subnet for a test Docker network in ${range} after ${attempts} overlapping candidates; ` +
        `remove leaked test networks or set ${testSubnetRangeVariable} to a free IPv4 range.`,
    );
    this.category = "testSubnetsExhausted";
  }
}

export function configuredTestSubnetRange(env = process.env) {
  return env[testSubnetRangeVariable] || defaultTestSubnetRange;
}

// Docker's refusal of a subnet that overlaps another network's pool.
export function isPoolOverlap(dockerError) {
  return /Pool overlaps|invalid pool request/i.test(dockerError);
}

export function subnetCandidates(range, prefixLength = testSubnetPrefixLength) {
  const match = /^(\d+\.\d+\.\d+\.\d+)\/(\d+)$/.exec(range);
  const base = match && net.isIPv4(match[1]) ? toInteger(match[1]) : null;
  const size = match ? Number(match[2]) : NaN;
  if (
    base === null ||
    !(size >= 0 && size <= prefixLength) ||
    fromInteger(base) !== match[1] ||
    base % 2 ** (32 - size) !== 0
  )
    throw new Error(
      `${testSubnetRangeVariable} must be an IPv4 network of /${prefixLength} or larger in canonical form, such as ${defaultTestSubnetRange}.`,
    );
  return {
    count: 2 ** (prefixLength - size),
    subnet: (index) =>
      `${fromInteger(base + index * 2 ** (32 - prefixLength))}/${prefixLength}`,
  };
}

// Creates a network on distinct random candidates until one does not overlap an existing network.
// `create` returns false only when Docker reported an overlapping pool.
export function createOnFreeSubnet(
  create,
  { range = configuredTestSubnetRange(), random = randomInt } = {},
) {
  const { count, subnet } = subnetCandidates(range);
  const attempts = Math.min(count, maximumSubnetAttempts);
  const tried = new Set();
  while (tried.size < attempts) {
    let index;
    do index = random(count);
    while (tried.has(index));
    tried.add(index);
    if (create(subnet(index))) return subnet(index);
  }
  throw new TestSubnetsExhausted(range, attempts);
}

function toInteger(address) {
  return address
    .split(".")
    .reduce((value, octet) => value * 256 + Number(octet), 0);
}

function fromInteger(value) {
  return [24, 16, 8, 0]
    .map((shift) => Math.floor(value / 2 ** shift) % 256)
    .join(".");
}
