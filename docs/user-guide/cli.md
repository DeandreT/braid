---
id: cli
title: Using the command line
order: 14
summary: Connect the weft command to your workspace, sign in, and use mappings safely from a terminal.
---

The `weft` command works as you, with the same project access as the browser.
It does not grant extra permissions or turn on features your workspace has disabled.
Ask your administrator for the CLI package and the connection settings for your
workspace. The CLI is available for Windows and Linux.

## Connect and sign in

Create a profile using the server address, tenant ID, CLI application ID and API
scope supplied by your administrator. Replace the example values below with
those settings; a profile contains connection settings, not a password.

```sh
weft profile set work --server https://weft.example.com --tenant TENANT_ID --client-id CLI_APPLICATION_ID --scope api://WEFT_APPLICATION_ID/weft.api --default
weft login
weft whoami
```

`weft login` prints a Microsoft address and a short code. Open that address and
enter the code yourself. Alternatively, `weft login --browser` opens the browser
for sign-in. Never paste a token or password into a command.

The CLI uses the operating system's protected credential storage. On Linux, it
needs an unlocked Secret Service keyring. If protected storage is unavailable,
sign-in stops instead of saving tokens in an unprotected file.

Use `--profile work` to choose a profile for one command. `weft logout` removes
that profile's local account and tokens; it does not sign out your browser or
other Microsoft applications.

`weft health` checks whether the configured server is alive. `weft health --ready`
checks whether it can accept work. These public checks do not need a sign-in or
access the token store. Readiness can be unavailable while the server safely
recovers its mapping service; do not treat that as a completed mapping operation.

## Find a mapping and read its reports

Start with `weft projects list`, then use `weft mappings list --project PROJECT_ID`
and `weft revisions list --project PROJECT_ID --mapping MAPPING_ID`. Replace
the capitalized IDs with the IDs returned by the previous commands.

Use `weft search "order number"` to search across the catalog resources you can
read. It includes saved report field and rule names and paths, not sample data.
Results identify the project, mapping, exact revision, retained report and
resource when applicable. Add `--json` for highlight spans and structured results.
Search accepts the same paging options as list commands. If saved reports are
still being indexed, retry the search; incomplete results are not returned as a
successful page.

`weft reports projection get` reads an existing explanation. `weft reports
projection ensure` can generate one if your permissions and the server allow it.
The validation commands work the same way. Use each command's `--help` to see
its required project, mapping and revision options. See [Reports](reports.md)
for what explanation coverage and validation results mean.

Other command groups cover [uploads](uploads.md), [sample previews](previews.md),
[regression tests](regression-tests.md), [builds and approvals](builds.md), and
[workspace chat](chat.md). Commands that change saved data still require the
appropriate project role.

For a mapping with explicit X12 or EDIFACT formats, use `weft previews create
--document` with the usual project, mapping, revision and input options. The
mapping selects the formats, not the input filename. Without `--document`, the
command keeps the JSON preview behavior. `weft regression capabilities` and the
build capability commands show the separate document profiles when available.

## Output and waiting

For read-only questions across your workspace, run `weft chat workspace availability`
and `weft chat workspace ask "How does this mapping work?"`. Add `--focus-project`,
`--focus-mapping` and optionally `--focus-revision` to guide the conversation without
restricting its tools. `weft chat workspace conversations list` includes both new
workspace and older revision-pinned conversations; older ones still use the original
`weft chat` commands with their original project, mapping and revision.

The workspace profile shows whether sample data is allowed, but your current project
payload-read grant is also required. Answers that actually share sample bytes are marked.
Use `weft chat workspace follow --conversation CONVERSATION_ID TURN_ID` to resume
reading a saved answer without sending another question.

- Human-readable output is the default. Add `--json` for structured output in a
  script. Read the command's exit code too; printed progress is not proof of success.
- List commands return a page. Use `--all` to request all pages. The command fails
  rather than silently returning an incomplete list if paging repeats or exceeds
  its safety limit.
- Waiting commands accept `--timeout`, such as `--timeout 90s`. A timeout does not
  mean the server canceled the work. Read the saved run or upload receipt before
  submitting anything again.
- `weft previews download` checks the output's media type, size and fingerprint before replacing
  a destination file or writing verified output to standard output.

## Recover an unconfirmed change

Keep the operation, approval or run ID printed by the command. Reuse the same ID
and the same inputs when retrying an unconfirmed change; a new ID is a new request.
An interrupted upload can be checked with `weft ingestions status` and continued
with `weft ingestions resume`. Do not send a second upload merely because the
first command stopped waiting.

Use `weft --help` and `weft COMMAND --help` for command options. You can also read
this guide in the terminal with `weft guide list` and `weft guide get cli`.
