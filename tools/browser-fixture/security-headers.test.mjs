import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { join } from "node:path";
import test from "node:test";
import {
  applicationCsp,
  browserSecurityHeaders,
  missingSecurityHeaders,
} from "./security-headers.mjs";

const root = fileURLToPath(new URL("../../", import.meta.url));
const read = (path) => readFileSync(join(root, path), "utf8");

test("both shipped pipelines install the same policy before static files and admission", () => {
  const helper = read("dotnet/src/Weft.Api/BrowserSecurityHeaders.cs");
  assert.ok(helper.includes(`ContentSecurityPolicy = "${applicationCsp}"`));
  for (const path of ["Program.cs", "SessionHostEntrypoint.cs"]) {
    const source = read("dotnet/apps/Weft.Server/" + path);
    assert.ok(source.indexOf("app.UseWeftBrowserSecurityHeaders();") >= 0);
    assert.ok(
      source.indexOf("app.UseWeftBrowserSecurityHeaders();") <
        source.indexOf("app.UseDefaultFiles();"),
    );
    if (path === "SessionHostEntrypoint.cs")
      assert.ok(
        source.indexOf("app.UseWeftBrowserSecurityHeaders();") <
          source.indexOf("requests.Use(app);"),
      );
  }
  assert.doesNotMatch(applicationCsp, /(?:wasm-)?unsafe-eval/);
  const config = JSON.parse(read("apps/web/angular.json")).projects.weft
    .architect;
  assert.deepEqual(config.serve.options.headers, browserSecurityHeaders);
  assert.equal(config.serve.options.hmr, false);
  assert.equal(config.serve.options.liveReload, false);
});

test("app and probe omit forbidden base tags and inline critical-CSS script handlers", () => {
  const config = JSON.parse(read("apps/web/angular.json")).projects.weft
    .architect;
  for (const [target, index] of [
    ["build", "apps/web/index.html"],
    ["build", "apps/web/index.dev.html"],
    [
      "browser-session-probe",
      "apps/web/tests/browser-session/probe-index.html",
    ],
  ]) {
    assert.doesNotMatch(
      read(index),
      /<base\b|<script\b[^>]*>[^<]+|\son\w+\s*=/i,
    );
    assert.equal(config[target].options.baseHref, undefined);
    assert.ok(config[target].options.deployUrl.startsWith("/"));
    assert.equal(
      config[target].configurations.production.optimization.styles
        .inlineCritical,
      false,
    );
  }
  assert.match(
    read("apps/web/src/main.ts"),
    /provide: APP_BASE_HREF, useValue: "\/"/,
  );
  assert.deepEqual(config.build.configurations.devServer.assets[0].ignore, [
    "edit/**",
  ]);
});

test("a missing or changed header fails independently, including missing CSP", () => {
  assert.deepEqual(missingSecurityHeaders(browserSecurityHeaders), []);
  for (const name of Object.keys(browserSecurityHeaders)) {
    const missing = { ...browserSecurityHeaders };
    delete missing[name];
    assert.deepEqual(missingSecurityHeaders(missing), [name]);
    assert.deepEqual(
      missingSecurityHeaders({ ...browserSecurityHeaders, [name]: "weakened" }),
      [name],
    );
  }
});

test("the controlled session-probe server sends the shared headers", () => {
  assert.match(
    read("tools/browser-session-controlled/owner.mjs"),
    /\.\.\.browserSecurityHeaders,/,
  );
});
