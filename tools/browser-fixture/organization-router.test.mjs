import "@angular/compiler";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import vm from "node:vm";
import test from "node:test";
import ts from "typescript";
import * as core from "@angular/core";
import { DOCUMENT } from "@angular/common";
import { provideLocationMocks } from "@angular/common/testing";
import * as routing from "@angular/router";
import { TestBed } from "@angular/core/testing";
import {
  BrowserTestingModule,
  platformBrowserTesting,
} from "@angular/platform-browser/testing";
import * as rxjs from "rxjs";

const a = "11111111-1111-4111-8111-111111111111";
const b = "22222222-2222-4222-8222-222222222222";
const projects = (id) => `/catalog/organizations/${id}/projects`;
const administration = (id) => `/organizations/${id}/administration`;
const message =
  "Leave this workspace? Unsent work and recovery information kept only in this tab may be lost. A sent request may already have committed.";
class BrowserSessionState {}
class CatalogContextStore {}
class OrganizationSelection {}
class UnsentWork {}
class Page {}
core.Component({ standalone: true, template: "" })(Page);
const mocks = {
  "@angular/core": core,
  "@angular/common": { DOCUMENT },
  "@angular/router": routing,
  rxjs,
  "./auth/browser-session-state": { BrowserSessionState },
  "./catalog/catalog-context": { CatalogContextStore },
  "./catalog/organization-selection": { OrganizationSelection },
  "./workbench/unsent-work": { UnsentWork },
  "./catalog/catalog-types": {
    isCatalogId: (id) =>
      /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/.test(id),
  },
  "./return-path": {
    defaultReturnPath: "/catalog",
    isReturnPath: (url) => url.startsWith("/"),
  },
  "./browser-session-state": { BrowserSessionState },
};
function load(file, override) {
  const exports = {};
  vm.runInNewContext(
    ts.transpileModule(
      override ??
        readFileSync(
          new URL(`../../apps/web/src/${file}`, import.meta.url),
          "utf8",
        ),
      {
        compilerOptions: {
          module: ts.ModuleKind.CommonJS,
          target: ts.ScriptTarget.ES2022,
        },
      },
    ).outputText,
    {
      exports,
      require: (id) => mocks[id] ?? new Proxy({}, { get: () => Page }),
    },
  );
  return exports;
}
mocks["./auth/sign-in-guards"] = load("auth/sign-in-guards.ts");
const production = load(
  "app.routes.ts",
  process.env.BRAID_LEAVE_BASELINE_SOURCE,
);
// The real route structure, production guards and reuse strategy run through
// Angular recognition/preactivation/redirects. Pages are inert, with no DOM.
function pages(routes) {
  return routes.map(({ loadComponent, component, children, ...route }) => ({
    ...route,
    ...(loadComponent || component ? { component: Page } : {}),
    ...(children ? { children: pages(children) } : {}),
  }));
}
TestBed.initTestEnvironment(BrowserTestingModule, platformBrowserTesting());
async function harness() {
  let selected = b;
  let dirty = false;
  let kind = "authenticated";
  let answer = false;
  const observed = new rxjs.Subject();
  const calls = [];
  const document = {
    defaultView: {
      confirm: (text) => {
        calls.push(["prompt", text]);
        return answer;
      },
    },
  };
  TestBed.configureTestingModule({
    providers: [
      core.provideZonelessChangeDetection(),
      routing.provideRouter(pages(production.routes)),
      provideLocationMocks(),
      { provide: DOCUMENT, useValue: document },
      {
        provide: routing.RouteReuseStrategy,
        useClass: production.OrganizationRouteReuseStrategy,
      },
      {
        provide: BrowserSessionState,
        useValue: {
          state: () => ({ kind }),
          visible: () => kind === "authenticated",
          observe: () => observed,
        },
      },
      {
        provide: OrganizationSelection,
        useValue: {
          organizationId: () => selected,
          rootOrganizationId: () => a,
        },
      },
      {
        provide: CatalogContextStore,
        useValue: {
          select: (id) => {
            calls.push(["select", id]);
            selected = id;
          },
        },
      },
      { provide: UnsentWork, useValue: { present: () => dirty } },
    ],
  });
  const router = TestBed.inject(routing.Router);
  // Register an inert outlet so Angular also schedules real canDeactivate
  // checks. Rendering is outside this test; activation/reuse/guard ordering isn't.
  function outlet(contexts) {
    const view = {
      isActivated: false,
      component: {
        requestLeave: () => {
          calls.push(["page-decision"]);
          return rxjs.of(false);
        },
        requestChatLeave: () => true,
      },
      activateWith: () => {
        view.isActivated = true;
        outlet(contexts.getOrCreateContext("primary").children);
      },
      deactivate: () => {
        view.isActivated = false;
      },
    };
    contexts.onChildOutletCreated("primary", view);
  }
  outlet(TestBed.inject(routing.ChildrenOutletContexts));
  await router.navigateByUrl(administration(b));
  calls.length = 0;
  dirty = true;
  return {
    router,
    calls,
    selected: () => selected,
    dirty: (v) => (dirty = v),
    kind: (v) => (kind = v),
    answer: (v) => (answer = v),
    observed,
    dispose: () => TestBed.resetTestingModule(),
  };
}

for (const destination of [
  "/catalog",
  "/",
  "/organizations/invalid/notifications",
  projects(a),
]) {
  test(`real Router cancellation before selection: ${destination}`, async () => {
    const h = await harness();
    try {
      assert.equal(await h.router.navigateByUrl(destination), false);
      assert.equal(h.router.url, administration(b));
      assert.equal(h.selected(), b);
      assert.deepEqual(h.calls, [["prompt", message]]);
    } finally {
      h.dispose();
    }
  });
}

test("real Router auth redirect asks once and cancel preserves the source selection", async () => {
  const h = await harness();
  try {
    h.kind("anonymous");
    assert.equal(
      await h.router.navigateByUrl(`/organizations/${a}/notifications`),
      false,
    );
    assert.equal(h.router.url, administration(b));
    assert.equal(h.selected(), b);
    assert.deepEqual(h.calls, [["prompt", message]]);
    h.calls.length = 0;
    h.answer(true);
    assert.equal(
      await h.router.navigateByUrl(`/organizations/${a}/notifications`),
      true,
    );
    assert.match(h.router.url, /^\/sign-in\?/);
    assert.equal(h.selected(), b);
    assert.deepEqual(h.calls, [["prompt", message]]);
  } finally {
    h.dispose();
  }
});

test("real Router signed-in sign-in redirect leaves the decision to catalog landing", async () => {
  const h = await harness();
  try {
    assert.equal(await h.router.navigateByUrl("/sign-in"), false);
    assert.equal(h.router.url, administration(b));
    assert.deepEqual(h.calls, [["prompt", message]]);
  } finally {
    h.dispose();
  }
});

test("real Router reused catalog shell guards its empty landing child", async () => {
  const h = await harness();
  try {
    h.dirty(false);
    await h.router.navigateByUrl(projects(b));
    h.dirty(true);
    h.calls.length = 0;
    assert.equal(await h.router.navigateByUrl("/catalog"), false);
    assert.equal(h.router.url, projects(b));
    assert.equal(h.selected(), b);
    assert.deepEqual(h.calls, [["prompt", message]]);
  } finally {
    h.dispose();
  }
});

test("real Router deactivation defers page+shared work through invalid-link redirects", async () => {
  const h = await harness();
  try {
    h.dirty(false);
    const edit = `/catalog/organizations/${b}/projects/${a}/mappings/${a}/edit`;
    await h.router.navigateByUrl(edit);
    h.dirty(true);
    h.calls.length = 0;
    assert.equal(
      await h.router.navigateByUrl("/organizations/invalid/notifications"),
      false,
    );
    assert.equal(h.router.url, edit);
    assert.equal(h.selected(), b);
    assert.deepEqual(h.calls, [["prompt", message]]);
    h.calls.length = 0;
    assert.equal(await h.router.navigateByUrl(projects(b)), false);
    assert.deepEqual(h.calls, [["page-decision"]]);
  } finally {
    h.dispose();
  }
});

test("real Router pending auth settles before any leave decision or mutation", async () => {
  const h = await harness();
  try {
    h.kind("checking");
    const navigation = h.router.navigateByUrl(
      `/organizations/${a}/notifications`,
    );
    await new Promise((resolve) => setImmediate(resolve));
    assert.deepEqual(h.calls, []);
    h.kind("anonymous");
    h.observed.next();
    assert.equal(await navigation, false);
    assert.equal(h.router.url, administration(b));
    assert.equal(h.selected(), b);
    assert.deepEqual(h.calls, [["prompt", message]]);
  } finally {
    h.dispose();
  }
});

test("real Router accepted landing selects only after its single decision", async () => {
  const h = await harness();
  try {
    h.answer(true);
    assert.equal(await h.router.navigateByUrl("/catalog"), true);
    assert.equal(h.router.url, "/catalog");
    assert.deepEqual(h.calls, [
      ["prompt", message],
      ["select", null],
    ]);
    h.calls.length = 0;
    assert.equal(await h.router.navigateByUrl(projects(a)), true);
    assert.deepEqual(h.calls, [["select", a]]);
    h.calls.length = 0;
    assert.equal(await h.router.navigateByUrl("/notifications"), true);
    assert.deepEqual(h.calls, [["select", null]]);
  } finally {
    h.dispose();
  }
});

for (const destination of [projects(b), "/help"]) {
  test(`real Router page decision cannot precede an auth redirect: ${destination}`, async () => {
    const h = await harness();
    try {
      h.dirty(false);
      const edit = `/catalog/organizations/${b}/projects/${a}/mappings/${a}/edit`;
      await h.router.navigateByUrl(edit);
      h.dirty(true);
      h.kind("anonymous");
      h.calls.length = 0;
      assert.equal(await h.router.navigateByUrl(destination), false);
      assert.equal(h.router.url, edit);
      assert.equal(h.selected(), b);
      assert.deepEqual(h.calls, [["prompt", message]]);
    } finally {
      h.dispose();
    }
  });
}
