# Browser sign-in and local session controls

The Angular shell uses the explicit authentication mode from
`GET /api/auth/session`; errors, 404s and missing configuration never imply
Development or anonymous access. The examples remain independent of catalog
authentication. Disabled hosts have no sign-in action, and Development displays
its existing local identity without pretending to be an Entra session. Neither mode
shows the session panel; the header names the Development workspace. Unavailable
status keeps the panel, because it cannot tell which mode applies.

## Using the controls

- **Sign in** checks the current session again, requests the fixed server login
  action, then navigates top-level to the bounded authorization URL returned by
  that admitted response. Navigation accepts only the fixed HTTPS Microsoft
  organizational-tenant `/oauth2/v2.0/authorize` endpoint. The server selects its
  configured tenant and OIDC query; Angular has no provider picker, return target
  or OIDC library. **Sign in again** makes the same deliberate choice from an
  authenticated session and additionally requires its fresh epoch token. Returning
  from the provider is not proof of sign-in: the reloaded application observes
  fresh status, and catalog access performs its own admission.
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

Whenever **Sign in** or **Sign in again** is shown, a fixed public warning appears
immediately before the panel's action buttons:

> Signing in opens Microsoft and reloads this page. Unsaved drafts and in-tab
> recovery details may be lost. Previously sent changes may already have committed.

Both login buttons reference this warning as their accessible description. It is
shown in anonymous, authenticationRequired and authenticated states, matching the
presence of a login button. The warning does not inspect drafts, expose private
recovery details or ask for a separate confirmation.

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
