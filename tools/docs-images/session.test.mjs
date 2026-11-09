import assert from "node:assert/strict";
import { existsSync } from "node:fs";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import http from "node:http";
import { createRequire } from "node:module";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { setTimeout as pause } from "node:timers/promises";
import test from "node:test";
import { lifecycleFixture } from "./lifecycle.fixture.mjs";
import { startDocsSession, stopDocsSessions } from "./session.mjs";
import { startDocsServer } from "./server.mjs";

const require = createRequire(import.meta.url);
const { launchProcess } = require("playwright-core/lib/coreBundle").utils;
const records = async (path) =>
  (await readFile(path, "utf8")).trim().split("\n").map(JSON.parse);
const alive = (pid) => {
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    if (error.code === "ESRCH") return false;
    throw error;
  }
};

test(
  "pinned Playwright global timeout closes a registered synthetic build tree and its scratch",
  { timeout: 20000 },
  async (t) => {
    const directory = await mkdtemp(join(tmpdir(), "weft-docs-cancel-test-"));
    let owner,
      exited = false,
      fixture;
    t.after(async () => {
      await owner?.gracefullyClose();
      // Synthetic descendants also have a 15s failsafe; a regression is still a
      // failure, not an orphan retained after this test's bounded cleanup.
      if (fixture && existsSync(fixture.receipt)) {
        const deadline = Date.now() + 15000;
        const known = await records(fixture.receipt);
        while (known.some((item) => alive(item.pid)) && Date.now() < deadline)
          await pause(25);
        assert.ok(
          known.every((item) => !alive(item.pid)),
          "Synthetic owned descendants remain alive",
        );
      }
      await rm(directory, { recursive: true, force: true });
    });
    fixture = await lifecycleFixture(directory);
    let output = "",
      complete;
    const closed = new Promise((fulfil) => {
      complete = fulfil;
    });
    owner = await launchProcess({
      command: process.execPath,
      args: [
        require.resolve("@playwright/test/cli"),
        "test",
        "--config",
        fixture.config,
      ],
      env: process.env,
      stdio: "stdin",
      tempDirectories: [],
      log(line) {
        output = (output + line + "\n").slice(-32768);
      },
      onExit(code, signal) {
        exited = true;
        complete({ code, signal });
      },
      attemptToGracefullyClose: async () => {
        if (!exited) throw new Error("Stop only the owned synthetic CLI tree");
      },
    });
    const result = await closed;
    assert.notEqual(result.code, 0);
    assert.match(output, /Timed out waiting 4s for the global setup/);
    const known = await records(fixture.receipt);
    assert.deepEqual(known.map((item) => item.role).sort(), [
      "builder",
      "grandchild",
      "worker",
    ]);
    const worker = known.find((item) => item.role === "worker");
    const builder = known.find((item) => item.role === "builder");
    const grandchild = known.find((item) => item.role === "grandchild");
    assert.equal(builder.parent, worker.pid);
    assert.equal(grandchild.parent, builder.pid);
    assert.ok(
      known.every((item) => !alive(item.pid)),
      "Registered launcher did not settle its actual owned process tree",
    );
    assert.ok(
      known.every((item) => Date.now() < item.expiresAt - 1000),
      "Natural fixture expiry cannot qualify cancellation cleanup",
    );
    assert.equal(existsSync(worker.root), false);
  },
);

test(
  "normal stop, setup errors and bind conflicts settle only their own session",
  { timeout: 15000 },
  async (t) => {
    const directory = await mkdtemp(join(tmpdir(), "weft-docs-startup-test-"));
    let host;
    t.after(async () => {
      await stopDocsSessions();
      await host?.stop();
      await rm(directory, { recursive: true, force: true });
    });
    const failed = await lifecycleFixture(join(directory, "failed"), {
      mode: "error",
    });
    await assert.rejects(
      startDocsSession({ worker: failed.worker }),
      /synthetic build failed/,
    );
    const failedRoot = (await records(failed.receipt))[0].root;
    assert.equal(existsSync(failedRoot), false);
    const ready = await lifecycleFixture(join(directory, "ready"), {
      mode: "ready",
    });
    const session = await startDocsSession({ worker: ready.worker });
    assert.equal(existsSync(session.scratch), true);
    await session.stop();
    assert.equal(session.child.exitCode, 0);
    assert.equal(existsSync(session.scratch), false);
    const root = join(directory, "occupied");
    await mkdir(root);
    await writeFile(join(root, "index.html"), "owned listener");
    host = await startDocsServer(root, 0);
    const fixture = await lifecycleFixture(join(directory, "conflict"), {
      mode: "ready",
      port: Number(new URL(host.origin).port),
    });
    await assert.rejects(startDocsSession({ worker: fixture.worker }), {
      code: "EADDRINUSE",
    });
    assert.equal(existsSync((await records(fixture.receipt))[0].root), false);
    await new Promise((fulfil, reject) => {
      http
        .get(host.origin, (response) => {
          assert.equal(response.statusCode, 200);
          response.resume();
          response.once("end", fulfil);
        })
        .once("error", reject);
    });
  },
);

test(
  "a cleanup failure does not replace the setup failure",
  { timeout: 10000 },
  async (t) => {
    const directory = await mkdtemp(join(tmpdir(), "weft-docs-error-test-"));
    t.after(() => rm(directory, { recursive: true, force: true }));
    const script = join(directory, "check.mjs");
    await writeFile(
      script,
      `
    import assert from 'node:assert/strict';
    import {startDocsSession} from ${JSON.stringify(new URL("./session.mjs", import.meta.url).href)};
    await assert.rejects(startDocsSession({launch:async(options)=>{
      const token=options.args.at(-1);
      options.log('[pid=1][out] WEFT_DOCS_'+token+' '+JSON.stringify({event:'failed',message:'original setup failure'}));
      return {launchedProcess:{stdin:{on(){}}},gracefullyClose:async()=>{throw new Error('original cleanup failure');}};
    }}),error=>{
      assert.ok(error instanceof AggregateError);
      assert.equal(error.errors[0].message,'original setup failure');
      assert.equal(error.errors[1].message,'original cleanup failure');
      assert.equal(error.cause,error.errors[0]);return true;
    });
  `,
    );
    let code, complete;
    const closed = new Promise((fulfil) => {
      complete = fulfil;
    });
    const owner = await launchProcess({
      command: process.execPath,
      args: [script],
      env: {
        ...process.env,
        TMP: directory,
        TEMP: directory,
        TMPDIR: directory,
      },
      stdio: "stdin",
      tempDirectories: [],
      log() {},
      onExit(value) {
        code = value;
        complete();
      },
      attemptToGracefullyClose: async () => {
        throw new Error("Stop only the owned failure-test child");
      },
    });
    t.after(() => owner.gracefullyClose());
    await closed;
    assert.equal(code, 0);
  },
);
