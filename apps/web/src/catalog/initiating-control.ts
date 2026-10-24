import { DOCUMENT } from "@angular/common";
import {
  DestroyRef,
  ElementRef,
  Injectable,
  effect,
  inject,
  untracked,
} from "@angular/core";
import { CatalogContextStore } from "./catalog-context";

const controls = "button, a[href], [role='button']";
/** A check that starts this soon after a click belongs to that click's action. */
const actionWindowMs = 1000;

/**
 * The workspace control that started an action shows the action's routine access check as its
 * busy state. If the action disables the control and focus falls back to the page, focus returns
 * to it once it is enabled again. A boundary, a later focus move or the next action forgets it.
 */
@Injectable()
export class InitiatingControl {
  private readonly host =
    inject<ElementRef<HTMLElement>>(ElementRef).nativeElement;
  private readonly document = inject(DOCUMENT);
  private readonly contexts = inject(CatalogContextStore);
  private initiator: HTMLElement | null = null;
  private clickedAt = 0;
  private busy: HTMLElement | null = null;
  private restoring = false;
  private readonly observer = new MutationObserver(() => this.restoreFocus());

  constructor() {
    const click = (event: Event) => {
      const control =
        event.target instanceof Element
          ? event.target.closest<HTMLElement>(controls)
          : null;
      if (!control || !this.host.contains(control)) return;
      this.forget();
      this.initiator = control;
      this.clickedAt = performance.now();
    };
    const focusOut = (event: FocusEvent) => {
      const control = this.initiator;
      if (
        control &&
        event.target === control &&
        event.relatedTarget === null &&
        control.matches(":disabled")
      )
        this.watch(control);
    };
    const focusIn = (event: FocusEvent) => {
      if (event.target !== this.initiator) this.forget();
    };
    this.host.addEventListener("click", click, true);
    this.document.addEventListener("focusout", focusOut, true);
    this.document.addEventListener("focusin", focusIn, true);
    effect(() => {
      const checking = this.contexts.checking();
      untracked(() => {
        const control = this.initiator;
        if (!checking) this.clearBusy();
        else if (
          control?.isConnected &&
          this.busy === null &&
          performance.now() - this.clickedAt < actionWindowMs
        )
          this.markBusy(control);
      });
    });
    const invalidation = this.contexts.invalidated.subscribe(() =>
      this.forget(),
    );
    inject(DestroyRef).onDestroy(() => {
      invalidation.unsubscribe();
      this.host.removeEventListener("click", click, true);
      this.document.removeEventListener("focusout", focusOut, true);
      this.document.removeEventListener("focusin", focusIn, true);
      this.forget();
    });
  }

  private markBusy(control: HTMLElement): void {
    this.busy = control;
    // One action's check: a later routine check does not mark this control again.
    this.clickedAt = 0;
    control.setAttribute("aria-busy", "true");
  }

  private clearBusy(): void {
    this.busy?.removeAttribute("aria-busy");
    this.busy = null;
  }

  private watch(control: HTMLElement): void {
    this.restoring = true;
    this.observer.observe(control, {
      attributes: true,
      attributeFilter: ["disabled"],
    });
  }

  private restoreFocus(): void {
    const control = this.initiator;
    if (!this.restoring || !control || control.matches(":disabled")) return;
    this.forget();
    const active = this.document.activeElement;
    if (
      control.isConnected &&
      this.host.contains(control) &&
      control.closest("[inert], [hidden]") === null &&
      (active === null ||
        active === this.document.body ||
        active === this.document.documentElement)
    )
      control.focus({ preventScroll: true });
  }

  private forget(): void {
    this.clearBusy();
    this.initiator = null;
    this.clickedAt = 0;
    this.restoring = false;
    this.observer.disconnect();
  }
}
