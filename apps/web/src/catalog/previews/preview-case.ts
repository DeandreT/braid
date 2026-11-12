import type {
  CaseCapabilities,
  CaseDefinition,
} from "../regression/regression-api";
import { encodeBytes } from "../regression/regression-api";
import { csvMetadata, csvPreviewProfile } from "./csv-contract";
import type { PreviewRun, PreviewSubmission } from "./preview-types";

export interface PreviewCasePending {
  readonly id: string;
  readonly version: string;
  readonly body: CaseDefinition;
}
export interface PreviewCaseWork {
  readonly name: string;
  readonly pending: PreviewCasePending | null;
}

/** The baseline dialect comes only from the verified native run output, never input text. */
export function previewCaseDefinition(
  run: PreviewRun,
  submission: PreviewSubmission,
  capabilities: CaseCapabilities,
  bytes: Uint8Array,
  name: string,
): CaseDefinition {
  const runtime = capabilities.csvRuntime;
  const output = run.outputs[0];
  const provenance = run.provenance;
  if (
    !capabilities.publishCases ||
    !run.revision ||
    !!run.draft ||
    !runtime ||
    submission.profile !== csvPreviewProfile ||
    run.state !== "completed" ||
    run.outcome !== "succeeded" ||
    run.outputs.length !== 1 ||
    !output ||
    !output.csvDialect ||
    !csvMetadata(output.mediaType, output.csvDialect) ||
    output.byteCount !== bytes.length ||
    !provenance?.confirmed ||
    provenance.profile !== runtime.profile ||
    provenance.engineRevision !== runtime.engineRevision ||
    provenance.backendBuild !== runtime.backendBuild ||
    provenance.adapterBuild !== runtime.adapterBuild ||
    submission.inputs.length !== 1 ||
    submission.target.kind === "named" ||
    !name.trim() ||
    new TextEncoder().encode(name).length > 256
  )
    throw new Error(
      "The verified CSV preview cannot be saved with the current runtime and permissions.",
    );
  return {
    name,
    revisionId: run.revision.revisionId,
    runtime: { ...runtime },
    limits: { ...capabilities.limits },
    currentDateTime: run.currentDateTime,
    target: {
      kind: submission.target.kind,
      name: submission.target.name ?? null,
    },
    primaryBase64Url: submission.inputs[0]!.content.replaceAll("+", "-")
      .replaceAll("/", "_")
      .replace(/=+$/, ""),
    namedInputs: [],
    parameters: submission.parameters
      .map((parameter) => ({ ...parameter }))
      .sort((a, b) => (a.name === b.name ? 0 : a.name < b.name ? -1 : 1)),
    comparisonPolicy: "weft.regression.exact-bytes.v1",
    expected: [
      {
        name: null,
        bytesBase64Url: encodeBytes(bytes),
        mediaType: output.mediaType,
        csvDialect: { ...output.csvDialect },
      },
    ],
  };
}
