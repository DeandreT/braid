# Bounded private-object maintenance command

`Weft.MaintenanceTool` performs one explicit maintenance invocation for one
organization/project. It adds no background job or HTTP route. Implementation
and qualification are recorded separately in
[issue #62](https://github.com/DeandreT/weft/issues/62) and its linked implementing
pull request; this document describes the command contract.

## Configuration and invocation

Run the published tool under the identity that owns the existing private root:

```text
dotnet Weft.MaintenanceTool.dll run-once ORGANIZATION_ID PROJECT_ID ROOT APPLICATION_ROOT [--repository-root REPOSITORY_ROOT] [--cleanup-after JSON] [--status-after JSON]
```

Organization/project IDs must be nonzero UUIDs in canonical lowercase D format.
Each named option may occur once. ROOT, APPLICATION_ROOT and optional
REPOSITORY_ROOT use the existing storage path/exclusion rules. The root must
already be strict v2; the command does not initialize, activate, repair or fall
back to v1 storage.

Set the dedicated connection through `WEFT_MAINTENANCE_CONNECTION_STRING`.
There is no credential argument or fallback to another pool. The existing
maintenance role/schema admission and organization/project binding remain in
force. The tool opens one lazy maintenance pool and one existing v2 store,
resolves no publication registry or mapping worker, and awaits owned disposal
before emitting a result. Sole `--help` prints usage and exits successfully.

## One bounded run

The Application service first obtains one status page, observes the project's
physical entries, then matches its candidate slots to retained registrations.
Unavailable, changed or inconsistent evidence stops the run before claiming new
destructive work. A successful page may still be incomplete. The observation
bounds and meaning are described in [Maintenance observations](maintenance-observations.md).

The service then makes one committed Claim call, examining at most 100 metadata
candidates and returning at most 16 claims. An uncertain Claim reply authorizes
no physical operation. Only those original claims reach retirement, serially,
outside database transactions. Observed registration matches confer no cleanup
authority and never become extra cleanup targets.

The existing policy protects every native revision/history, resumable ingestion,
and successful selected or retained report. Eligible terminal native stages,
failed/expired report candidates and positively tracked abandoned temporaries
still require the database-clock 24-hour grace and the applicable ownership,
reference, gate and permanent-fence checks. Unknown or unregistered bytes are not
adopted or swept. There is no successful-report retention expiry.

New observations, Claim, Retire and Finish admission stop at a monotonic
25-second cutoff. One shared settlement budget ends at 30 seconds and may only
shorten to caller cancellation plus five seconds. Ctrl+C cancels the actual
service token. Already-started calls, disposal and synchronous kernel work are
awaited truthfully; these are cooperative budgets, not a hard return-time promise.

Actual Deleted/Missing observations survive a later stop even when no Finish can
start. Io and UnsafeStorage may be acknowledged with their matching existing
dispositions. Busy and Unsupported remain distinct and receive no fabricated
completion. Their original open attempt retains its 60-second lease, subsequent
30-second retry delay and eight-attempt ceiling. Cancellation does not start an
extra Finish merely to journal the stop.

After a failed Finish settles, acknowledgement recovery uses only the remaining
shared budget and the entire original claim, retirement/generation, expected
disposition and nonnull completion. It never repeats deletion, finishes a newer
attempt or adds another recovery allowance. Process death can leave an open
attempt or an unacknowledged physical result; a later legitimate Missing result
is an actual storage observation, not an invented Deleted acknowledgement.

## Results and continuation

After a run settles, stdout contains one camelCase JSON serialization of
`PrivateObjectMaintenanceRunResult`, with categorical string enums and explicit
nulls. It exposes no raw names, private paths, actor/lease credentials, rich
claims or exception text. Physical and acknowledged counts are separate. Missing
evidence is null; a known empty response is zero. Finished means this bounded
batch was processed, not that every target was deleted or the project is clean.

Observation counts remain separate from destructive counts. Exhaustion uses the
initial database-time status observations plus distinct newly acknowledged
eighth-attempt failures; it is not inferred later from the host clock or promoted
to a project total. Structural hierarchy directories are entries, not unproved
payloads. Filesystem completeness describes only that bounded traversal.

For another explicit invocation, pass the complete `nextCleanupCursor` object as
`--cleanup-after` and/or `observations.nextStatusCursor` as `--status-after`.
These are independent scoped JSON objects, not the whole result. Preserve every
member, including the cleanup target's redundant scope. Cursor UUIDs use the
same canonical spelling, and cleanup time must parse with zero UTC offset.
Cleanup JSON is limited to 2048 strict UTF-8 bytes, status JSON to 512, and depth
to eight. Unknown, duplicate, missing or inconsistent members are refused. There
is no filesystem cursor or automatic second batch/page loop. Status-page EOF
after a cursor does not cover its omitted prefix.

| Exit | Meaning |
| --- | --- |
| 0 | Finished with every target acknowledged, including a known empty batch. Acknowledged retryable failures remain visible in the result. |
| 3 | Finished with a known unfinished/refused target, including Busy or Unsupported. |
| 1 | Failed, Canceled or Deadline result, or a fixed operational/setup/disposal failure category. |
| 2 | Usage or invalid configuration before the run. |

Usage/setup errors produce fixed stderr categories without submitted values or
provider diagnostics. A disposal or serialization failure cannot emit a Finished
summary or a success exit. Incomplete observations and zero targets do not imply
an all-clean inventory.
