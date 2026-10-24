// Public-client flows of the test-owned issuer for the Weft CLI, which signs in through MSAL as a
// public client application: device authorization (RFC 8628) with a test-only approval step that
// stands in for the user entering the code, authorization code with PKCE (S256) on a loopback
// redirect (form_post or query response mode), and refresh-token redemption. Every grant issues a
// delegated v2 access token for the registered audience and scope, an ID token and client_info.
// It logs one fixed event name per protocol step and never logs codes, tokens or user codes.
import { createHash, randomBytes, randomInt } from "node:crypto";

const deviceGrant = new Set([
  "device_code",
  "urn:ietf:params:oauth:grant-type:device_code",
]);
const userCodeAlphabet = "BCDFGHJKLMNPQRSTVWXZ";
const limit = 64;

function base64url(bytes) {
  return Buffer.from(bytes).toString("base64url");
}

function escape(text) {
  return String(text).replace(
    /[&<>"']/g,
    (character) => `&#${character.charCodeAt(0)};`,
  );
}

/**
 * Creates the public-client handlers. `clients` lists the registered public clients as
 * {clientId, audience, scope}: the CLI's application ID, the API application ID its access
 * tokens are for, and the delegated scope name. `user` is {objectId, subject, username}.
 * `signed(claims)` signs a JWT with the issuer key and `event(name)` records a protocol step.
 */
export function createPublicClients({
  host,
  tenant,
  issuer,
  clients,
  user,
  signed,
  event,
  lifetimeSeconds = 3600,
  now = () => Date.now(),
}) {
  const id = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
  for (const client of clients)
    if (
      !id.test(client.clientId) ||
      !id.test(client.audience) ||
      !/^[A-Za-z0-9._-]{1,64}$/.test(client.scope)
    )
      throw new Error("A public client needs a client ID, audience and scope.");
  if (clients.length > 0 && !id.test(user.objectId ?? ""))
    throw new Error("The public clients require WEFT_ACCEPTANCE_OBJECT_ID.");
  const registered = new Map(
    clients.map((client) => [client.clientId, client]),
  );
  const base = `/${tenant}/oauth2/v2.0`;
  const devices = new Map(); // device code -> pending authorization, until redeemed
  const codes = new Map(); // authorization code -> issued authorization, single use
  const refreshTokens = new Map(); // refresh token -> client ID, rotated on every use

  function json(response, status, value) {
    const body = JSON.stringify(value);
    response.writeHead(status, {
      "Content-Type": "application/json; charset=utf-8",
      "Cache-Control": "no-store",
      "Content-Length": Buffer.byteLength(body),
    });
    response.end(body);
  }

  // MSAL requires an error description with every OAuth error.
  function refuse(response, error, name) {
    event(`refused ${name}`);
    json(response, 400, {
      error,
      error_description: "Refused by the test issuer.",
    });
  }

  function single(parameters, name) {
    const values = parameters.getAll(name);
    return values.length === 1 &&
      values[0].length > 0 &&
      values[0].length <= 16384
      ? values[0]
      : null;
  }

  // The requested scopes must include the client's delegated API scope.
  function scoped(client, scope) {
    return (scope ?? "")
      .split(" ")
      .includes(`api://${client.audience}/${client.scope}`);
  }

  async function body(request) {
    const chunks = [];
    let length = 0;
    for await (const chunk of request) {
      length += chunk.length;
      if (length > 16384) throw new Error("body too large");
      chunks.push(chunk);
    }
    return new URLSearchParams(Buffer.concat(chunks).toString("utf8"));
  }

  function tokens(response, client, nonce) {
    const issuedAt = Math.floor(now() / 1000);
    const common = {
      iss: issuer,
      tid: tenant,
      oid: user.objectId,
      sub: user.subject,
      iat: issuedAt - 1,
      nbf: issuedAt - 1,
      exp: issuedAt + lifetimeSeconds,
      ver: "2.0",
      name: "Acceptance user",
      preferred_username: user.username,
    };
    const refresh = base64url(randomBytes(32));
    if (refreshTokens.size >= 1024) refreshTokens.clear();
    refreshTokens.set(refresh, client.clientId);
    event("public-token");
    json(response, 200, {
      token_type: "Bearer",
      scope: `api://${client.audience}/${client.scope}`,
      expires_in: lifetimeSeconds,
      ext_expires_in: lifetimeSeconds,
      access_token: signed({
        ...common,
        aud: client.audience,
        azp: client.clientId,
        azpacr: "0",
        scp: client.scope,
        uti: base64url(randomBytes(16)),
      }),
      id_token: signed({
        ...common,
        aud: client.clientId,
        ...(nonce ? { nonce } : {}),
      }),
      refresh_token: refresh,
      client_info: base64url(
        JSON.stringify({ uid: user.objectId, utid: tenant }),
      ),
    });
  }

  async function deviceCode(request, response) {
    const form = await body(request);
    const client = registered.get(single(form, "client_id"));
    if (!client || !scoped(client, single(form, "scope")))
      return refuse(response, "invalid_client", "public-device-code");
    if (devices.size >= limit)
      return refuse(response, "temporarily_unavailable", "public-device-code");
    const device = base64url(randomBytes(32));
    let userCode = "";
    for (let index = 0; index < 8; index++)
      userCode += userCodeAlphabet[randomInt(userCodeAlphabet.length)];
    devices.set(device, {
      client,
      userCode,
      approved: false,
      expires: now() + 900_000,
    });
    event("public-device-code");
    json(response, 200, {
      device_code: device,
      user_code: userCode,
      verification_uri: `https://${host}/common/oauth2/deviceauth`,
      expires_in: 900,
      interval: 1,
      message: `To sign in, open https://${host}/common/oauth2/deviceauth and enter the code ${userCode}`,
    });
  }

  // Test-only: approves a pending user code, as the user would after entering it.
  async function approve(request, response) {
    const form = await body(request);
    const userCode = single(form, "user_code");
    const pending = [...devices.values()].find(
      (device) => device.userCode === userCode && device.expires > now(),
    );
    if (!pending) return refuse(response, "invalid_request", "public-approve");
    pending.approved = true;
    event("public-approved");
    response.writeHead(204, { "Cache-Control": "no-store" });
    response.end();
  }

  function authorize(url, response) {
    const query = url.searchParams;
    const client = registered.get(single(query, "client_id"));
    const redirect = single(query, "redirect_uri");
    const mode = query.get("response_mode") ?? "query";
    let target;
    try {
      target = new URL(redirect);
    } catch {
      target = null;
    }
    if (
      !client ||
      !target ||
      target.protocol !== "http:" ||
      target.hostname !== "localhost" ||
      single(query, "response_type") !== "code" ||
      single(query, "code_challenge_method") !== "S256" ||
      single(query, "code_challenge")?.length !== 43 ||
      !single(query, "state") ||
      !scoped(client, single(query, "scope")) ||
      query.has("client_secret") ||
      query.has("code_verifier") ||
      !["form_post", "query"].includes(mode)
    )
      return refuse(response, "invalid_request", "public-authorize");
    if (codes.size >= limit)
      return refuse(response, "temporarily_unavailable", "public-authorize");
    const code = base64url(randomBytes(32));
    codes.set(code, {
      client,
      redirect,
      challenge: query.get("code_challenge"),
      nonce: query.get("nonce"),
      expires: now() + 300_000,
    });
    event("public-authorize");
    const state = query.get("state");
    if (mode === "form_post") {
      // The approved sign-in posts the code to the loopback listener, never in a URL.
      const page = `<!doctype html>
<html lang="en">
<head><meta charset="utf-8"><title>Signed in</title></head>
<body onload="document.forms[0].submit()">
<form method="post" action="${escape(redirect)}">
<input type="hidden" name="code" value="${escape(code)}">
<input type="hidden" name="state" value="${escape(state)}">
</form>
</body>
</html>`;
      response.writeHead(200, {
        "Content-Type": "text/html; charset=utf-8",
        "Cache-Control": "no-store",
      });
      return response.end(page);
    }
    target.searchParams.set("code", code);
    target.searchParams.set("state", state);
    response.writeHead(302, {
      Location: target.href,
      "Cache-Control": "no-store",
    });
    response.end();
  }

  return {
    /** Handles a public-client request; false when the request is not one. */
    async route(request, url, response) {
      if (
        clients.length > 0 &&
        request.method === "GET" &&
        url.pathname === "/common/discovery/instance"
      ) {
        const endpoint = single(url.searchParams, "authorization_endpoint");
        if (
          single(url.searchParams, "api-version") !== "1.1" ||
          ![
            `https://${host}${base}/authorize`,
            `https://${host}/${tenant}/oauth2/authorize`,
          ].includes(endpoint)
        ) {
          refuse(response, "invalid_request", "public-instance-discovery");
          return true;
        }
        event("public-instance-discovery");
        json(response, 200, {
          tenant_discovery_endpoint: `https://${host}/${tenant}/v2.0/.well-known/openid-configuration`,
          "api-version": "1.1",
          metadata: [
            {
              preferred_network: host,
              preferred_cache: "login.windows.net",
              aliases: [
                host,
                "login.windows.net",
                "login.microsoft.com",
                "sts.windows.net",
              ],
            },
          ],
        });
        return true;
      }
      if (request.method === "POST" && url.pathname === `${base}/devicecode`) {
        await deviceCode(request, response);
        return true;
      }
      if (
        request.method === "POST" &&
        url.pathname === `${base}/deviceapprove`
      ) {
        await approve(request, response);
        return true;
      }
      if (
        request.method === "GET" &&
        url.pathname === `${base}/authorize` &&
        registered.has(url.searchParams.get("client_id"))
      ) {
        authorize(url, response);
        return true;
      }
      return false;
    },

    /** Redeems a token request from a registered public client; false for any other client. */
    redeem(form, response) {
      const client = registered.get(form.get("client_id"));
      if (!client) return false;
      if (form.has("client_secret") || form.has("client_assertion")) {
        refuse(response, "invalid_client", "public-token-client");
        return true;
      }
      const grant = form.get("grant_type");
      if (deviceGrant.has(grant)) {
        const device = devices.get(form.get("device_code"));
        if (!device || device.client !== client || device.expires <= now()) {
          refuse(response, "expired_token", "public-device-grant");
          return true;
        }
        if (!device.approved) {
          // Polling before approval is the protocol's normal path, not a refusal.
          json(response, 400, {
            error: "authorization_pending",
            error_description: "The user has not yet completed sign-in.",
          });
          return true;
        }
        devices.delete(form.get("device_code"));
        tokens(response, client, null);
        return true;
      }
      if (grant === "authorization_code") {
        const authorization = codes.get(form.get("code"));
        codes.delete(form.get("code"));
        const verifier = form.get("code_verifier") ?? "";
        const challenge = base64url(
          createHash("sha256").update(verifier, "ascii").digest(),
        );
        if (
          !authorization ||
          authorization.client !== client ||
          authorization.expires <= now() ||
          form.get("redirect_uri") !== authorization.redirect ||
          challenge !== authorization.challenge
        ) {
          refuse(response, "invalid_grant", "public-code-grant");
          return true;
        }
        tokens(response, client, authorization.nonce);
        return true;
      }
      if (grant === "refresh_token") {
        const owner = refreshTokens.get(form.get("refresh_token"));
        refreshTokens.delete(form.get("refresh_token"));
        if (owner !== client.clientId) {
          refuse(response, "invalid_grant", "public-refresh-grant");
          return true;
        }
        tokens(response, client, null);
        return true;
      }
      refuse(response, "unsupported_grant_type", "public-grant");
      return true;
    },
  };
}
