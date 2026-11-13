// Every v1 operation is exercised against the self-authored edit-v1 native fixture.
const scope = { target: { kind: "primary" }, children: [] };
const binding = { scope, field: "code" };
const expression = {
  kind: "constant",
  id: "7",
  value: { type: "string", text: "edited" },
};
const rule = {
  iteration: { kind: "source", collection: { kind: "primary", path: [] } },
  selection: { kind: "all" },
  message: null,
};
const addFailure = { op: "add_failure_rule", rule };
export const operationCases = [
  [{ op: "bind", binding, expression }],
  [
    {
      op: "retarget_binding",
      binding,
      to: { scope, field: "spare" },
      node: "1",
    },
  ],
  [{ op: "remove_binding", binding }],
  [{ op: "connect", node: "6", input: "0", expression }],
  [{ op: "disconnect", node: "6", input: "0", replacement: "7" }],
  [{ op: "edit_constant", node: "2", value: { type: "float", text: "-0.0" } }],
  [{ op: "edit_call", node: "3", function: "upper", args: ["1"] }],
  [
    {
      op: "edit_value_map",
      node: "4",
      value: {
        input_type: { kind: "int" },
        table: [
          {
            from: { type: "int", text: "9007199254740993" },
            to: { type: "string", text: "exact" },
          },
        ],
        default: { type: "string", text: "other" },
      },
    },
  ],
  [{ op: "remove_nodes", scope, nodes: ["6"], replacements: [] }],
  [{ op: "add_scope", parent: scope, field: "extraOutput" }],
  [{ op: "remove_scope", scope: { ...scope, children: ["0"] } }],
  [{ op: "set_iteration", scope, source: { kind: "primary", path: [] } }],
  [{ op: "set_filter", scope, node: "2" }],
  [{ op: "set_sort", scope, keys: [{ node: "2", descending: true }] }],
  [{ op: "set_grouping", scope, grouping: { kind: "by", node: "2" } }],
  [addFailure],
  [addFailure, { op: "remove_failure_rule", index: "0" }],
  [
    addFailure,
    {
      op: "add_failure_rule",
      rule: {
        ...rule,
        selection: {
          kind: "when_false",
          expression: { kind: "existing", node: "2" },
        },
      },
    },
    { op: "move_failure_rule", from: "0", to: "1" },
  ],
  [
    addFailure,
    {
      op: "edit_failure_rule",
      index: "0",
      rule: {
        ...rule,
        selection: {
          kind: "when_true",
          expression: { kind: "existing", node: "2" },
        },
      },
    },
  ],
  [
    { op: "set_iteration", scope, source: { kind: "primary", path: [] } },
    { op: "set_iteration", scope, source: null },
  ],
  [
    { op: "set_filter", scope, node: "2" },
    { op: "set_filter", scope, node: null },
  ],
  [
    { op: "set_sort", scope, keys: [{ node: "2", descending: true }] },
    { op: "set_sort", scope, keys: [] },
  ],
  [
    { op: "set_grouping", scope, grouping: { kind: "by", node: "2" } },
    { op: "set_grouping", scope, grouping: { kind: "none" } },
  ],
  ...["adjacent_by", "starting_with", "ending_with", "into_blocks"].map(
    (kind) => [{ op: "set_grouping", scope, grouping: { kind, node: "2" } }],
  ),
].map((operations) => ({ format: "ferrule.edit.v1", operations }));
