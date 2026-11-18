import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import ts from "typescript";

const source = await readFile(
  new URL(
    "../../apps/web/src/administration/invitation-path.ts",
    import.meta.url,
  ),
  "utf8",
);
const { outputText } = ts.transpileModule(source, {
  compilerOptions: {
    module: ts.ModuleKind.ESNext,
    target: ts.ScriptTarget.ES2022,
  },
});
const { isInvitationPath } = await import(
  `data:text/javascript;base64,${Buffer.from(outputText).toString("base64")}`
);
const organization = "11111111-1111-4111-8111-111111111111";
const foreignOrganization = "22222222-2222-4222-8222-222222222222";
const secret = "0123456789abcdef".repeat(4);
const route = `/organizations/${organization}/invitations`;
const path = `${route}#${secret}`;

test("accepts the exact qualified path only for its captured organization", () => {
  assert.equal(isInvitationPath(path, organization), true);
  assert.equal(isInvitationPath(path, foreignOrganization), false);
  assert.equal(
    isInvitationPath(
      path.replace(organization, foreignOrganization),
      organization,
    ),
    false,
  );
  // Legacy root links are redemption compatibility, not creation responses.
  assert.equal(isInvitationPath(`/invitations#${secret}`, organization), false);
});

test("refuses absolute, encoded, query-bearing and malformed paths before URL parsing", () => {
  for (const value of [
    `https://braid.example${path}`,
    `https://evil.example${path}`,
    `//evil.example${path}`,
    `javascript:${path}`,
    path.slice(1),
    `/extra${path}`,
    path.replace("/invitations", "/../invitations"),
    path.replace("/invitations", "//invitations"),
    path.replace("/invitations", "\\invitations"),
    path.replace("organizations", "%6frganizations"),
    path.replace(organization, "%31" + organization.slice(1)),
    path.replace(organization, "not-a-guid"),
    path.replace("#", "%23"),
    `${route}?next=https://evil.example#${secret}`,
    `${route}/#${secret}`,
    `${path}?query=1`,
    `${path}#extra`,
    ` ${path}`,
    `${path}\n`,
    `${path}\r\n`,
    `${path}\u0000`,
    path.replace("/invitations", "/invi\ttations"),
    null,
    undefined,
    42,
    {},
  ]) {
    assert.equal(isInvitationPath(value, organization), false);
  }
});

test("requires exactly 64 lowercase hexadecimal secret characters", () => {
  for (const value of [
    "",
    secret.slice(1),
    secret + "a",
    secret.toUpperCase(),
    "g".repeat(64),
    "%30" + secret.slice(1),
    secret.slice(0, 63) + " ",
  ]) {
    assert.equal(isInvitationPath(`${route}#${value}`, organization), false);
  }
});
