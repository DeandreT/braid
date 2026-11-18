import { Component, DestroyRef, inject, input, signal } from "@angular/core";
import { Router } from "@angular/router";
import type { Subscription } from "rxjs";
import { CatalogApi, type OrganizationPage } from "./catalog-api";
import { catalogRoutes } from "./catalog-types";

type Organization = OrganizationPage["organizations"][number];
type Choices =
  | { kind: "loading" | "failed" }
  | {
      kind: "ready";
      organizations: readonly Organization[];
      next: string | null;
    };

/**
 * Lists the bounded discovered admissions on demand and requires an explicit choice. Choosing
 * opens the destination's home; nothing from the current organization is carried across.
 */
@Component({
  selector: "braid-organization-switcher",
  template: `
    <button
      id="organization-switcher"
      class="organization-switcher-trigger"
      type="button"
      title="Switch organization"
      [attr.aria-controls]="open() ? 'organization-switcher-menu' : null"
      [attr.aria-expanded]="open()"
      (click)="toggle()"
    >
      <span class="workspace-organization">
        @if (name(); as organization) {
          <strong>{{ organization }}</strong>
        }
        <span>{{ label() }}</span>
      </span>
      <span class="header-sr-only">, switch organization</span>
      <svg viewBox="0 0 24 24" aria-hidden="true">
        <path d="m7 10 5 5 5-5" />
      </svg>
    </button>
    @if (open()) {
      <section
        id="organization-switcher-menu"
        class="organization-switcher-menu"
        aria-label="Organizations"
        (keydown.escape)="close()"
      >
        @switch (choices().kind) {
          @case ("loading") {
            <p role="status">Loading organizations…</p>
          }
          @case ("failed") {
            <p role="alert">Organizations could not be loaded.</p>
            <button type="button" (click)="discover()">Retry</button>
          }
        }
        @if (choices(); as state) {
          @if (state.kind === "ready") {
            @if (state.organizations.length === 0) {
              <p role="status">You are not a member of an organization.</p>
            } @else {
              <ul>
                @for (
                  organization of state.organizations;
                  track organization.organizationId
                ) {
                  <li>
                    <button
                      type="button"
                      [attr.aria-current]="
                        organization.organizationId === current()
                          ? 'true'
                          : null
                      "
                      (click)="choose(organization.organizationId)"
                    >
                      {{ organization.name }}
                    </button>
                  </li>
                }
              </ul>
            }
            @if (state.next) {
              <button type="button" (click)="discover(state)">
                Show more organizations
              </button>
            }
          }
        }
      </section>
    }
  `,
  styles: `
    :host {
      position: relative;
      display: block;
      min-width: 0;
    }
    .organization-switcher-trigger {
      display: flex;
      align-items: center;
      gap: 6px;
      max-width: 100%;
      padding: 2px 6px;
      border: 1px solid transparent;
      border-radius: 4px;
      background: transparent;
      color: #213b34;
      text-align: left;
      cursor: pointer;
    }
    .organization-switcher-trigger:hover,
    .organization-switcher-trigger[aria-expanded="true"] {
      background: var(--wb-selection-tint);
      border-color: #d7e2d1;
    }
    .organization-switcher-trigger svg {
      flex: none;
      width: 14px;
      height: 14px;
      fill: none;
      stroke: currentColor;
      stroke-width: 1.6;
      stroke-linecap: round;
      stroke-linejoin: round;
    }
    .organization-switcher-menu {
      position: absolute;
      top: calc(100% + 8px);
      left: 0;
      z-index: 30;
      width: min(320px, calc(100vw - 24px));
      max-height: min(420px, calc(100dvh - 100px));
      overflow-y: auto;
      padding: 8px;
      border: 1px solid #d7e2d1;
      border-radius: 8px;
      background: #fff;
      box-shadow: 0 6px 18px #18352b22;
      font-size: 0.8125rem;
    }
    ul {
      margin: 0 0 8px;
      padding: 0;
      list-style: none;
    }
    li button {
      width: 100%;
      min-height: 36px;
      text-align: left;
      overflow-wrap: anywhere;
    }
    li button[aria-current="true"] {
      font-weight: 700;
    }
  `,
})
export class OrganizationSwitcherComponent {
  /** The organization the tab presents now, if any. */
  readonly current = input<string | null>(null);
  /** The presented organization's name; `null` while it cannot be shown. */
  readonly name = input<string | null>(null);
  readonly label = input("Workspace");
  readonly open = signal(false);
  readonly choices = signal<Choices>({ kind: "loading" });
  private readonly api = inject(CatalogApi);
  private readonly router = inject(Router);
  private read: Subscription | null = null;

  constructor() {
    inject(DestroyRef).onDestroy(() => this.read?.unsubscribe());
  }

  toggle(): void {
    if (this.open()) this.close();
    else {
      this.open.set(true);
      this.discover();
    }
  }

  close(): void {
    this.read?.unsubscribe();
    this.open.set(false);
  }

  /** Every opening reads fresh admissions; revocation may remove entries between pages. */
  discover(earlier?: Extract<Choices, { kind: "ready" }>): void {
    this.read?.unsubscribe();
    if (!earlier) this.choices.set({ kind: "loading" });
    this.read = this.api.organizations(earlier?.next ?? null).subscribe({
      next: (page) =>
        this.choices.set({
          kind: "ready",
          organizations: [
            ...(earlier?.organizations ?? []),
            ...page.organizations,
          ],
          next: page.nextCursor ?? null,
        }),
      error: () => this.choices.set({ kind: "failed" }),
    });
  }

  choose(organizationId: string): void {
    this.close();
    if (organizationId !== this.current())
      void this.router.navigateByUrl(catalogRoutes.projects(organizationId));
  }
}
