# Browser sign-in and local session controls

The Angular shell uses the explicit authentication mode from
`GET /api/auth/session`; errors, 404s and missing configuration never imply
Development or anonymous access. Disabled hosts have no sign-in action, and Development displays
its existing local identity without pretending to be an Entra session. Neither mode
offers Microsoft sign-in; the header names the Development workspace. Session
deployments send signed-out application navigation to `/sign-in`, preserving a
validated return path. Signed-in controls live in the header's account menu.
Unavailable status shows a recovery message rather than assuming a mode.

## Using the controls

- **Sign in** checks the current session again, requests the fixed server login
  action, then navigates top-level to the bounded authorization URL returned by
  that admitted response. Navigation accepts only the fixed HTTPS Microsoft
  organizational-tenant `/oauth2/v2.0/authorize` endpoint. The server selects its
   configured tenant and OIDC query; Angular has no provider picker or OIDC library.
   It supplies only a return path accepted by the shared same-origin application
   path rule. **Sign in again** makes the same deliberate choice from an
  authenticated session and additionally requires its fresh epoch token. Returning
  from the provider is not proof of sign-in: the reloaded application observes
   fresh status, and catalog access performs its own admission.

Return paths keep the original ASCII URI wire representation through protected
OIDC state and the final redirect. The path portion retains its 1024-character bound
and reject hidden separators, dot segments, controls and other origins. Query
data is decoded once as strict UTF-8, not interpreted as path syntax: search
allows only singular `q` and `cursor` values, with 256 UTF-8 bytes of trimmed text
and a 4096-character base64url cursor. Retained revision links accept only their
report selectors and `field:`/`rule:` fragments containing a nonempty resource ID
of at most 2048 UTF-8 bytes; a projection selector is required for those anchors.
Unknown or duplicate query keys, malformed escapes and decoded controls fail
closed. Help anchors keep their separate bounded identifier grammar.

The complete return URI is bounded to 7168 ASCII characters, accommodating
percent-encoded resource IDs without increasing the path-segment allowance.
Protected state is capped at 12288 characters after encryption and encoding;
the existing 16384-byte authorization URL and callback-query limits, cookie
bounds and request-header limits remain enforced. The host's 65536-byte request
line allowance also accommodates the sign-in page's nested `returnTo` encoding.
- **Sign out** requests local Weft revocation. Only an exact 204 acknowledgement
  is presented as acknowledged local sign-out. This does not sign out the provider
  account. An error, timeout, canceled request, malformed response or superseded
  result leaves the outcome unconfirmed, not successful. Check session explicitly
  before choosing another action; the client never retries a mutation.
- **Review local recovery** is available only for the explicit
  `authenticationRequired` session state. It requires a second deliberate choice
  labelled **Forget local session — revocation unconfirmed**. The server alone
  decides whether its exact denied-carrier recovery rule permits local cookie
  deletion. A successful response means only `revocationUnconfirmed`: a server
  session may remain. A refusal, including not-applicable 409, is not success.
  This is never an automatic fallback from unavailable status or failed sign-out.
- **Check session** performs only a fresh status observation. It does not replay
  login/logout/forget or resume catalog writes. After acknowledged logout or
  forgetting, the auth controls perform no automatic bootstrap: use Check session
  rather than assuming an anonymous status or manufacturing replacement tokens.
  Existing catalog resource/focus admissions retain their own fresh checks. A
  later anonymous observation is not an acknowledgement of an earlier uncertain
  revocation.

When the current page holds unsent work, a public warning appears immediately
before the account menu's action buttons:

> This page holds unsent work. Signing in again reloads this page; signing out ends
> this session. Unsaved drafts and in-tab recovery details may be lost. Previously
> sent changes may already have committed.

The relevant sign-in and sign-out buttons reference the warning as their accessible
description. Feature panels register only the presence of unsent work, never its
contents. Empty pages do not warn. The warning exposes no private recovery details
and asks for no separate confirmation. Sign-out does not reload the document.

Sign-in navigation leaves the whole document. Angular's existing catalog route
leave guards, including its native fixed privacy confirmation for hidden workspace
navigation, apply to router navigation; they do not intercept this native full-page
authorization navigation. Unsaved in-memory drafts and recovery material do not
survive a reload, provider round trip or tab closure. Resolve or intentionally
abandon pending catalog work before signing in. The controls add no unload guard,
persistence or second catalog recovery mechanism.

## Ownership and failure boundaries

`BrowserSessionState` remains the single tab-memory provenance boundary. Each
explicit cold action admits fresh status and requires the selected owner/state,
identity context and generation to remain unchanged. Before sending a POST it
invalidates the existing boundary synchronously, letting the catalog hide protected
content and suspend its own original-operation recovery. Busy and categorical
admission deny overlapping actions. Status observation cannot publish an old
identity while that POST is pending. Late action results must match both the
generation and observation fence before displaying acknowledgement or navigating.
Recovery never transfers an original catalog operation to another identity.

The current protected-request rejection classifier remains separately service-owned;
retiring its subscriber does not cancel the auth action subscription. A canceled
auth caller or service destruction ends its request without converting uncertainty
to success. No completed auth result is shared or replayed, and no automatic retry
or post-action check is introduced.

The separate auth-action antiforgery token is used only as `X-Weft-Auth-Token`.
Positive-session login/logout additionally use the fresh status mutation token
as `X-XSRF-TOKEN`; neither is replaced by a catalog context token. Forgetting uses
only the auth-action token. Mutation transport uses `HttpBackend` directly, without
interceptor retries or readable-cookie XSRF fallback. The browser supplies Origin
and cookies for same-origin bodyless POSTs; the client does not inspect cookies.
Tokens, fingerprints, authorization URLs and identity operands remain in private
tab memory, not UI projections, logs, browser storage or cross-tab buses. Only the
intentionally requested top-level navigation uses the returned URL. No auth URL is
rendered as a persistent link.

These fences govern this tab's presentation, not distributed revocation or browser
cookie delivery. Another tab may change the shared cookie, and a late HTTP response
may affect it even when its UI callback is fenced. Fresh server admission remains
mandatory for protected operations.

The controls use native buttons, live categorical status, a labelled keyboard-focusable
recovery review, and wrapping shell styles. They preserve the existing catalog
admission and original-owner recovery contracts. The fixed-tenant protocol and
host profile belong to the separate HTTP and Server boundaries; see
[Browser authentication HTTP](browser-authentication-http.md) and
[Entra browser client](entra-browser-client.md).
