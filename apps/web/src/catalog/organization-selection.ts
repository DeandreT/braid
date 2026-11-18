import { Injectable, signal } from "@angular/core";
import { organizationLink } from "./organization-links";

/**
 * The tab's organization selection: tab memory plus the authoritative URL, never a cookie,
 * browser storage or server preference, so separate tabs work in separate organizations.
 * `null` is an unqualified, root-only route.
 */
@Injectable({ providedIn: "root" })
export class OrganizationSelection {
  private readonly selected = signal<string | null>(null);
  private readonly root = signal<string | null>(null);
  private readonly revision = signal(0);
  readonly organizationId = this.selected.asReadonly();
  /** The configured root organization once a root context read has confirmed it. */
  readonly rootOrganizationId = this.root.asReadonly();
  /** Advances synchronously on every accepted change of organization. */
  readonly generation = this.revision.asReadonly();

  /** Returns whether the selection changed. */
  select(organizationId: string | null): boolean {
    const previous = this.selected();
    if (organizationId === previous) return false;
    this.selected.set(organizationId);
    // A confirmed root's unqualified and qualified routes are one organization, not a switch.
    const root = this.root();
    if (root !== null && (organizationId ?? root) === (previous ?? root))
      return false;
    this.revision.update((value) => value + 1);
    return true;
  }

  observeRoot(organizationId: string | null): void {
    this.root.set(organizationId);
  }

  link(path: string): string {
    return organizationLink(path, this.selected(), this.root());
  }
}
