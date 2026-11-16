---
id: creation
title: Create a mapping from specifications
order: 18
summary: Capture specifications, review a new mapping proposal, try samples and explicitly save the exact plan.
---

Use this workflow to turn specification documents into a proposed new mapping,
review it, try samples and save it.

## Check availability and access

Open a project. **Your new mapping proposals** appears when the server advertises
mapping creation for that project. The proposal library can refresh availability
and access. If a required feature or worker is unavailable, ask the operator
to check its setup.

Reading project metadata does not grant permission to read source content.
Sharing specification evidence with a model requires a sample-data-capable
model profile and your current project `run.payload.read` grant. See
[Sample data policy](chat.md#sample-data-policy) before asking chat to use it.

## Capture specification sources

1. On the project page, choose **Open specification intake**.
2. Choose **Start new bundle**, then **Create this bundle** when offered.
3. Bookmark the bundle URL so you can reopen its inventory.
4. Choose a **Specification file** and an **Extraction format**, then
   **Upload selected file**. Alternatively, paste text and choose **Save pasted text**.
5. Inspect **Source inventory** and choose **Read extraction** to check the
   evidence and its source locations.

The format selector and byte limits come from this server. It can offer plain
text, JSON and OpenAPI JSON. Word DOCX, Excel XLSX and PDF appear only when their
separately configured workers are admitted. Unknown or unavailable capability
responses stop new intake; **Refresh inventory and access** checks again without
turning an unreadable capability response into success.

Original retention and extraction are different results. An original may be
retained even if extraction fails. Word extraction covers only part of the
document; Excel shows stored cells and literal formulas, not recalculated results.
PDF offers page text, not guaranteed reading order or coverage. OCR is unavailable;
scanned pages can need OCR, and encrypted PDFs are refused. Partial, unsupported,
malformed and limit-exceeded results are not complete specifications. Read the
diagnostic and correct missing requirements before relying on a proposal.

URLs in sources remain text; braid does not fetch them. A specification's file
format is also not the mapping's input or output format.

## Request and review a proposal

Ask [workspace chat](chat.md) to create a new mapping from the project's retained
specification sources. Identify the project, bundle and relevant source versions,
the desired input and output formats, and the rules that must be preserved.
Chat can discover authorized source selections and your retained proposals.

New mapping plans can use JSON, XML, flat CSV/TSV, X12 and EDIFACT on either side.
They support source-field copying and fixed values, not arbitrary calculations,
lookups, sorting or filtering. Be explicit about XML structure, tabular dialect
and EDI envelope values; unsupported rules need clarification rather than invented defaults.

Open the proposal citation in the answer, or choose **Your new mapping proposals**
on the project page. On **Review new mapping**, inspect
**Source evidence**, **Open questions**, assumptions and **Exact mapping plan**.
The proposal fingerprint identifies this exact review.

## Try samples and give feedback

Under **Test a sample**, paste input or choose a sample file, at most 1 MiB, then
choose **Run sample**. Compare the displayed output with the expected business
result. This sends the input for execution against the exact proposal, not
automatically to the chat model. Empty successful output is different from failed
execution. Samples and output stay on the review page rather than being carried
into a feedback question.

Choose **Give feedback in chat**, then **Prepare feedback question**. Review and
edit the unsent question before sending it. The handoff carries the exact
proposal identity, not its samples. Reading retained proposal content for the
model still requires current source-content and payload-sharing permission.
For a correction or an unanswered critical question, request a new proposal.
Existing proposals and approvals do not change; every replacement needs its own
review, samples as appropriate, and explicit approval.

## Approve and save the exact mapping

Resolve critical open questions through a new proposal. Accept each critical
assumption only if it is correct, then check **I've reviewed this exact mapping
plan and its source evidence.** Choose **Approve and save this mapping**.

Wait for a confirmed **Committed** save status and use **Open saved revision**.
An **Approval retained** message alone does not confirm a saved mapping. Continue
with the [workbench](workbench.md), [previews](previews.md) and
[regression tests](regression-tests.md) for the saved revision.

## Recover an uncertain save

A missing or unverified reply can mean the original save committed. Reopen the
same proposal with its fingerprint and choose **Check save status**. This checks
and recovers the original operation. Use it before choosing another save action.

If recovery confirms a committed receipt, open that saved revision. If it
reconciles a retained approval with no reservation, or a **Reserved** operation
that needs its source, the page may offer **Retry saving this exact mapping**.
Review it and accept its critical assumptions again before that deliberate
retry. Do not start a replacement save just because the first response was lost.

In-tab samples, unsent input and retry details can be lost when you reload or
leave; navigation warns about unsent work. A sent save may still have committed.
Keep the exact proposal link and recover its durable status under your current
access. Identity or access changes clear private review content. Other upload
and edit recovery actions are described in [Unconfirmed results](unconfirmed-results.md).
