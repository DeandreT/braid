---
id: sign-in
title: Sign in
order: 2
summary: Sign in with your Microsoft work account, sign out, and what to do when access is refused.
---

braid runs in one of two ways, chosen by whoever runs it:

- A **signed-in workspace** uses your Microsoft work account.
- A **Development workspace** runs on a developer's own computer. It has no
  sign-in: you are already identified, and the header shows
  **Development workspace**.

## Sign in

1. Open braid in your browser.
2. On the sign-in page, choose **Sign in with Microsoft**.
3. Sign in with your work account on the Microsoft page.
4. Microsoft returns you to the page you opened, or to the catalog. The account
   menu in the header shows **Signed in to braid** when you open it.

braid opens your organization for you. There is no organization to pick.
The account menu shows your name, when available, and your organization. If you
open the sign-in page while already signed in, braid opens the catalog instead.

Signing in leaves the page and reloads it. Anything you haven't saved, such as
a form you were filling in, is lost, so finish or save your work first. A change
you already sent may have been saved even if you didn't see the reply.

## Sign out

Open the account menu in the header and choose **Sign out**. This ends your braid
session in this browser. It doesn't sign you out of your Microsoft account or other
Microsoft services.

**Sign in again** repeats the Microsoft sign-in while you are signed in. It
replaces your current braid session with a new one.

## Light and dark appearance

The account menu also has **Appearance**: **Light**, **Dark** or **System**.
System, the default, follows your device's setting and changes with it. Your
choice is saved in this browser only.

## When something goes wrong

- **Sign-in could not be completed**: Microsoft signed you in, but braid didn't
  accept the account. Your account may not be set up for braid yet. Ask your braid
  administrator to add it, then use the return link and sign in again.
- **Sign-in is temporarily unavailable**: braid couldn't finish sign-in. Try
  again later.
- **Sign-in required**: your earlier session can no longer be used. Choose
  **Sign in**.
- **Sign-in status unavailable**: braid couldn't check your session, so it hides
  your workspace until it can. This doesn't mean you were signed out. Choose
  **Check session** to try again.

Signing in only proves who you are. What you can see and change depends on your
project roles, which braid checks again for every action. See
[Projects and roles](catalog.md#projects-and-roles).
