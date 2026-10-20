import {
  constants,
  openSync,
  closeSync,
  fstatSync,
  readSync,
  lstatSync,
  realpathSync,
  readFileSync,
} from "node:fs";
import { isAbsolute, dirname } from "node:path";
import { createHash } from "node:crypto";
import net from "node:net";
import { networkCases } from "./network-names";

export const requireOwned: (value: unknown) => asserts value = (value) => {
  if (!value) throw new Error("Owned HTTPS qualification failed.");
};
export const safeFailure = () => new Error("Owned HTTPS qualification failed.");
export const maximum = 65536;
// Independent bounded JSON grammar: rejects duplicate properties at every depth.
export function strictJson(text: string): unknown {
  requireOwned(Buffer.byteLength(text) <= maximum);
  let index = 0;
  const space = () => {
    while (/[\x20\t\r\n]/.test(text[index] ?? "!") && index < text.length)
      ++index;
  };
  const string = (): string => {
    const start = index++;
    let escaped = false;
    while (index < text.length) {
      const character = text[index++];
      if (!escaped && character === '"')
        return JSON.parse(text.slice(start, index)) as string;
      if (!escaped && character === "\\") escaped = true;
      else escaped = false;
    }
    throw safeFailure();
  };
  const value = (depth: number): unknown => {
    requireOwned(depth <= 16);
    space();
    if (text[index] === '"') return string();
    if (text[index] === "{") {
      ++index;
      const output: Record<string, unknown> = Object.create(null) as Record<
        string,
        unknown
      >;
      space();
      if (text[index] === "}") {
        ++index;
        return output;
      }
      while (true) {
        space();
        requireOwned(text[index] === '"');
        const key = string();
        requireOwned(!Object.hasOwn(output, key));
        space();
        requireOwned(text[index++] === ":");
        output[key] = value(depth + 1);
        space();
        const next = text[index++];
        if (next === "}") return output;
        requireOwned(next === ",");
      }
    }
    if (text[index] === "[") {
      ++index;
      const output: unknown[] = [];
      space();
      if (text[index] === "]") {
        ++index;
        return output;
      }
      while (true) {
        requireOwned(output.length < 1024);
        output.push(value(depth + 1));
        space();
        const next = text[index++];
        if (next === "]") return output;
        requireOwned(next === ",");
      }
    }
    const match =
      /^(?:true|false|null|-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?)/.exec(
        text.slice(index),
      );
    requireOwned(match);
    index += match[0].length;
    return JSON.parse(match[0]) as unknown;
  };
  const output = value(0);
  space();
  requireOwned(index === text.length);
  return output;
}
export function closed(
  value: unknown,
  names: readonly string[],
): asserts value is Record<string, unknown> {
  requireOwned(
    value !== null && typeof value === "object" && !Array.isArray(value),
  );
  requireOwned(
    Object.keys(value).length === names.length &&
      names.every((name) => Object.hasOwn(value, name)),
  );
}
export type Input = {
  version: number;
  root: string;
  socketPath: string;
  origin: string;
  scenario: string;
  organization: string;
  assets: string[];
  home: string;
  node: string;
  nodeSha256: string;
  proxy: string;
  chromium: string;
  chromiumSha256: string;
};
export const digest = (bytes: Buffer) =>
  createHash("sha256").update(bytes).digest("hex");
export function input(): Input {
  const path = process.env.WEFT_AUTH_BROWSER_INPUT,
    root = process.env.WEFT_AUTH_BROWSER_ROOT;
  requireOwned(
    path &&
      root &&
      isAbsolute(path) &&
      dirname(path) === root &&
      realpathSync(root) === root,
  );
  const directory = lstatSync(root);
  requireOwned(
    directory.isDirectory() &&
      (directory.mode & 0o777) === 0o700 &&
      directory.uid === process.geteuid?.(),
  );
  const fd = openSync(
    path,
    constants.O_RDONLY | constants.O_NOFOLLOW | constants.O_NONBLOCK,
  );
  let bytes: Buffer;
  try {
    const stat = fstatSync(fd);
    requireOwned(
      stat.isFile() &&
        stat.nlink === 1 &&
        stat.uid === process.geteuid?.() &&
        (stat.mode & 0o777) === 0o600 &&
        stat.size > 0 &&
        stat.size <= maximum,
    );
    bytes = Buffer.alloc(stat.size + 1);
    let size = 0;
    while (size < bytes.length) {
      const count = readSync(fd, bytes, size, bytes.length - size, size);
      if (count === 0) break;
      size += count;
    }
    requireOwned(size === stat.size && fstatSync(fd).size === stat.size);
    bytes = bytes.subarray(0, size);
  } finally {
    closeSync(fd);
  }
  let parsed: unknown;
  try {
    parsed = strictJson(
      new TextDecoder("utf-8", { fatal: true }).decode(bytes),
    );
  } finally {
    bytes.fill(0);
  }
  closed(parsed, [
    "version",
    "root",
    "socketPath",
    "origin",
    "scenario",
    "organization",
    "assets",
    "home",
    "node",
    "nodeSha256",
    "chromium",
    "chromiumSha256",
    "proxy",
  ]);
  requireOwned(parsed["version"] === 1 && parsed["root"] === root);
  for (const key of [
    "socketPath",
    "origin",
    "scenario",
    "organization",
    "home",
    "node",
    "nodeSha256",
    "chromium",
    "chromiumSha256",
    "proxy",
  ])
    requireOwned(typeof parsed[key] === "string");
  const result = parsed as unknown as Input;
  requireOwned(
    ["positive", "tokens", "state", "correlation", "nonce", "replay"].includes(
      result.scenario,
    ) || networkCases.some((name) => result.scenario === "network-" + name),
  );
  requireOwned(
    result.socketPath === root + "/o.sock" && result.home === root + "/home",
  );
  requireOwned(
    Array.isArray(result.assets) &&
      result.assets.length > 0 &&
      result.assets.length <= 256 &&
      result.assets.every(
        (item) =>
          typeof item === "string" &&
          !item.startsWith("/") &&
          !item.split("/").includes(".."),
      ),
  );
  requireOwned(
    result.assets.includes("index.html") &&
      /^[a-f0-9-]{36}$/.test(result.organization),
  );
  const origin = new URL(result.origin);
  requireOwned(
    origin.protocol === "https:" &&
      origin.hostname === "127.0.0.1" &&
      origin.origin === result.origin &&
      origin.port !== "",
  );
  requireOwned(
    process.env.HOME === result.home &&
      realpathSync(process.execPath) === result.node,
  );
  const proxy = new URL(result.proxy);
  requireOwned(
    proxy.protocol === "http:" &&
      proxy.hostname === "127.0.0.1" &&
      proxy.port !== "" &&
      proxy.origin === result.proxy &&
      proxy.username === "" &&
      proxy.password === "" &&
      proxy.pathname === "/" &&
      !proxy.search &&
      !proxy.hash,
  );
  requireOwned(
    digest(readFileSync(result.node)) === result.nodeSha256 &&
      digest(readFileSync(result.chromium)) === result.chromiumSha256,
  );
  return result;
}
export type Reply = {
  version: number;
  outcome: string;
  status: number;
  callbackUrl: string | null;
  authorizationRequests: number;
  tokenRequests: number;
  assertionsAccepted: number;
  pkceAccepted: number;
  unexpected: number;
  sessionRows: number;
  activeRows: number;
  revokedRows: number;
  epochChanged: boolean;
  ringUnchanged: boolean;
  processGeneration: number;
  productDeliveries: number;
  admissionRefusals: number;
  proofStripped: boolean;
  networkRequests: number;
  networkPreflights: number;
  networkAborts: number;
  networkActive: number;
  proofPending: number;
  proofUsed: number;
  networkArmed: boolean;
  admissionSealed: boolean;
  proxyRefused: number;
  proxyApproved: number;
  closedObservation: boolean;
  admissionRefusalsTotal: number;
  counterValidation: boolean;
};
export type Admission = {
  proof: string;
  generation: number;
  rawTarget: string;
  method: string;
  destination: string;
  mode: string;
  site: string;
  origin: string | null;
  negative: string | null;
};
export class Control {
  private originals: Promise<void>[] = [];
  private observers: Promise<PromiseSettledResult<void>[]>[] = [];
  constructor(private readonly cfg: Input) {}
  async call(
    operation:
      | "authorize"
      | "counts"
      | "restart"
      | "stop"
      | "register"
      | "withdraw"
      | "seal"
      | "replay"
      | "networkBegin"
      | "revoke"
      | "barrier",
    authorizationUrl: string | null = null,
    tokenCase: "good" | "badNonce" | null = null,
    admission: Admission | null = null,
    callbackCase: "good" | "badState" | null = null,
  ): Promise<Reply> {
    const socketStat = lstatSync(this.cfg.socketPath);
    requireOwned(
      socketStat.isSocket() &&
        socketStat.uid === process.geteuid?.() &&
        (socketStat.mode & 0o777) === 0o600,
    );
    const bytes = Buffer.from(
      JSON.stringify({
        version: 1,
        operation,
        authorizationUrl,
        tokenCase,
        admission,
        callbackCase,
      }),
    );
    requireOwned(bytes.length <= maximum);
    const frame = Buffer.alloc(4 + bytes.length);
    frame.writeUInt32BE(bytes.length);
    bytes.copy(frame, 4);
    bytes.fill(0);
    let response: Reply | undefined;
    const original = new Promise<void>((resolve, reject) => {
      const socket = net.createConnection({ path: this.cfg.socketPath });
      let received = Buffer.alloc(0),
        expected: number | null = null,
        complete = false,
        failed = false;
      const fail = () => {
        failed = true;
        socket.destroy();
      };
      const timer = setTimeout(fail, 45000);
      socket.once("connect", () => {
        socket.end(frame, () => frame.fill(0));
      });
      socket.on("data", (chunk: Buffer) => {
        try {
          requireOwned(received.length + chunk.length <= maximum + 4);
          received = Buffer.concat([received, chunk]);
          if (received.length >= 4 && expected === null) {
            expected = received.readUInt32BE(0);
            requireOwned(expected > 0 && expected <= maximum);
          }
          if (expected !== null && received.length >= expected + 4) {
            requireOwned(!complete && received.length === expected + 4);
            complete = true;
            const value = strictJson(
              new TextDecoder("utf-8", { fatal: true }).decode(
                received.subarray(4),
              ),
            );
            closed(value, [
              "version",
              "outcome",
              "status",
              "callbackUrl",
              "authorizationRequests",
              "tokenRequests",
              "assertionsAccepted",
              "pkceAccepted",
              "unexpected",
              "sessionRows",
              "activeRows",
              "revokedRows",
              "epochChanged",
              "ringUnchanged",
              "processGeneration",
              "productDeliveries",
              "admissionRefusals",
              "proofStripped",
              "networkRequests",
              "networkPreflights",
              "networkAborts",
              "networkActive",
              "proofPending",
              "proofUsed",
              "networkArmed",
              "admissionSealed",
              "proxyRefused",
              "proxyApproved",
              "closedObservation",
              "admissionRefusalsTotal",
              "counterValidation",
            ]);
            requireOwned(value["version"] === 1 && value["outcome"] === "ok");
            for (const key of [
              "status",
              "authorizationRequests",
              "tokenRequests",
              "assertionsAccepted",
              "pkceAccepted",
              "unexpected",
              "sessionRows",
              "activeRows",
              "revokedRows",
              "processGeneration",
              "productDeliveries",
              "admissionRefusals",
              "networkRequests",
              "networkPreflights",
              "networkAborts",
              "networkActive",
              "proofPending",
              "proofUsed",
              "proxyRefused",
              "proxyApproved",
              "admissionRefusalsTotal",
            ])
              requireOwned(
                Number.isSafeInteger(value[key]) && (value[key] as number) >= 0,
              );
            requireOwned(
              (value["callbackUrl"] === null ||
                typeof value["callbackUrl"] === "string") &&
                typeof value["epochChanged"] === "boolean" &&
                typeof value["ringUnchanged"] === "boolean",
            );
            requireOwned(
              typeof value["proofStripped"] === "boolean" &&
                typeof value["networkArmed"] === "boolean" &&
                typeof value["admissionSealed"] === "boolean" &&
                typeof value["closedObservation"] === "boolean" &&
                typeof value["counterValidation"] === "boolean",
            );
            response = value as unknown as Reply;
          }
        } catch {
          fail();
        }
      });
      socket.once("error", fail);
      socket.once("close", () => {
        clearTimeout(timer);
        frame.fill(0);
        received.fill(0);
        if (!failed && complete) resolve();
        else reject(safeFailure());
      });
    });
    this.originals.push(original);
    this.observers.push(Promise.allSettled([original]));
    await original;
    requireOwned(response);
    return response;
  }
  async drain(): Promise<void> {
    const results = await Promise.all(this.observers);
    requireOwned(results.every((batch) => batch[0]?.status === "fulfilled"));
  }
}
