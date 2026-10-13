import type { components, paths } from "./generated";

// These examples are compiled separately and are not part of the application.
type PathSegment = components["schemas"]["PathSegment"];
type LiteralValue = components["schemas"]["LiteralValue"];
type NativeLocator = components["schemas"]["NativeLocator"];
type RuleDependency = components["schemas"]["RuleDependency"];
type ProjectionNodeReference = components["schemas"]["ProjectionNodeReference"];
type DetailNode = components["schemas"]["DetailNode"];
type ProjectionDocument = components["schemas"]["ProjectionDocumentV1"];
type ScopedRevisionId = components["schemas"]["ScopedRevisionId"];

function assertNever(value: never): never {
  return value;
}

export function describePath(segment: PathSegment): string {
  switch (segment.kind) {
    case "field":
      return segment.name;
    case "attribute":
      return segment.name;
    case "index":
      return segment.index;
    case "anyItem":
      return "*";
    default:
      return assertNever(segment);
  }
}

export function describeLiteral(literal: LiteralValue): string {
  switch (literal.kind) {
    case "string":
      return literal.value;
    case "boolean": {
      const value: boolean = literal.value;
      return value ? "true" : "false";
    }
    case "exactNumber":
      return literal.value;
    case "null":
      return literal.nullKind;
    default:
      return assertNever(literal);
  }
}

export function describeLocator(locator: NativeLocator): string {
  const documentId: string = locator.documentId;
  switch (locator.kind) {
    case "document":
      return documentId;
    case "jsonPointer":
      return locator.pointer;
    case "graphNode": {
      const functionId: string | null | undefined = locator.functionId;
      return functionId ?? locator.nodeId;
    }
    case "textSpan": {
      const length: string = locator.length;
      return length + locator.startOffset;
    }
    case "opaque": {
      const scheme: string = locator.scheme;
      return scheme + locator.value;
    }
    default:
      return assertNever(locator);
  }
}

export function describeReference(reference: ProjectionNodeReference): string {
  switch (reference.kind) {
    case "boundary":
      return reference.boundaryId;
    case "field":
      return reference.fieldId;
    case "parameter":
      return reference.parameterId;
    case "rule":
      return reference.ruleId;
    case "control":
      return reference.controlId;
    default:
      return assertNever(reference);
  }
}

export function describeDependency(dependency: RuleDependency): string {
  switch (dependency.kind) {
    case "field":
      return dependency.fieldId;
    case "parameter":
      return dependency.parameterId;
    case "constant":
      return describeLiteral(dependency.value);
    case "node":
      return describeReference(dependency.node);
    default:
      return assertNever(dependency);
  }
}

export function describeDetail(detail: DetailNode): string {
  switch (detail.kind) {
    case "group":
      return detail.label + detail.children.map(describeDetail).join(",");
    case "expression":
      return detail.operation + detail.arguments.map(describeDetail).join(",");
    case "literal":
      return describeLiteral(detail.value);
    case "reference":
      return describeReference(detail.reference);
    case "unsupported":
      return detail.description + describeLocator(detail.nativeLocator);
    default:
      return assertNever(detail);
  }
}

const largeNativeId = "18446744073709551615";
export const nativeLocator = {
  kind: "graphNode",
  documentId: "project",
  nodeId: largeNativeId,
  functionId: largeNativeId,
} satisfies NativeLocator;

export const indexedPath = {
  kind: "index",
  index: "9007199254740993",
} satisfies PathSegment;

export const exactInteger = {
  kind: "exactNumber",
  flavor: "integer",
  value: "900719925474099312345678901234567890",
} satisfies LiteralValue;

export const exactDecimal = {
  kind: "exactNumber",
  flavor: "decimal",
  value: "0.3000000000000000000000000000000000000001",
} satisfies LiteralValue;

export const negativeZeroBits = {
  kind: "exactNumber",
  flavor: "binaryFloat",
  value: "8000000000000000",
} satisfies LiteralValue;

export const scopedRevision = {
  organizationId: "organization",
  projectId: "project",
  mappingId: largeNativeId,
  revisionId: largeNativeId,
} satisfies ScopedRevisionId;

// @ts-expect-error Native node IDs must remain exact strings.
export const numericNodeId: Extract<
  NativeLocator,
  { kind: "graphNode" }
>["nodeId"] = 9007199254740993;
// @ts-expect-error Function identities must also remain exact strings.
export const numericFunctionId: Extract<
  NativeLocator,
  { kind: "graphNode" }
>["functionId"] = 9007199254740993;
// @ts-expect-error Index segments must preserve their exact lexical value.
export const numericIndex: Extract<PathSegment, { kind: "index" }>["index"] =
  9007199254740993;
// @ts-expect-error Exact numeric literals are text, not JavaScript numbers.
export const numericLiteral: Extract<
  LiteralValue,
  { kind: "exactNumber" }
>["value"] = 9007199254740993;
// @ts-expect-error Native span offsets must remain exact strings.
export const numericOffset: Extract<
  NativeLocator,
  { kind: "textSpan" }
>["startOffset"] = 9007199254740993;
// @ts-expect-error Public revision identifiers are strings.
export const numericRevision: ScopedRevisionId["revisionId"] = 9007199254740993;
// @ts-expect-error Projection responses declare the exact v1 schema version.
export const wrongSchemaVersion: ProjectionDocument["schemaVersion"] =
  "weft.mapping-projection.v2";

type FixtureGet = paths["/api/v1/fixtures/projections/{fixtureId}"]["get"];
type FixtureResponses = FixtureGet["responses"];
type FixtureResult = {
  [Status in keyof FixtureResponses]: {
    status: Status;
    response: FixtureResponses[Status];
  };
}[keyof FixtureResponses];

export const fixtureParameters = {
  fixtureId: "partial",
} satisfies FixtureGet["parameters"]["path"];

export function successfulFixture(document: ProjectionDocument): FixtureResult {
  return {
    status: 200,
    response: {
      headers: {},
      content: { "application/json": document },
    },
  };
}

export const missingFixture: FixtureResult = {
  status: 404,
  response: { headers: {} },
};

type NotFoundContent = FixtureResponses[404]["content"];
const jsonBody = { "application/json": exactInteger };
// @ts-expect-error The typed 404 response has no JSON body.
export const missingFixtureJson: NotFoundContent = jsonBody;

export function fixtureDocument(
  result: FixtureResult,
): ProjectionDocument | undefined {
  switch (result.status) {
    case 200:
      return result.response.content["application/json"];
    case 404:
      return undefined;
    default:
      return assertNever(result);
  }
}

type NativeOwner = components["schemas"]["NativeDiagnosticOwnerV1"];
type NativeEndpoint = components["schemas"]["NativeEndpointV1"];
type SchemaOwnerStep = components["schemas"]["SchemaNativeOwnerStepV1"];
type ScopeOwnerStep = components["schemas"]["ScopeNativeOwnerStepV1"];
type ValidationReport = components["schemas"]["ValidationReportV1"];
type NativeExampleGet = paths["/api/v1/examples/mappings/{exampleId}"]["get"];
type NativeExampleResult = components["schemas"]["NativeExampleResultV1"];

export function nativeOwnerId(owner: NativeOwner): string {
  switch (owner.kind) {
    case "graphNode":
      return owner.functionId ?? owner.nodeId;
    case "userFunction":
      return owner.functionId;
    case "endpoint":
      return nativeEndpointId(owner.endpoint);
    case "schemaNode":
      return (
        nativeEndpointId(owner.endpoint) +
        owner.path.map(schemaStepId).join("/")
      );
    case "scope":
      return (
        nativeEndpointId(owner.target) + owner.path.map(scopeStepId).join("/")
      );
    case "failureRule":
      return owner.index;
    default:
      return assertNever(owner);
  }
}

function nativeEndpointId(endpoint: NativeEndpoint): string {
  switch (endpoint.kind) {
    case "source":
    case "target":
      return endpoint.kind;
    case "namedSource":
    case "namedTarget":
      return endpoint.index;
    default:
      return assertNever(endpoint);
  }
}

function schemaStepId(step: SchemaOwnerStep): string {
  switch (step.kind) {
    case "child":
    case "contains":
    case "dependentSchema":
      return step.index;
    case "dynamicField":
      return step.kind;
    default:
      return assertNever(step);
  }
}

function scopeStepId(step: ScopeOwnerStep): string {
  switch (step.kind) {
    case "child":
    case "dynamicChild":
    case "segment":
      return step.index;
    default:
      return assertNever(step);
  }
}

export const nativeFunctionOwner = {
  kind: "graphNode",
  nodeId: "7",
  functionId: "18446744073709551615",
} satisfies NativeOwner;

// @ts-expect-error Native function owner identities must remain exact strings.
export const numericNativeOwnerFunction: Extract<
  NativeOwner,
  { kind: "graphNode" }
>["functionId"] = 18446744073709551615;
// @ts-expect-error Native schema indices must remain exact strings.
export const numericSchemaIndex: Extract<
  SchemaOwnerStep,
  { kind: "child" }
>["index"] = 9007199254740993;
// @ts-expect-error Native scope indices must remain exact strings.
export const numericScopeIndex: Extract<
  ScopeOwnerStep,
  { kind: "segment" }
>["index"] = 9007199254740993;
// @ts-expect-error No validator result must be represented by null, never a string status.
export const invalidValidity: ValidationReport["isValid"] = "unavailable";

export const nativeExampleParameters = {
  exampleId: "basic",
} satisfies NativeExampleGet["parameters"]["path"];

export function nativeExampleResponse(
  document: NativeExampleResult,
): NativeExampleGet["responses"][200]["content"] {
  return { "application/json": document };
}

// @ts-expect-error Native example 404 responses do not contain a projection/report body.
export const nativeExampleMissingBody: NativeExampleGet["responses"][404]["content"] =
  { "application/json": nativeFunctionOwner };
