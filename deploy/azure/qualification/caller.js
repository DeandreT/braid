// Qualification-only machine caller for deploy/azure/qualify.sh, run as `node -e` by the caller job inside the
// Container Apps environment, where the internal mapping endpoint is reachable. One execution sends one request:
// `invoke CALL ORIGIN REQUEST CREDENTIAL` posts the preview-orders regression inputs to /v1/invocations with the
// named token from the job's secrets (caller, foreign or expiring), and `ready CALL ORIGIN` reads readiness. It
// prints one line, `braid-qualify-call` and a JSON summary with the response body, and never prints a token.
const { createHash } = require("node:crypto");

const [mode, call, origin, requestId, credential] = process.argv.slice(1);
const tokens = {
  caller: process.env.BRAID_QUALIFY_CALLER_TOKEN,
  foreign: process.env.BRAID_QUALIFY_FOREIGN_TOKEN,
  expiring: process.env.BRAID_QUALIFY_EXPIRING_TOKEN,
};
const b64 = (text) => Buffer.from(text, "utf8").toString("base64");
const request = JSON.stringify({
  inputs: [
    { content: b64('{"id":"SO-1","lines":[{"sku":"A","qty":2},{"sku":"B","qty":9007199254740993}]}') },
    { name: "rates", content: b64('{"currency":"EUR"}') },
  ],
  parameters: [
    { name: "region", kind: "string", value: "west" },
    { name: "express", kind: "boolean", value: "true" },
  ],
});
const report = (summary) => console.log(`braid-qualify-call ${JSON.stringify({ call, requestId, ...summary })}`);

async function invoke() {
  const token = tokens[credential];
  if (!token) throw new Error("unknown credential");
  let response;
  let attempts = 0;
  // Busy, unavailable or past its deadline: nothing final was recorded, so the same request is retried.
  for (;;) {
    attempts += 1;
    response = await fetch(`${origin}/v1/invocations`, {
      method: "POST",
      headers: {
        authorization: `Bearer ${token}`,
        "braid-request-id": requestId,
        "content-type": "application/json",
      },
      body: request,
      signal: AbortSignal.timeout(120_000),
    });
    if (response.status !== 503 || attempts === 8) break;
    await response.arrayBuffer();
    await new Promise((resolve) => setTimeout(resolve, 3_000));
  }
  const text = await response.text();
  let body = null;
  try {
    body = JSON.parse(text);
  } catch {
    body = null;
  }
  report({
    status: response.status,
    attempts,
    replayed: response.headers.get("braid-invocation-replayed"),
    bearerChallenge: /^bearer/i.test(response.headers.get("www-authenticate") ?? ""),
    bodySha256: createHash("sha256").update(text, "utf8").digest("hex"),
    body,
  });
}

async function ready() {
  const response = await fetch(`${origin}/v1/health/ready`, { signal: AbortSignal.timeout(30_000) });
  report({ status: response.status });
}

(mode === "ready" ? ready() : mode === "invoke" ? invoke() : Promise.reject(new Error("unknown mode"))).catch((error) => {
  report({ status: 0, error: error.name });
  process.exitCode = 1;
});
