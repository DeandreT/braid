export const reportSampleNames: readonly [
  "basic",
  "exact",
  "partial",
  "ambiguous",
  "invalid",
  "uncached",
  "pending",
  "conditions",
  "controls",
  "functions",
  "generated",
  "namedInputs",
];
export type ReportSample = (typeof reportSampleNames)[number];
export const reportSampleArtifacts: readonly (readonly [
  path: string,
  sha256: string,
  byteCount: number,
])[];
export const reportSampleFixtureIndices: Readonly<Record<ReportSample, number>>;
export function reportSampleFixtureIndex(sample: ReportSample): number;
