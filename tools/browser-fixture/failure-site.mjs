// Only a known module name and line number may leave an operator stack trace.
// Messages, paths, arguments and provider text are deliberately discarded.
export function failureSite(error) {
  if (typeof error?.stack !== "string") return null;
  for (const frame of error.stack.split("\n").slice(1, 40)) {
    if (
      /^\s*at (?:new )?(?:Failure|check|run|docker|jsonDocker|tool)\b/.test(
        frame,
      )
    )
      continue;
    const match =
      /[/\\](runtime|operator)\.mjs:([1-9][0-9]{0,6}):[0-9]+\)?$/.exec(frame);
    if (match) return { module: match[1], line: Number(match[2]) };
  }
  return null;
}
