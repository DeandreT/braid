import { DOCUMENT, NgTemplateOutlet } from "@angular/common";
import {
  Component,
  ElementRef,
  HostListener,
  TemplateRef,
  computed,
  inject,
  input,
  output,
  signal,
  viewChild,
} from "@angular/core";
import { ResizeHandleComponent } from "./resize-handle.component";
import { WorkbenchState } from "./workbench-state";
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
export const workbenchLabels: Readonly<Record<WorkbenchView, string>> = {
  mapping: "Mapping",
  preview: "Preview",
  revisions: "Revisions",
  compare: "Compare",
  tests: "Tests",
  builds: "Builds",
  releases: "Releases",
  audit: "Audit",
};

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
  readonly views = workbenchViews;
  readonly docks: readonly { id: DockView; label: string }[] = [
    { id: "ask", label: "Ask Weft" },
    { id: "diagnostics", label: "Diagnostics" },
    { id: "input", label: "Input" },
    { id: "output", label: "Output" },
    { id: "diff", label: "Diff" },
    { id: "activity", label: "Activity" },
  ];
  readonly rowHeight = computed(() => rowHeights[this.state.layout().density]);
  readonly mobileInspector = signal(false);
  readonly query = signal("");
  readonly activeCommand = signal(0);
  readonly paletteItems = computed(() => {
    const items = [
      ...this.views.map((view) => ({
        id: `view:${view}`,
        label: workbenchLabels[view],
        detail: "This revision",
        view,
      })),
      ...this.commands(),
    ];
    const query = this.query().trim().toLocaleLowerCase();
    return items
      .filter(
        (item) =>
          !query ||
          `${item.label} ${item.detail ?? ""}`
            .toLocaleLowerCase()
            .includes(query),
      )
      .slice(0, 40);
  });
  private readonly palette =
    viewChild<ElementRef<HTMLDialogElement>>("palette");
  private readonly paletteInput =
    viewChild<ElementRef<HTMLInputElement>>("paletteInput");
  private readonly element = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly document = inject(DOCUMENT);
  private paletteReturn: HTMLElement | null = null;

  @HostListener("document:keydown", ["$event"])
  keyboard(event: KeyboardEvent): void {
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "k") {
      event.preventDefault();
      this.openPalette();
    }
    if (event.key === "Escape" && this.mobileInspector()) {
      this.mobileInspector.set(false);
      const target = event.target;
      if (target instanceof HTMLElement && target.closest(".wb-inspector"))
        this.focus(".wb-mobile-inspector-toggle");
    }
  }
  openPalette(): void {
    const dialog = this.palette()?.nativeElement;
    if (!dialog || dialog.open) return;
    this.paletteReturn =
      this.document.activeElement instanceof HTMLElement
        ? this.document.activeElement
        : null;
    this.query.set("");
    this.activeCommand.set(0);
    dialog.showModal();
    this.paletteInput()?.nativeElement.focus();
    this.paletteOpened.emit();
  }
  closePalette(): void {
    this.palette()?.nativeElement.close();
    if (this.paletteReturn?.isConnected) this.paletteReturn.focus();
    this.paletteReturn = null;
  }
  paletteKey(event: KeyboardEvent): void {
    if (event.key === "ArrowDown" || event.key === "ArrowUp") {
      event.preventDefault();
      this.activeCommand.update((value) =>
        Math.max(
          0,
          Math.min(
            this.paletteItems().length - 1,
            value + (event.key === "ArrowDown" ? 1 : -1),
          ),
        ),
      );
      queueMicrotask(() =>
        this.element.nativeElement
          .querySelector(`#wb-command-${this.activeCommand()}`)
          ?.scrollIntoView({ block: "nearest" }),
      );
    } else if (event.key === "Enter") {
      event.preventDefault();
      const item = this.paletteItems()[this.activeCommand()];
      if (item) this.choose(item);
    }
  }
  choose(item: WorkbenchCommand): void {
    this.closePalette();
    if (item.view) this.selectView(item.view, true);
    else this.command.emit(item);
  }
  selectView(view: WorkbenchView, focus = false): void {
    this.state.dispatch({ kind: "view", view });
    if (focus) this.focus(`[data-workflow="${view}"]`);
  }
  selectDock(dock: DockView, focus = false): void {
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
        ? this.views
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
  private focus(selector: string): void {
    queueMicrotask(() =>
      this.element.nativeElement.querySelector<HTMLElement>(selector)?.focus(),
    );
  }
}
