---
id: overview
title: Getting started
order: 1
summary: What Weft does, the words it uses, and where to begin.
---

Weft helps you understand, test and prepare data mappings. A mapping describes
how incoming documents become result documents. Weft keeps every version of a
mapping, explains what it does, checks it for problems, and lets you try it on
sample data before anyone relies on it.

Weft currently works with mappings saved in Ferrule's native JSON format.

## Words Weft uses

- **Organization**: your company's Weft workspace. The workspace decides which
  organization you work in; you don't choose it.
- **Project**: a group of related mappings. Your role in a project decides what
  you can do with its mappings.
- **Mapping**: one named mapping, such as an order or invoice translation. It
  has a name, a description and a history of revisions.
- **Revision**: one uploaded file of a mapping. The workbench calls it a
  *version*. A revision never changes after it is saved: uploading a changed
  file adds a new revision and keeps the old ones.
- **Head**: the newest revision of a mapping.
- **Reports**: the *explanation* (how each result field is filled) and the
  *validation* (problems found in the file) that Weft saves for one exact
  revision.

## Where to start

1. [Sign in](sign-in.md) to your workspace.
2. Open the **Catalog** and choose a project. See [The catalog](catalog.md).
3. Open a mapping, then one of its revisions, to see it in the
   [workbench](workbench.md).
4. Read its [reports](reports.md), [try a sample](previews.md) or save
   [regression tests](regression-tests.md).

## Finding help

Choose **Help** in the header at any time. Each help page has its own address,
so you can bookmark it or share it with a colleague.

To work from a terminal, see [Using the command line](cli.md).

To check new versions automatically and choose activity alerts, see
[Automation and notifications](automation.md).

Administrators can manage people and permissions in [Administration](administration.md).
