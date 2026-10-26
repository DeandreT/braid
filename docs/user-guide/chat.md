---
id: chat
title: Chat about your workspace
order: 12
summary: Ask read-only questions about mappings, projects, runs and the user guide.
---

Chat answers questions about projects and mappings you can read, saved reports,
runs and this user guide. It never generates reports, starts runs or changes
mappings. Chat must be set up for your workspace; if it isn't, the chat
panel says **Chat is not configured for this workspace**.

## Ask a question

1. Open **Chat** in the header, or open a revision and choose **Ask Weft** in the
   investigation dock.
2. Choose **New conversation**, or pick one under **Saved conversations**.
3. Choose a **Model profile**, type your **Question** and choose
   **Send question**.

A new workspace conversation does not need saved reports. Report tools only
read retained reports; they say when a report has not been generated. You can
[generate a report](reports.md#generate-a-report) yourself when appropriate.

On the chat page, use **Focus for the next conversation** to search mappings.
Choose the current head at each turn or an exact revision. New conversations
from the workbench focus its exact revision. Focus guides context; it never
limits tools to that mapping. **Open in chat** continues the same conversation
in a separate tab without changing the panel's revision.

The answer appears as it is written. **Cancel answer** stops it. If the
connection drops, choose **Reconnect answer** to continue from where it
stopped; this doesn't ask the question again.

When the answer uses mapping tools, open **Read-only tool activity** to see
what it looked up.

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
reuses its original identity and focus. Unsent and uncertain work stays in page
memory only. Leaving warns when such work could be lost; nothing is sent again
automatically.
