import { DatePipe } from "@angular/common";
import { Component, DestroyRef, computed, inject, signal } from "@angular/core";
import { takeUntilDestroyed } from "@angular/core/rxjs-interop";
import { ActivatedRoute, RouterLink } from "@angular/router";
import { finalize, switchMap } from "rxjs";
import { CatalogContextStore } from "../catalog-context";
import { CatalogStatusComponent } from "../catalog-shell.component";
import { createCatalogRead } from "../catalog-state";
import { isCatalogId } from "../catalog-types";
import { UnsentWork } from "../../workbench/unsent-work";
import {
  AutomationApi,
  notificationEventLabel,
  type Inbox,
} from "./automation-api";

@Component({
  selector: "braid-notification-centre",
  imports: [DatePipe, RouterLink, CatalogStatusComponent],
  template: ` <section
    class="catalog-page"
    aria-labelledby="notifications-title"
  >
    <header class="catalog-heading">
      <div>
        <p class="catalog-eyebrow">Workspace activity</p>
        <h1 id="notifications-title">Notification centre</h1>
        <p class="catalog-subtitle">
          Visible project activity and invitations addressed to you.
          Notifications contain IDs and links only. Choose subscriptions in each
          project's automation settings.
        </p>
      </div>
      <div class="catalog-actions">
        @if (view(); as v) {
          <a
            class="catalog-button"
            [routerLink]="[
              '/catalog/organizations',
              v.context.allowedOrganizationId,
              'projects',
            ]"
            >Projects and subscriptions</a
          >
        }
        <button
          type="button"
          class="catalog-button"
          (click)="read.retry()"
          [disabled]="busy()"
        >
          Refresh
        </button>
      </div>
    </header>
    @if (view(); as v) {
      <div class="notification-body">
        @if (v.data.notifications.length) {
          <ul class="catalog-list catalog-rows">
            @for (item of v.data.notifications; track item.id) {
              <li class="catalog-list-item">
                <div class="catalog-row-main">
                  <h2 class="catalog-row-title">{{ label(item.event) }}</h2>
                  <p class="catalog-row-description">
                    <time
                      >{{
                        item.createdAtUtc | date: "medium" : "UTC"
                      }}
                      UTC</time
                    >
                    ·
                    {{
                      item.projectId
                        ? "Project activity"
                        : "Organization invitation"
                    }}
                    · Resource
                    <code class="catalog-code">{{ item.resourceId }}</code>
                  </p>
                </div>
                <div class="catalog-row-meta">
                  <span
                    class="braid-pill"
                    [attr.data-tone]="item.read ? null : 'running'"
                    >{{ item.read ? "Read" : "Unread" }}</span
                  >
                  <a [routerLink]="item.link">Open related page</a>
                  @if (!item.read) {
                    <button
                      class="catalog-button"
                      type="button"
                      (click)="mark(item.id)"
                      [disabled]="busy()"
                    >
                      Mark read
                    </button>
                  }
                </div>
              </li>
            }
          </ul>
        } @else {
          <section class="braid-state">
            <h2>You're all caught up</h2>
            <p>
              No notifications on this page. Project activity and invitations
              for you appear here.
            </p>
          </section>
        }
        <nav class="catalog-pagination" aria-label="Notification pages">
          @if (v.data.nextId || after()) {
            <a class="catalog-page-link" routerLink="/notifications">Newest</a>
          }
          @if (v.data.nextId) {
            <a
              class="catalog-page-link"
              routerLink="/notifications"
              [queryParams]="{ after: v.data.nextId }"
              >Older notifications</a
            >
          }
        </nav>
      </div>
    } @else {
      <braid-catalog-status [kind]="read.state().kind" (retry)="read.retry()" />
    }
    @if (message()) {
      <p role="status">{{ message() }}</p>
    }
  </section>`,
})
export class NotificationCentreComponent {
  readonly read = createCatalogRead<Inbox>();
  readonly view = computed(() => {
    const value = this.read.state();
    return value.kind === "ready" ? value : null;
  });
  readonly busy = signal(false);
  readonly message = signal("");
  readonly after = signal<string | null>(null);
  readonly label = notificationEventLabel;
  private readonly api = inject(AutomationApi);
  private readonly contexts = inject(CatalogContextStore);
  private readonly destroy = inject(DestroyRef);
  constructor() {
    inject(UnsentWork).track(() => this.busy(), this.destroy);
    inject(ActivatedRoute)
      .queryParamMap.pipe(takeUntilDestroyed(this.destroy))
      .subscribe((query) => {
        const after = query.get("after");
        this.after.set(after);
        if (after !== null && !isCatalogId(after)) {
          this.read.invalidLink();
          return;
        }
        this.read.select(
          null,
          (context) => this.api.inbox(context, after),
          after,
        );
      });
  }
  mark(id: string): void {
    const view = this.view();
    if (!view || this.busy()) return;
    this.busy.set(true);
    this.message.set("");
    this.contexts
      .admitted(view.context)
      .pipe(
        switchMap((context) => this.api.markInboxRead(context, id)),
        takeUntilDestroyed(this.destroy),
        finalize(() => this.busy.set(false)),
      )
      .subscribe({
        next: () => {
          this.busy.set(false);
          this.read.retry();
        },
        error: () => {
          this.busy.set(false);
          this.message.set(
            "Read status was not confirmed. Refresh before retrying.",
          );
        },
      });
  }
}
