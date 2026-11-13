import { DOCUMENT, ViewportScroller } from "@angular/common";
import {
  Component,
  Injector,
  afterNextRender,
  computed,
  inject,
  signal,
} from "@angular/core";
import { takeUntilDestroyed, toObservable } from "@angular/core/rxjs-interop";
import { Title } from "@angular/platform-browser";
import {
  ActivatedRoute,
  Router,
  RouterLink,
  RouterLinkActive,
  Scroll,
} from "@angular/router";
import {
  catchError,
  combineLatest,
  filter,
  map,
  of,
  shareReplay,
  switchMap,
  throwError,
  type Observable,
} from "rxjs";
import {
  HelpApi,
  HelpError,
  type HelpErrorKind,
  type HelpSection,
  type HelpSectionList,
} from "./help-api";
import { HelpContentComponent } from "./help-content.component";

type Loaded =
  | { kind: "loading" }
  | { kind: "error"; error: HelpErrorKind }
  | {
      kind: "ready";
      contents: HelpSectionList;
      section: HelpSection | null;
      missing: boolean;
    };

/** The user guide at /help (contents) and /help/:sectionId (one section). */
@Component({
  selector: "weft-help-page",
  imports: [RouterLink, RouterLinkActive, HelpContentComponent],
  templateUrl: "./help-page.component.html",
  styleUrl: "./help-page.component.css",
})
export class HelpPageComponent {
  private readonly api = inject(HelpApi);
  private readonly route = inject(ActivatedRoute);
  private readonly title = inject(Title);
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);
  private readonly router = inject(Router);
  private readonly scroller = inject(ViewportScroller);
  private readonly retries = signal(0);
  /** A history navigation's saved position, applied once its section has rendered. */
  private restore: [number, number] | null = null;
  private historyNavigation = false;
  private contents$: Observable<HelpSectionList> | null = null;
  readonly state = signal<Loaded>({ kind: "loading" });
  readonly sectionId = signal<string | null>(null);
  readonly contents = computed(() => {
    const state = this.state();
    return state.kind === "ready" ? state.contents : null;
  });
  readonly section = computed(() => {
    const state = this.state();
    return state.kind === "ready" ? state.section : null;
  });
  readonly missing = computed(() => {
    const state = this.state();
    return state.kind === "ready" && state.missing;
  });
  readonly error = computed(() => {
    const state = this.state();
    return state.kind === "error" ? state.error : null;
  });
  readonly neighbours = computed(() => {
    const contents = this.contents(),
      section = this.section();
    const index =
      contents && section
        ? contents.sections.findIndex((item) => item.id === section.id)
        : -1;
    return {
      previous: index > 0 ? contents!.sections[index - 1] : null,
      next:
        index >= 0 && index + 1 < contents!.sections.length
          ? contents!.sections[index + 1]
          : null,
    };
  });
  /** The section's level 2 headings, for its "On this page" list. */
  readonly outline = computed(() =>
    (this.section()?.blocks ?? []).flatMap((block) =>
      block.kind === "heading" && block.level === 2
        ? [{ anchor: block.anchor, text: block.text }]
        : [],
    ),
  );

  constructor() {
    combineLatest([
      this.route.paramMap.pipe(map((params) => params.get("sectionId"))),
      toObservable(this.retries),
    ])
      .pipe(
        switchMap(([sectionId]) => {
          this.historyNavigation =
            this.router.currentNavigation()?.trigger === "popstate";
          this.restore = null;
          this.sectionId.set(sectionId);
          this.state.set({ kind: "loading" });
          return this.read(sectionId);
        }),
        takeUntilDestroyed(),
      )
      .subscribe((state) => {
        this.state.set(state);
        this.applyTitle();
        if (state.kind !== "ready" || !state.section) return;
        const position = this.restore;
        this.restore = null;
        if (position)
          afterNextRender(() => this.scroller.scrollToPosition(position), {
            injector: this.injector,
          });
        else if (!this.historyNavigation)
          this.scrollToFragment(this.route.snapshot.fragment);
      });
    // After each navigation the router applies the route's static title and scrolls to the top
    // or to a saved history position. Keep the section title, and follow in-guide anchors.
    this.router.events
      .pipe(
        filter((event): event is Scroll => event instanceof Scroll),
        takeUntilDestroyed(),
      )
      .subscribe((event) => {
        this.applyTitle();
        if (event.position) {
          if (this.state().kind === "loading") this.restore = event.position;
          return;
        }
        if (this.section()) this.scrollToFragment(event.anchor, false);
      });
  }

  private applyTitle(): void {
    const section = this.section();
    this.title.setTitle(
      section ? `${section.title} · Help · braid` : "Help · braid",
    );
  }

  retry(): void {
    this.retries.update((value) => value + 1);
  }

  private read(sectionId: string | null): Observable<Loaded> {
    const contents = (this.contents$ ??= this.api
      .sections()
      .pipe(shareReplay({ bufferSize: 1, refCount: false })));
    const section: Observable<HelpSection | null> =
      sectionId === null
        ? of(null)
        : this.api.section(sectionId).pipe(
            catchError((error: unknown) =>
              error instanceof HelpError && error.kind === "notFound"
                ? of(undefined)
                : throwError(() => error),
            ),
            map((value) => value ?? null),
          );
    return combineLatest([contents, section]).pipe(
      map(([list, current]): Loaded => ({
        kind: "ready",
        contents: list,
        section: current,
        missing: sectionId !== null && current === null,
      })),
      catchError((error: unknown) => {
        this.contents$ = null;
        return of<Loaded>({
          kind: "error",
          error: error instanceof HelpError ? error.kind : "failed",
        });
      }),
    );
  }

  private scrollToFragment(fragment: string | null, afterRender = true): void {
    if (!fragment) return;
    const scroll = () => {
      const target = this.document.getElementById("help-" + fragment);
      if (!target) return;
      target.scrollIntoView({ block: "start" });
      target.focus({ preventScroll: true });
    };
    if (afterRender) afterNextRender(scroll, { injector: this.injector });
    else scroll();
  }
}
