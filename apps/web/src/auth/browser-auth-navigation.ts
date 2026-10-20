import { DOCUMENT } from "@angular/common";
import { Injectable, inject } from "@angular/core";
import {
  BrowserSessionError,
  isAuthorizationUrl,
} from "./browser-session-types";

/** One fixed-provider top-level navigation; no caller return target or retained link. */
@Injectable({ providedIn: "root" })
export class BrowserAuthNavigation {
  private readonly document = inject(DOCUMENT);

  go(authorizationUrl: string): void {
    if (!this.document.defaultView || !isAuthorizationUrl(authorizationUrl))
      throw new BrowserSessionError();
    try {
      const link = this.document.createElement("a");
      link.href = authorizationUrl;
      link.target = "_self";
      link.rel = "noreferrer noopener";
      link.referrerPolicy = "no-referrer";
      link.click();
    } catch {
      throw new BrowserSessionError();
    }
  }
}
