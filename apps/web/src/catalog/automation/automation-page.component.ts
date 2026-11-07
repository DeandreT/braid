import { DOCUMENT } from "@angular/common";
import {
  Component,
  effect,
  inject,
  signal,
  untracked,
  viewChild,
} from "@angular/core";
import { RouterLink } from "@angular/router";
import { of, type Observable } from "rxjs";
import {
  CatalogRoutePage,
  CatalogStatusComponent,
} from "../catalog-shell.component";
import {
  catalogOwner,
  type CatalogContext,
  type CatalogProject,
  type CatalogProjectScope,
} from "../catalog-types";
import { CatalogContextStore } from "../catalog-context";
import { BrowserSessionState } from "../../auth/browser-session-state";
import { AutomationPanelComponent } from "./automation-panel.component";

@Component({
  selector: "weft-automation-page",
  imports: [RouterLink, CatalogStatusComponent, AutomationPanelComponent],
  template: ` <section class="catalog-page">
    <h1>Automation and notifications</h1>
    @if (data(); as project) {
      <p>
        <a [routerLink]="routes.project(project.scope)">{{ project.name }}</a>
      </p>
    }
    @if (heldProject(); as project) {
      @if (heldContext(); as admission) {
        <div [hidden]="!data()" [attr.inert]="!data() ? '' : null">
          <weft-automation-panel
            [scope]="project.scope"
            [context]="admission"
            [admin]="project.access.role === 'administrator'"
          />
        </div>
      }
    }
    @if (!data()) {
      <weft-catalog-status
        [kind]="kind()"
        [firstPage]="firstPage()"
        (retry)="retry()"
      />
    }
  </section>`,
})
export class AutomationPageComponent extends CatalogRoutePage<
  CatalogProject,
  CatalogProjectScope
> {
  readonly heldProject = signal<CatalogProject | null>(null);
  readonly heldContext = signal<CatalogContext | null>(null);
  readonly panel = viewChild(AutomationPanelComponent);
  private readonly ownerContexts = inject(CatalogContextStore);
  private readonly browserSessions = inject(BrowserSessionState);
  private readonly pageDocument = inject(DOCUMENT);
  private retainedOwner: string | null = null;
  constructor() {
    super();
    this.bind("project", (scope) => this.api.project(scope));
    effect(() => {
      const current = this.ownerContexts.context();
      const ended =
        this.browserSessions.state().kind === "disabled" ||
        this.browserSessions.action().kind === "logoutAcknowledged";
      const project = this.data(),
        context = this.context();
      untracked(() => {
        if (
          ended ||
          (current &&
            this.retainedOwner !== null &&
            catalogOwner(current) !== this.retainedOwner)
        ) {
          this.heldProject.set(null);
          this.heldContext.set(null);
          this.retainedOwner = null;
        }
        if (!ended && project && context) {
          this.retainedOwner = catalogOwner(context);
          this.heldProject.set(project);
          this.heldContext.set(context);
        }
      });
    });
  }
  requestLeave(): Observable<boolean> {
    return of(
      !this.panel()?.hasUnsent() ||
        (this.pageDocument.defaultView?.confirm(
          "Leave automation settings? Unsaved edits and unconfirmed requests will be lost.",
        ) ??
          false),
    );
  }
}
