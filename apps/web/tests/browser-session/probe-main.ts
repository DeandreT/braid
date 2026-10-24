// NONSHIPPED separate entry. Uses actual routed consumers and normal public APIs only.
import { APP_BASE_HREF } from "@angular/common";
import { provideHttpClient } from "@angular/common/http";
import { Component, DestroyRef, inject, signal } from "@angular/core";
import { bootstrapApplication } from "@angular/platform-browser";
import {
  ChildrenOutletContexts,
  PRIMARY_OUTLET,
  provideRouter,
  withInMemoryScrolling,
} from "@angular/router";
import { type Subscription } from "rxjs";
import { AppComponent } from "../../src/app.component";
import { routes } from "../../src/app.routes";
import { BrowserSessionState } from "../../src/auth/browser-session-state";
import { CatalogContextStore } from "../../src/catalog/catalog-context";
import type { CatalogContext } from "../../src/catalog/catalog-types";
import { CatalogRoutePage } from "../../src/catalog/catalog-shell.component";
import { CreateProjectPageComponent } from "../../src/catalog/projects/create-project-page.component";
import { EditMappingPageComponent } from "../../src/catalog/metadata/edit-mapping-page.component";
import { UploadMappingPageComponent } from "../../src/catalog/uploads/upload-mapping-page.component";
import { AppendRevisionPageComponent } from "../../src/catalog/uploads/append-revision-page.component";
import { RevisionPageComponent } from "../../src/catalog/revision-page.component";

@Component({
  selector: "weft119-probe",
  imports: [AppComponent],
  template: `<aside aria-label="Nonshipped session test controls">
      <button id="probe-observe" (click)="observe()">Observe</button>
      <button id="probe-admit" (click)="admit()">Admit context only</button>
      <button id="probe-invalidate" (click)="invalidate()">Invalidate</button>
      <button id="probe-arm" (click)="arm()">Arm current consumer</button>
      <button id="probe-sample" (click)="requestSample()">
        Sample categories
      </button>
      <button id="probe-foreign-actions" (click)="attemptForeignActions()">
        Attempt current feature actions
      </button>
      <button id="probe-cancel-first" (click)="cancel(0)">
        Cancel first waiter
      </button>
      <button id="probe-cancel-all" (click)="cancelAll()">
        Cancel all waiters
      </button>
      <output id="probe-result">{{ result() }}</output>
    </aside>
    @if (mounted()) {
      <weft-root />
    }`,
})
export class BrowserSessionPublicProbe {
  private readonly sessions = inject(BrowserSessionState);
  private readonly contexts = inject(CatalogContextStore);
  private readonly outlets = inject(ChildrenOutletContexts);
  private readonly subscriptions: Subscription[] = [];
  private context: CatalogContext | null = null;
  private component: unknown = null;
  private consumer: unknown = null;
  private beforeGeneration = 0;
  private notices = 0;
  private cleared = true;
  private complete = 0;
  private failed = 0;
  private requestedSamples = 0;
  private foreignActions = 0;
  private notice: Subscription | null = null;
  readonly mounted = signal(location.pathname !== "/__weft119_probe/__state");
  readonly result = signal("{}");
  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.cancelAll();
      this.notice?.unsubscribe();
    });
  }
  private active(): unknown {
    let contexts = this.outlets,
      active: unknown = null;
    for (let depth = 0; depth < 8; depth++) {
      const context = contexts.getContext(PRIMARY_OUTLET);
      if (!context?.outlet?.isActivated) break;
      active = context.outlet.component;
      contexts = context.children;
    }
    return active;
  }
  private owner(component: unknown): unknown {
    if (
      component instanceof CreateProjectPageComponent ||
      component instanceof EditMappingPageComponent ||
      component instanceof UploadMappingPageComponent ||
      component instanceof AppendRevisionPageComponent
    )
      return component.state;
    if (component instanceof RevisionPageComponent) return component.reports;
    if (component instanceof CatalogRoutePage) return component.read;
    return null;
  }
  private empty(component: unknown): boolean {
    if (component instanceof CreateProjectPageComponent)
      return (
        component.state.context() === null &&
        !component.state.canCreate() &&
        !component.state.canRecover() &&
        component.state.confirmation() === null
      );
    if (component instanceof EditMappingPageComponent)
      return (
        component.state.context() === null &&
        component.state.mapping() === null &&
        component.state.metadata() === null &&
        component.state.saved() === null &&
        !component.state.canSave()
      );
    if (component instanceof UploadMappingPageComponent)
      return (
        component.state.context() === null &&
        component.state.project() === null &&
        component.state.file() === null &&
        component.state.acknowledgement() === null &&
        !component.state.canUpload() &&
        !component.state.canResend()
      );
    if (component instanceof AppendRevisionPageComponent) {
      const view = component.state.view();
      return (
        view.mapping === null &&
        view.file === null &&
        view.acknowledgement === null &&
        !view.canAppend &&
        !view.canResend
      );
    }
    if (component instanceof RevisionPageComponent)
      return (
        component.data() === null &&
        component.reports.projectionDocument() === null &&
        component.reports.projection().response === null &&
        component.reports.validation().response === null
      );
    if (component instanceof CatalogRoutePage) return component.data() === null;
    return false;
  }
  arm(): void {
    this.notice?.unsubscribe();
    this.notices = 0;
    this.cleared = true;
    this.context = this.contexts.context();
    this.component = this.active();
    this.consumer = this.owner(this.component);
    this.beforeGeneration = this.sessions.generation();
    // Subscribe AFTER actual mounted consumers, including component-scoped metadata/report owners.
    this.notice = this.contexts.invalidated.subscribe(() => {
      ++this.notices;
      this.cleared &&=
        this.context !== null &&
        this.consumer !== null &&
        this.active() === this.component &&
        this.owner(this.component) === this.consumer &&
        this.sessions.generation() > this.beforeGeneration &&
        !this.sessions.visible() &&
        !this.sessions.accepts(this.context) &&
        !this.sessions.canDispatch(this.context) &&
        this.contexts.context() === null &&
        this.empty(this.component);
      this.sample();
    });
    this.sample();
  }
  observe(): void {
    this.subscriptions.push(
      this.sessions.observe().subscribe({
        complete: () => {
          ++this.complete;
          this.sample();
        },
        error: () => {
          ++this.failed;
          this.sample();
        },
      }),
    );
    this.sample();
  }
  admit(): void {
    this.subscriptions.push(
      this.contexts.refresh().subscribe({
        complete: () => {
          ++this.complete;
          this.sample();
        },
        error: () => {
          ++this.failed;
          this.sample();
        },
      }),
    );
    this.sample();
  }
  invalidate(): void {
    this.sessions.invalidate();
    this.sample();
  }
  cancel(index: number): void {
    this.subscriptions[index]?.unsubscribe();
    this.sample();
  }
  cancelAll(): void {
    for (const subscription of this.subscriptions) subscription.unsubscribe();
    this.sample();
  }
  attemptForeignActions(): void {
    const component = this.active();
    let actions: Array<() => void> = [];
    const file = () =>
      new File(
        ['{"owned":"foreign replacement"}'],
        "owned-foreign-replacement.native.json",
        { type: "application/json" },
      );
    if (component instanceof CreateProjectPageComponent)
      actions = [
        () => component.state.recover(),
        () =>
          component.state.setDraft(
            "Owned foreign overwrite",
            "Owned foreign description",
          ),
        () => component.state.create(),
      ];
    else if (component instanceof EditMappingPageComponent)
      actions = [
        () => component.state.reviewLatest(),
        () => component.state.keepMine(),
        () =>
          component.state.setDraft(
            "Owned foreign overwrite",
            "Owned foreign description",
          ),
        () => component.state.save(),
      ];
    else if (component instanceof UploadMappingPageComponent)
      actions = [
        () => component.state.resendOriginal(),
        () => component.state.resumeUpload(),
        () =>
          component.state.setDraft(
            "Owned foreign overwrite",
            "Owned foreign description",
            "owned-foreign-replacement.native.json",
          ),
        () => component.state.selectFile(file()),
        () => component.state.upload(),
      ];
    else if (component instanceof AppendRevisionPageComponent)
      actions = [
        () => component.state.resendOriginal(),
        () => component.state.resumeAppend(),
        () => component.state.reviewCurrentHead(),
        () =>
          component.state.setFilename("owned-foreign-replacement.native.json"),
        () => component.state.selectFile(file()),
        () => component.state.append(),
      ];
    // Each normal public action is attempted even if another refuses by throwing. Never emit its operands/error.
    for (const action of actions) {
      ++this.foreignActions;
      try {
        action();
      } catch {}
    }
    this.sample();
  }
  private featureFacts(component: unknown): Record<string, boolean> {
    if (component instanceof CreateProjectPageComponent)
      return {
        newDispatchAllowed: component.state.canCreate(),
        recoveryDenied:
          !component.state.canRecover() &&
          !component.state.canCreate() &&
          !component.state.canEdit(),
        privateProjectionHidden:
          component.state.attemptSummary() === null &&
          component.state.confirmation() === null &&
          !component.state.retainedConfirmation() &&
          component.state.draft().name.length === 0 &&
          component.state.draft().description.length === 0,
      };
    if (component instanceof EditMappingPageComponent)
      return {
        latestSaveChanged: component.state.saved()?.changed === true,
        newDispatchAllowed: component.state.canSave(),
        recoveryDenied:
          !component.state.canSave() && !component.state.canEdit(),
        privateProjectionHidden:
          component.state.comparison() === null &&
          component.state.saved() === null &&
          component.state.draft().name.length === 0 &&
          component.state.draft().description.length === 0,
      };
    if (component instanceof UploadMappingPageComponent)
      return {
        newDispatchAllowed: component.state.canUpload(),
        recoveryDenied:
          !component.state.canUpload() &&
          !component.state.canResend() &&
          !component.state.canResume() &&
          !component.state.canCheckStatus() &&
          !component.state.canEdit() &&
          !component.state.canStartAnother() &&
          !component.state.canAbandon(),
        privateProjectionHidden:
          component.state.file() === null &&
          component.state.acknowledgement() === null &&
          component.state.operationStatus() === null &&
          component.state.response() === null &&
          component.state.draft().name.length === 0 &&
          component.state.draft().description.length === 0 &&
          component.state.draft().originalFilename.length === 0,
        preparingFile: component.state.preparingFile(),
        fileAbsent: component.state.file() === null,
        filenameAbsent: component.state.draft().originalFilename.length === 0,
        currentFile:
          component.state.file()?.name === "owned-current-file.native.json",
        currentFilename:
          component.state.draft().originalFilename ===
          "owned-current-file.native.json",
      };
    if (component instanceof AppendRevisionPageComponent) {
      const view = component.state.view();
      return {
        newDispatchAllowed: view.canAppend,
        recoveryDenied:
          !view.canAppend &&
          !view.canResend &&
          !view.canResume &&
          !view.canCheckStatus &&
          !view.canEdit &&
          !view.canStartNew &&
          !view.canAbandon,
        privateProjectionHidden:
          view.file === null &&
          view.reviewedHead === null &&
          view.acknowledgement === null &&
          view.operationStatus === null &&
          view.response === null &&
          view.draft.originalFilename.length === 0,
        preparingFile: view.preparingFile,
        fileAbsent: view.file === null,
        filenameAbsent: view.draft.originalFilename.length === 0,
        currentFile: view.file?.name === "owned-current-file.native.json",
        currentFilename:
          view.draft.originalFilename === "owned-current-file.native.json",
        advancedHeadSeen:
          view.mapping !== null &&
          view.mapping.head.sequence === "9007199254740995" &&
          view.mapping.head.scope.revisionId ===
            "40000000-0000-4000-8000-000000000088",
      };
    }
    return {
      newDispatchAllowed: false,
      recoveryDenied: true,
      privateProjectionHidden: true,
      preparingFile: false,
      fileAbsent: true,
      filenameAbsent: true,
      currentFile: false,
      currentFilename: false,
    };
  }
  private summary(component: unknown): Record<string, boolean> {
    if (component instanceof CreateProjectPageComponent)
      return {
        busy: component.state.busy(),
        admitted: component.state.context() !== null,
        retained: component.state.hasRetainedRequest(),
        unknown: component.state.attemptSummary()?.uncertain === true,
        canRecover: component.state.canRecover(),
        acknowledged: component.state.confirmation() !== null,
        needsReview: false,
      };
    if (component instanceof EditMappingPageComponent)
      return {
        busy: component.state.busy(),
        admitted: component.state.context() !== null,
        retained: component.state.hasRetainedWork(),
        unknown:
          component.state.outcomeUnknown() || component.state.priorUnknown(),
        canRecover: component.state.canSave(),
        acknowledged: component.state.saved() !== null,
        needsReview: component.state.conflict(),
      };
    if (component instanceof UploadMappingPageComponent)
      return {
        busy: component.state.busy(),
        admitted: component.state.context() !== null,
        retained: component.state.hasRetainedUpload(),
        unknown: component.state.outcomeUnknown(),
        canRecover: component.state.canResend(),
        acknowledged: component.state.acknowledgement() !== null,
        needsReview: false,
      };
    if (component instanceof AppendRevisionPageComponent) {
      const view = component.state.view();
      return {
        busy: view.work !== "idle" || view.preparingFile,
        admitted: view.mapping !== null,
        retained: view.locked,
        unknown: view.outcomeUnknown,
        canRecover: view.canResend,
        acknowledged: view.acknowledgement !== null,
        needsReview: view.headReviewRequired,
      };
    }
    if (component instanceof CatalogRoutePage)
      return {
        // A same-selection read keeps its ready view in place while it runs.
        busy: component.kind() === "loading" || component.refreshing(),
        admitted: component.context() !== null,
        retained: false,
        unknown: false,
        canRecover: false,
        acknowledged: false,
        needsReview: false,
      };
    return {
      busy: false,
      admitted: false,
      retained: false,
      unknown: false,
      canRecover: false,
      acknowledged: false,
      needsReview: false,
    };
  }
  requestSample(): void {
    ++this.requestedSamples;
    this.sample();
  }
  sample(): void {
    const component = this.active();
    this.result.set(
      JSON.stringify({
        requestedSamples: this.requestedSamples,
        kind: this.sessions.state().kind,
        generation: this.sessions.generation(),
        visible: this.sessions.visible(),
        checking: this.sessions.checking(),
        accepts: this.context !== null && this.sessions.accepts(this.context),
        canDispatch:
          this.context !== null && this.sessions.canDispatch(this.context),
        context: this.contexts.context() !== null,
        actualOwner:
          this.consumer !== null &&
          this.active() === this.component &&
          this.owner(this.component) === this.consumer,
        notices: this.notices,
        clearedSynchronously: this.cleared,
        completed: this.complete,
        failed: this.failed,
        foreignActions: this.foreignActions,
        readReady:
          component instanceof CatalogRoutePage && component.data() !== null,
        reportsReady:
          component instanceof RevisionPageComponent &&
          component.reports.projection().response !== null &&
          component.reports.validation().response !== null,
        ...this.summary(component),
        ...this.featureFacts(component),
      }),
    );
  }
}
bootstrapApplication(BrowserSessionPublicProbe, {
  providers: [
    provideHttpClient(),
    { provide: APP_BASE_HREF, useValue: "/__weft119_probe/" },
    provideRouter(
      routes,
      withInMemoryScrolling({ scrollPositionRestoration: "enabled" }),
    ),
  ],
}).catch(() => {
  document.body.textContent = "The nonshipped public probe could not start.";
});
