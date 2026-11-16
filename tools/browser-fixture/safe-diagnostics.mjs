import { StringDecoder } from "node:string_decoder";

// Only fixed categories may leave a child-output stream. No line, message or
// exception operand is returned, even when a child prints private data.
export function hostFailureSummary() {
  const decoder = new StringDecoder("utf8");
  const found = new Set();
  let prefix = "";
  function classify(line) {
    for (const [pattern, category] of [
      [/^\s+at Braid\.Worker\./, "workerHostFrame"],
      [/^\s+at Braid\.Jobs\./, "durableJobFrame"],
      [/^\s+at Braid\.Persistence\.Postgres\./, "databaseFrame"],
      [/^\s+at Braid\.Storage\./, "privateStorageFrame"],
      [/^\s+at Braid\.MappingBackends\.Workers\./, "workerRunnerFrame"],
      [/^\s+at Wolverine\./, "wolverineFrame"],
      [/^\s+at JasperFx\./, "jasperFxFrame"],
    ]) {
      if (pattern.test(line)) found.add(category);
    }
    if (/^Out of memory\.$/.test(line)) found.add("outOfMemory");
    if (/^Fatal error\./.test(line)) found.add("fatalRuntimeError");
    const type =
      /^Unhandled exception\. System\.(OutOfMemoryException|InvalidOperationException|OperationCanceledException|AggregateException)(?::|$)/.exec(
        line,
      )?.[1];
    if (type)
      found.add(
        {
          OutOfMemoryException: "outOfMemory",
          InvalidOperationException: "unhandledInvalidOperation",
          OperationCanceledException: "unhandledCancellation",
          AggregateException: "unhandledAggregate",
        }[type],
      );
  }
  return {
    write(bytes) {
      const text = decoder.write(bytes);
      let start = 0;
      while (start < text.length) {
        const end = text.indexOf("\n", start);
        const limit = end < 0 ? text.length : end;
        prefix += text.slice(
          start,
          Math.min(limit, start + 256 - prefix.length),
        );
        if (end < 0) break;
        classify(prefix.replace(/\r$/, ""));
        prefix = "";
        start = end + 1;
      }
    },
    snapshot() {
      classify(prefix);
      return [...found].sort();
    },
  };
}

const states = ["notDerived", "pending", "available", "failed"];
const reads = [
  "observed",
  "objectUnavailable",
  "storageIntegrity",
  "unsupportedFormat",
  "resourceLimit",
];
const failures = [
  "objectUnavailable",
  "storageIntegrity",
  "storageIo",
  "backendUnavailable",
  "backendFailure",
  "timedOut",
  "canceled",
  "isolationFailure",
  "protocolFailure",
  "profileUnavailable",
  "resourceLimit",
  "unsupportedArtifact",
];
const known = (values, value) =>
  value == null ? null : values.includes(value) ? value : "unknown";

export function reportSetupDiagnostic(operation, response) {
  return {
    kind: "report-setup-result",
    operation: known(["projection", "validation"], operation),
    httpStatus:
      Number.isInteger(response?.status) &&
      response.status >= 100 &&
      response.status <= 599
        ? response.status
        : null,
    state: known(states, response?.body?.state),
    readOutcome: known(reads, response?.body?.readOutcome),
    failure: known(failures, response?.body?.failure),
  };
}
