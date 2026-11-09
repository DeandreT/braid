import { randomBytes } from "node:crypto";
import { existsSync, mkdtempSync } from "node:fs";
import { rm } from "node:fs/promises";
import { createRequire } from "node:module";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

const require = createRequire(import.meta.url);
// Use the same registry the pinned runner drains before ordinary CLI exit,
// including global-timeout/Ctrl+C paths that stop awaiting global setup.
const { launchProcess } = require("playwright-core/lib/coreBundle").utils;
const sessions = new Set();
const deferred = () => {
  let resolve, reject;
  const promise = new Promise((yes, no) => {
    resolve = yes;
    reject = no;
  });
  void promise.catch(() => {});
  return { promise, resolve, reject };
};
async function within(operation, milliseconds, message) {
  let timer;
  try {
    return await Promise.race([
      operation,
      new Promise((_, reject) => {
        timer = setTimeout(() => reject(new Error(message)), milliseconds);
      }),
    ]);
  } finally {
    clearTimeout(timer);
  }
}

function stop(session) {
  session.stopping ??= (async () => {
    if (!session.scratch) {
      sessions.delete(session);
      return;
    }
    if (!session.launch) {
      await rm(session.scratch, { recursive: true, force: true });
      sessions.delete(session);
      return;
    }
    let owner;
    try {
      owner = await session.launch;
    } catch {
      // No successfully launched process to adopt. The launcher also cleans a
      // failed spawn; remove only this session's own, already-known directory.
      await rm(session.scratch, { recursive: true, force: true });
      sessions.delete(session);
      return;
    }
    await within(
      owner.gracefullyClose(),
      5000,
      "Docs owned process cleanup did not settle.",
    );
    if (existsSync(session.scratch))
      throw new Error(
        `Docs build root cleanup failed; retain ${session.scratch}.`,
      );
    if (session.admitted && session.failure) throw session.failure;
    if (session.admitted && (session.exit?.code !== 0 || session.exit.signal))
      throw new Error("Docs worker did not close cleanly.");
    sessions.delete(session);
  })();
  return session.stopping;
}

/** The launcher argument is a code-only adapter for deterministic failure tests. */
export async function startDocsSession({
  worker = fileURLToPath(new URL("./server.mjs", import.meta.url)),
  launch = launchProcess,
} = {}) {
  const session = {
    ready: deferred(),
    closed: deferred(),
    scratch: null,
    launch: null,
    stopping: null,
  };
  sessions.add(session);
  try {
    // No await between creating custody, creating its root and registering the
    // actual worker with Playwright's process/temporary-directory cleanup set.
    session.scratch = mkdtempSync(join(tmpdir(), "weft-docs-images-"));
    const token = randomBytes(16).toString("hex");
    const prefix = `WEFT_DOCS_${token} `;
    session.launch = launch({
      command: process.execPath,
      args: [worker, "--serve", session.scratch, token],
      cwd: fileURLToPath(new URL("../../", import.meta.url)),
      env: process.env,
      stdio: "stdin",
      tempDirectories: [session.scratch],
      log(line) {
        const index = line.indexOf("[out] " + prefix);
        if (index !== -1) {
          try {
            const event = JSON.parse(line.slice(index + 6 + prefix.length));
            if (event.event === "ready") session.ready.resolve();
            else if (event.event === "failed") {
              const error = new Error(String(event.message).slice(0, 2048));
              error.code = event.code;
              session.failure = error;
              session.ready.reject(error);
            } else throw new Error("Unknown docs worker event.");
          } catch (error) {
            session.ready.reject(error);
          }
        } else process.stdout.write(line + "\n");
      },
      onExit(code, signal) {
        session.exit = { code, signal };
        session.closed.resolve({ code, signal });
        session.ready.reject(
          new Error(`Docs worker exited before readiness (${code ?? signal}).`),
        );
      },
      async attemptToGracefullyClose() {
        const owner = await session.launch;
        owner.launchedProcess.stdin.end();
        // A busy build remains in the live worker tree until the registered
        // launcher terminates it. No PID/name/port discovery or custom tree kill.
        await within(
          session.closed.promise,
          1000,
          "Docs worker did not stop after EOF.",
        );
      },
    });
    const owner = await session.launch;
    owner.launchedProcess.stdin.on("error", () => {});
    await within(
      session.ready.promise,
      120000,
      "Docs server exceeded its startup bound.",
    );
    session.admitted = true;
    return {
      scratch: session.scratch,
      child: owner.launchedProcess,
      stop: () => stop(session),
    };
  } catch (primary) {
    try {
      await stop(session);
    } catch (cleanup) {
      throw new AggregateError(
        [primary, cleanup],
        `${primary.message}; docs cleanup failed: ${cleanup.message}`,
        { cause: primary },
      );
    }
    throw primary;
  }
}

export async function stopDocsSessions() {
  const settled = await Promise.allSettled([...sessions].map(stop));
  const errors = settled
    .filter((item) => item.status === "rejected")
    .map((item) => item.reason);
  if (errors.length)
    throw new AggregateError(errors, "Docs session cleanup failed.");
}

export default async function setupDocsServer() {
  await startDocsSession();
}
