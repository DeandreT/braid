import { Component, input, output } from "@angular/core";

@Component({
  selector: "weft-resize-handle",
  template: `<div
    role="separator"
    tabindex="0"
    [attr.aria-label]="label()"
    [attr.aria-description]="description()"
    [attr.title]="description()"
    [attr.aria-orientation]="orientation()"
    [attr.aria-valuenow]="value()"
    [attr.aria-valuemin]="minimum()"
    [attr.aria-valuemax]="maximum()"
    [attr.aria-controls]="controls()"
    [class.vertical]="orientation() === 'vertical'"
    [class.horizontal]="orientation() === 'horizontal'"
    (pointerdown)="start($event)"
    (pointermove)="move($event)"
    (pointerup)="finish($event)"
    (lostpointercapture)="drag = null"
    (keydown)="key($event)"
  ></div>`,
  styles: [
    `
      :host {
        display: block;
      }
      div {
        position: absolute;
        touch-action: none;
        z-index: 3;
      }
      .vertical {
        width: 6px;
        top: 0;
        bottom: 0;
        left: 0;
        cursor: ew-resize;
      }
      .horizontal {
        height: 6px;
        left: 0;
        right: 0;
        top: -3px;
        cursor: ns-resize;
      }
      div:hover,
      div:focus-visible {
        background: var(--overlay-3);
        outline: 2px solid var(--focus-ring);
        outline-offset: -1px;
      }
    `,
  ],
})
export class ResizeHandleComponent {
  readonly label = input.required<string>();
  readonly description = input<string | null>(null);
  readonly controls = input<string | null>(null);
  readonly orientation = input<"vertical" | "horizontal">("vertical");
  readonly value = input.required<number>();
  readonly minimum = input(80);
  readonly maximum = input(600);
  readonly direction = input<1 | -1>(1);
  readonly resized = output<number>();
  drag: { pointer: number; origin: number; value: number } | null = null;
  start(event: PointerEvent): void {
    if (event.button !== 0) return;
    event.preventDefault();
    (event.currentTarget as HTMLElement).setPointerCapture(event.pointerId);
    this.drag = {
      pointer: event.pointerId,
      origin: this.coordinate(event),
      value: this.value(),
    };
  }
  move(event: PointerEvent): void {
    if (this.drag?.pointer !== event.pointerId) return;
    this.emit(
      this.drag.value +
        this.direction() * (this.coordinate(event) - this.drag.origin),
    );
  }
  finish(event: PointerEvent): void {
    if (this.drag?.pointer !== event.pointerId) return;
    this.drag = null;
    (event.currentTarget as HTMLElement).releasePointerCapture(event.pointerId);
  }
  key(event: KeyboardEvent): void {
    const negative =
      this.orientation() === "vertical" ? "ArrowLeft" : "ArrowUp";
    const positive =
      this.orientation() === "vertical" ? "ArrowRight" : "ArrowDown";
    if (![negative, positive, "Home", "End"].includes(event.key)) return;
    event.preventDefault();
    this.emit(
      event.key === "Home"
        ? this.minimum()
        : event.key === "End"
          ? this.maximum()
          : this.value() +
            this.direction() *
              (event.key === negative ? -1 : 1) *
              (event.shiftKey ? 20 : 10),
    );
  }
  private coordinate(event: PointerEvent): number {
    return this.orientation() === "vertical" ? event.clientX : event.clientY;
  }
  private emit(value: number): void {
    this.resized.emit(
      Math.round(Math.max(this.minimum(), Math.min(this.maximum(), value))),
    );
  }
}
