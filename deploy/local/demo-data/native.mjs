// Small builders for self-authored Ferrule native mappings (the JSON a Ferrule project file holds).
// Expressions are functions of (graph, frame) that add their nodes and return the node ID, so a
// mapping reads like the formula it computes and node IDs never need to be written by hand.
import { createHash } from "node:crypto";

export const scalar = (name, ty = "string") => ({
  name,
  kind: { kind: "scalar", ty },
});
export const group = (name, children, repeating = false) => ({
  name,
  ...(repeating && { repeating: true }),
  kind: { kind: "group", children },
});
export const list = (name, children) => group(name, children, true);

// An EDI segment with positional string elements e1..eN.
export const segment = (tag, count, repeating = false) =>
  group(
    tag,
    Array.from({ length: count }, (_, index) => scalar(`e${index + 1}`)),
    repeating,
  );

// A field of the current repeating frame, or of the document root when outside one.
export const field =
  (...path) =>
  (graph, frame) =>
    graph.add({ kind: "source_field", path, ...(frame && { frame }) });
// A root field read from inside a repeating scope.
export const top =
  (...path) =>
  (graph) =>
    graph.add({ kind: "source_field", path });
export const constant = (value) => (graph) =>
  graph.add({ kind: "const", value });
export const call =
  (name, ...args) =>
  (graph, frame) =>
    graph.add({
      kind: "call",
      function: name,
      args: args.map((arg) => arg(graph, frame)),
    });
export const when = (condition, then, otherwise) => (graph, frame) =>
  graph.add({
    kind: "if",
    condition: condition(graph, frame),
    then: then(graph, frame),
    else: otherwise(graph, frame),
  });
export const parameter =
  (name, ty = "string") =>
  (graph) =>
    graph.add({ kind: "runtime_parameter", name, ty });
export const now = () => (graph) =>
  graph.add({ kind: "runtime_value", value: "current_date_time" });
export const position =
  (...collection) =>
  (graph) =>
    graph.add({ kind: "position", collection });
export const count =
  (...collection) =>
  (graph) =>
    graph.add({ kind: "aggregate", function: "count", collection, value: [] });
export const sum =
  (collection, ...value) =>
  (graph) =>
    graph.add({ kind: "aggregate", function: "sum", collection, value });

// Shorthands for the builtins the demo uses most.
export const concat = (...parts) =>
  call(
    "concat",
    ...parts.map((part) => (typeof part === "string" ? constant(part) : part)),
  );
export const upper = (value) => call("upper", value);
export const fmt = (value, pattern = "0.00") =>
  call("format_number", value, constant(pattern));

// scope(target, { from, filter, sortBy, sortThen: [[expression, descending]], set: { field: expression }, children })
export const scope = (target, options = {}) => ({ target, ...options });

function compileScope(graph, definition, frame) {
  const current = definition.from ?? frame;
  const compiled = {
    target_field: definition.target,
    bindings: Object.entries(definition.set ?? {}).map(
      ([target, expression]) => ({
        target_field: target,
        node: expression(graph, current),
      }),
    ),
    children: (definition.children ?? []).map((child) =>
      compileScope(graph, child, current),
    ),
  };
  if (definition.from) compiled.source = definition.from;
  if (definition.filter) compiled.filter = definition.filter(graph, current);
  if (definition.sortBy) compiled.sort_by = definition.sortBy(graph, current);
  if (definition.sortThen)
    compiled.sort_then_by = definition.sortThen.map(
      ([expression, descending]) => ({
        node: expression(graph, current),
        descending,
      }),
    );
  return compiled;
}

const json = { json_document: true };

// Builds one native mapping document. Boundaries default to explicit JSON documents.
export function mapping({
  source,
  target,
  root,
  sourceOptions = json,
  targetOptions = json,
  sourcePath,
  targetPath,
}) {
  const graph = {
    nodes: {},
    next: 1,
    add(node) {
      const id = this.next++;
      this.nodes[id] = node;
      return id;
    },
  };
  const compiled = compileScope(graph, { target: "", ...root }, undefined);
  return {
    source,
    target,
    ...(sourcePath && { source_path: sourcePath }),
    ...(targetPath && { target_path: targetPath }),
    ...(sourceOptions && { source_options: sourceOptions }),
    ...(targetOptions && { target_options: targetOptions }),
    graph: { nodes: graph.nodes },
    root: compiled,
  };
}

// A stable UUID derived from a seed namespace and a resource name, so a rerun sends the same
// idempotency keys and resource IDs and the server replays instead of creating duplicates.
export function stableId(namespace, name) {
  const hex = createHash("sha256")
    .update(`${namespace}\0${name}`)
    .digest("hex")
    .slice(0, 32)
    .split("");
  hex[12] = "5";
  hex[16] = "89ab"[parseInt(hex[16], 16) & 3];
  const text = hex.join("");
  return `${text.slice(0, 8)}-${text.slice(8, 12)}-${text.slice(12, 16)}-${text.slice(16, 20)}-${text.slice(20)}`;
}

// Canonical unpadded Base64url, as the regression case API requires.
export const base64Url = (bytes) => Buffer.from(bytes).toString("base64url");
