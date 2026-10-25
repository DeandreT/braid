import type { CatalogRevisionScope } from "../catalog/catalog-types";
import {
  decodeBytes,
  type CaseDetail,
  type RegressionRun,
} from "../catalog/regression/regression-api";
import { type Field, type MappingRow, ProjectionView } from "./projection-view";
import { ObservedJson, type ObservedValue } from "./observed-json";
import { workbenchScopeKey } from "./workbench-model";

export type SampleObservation =
  | ObservedValue
  | { kind: "unavailable"; reason: "noEvidence" | "boundaryFormat" };

/** Private evidence is owned in memory by one revision; this is never a layout preference. */
export class WorkbenchSample {
  private readonly inputs = new Map<string | null, Uint8Array>();
  private readonly outputs = new Map<string | null, Uint8Array>();
  private readonly observations = new WeakMap<
    ProjectionView,
    Map<Field, SampleObservation>
  >();
  private constructor(
    readonly scopeKey: string,
    readonly caseId: string,
    readonly version: string,
    readonly name: string,
    readonly runId: string | null,
  ) {}

  static capture(
    scope: CatalogRevisionScope,
    value: CaseDetail,
    run: RegressionRun | null,
    readSamples: boolean,
  ): WorkbenchSample | null {
    const key = workbenchScopeKey(scope);
    if (
      !readSamples ||
      workbenchScopeKey(value.revision) !== key ||
      value.definition.revisionId !== scope.revisionId
    )
      return null;
    if (
      run &&
      (workbenchScopeKey(run.run.revision) !== key ||
        run.run.caseId !== value.caseId ||
        run.run.version !== value.version)
    )
      return null;
    const sample = new WorkbenchSample(
      key,
      value.caseId,
      value.version,
      value.definition.name,
      run?.run.runId ?? null,
    );
    sample.inputs.set(null, decodeBytes(value.definition.primaryBase64Url));
    for (const input of value.definition.namedInputs)
      sample.inputs.set(input.name, decodeBytes(input.bytesBase64Url));
    if (
      run?.run.state === "completed" &&
      run.executionOutcome === "completed" &&
      run.previewStatus === "succeeded"
    )
      for (const output of run.outputs ?? [])
        sample.outputs.set(output.name, decodeBytes(output.bytesBase64Url));
    return sample;
  }

  output(row: MappingRow, view: ProjectionView): SampleObservation {
    return this.field(row.field, view);
  }

  sources(
    row: MappingRow,
    view: ProjectionView,
  ): readonly { name: string; value: SampleObservation }[] {
    const seen = new Set<string>();
    return (row.rule?.dependencies ?? []).flatMap((dependency) => {
      const id =
        dependency.kind === "field"
          ? dependency.fieldId
          : dependency.kind === "node" && dependency.node.kind === "field"
            ? dependency.node.fieldId
            : null;
      if (!id || seen.has(id)) return [];
      seen.add(id);
      const field = view.fields.get(id);
      const boundary = field && view.boundaries.get(field.boundaryId);
      if (
        !field ||
        !boundary ||
        !["primaryInput", "namedInput"].includes(boundary.role)
      )
        return [];
      return [
        {
          name: view.label({ kind: "field", fieldId: id }),
          value: this.field(field, view),
        },
      ];
    });
  }

  private field(field: Field, view: ProjectionView): SampleObservation {
    let cached = this.observations.get(view);
    if (!cached) {
      cached = new Map();
      this.observations.set(view, cached);
    }
    const previous = cached.get(field);
    if (previous) return previous;
    const value = this.observe(field, view);
    cached.set(field, value);
    return value;
  }

  private observe(field: Field, view: ProjectionView): SampleObservation {
    if (workbenchScopeKey(view.document.provenance.revision) !== this.scopeKey)
      return { kind: "unavailable", reason: "noEvidence" };
    const boundary = view.boundaries.get(field.boundaryId);
    if (!boundary || boundary.format !== "json")
      return { kind: "unavailable", reason: "boundaryFormat" };
    const input =
      boundary.role === "primaryInput" || boundary.role === "namedInput";
    const key =
      boundary.role === "primaryInput" || boundary.role === "primaryOutput"
        ? null
        : boundary.name;
    const bytes = (input ? this.inputs : this.outputs).get(key);
    return bytes
      ? ObservedJson.read(bytes, field.path)
      : { kind: "unavailable", reason: "noEvidence" };
  }
}
