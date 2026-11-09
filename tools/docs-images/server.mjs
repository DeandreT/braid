// Docs-only host: build the ordinary app, then serve it without an API proxy.
// Angular's dev server discards deployUrl, so it cannot supply these deep links
// without a base tag. The ordinary build already emits root-relative assets.
import { spawn } from "node:child_process";
import { lstat, open, realpath } from "node:fs/promises";
import http from "node:http";
import {
  dirname,
  extname,
  isAbsolute,
  join,
  relative,
  resolve,
  sep,
} from "node:path";
import { fileURLToPath } from "node:url";

const maximumAssetBytes = 8 * 1024 * 1024;
const mediaTypes = new Map([
  [".html", "text/html; charset=utf-8"],
  [".js", "text/javascript; charset=utf-8"],
  [".css", "text/css; charset=utf-8"],
  [".json", "application/json"],
  [".map", "application/json"],
  [".txt", "text/plain; charset=utf-8"],
  [".svg", "image/svg+xml"],
  [".png", "image/png"],
  [".jpg", "image/jpeg"],
  [".jpeg", "image/jpeg"],
  [".webp", "image/webp"],
  [".gif", "image/gif"],
  [".ico", "image/x-icon"],
  [".woff", "font/woff"],
  [".woff2", "font/woff2"],
]);
const uuid = "[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}";
const catalogDocument = new RegExp(
  `^/catalog/organizations/${uuid}/projects(?:/${uuid}(?:/mappings(?:/${uuid}(?:/(?:operations|comparisons|revisions/${uuid}))?)?)?)?$`,
);
const isDocument = (path) =>
  ["/catalog", "/catalog/search", "/chat", "/help", "/administration"].includes(
    path,
  ) ||
  /^\/help\/[a-z0-9-]+$/.test(path) ||
  catalogDocument.test(path);
const contained = (root, path) => {
  const rel = relative(root, path);
  return (
    rel === "" ||
    (!isAbsolute(rel) && rel !== ".." && !rel.startsWith(".." + sep))
  );
};

// The build directory is private and immutable while listening. Reject links at
// every component and also resolve the final path before opening it.
async function asset(root, path) {
  let current = root;
  for (const part of ["", ...path.split("/")]) {
    current = join(current, part);
    const info = await lstat(current);
    if (info.isSymbolicLink()) throw new Error("Linked asset refused.");
    if (!contained(root, await realpath(current)))
      throw new Error("Asset escaped build root.");
  }
  const handle = await open(await realpath(current), "r");
  try {
    const info = await handle.stat();
    if (!info.isFile() || info.size > maximumAssetBytes)
      throw new Error("Asset refused.");
    // Bound the read even if an incorrectly shared build directory changes.
    const bytes = Buffer.alloc(info.size + 1);
    let length = 0;
    while (length < bytes.length) {
      const read = await handle.read(
        bytes,
        length,
        bytes.length - length,
        length,
      );
      if (!read.bytesRead) break;
      length += read.bytesRead;
    }
    if (length !== info.size) throw new Error("Asset changed while reading.");
    return bytes.subarray(0, length);
  } finally {
    await handle.close();
  }
}

/** Owns only its listener and sockets; never adopts or stops an existing server. */
export async function startDocsServer(directory, port = 5188) {
  if (!Number.isInteger(port) || port < 0 || port > 65535)
    throw new Error("Invalid docs port.");
  const requestedRoot = resolve(directory);
  if ((await lstat(requestedRoot)).isSymbolicLink())
    throw new Error("Linked build root refused.");
  const root = await realpath(requestedRoot);
  await asset(root, "index.html");
  const sockets = new Set();
  let origin;
  const server = http.createServer(
    { maxHeaderSize: 8192 },
    async (request, response) => {
      const reply = (
        status,
        body = Buffer.alloc(0),
        type = "text/plain; charset=utf-8",
        extra = {},
      ) => {
        response.writeHead(status, {
          "Cache-Control": "no-store",
          "Content-Type": type,
          "Content-Length": body.length,
          Connection: "close",
          ...extra,
        });
        response.end(request.method === "HEAD" ? undefined : body);
      };
      try {
        if (!["GET", "HEAD"].includes(request.method))
          return reply(405, undefined, undefined, { Allow: "GET, HEAD" });
        const target = request.url ?? "";
        const path = target.split("?", 1)[0];
        if (
          Buffer.byteLength(target) > 2048 ||
          /[\x00-\x20\x7f#\\]/.test(target) ||
          !/^\/[A-Za-z0-9._/-]*$/.test(path) ||
          path.startsWith("//") ||
          path
            .split("/")
            .slice(1)
            .some(
              (part) =>
                part.startsWith(".") ||
                part.endsWith(".") ||
                /^(?:con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)/i.test(part),
            ) ||
          (path !== "/" && path.split("/").slice(1).includes("")) ||
          request.headers.host !== new URL(origin).host ||
          request.headers["transfer-encoding"] !== undefined ||
          ![undefined, "0"].includes(request.headers["content-length"])
        )
          return reply(400);
        if (path === "/api" || path.startsWith("/api/")) return reply(404);
        let file = path === "/" ? "index.html" : path.slice(1);
        let type = mediaTypes.get(extname(file));
        if (!type) {
          if (
            !isDocument(path) ||
            !/(?:^|,)\s*text\/html(?:\s*;[^,]*)?(?:,|$)/i.test(
              request.headers.accept ?? "",
            )
          )
            return reply(404);
          file = "index.html";
          type = mediaTypes.get(".html");
        }
        if (extname(file) === ".html" && file !== "index.html")
          return reply(404);
        let bytes;
        try {
          bytes = await asset(root, file);
        } catch {
          return reply(404);
        }
        reply(200, bytes, type);
      } catch {
        if (!response.headersSent) reply(500);
        else response.destroy();
      }
    },
  );
  server.requestTimeout = 5000;
  server.headersTimeout = 5000;
  server.setTimeout(5000, (socket) => socket.destroy());
  server.maxConnections = 16;
  server.maxRequestsPerSocket = 32;
  server.on("connection", (socket) => {
    sockets.add(socket);
    socket.once("close", () => sockets.delete(socket));
  });
  server.on("clientError", (_error, socket) => {
    if (socket.writable)
      socket.end(
        "HTTP/1.1 400 Bad Request\r\nConnection: close\r\nContent-Length: 0\r\n\r\n",
      );
    else socket.destroy();
  });
  const startup = new AbortController();
  const timer = setTimeout(() => startup.abort(), 5000);
  try {
    await new Promise((fulfil, reject) => {
      server.once("error", reject);
      startup.signal.addEventListener(
        "abort",
        () => reject(new Error("Docs listener did not start.")),
        { once: true },
      );
      server.listen(
        { port, host: "127.0.0.1", signal: startup.signal },
        fulfil,
      );
    });
  } finally {
    clearTimeout(timer);
  }
  origin = `http://127.0.0.1:${server.address().port}`;
  let stopped;
  return {
    origin,
    stop() {
      stopped ??= new Promise((fulfil, reject) => {
        const timer = setTimeout(
          () => reject(new Error("Docs listener did not settle.")),
          5000,
        );
        server.close((error) => {
          clearTimeout(timer);
          if (error) reject(error);
          else fulfil();
        });
        // Only sockets accepted by this listener, including incomplete requests.
        for (const socket of sockets) socket.destroy();
      });
      return stopped;
    },
  };
}

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

// This worker and its build descendants belong to Playwright's registered
// process launcher. EOF requests a normal listener close; a build that cannot
// settle is terminated by that launcher's owned-process-tree cleanup.
async function serve(scratch, token) {
  const repository = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
  let host;
  const stopping = new Promise((fulfil) => process.stdin.once("end", fulfil));
  process.stdin.resume();
  const report = (event) =>
    console.log(`WEFT_DOCS_${token} ${JSON.stringify(event)}`);
  try {
    const build = spawn(
      process.execPath,
      [
        join(repository, "node_modules/@angular/cli/bin/ng.js"),
        "build",
        "--configuration",
        "development",
        "--output-path",
        join(scratch, "app"),
      ],
      {
        cwd: join(repository, "apps/web"),
        env: { ...process.env, NG_CLI_ANALYTICS: "false" },
        stdio: "inherit",
      },
    );
    const buildClosed = new Promise((fulfil, reject) => {
      build.once("error", reject);
      build.once("close", (code, signal) => {
        fulfil({ code, signal });
      });
    });
    // Startup stays inside the former 120-second webServer limit. Never reuse
    // another listener: the exclusive 127.0.0.1:5188 bind must succeed itself.
    const exit = await within(
      buildClosed,
      90000,
      "Docs development build exceeded its startup bound.",
    );
    if (exit.code !== 0 || exit.signal)
      throw new Error("Docs development build did not complete.");
    host = await startDocsServer(join(scratch, "app/browser"));
    report({ event: "ready" });
    await stopping;
    await host.stop();
  } catch (error) {
    report({ event: "failed", message: error.message, code: error.code });
    process.exitCode = 1;
    await stopping;
    try {
      await host?.stop();
    } catch (cleanup) {
      console.error(`Docs listener cleanup failed: ${cleanup.message}`);
    }
  } finally {
    process.stdin.destroy();
  }
}

if (
  process.argv[1] &&
  resolve(process.argv[1]) === fileURLToPath(import.meta.url)
) {
  if (
    process.argv[2] !== "--serve" ||
    !isAbsolute(process.argv[3] ?? "") ||
    !/^[0-9a-f]{32}$/.test(process.argv[4] ?? "")
  ) {
    console.error(
      "Docs worker requires its owned scratch directory and launch token.",
    );
    process.exitCode = 2;
  } else {
    await serve(process.argv[3], process.argv[4]);
  }
}
