---
id: chat
title: Chat about your workspace
order: 12
summary: Ask about your workspace, review exact mapping proposals and recover their saves safely.
---

Chat answers questions about projects and mappings you can read, saved reports,
runs and this user guide. It can propose mapping edits; saving requires your
review and approval of that exact proposal with current edit permission.
It does not generate reports, start saved runs or change the version in use.
Where creation is configured, it can also propose a
[new mapping from specifications](creation.md). Chat must be set up for your workspace; if it isn't, the chat
entry is unavailable; the Conversations page explains when chat is not configured.

## Ask a question

1. Choose **Show chat** in the header. The same right-hand panel is available
   from mappings, projects, the guide and other workspace views.
2. Choose **New conversation**, or pick one under **Saved conversations**.
3. Choose a **Model profile**, type your **Question** and choose
   **Send question**.

A new workspace conversation does not need saved reports. Report tools only
read retained reports; they say when a report has not been generated. You can
[generate a report](reports.md#generate-a-report) yourself when appropriate.

Use **Focus for the next conversation** to choose a mapping. It lists the
mappings you can read as soon as it opens; type in **Find mapping** to filter
the list.
Choose the current head at each turn or an exact revision. New conversations
from a revision initially suggest that exact revision; you can choose another focus.
Focus guides context; it never
limits tools to that mapping. **Open in chat** continues the same conversation
in a separate tab without changing the panel's revision.

The answer appears as it is written. **Cancel answer** stops it. If the
connection drops, choose **Reconnect answer** to continue from where it
stopped; this doesn't ask the question again.

When the answer uses mapping tools, open tool activity to see
what it looked up.

## Ask for a mapping change

Name the mapping, exact base version and change you want. The proposal's change
summaries are not the complete edit, and may show only some changes.
Choose **Review complete operations**, read the complete operation script, then
check **I reviewed these complete operations for this exact proposal.**
Choose **Approve this exact proposal** only when those operations are correct.
Approval applies to that proposal and fingerprint, never the whole turn or future edits.

A new version can be saved only while its base is still the newest version and
you have current edit permission. A confirmed saved proposal offers **Open saved
version**; it does not activate, build or test the mapping. If a newer version
makes a proposal out of date, **Prepare new proposal question** prepares an
unsent question. Review it before sending. Its replacement needs a new review
and approval.

If a save is pending or unconfirmed, use **Refresh proposal status** before another
action. **Resume pending save**
recovers only that proposal's existing operation; recovery does not approve it
or supply missing source. If another approval is needed, review the complete
operations again before choosing **Approve this exact proposal again**.
An **Unidentified save receipt** cannot safely be matched to a proposal. Refresh
its status, but use the exact proposal card for approval or recovery, not a
turn-wide retry. Cancelling an answer does not undo a version already committed.

Older saved-edit history remains readable. Where a saved history card offers
**Undo**, it saves the earlier content as another version and refuses if a newer
edit has moved the head. New-mapping proposals use their separate
[review and save workflow](creation.md#approve-and-save-the-exact-mapping).

## Check the answer

Answers can cite fields, rules, diagnostics, other projects and mappings, runs,
and sections of this guide. A citation of the open revision and displayed saved
report selects its resource in the workbench. Other citations open their own
view in a separate tab. Access is checked again; a resource you can no longer
read is not shown.

Chat can be wrong. Check its citations and the report's
[coverage](reports.md#coverage) before relying on an answer.

## Your conversations

Conversations are private: only you can see yours. The exact focus resolved for
each turn is recorded, so an answer never silently follows a later head.
Existing revision-pinned conversations also appear in the list and keep their
original revision and report pins.
The **Conversations** navigation entry opens your library. Moving to another
workspace page or hiding chat keeps the same conversation and unsent question.
Use the left-edge splitter or width control to resize the panel. On a narrow
screen it opens as a focused view; **Hide chat** returns to your work.
Choose an earlier question in a conversation to read its answer again; this
doesn't ask the model again.

Each conversation has a retention period set by your administrators. After it
ends, the conversation's content can be erased.

If braid can't confirm that a question was received, it keeps the question in
the tab and offers **Recover original question**, which sends exactly the same
question again.

## Sample data policy

The model profile shows whether its destination permits sample data, including
specification evidence used for creation or feedback. This is
not a permission grant: a payload tool also needs your current project
`run.payload.read` grant. Without both, sample payload tools are not offered.
The server rechecks each tool call. Turns that actually share sample bytes are
marked **Data shared with** the selected model and audited without their data
content. Their answers may include sample-derived content and follow the
conversation's retention period.

Earlier turns that could use sample data stay visible to you but are not sent
to the model again automatically. Ask it to read the current data again; the
same profile and current project permission checks apply.

If conversation creation was not acknowledged, **Recover conversation creation**
reuses its original identity and focus. Unsent and uncertain work stays in tab
memory only. Switching conversations or leaving the workspace warns when such work could be lost; nothing is sent again
automatically.
