export type IntakeFormat = {
  format: string;
  maximumSourceBytes: number;
  extractorVersion: number;
  evidenceFormat: string;
};
export type IntakeCapabilities = {
  version: 1;
  maximumTextRequestBytes: number;
  formats: IntakeFormat[];
};
export const formatLabels: Record<string, string> = {
  text: "Plain text",
  json: "JSON",
  "openapi-json": "OpenAPI JSON (structural extraction)",
  docx: "Word DOCX (partial text extraction)",
  xlsx: "Excel XLSX (stored cells only)",
  pdf: "PDF (page text; OCR unavailable)",
};
export function capabilities(value: unknown): IntakeCapabilities {
  const v = value as IntakeCapabilities;
  if (
    !v ||
    v.version !== 1 ||
    v.maximumTextRequestBytes !== 1048576 ||
    !Array.isArray(v.formats) ||
    v.formats.length > 6
  )
    throw new Error("Invalid intake capabilities");
  const seen = new Set<string>();
  for (const f of v.formats) {
    const version =
      f?.format === "pdf" ? 4 : ["docx", "xlsx"].includes(f?.format) ? 3 : 2;
    if (
      !f ||
      !Object.hasOwn(formatLabels, f.format) ||
      seen.has(f.format) ||
      !Number.isSafeInteger(f.maximumSourceBytes) ||
      f.maximumSourceBytes < 1 ||
      f.maximumSourceBytes > (f.format === "pdf" ? 8388608 : 10485760) ||
      f.extractorVersion !== version ||
      f.evidenceFormat !== `braid.spec-evidence.v${version === 2 ? 1 : 2}`
    )
      throw new Error("Invalid intake format");
    seen.add(f.format);
  }
  return v;
}
