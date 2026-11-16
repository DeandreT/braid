---
id: automation
title: Automation and notifications
order: 16
summary: Run regression checks on new versions, require them before builds, and choose activity notifications.
---

Open a project and choose **Automation and notifications**.
Choose **Notifications** in the header to see activity from all your visible
projects and organization invitations addressed to you.

## Automatic regression checks

Automatic checks are on by default when a mapping has saved regression cases.
Every new version runs all the current cases against that new version, not the
older version where the case was created. Original samples and expected outputs
stay unchanged. A failed case does not prevent the other cases from running.

Mapping and version lists show pending, passed or failed checks. **No cases** or
**not recorded** does not mean the version passed. A mapping administrator can
turn checks off for future versions without removing saved results.

## Require passing checks before builds

A project administrator can enable **Require passing exact-revision regression**.
braid then refuses approval and qualification until all current cases pass on the
exact version being built. A pass on a different version does not count.

## Choose notifications

Select the events you want, choose the in-app centre and any available external
channels, then save your subscriptions. Notifications include IDs and links,
never mapping payloads or private error details. Email and webhooks appear only
after an operator configures the provider, verified recipients and destinations.

Use the notification centre to read events and mark them read. Administrators can
also inspect the delivery log. External delivery retries a limited number of
times; repeated failures disable a destination until an administrator re-enables
it. Delivery means the provider accepted the message, not that a person read it.

Organization-only invitations appear in the in-app centre without a project
subscription. They do not send unsolicited email, and never include the private
invitation link. Use the original secure link to join or ask the administrator
for one. Project visibility is checked again when you open the centre.

See [regression tests](regression-tests.md) to create saved cases.
