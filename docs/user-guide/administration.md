---
id: administration
title: Administration
order: 15
summary: Manage organization admission, invitations, project roles, payload access and machine callers.
---

Choose **Administration** in the workspace navigation. The operator must enable
the feature first. Organization and project administrators have different
responsibilities; being an organization administrator doesn't grant project
access.

## Organization members

An organization administrator can admit an existing principal, change its
organization role, suspend admission, or restore it. Suspension preserves
project memberships and history. Restoring admission restores the person's
previous project access. Disabling a principal or identity is permanent in the
app and refuses its next request. The last administrator cannot be removed or
disabled; add another administrator first.

## Invitations

Enter the fixed organization's Entra tenant ID. You can restrict the invitation
to an expected Entra object ID and add roles in projects you administer. Choose
**Create invitation**, then share the private link with its intended recipient.
The app shows the link only once. It expires after 24 hours. Revoke an unused
invitation if it was sent to the wrong person or the link is lost.

The recipient opens the link and chooses **Sign in and redeem invitation**.
braid binds the verified tenant and object ID, not an email address. Signing in
alone does not grant access to projects omitted from the invitation.

## Project access

Copy the project ID from the catalog's project details and choose **Open project
access**. A project administrator can add admitted principals, save their roles,
remove or restore memberships, and grant or revoke **payload read**. A project
role never grants payload access by itself.

For machine invocation, use the registered target ID, fixed tenant ID, and the
machine's service-principal object ID, not its client application ID. Grant
invocation separately for each target. Revocation retains the grant's history.

## Conflicts and unconfirmed changes

**Refresh access** reads current permissions and state. If another administrator
changed the state, refresh and review it before editing again. If a request was
not confirmed, it may already have committed: check the current state and recent
administrative changes instead of resending it automatically. Private invitation
links and administrative drafts are not stored across sign-out or reload.
The account menu warns when signing in or out would lose an unsent form, a
member-role choice, a pending edit, or a private invitation link. A normal
access refresh keeps unsent changes, and saving one form leaves other drafts
alone.
