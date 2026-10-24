/** Layout whitespace is not part of the session status; its words remain exact. */
export function sessionSummaryMatches(
  actual: string | null,
  expected: string,
): boolean {
  return actual?.trim() === expected;
}
