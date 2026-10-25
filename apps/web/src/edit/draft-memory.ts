import { Injectable } from "@angular/core";
import {
  catalogOwner,
  type CatalogContext,
  type CatalogMappingScope,
} from "../catalog/catalog-types";
import type { Draft } from "./draft-api";
import type { EditScript, EditOperation } from "./edit-core";

export interface RetainedDraft {
  draft: Draft;
  operations: EditScript;
  redo: EditOperation[];
  saveKey: string | null;
  saving: boolean;
  dirty: boolean;
}
/** Tab memory only. A different admitted owner cannot see or resume the previous owner's work. */
@Injectable({ providedIn: "root" })
export class DraftMemory {
  private owner: string | null = null;
  private readonly drafts = new Map<string, RetainedDraft>();
  admit(context: CatalogContext): void {
    const owner = catalogOwner(context);
    if (this.owner !== owner) this.drafts.clear();
    this.owner = owner;
  }
  read(id: string, mapping: CatalogMappingScope): RetainedDraft | undefined {
    return this.drafts.get(key(id, mapping));
  }
  pending(mapping: CatalogMappingScope): RetainedDraft | undefined {
    return [...this.drafts.values()].find(
      (value) =>
        key("", value.draft.mapping) === key("", mapping) && value.saving,
    );
  }
  keep(value: RetainedDraft, context: CatalogContext): void {
    if (
      this.owner !== catalogOwner(context) ||
      value.draft.authorPrincipalId !== context.actorPrincipalId
    )
      return;
    this.drafts.set(key(value.draft.id, value.draft.mapping), value);
  }
  forget(id: string, mapping: CatalogMappingScope): void {
    this.drafts.delete(key(id, mapping));
  }
}
function key(id: string, mapping: CatalogMappingScope): string {
  return JSON.stringify([
    mapping.organizationId,
    mapping.projectId,
    mapping.mappingId,
    id,
  ]);
}
