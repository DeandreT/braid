import { DatePipe } from "@angular/common";
import {
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  signal,
} from "@angular/core";
import { FormsModule } from "@angular/forms";
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
  type CatalogProjectScope,
} from "../catalog-types";
import { CatalogContextStore } from "../catalog-context";
import { UnsentWork } from "../../workbench/unsent-work";
import {
  AutomationApi,
  type Deliveries,
  type Destinations,
  type Inbox,
  type Options,
  type Policy,
  type Subscription,
  notificationEventLabel,
} from "./automation-api";

@Component({
  selector: "weft-automation-panel",
  imports: [FormsModule, RouterLink, DatePipe],
  templateUrl: "./automation-panel.component.html",
  styles: [
    `
      .automation-grid {
        display: grid;
        gap: 1rem;
        grid-template-columns: repeat(auto-fit, minmax(min(100%, 20rem), 1fr));
      }
      .automation-box {
        padding: 1rem;
        background: var(--surface-raised, #ffffff08);
        border: 1px solid var(--border, #8884);
        border-radius: 0.5rem;
      }
      .automation-box label {
        display: flex;
        align-items: center;
        gap: 0.5rem;
        margin: 0.6rem 0;
      }
      .automation-box input[type="checkbox"] {
        width: auto;
        display: inline-block;
        margin: 0;
      }
      .automation-box input[type="number"] {
        max-width: 6rem;
        margin-left: 0.5rem;
      }
      .automation-event {
        padding: 0.8rem 0;
        border-bottom: 1px solid var(--border, #8884);
      }
      code {
        overflow-wrap: anywhere;
      }
      button {
        margin: 0.3rem;
      }
      fieldset {
        border: 0;
        padding: 0;
      }
      legend {
        font-weight: 600;
      }
    `,
  ],
})
export class AutomationPanelComponent {
  readonly context = input.required<CatalogContext>();
  readonly scope = input.required<CatalogProjectScope>();
  readonly admin = input(false);
  readonly policy = signal<Policy | null>(null);
  readonly subscription = signal<Subscription | null>(null);
  readonly options = signal<Options | null>(null);
  readonly inbox = signal<Inbox | null>(null);
  readonly deliveries = signal<Deliveries | null>(null);
  readonly destinations = signal<Destinations | null>(null);
  readonly message = signal("");
  readonly busy = signal(false);
  readonly policyDirty = signal(false);
  readonly subscriptionDirty = signal(false);
  readonly uncertain = signal(false);
  readonly hasUnsent = computed(
    () =>
      this.policyDirty() ||
      this.subscriptionDirty() ||
      this.busy() ||
      this.uncertain(),
  );
  readonly label = notificationEventLabel;
  private readonly api = inject(AutomationApi);
  private readonly contexts = inject(CatalogContextStore);
  private readonly destroy = inject(DestroyRef);
  private reads: RxSubscription | null = null;
  private write: RxSubscription | null = null;
  private ownerKey: string | null = null;
  constructor() {
    inject(UnsentWork).track(() => this.hasUnsent(), this.destroy);
    effect((onCleanup) => {
      const key = catalogOwner(this.context()) + this.scope().projectId;
      if (key !== this.ownerKey) {
        this.ownerKey = key;
        this.policy.set(null);
        this.subscription.set(null);
        this.inbox.set(null);
        this.deliveries.set(null);
        this.destinations.set(null);
        this.policyDirty.set(false);
        this.subscriptionDirty.set(false);
        this.uncertain.set(false);
      }
      this.admin();
      this.load();
      onCleanup(() => {
        this.reads?.unsubscribe();
        this.write?.unsubscribe();
      });
    });
    this.destroy.onDestroy(() => {
      this.reads?.unsubscribe();
      this.write?.unsubscribe();
    });
  }
  load(): void {
    this.reads?.unsubscribe();
    this.message.set("");
    const c = this.context(),
      s = this.scope();
    this.reads = new RxSubscription();
    this.reads.add(
      forkJoin({
        policy: this.api.read<Policy>(c, s, "/automation"),
        subscription: this.api.read<Subscription>(
          c,
          s,
          "/notification-subscription",
        ),
        options: this.api.read<Options>(c, s, "/notification-options"),
        inbox: this.api.read<Inbox>(c, s, "/notifications?limit=20"),
      }).subscribe({
        next: (values) => {
          if (!this.policyDirty()) this.policy.set(values.policy);
          if (!this.subscriptionDirty())
            this.subscription.set(values.subscription);
          this.uncertain.set(false);
          this.options.set(values.options);
          this.inbox.set(values.inbox);
        },
        error: () =>
          this.message.set(
            "Automation is unavailable. Your saved settings have not changed.",
          ),
      }),
    );
    if (this.admin())
      this.reads.add(
        forkJoin({
          deliveries: this.api.read<Deliveries>(
            c,
            s,
            "/notification-deliveries?limit=20",
          ),
          destinations: this.api.read<Destinations>(
            c,
            s,
            "/notification-destinations",
          ),
        }).subscribe({
          next: (values) => {
            this.deliveries.set(values.deliveries);
            this.destinations.set(values.destinations);
          },
          error: () => this.message.set("Delivery history is unavailable."),
        }),
      );
  }
  save(path: string, body: unknown): void {
    if (this.busy()) return;
    this.busy.set(true);
    this.message.set("");
    let acknowledged = false;
    const snapshot = structuredClone(body),
      scope = { ...this.scope() };
    this.write = this.contexts
      .admitted(this.context())
      .pipe(
        switchMap((context) => this.api.write(context, scope, path, snapshot)),
        finalize(() => {
          this.busy.set(false);
          if (!acknowledged) this.uncertain.set(true);
        }),
      )
      .subscribe({
        next: () => {
          acknowledged = true;
          this.uncertain.set(false);
          if (path === "/automation") this.policyDirty.set(false);
          if (path === "/notification-subscription")
            this.subscriptionDirty.set(false);
          this.busy.set(false);
          this.message.set("Saved.");
        },
        error: () => {
          this.busy.set(false);
          this.message.set(
            "Not confirmed. Refresh to check the saved settings before retrying.",
          );
        },
      });
  }
  event(value: string, checked: boolean): void {
    this.subscriptionDirty.set(true);
    this.subscription.update((current) =>
      current
        ? {
            ...current,
            events: checked
              ? [...current.events, value]
              : current.events.filter((e) => e !== value),
          }
        : null,
    );
  }
  webhook(value: string, checked: boolean): void {
    this.subscriptionDirty.set(true);
    this.subscription.update((current) =>
      current
        ? {
            ...current,
            webhooks: checked
              ? [...current.webhooks, value]
              : current.webhooks.filter((e) => e !== value),
          }
        : null,
    );
  }
  nextInbox(): void {
    const after = this.inbox()?.nextId;
    if (!after) return;
    this.reads?.add(
      this.api
        .read<Inbox>(
          this.context(),
          this.scope(),
          "/notifications?limit=20&afterId=" + after,
        )
        .subscribe({
          next: (value) => this.inbox.set(value),
          error: () => this.message.set("Notification page unavailable."),
        }),
    );
  }
  nextDeliveries(): void {
    const after = this.deliveries()?.nextId;
    if (!after) return;
    this.reads?.add(
      this.api
        .read<Deliveries>(
          this.context(),
          this.scope(),
          "/notification-deliveries?limit=20&afterId=" + after,
        )
        .subscribe({
          next: (value) => this.deliveries.set(value),
          error: () => this.message.set("Delivery page unavailable."),
        }),
    );
  }
}
