/**
 * Generated from contracts/weft-v1.json. Do not edit manually.
 * Refresh with: npm run contracts:generate
 */
export interface paths {
  "/api/v1/fixtures/projections/{fixtureId}": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read a deterministic synthetic mapping projection (development only). */
    get: operations["GetProjectionFixture"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/health": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    get: operations["GetHealth"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
}
export type webhooks = Record<string, never>;
export interface components {
  schemas: {
    ArtifactDigest: {
      algorithm: string;
      value: string;
    };
    BackendIdentity: {
      buildId: string;
      id: string;
      revision: string;
    };
    /** @enum {unknown} */
    BoundaryRole:
      "primaryInput" | "namedInput" | "primaryOutput" | "namedOutput";
    /** @enum {unknown} */
    BoundaryShape: "unknown" | "scalar" | "object" | "rows" | "documentSet";
    CapabilityCondition: {
      code: string;
      description: string;
    };
    /** @enum {unknown} */
    CapabilityStatus:
      "supported" | "unsupported" | "conditional" | "notQualified";
    /** @enum {unknown} */
    CardinalityKind:
      | "unknown"
      | "exactlyOne"
      | "zeroOrOne"
      | "zeroOrMore"
      | "oneOrMore"
      | "bounded";
    /** @enum {unknown} */
    ControlKind:
      | "unknown"
      | "iteration"
      | "filter"
      | "join"
      | "grouping"
      | "sorting"
      | "window"
      | "failure"
      | "ordering"
      | "dynamicBoundary"
      | "construction";
    CoverageGap: {
      code: string;
      message: string;
      nativeLocator: components["schemas"]["NativeLocator"];
      projectionLocator?:
        null | components["schemas"]["ProjectionNodeReference"];
    };
    /** @enum {unknown} */
    CoverageStatus: "complete" | "partial" | "unavailable";
    /**
     * @default value
     * @enum {unknown}
     */
    DependencyRole: "value" | "condition" | "default";
    DetailNode:
      | components["schemas"]["DetailNodeGroupDetail"]
      | components["schemas"]["DetailNodeExpressionDetail"]
      | components["schemas"]["DetailNodeLiteralDetail"]
      | components["schemas"]["DetailNodeReferenceDetail"]
      | components["schemas"]["DetailNodeUnsupportedDetail"];
    DetailNodeExpressionDetail: {
      arguments: components["schemas"]["DetailNode"][];
      /** @enum {string} */
      kind: "expression";
      operation: string;
    };
    DetailNodeGroupDetail: {
      children: components["schemas"]["DetailNode"][];
      /** @enum {string} */
      kind: "group";
      label: string;
    };
    DetailNodeLiteralDetail: {
      /** @enum {string} */
      kind: "literal";
      value: components["schemas"]["LiteralValue"];
    };
    DetailNodeReferenceDetail: {
      /** @enum {string} */
      kind: "reference";
      reference: components["schemas"]["ProjectionNodeReference"];
    };
    DetailNodeUnsupportedDetail: {
      construct: string;
      description: string;
      /** @enum {string} */
      kind: "unsupported";
      nativeLocator: components["schemas"]["NativeLocator"];
    };
    /** @enum {unknown} */
    DiagnosticSeverity: "information" | "warning" | "error";
    /** @enum {unknown} */
    ExactNumberFlavor: "integer" | "decimal" | "binaryFloat";
    FieldCardinality: {
      kind: components["schemas"]["CardinalityKind"];
      maximum?: null | string;
      minimum?: null | string;
    };
    FieldDataType: {
      kind: components["schemas"]["FieldDataTypeKind"];
      nativeName?: null | string;
      scalarTypes: components["schemas"]["ScalarDataType"][];
    };
    /** @enum {unknown} */
    FieldDataTypeKind:
      "unknown" | "scalar" | "scalarUnion" | "object" | "array";
    /** @enum {unknown} */
    FieldNullability: "unknown" | "nonNullable" | "nullable";
    /** @enum {unknown} */
    FieldRequirement: "unknown" | "optional" | "required";
    HealthResponseV1: {
      apiVersion: string;
      service: string;
      status: string;
      version: string;
    };
    LiteralValue:
      | components["schemas"]["LiteralValueStringLiteralValue"]
      | components["schemas"]["LiteralValueBooleanLiteralValue"]
      | components["schemas"]["LiteralValueExactNumberLiteralValue"]
      | components["schemas"]["LiteralValueNullLiteralValue"];
    LiteralValueBooleanLiteralValue: {
      /** @enum {string} */
      kind: "boolean";
      value: boolean;
    };
    LiteralValueExactNumberLiteralValue: {
      flavor: components["schemas"]["ExactNumberFlavor"];
      /** @enum {string} */
      kind: "exactNumber";
      value: string;
    };
    LiteralValueNullLiteralValue: {
      /** @enum {string} */
      kind: "null";
      nullKind: components["schemas"]["NullLiteralKind"];
    };
    LiteralValueStringLiteralValue: {
      /** @enum {string} */
      kind: "string";
      value: string;
    };
    /** @enum {unknown} */
    MappingOperation: "inspect" | "project" | "validate" | "preview" | "build";
    NativeLocator:
      | components["schemas"]["NativeLocatorDocumentNativeLocator"]
      | components["schemas"]["NativeLocatorJsonPointerNativeLocator"]
      | components["schemas"]["NativeLocatorGraphNodeNativeLocator"]
      | components["schemas"]["NativeLocatorTextSpanNativeLocator"]
      | components["schemas"]["NativeLocatorOpaqueNativeLocator"];
    NativeLocatorDocumentNativeLocator: {
      documentId: string;
      /** @enum {string} */
      kind: "document";
    };
    NativeLocatorGraphNodeNativeLocator: {
      documentId: string;
      functionId?: null | string;
      /** @enum {string} */
      kind: "graphNode";
      nodeId: string;
    };
    NativeLocatorJsonPointerNativeLocator: {
      documentId: string;
      /** @enum {string} */
      kind: "jsonPointer";
      pointer: string;
    };
    NativeLocatorOpaqueNativeLocator: {
      documentId: string;
      /** @enum {string} */
      kind: "opaque";
      scheme: string;
      value: string;
    };
    NativeLocatorTextSpanNativeLocator: {
      documentId: string;
      /** @enum {string} */
      kind: "textSpan";
      length: string;
      startOffset: string;
      unit?: components["schemas"]["TextSpanUnit"];
    };
    /** @enum {unknown} */
    NullLiteralKind: "absent" | "explicitNull" | "xmlNil";
    OperationCapability: {
      boundaryIds: string[];
      conditions: components["schemas"]["CapabilityCondition"][];
      operation: components["schemas"]["MappingOperation"];
      profile: string;
      status: components["schemas"]["CapabilityStatus"];
    };
    PathSegment:
      | components["schemas"]["PathSegmentFieldPathSegment"]
      | components["schemas"]["PathSegmentAttributePathSegment"]
      | components["schemas"]["PathSegmentIndexPathSegment"]
      | components["schemas"]["PathSegmentAnyItemPathSegment"];
    PathSegmentAnyItemPathSegment: {
      /** @enum {string} */
      kind: "anyItem";
    };
    PathSegmentAttributePathSegment: {
      /** @enum {string} */
      kind: "attribute";
      name: string;
      namespaceUri?: null | string;
    };
    PathSegmentFieldPathSegment: {
      /** @enum {string} */
      kind: "field";
      name: string;
      namespaceUri?: null | string;
    };
    PathSegmentIndexPathSegment: {
      index: string;
      /** @enum {string} */
      kind: "index";
    };
    ProjectionBoundary: {
      format: string;
      id: string;
      name: string;
      nativeLocator: components["schemas"]["NativeLocator"];
      role: components["schemas"]["BoundaryRole"];
      rootFieldId: null | string;
      shape: components["schemas"]["BoundaryShape"];
    };
    ProjectionControl: {
      appliesTo: components["schemas"]["ProjectionNodeReference"][];
      dependencies: components["schemas"]["RuleDependency"][];
      details: components["schemas"]["DetailNode"][];
      id: string;
      kind: components["schemas"]["ControlKind"];
      nativeLocator: components["schemas"]["NativeLocator"];
      summary: string;
    };
    ProjectionCoverage: {
      gaps: components["schemas"]["CoverageGap"][];
      status: components["schemas"]["CoverageStatus"];
    };
    ProjectionDiagnostic: {
      code: string;
      message: string;
      nativeLocator?: null | components["schemas"]["NativeLocator"];
      projectionLocator?:
        null | components["schemas"]["ProjectionNodeReference"];
      severity: components["schemas"]["DiagnosticSeverity"];
    };
    ProjectionDocumentV1: {
      boundaries: components["schemas"]["ProjectionBoundary"][];
      capabilities: components["schemas"]["OperationCapability"][];
      controls: components["schemas"]["ProjectionControl"][];
      coverage: components["schemas"]["ProjectionCoverage"];
      diagnostics: components["schemas"]["ProjectionDiagnostic"][];
      fields: components["schemas"]["ProjectionField"][];
      parameters: components["schemas"]["ProjectionParameter"][];
      provenance: components["schemas"]["ProjectionProvenance"];
      relationships: components["schemas"]["ProjectionRelationship"][];
      rules: components["schemas"]["ProjectionRule"][];
      /** @enum {string} */
      readonly schemaVersion: "weft.mapping-projection.v1";
    };
    ProjectionField: {
      boundaryId: string;
      cardinality: components["schemas"]["FieldCardinality"];
      dataType: components["schemas"]["FieldDataType"];
      id: string;
      name: string;
      nativeLocator: components["schemas"]["NativeLocator"];
      nullability: components["schemas"]["FieldNullability"];
      path: components["schemas"]["PathSegment"][];
      requirement: components["schemas"]["FieldRequirement"];
    };
    ProjectionNodeReference:
      | components["schemas"]["ProjectionNodeReferenceBoundaryNodeReference"]
      | components["schemas"]["ProjectionNodeReferenceFieldNodeReference"]
      | components["schemas"]["ProjectionNodeReferenceParameterNodeReference"]
      | components["schemas"]["ProjectionNodeReferenceRuleNodeReference"]
      | components["schemas"]["ProjectionNodeReferenceControlNodeReference"];
    ProjectionNodeReferenceBoundaryNodeReference: {
      boundaryId: string;
      /** @enum {string} */
      kind: "boundary";
    };
    ProjectionNodeReferenceControlNodeReference: {
      controlId: string;
      /** @enum {string} */
      kind: "control";
    };
    ProjectionNodeReferenceFieldNodeReference: {
      fieldId: string;
      /** @enum {string} */
      kind: "field";
    };
    ProjectionNodeReferenceParameterNodeReference: {
      /** @enum {string} */
      kind: "parameter";
      parameterId: string;
    };
    ProjectionNodeReferenceRuleNodeReference: {
      /** @enum {string} */
      kind: "rule";
      ruleId: string;
    };
    ProjectionParameter: {
      dataType: components["schemas"]["FieldDataType"];
      defaultValue: null | components["schemas"]["LiteralValue"];
      id: string;
      name: string;
      nativeLocator: components["schemas"]["NativeLocator"];
      requirement: components["schemas"]["FieldRequirement"];
    };
    ProjectionProvenance: {
      adapterVersion: string;
      backend: components["schemas"]["BackendIdentity"];
      nativeArtifactDigest: components["schemas"]["ArtifactDigest"];
      projectionVersion: string;
      revision: components["schemas"]["ScopedRevisionId"];
    };
    ProjectionRelationship: {
      from: components["schemas"]["ProjectionNodeReference"];
      id: string;
      kind: components["schemas"]["RelationshipKind"];
      to: components["schemas"]["ProjectionNodeReference"];
    };
    ProjectionRule: {
      dependencies: components["schemas"]["RuleDependency"][];
      details: components["schemas"]["DetailNode"][];
      id: string;
      nativeLocator: components["schemas"]["NativeLocator"];
      outputFieldId: string;
      summary: string;
    };
    /** @enum {unknown} */
    RelationshipKind:
      | "readsFrom"
      | "writesTo"
      | "governedBy"
      | "dependsOn"
      | "produces"
      | "boundaryMember";
    RuleDependency:
      | components["schemas"]["RuleDependencyFieldDependency"]
      | components["schemas"]["RuleDependencyParameterDependency"]
      | components["schemas"]["RuleDependencyConstantDependency"]
      | components["schemas"]["RuleDependencyNodeDependency"];
    RuleDependencyConstantDependency: {
      /** @enum {string} */
      kind: "constant";
      role?: components["schemas"]["DependencyRole"];
      value: components["schemas"]["LiteralValue"];
    };
    RuleDependencyFieldDependency: {
      fieldId: string;
      /** @enum {string} */
      kind: "field";
      role?: components["schemas"]["DependencyRole"];
    };
    RuleDependencyNodeDependency: {
      /** @enum {string} */
      kind: "node";
      node: components["schemas"]["ProjectionNodeReference"];
      role?: components["schemas"]["DependencyRole"];
    };
    RuleDependencyParameterDependency: {
      /** @enum {string} */
      kind: "parameter";
      parameterId: string;
      role?: components["schemas"]["DependencyRole"];
    };
    /** @enum {unknown} */
    ScalarDataType:
      "string" | "integer" | "decimal" | "binaryFloat" | "boolean";
    ScopedRevisionId: {
      mappingId: string;
      organizationId: string;
      projectId: string;
      revisionId: string;
    };
    /**
     * @default utf8Bytes
     * @enum {unknown}
     */
    TextSpanUnit: "utf8Bytes" | "utf16CodeUnits" | "unicodeScalars";
  };
  responses: never;
  parameters: never;
  requestBodies: never;
  headers: never;
  pathItems: never;
}
export type $defs = Record<string, never>;
export interface operations {
  GetProjectionFixture: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        fixtureId: string;
      };
      cookie?: never;
    };
    requestBody?: never;
    responses: {
      /** @description OK */
      200: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ProjectionDocumentV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          [name: string]: unknown;
        };
        content?: never;
      };
    };
  };
  GetHealth: {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    requestBody?: never;
    responses: {
      /** @description OK */
      200: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["HealthResponseV1"];
        };
      };
    };
  };
}
