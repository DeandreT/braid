import { DOCUMENT } from "@angular/common";
import {
  Component,
  DestroyRef,
  ElementRef,
  effect,
  inject,
  input,
  output,
  viewChild,
} from "@angular/core";
import type { ConfirmationView } from "./confirmation";

let nextConfirmation = 0;

/** Presentation only: the owner retains and validates the exact operation frame. */
@Component({
  selector: "weft-confirmation",
  template: `
    <dialog #dialog [attr.aria-labelledby]="titleId" (cancel)="cancel($event)">
      @if (model(); as value) {
        <h2 [id]="titleId">{{ value.title }}</h2>
        <p>{{ value.effect }}</p>
        <dl>
          @for (fact of value.facts; track $index) {
            <dt>{{ fact.label }}</dt>
            <dd>{{ fact.value }}</dd>
          }
        </dl>
        <p class="leaving">{{ value.afterLeaving }}</p>
        <footer>
          <button #dismiss type="button" (click)="dismissed.emit()">
            Not now
          </button>
          <button
            type="button"
            class="primary"
            data-confirm-action
            (click)="confirmed.emit()"
          >
            {{ value.action }}
          </button>
        </footer>
      }
    </dialog>
  `,
  styles: [
    `
      dialog {
        max-width: min(520px, calc(100% - 24px));
        padding: 14px;
        border: 1px solid #bcccaf;
        border-radius: 5px;
        background: #fff;
        color: #213b34;
      }
      dialog::backdrop {
        background: #1c342b55;
      }
      h2 {
        font-size: 1rem;
        margin: 0 0 8px;
      }
      p {
        font-size: 0.8125rem;
        line-height: 1.5;
        margin: 8px 0;
      }
      dl {
        display: grid;
        grid-template-columns: minmax(85px, max-content) minmax(0, 1fr);
        gap: 5px 9px;
        font-size: 0.8125rem;
      }
      dt,
      .leaving {
        color: var(--wb-evidence-text);
      }
      dd {
        margin: 0;
        overflow-wrap: anywhere;
      }
      footer {
        display: flex;
        flex-wrap: wrap;
        justify-content: flex-end;
        gap: 8px;
        margin-top: 12px;
      }
      button {
        border: 1px solid var(--wb-control-border);
        border-radius: 3px;
        padding: 5px 9px;
        background: #fff;
        color: #213b34;
        font: inherit;
        font-size: 0.8125rem;
        cursor: pointer;
      }
      .primary {
        background: var(--wb-selection-text);
        border-color: var(--wb-selection-text);
        color: #fff;
      }
      button:focus-visible {
        outline: 2px solid var(--wb-focus);
        outline-offset: 2px;
      }
    `,
  ],
})
export class ConfirmationComponent {
  readonly titleId = `action-confirmation-${++nextConfirmation}`;
  readonly model = input<ConfirmationView | null>(null);
  readonly confirmed = output<void>();
  readonly dismissed = output<void>();
  private readonly dialog = viewChild<ElementRef<HTMLDialogElement>>("dialog");
  private readonly dismiss =
    viewChild<ElementRef<HTMLButtonElement>>("dismiss");
  private readonly document = inject(DOCUMENT);
  private returnTo: HTMLElement | null = null;
  private needsFocus = false;

  constructor() {
    effect(() => {
      const value = this.model(),
        dialog = this.dialog()?.nativeElement,
        dismiss = this.dismiss()?.nativeElement;
      if (!dialog) return;
      if (value && !dialog.open) {
        this.returnTo =
          this.document.activeElement instanceof HTMLElement
            ? this.document.activeElement
            : null;
        this.needsFocus = true;
        dialog.showModal();
      } else if (!value && dialog.open) this.close();
      if (value && this.needsFocus && dismiss) {
        dismiss.focus();
        this.needsFocus = false;
      }
    });
    inject(DestroyRef).onDestroy(() => this.close());
  }
  cancel(event: Event): void {
    event.preventDefault();
    this.dismissed.emit();
  }
  private close(): void {
    this.dialog()?.nativeElement.close();
    this.needsFocus = false;
    const target = this.returnTo;
    this.returnTo = null;
    if (target?.isConnected && !target.closest("[hidden], [inert]"))
      target.focus({ preventScroll: true });
  }
}
