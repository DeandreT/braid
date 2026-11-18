import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import vm from "node:vm";
import test from "node:test";
import ts from "typescript";
import * as rxjs from "rxjs";

const a = "11111111-1111-4111-8111-111111111111";
const b = "22222222-2222-4222-8222-222222222222";
const source =
  process.env.BRAID_LEAVE_BASELINE_SOURCE ??
  readFileSync(
    new URL("../../apps/web/src/app.routes.ts", import.meta.url),
    "utf8",
  );
const message =
  "Leave this workspace? Unsent work and recovery information kept only in this tab may be lost. A sent request may already have committed.";

// Execute the production route table and guard functions; only Angular DI and
// imported page/service tokens are replaced. No reimplementation of the guard.
function harness({
  selected = a,
  root = a,
  dirty = true,
  answer = false,
  visible = true,
} = {}) {
  const tokens = new Map();
  const token = (name) => {
    if (!tokens.has(name)) tokens.set(name, { name });
    return tokens.get(name);
  };
  const calls = [];
  let pageCalls = 0;
  const providers = {
    Injector: {},
    UnsentWork: { present: () => dirty },
    DOCUMENT: {
      defaultView: {
        confirm: (text) => {
          calls.push(text);
          return answer;
        },
      },
    },
    OrganizationSelection: {
      organizationId: () => selected,
      rootOrganizationId: () => root,
    },
    CatalogContextStore: {
      select: (id) => {
        calls.push(["select", id]);
        selected = id;
      },
    },
    BrowserSessionState: { visible: () => visible },
    Router: { parseUrl: (url) => ({ redirect: url }) },
  };
  const exports = {};
  vm.runInNewContext(
    ts.transpileModule(source, {
      compilerOptions: {
        module: ts.ModuleKind.CommonJS,
        target: ts.ScriptTarget.ES2022,
      },
    }).outputText,
    {
      exports,
      require: (id) => {
        if (id === "@angular/core")
          return {
            Injector: token("Injector"),
            runInInjectionContext: (_, next) => next(),
            inject: (key) => {
              assert.ok(key.name in providers, key.name);
              return providers[key.name];
            },
          };
        if (id === "rxjs") return rxjs;
        if (id === "./auth/sign-in-guards")
          return { requireSignIn: () => true, signInEntry: () => true };
        if (id === "@angular/router")
          return {
            Router: token("Router"),
            BaseRouteReuseStrategy: class {
              shouldReuseRoute(f, c) {
                return f.routeConfig === c.routeConfig;
              }
            },
          };
        if (id === "./catalog/catalog-types")
          return {
            isCatalogId: (id) =>
              /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/.test(id),
          };
        return new Proxy({}, { get: (_, name) => token(name) });
      },
    },
  );
  const routes = exports.routes;
  const catalog = routes.find((r) => r.path === "catalog");
  const standalone = routes.find(
    (r) => r.path === "organizations/:organizationId",
  );
  function snapshot(config, params = {}, parent = null) {
    const route = {
      routeConfig: config,
      params,
      paramMap: { get: (key) => params[key] ?? null },
      children: [],
      outlet: "primary",
    };
    route.pathFromRoot = [...(parent?.pathFromRoot ?? []), route];
    if (parent) parent.children.push(route);
    return route;
  }
  function destination(id, kind = "catalog") {
    const rootSnapshot = snapshot(null);
    if (kind === "entry") {
      const leaf = snapshot(
        routes.find((r) => r.path === "invitations"),
        {},
        rootSnapshot,
      );
      return {
        root: rootSnapshot,
        leaf,
        activate: () => leaf.routeConfig.canActivate[0](leaf),
      };
    }
    if (kind === "root") {
      const leaf = snapshot(
        routes.find((r) => r.path === "notifications"),
        {},
        rootSnapshot,
      );
      return {
        root: rootSnapshot,
        leaf,
        activate: () => leaf.routeConfig.canActivate.at(-1)(leaf),
      };
    }
    const parent = snapshot(
      kind === "catalog" ? catalog : standalone,
      kind === "catalog" ? {} : { organizationId: id },
      rootSnapshot,
    );
    const leaf = snapshot(
      kind === "catalog"
        ? catalog.children.find((r) => r.path.endsWith("/projects"))
        : standalone.children.find((r) => r.path === "notifications"),
      kind === "catalog" ? { organizationId: id } : {},
      parent,
    );
    return {
      root: rootSnapshot,
      leaf,
      activate: () => leaf.routeConfig.canActivate[0](leaf),
    };
  }
  const component = {
    requestLeave: () => {
      pageCalls++;
      return "page-decision";
    },
    requestChatLeave: () => {
      pageCalls++;
      return "page-decision";
    },
  };
  return {
    calls,
    providers,
    destination,
    exports,
    catalog,
    snapshot,
    pageCalls: () => pageCalls,
    deactivate: (path, next) =>
      catalog.children
        .find((r) => r.path.endsWith(path))
        .canDeactivate[0](component, {}, {}, next),
  };
}

test("aggregate cancellation precedes selection for catalog and inherited standalone snapshots", () => {
  for (const kind of ["catalog", "standalone", "root"]) {
    const h = harness({ selected: b });
    const next = h.destination(a, kind);
    assert.equal(next.activate(), false);
    assert.deepEqual(h.calls, [message]);
  }
});

test("all page guards defer cross-organization work to one aggregate decision", () => {
  for (const path of [
    "/edit",
    "/mappings/new",
    "/revisions/new",
    "/operations",
    "/automation",
    "/revisions/:revisionId",
  ]) {
    for (const answer of [false, true]) {
      const h = harness({ answer });
      const next = h.destination(b);
      assert.equal(h.deactivate(path, next), true);
      assert.equal(h.pageCalls(), 0);
      assert.equal(next.activate(), answer);
      assert.deepEqual(h.calls, answer ? [message, ["select", b]] : [message]);
    }
  }
});

test("same organization and confirmed root aliases keep page-specific decisions", () => {
  for (const [selected, id, kind] of [
    [a, a, "catalog"],
    [null, a, "catalog"],
    [a, null, "root"],
  ]) {
    const h = harness({ selected });
    const next = h.destination(id, kind);
    assert.equal(h.deactivate("/edit", next), "page-decision");
    assert.equal(h.pageCalls(), 1);
    assert.equal(next.activate(), true);
    assert.deepEqual(h.calls, [["select", id]]);
  }
});

test("clean switch does not ask; hidden work uses the same private-safe decision", () => {
  const clean = harness({ dirty: false });
  assert.equal(clean.destination(b).activate(), true);
  assert.deepEqual(clean.calls, [["select", b]]);
  const hidden = harness({ visible: false });
  const next = hidden.destination(b);
  assert.equal(hidden.deactivate("/edit", next), true);
  assert.equal(next.activate(), false);
  assert.deepEqual(hidden.calls, [message]);
});

test("missing and throwing confirmation fail closed for switching and auth entry", () => {
  for (const view of [
    null,
    {},
    {
      confirm: () => {
        throw Error("blocked");
      },
    },
  ]) {
    for (const kind of ["catalog", "entry"]) {
      const h = harness();
      h.providers.DOCUMENT.defaultView = view;
      assert.equal(h.destination(b, kind).activate(), false);
      assert.deepEqual(h.calls, []);
    }
  }
});

test("invalid standalone redirects before selection or aggregate prompting", () => {
  const h = harness();
  const next = h.destination("invalid", "standalone");
  assert.equal(next.activate().redirect, "/catalog");
  assert.deepEqual(h.calls, []);
});

test("auth entry owns one aggregate decision instead of a destructive page decision first", () => {
  const h = harness();
  const next = h.destination(null, "entry");
  assert.equal(h.deactivate("/edit", next), true);
  assert.equal(next.activate(), false);
  assert.equal(h.pageCalls(), 0);
  assert.deepEqual(h.calls, [message]);
});

test("route reuse retains same-org pages and remounts changed organization params", () => {
  const h = harness();
  const strategy = new h.exports.OrganizationRouteReuseStrategy();
  const config = h.catalog.children[0];
  assert.equal(
    strategy.shouldReuseRoute(
      h.snapshot(config, { organizationId: a }),
      h.snapshot(config, { organizationId: a }),
    ),
    true,
  );
  assert.equal(
    strategy.shouldReuseRoute(
      h.snapshot(config, { organizationId: b }),
      h.snapshot(config, { organizationId: a }),
    ),
    false,
  );
});
