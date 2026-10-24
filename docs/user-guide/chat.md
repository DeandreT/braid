---
id: chat
title: Chat about a revision
order: 12
summary: Ask questions about a revision and check the answers against its reports.
---

Chat answers questions about one revision, using its saved explanation and
validation. It never reads production data, generates reports, runs samples or
changes anything. Chat must be set up for your workspace; if it isn't, the chat
panel says **Chat is not configured for this workspace**.

## Ask a question

1. Open the revision in the workbench and choose the **Ask Weft** tab in the
   investigation dock.
2. Choose **New conversation**, or pick one under **Saved conversations**.
3. Choose a **Model profile**, type your **Question** and choose
   **Send question**.

A new conversation needs the revision's saved explanation and validation. If
they haven't been generated, [generate them](reports.md#generate-a-report)
first.

The answer appears as it is written. **Cancel answer** stops it. If the
connection drops, choose **Reconnect answer** to continue from where it
stopped; this doesn't ask the question again.

When the answer uses mapping tools, open **Read-only tool activity** to see
what it looked up.

## Check the answer

Answers can cite fields, rules and diagnostics. Choose a citation to select it
in the mapping table or the **Issues** tab. A citation works only while the
page shows the same saved report the conversation used; otherwise Weft tells
you it isn't available.

Chat can be wrong. Check its citations and the report's
[coverage](reports.md#coverage) before relying on an answer.

## Your conversations

Conversations are private: only you can see yours. Each one stays tied to the
revision and reports it started with, even after newer revisions are uploaded.
Choose an earlier question in a conversation to read its answer again; this
doesn't ask the model again.

Each conversation has a retention period set by your administrators. After it
ends, the conversation's content can be erased.

If Weft can't confirm that a question was received, it keeps the question in
the tab and offers **Recover original question**, which sends exactly the same
question again.
