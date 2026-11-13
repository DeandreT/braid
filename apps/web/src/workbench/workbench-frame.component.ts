import { DOCUMENT, NgTemplateOutlet } from "@angular/common";
import {
  Component,
  DestroyRef,
  ElementRef,
  HostListener,
  TemplateRef,
  computed,
  effect,
  afterNextRender,
  inject,
  input,
  output,
  signal,
} from "@angular/core";
import { ResizeHandleComponent } from "./resize-handle.component";
import { WorkbenchState } from "./workbench-state";
import { WorkspaceCommands } from "./workspace-commands";
import { dockLabels, workbenchLabels } from "./presentation";
import {
  rowHeights,
  workbenchViews,
  type DockView,
  type InspectorTab,
  type WorkbenchView,
} from "./workbench-model";

export interface WorkbenchCommand {
  id: string;
  label: string;
  detail?: string;
  view?: WorkbenchView;
}
export { workbenchLabels } from "./presentation";

@Component({
  selector: "weft-workbench-frame",
  imports: [NgTemplateOutlet, ResizeHandleComponent],
  templateUrl: "./workbench-frame.component.html",
  styleUrl: "./workbench-frame.component.css",
})
export class WorkbenchFrameComponent {
  readonly state = inject(WorkbenchState);
  readonly inspector = input<TemplateRef<unknown> | null>(null);
  readonly dock = input<TemplateRef<unknown> | null>(null);
  readonly commands = input<readonly WorkbenchCommand[]>([]);
  readonly command = output<WorkbenchCommand>();
  readonly paletteOpened = output<void>();
  readonly dockContext = input("");
  readonly viewLabels = workbenchLabels;
  readonly viewOptions = input<readonly WorkbenchView[]>(workbenchViews);
  readonly docks: readonly { id: DockView; label: string }[] = [
    ...(
      [
        "inspector",
        "diagnostics",
        "input",
        "output",
        "diff",
        "activity",
      ] as const
    ).map((id) => ({ id, label: dockLabels[id] })),
  ];
  readonly rowHeight = computed(() => rowHeights[this.state.layout().density]);
  readonly paletteItems = computed(() => {
    const items = [
      ...this.viewOptions().map((view) => ({
        id: `view:${view}`,
        label: workbenchLabels[view],
        detail: "This saved version",
        view,
      })),
      ...this.commands(),
    ];
    return items.slice(0, 40);
  });
  private readonly element = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly document = inject(DOCUMENT);
  private readonly sharedCommands = inject(WorkspaceCommands);
  private readonly regionHeight = signal(600);
  private readonly regionWidth = signal(800);
  private trayReturn: HTMLElement | null = null;
  readonly trayMaximum = computed(() =>
    Math.floor(Math.min(this.regionHeight() * 0.4, this.regionHeight() - 240)),
  );
  readonly focusedTray = computed(
    () => this.regionWidth() < 480 || this.trayMaximum() < 140,
  );
  readonly trayHeight = computed(() =>
    Math.max(140, Math.min(this.trayMaximum(), this.state.layout().dockHeight)),
  );
  constructor() {
    const destroy = inject(DestroyRef);
    effect(() => this.sharedCommands.entries.set(this.paletteItems()));
    effect(() => {
      const selection = this.state.selection();
      queueMicrotask(() => {
        this.revealTab(`[data-workflow="${selection.view}"]`);
        this.revealTab(`[data-dock="${selection.dock}"]`);
        this.revealTab(`[data-inspector="${selection.inspectorTab}"]`);
      });
    });
    const subscriptions = this.sharedCommands.chosen.subscribe((item) =>
      this.choose(item),
    );
    subscriptions.add(
      this.sharedCommands.opening.subscribe(() => this.paletteOpened.emit()),
    );
    afterNextRender(() => {
      const region =
        this.element.nativeElement.querySelector<HTMLElement>(".wb-workspace");
      if (!region) return;
      const observer = new ResizeObserver(() => {
        this.regionHeight.set(region.clientHeight);
        this.regionWidth.set(region.clientWidth);
      });
      observer.observe(region);
      destroy.onDestroy(() => observer.disconnect());
    });
    destroy.onDestroy(() => {
      subscriptions.unsubscribe();
      this.sharedCommands.entries.set([]);
    });
  }
  openInspector(): void {
    this.trayReturn =
      this.document.activeElement instanceof HTMLElement
        ? this.document.activeElement
        : null;
    this.state.dispatch({ kind: "dock", dock: "inspector" });
    this.state.updateLayout({
      dockCollapsed: false,
      inspectorCollapsed: false,
    });
    if (this.focusedTray()) this.focus('[data-inspector="0"]');
  }
  closeTray(): void {
    this.state.updateLayout({ dockCollapsed: true, inspectorCollapsed: true });
    const target = this.trayReturn;
    queueMicrotask(() => {
      if (target?.isConnected && target.getClientRects().length) target.focus();
      else this.focus(".wb-inspector-toggle");
    });
    this.trayReturn = null;
  }

  @HostListener("document:keydown", ["$event"])
  keyboard(event: KeyboardEvent): void {
    if (
      event.key === "Escape" &&
      !event.defaultPrevented &&
      !this.document.querySelector("#account-menu") &&
      !this.document.querySelector(".chat-focused.chat-open") &&
      this.focusedTray() &&
      !this.state.layout().dockCollapsed &&
      !this.document.querySelector("dialog[open]")
    ) {
      this.closeTray();
    }
  }
  openPalette(): void {
    this.sharedCommands.requested.next();
  }
  choose(item: WorkbenchCommand): void {
    if (item.view) this.selectView(item.view, true);
    else this.command.emit(item);
  }
  selectView(view: WorkbenchView, focus = false): void {
    this.state.dispatch({ kind: "view", view });
    if (focus) this.focus(`[data-workflow="${view}"]`);
  }
  selectDock(dock: DockView, focus = false): void {
    if (this.state.layout().dockCollapsed)
      this.trayReturn =
        this.document.activeElement instanceof HTMLElement
          ? this.document.activeElement
          : null;
    this.state.dispatch({ kind: "dock", dock });
    this.state.updateLayout({ dockCollapsed: false });
    if (focus) this.focus(`[data-dock="${dock}"]`);
  }
  selectInspector(tab: number, focus = false): void {
    if (tab !== 0 && tab !== 1 && tab !== 2) return;
    this.state.dispatch({ kind: "tab", tab: tab as InspectorTab });
    if (focus) this.focus(`[data-inspector="${tab}"]`);
  }
  tabKey(event: KeyboardEvent, area: "view" | "dock" | "inspector"): void {
    if (!["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) return;
    event.preventDefault();
    const values =
      area === "view"
        ? this.viewOptions()
        : area === "dock"
          ? this.docks.map((v) => v.id)
          : [0, 1, 2];
    const current =
      area === "view"
        ? this.state.selection().view
        : area === "dock"
          ? this.state.selection().dock
          : this.state.selection().inspectorTab;
    const index = values.findIndex((value) => value === current);
    const next =
      event.key === "Home"
        ? 0
        : event.key === "End"
          ? values.length - 1
          : (index + (event.key === "ArrowRight" ? 1 : values.length - 1)) %
            values.length;
    if (area === "view") this.selectView(values[next] as WorkbenchView, true);
    else if (area === "dock") this.selectDock(values[next] as DockView, true);
    else this.selectInspector(values[next] as number, true);
  }
  cycleDensity(): void {
    const values = ["compact", "comfortable", "dense"] as const;
    const index = values.indexOf(this.state.layout().density);
    this.state.updateLayout({ density: values[(index + 1) % values.length]! });
  }
  private revealTab(selector: string): void {
    const tab = this.element.nativeElement.querySelector<HTMLElement>(selector);
    const nav = tab?.parentElement;
    if (!tab || !nav || !tab.getClientRects().length) return;
    const item = tab.getBoundingClientRect();
    const viewport = nav.getBoundingClientRect();
    if (item.left < viewport.left) nav.scrollLeft += item.left - viewport.left;
    else if (item.right > viewport.right)
      nav.scrollLeft += item.right - viewport.right;
  }
  private focus(selector: string): void {
    queueMicrotask(() =>
      this.element.nativeElement
        .querySelector<HTMLElement>(selector)
        ?.focus({ preventScroll: true }),
    );
  }
}
