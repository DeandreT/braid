import type { StatusTone } from "../workbench/presentation";

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
