# API authentication

The Session host's API accepts two credentials. The browser application uses its
[server session](browser-server-sessions.md): the `__Host-Weft.Session` cookie, plus
the epoch-bound `X-XSRF-TOKEN` header and exact `Origin` on unsafe methods. A
signed-in user's other clients, such as the `weft` CLI, can use a Microsoft Entra
**bearer token** instead, once a deployment enables bearer access. Both credentials
resolve the same person to the same internal principal, and every business operation
then runs the same fresh organization and project authority checks.

`contracts/weft-v1.json` declares both as security schemes: `weftSession` with
`weftAntiforgery` for unsafe methods, and the OAuth2 scheme `entraBearer`. Every
business operation lists both; the `/api/auth/*` operations admit an anonymous
request or a session, never a bearer token. The OAuth2 flows use the fixed tenant's
endpoints with placeholders: `{tenantId}` is `WEFT_ENTRA_TENANT_ID`, and the scope
`api://{sessionClientId}/{scope}` names the Session app registration's client ID and
its exposed scope. The device-code flow is the `x-deviceAuthorization` extension,
which follows the OpenAPI 3.2 `deviceAuthorization` flow object.

## App registrations

Bearer access uses the Session host's existing app registration (see
[Entra app registration](azure-deployment.md#entra-app-registration)) and one more
registration for the client.

- **Session app: expose a delegated scope.** Under *Expose an API*, keep the default
  Application ID URI `api://<session-client-id>` and add one scope, for example
  `weft.api`, that only administrators can consent to. Set the manifest's
  `requestedAccessTokenVersion` to `2`: Entra then issues v2 access tokens, whose
  `aud` is always the API's client ID. Version 1 tokens (`ver` `1.0`, issuer
  `https://sts.windows.net/...`) are refused.
- **Public client for the CLI.** Register a separate single-tenant application with
  no secret or certificate. Add the *Mobile and desktop applications* platform with
  the redirect URI `http://localhost`, for the authorization-code flow with PKCE on a
  loopback port, and turn on *Allow public client flows* for the device-code flow.
  Add the delegated API permission for the Session app's scope.
- **Consent.** Grant tenant-wide admin consent for that permission once, or add the
  CLI's client ID to the Session app's *Authorized client applications* for the scope.
  Conditional Access policies can block the device-code flow; the browser flow with
  PKCE then still works.

The CLI requests `api://<session-client-id>/<scope>`. MSAL adds `openid profile
offline_access`; `profile` is what gives the token the user's `oid` and `tid`.

## Configuration

Bearer access is off unless the Session profile sets all of these. Any partial or
mixed profile, a bearer setting in another authentication mode, or a plain
(non-`_FILE`) audit credential refuses startup.

| Setting | Value |
| --- | --- |
| `WEFT_API_BEARER_ENABLED` | `true`; absent or `false` disables bearer access, and then every other setting below must be absent. |
| `WEFT_API_BEARER_AUDIENCE` | The canonical client ID that v2 access tokens carry as `aud`. It must equal `WEFT_ENTRA_CLIENT_ID`: the Session app exposes the scope. |
| `WEFT_API_BEARER_SCOPE` | The scope name exactly as it appears in `scp`, for example `weft.api`: letters, digits, `.`, `_`, `-` or `:`, at most 120 characters, with no `api://` prefix. |
| `WEFT_API_BEARER_CLIENT_IDS` | Comma-separated canonical client IDs of the allowed public clients, at most 16, distinct and different from the audience. |
| `WEFT_API_AUDIT_WRITER_CONNECTION_STRING_FILE` | Protected private file with the `weft_api_audit_writer` connection string, admitted like the other [Session credentials](session-host.md). |

Startup does not contact Entra. The first bearer request loads the tenant's discovery
document and signing keys; until they load, bearer requests answer `503`.

## Accepted tokens

The validator shares the [machine token](machine-authorization.md#token-validation)
validator's fixed-tenant discovery, signing-key refresh and strict JWT handling.
There is no issuer, key, authority or transport override. A token is accepted only
with all of the following:

- an RS256 signature from an admitted signing key of the tenant, a `JWT` type if one
  is present, and no critical header;
- the exact issuer `https://login.microsoftonline.com/<tenant>/v2.0`, `tid` equal to
  the tenant, `ver` `2.0` and the exact audience as a string;
- integer `exp`, `nbf` and `iat`, valid now with no clock skew;
- `scp` containing the configured scope among its space-separated scopes;
- a delegated token: `idtyp`, when present, is `user`, and a token without `scp`,
  such as an app-only or roles-only token, is refused;
- `azp` in the allowlist; `appid`, when present, equals `azp`;
- a canonical nonzero `oid`, a nonempty `sub`, and no duplicated claim.

Roles, groups, names and other claims grant nothing. The caller is the tenant-stable
key `("entra.oid", tid, oid)` from [external identity
bindings](external-identity-bindings.md#entra-users), the same key browser sign-in
builds.

## Requests

- **Business routes only.** Every route under `/api/v1` that the catalog boundary
  admits takes a bearer token. The `/api/auth/*` routes refuse any `Authorization`
  header with `400`, whether or not bearer access is enabled.
- **One credential.** A bearer request must carry exactly one `Authorization: Bearer
  <token>` header and no cookie at all, so a bearer token never mixes with braid's
  session or antiforgery cookies. Without bearer access, an `Authorization` header on
  a business route is refused with `401` before the session is read, even alongside
  a valid session cookie.
- **No antiforgery, no CORS.** A bearer token is not an ambient credential, so bearer
  requests need neither `X-XSRF-TOKEN` nor `Origin`. The host answers no CORS
  preflight, so a browser page on another origin cannot send one.
- **No session.** Each request validates its token, then reads the enabled binding and
  the principal's activity freshly, and admits that principal for the configured
  organization. Disabling the binding or the principal takes effect on the next
  request; the token's own lifetime bounds a request already in flight. Nothing is
  cached, no session is created and no cookie is set.
- **Context.** `GET /api/v1/catalog/context` answers `mode` `bearer`, with no
  `requestToken` and no `identityContext`.
- **Server-sent events.** The chat event stream is an ordinary `GET` and works with a
  bearer token like any other read.

Refusals are fixed and carry no token, claim or provider text:

| Status | `code` | When |
| --- | --- | --- |
| `400` | `invalidRequest` | Malformed `Authorization`, or any cookie with a bearer token. Sends `WWW-Authenticate: Bearer error="invalid_request"`. |
| `401` | `identityRejected` | A refused token (`WWW-Authenticate: Bearer error="invalid_token"`), or any `Authorization` header while bearer access is disabled (no challenge). |
| `403` | `identityRejected` | A valid token whose `(tid, oid)` has no enabled binding, an inactive principal, or a binding that changed before the audit. |
| `429` | `rateLimited` | The principal's rate is exceeded. `Retry-After` gives whole seconds. |
| `503` | `unavailable` | The signing keys, identity store or audit store is unavailable. |

Chat routes answer the same statuses with their own error codes.

## Rate limit

Each principal's bearer requests share one token bucket: a burst of 60 requests,
then one request per second. A request over the limit is refused with `429` before
its audit or any business work. Browser sessions are not counted. The limit is per
Session host process.

## Audit

Migration `0025_api_request_audit.sql` adds the append-only `weft.api_request_audit`.
Each mutating bearer request (`POST`, `PUT`, `PATCH` or `DELETE`) records, before it
runs: the organization, principal, binding, client application (`azp`), operation ID
and method, and the route's resource identifiers, keeping only canonical UUIDs and
positive integers. It records no token, claim, header, body or response. The write
also checks that the binding is still enabled and owned by an active principal. If the
audit cannot be written the request is refused with `503`, and a changed identity is
refused with `403`, so no bearer mutation runs unaudited. Reads are not audited. An
audit row records an admitted attempt; the operation's own authority check can still
refuse it.

`weft_api_audit_writer` is a CONNECT-only login that can only execute
`weft.record_api_request`; it reads no table. Updates, deletes and truncation of the
audit are refused for every login. The host admits the login and the exact 0025
contract before each write.

## Privacy

The Session host's privacy profile covers bearer validation as it does browser
sign-in: IdentityModel's logger and PII flags stay off, ILogger output is disabled and
no request logging or Activity export is admitted. Tokens are kept only for the
request's validation and are never written to a log, an audit row, a response or an
exception message.

## Installing and upgrading

New installations create `weft_api_audit_writer` in `deploy/local/postgres-init.sh`
(password `WEFT_LOCAL_API_AUDIT_WRITER_PASSWORD`) or `deploy/azure/provision-database.sh`.
For an existing database, the administrator runs
`deploy/local/postgres-add-api-audit-role.sh` before the ordinary `migrate`; without
the login, migration 0025 refuses and the schema stays at version 24.

Browser sign-in and bearer access both key users on `(tid, oid)`. An installation
that bound users from their pairwise `sub` follows the [upgrade
procedure](external-identity-bindings.md#upgrading-an-installation-with-sub-bindings)
first; bearer tokens never match those older bindings.
