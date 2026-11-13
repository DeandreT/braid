---
id: uploads
title: Upload mappings and revisions
order: 4
summary: Add a new mapping from its native file, or upload a changed file as a new revision.
---

Publishers and administrators can add mappings to a project and upload new
revisions of existing mappings. braid stores the exact bytes of the file you
choose; it never rewrites or converts the file.

braid accepts mappings in Ferrule's native JSON format, up to 4 MiB per file. It
doesn't convert XML, MapForce files or project bundles, and it doesn't fetch
files that a mapping refers to.

## Upload a new mapping

1. Open the project and choose **Upload mapping**.
2. Enter the mapping's **Name** and, if you like, a **Description**.
3. Choose the **Native mapping file**.
4. Check the **Saved filename**. It is shown with the revision and defaults to
   the name of the file you chose; you can change it.
5. Choose **Upload mapping**.

One file creates one mapping and its first revision. When braid confirms the
upload, it says **Mapping saved**. Choose **View revision** to open it.

If the mapping engine finds problems in the file, braid still saves it and says
**Mapping saved with validation issues**. Open the revision and read its
[validation](reports.md#validation) to see what's wrong.

## Upload a new revision

1. Open the mapping and choose **Upload revision**.
2. Check **Reviewed head for this append**. This is the newest revision you
   are replacing.
3. Choose the **Native mapping file** and check the **Saved filename**.
4. Choose **Append revision**.

The mapping's name and description don't change. Older revisions, and the
reports saved for them, stay available.

If someone else uploads a revision while you are working, braid doesn't save
yours on top of theirs. It asks you to choose **Review current head** first, so
you can check their change before you upload again.

## While an upload is in progress

Keep the tab open until braid confirms the result. **Stop waiting** stops the
page from waiting, but the server may still finish the upload.

If braid can't confirm the result, it says **Upload outcome is unknown** or
**Append outcome is unknown**. Choose **Resend original upload** (or **Resend
original append**) to send exactly the same request again. braid recognises it
and never saves the file twice. Don't start a new upload with the same file
until you know what happened. See
[When a result isn't confirmed](unconfirmed-results.md).

Leaving the page, reloading it or closing the tab discards the file you
selected and any recovery information. braid asks before you leave a page with
unsent or unconfirmed work.
