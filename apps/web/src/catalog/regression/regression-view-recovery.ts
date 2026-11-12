import { Injectable } from "@angular/core";
import { RetainedViewWork } from "../retained-view-work";
import type { CaseDefinition } from "./regression-api";
import type { CsvDocumentDialect } from "../previews/csv-contract";
import type { PreviewCaseWork } from "../previews/preview-case";
import type { CatalogRevisionScope } from "../catalog-types";

export interface DocumentDraft {
  id: number;
  name: string;
  text: string;
  mediaType?: string;
  csvDialect?: CsvDocumentDialect;
  file: { name: string; encoded: string; length: number } | null;
}
export interface ParameterDraft {
  id: number;
  name: string;
  kind: string;
  value: string;
}
export interface PendingCase {
  id: string;
  version: string;
  body: CaseDefinition;
}
export interface PendingRun {
  id: string;
  caseId: string;
  version: string;
}
export interface RegressionDraft {
  readonly editId: { id: string; version: string } | null;
  readonly name: string;
  readonly clock: string;
  readonly target: string;
  readonly targetName: string;
  readonly documentMode?: boolean;
  readonly csvMode?: boolean;
  readonly comparisonPolicy?: string;
  readonly compareSeparators?: boolean;
  readonly ignoreControlNumbers?: boolean;
  readonly ignoreDates?: boolean;
  readonly inputs: readonly DocumentDraft[];
  readonly outputs: readonly DocumentDraft[];
  readonly parameters: readonly ParameterDraft[];
}
export interface RegressionWork {
  readonly draft: RegressionDraft | null;
  readonly pendingCase: PendingCase | null;
  readonly pendingRun: PendingRun | null;
}

/** Provided by the revision page: a refresh or failed session check can replace the regression panel without losing its editor or uncertain requests. */
@Injectable()
export class RegressionViewRecovery extends RetainedViewWork<RegressionWork> {
  readonly previewCase = new PreviewCaseRecovery();

  override routeScope(scope: CatalogRevisionScope | null): void {
    super.routeScope(scope);
    this.previewCase.routeScope(scope);
  }
}

class PreviewCaseRecovery extends RetainedViewWork<PreviewCaseWork> {}
