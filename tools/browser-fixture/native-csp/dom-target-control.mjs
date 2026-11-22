// Stock-only defensive fixture. Only task-owned .invalid responses are served.
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { createHash } from "node:crypto";
import { createReadStream } from "node:fs";
import { createRequire } from "node:module";
import { isAbsolute, join } from "node:path";
import { assertTargetObservation, cases } from "./dom-target-expectations.mjs";

const origin = "http://braid.invalid";
const adoptInto = `${origin}/other-document`;
const documents = Object.freeze({
  html: {
    mime: "text/html",
    source: "<!doctype html><title>HTML target fixture</title>",
  },
  xhtml: {
    mime: "application/xhtml+xml",
    source:
      '<html xmlns="http://www.w3.org/1999/xhtml"><head><title>XHTML target fixture</title></head><body /></html>',
  },
  svg: {
    mime: "image/svg+xml",
    source:
      '<svg xmlns="http://www.w3.org/2000/svg"><title>SVG target fixture</title></svg>',
  },
});

const requested = process.argv.slice(2);
assert(
  requested.every((mode) => Object.hasOwn(cases, mode)) &&
    new Set(requested).size === requested.length,
  "unknown or repeated fixture mode",
);
const modes = requested.length ? requested : Object.keys(cases);
const [loopback, ...others] = JSON.parse(
  execFileSync("/usr/bin/ip", ["-j", "link", "show"], { encoding: "utf8" }),
);
assert.equal(others.length, 0);
assert.equal(loopback?.ifname, "lo");
assert(!loopback.flags.includes("UP"));
assert.notEqual(process.getuid(), 0);

async function verify(path, pin) {
  assert(path && isAbsolute(path), "absolute local artifact path required");
  const hash = createHash("sha256");
  for await (const bytes of createReadStream(path)) hash.update(bytes);
  assert.equal(hash.digest("hex"), pin, "stock artifact digest differs");
}

const executablePath = process.env.BRAID_CHROME;
const driver = process.env.BRAID_DRIVER;
assert(driver && isAbsolute(driver));
await verify(
  executablePath,
  "b9f56b6d4d5e791dc5d56d8218abf2d7ca09bc218719b27043205b3ebf86c46d",
);
await verify(
  join(driver, "lib/coreBundle.js"),
  "2325ca4c1c83070e89dc49fb82525508f0a68e0cf12e2a624277968da216b7e0",
);
const { chromium } = createRequire(import.meta.url)(driver);
const browser = await chromium.launch({
  executablePath,
  headless: true,
  chromiumSandbox: true,
  timeout: 15000,
  env: {
    PATH: "/usr/bin",
    HOME: process.env.HOME,
    TMPDIR: process.env.TMPDIR,
  },
});

try {
  assert.equal(browser.version(), "157.0.8090.0");
  for (const mode of modes) {
    const { document: kind, mutation } = cases[mode];
    const originalUrl = `${origin}/${mode}`;
    const { mime, source } = documents[kind];
    const context = await browser.newContext({ serviceWorkers: "block" });
    try {
      await context.route("**/*", async (route) => {
        const url = route.request().url();
        if (url === originalUrl) {
          return route.fulfill({
            status: 200,
            headers: {
              "content-type": mime,
              "content-security-policy": "style-src 'none'",
              "x-content-type-options": "nosniff",
            },
            body:
              source +
              (mutation === "adopt"
                ? `<iframe src="${adoptInto}"></iframe>`
                : ""),
          });
        }
        if (mutation === "adopt" && url === adoptInto) {
          return route.fulfill({
            status: 200,
            contentType: "text/html",
            body: "<!doctype html><title>Other original fixture document</title>",
          });
        }
        return route.abort();
      });

      const page = await context.newPage();
      page.setDefaultTimeout(6000);
      const response = await page.goto(originalUrl, { timeout: 8000 });
      assert.equal(response.status(), 200);
      assert.equal(await response.headerValue("content-type"), mime);
      const watched = await page.evaluateHandle(
        ({ mutation, adoptInto }) => {
          const original = document;
          const ns = original.documentElement.namespaceURI;
          const container = original.body ?? original.documentElement;
          const adoption = mutation === "adopt";
          const other = adoption
            ? original.querySelector("iframe").contentDocument
            : null;
          if (
            adoption &&
            (!other?.body || other.URL !== adoptInto || other === original)
          ) {
            throw new Error("second live document not loaded");
          }

          const isShadow = mutation === "open" || mutation === "closed";
          const host = isShadow
            ? container.appendChild(original.createElement("div"))
            : null;
          const root = host?.attachShadow({ mode: mutation });
          const style = original.createElementNS(ns, "style");
          style.textContent =
            ".braid-csp-probe { --braid-csp-probe: applied; }";
          const probeNode = original.createElementNS(
            ns,
            ns.endsWith("/svg") ? "g" : "span",
          );
          probeNode.setAttribute("class", "braid-csp-probe");
          (root ?? container).append(probeNode);
          if (other) other.documentElement.classList.add("braid-csp-probe");

          const labels = new Map(
            [
              [original, "original"],
              [style, "style"],
              [host, "host"],
              [root, "shadow"],
              [other, "other"],
            ].filter(([node]) => node),
          );
          const label = (node) => labels.get(node) ?? "unlabeled";
          const events = [];
          const identities = new WeakMap();
          let eventCount = 0;
          for (const [node, listener] of labels) {
            node.addEventListener(
              "securitypolicyviolation",
              (event) => {
                if (!identities.has(event)) identities.set(event, ++eventCount);
                events.push({
                  listener,
                  serializedTarget: label(event.target),
                  current: label(event.currentTarget),
                  path: event.composedPath().map(label),
                  phase: event.eventPhase,
                  id: identities.get(event),
                  trusted: event.isTrusted,
                  genuineType: event instanceof SecurityPolicyViolationEvent,
                  bubbles: event.bubbles,
                  composed: event.composed,
                  cancelable: event.cancelable,
                  blocked: event.blockedURI,
                  effective: event.effectiveDirective,
                  violated: event.violatedDirective,
                  disposition: event.disposition,
                  policy: event.originalPolicy,
                  uri: event.documentURI,
                  currentDocumentIsOriginal: document === original,
                  elementConnected: style.isConnected,
                  owner: label(style.ownerDocument),
                });
              },
              listener === "original" || listener === "other",
            );
          }

          (root ?? container).append(style);
          const checkedConnectedOriginal =
            style.isConnected &&
            style.ownerDocument === original &&
            style.sheet === null;
          if (mutation === "remove") {
            style.remove();
          } else if (adoption) {
            other.body.append(other.adoptNode(style));
          }
          // All mutation and enrollment checks share a JS task with insertion.
          // The CSP networking task cannot have called the listener yet.
          const callsBeforeReturn = events.length;
          return {
            originalSawEvent() {
              return events.some(({ listener }) => listener === "original");
            },
            snapshot() {
              return {
                callsBeforeReturn,
                checkedConnectedOriginal,
                events,
                eventCount,
                documentType: {
                  mime: original.contentType,
                  html: original instanceof HTMLDocument,
                  xml: original instanceof XMLDocument,
                  root: original.documentElement.localName,
                  namespace: ns,
                },
                connected: style.isConnected,
                owner: label(style.ownerDocument),
                styleAppliedInOther: adoption
                  ? other.defaultView
                      .getComputedStyle(other.documentElement)
                      .getPropertyValue("--braid-csp-probe")
                      .trim() === "applied"
                  : null,
                originalStyleUnchanged:
                  getComputedStyle(probeNode)
                    .getPropertyValue("--braid-csp-probe")
                    .trim() === "",
              };
            },
          };
        },
        { mutation, adoptInto },
      );
      try {
        // A listener invocation is the positive barrier, never a quiet delay.
        await page.waitForFunction(
          (probe) => probe.originalSawEvent(),
          watched,
        );
        const observation = await watched.evaluate((probe) => probe.snapshot());
        assertTargetObservation(mode, originalUrl, observation);
        console.log(
          JSON.stringify({
            mode,
            document: observation.documentType,
            recipients: observation.events.map(
              ({ listener, serializedTarget, path, phase }) => ({
                listener,
                target: serializedTarget,
                pathStart: path[0],
                phase,
              }),
            ),
          }),
        );
      } finally {
        await watched.dispose();
      }
    } finally {
      await context.close();
    }
  }
} finally {
  await browser.close();
}
