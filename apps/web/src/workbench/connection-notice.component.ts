import { DOCUMENT } from "@angular/common";
import { Component, DestroyRef, inject, signal } from "@angular/core";

@Component({
  selector: "weft-connection-notice",
  template: `@if (offline()) {
    <aside
      class="wb-state"
      data-tone="attention"
      role="status"
      aria-label="Browser offline"
    >
      <strong>Browser offline.</strong> Reconnect, then check the previous
      request before trying again. A request already sent may still finish.
    </aside>
  }`,
  styles: [
    `
      :host {
        display: contents;
      }
      aside {
        margin: 4px 6px;
        padding-block: 4px;
        font-size: 0.8125rem;
        line-height: 1.5;
        background: var(--wb-attention-tint);
        color: var(--wb-attention-text);
      }
    `,
  ],
})
export class ConnectionNoticeComponent {
  private readonly window = inject(DOCUMENT).defaultView;
  readonly offline = signal(this.window?.navigator.onLine === false);

  constructor() {
    const update = () =>
      this.offline.set(this.window?.navigator.onLine === false);
    this.window?.addEventListener("online", update);
    this.window?.addEventListener("offline", update);
    inject(DestroyRef).onDestroy(() => {
      this.window?.removeEventListener("online", update);
      this.window?.removeEventListener("offline", update);
    });
  }
}
