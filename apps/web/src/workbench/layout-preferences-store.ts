import { DOCUMENT } from "@angular/common";
import { Injectable, computed, inject, signal } from "@angular/core";
import {
  readSavedLayout,
  writeSavedLayout,
  normalizeSavedLayout,
  type MappingTableLayout,
} from "./layout-preferences";
import type { WorkbenchLayout } from "./workbench-model";

/** Only harmless presentation data crosses page and identity lifetimes. */
@Injectable({ providedIn: "root" })
export class LayoutPreferencesStore {
  private readonly storage = (() => {
    try {
      return inject(DOCUMENT).defaultView?.localStorage ?? null;
    } catch {
      return null;
    }
  })();
  private readonly value = signal(readSavedLayout(this.storage));
  readonly frame = computed(() => this.value().frame);
  readonly table = computed(() => this.value().table);

  updateFrame(change: Partial<WorkbenchLayout>): void {
    this.save({ ...this.value(), frame: { ...this.frame(), ...change } });
  }
  updateTable(change: Partial<MappingTableLayout>): void {
    this.save({ ...this.value(), table: { ...this.table(), ...change } });
  }
  reset(): void {
    this.save(null);
  }
  private save(value: unknown): void {
    const next = normalizeSavedLayout(value);
    this.value.set(next);
    writeSavedLayout(this.storage, next);
  }
}
