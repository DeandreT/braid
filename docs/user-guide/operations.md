---
id: operations
title: Release and endpoint operations
order: 17
summary: Inspect deployed releases, invocation health and machine access, and make conditional release changes.
---

Open a mapping and choose **Operations**. You need the Publisher or Administrator
role. If the feature is not enabled, ask your workspace administrator.

## Inspect a target

Targets show their current version, enabled state, active release, build,
qualification, artifact and runtime identity. Choose **Operate this target** to
select its exact environment and reviewed target version.

Switch views to see releases, target state history, action audit, invocations,
failure and latency summaries, or caller grants. Use **Next page** to continue.
Invocation windows have an inclusive start and exclusive end; without a window
the last 24 hours are shown. No view displays input or output payload bytes.

## Change a release

Publishers can activate and roll back; Administrators can also disable and enable.
Activation needs your stored
qualification ID. Rollback also needs the ID of your previous successful release;
its build approval must still be valid. Review the target, environment and version,
then confirm the action.

Another operator or the release tool may change the target after you read it. In
that case, refresh and review it again instead of overwriting the newer state.
Disabling prevents new invocation admission; enabling does not restore revoked
caller grants or expired build approvals.

## Recover an uncertain result

If the response is lost, the page retains the original operation in this tab.
Choose **Recover original operation**. Recovery uses the same action and condition;
it does not repeat an older state change against today's target. Do not submit a
replacement with a new ID just because the first response was uncertain.
Closing or reloading the page loses this in-memory request. Keep its displayed
operation ID and original arguments if you need to recover from the command line.

The command line also supports these views and actions through `weft operations`.
Keep the original command arguments for recovery.

## Caller administration

The caller view lists the machine principal, grant, granting actor and time, and
revocation status. Only an Administrator can change grants. Ask an administrator
to use the workspace's machine-grant administration commands; the operations page
does not create credentials or turn a user session into a machine caller.
