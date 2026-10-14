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

type CatalogProject = components["schemas"]["CatalogProjectV1"];
type CatalogMapping = components["schemas"]["CatalogMappingV1"];
type CatalogRevision = components["schemas"]["CatalogRevisionV1"];
type CatalogMetadata = components["schemas"]["CatalogMappingMetadataV1"];
type CatalogRevisionGet =
  paths["/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/revisions/{revisionId}"]["get"];

export const catalogVersion: CatalogProject["version"] = "9007199254740993";
export const catalogHeadSequence: CatalogMapping["head"]["sequence"] =
  "9223372036854775807";
export const catalogRevisionSequence: CatalogRevision["sequence"] =
  "9007199254740993";

// @ts-expect-error Catalog versions retain precision as decimal strings.
export const numericCatalogVersion: CatalogProject["version"] = 9007199254740993;
// @ts-expect-error Revision sequences retain precision as decimal strings.
export const numericCatalogSequence: CatalogRevision["sequence"] = 9007199254740993;
// @ts-expect-error Metadata-only responses do not contain the changing revision head.
export const metadataHead: CatalogMetadata["head"] = null;
// @ts-expect-error Catalog errors contain a closed code, without raw provider text.
export const catalogProviderError: components["schemas"]["CatalogErrorV1"]["message"] =
  "provider detail";

export function describeCatalogAdmission(
  admission: CatalogRevision["admission"],
): string {
  switch (admission) {
    case "valid":
      return "Admitted valid revision";
    case "decodedInvalid":
      return "Admitted revision with validation findings";
    default:
      return assertNever(admission);
  }
}

export const catalogRevisionParameters = {
  organizationId: "11111111-1111-1111-1111-111111111111",
  projectId: "00000001-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
  mappingId: "33333333-3333-3333-3333-333333333333",
  revisionId: "44444444-4444-4444-4444-444444444444",
} satisfies CatalogRevisionGet["parameters"]["path"];

export function catalogRevisionResponse(
  revision: CatalogRevision,
): CatalogRevisionGet["responses"][200]["content"] {
  return { "application/json": revision };
}

type MetadataEdit = components["schemas"]["CatalogMappingMetadataEditV1"];
type MetadataRoute =
  paths["/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/metadata"];
type MetadataPatch = MetadataRoute["patch"];
type MetadataGet = MetadataRoute["get"];
type MetadataPatchBody = NonNullable<MetadataPatch["requestBody"]>["content"];

export const metadataEdit = {
  name: "A readable mapping name",
  description: "Preserve this description exactly.",
} satisfies MetadataEdit;

export const metadataPatchDocument = {
  "application/vnd.weft.mapping-metadata-edit.v1+json": metadataEdit,
} satisfies MetadataPatchBody;

// @ts-expect-error Metadata edits require a description, including an explicitly empty one.
export const incompleteMetadataEdit: MetadataEdit = { name: "Mapping" };
// @ts-expect-error The metadata edit document cannot change the revision head.
export const metadataEditHead: MetadataEdit["head"] = null;
export const genericMetadataPatch: MetadataPatchBody = {
  // @ts-expect-error The metadata PATCH document has its own versioned media type.
  "application/json": metadataEdit,
};
// @ts-expect-error Conditional GET 304 responses have no representation body.
export const metadataNotModifiedBody: MetadataGet["responses"][304]["content"] =
  {
    "application/json": metadataEdit,
  };

export function metadataUpdatedResponse(
  metadata: CatalogMetadata,
): MetadataPatch["responses"][200]["content"] {
  return { "application/json": metadata };
}

export const metadataPreconditionError: components["schemas"]["CatalogErrorV1"]["code"] =
  "preconditionFailed";

type RevisionIngestionReceipt =
  components["schemas"]["CatalogRevisionIngestionReceiptV1"];
type RevisionIngestionResult =
  components["schemas"]["CatalogRevisionIngestionResultV1"];
type MappingCreate =
  paths["/api/v1/organizations/{organizationId}/projects/{projectId}/mappings"]["post"];
type RevisionAppend =
  paths["/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/revisions"]["post"];
type OwnReceiptGet =
  paths["/api/v1/organizations/{organizationId}/projects/{projectId}/revision-ingestions/{operationId}"]["get"];
type OwnReceiptResume =
  paths["/api/v1/organizations/{organizationId}/projects/{projectId}/revision-ingestions/{operationId}/resume"]["post"];
type MappingCreateContent = NonNullable<
  MappingCreate["requestBody"]
>["content"];
type RevisionAppendContent = NonNullable<
  RevisionAppend["requestBody"]
>["content"];

export function rawMappingCreateBody(
  native: MappingCreateContent["application/octet-stream"],
): MappingCreateContent {
  return { "application/octet-stream": native };
}

export function rawRevisionAppendBody(
  native: RevisionAppendContent["application/octet-stream"],
): RevisionAppendContent {
  return { "application/octet-stream": native };
}

export const jsonMappingCreateBody: MappingCreateContent = {
  // @ts-expect-error Native uploads use exact binary content rather than a JSON envelope.
  "application/json": { native: "{}" },
};

export const ingestionCommittedSequence: RevisionIngestionReceipt["committedSequence"] =
  "9223372036854775807";
// @ts-expect-error Committed ingestion sequences retain bigint precision as text.
export const numericIngestionSequence: RevisionIngestionReceipt["committedSequence"] = 9007199254740993;
// @ts-expect-error Safe receipt polling does not expose idempotency keys.
export const receiptIdempotencyKey: RevisionIngestionReceipt["idempotencyKey"] =
  "key";
// @ts-expect-error Safe receipt polling does not expose private object references.
export const receiptPrivateReference: RevisionIngestionReceipt["nativeReference"] =
  null;
// @ts-expect-error Rejection reasons belong to actual mutation results, not inferred polling outcomes.
export const receiptRejection: RevisionIngestionReceipt["rejection"] =
  "nativeDecode";
// @ts-expect-error An ingestion failure is a closed category rather than provider exception text.
export const arbitraryIngestionFailure: RevisionIngestionResult["failure"] =
  "provider detail";
// @ts-expect-error Future receipt phases are not automatically accepted as supported categories.
export const unknownIngestionPhase: RevisionIngestionReceipt["phase"] =
  "running";

export function ownReceiptResponse(
  receipt: RevisionIngestionReceipt,
): OwnReceiptGet["responses"][200]["content"] {
  return { "application/json": receipt };
}

export function committedMappingCreateResponse(
  result: RevisionIngestionResult,
): MappingCreate["responses"][200]["content"] {
  return { "application/json": result };
}

export function inProgressRevisionAppendResponse(
  result: RevisionIngestionResult,
): RevisionAppend["responses"][202]["content"] {
  return { "application/json": result };
}

export function explicitResumeConflictResponse(
  result: RevisionIngestionResult,
): OwnReceiptResume["responses"][409]["content"] {
  return { "application/json": result };
}

export const ingestionAdmissionBusy: components["schemas"]["CatalogErrorV1"]["code"] =
  "ingestionAdmissionBusy";
