import {
  DestroyRef,
  Injectable,
  computed,
  inject,
  signal,
} from "@angular/core";

/**
 * Whether the current page holds work that only this tab knows about: an unsaved draft or an
 * unconfirmed request. Signing in again reloads the page; signing out ends its session. The
 * account menu warns only while some source reports such work.
 */
@Injectable({ providedIn: "root" })
export class UnsentWork {
  private readonly sources = signal<ReadonlyArray<() => boolean>>([]);
  readonly present = computed(() => this.sources().some((source) => source()));

  /** Reports `source` until the calling component or service is destroyed. */
  track(source: () => boolean, destroyRef = inject(DestroyRef)): void {
    this.sources.update((list) => [...list, source]);
    destroyRef.onDestroy(() =>
      this.sources.update((list) => list.filter((item) => item !== source)),
    );
  }
}
