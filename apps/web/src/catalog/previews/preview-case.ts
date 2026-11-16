import type {
  CaseCapabilities,
  CaseDefinition,
} from "../regression/regression-api";
import { encodeBytes } from "../regression/regression-api";
import { csvMetadata, csvPreviewProfile } from "./csv-contract";
import type { PreviewRun, PreviewSubmission } from "./preview-types";
import { xmlMetadata, xmlPreviewProfile } from "./xml-contract";

export interface PreviewCasePending {
  readonly id: string;
  readonly version: string;
  readonly body: CaseDefinition;
}
export interface PreviewCaseWork {
  readonly name: string;
  readonly pending: PreviewCasePending | null;
}

const base64Url = (content: string): string =>
  content.replaceAll("+", "-").replaceAll("/", "_").replace(/=+$/, "");

/** Baseline metadata comes only from the verified native run outputs, never input text. */
export function previewCaseDefinition(
  run: PreviewRun,
  submission: PreviewSubmission,
  capabilities: CaseCapabilities,
  bytes: readonly Uint8Array[],
  name: string,
): CaseDefinition {
  const xml = submission.profile === xmlPreviewProfile;
  const runtime = xml ? capabilities.xmlRuntime : capabilities.csvRuntime;
  const provenance = run.provenance;
  if (
    !capabilities.publishCases ||
    !run.revision ||
    !!run.draft ||
    !runtime ||
    (!xml && submission.profile !== csvPreviewProfile) ||
    run.state !== "completed" ||
    run.outcome !== "succeeded" ||
    run.outputs.length < 1 ||
    run.outputs.length !== bytes.length ||
    (!xml &&
      (run.outputs.length !== 1 ||
        submission.inputs.length !== 1 ||
        submission.target.kind === "named")) ||
    run.outputs.some(
      (output, index) =>
        output.byteCount !== bytes[index]!.length ||
        (xml
          ? output.mediaType !== "application/xml" ||
            !xmlMetadata(output.xmlMetadata) ||
            (output.boundaryId !== "primary-output" &&
              output.targetNameTruncated)
          : !csvMetadata(output.mediaType, output.csvDialect)),
    ) ||
    !provenance?.confirmed ||
    provenance.profile !== runtime.profile ||
    provenance.engineRevision !== runtime.engineRevision ||
    provenance.backendBuild !== runtime.backendBuild ||
    provenance.adapterBuild !== runtime.adapterBuild ||
    !name.trim() ||
    new TextEncoder().encode(name).length > 256
  )
    throw new Error(
      `The verified ${xml ? "XML" : "CSV"} preview cannot be saved with the current runtime and permissions.`,
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
    primaryBase64Url: base64Url(submission.inputs[0]!.content),
    namedInputs: submission.inputs.slice(1).map((input) => ({
      name: input.name!,
      bytesBase64Url: base64Url(input.content),
    })),
    parameters: submission.parameters
      .map((parameter) => ({ ...parameter }))
      .sort((a, b) => (a.name === b.name ? 0 : a.name < b.name ? -1 : 1)),
    comparisonPolicy: "braid.regression.exact-bytes.v1",
    expected: run.outputs.map((output, index) => ({
      name: output.boundaryId === "primary-output" ? null : output.targetName,
      bytesBase64Url: encodeBytes(bytes[index]!),
      mediaType: output.mediaType,
      ...(xml
        ? { xmlMetadata: structuredClone(output.xmlMetadata!) }
        : { csvDialect: { ...output.csvDialect! } }),
    })),
  };
}
