import assert from "node:assert/strict";
import { test } from "node:test";
import {
  TestSubnetsExhausted,
  configuredTestSubnetRange,
  createOnFreeSubnet,
  defaultTestSubnetRange,
  isPoolOverlap,
  maximumSubnetAttempts,
  subnetCandidates,
  testSubnetRangeVariable,
} from "./test-subnets.mjs";

test("overlapping candidates are retried on distinct /28 subnets inside the range", () => {
  const offered = [];
  const chosen = createOnFreeSubnet((subnet) => offered.push(subnet) === 4, {
    range: "198.19.0.0/16",
  });
  assert.equal(offered.length, 4);
  assert.equal(chosen, offered[3]);
  assert.equal(new Set(offered).size, 4);
  for (const subnet of offered) assert.match(subnet, /^198\.19\.\d+\.\d+\/28$/);
});

test("a small range is exhausted after each candidate is tried once", () => {
  const offered = [];
  assert.throws(
    () =>
      createOnFreeSubnet((subnet) => offered.push(subnet) && false, {
        range: "10.250.0.0/26",
      }),
    (error) =>
      error instanceof TestSubnetsExhausted &&
      error.category === "testSubnetsExhausted" &&
      error.message.includes("10.250.0.0/26") &&
      error.message.includes(testSubnetRangeVariable),
  );
  assert.deepEqual(offered.sort(), [
    "10.250.0.0/28",
    "10.250.0.16/28",
    "10.250.0.32/28",
    "10.250.0.48/28",
  ]);
});

test("a crowded large range stops at the attempt bound", () => {
  const offered = new Set();
  assert.throws(
    () =>
      createOnFreeSubnet((subnet) => offered.add(subnet) && false, {
        range: defaultTestSubnetRange,
      }),
    TestSubnetsExhausted,
  );
  assert.equal(offered.size, maximumSubnetAttempts);
});

test("candidates follow the injected order and other failures are not retried", () => {
  let next = 0;
  const offered = [];
  assert.throws(
    () =>
      createOnFreeSubnet(
        (subnet) => {
          offered.push(subnet);
          if (offered.length === 2) throw new Error("name already exists");
          return false;
        },
        { range: "198.19.4.0/27", random: (count) => next++ % count },
      ),
    /name already exists/,
  );
  assert.deepEqual(offered, ["198.19.4.0/28", "198.19.4.16/28"]);
});

test("invalid ranges are refused before any network is created", () => {
  for (const range of [
    "2001:db8::/64",
    "198.19.0.0/29",
    "198.19.0.1/16",
    "198.19.0.0",
    "198.019.0.0/16",
    "not a range",
  ])
    assert.throws(
      () =>
        createOnFreeSubnet(
          () => assert.fail("a network was created for " + range),
          { range },
        ),
      (error) => error.message.includes(testSubnetRangeVariable),
    );
  assert.equal(subnetCandidates("0.0.0.0/0").count, 2 ** 28);
  assert.equal(
    subnetCandidates("10.0.0.0/8").subnet(2 ** 20 - 1),
    "10.255.255.240/28",
  );
});

test("the range comes from the environment with a fixed default", () => {
  assert.equal(configuredTestSubnetRange({}), defaultTestSubnetRange);
  assert.equal(
    configuredTestSubnetRange({ [testSubnetRangeVariable]: "" }),
    defaultTestSubnetRange,
  );
  assert.equal(
    configuredTestSubnetRange({ [testSubnetRangeVariable]: "10.250.0.0/24" }),
    "10.250.0.0/24",
  );
});

test("only Docker pool overlap refusals are retried", () => {
  assert.ok(
    isPoolOverlap(
      "Error response from daemon: invalid pool request: Pool overlaps with other one on this address space",
    ),
  );
  assert.ok(
    !isPoolOverlap(
      "Error response from daemon: network with name x already exists",
    ),
  );
  assert.ok(
    !isPoolOverlap(
      "Error response from daemon: all predefined address pools have been fully subnetted",
    ),
  );
});
