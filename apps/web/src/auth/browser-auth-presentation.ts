import type { StatusTone } from "../workbench/presentation";
import type { BrowserAuthActionView } from "./browser-session-types";

/** UI treatment only; this never grants access or classifies a server response. */
export function sessionTone(state: string, action: string): StatusTone {
  if (action === "unconfirmed" || action === "revocationUnconfirmed")
    return "uncertain";
  if (
    action === "admitting" ||
    action === "pending" ||
    action === "redirecting"
  )
    return "running";
  if (action === "notAdmitted") return "attention";
  if (state === "checking") return "running";
  if (state === "authenticated") return "success";
  if (state === "authenticationRequired") return "attention";
  if (state === "anonymous" || state === "development") return "neutral";
  return "unavailable";
}

/** The progress or outcome of an explicit sign-in, sign-out or local forgetting action. */
export function sessionActionMessage(action: BrowserAuthActionView): string {
  switch (action.kind) {
    case "idle":
      return "";
    case "admitting":
      return "Checking the current session before your chosen action…";
    case "pending":
      return action.action === "logout"
        ? "Waiting for local sign-out acknowledgement…"
        : action.action === "forgetLocal"
          ? "Waiting for explicit local forgetting…"
          : "Requesting sign-in…";
    case "redirecting":
      return "Continuing to the server-approved sign-in page. Sign-in is not yet confirmed.";
    case "notAdmitted":
      return "The action was not sent because the current session could not be admitted unchanged. Check session, then choose again.";
    case "logoutAcknowledged":
      return "Local sign-out was acknowledged. This does not sign you out of Microsoft Entra. Check session to continue.";
    case "revocationUnconfirmed":
      return "Local forgetting was acknowledged. Revocation is unconfirmed: a server session may still exist. Check session to continue.";
    case "unconfirmed":
      return action.action === "logout"
        ? "Sign-out was not confirmed. The session may still be active. Nothing was retried; check session before choosing another action."
        : action.action === "forgetLocal"
          ? "Local forgetting was not confirmed or was not available. Revocation is unconfirmed. Nothing was retried; check session to continue."
          : "Sign-in could not be confirmed. Nothing was retried; check session before choosing again.";
  }
}
