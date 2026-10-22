# Revision chat

The saved revision screen places private mapping chat beside its reports. The
chat uses the [chat HTTP API](chat-http.md). It never navigates to another
revision, starts report derivation, or reads production payloads.

## Conversations and questions

Choose **New conversation** to pin the current verified explanation and
validation reports, or open one of the newest 50 saved conversations. Up to 100
recent questions are listed. Selecting a question reads its answer from the
durable event stream; it does not run the model again.

Choose a configured model profile, enter a question, and choose **Send question**.
The service controls provider endpoints, tools and limits. The browser sends
only the profile ID/version, question, client-generated turn ID, and an empty
selector list. Answers are plain text, not executable HTML. Tool activity and
the latest token snapshot per round are displayed separately; unknown usage is
not treated as zero, and observations are not added together.

**Cancel answer** makes an explicit cancellation request. **Reconnect answer**
reads events after the last accepted sequence without creating another turn.
Closing a tab does not claim to cancel the server's operation.

If a question's POST acknowledgement is lost, **Recover original question**
resends the exact original ID and body only when requested. No timer retries a
mutation. Confirmed admission refusals retain the draft so the user can correct
it; uncertain requests retain their original identity in tab memory. Nothing is
written to local storage, session storage or a browser database. After a reload,
open the saved conversation explicitly to recover acknowledged server work.

## Citations and access

Citations select resources only when the conversation's revision and exact
report derivation match the displayed report. A different or missing report
produces a notice instead of changing the viewer selection. Validation and
operation diagnostic ordinals refer to their own report lists; explanation
document diagnostics are not substituted for operation diagnostics.

Each operation observes fresh session status and catalog context. Session
boundaries and catalog refreshes invalidate outstanding requests and clear
displayed conversations. An unsent question, an unacknowledged question and an
unconfirmed new-conversation request are instead suspended in a page-scoped
provider. After the panel's next availability read, the same owner (mode,
principal, organization and Session identity context) on the same revision gets
them back, and the conversation they belong to is reopened. Nothing is sent
again. A fresh context for another owner, another revision or leaving the page
discards them. Expired or
rejected sessions, unconfigured chat, missing reports, unavailable profiles,
provider failure and interrupted streams remain distinct from a successful
answer. The server remains responsible for fresh project authorization.

Chat waits for the initial report reads before loading its availability. Its
fresh context checks do not replace the catalog's shared selected context:
replacing that context would invalidate the page that owns the chat itself.
Settlement tracks actual requests, including cancellation after a report
refusal; an idle presentation pane does not imply a request is still running.
After admission hides and restores the workspace, keyboard focus returns to the
initiating control, or to the composer after creating a conversation. A deliberate
focus move elsewhere is preserved. An unacknowledged send focuses the explicit
recovery action rather than a disabled composer or send button.

## Browser checks

`apps/web/playwright.chat.config.ts` runs the focused synthetic API suite with an
offscreen browser. From `apps/web`, run `npx playwright test --config
playwright.chat.config.ts` using the repository's pinned Node/npm versions.
These checks exercise presentation, request recovery, stream framing and
revision/report-bound citation selection; they do not qualify a live provider,
session deployment, database or hosted environment. Keep the existing ordinary
browser and type/build checks as separate regression gates.
