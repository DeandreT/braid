---
id: builds
title: Builds, approvals and the mapping endpoint
order: 11
summary: Approve and prepare a build of a tested revision, and how a build reaches the mapping endpoint.
---

A build turns one revision into a compiled file that other systems can run.
Preparing a build doesn't put it in use: that is a separate step, described in
[The mapping endpoint](#the-mapping-endpoint).

Publishers and administrators can approve and prepare builds. Administrators
also manage build environments. Builds may be turned off in your workspace.

## Before you start

A build needs:

- a **build environment**: the runtime the compiled mapping will run in,
  registered by an administrator;
- a **passing regression run** of the same revision: a finished run whose
  result matches its expected output, made with the mapping engine version the
  server uses now. See [Regression tests](regression-tests.md);
- the revision's **current explanation**, saved and successful with
  **complete** [coverage](reports.md#coverage). If it hasn't been generated,
  [generate it](reports.md#generate-a-report) first;
- a mapping that reads and writes JSON documents, with one primary input and one
  primary output.

If any of these is missing, Weft refuses the approval or the build.

## Prepare a build

1. In the workbench, open the **Prepare** view and choose
   **Load preparation controls**.
2. Under **Environment version**, choose an active environment.
3. Under **Required test result**, choose **Load saved regression runs**, then
   **Verify this evidence** next to a finished run. Weft checks that its result
   matches the saved expectation.
4. Choose **Approve preparation**. This records your approval of this
   revision, environment and test result.
5. Under **Your approvals**, open the approval and choose
   **Prepare approved version**.

Weft queues the build and shows its progress. Under
**Your preparation history**, open a build to see its state:

- **Preparation complete**: the compiled file was made and saved.
- **Preparation failed** or **Preparation attempts exhausted**: the build
  didn't finish. The reason is shown.

Choose **Cancel preparation** to stop a build that hasn't finished.
**Withdraw your approval** stops an approval from being used for new builds.
It also stops a release made from that approval: the mapping endpoint checks
the approval on every call, so agree it with your operators before withdrawing
an approval that is in use.

Each build shows the size and fingerprint of its compiled file. Compiled files
can't be downloaded from the browser.

## Build environments

Administrators open **Environment administration** to register an environment
for the runtime the server offers. **Register new environment** adds one;
**Register next version** replaces the selected environment with a new version.
Older versions are kept but become inactive, and new approvals need an active
version. A release built for the older version stops answering calls, because
the mapping endpoint checks the environment on every call, so coordinate a new
version with the people who run the endpoint.

## The mapping endpoint

The mapping endpoint lets other systems send documents to a mapping and receive
its output, without using the browser. Putting a build in use happens outside
the browser, through your Weft operators:

1. They **qualify** a completed build: Weft runs the build's saved regression
   test against the compiled file and records the result.
2. They register a **release target** for the mapping and environment, and
   **activate** a qualified build as its release. Only one release is active at
   a time, and an earlier release can be reactivated to roll back.
3. A project administrator grants each calling system permission to use the
   target.

The endpoint always runs the active release. The workbench's **In use** view
can't show which release is active, so ask your operators which revision is in
use.
