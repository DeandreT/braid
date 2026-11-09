export function captureProvenance(root: string): Promise<{
  sourceCommit: string;
  sourceChanges: string[];
  sourceSha256: string;
  captureSpecSha256: string;
}>;
