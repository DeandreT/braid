import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { randomUUID } from "node:crypto";
import { createRequire } from "node:module";
import test from "node:test";
import vm from "node:vm";
import ts from "typescript";

const require = createRequire(import.meta.url);
const rx = require("rxjs");
const compile = async (path) =>
  ts.transpileModule(await readFile(new URL(path, import.meta.url), "utf8"), {
    compilerOptions: {
      module: ts.ModuleKind.CommonJS,
      target: ts.ScriptTarget.ES2022,
      experimentalDecorators: true,
    },
  }).outputText;
const source = await compile(
  "../../apps/web/src/catalog/sandbox/sandbox-page.component.ts",
);
const types = { exports: {} };
vm.runInNewContext(
  await compile("../../apps/web/src/catalog/catalog-types.ts"),
  { exports: types.exports, module: types },
);
const id = "11111111-1111-4111-8111-111111111111";
const owner = (actor = "A", extra = {}) => ({
  mode: "session",
  actorPrincipalId: actor,
  allowedOrganizationId: id,
  identityContext: "session-1",
  ...extra,
});
const signal = (initial) => {
  let value = initial;
  const read = () => value;
  read.set = (next) => {
    value = next;
  };
  read.update = (update) => {
    value = update(value);
  };
  return read;
};

function harness() {
  const effects = [];
  const microtasks = [];
  const calls = [];
  const invalidated = new rx.Subject();
  const params = new rx.Subject();
  const context = {
    current: null,
    fresh: owner(),
    invalidated,
    context: () => context.current,
    canPresent: (value) =>
      !!value &&
      !!context.current &&
      types.exports.catalogOwner(value) ===
        types.exports.catalogOwner(context.current),
    isCurrent: (value) => context.canPresent(value),
    refresh: () =>
      rx.defer(() => {
        const response = context.refreshResult ?? rx.of(context.fresh);
        return response.pipe(
          rx.tap((value) => {
            context.current = value;
          }),
        );
      }),
  };
  const state = {
    targets: [{ targetId: id }],
    builds: [{ buildId: id }],
    samples: [{ reference: "case-A" }],
  };
  const api = {};
  for (const name of ["state", "messages", "message", "send", "deploy"]) {
    api[name] = (...args) => {
      calls.push({ name, args });
      return (
        api[`${name}Result`] ?? rx.of(name === "state" ? state : { items: [] })
      );
    };
  }
  const operations = {
    read: (...args) => {
      calls.push({ name: "read", args });
      return rx.of({ items: [] });
    },
    apply: (...args) => {
      calls.push({ name: "apply", args });
      return operations.result ?? rx.of({ outcome: "applied" });
    },
  };
  const token = (name) => ({ name });
  const tokens = Object.fromEntries(
    [
      "CatalogContextStore",
      "SandboxApi",
      "OperationsApi",
      "ActivatedRoute",
      "DestroyRef",
      "DOCUMENT",
      "UnsentWork",
    ].map((name) => [name, token(name)]),
  );
  const services = {
    CatalogContextStore: context,
    SandboxApi: api,
    OperationsApi: operations,
    ActivatedRoute: { paramMap: params },
    DestroyRef: {},
    DOCUMENT: { defaultView: { confirm: () => true } },
    UnsentWork: {
      track: (fn) => {
        services.unsent = fn;
      },
    },
  };
  class HttpErrorResponse extends Error {
    constructor(status) {
      super("HTTP error");
      this.status = status;
    }
  }
  const modules = {
    "@angular/core": {
      ...tokens,
      signal,
      computed: (fn) => fn,
      effect: (fn) => {
        effects.push(fn);
        fn();
      },
      untracked: (fn) => fn(),
      inject: (key) => services[key.name],
      Component: () => (type) => type,
    },
    "@angular/common": tokens,
    "@angular/common/http": { HttpErrorResponse },
    "@angular/core/rxjs-interop": {
      takeUntilDestroyed: () => (stream) => stream,
    },
    "@angular/forms": { FormsModule: {} },
    "@angular/router": tokens,
    "../catalog-context": tokens,
    "../../workbench/unsent-work": tokens,
    "../catalog-types": types.exports,
    "../operations/operations-api": tokens,
    "./sandbox-api": tokens,
    rxjs: rx,
  };
  const module = { exports: {} };
  vm.runInNewContext(source, {
    exports: module.exports,
    module,
    require: (name) => {
      assert.ok(modules[name], name);
      return modules[name];
    },
    TextEncoder,
    TextDecoder,
    Uint8Array,
    URLSearchParams,
    atob,
    btoa,
    crypto: { randomUUID },
    queueMicrotask: (fn) => microtasks.push(fn),
  });
  const page = new module.exports.SandboxPageComponent();
  params.next({ get: () => id });
  return {
    page,
    context,
    api,
    operations,
    calls,
    microtasks,
    services,
    HttpErrorResponse,
    flushEffects: () => effects.forEach((fn) => fn()),
    invalidate: () => {
      context.current = null;
      invalidated.next();
    },
    draft: () => {
      page.source = "inline";
      page.primary = "private A";
      page.uploadedPrimary = {
        text: "private A",
        bytes: new Uint8Array([255]),
      };
      page.named = [{ name: "secret", text: "named A" }];
      page.detail.set({ messageId: "private-result" });
      page.comparison.set([{}, {}]);
      page.history.set([{}]);
      page.releases.set([{}]);
      page.rollbackSource = "release-A";
    },
  };
}

function assertCleared(page) {
  assert.equal(page.primary, "");
  assert.equal(page.uploadedPrimary, null);
  assert.equal(page.named.length, 0);
  for (const key of ["target", "build", "sample", "rollbackSource"])
    assert.equal(page[key], "");
  for (const key of [
    "state",
    "detail",
    "comparison",
    "pending",
    "retainedPending",
  ])
    assert.equal(page[key](), null);
  for (const key of ["history", "releases", "compare"])
    assert.equal(page[key]().length, 0);
  assert.equal(page.busy(), false);
}

test("routine same-owner refresh retains drafts, bytes and uncertain request identity; retry is explicit", () => {
  const h = harness();
  h.draft();
  h.api.sendResult = rx.throwError(() => new Error("lost receipt"));
  h.page.sendSample();
  const original = h.page.pending();
  const first = h.calls.find((call) => call.name === "send");
  assert.ok(original);
  h.context.fresh = owner("A", { requestToken: "rotated" });
  h.page.load();
  h.flushEffects();
  assert.equal(h.page.pending(), original);
  assert.equal(h.page.primary, "private A");
  assert.equal(h.page.named[0].text, "named A");
  assert.equal(h.calls.filter((call) => call.name === "send").length, 1);
  h.page.recover();
  const retry = h.calls.filter((call) => call.name === "send")[1];
  assert.equal(retry.args[3], first.args[3]);
  assert.equal(retry.args[4], first.args[4]);
  assert.equal(retry.args[0].requestToken, "rotated");
  assert.equal(retry.args[4].inputs[0].content, "/w==");
});

test("invalidation hides recovery but same-owner reauthentication preserves it without resending", () => {
  const h = harness();
  h.draft();
  h.api.sendResult = new rx.Subject();
  h.page.sendSample();
  const original = h.page.pending();
  h.invalidate();
  assert.equal(h.page.pending(), null);
  assert.equal(h.services.unsent(), true);
  assert.equal(h.page.detail(), null);
  h.api.sendResult.next({ messageId: "late" });
  assert.equal(h.page.detail(), null);
  h.page.load();
  h.flushEffects();
  assert.equal(h.page.pending(), original);
  assert.equal(h.page.primary, "private A");
  assert.equal(h.calls.filter((call) => call.name === "send").length, 1);
});

test("a different owner during refresh clears all private state and never dispatches A's send as B", () => {
  const h = harness();
  h.draft();
  h.context.fresh = owner("B");
  h.page.sendSample();
  assertCleared(h.page);
  assert.equal(h.calls.filter((call) => call.name === "send").length, 0);
  assert.equal(h.page.presented(), false);
  h.page.load();
  assert.equal(h.page.presented(), true);
  assert.equal(h.page.source, "case");
});

test("external context owner changes wipe retained recovery even after invalidation", () => {
  for (const changed of [
    owner("B"),
    owner("A", { identityContext: "session-2" }),
    owner("A", { allowedOrganizationId: "other-org" }),
  ]) {
    const h = harness();
    h.draft();
    h.api.sendResult = rx.throwError(() => new Error("uncertain"));
    h.page.sendSample();
    h.invalidate();
    h.context.current = changed;
    assert.equal(h.page.pending(), null);
    h.flushEffects();
    assertCleared(h.page);
    assert.equal(h.services.unsent(), false);
  }
});

test("late state response cannot repopulate state or start dependent reads under a replaced owner", () => {
  const h = harness();
  h.api.stateResult = new rx.Subject();
  h.page.load();
  const before = h.calls.length;
  h.context.current = owner("B");
  // Exercise the window before Angular runs its owner effect.
  h.api.stateResult.next({
    targets: [{ targetId: id }],
    builds: [],
    samples: [],
  });
  assert.equal(h.calls.length, before);
  h.flushEffects();
  assertCleared(h.page);
});

test("late detail and mutation callbacks cannot present A's output after B arrives", () => {
  for (const mutation of [false, true]) {
    const h = harness();
    const response = new rx.Subject();
    if (mutation) {
      h.draft();
      h.api.sendResult = response;
      h.page.sendSample();
    } else {
      h.api.messageResult = response;
      h.page.open({ messageId: id });
    }
    h.context.current = owner("B");
    response.next({ messageId: "A-result" });
    assert.notEqual(h.page.detail()?.messageId, "A-result");
    h.flushEffects();
    response.error(new h.HttpErrorResponse(409));
    assertCleared(h.page);
    assert.equal(h.page.message(), "");
    assert.equal(h.microtasks.length, 0);
  }
});

test("file completion and queued post-mutation reload are fenced by owner boundaries", async () => {
  const h = harness();
  let finish;
  const upload = h.page.upload({
    target: {
      files: [
        {
          size: 1,
          arrayBuffer: () =>
            new Promise((resolve) => {
              finish = resolve;
            }),
        },
      ],
    },
  });
  h.context.current = owner("B");
  h.flushEffects();
  finish(new Uint8Array([65]).buffer);
  await upload;
  assertCleared(h.page);

  const settled = harness();
  settled.draft();
  settled.page.sendSample();
  assert.equal(settled.microtasks.length, 1);
  const before = settled.calls.length;
  settled.context.current = owner("B");
  settled.flushEffects();
  settled.microtasks.shift()();
  assert.equal(settled.calls.length, before);
  assertCleared(settled.page);
});

test("read selection captured under A is not dispatched as B", () => {
  for (const action of [
    (page) => page.open({ messageId: id }),
    (page) => {
      page.compare.set([id, id]);
      page.runComparison();
    },
  ]) {
    const h = harness();
    h.draft();
    h.context.fresh = owner("B");
    action(h.page);
    assert.equal(h.calls.filter((call) => call.name === "message").length, 0);
    assertCleared(h.page);
  }
});

test("deploy and operation actions cannot cross owners, and same-owner operation retry keeps its original body", () => {
  for (const action of [
    (page) => page.deploy(),
    (page) => page.operate("disable"),
  ]) {
    const h = harness();
    h.context.fresh = owner("B");
    action(h.page);
    assert.equal(
      h.calls.filter((call) => ["deploy", "apply"].includes(call.name)).length,
      0,
    );
    assertCleared(h.page);
  }
  const h = harness();
  h.operations.result = rx.throwError(
    () => new Error("lost operation receipt"),
  );
  h.page.operate("disable");
  const original = h.calls.find((call) => call.name === "apply").args[2];
  h.invalidate();
  h.page.load();
  h.page.recover();
  const retry = h.calls.filter((call) => call.name === "apply")[1].args[2];
  assert.equal(retry.id, original.id);
  assert.equal(retry.owner, original.owner);
  assert.equal(retry.body, original.body);
  assert.equal(retry.target, original.target);
});

test("invalidation cancels refresh admission before any mutation is sent", () => {
  const h = harness();
  h.draft();
  const refresh = new rx.Subject();
  h.context.refreshResult = refresh;
  h.page.sendSample();
  const original = h.page.retainedPending();
  h.invalidate();
  refresh.next(owner("B"));
  assert.equal(h.calls.filter((call) => call.name === "send").length, 0);
  assert.equal(h.page.retainedPending(), original);
  h.context.refreshResult = null;
  h.page.load();
  assert.equal(h.page.pending(), original);
});
