/** Only local drafts and unsettled mutations need the full-page sign-in warning. */
export function hasUnsentPreview(work: {
  inputs: readonly { name: string; text: string; file: unknown | null }[];
  parameters: readonly unknown[];
  targetKind: string;
  targetName: string;
  documentProfile: boolean;
  csvProfile?: boolean;
  busy: string | null;
  pendingKey: boolean;
}): boolean {
  return (
    work.inputs.length > 1 ||
    work.inputs.some(
      (input) =>
        input.name.length > 0 || input.text.length > 0 || input.file !== null,
    ) ||
    work.parameters.length > 0 ||
    work.targetKind !== "all" ||
    work.targetName.length > 0 ||
    work.documentProfile ||
    work.csvProfile ||
    work.busy === "submitting" ||
    work.busy === "canceling" ||
    work.pendingKey
  );
}
