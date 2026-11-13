// Independent static-only owner. Never imports an earlier increment operator/runtime.
import http from "node:http";
import { createHash } from "node:crypto";
import { constants } from "node:fs";
import { readFile, lstat, open, realpath } from "node:fs/promises";
import { resolve, isAbsolute, relative, join } from "node:path";
import { browserSecurityHeaders } from "../browser-fixture/security-headers.mjs";

const demand = (value) => {
  if (!value)
    throw new Error(
      "The owned static browser fixture refused its configuration.",
    );
};
const digest = (bytes) => createHash("sha256").update(bytes).digest("hex");
const bounded = async (promise, milliseconds) => {
  let timer;
  try {
    return await Promise.race([
      promise,
      new Promise((_, reject) => {
        timer = setTimeout(
          () => reject(new Error("Owned static fixture settlement expired.")),
          milliseconds,
        );
      }),
    ]);
  } finally {
    clearTimeout(timer);
  }
};

export class BrowserSessionStaticOwner {
  #server;
  #sockets = new Set();
  #socketCloses = new Map();
  #close;
  #originalServerClose;
  #originalServerCloseSettled = false;
  #originalDrain;
  #files = new Map();
  #requests = new Set();
  #sequence = 0;
  unexpectedApi = 0;
  origin = "";
  settled = false;
  static async open(configurationPath) {
    const owner = new BrowserSessionStaticOwner();
    try {
      await owner.#start(configurationPath);
      return owner;
    } catch {
      const primary = new Error("Static fixture construction failed.");
      try {
        await owner.close();
      } catch {
        throw new AggregateError(
          [primary, new Error("Static fixture original cleanup failed.")],
          "Static fixture construction and cleanup failed.",
        );
      }
      throw primary;
    }
  }
  async #start(configurationPath) {
    demand(
      typeof configurationPath === "string" && isAbsolute(configurationPath),
    );
    const configurationStat = await lstat(configurationPath);
    demand(
      configurationStat.isFile() &&
        !configurationStat.isSymbolicLink() &&
        (configurationStat.mode & 0o777) === 0o600 &&
        configurationStat.size <= 65536 &&
        (typeof process.geteuid !== "function" ||
          configurationStat.uid === process.geteuid()),
    );
    const configuration = await open(
      configurationPath,
      constants.O_RDONLY | constants.O_NOFOLLOW | constants.O_NONBLOCK,
    );
    let raw;
    try {
      const opened = await configuration.stat();
      demand(
        opened.isFile() &&
          (opened.mode & 0o777) === 0o600 &&
          opened.dev === configurationStat.dev &&
          opened.ino === configurationStat.ino &&
          opened.uid === configurationStat.uid &&
          opened.size <= 65536,
      );
      // A raced growth can consume at most the limit plus one refusal byte.
      const bytes = Buffer.alloc(65537);
      let length = 0;
      while (length < bytes.length) {
        const { bytesRead } = await configuration.read(
          bytes,
          length,
          bytes.length - length,
          length,
        );
        if (bytesRead === 0) break;
        length += bytesRead;
      }
      const finished = await configuration.stat();
      demand(
        length <= 65536 &&
          length === opened.size &&
          finished.size === opened.size &&
          finished.mtimeMs === opened.mtimeMs &&
          finished.ctimeMs === opened.ctimeMs,
      );
      raw = bytes.subarray(0, length);
    } finally {
      await configuration.close();
    }
    const cfg = JSON.parse(raw.toString("utf8"));
    demand(
      cfg.schemaVersion === "weft.browser-session-controlled-owner.v1" &&
        cfg.runtimeReleased === true &&
        cfg.sourceAndPublicationsAccepted === true,
    );
    let publicationBytes = 0;
    for (const [prefix, publication] of [
      ["", cfg.application],
      ["/__weft119_probe", cfg.probe],
    ]) {
      demand(
        publication &&
          isAbsolute(publication.root) &&
          Array.isArray(publication.files) &&
          publication.files.length > 0 &&
          publication.files.length <= 256,
      );
      const root = await realpath(publication.root);
      demand(root === resolve(publication.root));
      const rootStat = await lstat(root);
      demand(rootStat.isDirectory() && !rootStat.isSymbolicLink());
      let hasIndex = false;
      for (const item of publication.files) {
        demand(
          typeof item.path === "string" &&
            !isAbsolute(item.path) &&
            !item.path.split(/[\\/]/).includes("..") &&
            /^[a-f0-9]{64}$/.test(item.sha256),
        );
        const file = join(root, item.path),
          inside = relative(root, file);
        demand(
          inside !== ".." &&
            !inside.startsWith("../") &&
            !isAbsolute(inside) &&
            (await realpath(file)) === file,
        );
        const stat = await lstat(file);
        demand(
          stat.isFile() && !stat.isSymbolicLink() && stat.size <= 16777216,
        );
        publicationBytes += stat.size;
        demand(publicationBytes <= 134217728);
        const bytes = await readFile(file);
        demand(bytes.length === item.bytes && digest(bytes) === item.sha256);
        const key = prefix + "/" + item.path.replaceAll("\\", "/");
        demand(!this.#files.has(key));
        this.#files.set(key, {
          bytes,
          type: item.path.endsWith(".html")
            ? "text/html; charset=utf-8"
            : item.path.endsWith(".js")
              ? "text/javascript; charset=utf-8"
              : item.path.endsWith(".css")
                ? "text/css; charset=utf-8"
                : "application/octet-stream",
        });
        if (item.path === "index.html") hasIndex = true;
      }
      demand(hasIndex);
    }
    this.#server = http.createServer((request, response) => {
      const pending = {};
      this.#requests.add(pending);
      const settle = () => this.#requests.delete(pending);
      response.once("close", settle);
      response.once("finish", settle);
      if (
        ++this.#sequence > 4096 ||
        request.method !== "GET" ||
        request.headers.host !== new URL(this.origin).host
      ) {
        response.writeHead(400).end();
        return;
      }
      let url;
      try {
        url = new URL(request.url, this.origin);
      } catch {
        response.writeHead(400).end();
        return;
      }
      if (url.pathname.startsWith("/api/")) {
        ++this.unexpectedApi;
        response.writeHead(503).end();
        return;
      }
      if (url.search || url.hash || /%2f|%5c|%2e/i.test(url.pathname)) {
        response.writeHead(400).end();
        return;
      }
      const probe =
        url.pathname === "/__weft119_probe" ||
        url.pathname.startsWith("/__weft119_probe/");
      const file =
        this.#files.get(url.pathname) ??
        (!url.pathname.split("/").at(-1).includes(".")
          ? this.#files.get(
              probe ? "/__weft119_probe/index.html" : "/index.html",
            )
          : null);
      if (!file) {
        response.writeHead(404).end();
        return;
      }
      response.writeHead(200, {
        ...browserSecurityHeaders,
        "Content-Type": file.type,
        "Cache-Control": "no-store",
      });
      response.end(file.bytes);
    });
    this.#server.requestTimeout = 10000;
    this.#server.headersTimeout = 10000;
    this.#server.on("connection", (socket) => {
      this.#sockets.add(socket);
      const originalClose = new Promise((resolveClosed) => {
        socket.once("close", () => {
          this.#sockets.delete(socket);
          this.#socketCloses.delete(socket);
          resolveClosed();
        });
      });
      this.#socketCloses.set(socket, originalClose);
    });
    await bounded(
      new Promise((resolveReady, reject) => {
        this.#server.once("error", reject);
        this.#server.listen(0, "127.0.0.1", resolveReady);
      }),
      10000,
    );
    const address = this.#server.address();
    demand(address && typeof address === "object");
    this.origin = `http://127.0.0.1:${address.port}`;
  }
  close() {
    return (this.#close ??= this.#stop());
  }
  get closeFacts() {
    return {
      unexpectedApi: this.unexpectedApi,
      pendingRequests: this.#requests.size,
      sockets: this.#sockets.size,
      serverCloseStarted: this.#originalServerClose !== undefined,
      serverCloseSettled: this.#originalServerCloseSettled,
      settled: this.settled,
    };
  }
  async #stop() {
    if (!this.#server) {
      this.settled = true;
      return;
    }
    // Own the first close callback; destroying sockets never substitutes for observing it.
    const original = (this.#originalServerClose ??= new Promise(
      (resolveClosed, reject) =>
        this.#server.close((error) => {
          this.#originalServerCloseSettled = true;
          if (error) reject(error);
          else resolveClosed();
        }),
    ));
    const drain = (this.#originalDrain ??= (async () => {
      await original;
      // Server close can precede its sockets' close notifications. Join the
      // observers installed on the actual connections, not a later close call.
      await Promise.all([...this.#socketCloses.values()]);
      demand(this.#requests.size === 0 && this.#sockets.size === 0);
      this.settled = true;
      this.#files.clear();
    })());
    this.#server.closeAllConnections();
    await bounded(drain, 15000);
  }
  toString() {
    return "BrowserSessionStaticOwner";
  }
}
