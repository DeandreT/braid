# Weft CLI

`weft` works with the Weft API as the signed-in user. It signs in to Microsoft Entra ID as a
public client application and sends the user's delegated access token, so every command has
exactly the user's own project roles and the server's feature settings, as in the browser.
Source: `dotnet/apps/Weft.Cli`.

## Install

The CLI targets linux-x64 and win-x64.

- **.NET tool** (needs the .NET 10 runtime):

  ```sh
  dotnet pack dotnet/apps/Weft.Cli/Weft.Cli.csproj -c Release -o ./artifacts/cli
  dotnet tool install --global Weft.Cli --add-source ./artifacts/cli
  ```

  The pack produces a small `Weft.Cli` package and framework-dependent `Weft.Cli.linux-x64` and
  `Weft.Cli.win-x64` packages; `dotnet tool install` picks the one for the current platform.
- **Single-file executable** (self-contained, no .NET installation needed):

  ```sh
  dotnet publish dotnet/apps/Weft.Cli/Weft.Cli.csproj -c Release -p:WeftCliRuntime=linux-x64 -o ./artifacts/weft-linux-x64
  dotnet publish dotnet/apps/Weft.Cli/Weft.Cli.csproj -c Release -p:WeftCliRuntime=win-x64 -o ./artifacts/weft-win-x64
  ```

  Each output folder holds only `weft` or `weft.exe`. Both builds work with the repository's
  locked restore (`-p:RestoreLockedMode=true`).

## Entra registration and server settings

The CLI needs its own app registration in the Weft tenant:

- a public client: enable **Allow public client flows** for device-code sign-in, and add the
  redirect URI `http://localhost` on the **Mobile and desktop applications** platform for
  `weft login --browser`;
- a delegated API permission for the Weft API application's scope (for example
  `api://<Weft application ID>/weft.api`), with the consent the tenant requires.

The server accepts the CLI's tokens only when bearer access is enabled for this client ID; see
[API authentication](api-authentication.md). The signed-in user must be bound and admitted,
exactly as for the browser.

## Profiles

A profile names one server and the registration the CLI signs in with:

```sh
weft profile set prod --server https://weft.example.com --tenant <tenant ID> \
  --client-id <CLI application ID> --scope api://<Weft application ID>/weft.api --default
weft profile list
weft profile show [name]
weft profile use <name>
weft profile remove <name>
```

- Commands use `--profile <name>`, otherwise `WEFT_PROFILE`, otherwise the default profile.
- The server must be an `https://` origin with no path. Plain `http://` is accepted only for a
  loopback address, for local development.
- The authority is always `https://login.microsoftonline.com/<tenant>`. Nothing in a request,
  a profile or the environment can point sign-in elsewhere.
- The scope must be the delegated Weft scope; `.default` scopes are refused.

Profiles live in `config.json` in the CLI's directory: `WEFT_CONFIG_DIR` if it is set,
otherwise `%APPDATA%\Weft` on Windows and `$XDG_CONFIG_HOME/weft` (or `~/.config/weft`)
elsewhere. The directory and file are owner-only. On Unix they are created with modes 0700 and
0600, and a file or directory that other users can reach is refused. On Windows they get an
explicit access list that grants only the current user. An existing directory or file that other users can
reach is refused and never changed. The file holds no secret, and it is parsed strictly: unknown or duplicate
members are refused.

## Sign-in

```sh
weft login            # device code: open the shown address and enter the code
weft login --browser  # system browser: authorization code with PKCE on a loopback redirect
weft whoami           # the cached account and the identity the server admits
weft logout           # remove the profile's account and its tokens
```

- Device-code sign-in prints only the verification address and the user code.
- `--browser` uses MSAL's loopback listener on `http://localhost:<port>`, which receives the
  code by `form_post`.
- A profile keeps one account: a new sign-in replaces the earlier one.
- Later commands get tokens silently, refreshing them when they near expiry. If a refresh is
  refused, the command exits with code 3 and asks you to sign in again.
- `whoami` and `context` read `GET /api/v1/catalog/context`. The organization they report is
  also the default for organization-scoped commands; `--organization` overrides it.

## Token storage

MSAL keeps its token cache in the operating system's protected store, through MSAL
Extensions:

- Windows: a DPAPI-encrypted file for the current user, in the CLI's directory.
- macOS: the Keychain.
- Linux: the Secret Service keyring through libsecret. This needs a D-Bus session and an
  unlocked keyring, such as GNOME Keyring or KeePassXC.

Each profile of each configuration directory has its own cache entry. The CLI verifies the store before it signs in. If no
protected store is available, `weft login` exits with code 14 and writes nothing. There is no
plaintext fallback. The CLI never accepts a token from an argument or an environment variable,
and never prints one. Diagnostic output is also passed through a redaction filter for anything
shaped like a token.

## Commands

| Command | Operations |
| --- | --- |
| `weft context`, `weft whoami` | Catalog context and admitted identity |
| `weft guide list/get` | The authenticated server's user guide, as readable text or exact structured JSON |
| `weft projects list/get/create` | Projects; creation makes you the project's administrator |
| `weft mappings list/get/create` | Mappings; `create` uploads the first native revision |
| `weft mappings metadata get/edit` | Name and description, edited with `If-Match` |
| `weft revisions list/get/append` | Immutable revisions; `append --expected-head` appends only on that head |
| `weft ingestions status/resume` | Upload receipts and source-free recovery |
| `weft reports projection/validation get/ensure` | Current or retained (`--derivation`) reports; `ensure` derives and waits |
| `weft compare` | Projection comparison of two revisions, with optional validation |
| `weft previews create/get/cancel/download` | Sample previews and verified output downloads |
| `weft regression capabilities`, `cases list/get/create`, `runs list/create/get/cancel` | Saved cases and their runs |
| `weft builds capabilities`, `environment-capabilities`, `environments list/get/create`, `approvals list/get/create/revoke`, `runs list/create/get/cancel` | Build environments, approvals and runs |
| `weft chat availability`, `conversations list/get/create`, `ask`, `follow`, `cancel` | Grounded chat about a revision |

`weft <command> --help` lists every option.

- Identifiers are UUIDs. They are canonicalized to lowercase before they are sent.
- Lists take `--limit`. Catalog lists also take `--cursor`. `--all` reads every page; with `--json` it writes the
  list's usual document with every item and no next cursor.
  A repeated cursor or more than 1,000 pages fails rather than returning an incomplete list as complete;
  use individual pages for larger catalogs.
  Build environment lists continue with the paired `--after-environment` and `--after-version` values;
  approvals, build runs, regression cases and regression runs use `--after`.
- Inputs are files, or `-` for standard input: native mappings, preview inputs,
  regression-case definitions and chat questions. Inputs are bounded by the server's limits.
- Waiting commands take `--timeout`, for example `90s`, `10m` or `1h`:
  - `previews create --wait`, `regression runs create --wait` and `builds runs create --wait`
    poll until the run finishes, and exit 15 if it did not succeed;
  - `reports ... ensure` waits as the server's `Retry-After` advises unless `--no-wait` is given;
  - uploads wait for validation unless `--no-wait` is given.

## Output

Human-readable tables and fields are the default. `--json` writes the exact response document
of the API contract instead, for scripts; browser-session request tokens are always withheld.
Text that comes from the server or a model, such as names, findings and chat answers, is shown
with control and bidirectional-override characters replaced, so stored text cannot control the
terminal. Progress, retries and sign-in prompts go to standard error.

## Requests and recovery

- **Transport:** every request carries only `Authorization: Bearer`. There is no cookie handler,
  redirects are not followed, and no browser antiforgery header is sent.
- **Retries:** a `429` or a busy `503` is retried after the server's `Retry-After`, capped at 60
  seconds (2 seconds when absent), up to five attempts. Connection failures are retried with
  backoff. A `401` triggers one forced token refresh and one retry.
- **Idempotency:** a retry replays the request exactly, with the same identity:
  - the `Idempotency-Key` of project creation, uploads and previews, which you can choose with
    `--request-id` or `--operation-id`;
  - the client-chosen ID in the path for regression cases and runs, build environments,
    approvals and runs;
  - the conversation or turn ID in the body for chat.
  When an approval omits `--build`, its default build ID is derived from the approval ID.
  Repeating `builds approvals create --approval-id <ID>` therefore reuses the same build after a lost response.
- **Resumable uploads:** an upload prints its operation ID before it sends anything.
  - If the server needs the source again, the CLI sends the same bytes under the same key.
  - A pending or retryably failed upload is resumed without its source.
  - An interrupted upload continues with `--operation-id <ID>` and the same file, or with
    `weft ingestions resume`.
- **Metadata edits** send the strong entity tag just read, or `--if-match`. A concurrent change
  exits with code 8.
- **Chat:** `weft chat ask` streams the answer from the turn's server-sent events and then lists
  the verified citations.
  - A dropped stream reconnects after the last delivered sequence.
  - Interrupting the command (Ctrl+C) cancels the turn on the server; the CLI allows up to 20 seconds for that
    before it exits.
  - `weft chat follow` replays and follows an existing turn.
  - Conversation reads explicitly say when earlier turns are omitted from the returned window.
- **Preview downloads** stream to a temporary file beside the destination. The file is kept
  only when its size and SHA-256 match the run's output record and the `Content-Digest` header.
  Standard-output downloads are staged within the server's 8 MiB output bound and fully verified
  before any bytes are written to standard output.

Requests retain their 120-second deadline through response-body reads. Successful chat SSE bodies
remain long-lived; their connection and headers still have a deadline. Waiting commands' `--timeout`
also cancels in-flight polling requests and their body reads, rather than only stopping between polls.

## Exit codes

| Code | Meaning |
| --- | --- |
| 0 | Success |
| 1 | Unexpected client failure |
| 2 | Invalid arguments, profile settings or local input |
| 3 | Not signed in, or the sign-in expired: run `weft login` |
| 4 | Sign-in failed, or the server refused the signed-in identity (`401`, or `403 identityRejected`) |
| 5 | Permission denied (`403`) |
| 6 | Not found or not visible (`404`) |
| 7 | Conflict (`409`), including idempotency and head conflicts |
| 8 | Precondition failed or required (`412`, `428`) |
| 9 | Request refused as invalid, too large or of an unsupported type (`400`, `413`, `415`, `422`) |
| 10 | Rate limited after the retries (`429`) |
| 11 | Feature disabled or temporarily unavailable (`404 unavailable`, `503`) |
| 12 | Server failure (`5xx`) |
| 13 | The server was unreachable, or its response was not a valid Weft response |
| 14 | No operating-system protected token store |
| 15 | A waited-for operation finished without success |
| 130 | Canceled |

Error messages name the HTTP status and the API's closed error category, never the response
body.

## Contract alignment

`Http/ApiOperations.cs` lists every documented operation the CLI calls, together with its
method, path, the only query parameters and headers it may send, its media types and its
contract types. Operations it does not call are listed with the reason. A request can set only
the parameters its operation declares.

`dotnet/tests/Weft.Tests/Cli/CliContractDriftTests.cs` compares that table with
`contracts/weft-v1.json`:

- every documented operation is either called or listed as unused;
- every sent parameter is declared, and every required one is sent;
- the media types match;
- the request, response and error contracts have the documented JSON shapes.

The command tests run the real CLI against a stub that is built from the same document. The
stub rejects any undeclared parameter, missing bearer token or cookie, and any body that does
not conform to its schema. The [hosted acceptance](hosted-acceptance.md#cli-stage) runs the
published single-file CLI against the packaged stack.
