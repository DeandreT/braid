---
id: chat
title: Chat about your workspace
order: 12
summary: Ask about your workspace and save mapping edits when you have edit permission.
---

Chat answers questions about projects and mappings you can read, saved reports,
runs and this user guide. When asked, it can save mapping edits as new versions if
you have edit permission. It never generates reports, starts runs or changes the
version in use. Chat must be set up for your workspace; if it isn't, the chat
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

Name the mapping, exact base version and change you want. Chat saves a new version
only while that base is still the newest version and you have edit permission.
Otherwise it shows a read-only proposal. The saved card shows before/after changes,
**Diff**, **Open version** and **Undo**. Undo saves the earlier content as another
version and refuses if a newer edit has already moved the head. Nothing is activated
or tested automatically.

If a newer version caused the proposal, **Update for version** sends a new question
for that selected mapping and edit. It includes the original question and change
summaries, then rechecks the current mapping. If the complete context won't fit or
the summaries are incomplete, nothing is sent; write a new, specific question.

A **Save pending** card is not confirmation that a version was saved. Use **Refresh
save status** before repeating an edit. **Continue pending save** explicitly resumes
the original accepted save with current permissions; it does not ask the model again.
Saved status is recoverable after an interrupted answer or a server restart. Cancelling
an answer does not undo a mapping version that already committed.

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

If Weft can't confirm that a question was received, it keeps the question in
the tab and offers **Recover original question**, which sends exactly the same
question again.

## Sample data policy

The model profile shows whether its destination permits sample data. This is
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
