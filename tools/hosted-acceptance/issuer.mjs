// Test-owned OpenID Connect issuer for the fixed Microsoft organizational endpoints. The
// acceptance stack resolves login.microsoftonline.com to this container and trusts its
// test certificate authority; the Session host's identity code and validation are unchanged.
// It implements only what the Braid Entra client uses: discovery, signing keys, an
// authorization page with one sign-in button, and code redemption with a PS256 certificate
// assertion, PKCE (S256), single-use codes and RS256 ID tokens. The ID token carries the user's
// pairwise subject and, for the requested profile scope, the tenant-stable object ID that Braid
// binds. For mapping endpoint callers
// it also issues app-only v2 access tokens to registered machine clients (client credentials).
// The Braid CLI's public-client flows are in public-client.mjs. It logs one fixed event name per
// protocol step and never logs codes, tokens, assertions, secrets or keys.
import {
  createHash,
  createPrivateKey,
  createPublicKey,
  randomBytes,
  sign,
  timingSafeEqual,
  verify,
  constants,
  X509Certificate,
} from "node:crypto";
import { createServer } from "node:https";
import { createPublicClients } from "./public-client.mjs";

const host = "login.microsoftonline.com";
const take = (name) => {
  const value = process.env[name];
  if (!value) throw new Error(`The issuer requires ${name}.`);
  delete process.env[name];
  return value;
};
const tenant = take("BRAID_ACCEPTANCE_TENANT_ID");
const client = take("BRAID_ACCEPTANCE_CLIENT_ID");
const subject = take("BRAID_ACCEPTANCE_SUBJECT");
const objectId = take("BRAID_ACCEPTANCE_OBJECT_ID");
const callback = take("BRAID_ACCEPTANCE_CALLBACK");
const tlsBundle = take("BRAID_ACCEPTANCE_ISSUER_PEM");
const clientCertificate = new X509Certificate(
  take("BRAID_ACCEPTANCE_CLIENT_CERTIFICATE"),
);
// The run's ID-token signing key, stable across issuer restarts so cached keys stay valid.
const signingKey = createPrivateKey(take("BRAID_ACCEPTANCE_SIGNING_KEY"));
const tokenLifetimeSeconds = 3600;
// Optional machine clients: [{clientId, objectId, secretSha256, lifetimeSeconds}], each a client
// application with its service principal's object ID and the SHA-256 of its client secret.
const machineClients = new Map(
  JSON.parse(process.env.BRAID_ACCEPTANCE_MACHINE_CLIENTS || "[]").map(
    (machine) => [machine.clientId, machine],
  ),
);
delete process.env.BRAID_ACCEPTANCE_MACHINE_CLIENTS;

const issuer = `https://${host}/${tenant}/v2.0`;
const paths = {
  discovery: `/${tenant}/v2.0/.well-known/openid-configuration`,
  keys: `/${tenant}/discovery/v2.0/keys`,
  authorize: `/${tenant}/oauth2/v2.0/authorize`,
  token: `/${tenant}/oauth2/v2.0/token`,
};
const tokenAddress = `https://${host}${paths.token}`;
const signingJwk = createPublicKey(signingKey).export({ format: "jwk" });
const keyId = base64url(
  createHash("sha256").update(`${signingJwk.e}.${signingJwk.n}`).digest(),
);
const clientDigest = base64url(
  createHash("sha256").update(clientCertificate.raw).digest(),
);
// Optional public clients for the CLI acceptance stage: [{clientId, scope}], each the CLI's
// application ID and the delegated scope of its access tokens for this run's Braid application.
const publicClients = createPublicClients({
  host,
  tenant,
  issuer,
  clients: JSON.parse(process.env.BRAID_ACCEPTANCE_PUBLIC_CLIENTS || "[]").map(
    (registration) => ({ ...registration, audience: client }),
  ),
  user: {
    objectId,
    subject,
    username: "acceptance.user@braid.example.test",
  },
  signed,
  event,
  lifetimeSeconds: tokenLifetimeSeconds,
});
delete process.env.BRAID_ACCEPTANCE_PUBLIC_CLIENTS;
const pending = new Map(); // transaction -> authorization request, until the button is pressed
const codes = new Map(); // code -> issued authorization, single use
const assertionIds = new Set();

function base64url(bytes) {
  return Buffer.from(bytes).toString("base64url");
}

function event(name) {
  process.stdout.write(`issuer ${name}\n`);
}

function json(response, status, value) {
  const body = JSON.stringify(value);
  response.writeHead(status, {
    "Content-Type": "application/json; charset=utf-8",
    "Cache-Control": "no-store",
    "Content-Length": Buffer.byteLength(body),
  });
  response.end(body);
}

function refuse(response, error, name) {
  event(`refused ${name}`);
  json(response, 400, { error });
}

async function readBody(request, limit) {
  const chunks = [];
  let length = 0;
  for await (const chunk of request) {
    length += chunk.length;
    if (length > limit) throw new Error("body too large");
    chunks.push(chunk);
  }
  return Buffer.concat(chunks).toString("utf8");
}

function singletons(parameters, names) {
  const result = {};
  for (const name of names) {
    const values = parameters.getAll(name);
    if (
      values.length !== 1 ||
      values[0].length === 0 ||
      values[0].length > 16384
    )
      return null;
    result[name] = values[0];
  }
  return result;
}

function authorize(url, response) {
  const query = singletons(url.searchParams, [
    "client_id",
    "redirect_uri",
    "response_type",
    "scope",
    "code_challenge",
    "code_challenge_method",
    "state",
    "nonce",
  ]);
  if (
    query === null ||
    query.client_id !== client ||
    query.redirect_uri !== callback ||
    query.response_type !== "code" ||
    query.scope !== "openid profile" ||
    query.code_challenge_method !== "S256" ||
    query.code_challenge.length !== 43 ||
    url.searchParams.has("code_verifier") ||
    url.searchParams.has("client_secret") ||
    (url.searchParams.has("response_mode") &&
      url.searchParams.get("response_mode") !== "query")
  )
    return refuse(response, "invalid_request", "authorize");
  if (pending.size >= 64)
    return refuse(response, "temporarily_unavailable", "authorize");
  const transaction = base64url(randomBytes(24));
  pending.set(transaction, {
    state: query.state,
    nonce: query.nonce,
    challenge: query.code_challenge,
    expires: Date.now() + 600_000,
  });
  event("authorize");
  const page = `<!doctype html>
<html lang="en">
<head><meta charset="utf-8"><title>Sign in to your account</title></head>
<body>
<main>
<h1>Sign in</h1>
<p>Acceptance identity provider for the fixed Microsoft organizational endpoints.</p>
<form method="post" action="${paths.authorize}">
<input type="hidden" name="transaction" value="${transaction}">
<button type="submit">Sign in as the acceptance user</button>
</form>
</main>
</body>
</html>`;
  response.writeHead(200, {
    "Content-Type": "text/html; charset=utf-8",
    "Cache-Control": "no-store",
    // form-action is left open: the approval redirects to the Braid callback.
    "Content-Security-Policy": "default-src 'none'",
  });
  response.end(page);
}

async function approve(request, response) {
  const form = new URLSearchParams(await readBody(request, 4096));
  const fields = singletons(form, ["transaction"]);
  const authorization = fields && pending.get(fields.transaction);
  if (!authorization || authorization.expires <= Date.now())
    return refuse(response, "invalid_request", "approve");
  pending.delete(fields.transaction);
  const code = base64url(randomBytes(32));
  codes.set(code, { ...authorization, expires: Date.now() + 300_000 });
  event("approved");
  const location = new URL(callback);
  location.searchParams.set("code", code);
  location.searchParams.set("state", authorization.state);
  response.writeHead(303, {
    Location: location.href,
    "Cache-Control": "no-store",
  });
  response.end();
}

function decodeJwt(token) {
  const parts = token.split(".");
  if (parts.length !== 3) return null;
  try {
    return {
      header: JSON.parse(Buffer.from(parts[0], "base64url").toString("utf8")),
      claims: JSON.parse(Buffer.from(parts[1], "base64url").toString("utf8")),
      signed: Buffer.from(`${parts[0]}.${parts[1]}`, "ascii"),
      signature: Buffer.from(parts[2], "base64url"),
    };
  } catch {
    return null;
  }
}

// The confidential client's PS256 assertion signed with the configured Entra client certificate.
function assertionValid(assertion) {
  const token = assertion.length <= 16384 ? decodeJwt(assertion) : null;
  if (!token) return false;
  const { header, claims } = token;
  const now = Math.floor(Date.now() / 1000);
  if (
    header.alg !== "PS256" ||
    header["x5t#S256"] !== clientDigest ||
    claims.iss !== client ||
    claims.sub !== client ||
    !(
      claims.aud === tokenAddress ||
      (Array.isArray(claims.aud) &&
        claims.aud.length === 1 &&
        claims.aud[0] === tokenAddress)
    ) ||
    typeof claims.jti !== "string" ||
    claims.jti.length === 0 ||
    typeof claims.exp !== "number" ||
    claims.exp <= now ||
    claims.exp > now + 600 ||
    (typeof claims.nbf === "number" && claims.nbf > now + 5)
  )
    return false;
  const verified = verify(
    "sha256",
    token.signed,
    {
      key: clientCertificate.publicKey,
      padding: constants.RSA_PKCS1_PSS_PADDING,
      saltLength: constants.RSA_PSS_SALTLEN_DIGEST,
    },
    token.signature,
  );
  if (!verified || assertionIds.has(claims.jti)) return false;
  assertionIds.add(claims.jti);
  return true;
}

function signed(claims) {
  const header = base64url(
    JSON.stringify({ alg: "RS256", typ: "JWT", kid: keyId }),
  );
  const body = base64url(JSON.stringify(claims));
  const signature = sign(
    "sha256",
    Buffer.from(`${header}.${body}`, "ascii"),
    signingKey,
  );
  return `${header}.${body}.${base64url(signature)}`;
}

function idToken(nonce) {
  const now = Math.floor(Date.now() / 1000);
  return signed({
    iss: issuer,
    aud: client,
    tid: tenant,
    sub: subject,
    oid: objectId,
    name: "Acceptance user",
    preferred_username: "acceptance.user@braid.example.test",
    iat: now - 1,
    nbf: now - 1,
    exp: now + tokenLifetimeSeconds,
    nonce,
    ver: "2.0",
  });
}

// An app-only v2 access token with the claims Entra issues to a client-credentials
// application: the requested resource as the audience, idtyp app and no scp.
function accessToken(machine, audience) {
  const now = Math.floor(Date.now() / 1000);
  return signed({
    aud: audience,
    iss: issuer,
    iat: now - 1,
    nbf: now - 1,
    exp: now + machine.lifetimeSeconds,
    aio: base64url(randomBytes(16)),
    azp: machine.clientId,
    azpacr: "1",
    idtyp: "app",
    oid: machine.objectId,
    rh: base64url(randomBytes(16)),
    sub: machine.objectId,
    tid: tenant,
    uti: base64url(randomBytes(16)),
    ver: "2.0",
  });
}

function secretValid(machine, secret) {
  const presented = createHash("sha256").update(secret, "utf8").digest();
  const expected = Buffer.from(machine.secretSha256, "hex");
  return (
    expected.length === presented.length && timingSafeEqual(presented, expected)
  );
}

// Client credentials for a registered machine client authenticated by its client secret.
function clientCredentials(form, response) {
  const fields = singletons(form, [
    "grant_type",
    "client_id",
    "client_secret",
    "scope",
  ]);
  const machine =
    fields !== null &&
    [...form.keys()].length === 4 &&
    machineClients.get(fields.client_id);
  if (!machine || !secretValid(machine, fields.client_secret))
    return refuse(response, "invalid_client", "machine-client");
  const resource =
    /^(?:api:\/\/)?([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\/\.default$/.exec(
      fields.scope,
    );
  if (!resource) return refuse(response, "invalid_scope", "machine-scope");
  event("machine-token");
  json(response, 200, {
    token_type: "Bearer",
    expires_in: machine.lifetimeSeconds,
    ext_expires_in: machine.lifetimeSeconds,
    access_token: accessToken(machine, resource[1]),
  });
}

async function redeem(request, response) {
  const form = new URLSearchParams(await readBody(request, 16384));
  if (form.get("grant_type") === "client_credentials")
    return clientCredentials(form, response);
  if (publicClients.redeem(form, response)) return;
  const fields = singletons(form, [
    "grant_type",
    "client_id",
    "client_assertion_type",
    "client_assertion",
    "code",
    "redirect_uri",
    "code_verifier",
  ]);
  if (
    fields === null ||
    form.has("client_secret") ||
    fields.grant_type !== "authorization_code" ||
    fields.client_id !== client ||
    fields.client_assertion_type !==
      "urn:ietf:params:oauth:client-assertion-type:jwt-bearer" ||
    !assertionValid(fields.client_assertion)
  )
    return refuse(response, "invalid_client", "token-client");
  const authorization = codes.get(fields.code);
  codes.delete(fields.code);
  const challenge = base64url(
    createHash("sha256").update(fields.code_verifier, "ascii").digest(),
  );
  if (
    !authorization ||
    authorization.expires <= Date.now() ||
    fields.redirect_uri !== callback ||
    challenge !== authorization.challenge
  )
    return refuse(response, "invalid_grant", "token-grant");
  event("token");
  json(response, 200, {
    token_type: "Bearer",
    expires_in: tokenLifetimeSeconds,
    access_token: base64url(randomBytes(32)),
    id_token: idToken(authorization.nonce),
  });
}

const tls = {
  key: tlsBundle.match(
    /-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]+?-----END [A-Z ]*PRIVATE KEY-----/,
  )[0],
  cert: tlsBundle
    .match(/-----BEGIN CERTIFICATE-----[\s\S]+?-----END CERTIFICATE-----/g)
    .join("\n"),
  ALPNProtocols: ["http/1.1"],
};

const server = createServer(tls, async (request, response) => {
  try {
    if (request.headers.host !== host)
      return refuse(response, "invalid_request", "host");
    const url = new URL(request.url, `https://${host}`);
    if (await publicClients.route(request, url, response)) return;
    if (
      request.method === "GET" &&
      url.pathname === paths.discovery &&
      url.search === ""
    )
      return json(response, 200, {
        issuer,
        authorization_endpoint: `https://${host}${paths.authorize}`,
        token_endpoint: tokenAddress,
        jwks_uri: `https://${host}${paths.keys}`,
        response_types_supported: ["code"],
        response_modes_supported: ["query"],
        subject_types_supported: ["pairwise"],
        scopes_supported: ["openid", "profile"],
        id_token_signing_alg_values_supported: ["RS256"],
        token_endpoint_auth_methods_supported: [
          "private_key_jwt",
          "client_secret_post",
        ],
        code_challenge_methods_supported: ["S256"],
      });
    if (
      request.method === "GET" &&
      url.pathname === paths.keys &&
      url.search === ""
    ) {
      const { n, e } = signingJwk;
      return json(response, 200, {
        keys: [
          { kty: "RSA", use: "sig", kid: keyId, alg: "RS256", n, e, issuer },
        ],
      });
    }
    if (request.method === "GET" && url.pathname === paths.authorize)
      return authorize(url, response);
    if (request.method === "POST" && url.pathname === paths.authorize)
      return await approve(request, response);
    if (
      request.method === "POST" &&
      url.pathname === paths.token &&
      url.search === ""
    )
      return await redeem(request, response);
    refuse(response, "invalid_request", "route");
  } catch (error) {
    // Error codes and names carry no protocol values.
    event(`error ${error?.code ?? error?.name ?? "unknown"}`);
    if (!response.headersSent) refuse(response, "server_error", "failure");
    else response.destroy();
  }
});
server.listen(443, "0.0.0.0", () => event("listening"));
for (const signal of ["SIGTERM", "SIGINT"])
  process.on(signal, () => server.close(() => process.exit(0)));
