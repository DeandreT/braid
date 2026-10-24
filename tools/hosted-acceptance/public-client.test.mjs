import assert from "node:assert/strict";
import { createHash, randomBytes, randomUUID } from "node:crypto";
import { createServer } from "node:http";
import { after, before, test } from "node:test";
import { createPublicClients } from "./public-client.mjs";

const tenant = randomUUID();
const client = {
  clientId: randomUUID(),
  audience: randomUUID(),
  scope: "weft.api",
};
const user = {
  objectId: randomUUID(),
  subject: "pairwise-subject",
  username: "user@example.test",
};
const scope = `api://${client.audience}/${client.scope} openid profile offline_access`;
const events = [];
const issued = [];
// Unsigned test tokens: these tests read claims; the issuer's own signer is tested by the acceptance run.
const signed = (claims) => {
  const token = `${Buffer.from("{}").toString("base64url")}.${Buffer.from(JSON.stringify(claims)).toString("base64url")}.x`;
  issued.push(token);
  return token;
};
const flows = createPublicClients({
  host: "login.microsoftonline.com",
  tenant,
  issuer: `https://login.microsoftonline.com/${tenant}/v2.0`,
  clients: [client],
  user,
  signed,
  event: (name) => events.push(name),
});
const base = `/${tenant}/oauth2/v2.0`;
let server;
let origin;

before(async () => {
  server = createServer(async (request, response) => {
    const url = new URL(request.url, "http://localhost");
    if (await flows.route(request, url, response)) return;
    if (request.method === "POST" && url.pathname === `${base}/token`) {
      const chunks = [];
      for await (const chunk of request) chunks.push(chunk);
      const form = new URLSearchParams(Buffer.concat(chunks).toString("utf8"));
      if (flows.redeem(form, response)) return;
    }
    response.writeHead(404);
    response.end();
  });
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  origin = `http://127.0.0.1:${server.address().port}`;
});
after(() => server.close());

const post = (path, fields) =>
  fetch(origin + path, {
    method: "POST",
    body: new URLSearchParams(fields),
    redirect: "manual",
  });
const claims = (token) =>
  JSON.parse(Buffer.from(token.split(".")[1], "base64url").toString("utf8"));

function assertTokens(body) {
  assert.equal(body.token_type, "Bearer");
  const access = claims(body.access_token);
  assert.equal(access.aud, client.audience);
  assert.equal(access.azp, client.clientId);
  assert.equal(access.scp, "weft.api");
  assert.equal(access.oid, user.objectId);
  assert.equal(access.tid, tenant);
  assert.equal(access.ver, "2.0");
  assert.equal(access.iss, `https://login.microsoftonline.com/${tenant}/v2.0`);
  assert.equal(access.idtyp, undefined);
  assert.ok(access.exp > access.nbf);
  assert.equal(claims(body.id_token).aud, client.clientId);
  assert.deepEqual(
    JSON.parse(Buffer.from(body.client_info, "base64url").toString("utf8")),
    {
      uid: user.objectId,
      utid: tenant,
    },
  );
}

test("MSAL instance discovery is confined to the registered tenant and Microsoft authority", async () => {
  const query = new URLSearchParams({
    "api-version": "1.1",
    authorization_endpoint: `https://login.microsoftonline.com${base}/authorize`,
  });
  const response = await fetch(`${origin}/common/discovery/instance?${query}`);
  assert.equal(response.status, 200);
  assert.match(response.headers.get("cache-control"), /no-store/);
  const metadata = await response.json();
  assert.equal(
    metadata.tenant_discovery_endpoint,
    `https://login.microsoftonline.com/${tenant}/v2.0/.well-known/openid-configuration`,
  );
  assert.equal(
    metadata.metadata[0].preferred_network,
    "login.microsoftonline.com",
  );
  query.set(
    "authorization_endpoint",
    "https://foreign.invalid/oauth2/authorize",
  );
  assert.equal(
    (await fetch(`${origin}/common/discovery/instance?${query}`)).status,
    400,
  );
  query.set(
    "authorization_endpoint",
    `https://login.microsoftonline.com${base}/authorize`,
  );
  query.append(
    "authorization_endpoint",
    `https://login.microsoftonline.com${base}/authorize`,
  );
  assert.equal(
    (await fetch(`${origin}/common/discovery/instance?${query}`)).status,
    400,
  );
});

test("device authorization issues delegated tokens only after approval, once", async () => {
  const started = await (
    await post(`${base}/devicecode`, { client_id: client.clientId, scope })
  ).json();
  assert.match(started.user_code, /^[A-Z]{8}$/);
  assert.equal(started.interval, 1);
  const redeem = () =>
    post(`${base}/token`, {
      grant_type: "device_code",
      client_id: client.clientId,
      device_code: started.device_code,
    });
  const pending = await redeem();
  assert.equal(pending.status, 400);
  assert.equal((await pending.json()).error, "authorization_pending");
  assert.equal(
    (await post(`${base}/deviceapprove`, { user_code: "WRONGCOD" })).status,
    400,
  );
  assert.equal(
    (await post(`${base}/deviceapprove`, { user_code: started.user_code }))
      .status,
    204,
  );
  const granted = await redeem();
  assert.equal(granted.status, 200);
  assertTokens(await granted.json());
  assert.equal((await (await redeem()).json()).error, "expired_token");
});

test("authorization code with PKCE posts the code to the loopback listener", async () => {
  const verifier = randomBytes(32).toString("base64url");
  const challenge = createHash("sha256")
    .update(verifier, "ascii")
    .digest("base64url");
  const query = (mode) =>
    new URLSearchParams({
      client_id: client.clientId,
      response_type: "code",
      redirect_uri: "http://localhost:43123",
      scope,
      code_challenge: challenge,
      code_challenge_method: "S256",
      state: "state-1",
      response_mode: mode,
    });
  const page = await fetch(`${origin}${base}/authorize?${query("form_post")}`);
  assert.equal(page.status, 200);
  const html = await page.text();
  assert.match(html, /action="http:\/\/localhost:43123"/);
  assert.match(html, /name="state" value="state-1"/);
  const code = /name="code" value="([^"]+)"/.exec(html)[1];
  const redeem = (codeVerifier) =>
    post(`${base}/token`, {
      grant_type: "authorization_code",
      client_id: client.clientId,
      code,
      redirect_uri: "http://localhost:43123",
      code_verifier: codeVerifier,
    });
  assert.equal(
    (await (await redeem("wrong-verifier")).json()).error,
    "invalid_grant",
  );
  // A refused redemption consumes the code.
  assert.equal((await (await redeem(verifier)).json()).error, "invalid_grant");

  const redirect = await fetch(`${origin}${base}/authorize?${query("query")}`, {
    redirect: "manual",
  });
  assert.equal(redirect.status, 302);
  const location = new URL(redirect.headers.get("location"));
  assert.equal(location.origin, "http://localhost:43123");
  assert.equal(location.searchParams.get("state"), "state-1");
  const granted = await post(`${base}/token`, {
    grant_type: "authorization_code",
    client_id: client.clientId,
    code: location.searchParams.get("code"),
    redirect_uri: "http://localhost:43123",
    code_verifier: verifier,
  });
  assert.equal(granted.status, 200);
  const body = await granted.json();
  assertTokens(body);

  const refreshed = await post(`${base}/token`, {
    grant_type: "refresh_token",
    client_id: client.clientId,
    refresh_token: body.refresh_token,
    scope,
  });
  assert.equal(refreshed.status, 200);
  assertTokens(await refreshed.json());
  const reused = await post(`${base}/token`, {
    grant_type: "refresh_token",
    client_id: client.clientId,
    refresh_token: body.refresh_token,
  });
  assert.equal((await reused.json()).error, "invalid_grant");
});

test("unregistered clients, missing scopes and confidential credentials are not public-client requests", async () => {
  assert.equal(
    (await post(`${base}/devicecode`, { client_id: randomUUID(), scope }))
      .status,
    400,
  );
  assert.equal(
    (
      await post(`${base}/devicecode`, {
        client_id: client.clientId,
        scope: "openid",
      })
    ).status,
    400,
  );
  const other = await post(`${base}/token`, {
    grant_type: "authorization_code",
    client_id: randomUUID(),
  });
  assert.equal(other.status, 404);
  const secret = await post(`${base}/token`, {
    grant_type: "refresh_token",
    client_id: client.clientId,
    client_secret: "x",
  });
  assert.equal((await secret.json()).error, "invalid_client");
  const query = new URLSearchParams({
    client_id: client.clientId,
    response_type: "code",
    redirect_uri: "https://evil.example/cb",
    scope,
  });
  assert.equal(
    (await fetch(`${origin}${base}/authorize?${query}`)).status,
    400,
  );
  assert.throws(() =>
    createPublicClients({
      tenant,
      clients: [client],
      user: {},
      signed,
      event: () => {},
    }),
  );
});

test("events name protocol steps and never carry codes or tokens", () => {
  for (const name of events) {
    assert.match(name, /^(refused )?public-[a-z-]+$/);
    for (const token of issued) assert.ok(!name.includes(token));
  }
  assert.ok(events.includes("public-token"));
});
