import { DOCUMENT, ViewportScroller } from "@angular/common";
import { Injectable, inject } from "@angular/core";

/** Router history belongs to the content viewport, not the persistent shell. */
@Injectable()
export class WorkspaceViewportScroller extends ViewportScroller {
  private readonly document = inject(DOCUMENT);
  private offset: () => [number, number] = () => [0, 0];
  private get region(): HTMLElement | null {
    return this.document.querySelector<HTMLElement>(
      ".app-workbench #main-content",
    );
  }
  setOffset(value: [number, number] | (() => [number, number])): void {
    this.offset = typeof value === "function" ? value : () => value;
  }
  getScrollPosition(): [number, number] {
    const region = this.region,
      window = this.document.defaultView;
    return region
      ? [region.scrollLeft, region.scrollTop]
      : [window?.scrollX ?? 0, window?.scrollY ?? 0];
  }
  scrollToPosition(
    [left, top]: [number, number],
    options?: ScrollOptions,
  ): void {
    (this.region ?? this.document.defaultView)?.scrollTo({
      left,
      top,
      ...options,
    });
  }
  scrollToAnchor(anchor: string, options?: ScrollOptions): void {
    const target =
      this.document.getElementById(anchor) ??
      this.document.getElementsByName(anchor)[0];
    if (!target) return;
    target.scrollIntoView({ block: "start", ...options });
    const [x, y] = this.getScrollPosition(),
      [dx, dy] = this.offset();
    this.scrollToPosition([x - dx, y - dy], options);
    target.focus({ preventScroll: true });
  }
  setHistoryScrollRestoration(value: "auto" | "manual"): void {
    if (this.document.defaultView)
      this.document.defaultView.history.scrollRestoration = value;
  }
}
