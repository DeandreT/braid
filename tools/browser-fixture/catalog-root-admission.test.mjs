import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import vm from "node:vm";
import test from "node:test";
import ts from "typescript";
import * as rxjs from "rxjs";

const rootId = "11111111-1111-4111-8111-111111111111";
const otherId = "22222222-2222-4222-8222-222222222222";
const legacyPath = "/api/v1/catalog/context";
const qualifiedPath = (id = rootId) => `/api/v1/organizations/${id}/context`;
const identityContext = `v1.${"A".repeat(43)}`;

const fixtureExports = {};
vm.runInNewContext(
  ts.transpileModule(
    readFileSync(
      new URL(
        "../../apps/web/tests/organization-context.fixture.ts",
        import.meta.url,
      ),
      "utf8",
    ),
    {
      compilerOptions: {
        module: ts.ModuleKind.CommonJS,
        target: ts.ScriptTarget.ES2022,
      },
    },
  ).outputText,
  { exports: fixtureExports },
);

function context(token = "AAAA", organizationId = rootId) {
  return {
    mode: "session",
    identityContext,
    actorPrincipalId: "33333333-3333-4333-8333-333333333333",
    allowedOrganizationId: organizationId,
    organizationName: "Root",
    requestToken: token,
    features: Object.fromEntries(
      [
        "readCatalog",
        "createProjects",
        "createMappings",
        "editMetadata",
        "edit",
        "uploadRevisions",
        "readReports",
        "chat",
        "preview",
        "manageMembership",
        "deriveReports",
      ].map((key) => [key, true]),
    ),
    organizationActions: { createProjects: true },
  };
}

function envelope(version = "1", token = "AAAA", organizationId = rootId) {
  return {
    schemaVersion: "braid.organization-context.v1",
    admissionVersion: version,
    catalog: context(token, organizationId),
  };
}

// Only Angular DI/signals and the HTTP transport/session boundary are mocked. The
// store, API guards, selection aliases, owner identity and RxJS are production code.
function harness(selected = null, legacyReply = context()) {
  const providers = new Map();
  const signal = (initial) => {
    let value = initial;
    const read = () => value;
    read.set = (next) => (value = next);
    read.update = (update) => (value = update(value));
    read.asReadonly = () => () => value;
    return read;
  };
  class HttpClient {}
  class HttpErrorResponse extends Error {
    constructor(status) {
      super(`HTTP ${status}`);
      this.status = status;
    }
  }
  class BrowserSessionState {}
  const destroyRef = {};
  const mocks = {
    "@angular/core": {
      Injectable: () => (value) => value,
      inject: (key) => {
        assert.ok(providers.has(key), `Missing provider: ${key.name}`);
        return providers.get(key);
      },
      signal,
      DestroyRef: destroyRef,
    },
    "@angular/common/http": { HttpClient, HttpErrorResponse },
    rxjs,
  };
  const modules = new Map();
  function load(url) {
    if (url.pathname.endsWith("/auth/browser-session-state.ts"))
      return { BrowserSessionState };
    if (modules.has(url.href)) return modules.get(url.href);
    const exports = {};
    modules.set(url.href, exports);
    const { outputText } = ts.transpileModule(readFileSync(url, "utf8"), {
      compilerOptions: {
        module: ts.ModuleKind.CommonJS,
        target: ts.ScriptTarget.ES2022,
        experimentalDecorators: true,
      },
    });
    const require = (name) => {
      if (Object.hasOwn(mocks, name)) return mocks[name];
      assert.ok(name.startsWith("."), `Unexpected dependency: ${name}`);
      return load(new URL(`${name}.ts`, url));
    };
    vm.runInNewContext(
      outputText,
      { exports, require },
      {
        filename: fileURLToPath(url),
      },
    );
    return exports;
  }
  const source = (name) =>
    new URL(`../../apps/web/src/catalog/${name}.ts`, import.meta.url);
  const { CatalogApi } = load(source("catalog-api"));
  const { OrganizationSelection } = load(source("organization-selection"));
  const { CatalogContextStore } = load(source("catalog-context"));
  const selection = new OrganizationSelection();
  selection.select(selected);
  const pending = [];
  const requests = [];
  providers.set(HttpClient, {
    get: (path) => {
      requests.push(path);
      if (path === legacyPath)
        return legacyReply === null
          ? rxjs.throwError(() => new HttpErrorResponse(404))
          : rxjs.of(legacyReply);
      const reply = new rxjs.Subject();
      pending.push({ path, reply });
      return reply;
    },
  });
  const sessions = {
    invalidated: new rxjs.Subject(),
    routineSettled: new rxjs.Subject(),
    visible: signal(true),
    checking: signal(false),
    settled: signal(true),
    observe: () => rxjs.of(undefined),
    whenSettled: () => rxjs.of(undefined),
    generation: () => 0,
    state: () => ({ kind: "authenticated" }),
    accepts: (value) => value.identityContext === identityContext,
    presents: (value) => value.identityContext === identityContext,
    canDispatch: (value) =>
      !sessions.checking() && value.identityContext === identityContext,
    invalidate: () => assert.fail("Unexpected session identity invalidation"),
    rejectProtectedRequest: () => assert.fail("Unexpected session refusal"),
  };
  providers.set(BrowserSessionState, sessions);
  providers.set(OrganizationSelection, selection);
  providers.set(CatalogApi, new CatalogApi());
  providers.set(destroyRef, { onDestroy: () => {} });
  const store = new CatalogContextStore();
  return {
    store,
    selection,
    sessions,
    requests,
    respond(value, id = rootId) {
      const next = pending.shift();
      assert.ok(next, "Expected an authoritative organization context request");
      assert.equal(next.path, qualifiedPath(id));
      next.reply.next(value);
      next.reply.complete();
    },
    fail(status) {
      const next = pending.shift();
      assert.ok(next, "Expected an authoritative organization context request");
      next.reply.error(new HttpErrorResponse(status));
    },
  };
}

const result = (observable) => {
  const state = { values: [], error: null, complete: false };
  observable.subscribe({
    next: (value) => state.values.push(value),
    error: (error) => (state.error = error),
    complete: () => (state.complete = true),
  });
  return state;
};

function admit(h, version = "1", token = "AAAA") {
  const read = result(h.store.refresh());
  h.respond(envelope(version, token));
  assert.equal(read.error, null);
  assert.equal(read.complete, true);
  return read.values[0];
}

// A queue marker follows the store's observeOn(asapScheduler) continuation.
const settled = () =>
  new Promise((resolve) => rxjs.asapScheduler.schedule(resolve));

for (const alias of [null, rootId]) {
  const label = alias === null ? "unqualified" : "qualified";
  test(`${label} root: reactivation fences old lineage and pending sends in the same session`, async () => {
    const h = harness(alias);
    const old = admit(h);
    const refreshed = admit(h, "1", "BBBB");
    assert.equal(h.store.isCurrent(old), true);
    const revision = h.store.identityVersion();
    const read = result(h.store.refresh());
    const waiting = result(h.store.admitted(old));
    assert.deepEqual(waiting.values, []);
    assert.equal(waiting.error, null);
    h.respond(envelope("2", "CCCC"));
    const current = read.values[0];
    assert.equal(current.identityContext, old.identityContext);
    assert.equal(h.store.isCurrent(old), false);
    assert.equal(h.store.isCurrent(refreshed), false);
    assert.equal(h.store.isCurrent(current), true);
    assert.equal(h.store.identityVersion(), revision + 1);
    await settled();
    assert.deepEqual(waiting.values, []);
    assert.equal(waiting.error?.kind, "unavailable");
    const late = result(h.store.admitted(old));
    assert.deepEqual(late.values, []);
    assert.equal(late.error?.kind, "unavailable");
    assert.equal(result(h.store.admitted(current)).values[0], current);
  });

  test(`${label} root: same-incarnation refresh releases a waiter with the authoritative fresh token`, async () => {
    const h = harness(alias);
    const old = admit(h);
    const revision = h.store.identityVersion();
    const read = result(h.store.refresh());
    const waiting = result(h.store.admitted(old));
    assert.equal(h.store.isCurrent(old), true);
    assert.deepEqual(waiting.values, []);
    h.respond(envelope("1", "BBBB"));
    assert.deepEqual(waiting.values, []);
    await settled();
    assert.equal(waiting.error, null);
    assert.equal(waiting.values[0], read.values[0]);
    assert.equal(waiting.values[0].requestToken, "BBBB");
    assert.equal(h.store.isCurrent(old), true);
    assert.equal(h.store.identityVersion(), revision);
  });
}

test("browser fixture admission overrides keep discovery separate and retain API validation", async () => {
  const routes = [];
  const page = { route: async (...args) => routes.push(args) };
  let supplied = context("BBBB");
  await fixtureExports.routeOrganizationContext(
    page,
    (route) => route.fulfill({ json: supplied }),
    { times: 1 },
  );
  assert.equal(routes.length, 1);
  const [pattern, handler, options] = routes[0];
  assert.equal(pattern.test(`https://fixture.test${qualifiedPath()}`), true);
  assert.equal(pattern.test(`https://fixture.test${legacyPath}`), false);
  assert.equal(options.times, 1);
  for (const invalid of [false, true]) {
    const h = harness(rootId);
    const read = result(h.store.refresh());
    supplied = invalid ? context("BBBB", otherId) : context("BBBB");
    await handler({ fulfill: async (reply) => h.respond(reply.json) });
    assert.equal(read.error?.kind, invalid ? "unavailable" : undefined);
    assert.equal(read.values.length, invalid ? 0 : 1);
  }
  const refusal = { status: 404, json: { code: "notFound" } };
  assert.equal(fixtureExports.organizationContextReply(refusal), refusal);
  const malformed = { status: 200, body: "not JSON" };
  assert.equal(fixtureExports.organizationContextReply(malformed), malformed);
});

test("confirmed root aliases preserve selection and admission lineage in both directions", () => {
  const h = harness();
  const old = admit(h);
  const generation = h.selection.generation();
  assert.equal(h.selection.rootOrganizationId(), rootId);
  h.store.select(rootId);
  assert.equal(h.selection.organizationId(), rootId);
  assert.equal(h.selection.generation(), generation);
  assert.equal(h.store.isCurrent(old), true);
  admit(h, "1", "BBBB");
  assert.equal(h.requests.filter((path) => path === legacyPath).length, 1);
  h.store.select(null);
  assert.equal(h.selection.organizationId(), null);
  assert.equal(h.selection.generation(), generation);
  assert.equal(h.store.isCurrent(old), true);
  admit(h, "1", "CCCC");
  assert.equal(h.store.isCurrent(old), true);
  assert.equal(h.requests.filter((path) => path === legacyPath).length, 1);
  assert.equal(h.selection.link("/projects"), "/projects");
});

test("non-root discovery and rootless principals still read the selected qualified context", () => {
  for (const legacy of [context(), null]) {
    const h = harness(otherId, legacy);
    const read = result(h.store.refresh());
    h.respond(envelope("7", "BBBB", otherId), otherId);
    assert.equal(read.error, null);
    assert.equal(read.values[0].allowedOrganizationId, otherId);
    assert.equal(h.selection.rootOrganizationId(), legacy ? rootId : "");
    const again = result(h.store.refresh());
    h.respond(envelope("7", "CCCC", otherId), otherId);
    assert.equal(again.error, null);
    assert.equal(h.requests.filter((path) => path === legacyPath).length, 1);
  }
});

test("a session boundary clears cached root discovery and all admitted lineage", () => {
  const h = harness();
  const old = admit(h);
  h.sessions.invalidated.next();
  assert.equal(h.selection.rootOrganizationId(), null);
  assert.equal(h.store.isCurrent(old), false);
  assert.equal(result(h.store.admitted(old)).error?.kind, "unavailable");
  admit(h, "2", "BBBB");
  assert.equal(h.requests.filter((path) => path === legacyPath).length, 2);
  assert.equal(h.store.isCurrent(old), false);
});

test("unqualified routes remain root-only when root discovery is refused", () => {
  const h = harness(null, null);
  const read = result(h.store.refresh());
  assert.equal(read.error?.kind, "notFound");
  assert.deepEqual(h.requests, [legacyPath]);
  assert.equal(h.store.context(), null);
});

test("qualified context 404 is not retried as a root-discovery failure", () => {
  const h = harness(rootId);
  const read = result(h.store.refresh());
  h.fail(404);
  assert.equal(read.error?.kind, "notFound");
  assert.equal(h.selection.rootOrganizationId(), rootId);
  assert.deepEqual(h.requests, [legacyPath, qualifiedPath()]);
});

for (const alias of [null, rootId]) {
  test(`${alias === null ? "unqualified" : "qualified"} root: invalid authoritative contexts end lineage and refuse waiters`, async () => {
    for (const invalid of [
      { ...envelope(), admissionVersion: undefined },
      { ...envelope(), admissionVersion: "0" },
      { ...envelope(), admissionVersion: "01" },
      { ...envelope(), admissionVersion: "9223372036854775808" },
      { ...envelope(), schemaVersion: "wrong" },
      envelope("1", "BBBB", otherId),
    ]) {
      const h = harness(alias);
      const old = admit(h);
      const read = result(h.store.refresh());
      const waiting = result(h.store.admitted(old));
      h.respond(invalid);
      assert.equal(read.error?.kind, "unavailable");
      assert.equal(h.store.isCurrent(old), false);
      assert.equal(h.store.context(), null);
      await settled();
      assert.deepEqual(waiting.values, []);
      assert.equal(waiting.error?.kind, "unavailable");
    }
  });
}
