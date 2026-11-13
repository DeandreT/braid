import type { EditBinding, EditOperation } from "./edit-core";

export const operationNames = {
  bind: "Map field",
  retarget_binding: "Move mapping",
  remove_binding: "Remove mapping",
  connect: "Connect input",
  disconnect: "Disconnect input",
  edit_constant: "Edit constant",
  edit_call: "Edit function call",
  edit_value_map: "Edit value map",
  remove_nodes: "Remove nodes",
  add_scope: "Add child scope",
  remove_scope: "Remove scope",
  set_iteration: "Set or clear iteration",
  set_filter: "Set or clear filter",
  set_sort: "Set or clear sort keys",
  set_grouping: "Set or clear grouping",
  add_failure_rule: "Add failure rule",
  remove_failure_rule: "Remove failure rule",
  move_failure_rule: "Move failure rule",
  edit_failure_rule: "Edit failure rule",
} as const satisfies Record<EditOperation["op"], string>;
export const operationKinds = Object.keys(
  operationNames,
) as (keyof typeof operationNames)[];

export function operationTemplate(
  kind: keyof typeof operationNames,
  binding: EditBinding,
  newNode: string,
  node: string,
): EditOperation {
  const scope = binding.scope;
  const rule = {
    iteration: {
      kind: "source" as const,
      collection: { kind: "primary" as const, path: [] },
    },
    selection: { kind: "all" as const },
    message: null,
  };
  switch (kind) {
    case "bind":
      return {
        op: kind,
        binding,
        expression: {
          kind: "constant",
          id: newNode,
          value: { type: "string", text: "" },
        },
      };
    case "retarget_binding":
      return {
        op: kind,
        binding,
        to: { ...binding, field: "destination" },
        node,
      };
    case "remove_binding":
      return { op: kind, binding };
    case "connect":
      return {
        op: kind,
        node,
        input: "0",
        expression: { kind: "existing", node: newNode },
      };
    case "disconnect":
      return { op: kind, node, input: "0", replacement: null };
    case "edit_constant":
      return { op: kind, node, value: { type: "string", text: "" } };
    case "edit_call":
      return { op: kind, node, function: "concat", args: [] };
    case "edit_value_map":
      return {
        op: kind,
        node,
        value: { input_type: { kind: "string" }, table: [], default: null },
      };
    case "remove_nodes":
      return { op: kind, scope, nodes: [node], replacements: [] };
    case "add_scope":
      return { op: kind, parent: scope, field: "child" };
    case "remove_scope":
      return { op: kind, scope };
    case "set_iteration":
      return { op: kind, scope, source: null };
    case "set_filter":
      return { op: kind, scope, node: null };
    case "set_sort":
      return { op: kind, scope, keys: [] };
    case "set_grouping":
      return { op: kind, scope, grouping: { kind: "none" } };
    case "add_failure_rule":
      return { op: kind, rule };
    case "edit_failure_rule":
      return { op: kind, index: "0", rule };
    case "remove_failure_rule":
      return { op: kind, index: "0" };
    case "move_failure_rule":
      return { op: kind, from: "0", to: "1" };
  }
}
