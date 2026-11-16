---
id: catalog
title: The catalog
order: 3
summary: Browse projects, mappings and revisions, understand project roles, and create a project.
---

The catalog lists the projects, mappings and revisions you can see. Choose
**Catalog** in the workspace navigation to open it. Medium-width screens show
navigation icons with named links; hover over an icon to see its name. On a
phone, open **Menu** first.

## Projects

The **Projects** page lists the projects in your organization that you are a
member of. Projects you don't belong to don't appear. Each project shows your
role in it.

Choose a project to see its description, your role and its mappings. Open
**Project details** for its ID, creation and update times.

Lists show up to ten entries per page. Use **Next page** and **First page** to
move through longer lists. **Refresh catalog** reads the page again with your
current access.

## Mappings and revisions

Choose a mapping on a project page to open it. The mapping page shows:

- its current name and description;
- the **Current head when this view loaded**: the newest revision at the moment
  the page was read. **View current head** reads the mapping again and opens
  the newest revision;
- the **Revision history**, newest first, with each revision's file name, the
  time it was saved and its file status.

Each revision has a file status:

- **File accepted**: the file passed the mapping engine's checks when it was
  uploaded.
- **Saved with validation issues**: braid saved the file, but the mapping
  engine's checks found problems in it. The revision stays in the history and
  you can still open it and read its [validation](reports.md#validation).

Choose a revision to open it in the [workbench](workbench.md). A revision's
address always opens that exact revision, even after newer revisions are
uploaded, so you can share it safely.

## Projects and roles

Each project member has one role in that project. A role in one project gives
no access to any other project.

| Role          | What it allows                                                                                                                                              |
| ------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Viewer        | Browse the project's mappings and revisions, read and generate reports, compare revisions, use chat, and see the names of saved regression tests.           |
| Runner        | Everything a viewer can do, plus try samples, open saved regression tests and run them.                                                                     |
| Publisher     | Everything a runner can do, plus upload mappings and revisions, edit mapping names and descriptions, save regression tests, and approve and prepare builds. |
| Administrator | Everything a publisher can do, plus manage build environments.                                                                                              |

Project administrators manage members and roles through
[Administration](administration.md#project-access) when that feature is enabled.
Some features can also be turned off for your workspace. When
a feature is off, or your role doesn't allow an action, its controls are hidden
or shown as unavailable.

braid checks your current role again for every action. If your role changes
while a page is open, your next action uses the new role.

## Create a project

You can create projects only if your organization allows you to. Being an
administrator of an existing project doesn't give you this permission.

1. On the **Projects** page, choose **Create project**.
2. Enter a **Name** and, if you like, a **Description**.
3. Choose **Create project**.

When braid confirms the creation, you become the new project's administrator.
Choose **Open project** to go to it.

If braid can't confirm whether the project was created, it says **Creation
outcome is unknown**. Choose **Recover creation** to send the same request again;
it can't create a second project. See
[When a result isn't confirmed](unconfirmed-results.md).

To turn specifications into a new mapping rather than upload an existing file,
see [Create a mapping from specifications](creation.md). This requires the
workspace's creation capabilities and your current project permissions.
