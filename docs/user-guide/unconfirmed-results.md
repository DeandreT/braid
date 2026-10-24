---
id: unconfirmed-results
title: When a result isn't confirmed
order: 13
summary: What to do when Weft can't tell whether a change you sent was saved.
---

Sometimes Weft sends a change but never receives a clear reply, for example
when the network drops or the server is slow. The change may or may not have
been saved. Weft never guesses: it tells you the result isn't confirmed and
keeps your original request so you can check it.

You'll see messages such as **Upload outcome is unknown**,
**Creation outcome is unknown**, **Save outcome is unknown**, or
**Result not confirmed — check the previous request**.

## What to do

1. Keep the tab open. The original request lives only in this tab.
2. Use the check action Weft offers, such as **Check previous result**,
   **Check saved version** or **Check current metadata**, to read what the
   server has now.
3. If you want to try again, use the resend or recover action, such as
   **Resend original upload** or **Recover creation**. It sends exactly the same
   request. Weft recognises it, so it can't save the same change twice.

Don't start a new request for the same change until you know what happened to
the first one. A new request is treated as different, so it can create a
duplicate.

## Stopping and forgetting

**Stop waiting** only stops the page from waiting for a reply. The server may
still finish the request.

Actions such as **Abandon original upload** or **Forget recovery details**
discard the original request from this tab. They don't undo anything on the
server. After that, Weft can no longer tell you whether the earlier request was
saved, so check the catalog before you try again.

Reloading the page or closing the tab also discards the original request.
Leaving the page discards it too, except on the project creation page, which
keeps its request while the tab stays open. The upload, revision upload and
metadata pages ask before you leave with unsent or unconfirmed work. The
workbench doesn't ask: leaving a revision discards an unconfirmed regression
test, build or chat request, so check it first. Help links on these pages open
in a new tab, so reading the guide doesn't leave your work.

## While Weft checks your session

If Weft can't confirm your session for a moment, it hides your workspace until
it can. Pages that hold unsent work, such as uploads, regression tests and
chat, keep it in the tab. When Weft confirms the same person again, the work
comes back as you left it, and nothing is sent until you choose to. If a
different person is confirmed, the work is discarded.

Signing in reloads the page, so work held in the tab doesn't survive
[signing in again](sign-in.md).
