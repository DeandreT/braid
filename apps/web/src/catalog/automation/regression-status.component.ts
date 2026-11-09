import {
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from "@angular/core";
import { RouterLink } from "@angular/router";
import {
  finalize,
  forkJoin,
  Subscription as RxSubscription,
  switchMap,
} from "rxjs";
import {
  catalogOwner,
  type CatalogContext,
  type CatalogMappingScope,
} from "../catalog-types";
import { CatalogContextStore } from "../catalog-context";
import { UnsentWork } from "../../workbench/unsent-work";
import {
  AutomationApi,
  type Evaluations,
  type Policy,
  type Suite,
} from "./automation-api";

/** Suite states recorded by the server (see migrations 0030). Unknown states show as given. */
export function regressionStateLabel(state: string | undefined): string {
  switch (state) {
    case undefined:
      return "Tests not recorded";
    case "noCases":
      return "No tests yet";
    case "queued":
      return "Tests queued";
    case "passed":
      return "Tests passed";
    case "failed":
      return "Tests failed";
    case "disabled":
      return "Automatic checks off";
    default:
      return `Tests: ${state}`;
  }
}

export function regressionStateTone(state: string | undefined): string | null {
  return state === "passed"
    ? "success"
    : state === "failed"
      ? "failure"
      : state === "queued"
        ? "running"
        : null;
}

@Component({
  selector: "weft-regression-status",
  imports: [RouterLink],
  template: `
    <span class="weft-pill" role="status" [attr.data-tone]="tone()">{{
      label()
    }}</span>
    @if (suite(); as s) {
      @if (s.totalCases > 0) {
        <span class="regression-counts">
          {{ s.passedCases }}/{{ s.totalCases }} passed ·
          {{ s.failedCases }} failed · {{ s.pendingCases }} pending</span
        >
      }
    }
    @if (runInline()) {
      @if (canRun()) {
        <button
          type="button"
          class="catalog-button"
          [disabled]="saving()"
          (click)="recheck()"
        >
          Run all current cases now
        </button>
      }
      @if (message()) {
        <span role="status">{{ message() }}</span>
      }
    } @else {
      <details
        class="regression-controls"
        (toggle)="controlsOpen.set($any($event.target).open)"
      >
        <summary>Check controls and policy</summary>
        @if (policy(); as p) {
          <p>
            Build policy:
            {{
              p.requirePassingRegression
                ? "passing exact-revision regression required"
                : "regression gate not required"
            }}.
          </p>
        }
        @if (manage()) {
          @if (canRun()) {
            <button
              type="button"
              class="catalog-button"
              [disabled]="saving()"
              (click)="recheck()"
            >
              Run all current cases now
            </button>
          }
          @if (casesLoading()) {
            <p role="status">Loading case results…</p>
          }
          @if (evaluations(); as page) {
            <ul>
              @for (item of page.cases; track item.caseId) {
                <li>
                  <code>{{ item.caseId }}</code> v{{ item.version }}:
                  {{ item.state }} ({{ item.attempts }} attempts); original
                  revision
                  <code>{{ item.originalRevisionId }}</code>
                </li>
              }
            </ul>
            @if (page.nextCaseId) {
              <button type="button" (click)="cases(page.nextCaseId)">
                Next case results
              </button>
            }
          }
          <p>
            Automatic checks:
            {{
              enabled() === null
                ? "unavailable"
                : enabled()
                  ? "on (all current cases)"
                  : "off"
            }}.
            @if (admin() && enabled() !== null) {
              <button
                type="button"
                class="catalog-button"
                [disabled]="saving()"
                (click)="toggle()"
              >
                {{
                  pendingToggle() !== null
                    ? "Retry change"
                    : enabled()
                      ? "Turn off"
                      : "Turn on"
                }}
              </button>
            }
          </p>
          <a
            [routerLink]="[
              '/catalog/organizations',
              scope().organizationId,
              'projects',
              scope().projectId,
              'automation',
            ]"
            >Automation and notifications</a
          >
          @if (message()) {
            <p role="status">{{ message() }}</p>
          }
        }
      </details>
    }
  `,
  styles: `
    :host {
      display: inline-flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 4px 10px;
      min-width: 0;
      font-size: 0.75rem;
    }
    .regression-counts {
      color: var(--text-muted);
    }
    .regression-controls > summary {
      color: var(--text-muted);
      cursor: pointer;
    }
    .regression-controls[open] {
      flex-basis: 100%;
      padding: 8px 10px;
      border: 1px solid var(--border);
      border-radius: 6px;
      background: var(--surface-subtle-2);
      text-align: left;
    }
    .regression-controls[open] > summary {
      margin-bottom: 6px;
      color: var(--text);
    }
    .regression-controls p {
      margin: 6px 0;
    }
  `,
})
export class RegressionStatusComponent {
  readonly context = input.required<CatalogContext>();
  readonly scope = input.required<CatalogMappingScope>();
  readonly revisionId = input.required<string>();
  readonly manage = input(false);
  readonly admin = input(false);
  readonly canRun = input(false);
  /** Show Run all next to the counts instead of the controls disclosure. */
  readonly runInline = input(false);
  readonly suite = signal<Suite | null>(null);
  readonly policy = signal<Policy | null>(null);
  readonly enabled = signal<boolean | null>(null);
  readonly saving = signal(false);
  readonly message = signal("");
  readonly uncertain = signal(false);
  readonly pendingToggle = signal<boolean | null>(null);
  readonly evaluations = signal<Evaluations | null>(null);
  readonly controlsOpen = signal(false);
  readonly casesLoading = signal(false);
  readonly label = computed(() => regressionStateLabel(this.suite()?.state));
  readonly tone = computed(() => regressionStateTone(this.suite()?.state));
  private readonly api = inject(AutomationApi);
  private readonly contexts = inject(CatalogContextStore);
  private actions = new Set<RxSubscription>();
  private readonly pendingRecheck = signal<string | null>(null);
  private ownerKey: string | null = null;
  constructor() {
    inject(UnsentWork).track(
      () =>
        this.saving() ||
        this.uncertain() ||
        this.pendingRecheck() !== null ||
        this.pendingToggle() !== null,
    );
    effect((onCleanup) => {
      const context = this.context(),
        scope = this.scope(),
        revision = this.revisionId();
      this.suite.set(null);
      this.policy.set(null);
      this.enabled.set(null);
      this.evaluations.set(null);
      const key = catalogOwner(context) + scope.mappingId + revision;
      if (key !== this.ownerKey) {
        this.ownerKey = key;
        this.pendingRecheck.set(null);
        this.pendingToggle.set(null);
        this.uncertain.set(false);
      }
      this.saving.set(false);
      const request = new RxSubscription();
      request.add(
        this.api
          .read<Suite>(context, scope, "/regression-suites/" + revision)
          .subscribe({
            next: (suite) => {
              this.suite.set(suite);
              if (suite.suiteId === this.pendingRecheck()) {
                this.pendingRecheck.set(null);
                this.uncertain.set(false);
              }
            },
            error: () => {},
          }),
      );
      if (this.manage())
        request.add(
          forkJoin({
            policy: this.api.read<Policy>(context, scope, "/automation"),
            enabled: this.api.read<{ enabled: boolean }>(
              context,
              scope,
              "/mappings/" + scope.mappingId + "/automation",
            ),
          }).subscribe({
            next: (result) => {
              this.policy.set(result.policy);
              this.enabled.set(result.enabled.enabled);
              if (this.pendingToggle() === result.enabled.enabled) {
                this.pendingToggle.set(null);
                this.uncertain.set(false);
              }
            },
            error: () => {},
          }),
        );
      onCleanup(() => {
        request.unsubscribe();
        this.actions.forEach((action) => action.unsubscribe());
        this.actions.clear();
      });
    });
    // Case results load when the controls are opened, once per suite read.
    effect(() => {
      const suite = this.suite();
      if (this.controlsOpen() && this.manage() && suite && !this.evaluations())
        untracked(() => this.cases());
    });
  }
  toggle(): void {
    if (this.saving()) return;
    const next = this.pendingToggle() ?? !this.enabled();
    this.pendingToggle.set(next);
    this.saving.set(true);
    this.actions.add(
      this.contexts
        .admitted(this.context())
        .pipe(
          switchMap((context) =>
            this.api.write(
              context,
              this.scope(),
              "/mappings/" + this.scope().mappingId + "/automation",
              { enabled: next },
            ),
          ),
          finalize(() => this.saving.set(false)),
        )
        .subscribe({
          next: () => {
            this.enabled.set(next);
            this.pendingToggle.set(null);
            this.uncertain.set(false);
            this.saving.set(false);
            this.message.set(
              "Saved. This applies to future revisions; retained results are unchanged.",
            );
          },
          error: () => {
            this.uncertain.set(true);
            this.saving.set(false);
            this.message.set("Change not confirmed. Refresh before retrying.");
          },
        }),
    );
  }
  cases(after?: string | null): void {
    const suite = this.suite();
    if (!suite || this.casesLoading()) return;
    this.casesLoading.set(true);
    this.actions.add(
      this.api
        .read<Evaluations>(
          this.context(),
          this.scope(),
          `/regression-suites/${suite.suiteId}/cases?limit=20${after ? "&afterId=" + after : ""}`,
        )
        .pipe(finalize(() => this.casesLoading.set(false)))
        .subscribe({
          next: (value) => this.evaluations.set(value),
          error: () => this.message.set("Case results unavailable."),
        }),
    );
  }
  recheck(): void {
    if (this.saving()) return;
    if (this.pendingRecheck() === null)
      this.pendingRecheck.set(crypto.randomUUID());
    this.saving.set(true);
    this.actions.add(
      this.contexts
        .admitted(this.context())
        .pipe(
          switchMap((context) =>
            this.api.write(
              context,
              this.scope(),
              "/regression-suites/" + this.pendingRecheck(),
              {
                mappingId: this.scope().mappingId,
                revisionId: this.revisionId(),
              },
            ),
          ),
          finalize(() => this.saving.set(false)),
        )
        .subscribe({
          next: () => {
            this.saving.set(false);
            this.pendingRecheck.set(null);
            this.uncertain.set(false);
            this.message.set(
              "All current cases queued for this exact revision.",
            );
            this.actions.add(
              this.api
                .read<Suite>(
                  this.context(),
                  this.scope(),
                  "/regression-suites/" + this.revisionId(),
                )
                .subscribe({
                  next: (value) => this.suite.set(value),
                  error: () => {},
                }),
            );
          },
          error: () => {
            this.saving.set(false);
            this.message.set(
              "Queueing not confirmed. Retry preserves the same request ID.",
            );
          },
        }),
    );
  }
}
