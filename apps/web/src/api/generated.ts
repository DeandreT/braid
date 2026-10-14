/**
 * Generated from contracts/weft-v1.json. Do not edit manually.
 * Refresh with: npm run contracts:generate
 */
export interface paths {
  "/api/v1/catalog/context": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read the explicitly configured development catalog context and request token. */
    get: operations["GetCatalogContext"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/examples/mappings": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** List immutable self-authored native examples (development only). */
    get: operations["GetNativeExamples"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/examples/mappings/{exampleId}": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read separate native projection and validation reports (development only). */
    get: operations["GetNativeExample"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
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
  "/api/v1/organizations/{organizationId}/projects": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read a bounded page of currently visible projects. */
    get: operations["GetCatalogProjects"];
    put?: never;
    /**
     * Atomically create one project and its original actor's first Administrator membership, or replay its original creation.
     * @description Requires fresh active organization admission and an explicit project-creation capability, independent of existing project roles. The document contains exactly the case-sensitive string properties name and description; both are required, unknown and duplicate fields are rejected. The strict UTF-8 entity is limited to 65536 bytes with no BOM, comments, trailing commas or decompression. Name is trimmed once and limited to 1–200 Unicode scalars and 800 UTF-8 bytes; description is preserved exactly and limited to 4000 scalars and 16000 UTF-8 bytes. Neither permits NUL or invalid Unicode. Only an optional UTF-8 charset and absent or one identity Content-Encoding are supported. No query, If-Match or If-None-Match fields are supported. One canonical nonzero lowercase UUID Idempotency-Key is required. Fresh capability admission and bounded immediate work admission precede body consumption; the final short atomic operation rechecks authority. 201 means actual creation and 200 exact replay after commit; changed intent or another actor's key is opaque 409. Initial membership facts are historical and never establish current project access or repair a revoked membership.
     */
    post: operations["CreateCatalogProject"];
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read project metadata and current supported actions. */
    get: operations["GetCatalogProject"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read a bounded page of mappings and committed heads. */
    get: operations["GetCatalogMappings"];
    put?: never;
    /**
     * Create a mapping and its first revision from exact native bytes.
     * @description One raw application/octet-stream source with no media parameters, at most 4194304 bytes. Content-Encoding permits only identity. A canonical nonzero UUID Idempotency-Key is required; If-Match and If-None-Match are unsupported. The exact body is consumed only after fresh Publisher or Administrator authority and bounded work admission. Accepted results expose safe own receipts only; only committed outcomes include a committed revision URL.
     */
    post: operations["CreateCatalogMapping"];
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read mapping metadata and its current committed head. */
    get: operations["GetCatalogMapping"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/metadata": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read the exact mapping metadata representation. */
    get: operations["GetCatalogMappingMetadata"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    /**
     * Atomically replace mapping name and description using one strong metadata If-Match validator.
     * @description The edit document contains exactly the case-sensitive string properties name and description. Both are required. The strict UTF-8 body is limited to 65536 bytes with no BOM; unknown or duplicate properties are rejected. Name is trimmed, nonblank and limited to 200 Unicode scalars after trimming; description is preserved and limited to 4000 Unicode scalars. Neither field permits NUL or invalid Unicode. Media type parameters permit only an optional UTF-8 charset; Content-Encoding permits only identity.
     */
    patch: operations["EditCatalogMappingMetadata"];
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/revisions": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read immutable revision history in descending committed sequence. */
    get: operations["GetCatalogRevisions"];
    put?: never;
    /**
     * Append one native revision using the mapping's persisted backend.
     * @description One raw application/octet-stream source with no media parameters, at most 4194304 bytes. Content-Encoding permits only identity. A canonical nonzero UUID Idempotency-Key is required; If-Match and If-None-Match are unsupported. The exact body is consumed only after fresh Publisher or Administrator authority and bounded work admission. Accepted results expose safe own receipts only; only committed outcomes include a committed revision URL.
     */
    post: operations["AppendCatalogRevision"];
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/revisions/{revisionId}": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read one scoped immutable revision's catalog metadata. */
    get: operations["GetCatalogRevision"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/revision-ingestions/{operationId}": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /**
     * Read the original actor's safe durable revision ingestion receipt after fresh upload authority.
     * @description No query fields. Fresh Publisher or Administrator authority and original operation ownership are required. This read never acquires a work permit, claims, executes, or touches native storage. Future scopes do not imply that resources exist.
     */
    get: operations["GetCatalogRevisionIngestionReceipt"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/revision-ingestions/{operationId}/resume": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    get?: never;
    put?: never;
    /**
     * Attempt one explicit source-free recovery of the original actor's ingestion operation.
     * @description No query fields, Idempotency-Key, If-Match, If-None-Match or body. Fresh own-receipt authority precedes the bounded empty-body check and immediate work admission. This synchronous operation attempts one claim without retry; terminal results replay and missing reserved native source yields NeedsSource.
     */
    post: operations["ResumeCatalogRevisionIngestion"];
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
    CatalogAccessV1: {
      actions: components["schemas"]["CatalogActionsV1"];
      role: components["schemas"]["CatalogRoleV1"];
    };
    CatalogActionsV1: {
      chat: boolean;
      createMappings: boolean;
      createProjects: boolean;
      editMetadata: boolean;
      manageMembership: boolean;
      preview: boolean;
      readReports: boolean;
      uploadRevisions: boolean;
      view: boolean;
    };
    /** @enum {unknown} */
    CatalogAdmissionV1: "valid" | "decodedInvalid";
    CatalogContextV1: {
      actorPrincipalId: string;
      allowedOrganizationId: string;
      features: components["schemas"]["CatalogFeaturesV1"];
      mode: string;
      organizationActions: components["schemas"]["CatalogOrganizationActionsV1"];
      organizationName: string;
      requestToken: string;
    };
    /** @enum {unknown} */
    CatalogErrorCodeV1:
      | "unavailable"
      | "invalidRequest"
      | "notFound"
      | "identityRejected"
      | "persistenceFailure"
      | "internalFailure"
      | "preconditionRequired"
      | "preconditionFailed"
      | "unsupportedMediaType"
      | "bodyLimit"
      | "versionExhausted"
      | "ingestionAdmissionBusy"
      | "permissionDenied"
      | "idempotencyConflict"
      | "projectCreationAdmissionBusy";
    CatalogErrorV1: {
      code: components["schemas"]["CatalogErrorCodeV1"];
    };
    CatalogFeaturesV1: {
      chat: boolean;
      createMappings: boolean;
      createProjects: boolean;
      editMetadata: boolean;
      manageMembership: boolean;
      preview: boolean;
      readCatalog: boolean;
      readReports: boolean;
      uploadRevisions: boolean;
    };
    CatalogHeadV1: {
      admission: components["schemas"]["CatalogAdmissionV1"];
      scope: components["schemas"]["CatalogRevisionScopeV1"];
      sequence: string;
    };
    CatalogMappingMetadataEditV1: {
      description: string;
      name: string;
    };
    CatalogMappingMetadataV1: {
      description: string;
      name: string;
      scope: components["schemas"]["CatalogMappingScopeV1"];
      version: string;
    };
    CatalogMappingPageV1: {
      mappings: components["schemas"]["CatalogMappingV1"][];
      nextCursor?: null | string;
    };
    CatalogMappingScopeV1: {
      mappingId: string;
      organizationId: string;
      projectId: string;
    };
    CatalogMappingV1: {
      access: components["schemas"]["CatalogAccessV1"];
      backendId: string;
      /** Format: date-time */
      createdAtUtc: string;
      creatorPrincipalId: string;
      description: string;
      head: components["schemas"]["CatalogHeadV1"];
      name: string;
      ownerPrincipalId: string;
      scope: components["schemas"]["CatalogMappingScopeV1"];
      /** Format: date-time */
      updatedAtUtc: string;
      version: string;
    };
    CatalogOrganizationActionsV1: {
      createProjects: boolean;
    };
    CatalogProjectCreateV1: {
      description: string;
      name: string;
    };
    /** @enum {unknown} */
    CatalogProjectCreationOutcomeV1: "created" | "replayed";
    CatalogProjectCreationReceiptV1: {
      /** Format: date-time */
      createdAtUtc: string;
      creatorPrincipalId: string;
      description: string;
      initialMembershipActive: boolean;
      initialMembershipRole: components["schemas"]["CatalogRoleV1"];
      initialMembershipVersion: string;
      initialProjectVersion: string;
      name: string;
      project: components["schemas"]["CatalogProjectScopeV1"];
    };
    CatalogProjectCreationResultV1: {
      outcome: components["schemas"]["CatalogProjectCreationOutcomeV1"];
      projectUrl: string;
      receipt: components["schemas"]["CatalogProjectCreationReceiptV1"];
    };
    CatalogProjectPageV1: {
      nextCursor?: null | string;
      projects: components["schemas"]["CatalogProjectV1"][];
    };
    CatalogProjectScopeV1: {
      organizationId: string;
      projectId: string;
    };
    CatalogProjectV1: {
      access: components["schemas"]["CatalogAccessV1"];
      /** Format: date-time */
      createdAtUtc: string;
      creatorPrincipalId: string;
      description: string;
      name: string;
      scope: components["schemas"]["CatalogProjectScopeV1"];
      /** Format: date-time */
      updatedAtUtc: string;
      version: string;
    };
    /** @enum {unknown} */
    CatalogRevisionIngestionFailureV1:
      | "needsSource"
      | "storageIntegrity"
      | "storageIo"
      | "backendUnavailable"
      | "backendFailure"
      | "timedOut"
      | "canceled"
      | "isolationFailure"
      | "protocolFailure"
      | "reservedProfileUnavailable"
      | "evidenceLimit"
      | null;
    /** @enum {unknown} */
    CatalogRevisionIngestionOutcomeV1:
      | "committedValid"
      | "committedDecodedInvalid"
      | "inProgress"
      | "needsSource"
      | "retryableFailure"
      | "rejected"
      | "headConflict"
      | "attemptExhausted"
      | "idempotencyConflict"
      | "claimLost"
      | "notAuthorizedOrNotFound";
    /** @enum {unknown} */
    CatalogRevisionIngestionPhaseV1:
      | "reserved"
      | "staged"
      | "ready"
      | "committed"
      | "rejected"
      | "headConflict"
      | "attemptExhausted";
    CatalogRevisionIngestionReceiptV1: {
      /** Format: int32 */
      claimCount: number;
      committedAdmission: components["schemas"]["CatalogAdmissionV1"];
      committedSequence: null | string;
      /** Format: date-time */
      completedAtUtc: null | string;
      futureMapping: components["schemas"]["CatalogMappingScopeV1"];
      futureRevision: components["schemas"]["CatalogRevisionScopeV1"];
      latestFailure: components["schemas"]["CatalogRevisionIngestionFailureV1"];
      operationId: string;
      originalFilename: string;
      phase: components["schemas"]["CatalogRevisionIngestionPhaseV1"];
      project: components["schemas"]["CatalogProjectScopeV1"];
      /** Format: date-time */
      reservedAtUtc: string;
    };
    /** @enum {unknown} */
    CatalogRevisionIngestionRejectionV1:
      | "emptyNative"
      | "nativeLimit"
      | "unregisteredBackend"
      | "nativeDecode"
      | "unsupportedArtifact"
      | "resourceLimit"
      | null;
    CatalogRevisionIngestionResultV1: {
      committedRevisionUrl?: null | string;
      failure?: components["schemas"]["CatalogRevisionIngestionFailureV1"];
      outcome: components["schemas"]["CatalogRevisionIngestionOutcomeV1"];
      receipt?: components["schemas"]["CatalogRevisionIngestionReceiptV1"];
      rejection?: components["schemas"]["CatalogRevisionIngestionRejectionV1"];
    };
    CatalogRevisionPageV1: {
      nextCursor?: null | string;
      revisions: components["schemas"]["CatalogRevisionV1"][];
    };
    CatalogRevisionScopeV1: {
      mappingId: string;
      organizationId: string;
      projectId: string;
      revisionId: string;
    };
    CatalogRevisionV1: {
      admission: components["schemas"]["CatalogAdmissionV1"];
      backendId: string;
      /** Format: date-time */
      committedAtUtc: string;
      creatorPrincipalId: string;
      logicalDocument: string;
      /** Format: int32 */
      nativeByteCount: number;
      nativeDigest: components["schemas"]["ArtifactDigest"];
      originalFilename: string;
      scope: components["schemas"]["CatalogRevisionScopeV1"];
      sequence: string;
    };
    /** @enum {unknown} */
    CatalogRoleV1: "viewer" | "runner" | "publisher" | "administrator";
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
    NativeDiagnosticOwnerV1:
      | components["schemas"]["NativeDiagnosticOwnerV1GraphNodeNativeOwnerV1"]
      | components["schemas"]["NativeDiagnosticOwnerV1UserFunctionNativeOwnerV1"]
      | components["schemas"]["NativeDiagnosticOwnerV1EndpointNativeOwnerV1"]
      | components["schemas"]["NativeDiagnosticOwnerV1SchemaNodeNativeOwnerV1"]
      | components["schemas"]["NativeDiagnosticOwnerV1ScopeNativeOwnerV1"]
      | components["schemas"]["NativeDiagnosticOwnerV1FailureRuleNativeOwnerV1"];
    NativeDiagnosticOwnerV1EndpointNativeOwnerV1: {
      endpoint: components["schemas"]["NativeEndpointV1"];
      /** @enum {string} */
      kind: "endpoint";
    };
    NativeDiagnosticOwnerV1FailureRuleNativeOwnerV1: {
      index: string;
      /** @enum {string} */
      kind: "failureRule";
    };
    NativeDiagnosticOwnerV1GraphNodeNativeOwnerV1: {
      functionId: null | string;
      /** @enum {string} */
      kind: "graphNode";
      nodeId: string;
    };
    NativeDiagnosticOwnerV1SchemaNodeNativeOwnerV1: {
      endpoint: components["schemas"]["NativeEndpointV1"];
      /** @enum {string} */
      kind: "schemaNode";
      path: components["schemas"]["SchemaNativeOwnerStepV1"][];
    };
    NativeDiagnosticOwnerV1ScopeNativeOwnerV1: {
      /** @enum {string} */
      kind: "scope";
      path: components["schemas"]["ScopeNativeOwnerStepV1"][];
      target: components["schemas"]["NativeEndpointV1"];
    };
    NativeDiagnosticOwnerV1UserFunctionNativeOwnerV1: {
      functionId: string;
      /** @enum {string} */
      kind: "userFunction";
    };
    NativeEndpointV1:
      | components["schemas"]["NativeEndpointV1SourceNativeEndpointV1"]
      | components["schemas"]["NativeEndpointV1TargetNativeEndpointV1"]
      | components["schemas"]["NativeEndpointV1NamedSourceNativeEndpointV1"]
      | components["schemas"]["NativeEndpointV1NamedTargetNativeEndpointV1"];
    NativeEndpointV1NamedSourceNativeEndpointV1: {
      index: string;
      /** @enum {string} */
      kind: "namedSource";
      name: string;
    };
    NativeEndpointV1NamedTargetNativeEndpointV1: {
      index: string;
      /** @enum {string} */
      kind: "namedTarget";
      name: string;
    };
    NativeEndpointV1SourceNativeEndpointV1: {
      /** @enum {string} */
      kind: "source";
    };
    NativeEndpointV1TargetNativeEndpointV1: {
      /** @enum {string} */
      kind: "target";
    };
    NativeExampleDescriptorV1: {
      /** Format: int32 */
      byteCount: number;
      digest: components["schemas"]["ArtifactDigest"];
      id: string;
      revision: components["schemas"]["ScopedRevisionId"];
      source: string;
      title: string;
    };
    NativeExampleResultV1: {
      example: components["schemas"]["NativeExampleDescriptorV1"];
      projection: components["schemas"]["ProjectionReportV1"];
      validation: components["schemas"]["ValidationReportV1"];
    };
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
    OperationDiagnosticV1: {
      code: string;
      message: string;
      messageTruncated: boolean;
      nativeLocator?: null | components["schemas"]["NativeLocator"];
      severity: components["schemas"]["DiagnosticSeverity"];
    };
    OperationProvenanceV1: {
      adapterVersion: string;
      backend: components["schemas"]["BackendIdentity"];
      confirmed: boolean;
      nativeArtifactDigest: components["schemas"]["ArtifactDigest"];
      revision: components["schemas"]["ScopedRevisionId"];
    };
    /** @enum {unknown} */
    OperationStatusV1:
      | "succeeded"
      | "invalidMapping"
      | "unsupportedArtifact"
      | "resourceLimit"
      | "protocolFailure"
      | "backendUnavailable"
      | "backendFailure"
      | "timedOut"
      | "canceled"
      | "isolationFailure";
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
    ProjectionReportV1: {
      diagnostics: components["schemas"]["OperationDiagnosticV1"][];
      document: null | components["schemas"]["ProjectionDocumentV1"];
      provenance: null | components["schemas"]["OperationProvenanceV1"];
      status: components["schemas"]["OperationStatusV1"];
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
    SchemaNativeOwnerStepV1:
      | components["schemas"]["SchemaNativeOwnerStepV1ChildSchemaNativeOwnerStepV1"]
      | components["schemas"]["SchemaNativeOwnerStepV1DynamicFieldSchemaNativeOwnerStepV1"]
      | components["schemas"]["SchemaNativeOwnerStepV1ContainsSchemaNativeOwnerStepV1"]
      | components["schemas"]["SchemaNativeOwnerStepV1DependentSchemaNativeOwnerStepV1"];
    SchemaNativeOwnerStepV1ChildSchemaNativeOwnerStepV1: {
      index: string;
      /** @enum {string} */
      kind: "child";
    };
    SchemaNativeOwnerStepV1ContainsSchemaNativeOwnerStepV1: {
      index: string;
      /** @enum {string} */
      kind: "contains";
    };
    SchemaNativeOwnerStepV1DependentSchemaNativeOwnerStepV1: {
      index: string;
      /** @enum {string} */
      kind: "dependentSchema";
    };
    SchemaNativeOwnerStepV1DynamicFieldSchemaNativeOwnerStepV1: {
      /** @enum {string} */
      kind: "dynamicField";
    };
    ScopedRevisionId: {
      mappingId: string;
      organizationId: string;
      projectId: string;
      revisionId: string;
    };
    ScopeNativeOwnerStepV1:
      | components["schemas"]["ScopeNativeOwnerStepV1ChildScopeNativeOwnerStepV1"]
      | components["schemas"]["ScopeNativeOwnerStepV1DynamicChildScopeNativeOwnerStepV1"]
      | components["schemas"]["ScopeNativeOwnerStepV1SegmentScopeNativeOwnerStepV1"];
    ScopeNativeOwnerStepV1ChildScopeNativeOwnerStepV1: {
      index: string;
      /** @enum {string} */
      kind: "child";
    };
    ScopeNativeOwnerStepV1DynamicChildScopeNativeOwnerStepV1: {
      index: string;
      /** @enum {string} */
      kind: "dynamicChild";
    };
    ScopeNativeOwnerStepV1SegmentScopeNativeOwnerStepV1: {
      index: string;
      /** @enum {string} */
      kind: "segment";
    };
    /**
     * @default utf8Bytes
     * @enum {unknown}
     */
    TextSpanUnit: "utf8Bytes" | "utf16CodeUnits" | "unicodeScalars";
    ValidationDiagnosticV1: {
      code: string;
      location?: null | string;
      /** @default false */
      locationTruncated: boolean;
      message: string;
      messageTruncated: boolean;
      nativeLocator?: null | components["schemas"]["NativeLocator"];
      owner?: null | components["schemas"]["NativeDiagnosticOwnerV1"];
      projectionLocator?:
        null | components["schemas"]["ProjectionNodeReference"];
      severity: components["schemas"]["DiagnosticSeverity"];
    };
    ValidationReportV1: {
      diagnostics: components["schemas"]["ValidationDiagnosticV1"][];
      /** Format: int32 */
      emittedIssueCount: number;
      isValid: null | boolean;
      provenance: null | components["schemas"]["OperationProvenanceV1"];
      status: components["schemas"]["OperationStatusV1"];
      /** Format: int32 */
      totalIssueCount: number;
      truncated: boolean;
    };
  };
  responses: never;
  parameters: never;
  requestBodies: never;
  headers: never;
  pathItems: never;
}
export type $defs = Record<string, never>;
export interface operations {
  GetCatalogContext: {
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
          "application/json": components["schemas"]["CatalogContextV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Internal Server Error */
      500: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
    };
  };
  GetNativeExamples: {
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
          "application/json": components["schemas"]["NativeExampleDescriptorV1"][];
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
  GetNativeExample: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        exampleId: string;
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
          "application/json": components["schemas"]["NativeExampleResultV1"];
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
  GetCatalogProjects: {
    parameters: {
      query?: {
        cursor?: string;
        limit?: string;
      };
      header?: never;
      path: {
        organizationId: string;
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
          "application/json": components["schemas"]["CatalogProjectPageV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Internal Server Error */
      500: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
    };
  };
  CreateCatalogProject: {
    parameters: {
      query?: never;
      header: {
        /** @description Absent or one identity value; compressed and multiple codings are unsupported. */
        "Content-Encoding"?: string;
        /** @description Exactly one nonzero canonical lowercase D-format UUID with only surrounding SP/HTAB OWS allowed. Raw value at most 128 characters; duplicates and lists are rejected. */
        "Idempotency-Key": string;
        /** @description One exact canonical configured browser origin, checked before any body read. */
        Origin: string;
        /** @description Cookie-bound framework request token from a successfully admitted catalog context, at most 4096 characters. */
        "X-XSRF-TOKEN": string;
      };
      path: {
        organizationId: string;
      };
      cookie?: never;
    };
    requestBody: {
      content: {
        "application/vnd.weft.project-create.v1+json": components["schemas"]["CatalogProjectCreateV1"];
      };
    };
    responses: {
      /** @description OK */
      200: {
        headers: {
          /** @description Always no-store. */
          "Cache-Control"?: "no-store";
          /** @description Exact full-scoped relative ordinary GET project URL. That read still checks current project membership. */
          Location?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogProjectCreationResultV1"];
        };
      };
      /** @description Created */
      201: {
        headers: {
          /** @description Always no-store. */
          "Cache-Control"?: "no-store";
          /** @description Exact full-scoped relative ordinary GET project URL. That read still checks current project membership. */
          Location?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogProjectCreationResultV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Always no-store. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          /** @description Always no-store. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          /** @description Always no-store. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          /** @description Always no-store. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Payload Too Large */
      413: {
        headers: {
          /** @description Always no-store. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unsupported Media Type */
      415: {
        headers: {
          /** @description Always no-store. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Internal Server Error */
      500: {
        headers: {
          /** @description Always no-store. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          /** @description Always no-store. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
    };
  };
  GetCatalogProject: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        organizationId: string;
        projectId: string;
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
          "application/json": components["schemas"]["CatalogProjectV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Internal Server Error */
      500: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
    };
  };
  GetCatalogMappings: {
    parameters: {
      query?: {
        cursor?: string;
        limit?: string;
      };
      header?: never;
      path: {
        organizationId: string;
        projectId: string;
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
          "application/json": components["schemas"]["CatalogMappingPageV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Internal Server Error */
      500: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
    };
  };
  CreateCatalogMapping: {
    parameters: {
      query: {
        /** @description Required exact backend ID, nonblank and at most 128 UTF-8 bytes; no profile or default backend is accepted. */
        backendId: string;
        /** @description Preserved text, default empty, at most 4000 Unicode scalars; valid Unicode and no NUL. */
        description?: string;
        /** @description Required display name: trim before nonblank and 200 Unicode scalar validation; valid Unicode and no NUL. */
        name: string;
        /** @description Safe display basename, default entry.native.json; at most 255 UTF-8 bytes, no control characters or path separators. */
        originalFilename?: string;
      };
      header: {
        /** @description Absent or one identity token; compressed or multiple codings are unsupported. */
        "Content-Encoding"?: string;
        /** @description One canonical nonzero lowercase UUID, with surrounding protocol OWS allowed. Repeated fields and lists are rejected. */
        "Idempotency-Key": string;
      };
      path: {
        organizationId: string;
        projectId: string;
      };
      cookie?: never;
    };
    requestBody: {
      content: {
        "application/octet-stream": string;
      };
    };
    responses: {
      /** @description OK */
      200: {
        headers: {
          /** @description Always no-store; receipts and operation results are not cacheable. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogRevisionIngestionResultV1"];
        };
      };
      /** @description Accepted */
      202: {
        headers: {
          /** @description Always no-store; receipts and operation results are not cacheable. */
          "Cache-Control"?: "no-store";
          /** @description Canonical full-scoped own receipt URL for the actual InProgress operation. */
          Location?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogRevisionIngestionResultV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Always no-store; receipts and operation results are not cacheable. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json":
            | components["schemas"]["CatalogRevisionIngestionResultV1"]
            | components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          /** @description Always no-store; receipts and operation results are not cacheable. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          /** @description Always no-store; receipts and operation results are not cacheable. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json":
            | components["schemas"]["CatalogRevisionIngestionResultV1"]
            | components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          /** @description Always no-store; receipts and operation results are not cacheable. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogRevisionIngestionResultV1"];
        };
      };
      /** @description Payload Too Large */
      413: {
        headers: {
          /** @description Always no-store; receipts and operation results are not cacheable. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json":
            | components["schemas"]["CatalogRevisionIngestionResultV1"]
            | components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unsupported Media Type */
      415: {
        headers: {
          /** @description Always no-store; receipts and operation results are not cacheable. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unprocessable Entity */
      422: {
        headers: {
          /** @description Always no-store; receipts and operation results are not cacheable. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogRevisionIngestionResultV1"];
        };
      };
      /** @description Internal Server Error */
      500: {
        headers: {
          /** @description Always no-store; receipts and operation results are not cacheable. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          /** @description Always no-store; receipts and operation results are not cacheable. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json":
            | components["schemas"]["CatalogRevisionIngestionResultV1"]
            | components["schemas"]["CatalogErrorV1"];
        };
      };
    };
  };
  GetCatalogMapping: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        mappingId: string;
        organizationId: string;
        projectId: string;
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
          "application/json": components["schemas"]["CatalogMappingV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Internal Server Error */
      500: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
    };
  };
  GetCatalogMappingMetadata: {
    parameters: {
      query?: never;
      header?: {
        /** @description Optional entity-tag list or wildcard, using strong comparison. Combined conditional headers are limited to 4096 characters and each to 16 tags. */
        "If-Match"?: string;
        /** @description Optional entity-tag list or wildcard, using weak comparison after If-Match. Matching returns 304 with no body. */
        "If-None-Match"?: string;
      };
      path: {
        mappingId: string;
        organizationId: string;
        projectId: string;
      };
      cookie?: never;
    };
    requestBody?: never;
    responses: {
      /** @description OK */
      200: {
        headers: {
          /** @description Supported edit document media type, present only for an enabled edit capability on an admitted resource. */
          "Accept-Patch"?: string;
          /** @description Quoted strong metadata-v1 validator bound to the complete mapping scope and positive version. */
          ETag?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogMappingMetadataV1"];
        };
      };
      /** @description Not Modified */
      304: {
        headers: {
          /** @description Supported edit document media type, present only for an enabled edit capability on an admitted resource. */
          "Accept-Patch"?: string;
          /** @description Quoted strong metadata-v1 validator bound to the complete mapping scope and positive version. */
          ETag?: string;
          [name: string]: unknown;
        };
        content?: never;
      };
      /** @description Bad Request */
      400: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Precondition Failed */
      412: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Internal Server Error */
      500: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
    };
  };
  EditCatalogMappingMetadata: {
    parameters: {
      query?: never;
      header: {
        /** @description Exactly one concrete strong metadata entity-tag, at most 256 characters with surrounding OWS. Lists, wildcard and additional If-None-Match are unsupported. */
        "If-Match": string;
      };
      path: {
        mappingId: string;
        organizationId: string;
        projectId: string;
      };
      cookie?: never;
    };
    requestBody: {
      content: {
        "application/vnd.weft.mapping-metadata-edit.v1+json": components["schemas"]["CatalogMappingMetadataEditV1"];
      };
    };
    responses: {
      /** @description OK */
      200: {
        headers: {
          /** @description Supported edit document media type, present only for an enabled edit capability on an admitted resource. */
          "Accept-Patch"?: string;
          /** @description Quoted strong metadata-v1 validator bound to the complete mapping scope and positive version. */
          ETag?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogMappingMetadataV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Precondition Failed */
      412: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Payload Too Large */
      413: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unsupported Media Type */
      415: {
        headers: {
          /** @description Supported edit document media type, present only for an enabled edit capability on an admitted resource. */
          "Accept-Patch"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Precondition Required */
      428: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Internal Server Error */
      500: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
    };
  };
  GetCatalogRevisions: {
    parameters: {
      query?: {
        cursor?: string;
        limit?: string;
      };
      header?: never;
      path: {
        mappingId: string;
        organizationId: string;
        projectId: string;
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
          "application/json": components["schemas"]["CatalogRevisionPageV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Internal Server Error */
      500: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
    };
  };
  AppendCatalogRevision: {
    parameters: {
      query?: {
        /** @description Canonical nonzero lowercase revision UUID, required only with head=equal. */
        expectedHeadRevisionId?: string;
        /** @description Exact any (default) or equal. equal requires expectedHeadRevisionId; any forbids it. */
        head?: "any" | "equal";
        /** @description Safe display basename, default entry.native.json; at most 255 UTF-8 bytes, no control characters or path separators. */
        originalFilename?: string;
      };
      header: {
        /** @description Absent or one identity token; compressed or multiple codings are unsupported. */
        "Content-Encoding"?: string;
        /** @description One canonical nonzero lowercase UUID, with surrounding protocol OWS allowed. Repeated fields and lists are rejected. */
        "Idempotency-Key": string;
      };
      path: {
        mappingId: string;
        organizationId: string;
        projectId: string;
      };
      cookie?: never;
    };
    requestBody: {
      content: {
        "application/octet-stream": string;
      };
    };
    responses: {
      /** @description OK */
      200: {
        headers: {
          /** @description Always no-store; receipts and operation results are not cacheable. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogRevisionIngestionResultV1"];
        };
      };
      /** @description Accepted */
      202: {
        headers: {
          /** @description Always no-store; receipts and operation results are not cacheable. */
          "Cache-Control"?: "no-store";
          /** @description Canonical full-scoped own receipt URL for the actual InProgress operation. */
          Location?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogRevisionIngestionResultV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Always no-store; receipts and operation results are not cacheable. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json":
            | components["schemas"]["CatalogRevisionIngestionResultV1"]
            | components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          /** @description Always no-store; receipts and operation results are not cacheable. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          /** @description Always no-store; receipts and operation results are not cacheable. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json":
            | components["schemas"]["CatalogRevisionIngestionResultV1"]
            | components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          /** @description Always no-store; receipts and operation results are not cacheable. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogRevisionIngestionResultV1"];
        };
      };
      /** @description Payload Too Large */
      413: {
        headers: {
          /** @description Always no-store; receipts and operation results are not cacheable. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json":
            | components["schemas"]["CatalogRevisionIngestionResultV1"]
            | components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unsupported Media Type */
      415: {
        headers: {
          /** @description Always no-store; receipts and operation results are not cacheable. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unprocessable Entity */
      422: {
        headers: {
          /** @description Always no-store; receipts and operation results are not cacheable. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogRevisionIngestionResultV1"];
        };
      };
      /** @description Internal Server Error */
      500: {
        headers: {
          /** @description Always no-store; receipts and operation results are not cacheable. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          /** @description Always no-store; receipts and operation results are not cacheable. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json":
            | components["schemas"]["CatalogRevisionIngestionResultV1"]
            | components["schemas"]["CatalogErrorV1"];
        };
      };
    };
  };
  GetCatalogRevision: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        mappingId: string;
        organizationId: string;
        projectId: string;
        revisionId: string;
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
          "application/json": components["schemas"]["CatalogRevisionV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Internal Server Error */
      500: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
    };
  };
  GetCatalogRevisionIngestionReceipt: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        operationId: string;
        organizationId: string;
        projectId: string;
      };
      cookie?: never;
    };
    requestBody?: never;
    responses: {
      /** @description OK */
      200: {
        headers: {
          /** @description Always no-store; receipts and operation results are not cacheable. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogRevisionIngestionReceiptV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Always no-store; receipts and operation results are not cacheable. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          /** @description Always no-store; receipts and operation results are not cacheable. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          /** @description Always no-store; receipts and operation results are not cacheable. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Internal Server Error */
      500: {
        headers: {
          /** @description Always no-store; receipts and operation results are not cacheable. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
    };
  };
  ResumeCatalogRevisionIngestion: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        operationId: string;
        organizationId: string;
        projectId: string;
      };
      cookie?: never;
    };
    requestBody?: never;
    responses: {
      /** @description OK */
      200: {
        headers: {
          /** @description Always no-store; receipts and operation results are not cacheable. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogRevisionIngestionResultV1"];
        };
      };
      /** @description Accepted */
      202: {
        headers: {
          /** @description Always no-store; receipts and operation results are not cacheable. */
          "Cache-Control"?: "no-store";
          /** @description Canonical full-scoped own receipt URL for the actual InProgress operation. */
          Location?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogRevisionIngestionResultV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Always no-store; receipts and operation results are not cacheable. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json":
            | components["schemas"]["CatalogRevisionIngestionResultV1"]
            | components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          /** @description Always no-store; receipts and operation results are not cacheable. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          /** @description Always no-store; receipts and operation results are not cacheable. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json":
            | components["schemas"]["CatalogRevisionIngestionResultV1"]
            | components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          /** @description Always no-store; receipts and operation results are not cacheable. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogRevisionIngestionResultV1"];
        };
      };
      /** @description Payload Too Large */
      413: {
        headers: {
          /** @description Always no-store; receipts and operation results are not cacheable. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json":
            | components["schemas"]["CatalogRevisionIngestionResultV1"]
            | components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unsupported Media Type */
      415: {
        headers: {
          /** @description Always no-store; receipts and operation results are not cacheable. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unprocessable Entity */
      422: {
        headers: {
          /** @description Always no-store; receipts and operation results are not cacheable. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogRevisionIngestionResultV1"];
        };
      };
      /** @description Internal Server Error */
      500: {
        headers: {
          /** @description Always no-store; receipts and operation results are not cacheable. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          /** @description Always no-store; receipts and operation results are not cacheable. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json":
            | components["schemas"]["CatalogRevisionIngestionResultV1"]
            | components["schemas"]["CatalogErrorV1"];
        };
      };
    };
  };
}
