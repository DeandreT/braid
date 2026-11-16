import type { Reporter, FullResult } from "@playwright/test/reporter";

// Never render Playwright errors, attachments, action arguments, call logs or titles.
export default class CategoricalReporter implements Reporter {
  printsToStdio(): boolean {
    return true;
  }
  onEnd(result: FullResult): void {
    process.stdout.write(
      result.status === "passed"
        ? "BraidAuthBrowserPassed\n"
        : "BraidAuthBrowserFailed\n",
    );
  }
  onError(): void {
    /* The final result owns failure without exporting its operands. */
  }
}
