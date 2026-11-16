import assert from "node:assert/strict";
import { File } from "node:buffer";
import test from "node:test";
import { readFileSync } from "node:fs";
import { parseTemplate } from "@angular/compiler";
import { firstValueFrom, of, throwError } from "rxjs";
import { mfdProtectedResponse } from "../apps/web/src/catalog/uploads/mfd-request-boundary.ts";
import {
  mfdPackage,
  readMfdReview,
  mfdFailureOutcome,
} from "../apps/web/src/catalog/uploads/mfd-interchange.ts";

test("the MFD child is not owned by the transient native form or project admission", () => {
  const template = parseTemplate(
    readFileSync(
      new URL(
        "../apps/web/src/catalog/uploads/upload-mapping-page.component.html",
        import.meta.url,
      ),
      "utf8",
    ),
    "upload",
  );
  assert.equal(template.errors, null);
  const parents = [];
  function visit(nodes, conditions = []) {
    for (const node of nodes) {
      if (node.name === "braid-mfd-import") parents.push(conditions);
      if (node.children) visit(node.children, conditions);
      for (const branch of node.branches ?? [])
        visit(branch.children, [
          ...conditions,
          branch.expression?.source ?? "",
        ]);
    }
  }
  visit(template.nodes);
  assert.equal(parents.length, 1);
  assert.deepEqual(parents[0], ["mfdProject()"]);
});

for (const status of [401, 403]) {
  test(`current MFD ${status} rejection uses the original protected boundary`, async () => {
    const context = { mode: "session", identityContext: "original" };
    const error = { status };
    const rejected = [];
    const boundary = {
      generation: () => 7,
      accepts: (value) => value === context,
      rejectProtectedRequest: (generation, value) =>
        rejected.push([generation, value]),
    };
    await assert.rejects(
      firstValueFrom(
        throwError(() => error).pipe(
          mfdProtectedResponse(boundary, context, 7, (value) => value.status),
        ),
      ),
      (value) => value === error,
    );
    assert.deepEqual(rejected, [[7, context]]);
  });
}

for (const changed of ["generation", "identity"]) {
  test(`a late MFD response cannot affect a newer ${changed}`, async () => {
    const context = { mode: "session", identityContext: "original" };
    let rejected = 0;
    const boundary = {
      generation: () => (changed === "generation" ? 8 : 7),
      accepts: () => changed !== "identity",
      rejectProtectedRequest: () => rejected++,
    };
    const protect = () =>
      mfdProtectedResponse(boundary, context, 7, (value) => value.status);
    await assert.rejects(
      firstValueFrom(of({ privateReview: true }).pipe(protect())),
      /no longer current/,
    );
    await assert.rejects(
      firstValueFrom(throwError(() => ({ status: 403 })).pipe(protect())),
      /no longer current/,
    );
    assert.equal(rejected, 0);
  });
}

test("routine checks preserve accepted replies and Development never starts Session recovery", async () => {
  const context = { mode: "development" };
  let rejected = 0;
  const boundary = {
    generation: () => 7,
    accepts: () => true,
    rejectProtectedRequest: () => rejected++,
  };
  const protect = () =>
    mfdProtectedResponse(boundary, context, 7, (value) => value.status);
  assert.equal(
    await firstValueFrom(of("kept review").pipe(protect())),
    "kept review",
  );
  const error = { status: 403 };
  await assert.rejects(
    firstValueFrom(throwError(() => error).pipe(protect())),
    (value) => value === error,
  );
  assert.equal(rejected, 0);
});

test("non-success statuses never become a claimed saved revision", () => {
  assert.equal(mfdFailureOutcome(503, { outcome: "committedValid" }), null);
  assert.equal(
    mfdFailureOutcome(409, { outcome: "committedDecodedInvalid" }),
    null,
  );
  assert.equal(
    mfdFailureOutcome(401, { outcome: "notAuthorizedOrNotFound" }),
    null,
  );
  assert.equal(mfdFailureOutcome(409, { code: "preconditionFailed" }), null);
  assert.equal(
    mfdFailureOutcome(409, { outcome: "headConflict" }).outcome,
    "headConflict",
  );
  assert.equal(
    mfdFailureOutcome(503, { outcome: "retryableFailure" }).outcome,
    "retryableFailure",
  );
});

test("package framing preserves exact bytes and uses Rust-compatible UTF-8 path order", async () => {
  const files = [
    new File([new Uint8Array([0, 255])], "\u{10000}.json"),
    new File(["b"], "\ue000.json"),
    new File(["<self-authored/>"], "design.mfd"),
  ];
  const bytes = new Uint8Array(
    await (await mfdPackage(files, "design.mfd")).arrayBuffer(),
  );
  const view = new DataView(bytes.buffer),
    decoder = new TextDecoder("utf-8", { fatal: true });
  let offset = 0;
  const number = () => {
    const n = view.getUint32(offset, false);
    offset += 4;
    return n;
  };
  const field = () => {
    const n = number();
    const value = bytes.slice(offset, offset + n);
    offset += n;
    return value;
  };
  assert.equal(decoder.decode(field()), "braid.interchange-package.v1");
  assert.equal(decoder.decode(field()), "design.mfd");
  assert.equal(number(), 3);
  const paths = [],
    values = [];
  for (let i = 0; i < 3; i++) {
    paths.push(decoder.decode(field()));
    values.push(field());
  }
  assert.deepEqual(paths, ["design.mfd", "\ue000.json", "\u{10000}.json"]);
  assert.deepEqual([...values[2]], [0, 255]);
  assert.equal(offset, bytes.length);
});

test("package refuses traversal, duplicate paths and oversized files before upload", async () => {
  await assert.rejects(
    mfdPackage([new File(["x"], "../design.mfd")], "../design.mfd"),
  );
  await assert.rejects(
    mfdPackage(
      [new File(["x"], "design.mfd"), new File(["y"], "design.mfd")],
      "design.mfd",
    ),
  );
  await assert.rejects(
    mfdPackage(
      [new File([new Uint8Array(4 * 1024 * 1024 + 1)], "design.mfd")],
      "design.mfd",
    ),
  );
});

test("review parsing refuses truncated/oversized warnings and inconsistent destinations", () => {
  const review = {
    reviewId: "11111111-1111-1111-1111-111111111111",
    reviewDigest: "a".repeat(64),
    packageDigest: "b".repeat(64),
    nativeDigest: "c".repeat(64),
    engine: {
      backendId: "ferrule",
      engineRevision: "pinned",
      backendBuild: "build",
      adapterBuild: "adapter",
    },
    summary: "Summary",
    warnings: [{ code: "unsupported", message: "Explicit warning" }],
    expiresAt: "2026-10-24T15:00:00Z",
    targetMappingId: null,
    expectedHeadRevisionId: null,
    mappingName: "Imported",
  };
  assert.equal(readMfdReview(review).mappingName, "Imported");
  assert.throws(() =>
    readMfdReview({ ...review, warnings: Array(257).fill(review.warnings[0]) }),
  );
  assert.throws(() =>
    readMfdReview({ ...review, expectedHeadRevisionId: review.reviewId }),
  );
  assert.throws(() =>
    readMfdReview({ ...review, reviewDigest: "A".repeat(64) }),
  );
});
