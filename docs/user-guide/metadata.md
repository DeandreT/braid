---
id: metadata
title: Edit a mapping's name and description
order: 5
summary: Change a mapping's name and description, and resolve a conflict with someone else's edit.
---

Publishers and administrators can change a mapping's name and description. This
changes only the catalog details. The mapping file, its revisions and their
reports stay exactly as they were.

## Edit the details

1. Open the mapping and choose **Edit mapping metadata**.
2. Change the **Name** or the **Description**.
3. Choose **Save metadata**.

When braid confirms the change, it says **Metadata save confirmed**.

Revision pages show the mapping's *current* name and description, even for old
revisions.

## If someone else changed it first

braid saves your change only if nobody else changed the details since you opened
the editor. Otherwise it keeps your draft and asks you to
**Review before another save**:

1. Choose **Review latest**. braid shows three versions side by side: **Base**
   (what you started from), **Current** (what is saved now) and **Mine** (your
   draft).
2. Choose **Keep mine** to keep your draft, **Edit mine** to change it further,
   or **Use current** to drop your draft and keep the saved details.
3. If you kept your draft, choose **Save metadata** again.

Reviewing never saves anything by itself.

## If braid can't confirm a save

If the reply to a save is lost, braid says **Save outcome is unknown**. The save
may have been applied. Choose **Check current metadata** to read the saved
details and compare them with what you tried to save.

Your draft lives only in this tab. Closing or reloading the tab loses it, and a
save that was already sent can't be undone by closing the page.
