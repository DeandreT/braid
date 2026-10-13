import type { components } from "../api/generated";

export type ProjectionDocument = components["schemas"]["ProjectionDocumentV1"];
export type Field = components["schemas"]["ProjectionField"];
export type Rule = components["schemas"]["ProjectionRule"];
export type Control = components["schemas"]["ProjectionControl"];
export type Boundary = components["schemas"]["ProjectionBoundary"];
export type Parameter = components["schemas"]["ProjectionParameter"];
export type NodeReference = components["schemas"]["ProjectionNodeReference"];
export type Detail = components["schemas"]["DetailNode"];
export type Literal = components["schemas"]["LiteralValue"];
export type NativeLocator = components["schemas"]["NativeLocator"];
export type Dependency = components["schemas"]["RuleDependency"];
type PathSegment = components["schemas"]["PathSegment"];

function impossible(value: never): never {
  throw new Error("The mapping contains an unsupported explanation variant.");
}

export function pathText(path: readonly PathSegment[]): string {
  if (path.length === 0) return "(root)";
  return path
    .map((segment) => {
      switch (segment.kind) {
        case "field":
        case "attribute": {
          const namespace =
            segment.namespaceUri != null
              ? `{${JSON.stringify(segment.namespaceUri)}}`
              : "";
          return `${segment.kind === "attribute" ? "@" : ""}${namespace}[${JSON.stringify(segment.name)}]`;
        }
        case "index":
          return `[${segment.index}]`;
        case "anyItem":
          return "[*]";
        default:
          return impossible(segment);
      }
    })
    .join("");
}

export function literalText(literal: Literal): string {
  switch (literal.kind) {
    case "string":
      return JSON.stringify(literal.value);
    case "boolean":
      return literal.value ? "true" : "false";
    case "null":
      return {
        absent: "Absent value",
        explicitNull: "Explicit null",
        xmlNil: "XML nil",
      }[literal.nullKind];
    case "exactNumber": {
      if (literal.flavor !== "binaryFloat") return literal.value;
      const bytes = new DataView(new ArrayBuffer(8));
      bytes.setBigUint64(0, BigInt(`0x${literal.value}`));
      const number = bytes.getFloat64(0);
      return Object.is(number, -0) ? "-0" : String(number);
    }
    default:
      return impossible(literal);
  }
}

export function literalKind(literal: Literal): string {
  if (literal.kind === "exactNumber")
    return {
      integer: "Exact integer",
      decimal: "Exact decimal",
      binaryFloat: "Binary floating-point value",
    }[literal.flavor];
  return { string: "Text", boolean: "Boolean", null: "Missing or null value" }[
    literal.kind
  ];
}

export function nativeText(locator: NativeLocator): string {
  switch (locator.kind) {
    case "document":
      return `Document: ${locator.documentId}`;
    case "jsonPointer":
      return `Document: ${locator.documentId} · JSON location: ${locator.pointer || "(root)"}`;
    case "graphNode":
      return `Document: ${locator.documentId} · ${locator.functionId == null ? "Main mapping" : `Function ${locator.functionId}`} · Node ${locator.nodeId}`;
    case "textSpan":
      return `Document: ${locator.documentId} · Offset ${locator.startOffset} · Length ${locator.length} ${locator.unit ?? "units"}`;
    case "opaque":
      return `Document: ${locator.documentId} · ${locator.scheme}: ${locator.value}`;
    default:
      return impossible(locator);
  }
}

export function referenceKey(reference: NodeReference): string {
  switch (reference.kind) {
    case "boundary":
      return `boundary:${reference.boundaryId}`;
    case "field":
      return `field:${reference.fieldId}`;
    case "parameter":
      return `parameter:${reference.parameterId}`;
    case "rule":
      return `rule:${reference.ruleId}`;
    case "control":
      return `control:${reference.controlId}`;
    default:
      return impossible(reference);
  }
}

export function dependencyReference(
  dependency: Dependency,
): NodeReference | null {
  switch (dependency.kind) {
    case "field":
      return { kind: "field", fieldId: dependency.fieldId };
    case "parameter":
      return { kind: "parameter", parameterId: dependency.parameterId };
    case "node":
      return dependency.node;
    case "constant":
      return null;
    default:
      return impossible(dependency);
  }
}

export function dataTypeText(field: Field | Parameter): string {
  const type = field.dataType;
  const scalar = type.scalarTypes
    .map(
      (scalar) =>
        ({
          string: "text",
          integer: "integer",
          decimal: "decimal",
          binaryFloat: "binary number",
          boolean: "boolean",
        })[scalar],
    )
    .join(" or ");
  switch (type.kind) {
    case "unknown":
      return "Type unknown";
    case "object":
      return "Object";
    case "array":
      return scalar ? `Repeated ${scalar}` : "Repeated values";
    case "scalar":
    case "scalarUnion":
      return scalar || "Type unknown";
    default:
      return impossible(type.kind);
  }
}

export function cardinalityText(field: Field): string {
  const cardinality = field.cardinality;
  switch (cardinality.kind) {
    case "unknown":
      return "Repetition unknown";
    case "exactlyOne":
      return "Exactly one";
    case "zeroOrOne":
      return "Zero or one";
    case "zeroOrMore":
      return "Zero or more";
    case "oneOrMore":
      return "One or more";
    case "bounded":
      if (cardinality.minimum == null) return `At most ${cardinality.maximum}`;
      if (cardinality.maximum == null) return `At least ${cardinality.minimum}`;
      return `${cardinality.minimum} to ${cardinality.maximum}`;
    default:
      return impossible(cardinality.kind);
  }
}

export function boundaryFormat(format: string): string {
  return format === "unknown" || format === "" ? "Format unknown" : format;
}

export type MappingRow = {
  key: string;
  field: Field;
  rule: Rule | null;
  controls: Control[];
  incomplete: boolean;
  searchText: string;
};

export class ProjectionView {
  readonly fields: Map<string, Field>;
  readonly rules: Map<string, Rule>;
  readonly controls: Map<string, Control>;
  readonly boundaries: Map<string, Boundary>;
  readonly parameters: Map<string, Parameter>;
  readonly outputs: Boundary[];
  readonly inputs: Boundary[];
  readonly rows: MappingRow[];
  private readonly producingRules = new Map<string, Rule[]>();
  private readonly dependentEntities = new Map<string, (Rule | Control)[]>();
  private readonly controlsByReference = new Map<string, Set<string>>();
  private readonly controlOrder = new Map<string, number>();
  private readonly ancestorsByField = new Map<string, string[]>();

  constructor(readonly document: ProjectionDocument) {
    this.fields = new Map(document.fields.map((field) => [field.id, field]));
    this.rules = new Map(document.rules.map((rule) => [rule.id, rule]));
    this.controls = new Map(
      document.controls.map((control) => [control.id, control]),
    );
    this.boundaries = new Map(
      document.boundaries.map((boundary) => [boundary.id, boundary]),
    );
    this.parameters = new Map(
      document.parameters.map((parameter) => [parameter.id, parameter]),
    );
    this.outputs = document.boundaries.filter((boundary) =>
      ["primaryOutput", "namedOutput"].includes(boundary.role),
    );
    this.inputs = document.boundaries.filter((boundary) =>
      ["primaryInput", "namedInput"].includes(boundary.role),
    );
    for (const rule of document.rules) {
      const producers = this.producingRules.get(rule.outputFieldId) ?? [];
      producers.push(rule);
      this.producingRules.set(rule.outputFieldId, producers);
    }
    for (const entity of [...document.rules, ...document.controls]) {
      const keys = new Set(
        entity.dependencies.flatMap((dependency) => {
          const reference = dependencyReference(dependency);
          return reference ? [referenceKey(reference)] : [];
        }),
      );
      for (const key of keys) {
        const entities = this.dependentEntities.get(key) ?? [];
        entities.push(entity);
        this.dependentEntities.set(key, entities);
      }
    }
    document.controls.forEach((control, index) => {
      this.controlOrder.set(control.id, index);
      for (const reference of control.appliesTo)
        this.indexControl(referenceKey(reference), control.id);
    });
    for (const relationship of document.relationships) {
      if (
        relationship.kind === "governedBy" &&
        relationship.to.kind === "control"
      )
        this.indexControl(
          referenceKey(relationship.from),
          relationship.to.controlId,
        );
    }
    const fieldsByPath = new Map<string, Map<string, string[]>>();
    for (const field of document.fields) {
      const boundaryPaths =
        fieldsByPath.get(field.boundaryId) ?? new Map<string, string[]>();
      const key = pathKey(field.path);
      const ids = boundaryPaths.get(key) ?? [];
      ids.push(field.id);
      boundaryPaths.set(key, ids);
      fieldsByPath.set(field.boundaryId, boundaryPaths);
    }
    for (const field of document.fields) {
      const ancestors: string[] = [];
      for (let depth = 0; depth < field.path.length; depth++) {
        ancestors.push(
          ...(fieldsByPath
            .get(field.boundaryId)
            ?.get(pathKey(field.path.slice(0, depth))) ?? []),
        );
      }
      this.ancestorsByField.set(field.id, ancestors);
    }
    const outputIds = new Set(this.outputs.map((boundary) => boundary.id));
    const controlText = new Map(
      document.controls.map((control) => [
        control.id,
        `${control.kind} ${control.summary} ${control.details.map((detail) => this.detailText(detail)).join(" ")}`,
      ]),
    );
    this.rows = document.fields.flatMap((field) => {
      if (!outputIds.has(field.boundaryId)) return [];
      const rules = this.producers(field.id);
      if (
        rules.length === 0 &&
        (field.dataType.kind === "object" ||
          (field.dataType.kind === "array" &&
            field.dataType.scalarTypes.length === 0))
      )
        return [];
      return (rules.length ? rules : [null]).map((rule) => {
        const controls = this.controlsFor(field, rule);
        const incomplete =
          rule == null || document.coverage.status !== "complete";
        const text = [
          this.fieldLabel(field),
          rule?.summary ?? "",
          ...(rule?.dependencies ?? []).map((dependency) =>
            this.dependencyLabel(dependency),
          ),
          ...(rule?.details ?? []).map((detail) => this.detailText(detail)),
          ...controls.map((control) => controlText.get(control.id) ?? ""),
        ].join(" ");
        return {
          key: rule?.id ?? `field:${field.id}`,
          field,
          rule,
          controls,
          incomplete,
          searchText: text.toLocaleLowerCase(),
        };
      });
    });
  }

  fieldLabel(field: Field): string {
    return `${this.boundaries.get(field.boundaryId)?.name ?? "Unknown boundary"} ${pathText(field.path)}`;
  }

  label(reference: NodeReference): string {
    switch (reference.kind) {
      case "field": {
        const field = this.fields.get(reference.fieldId);
        return field ? this.fieldLabel(field) : "Field unavailable";
      }
      case "boundary":
        return (
          this.boundaries.get(reference.boundaryId)?.name ??
          "Boundary unavailable"
        );
      case "parameter":
        return `Parameter: ${this.parameters.get(reference.parameterId)?.name ?? "unavailable"}`;
      case "rule":
        return this.rules.get(reference.ruleId)?.summary ?? "Rule unavailable";
      case "control": {
        const control = this.controls.get(reference.controlId);
        return control
          ? `${control.kind}: ${control.summary}`
          : "Control unavailable";
      }
      default:
        return impossible(reference);
    }
  }

  dependencyLabel(dependency: Dependency): string {
    return dependency.kind === "constant"
      ? literalText(dependency.value)
      : this.label(dependencyReference(dependency)!);
  }

  detailText(detail: Detail): string {
    switch (detail.kind) {
      case "group":
        return `${detail.label} ${detail.children.map((child) => this.detailText(child)).join(" ")}`;
      case "expression":
        return `${detail.operation} ${detail.arguments.map((child) => this.detailText(child)).join(" ")}`;
      case "literal":
        return literalText(detail.value);
      case "reference":
        return this.label(detail.reference);
      case "unsupported":
        return `${detail.construct} ${detail.description}`;
      default:
        return impossible(detail);
    }
  }

  controlsFor(field: Field, rule: Rule | null): Control[] {
    const keys = new Set([`field:${field.id}`, `boundary:${field.boundaryId}`]);
    for (const ancestorId of this.ancestorsByField.get(field.id) ?? [])
      keys.add(`field:${ancestorId}`);
    if (rule) keys.add(`rule:${rule.id}`);
    const ids = new Set<string>();
    for (const key of keys)
      for (const id of this.controlsByReference.get(key) ?? []) ids.add(id);
    return [...ids]
      .sort(
        (left, right) =>
          this.controlOrder.get(left)! - this.controlOrder.get(right)!,
      )
      .map((id) => this.controls.get(id)!);
  }

  producers(fieldId: string): Rule[] {
    return this.producingRules.get(fieldId) ?? [];
  }

  uses(reference: NodeReference): (Rule | Control)[] {
    return this.dependentEntities.get(referenceKey(reference)) ?? [];
  }

  private indexControl(key: string, id: string): void {
    const controls = this.controlsByReference.get(key) ?? new Set<string>();
    controls.add(id);
    this.controlsByReference.set(key, controls);
  }
}

function pathKey(path: readonly PathSegment[]): string {
  return JSON.stringify(
    path.map((segment) => {
      switch (segment.kind) {
        case "field":
        case "attribute":
          return [segment.kind, segment.name, segment.namespaceUri ?? null];
        case "index":
          return [segment.kind, segment.index];
        case "anyItem":
          return [segment.kind];
        default:
          return impossible(segment);
      }
    }),
  );
}
