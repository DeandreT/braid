import { DOCUMENT } from "@angular/common";
import { Injectable, effect, inject, signal } from "@angular/core";

export type ThemeChoice = "light" | "dark" | "system";
/** public/theme.js reads the same key before the first paint. */
export const themeStorageKey = "braid.theme.v1";

/** The Light, Dark or System appearance, saved in this browser only. */
@Injectable({ providedIn: "root" })
export class ThemePreference {
  private readonly document = inject(DOCUMENT);
  private readonly storage = (() => {
    try {
      return this.document.defaultView?.localStorage ?? null;
    } catch {
      return null;
    }
  })();
  private readonly query =
    this.document.defaultView?.matchMedia?.("(prefers-color-scheme: dark)") ??
    null;
  private readonly systemDark = signal(this.query?.matches ?? false);
  readonly choice = signal<ThemeChoice>(this.read());

  constructor() {
    this.query?.addEventListener("change", (event) =>
      this.systemDark.set(event.matches),
    );
    effect(() => {
      const choice = this.choice();
      const dark =
        choice === "dark" || (choice === "system" && this.systemDark());
      this.document.documentElement.setAttribute(
        "data-theme",
        dark ? "dark" : "light",
      );
    });
  }

  set(choice: ThemeChoice): void {
    this.choice.set(choice);
    try {
      if (choice === "system") this.storage?.removeItem(themeStorageKey);
      else this.storage?.setItem(themeStorageKey, choice);
    } catch {
      // Storage can be unavailable; the choice still applies to this page.
    }
  }

  private read(): ThemeChoice {
    try {
      const value = this.storage?.getItem(themeStorageKey);
      return value === "light" || value === "dark" ? value : "system";
    } catch {
      return "system";
    }
  }
}
