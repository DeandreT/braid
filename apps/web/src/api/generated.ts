/**
 * Generated from contracts/weft-v1.json. Do not edit manually.
 * Refresh with: npm run contracts:generate
 */
export interface paths {
  "/api/auth/callback/entra": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /**
     * Complete the fixed Entra code flow.
     * @description Supported contract; presence does not activate a runtime route or authentication mode. Bodyless, bounded, no-store/no-ETag/no-referrer. No bearer/ambient-claim identity or request-selected return target. Exactly code,state,session_state,client_info,iss,error,error_description,error_uri; no duplicates, token fields or code+error. Success only303Location'/' after acknowledged fresh issuance; failure is fixed readable401/503 HTML, never provider text.
     */
    get: operations["CompleteEntraLogin"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/auth/forget-local": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    get?: never;
    put?: never;
    /**
     * Explicit browser authentication protocol.
     * @description Supported contract; presence does not activate a runtime route or authentication mode. Bodyless, bounded, no-store/no-ETag/no-referrer. No bearer/ambient-claim identity or request-selected return target. One exact action header. Unsafe actions require exact Origin and separate standard auth-action AF; positive sessions additionally require112 epoch AF. Session fields/fingerprint are non-authority and memory-only. Forget success means revocation-unconfirmed only.
     */
    post: operations["ForgetDeniedLocalSession"];
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/auth/login": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    get?: never;
    put?: never;
    /**
     * Explicit browser authentication protocol.
     * @description Supported contract; presence does not activate a runtime route or authentication mode. Bodyless, bounded, no-store/no-ETag/no-referrer. No bearer/ambient-claim identity or request-selected return target. One exact action header. Unsafe actions require exact Origin and separate standard auth-action AF; positive sessions additionally require112 epoch AF. Session fields/fingerprint are non-authority and memory-only. Forget success means revocation-unconfirmed only.
     */
    post: operations["BeginBrowserLogin"];
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/auth/logout": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    get?: never;
    put?: never;
    /**
     * Explicit browser authentication protocol.
     * @description Supported contract; presence does not activate a runtime route or authentication mode. Bodyless, bounded, no-store/no-ETag/no-referrer. No bearer/ambient-claim identity or request-selected return target. One exact action header. Unsafe actions require exact Origin and separate standard auth-action AF; positive sessions additionally require112 epoch AF. Session fields/fingerprint are non-authority and memory-only. Forget success means revocation-unconfirmed only.
     */
    post: operations["LogoutBrowserSession"];
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/auth/session": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /**
     * Explicit browser authentication protocol.
     * @description Supported contract; presence does not activate a runtime route or authentication mode. Bodyless, bounded, no-store/no-ETag/no-referrer. No bearer/ambient-claim identity or request-selected return target. One exact action header. Unsafe actions require exact Origin and separate standard auth-action AF; positive sessions additionally require112 epoch AF. Session fields/fingerprint are non-authority and memory-only. Forget success means revocation-unconfirmed only.
     */
    get: operations["ReadBrowserAuthentication"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/catalog/context": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read the explicitly configured catalog identity context and request token. */
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
  "/api/v1/organizations/{organizationId}/previews/{runId}": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read one of the caller's own preview runs. */
    get: operations["GetCatalogPreview"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/previews/{runId}/cancel": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    get?: never;
    put?: never;
    /** Cancel a queued preview run or request cancellation of a running one. */
    post: operations["CancelCatalogPreview"];
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/previews/{runId}/outputs/{ordinal}": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read the exact bytes of one output of a succeeded preview run. */
    get: operations["GetCatalogPreviewOutput"];
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
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/comparisons": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /**
     * Compare the retained explanations of two exact revisions of one mapping.
     * @description Requires the revision report read feature and fresh project ReadMetadata authority for both revisions. leftRevision, leftProjection, rightRevision and rightProjection are required; leftValidation and rightValidation are optional. Each names an exact retained successful derivation, never a pending or failed attempt; each name may appear once and no other query is accepted. All UUIDs are canonical nonzero lowercase D strings. Reads never derive, claim, repair or start a worker. Missing revisions or reports are opaque 404s; unavailable report storage is 503. Facts pair only through unique structural anchors, never node IDs; partial or truncated evidence is reported as uncertainty, not equality. The comparison is bounded before response bytes and followed by fresh authority and source validation. No body or Content-Type is supported. Responses are no-store and carry no ETag.
     */
    get: operations["GetCatalogRevisionComparison"];
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
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/regression-cases": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read a bounded metadata page of saved regression cases. */
    get: operations["GetRegressionCases"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/regression-cases/{caseId}/versions/{version}": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read an exact saved case version under fresh sample permission. */
    get: operations["GetRegressionCaseVersion"];
    /** Publish an immutable case version or replay the identical original request. */
    put: operations["PutRegressionCaseVersion"];
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/regression-cases/capabilities": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read the active regression-case profile and current sample permissions. */
    get: operations["GetRegressionCaseCapabilities"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/regression-runs": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read the caller's bounded regression run history. */
    get: operations["GetRegressionRuns"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/regression-runs/{runId}": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read a retained exact-case result under fresh sample authority. */
    get: operations["GetRegressionRun"];
    /** Queue one exact saved-case version or recover the original request. */
    put: operations["PutRegressionRun"];
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/regression-runs/{runId}/cancel": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    get?: never;
    put?: never;
    /** Request cancellation without replacing a retained terminal result. */
    post: operations["CancelRegressionRun"];
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
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
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/revisions/{revisionId}/chat": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read the configured model profiles and limits for an authorized revision. */
    get: operations["GetChatAvailability"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/revisions/{revisionId}/chat/conversations": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read the caller's most recent private conversations about this revision. */
    get: operations["GetChatConversations"];
    put?: never;
    /** Create or replay a private conversation pinned to the revision's current verified reports. */
    post: operations["CreateChatConversation"];
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/revisions/{revisionId}/chat/conversations/{conversationId}": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read one private conversation and its most recent turns. */
    get: operations["GetChatConversation"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/revisions/{revisionId}/chat/conversations/{conversationId}/turns": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    get?: never;
    put?: never;
    /** Start or replay a turn identified by its client-generated turn ID. */
    post: operations["CreateChatTurn"];
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/revisions/{revisionId}/chat/conversations/{conversationId}/turns/{turnId}/cancel": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    get?: never;
    put?: never;
    /** Cancel an active turn; a terminal turn is returned unchanged. */
    post: operations["CancelChatTurn"];
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/revisions/{revisionId}/chat/conversations/{conversationId}/turns/{turnId}/events": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Stream the turn's durable events after a sequence number using SSE framing. */
    get: operations["StreamChatTurnEvents"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/revisions/{revisionId}/previews": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    get?: never;
    put?: never;
    /** Submit an idempotent bounded JSON sample preview of this exact revision. */
    post: operations["SubmitCatalogPreview"];
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/revisions/{revisionId}/reports/projection": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /**
     * Read the current selected projection report for one immutable revision.
     * @description Requires fresh project ReadMetadata authority for the exact actor and full revision ancestry; all four project roles qualify. Enabled features are independent of organization project-creation capability and upload authority. All route and query UUIDs are canonical nonzero lowercase D strings. Unknown, repeated and escaped-duplicate query names are rejected; no actor, digest, backend profile, policy or private object inputs are accepted. No body or Content-Type is supported. Content-Encoding is absent or one identity token, Transfer-Encoding is absent, and Content-Length is zero or the server proves no body. Unknown/chunked/nonempty framing is rejected before body consumption. Conditional headers and Idempotency-Key are unsupported. Reads never derive, claim, repair, provision storage or start a worker. Current selects the server's full report key for this revision, independently of the mapping head. No query is accepted. Lifecycle, backend status and read refusal are separate. False validation is a usable report. Missing, corrupt, unsupported or over-budget selected payloads retain honest available metadata with a categorical read refusal and null report. The complete wrapper including linked findings is bounded before response bytes and followed by fresh authority/source validation. Local work/read admission is nonwaiting 429; report dependency availability is closed 503. A ResourceLimit wrapper that cannot fit its policy uses a fixed categorical 503 error of at most 1024 bytes. All responses are no-store and carry no ETag.
     */
    get: operations["GetCatalogProjectionReport"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/revisions/{revisionId}/reports/projection/{derivationId}": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /**
     * Read one exact retained successful projection report.
     * @description Requires fresh project ReadMetadata authority for the exact actor and full revision ancestry; all four project roles qualify. Enabled features are independent of organization project-creation capability and upload authority. All route and query UUIDs are canonical nonzero lowercase D strings. Unknown, repeated and escaped-duplicate query names are rejected; no actor, digest, backend profile, policy or private object inputs are accepted. No body or Content-Type is supported. Content-Encoding is absent or one identity token, Transfer-Encoding is absent, and Content-Length is zero or the server proves no body. Unknown/chunked/nonempty framing is rejected before body consumption. Conditional headers and Idempotency-Key are unsupported. Reads never derive, claim, repair, provision storage or start a worker. The derivationId selects an immutable successful result, never a pending or failed attempt. No query is accepted. Lifecycle, backend status and read refusal are separate. False validation is a usable report. Missing, corrupt, unsupported or over-budget selected payloads retain honest available metadata with a categorical read refusal and null report. The complete wrapper including linked findings is bounded before response bytes and followed by fresh authority/source validation. Local work/read admission is nonwaiting 429; report dependency availability is closed 503. A ResourceLimit wrapper that cannot fit its policy uses a fixed categorical 503 error of at most 1024 bytes. All responses are no-store and carry no ETag.
     */
    get: operations["GetRetainedCatalogProjectionReport"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/revisions/{revisionId}/reports/projection/ensure": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    get?: never;
    put?: never;
    /**
     * Explicitly ensure one scoped persisted projection report.
     * @description Requires fresh project ReadMetadata authority for the exact actor and full revision ancestry; all four project roles qualify. Enabled features are independent of organization project-creation capability and upload authority. All route and query UUIDs are canonical nonzero lowercase D strings. Unknown, repeated and escaped-duplicate query names are rejected; no actor, digest, backend profile, policy or private object inputs are accepted. No body or Content-Type is supported. Content-Encoding is absent or one identity token, Transfer-Encoding is absent, and Content-Length is zero or the server proves no body. Unknown/chunked/nonempty framing is rejected before body consumption. Conditional headers and Idempotency-Key are unsupported. A cookie-bound framework request token and exact configured Origin are required. Optional repairDerivationId selects only the exact current successful result; there is no implicit repair or projection-link selector. Completed or cached observations return 200; actual pending or retry-delayed observations return 202. ClaimLost, SelectionChanged and GenerationExhausted are distinct 409 errors. Lifecycle, backend status and read refusal are separate. False validation is a usable report. Missing, corrupt, unsupported or over-budget selected payloads retain honest available metadata with a categorical read refusal and null report. The complete wrapper including linked findings is bounded before response bytes and followed by fresh authority/source validation. Local work/read admission is nonwaiting 429; report dependency availability is closed 503. A ResourceLimit wrapper that cannot fit its policy uses a fixed categorical 503 error of at most 1024 bytes. All responses are no-store and carry no ETag.
     */
    post: operations["EnsureCatalogProjectionReport"];
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/revisions/{revisionId}/reports/validation": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /**
     * Read the current selected validation report for one immutable revision.
     * @description Requires fresh project ReadMetadata authority for the exact actor and full revision ancestry; all four project roles qualify. Enabled features are independent of organization project-creation capability and upload authority. All route and query UUIDs are canonical nonzero lowercase D strings. Unknown, repeated and escaped-duplicate query names are rejected; no actor, digest, backend profile, policy or private object inputs are accepted. No body or Content-Type is supported. Content-Encoding is absent or one identity token, Transfer-Encoding is absent, and Content-Length is zero or the server proves no body. Unknown/chunked/nonempty framing is rejected before body consumption. Conditional headers and Idempotency-Key are unsupported. Reads never derive, claim, repair, provision storage or start a worker. Current selects the server's full report key for this revision, independently of the mapping head. Optional projectionDerivationId links findings only through that exact verified projection; unavailable selection is opaque 404. Lifecycle, backend status and read refusal are separate. False validation is a usable report. Missing, corrupt, unsupported or over-budget selected payloads retain honest available metadata with a categorical read refusal and null report. The complete wrapper including linked findings is bounded before response bytes and followed by fresh authority/source validation. Local work/read admission is nonwaiting 429; report dependency availability is closed 503. A ResourceLimit wrapper that cannot fit its policy uses a fixed categorical 503 error of at most 1024 bytes. All responses are no-store and carry no ETag.
     */
    get: operations["GetCatalogValidationReport"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/revisions/{revisionId}/reports/validation/{derivationId}": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /**
     * Read one exact retained successful validation report.
     * @description Requires fresh project ReadMetadata authority for the exact actor and full revision ancestry; all four project roles qualify. Enabled features are independent of organization project-creation capability and upload authority. All route and query UUIDs are canonical nonzero lowercase D strings. Unknown, repeated and escaped-duplicate query names are rejected; no actor, digest, backend profile, policy or private object inputs are accepted. No body or Content-Type is supported. Content-Encoding is absent or one identity token, Transfer-Encoding is absent, and Content-Length is zero or the server proves no body. Unknown/chunked/nonempty framing is rejected before body consumption. Conditional headers and Idempotency-Key are unsupported. Reads never derive, claim, repair, provision storage or start a worker. The derivationId selects an immutable successful result, never a pending or failed attempt. Optional projectionDerivationId links findings only through that exact verified projection; unavailable selection is opaque 404. Lifecycle, backend status and read refusal are separate. False validation is a usable report. Missing, corrupt, unsupported or over-budget selected payloads retain honest available metadata with a categorical read refusal and null report. The complete wrapper including linked findings is bounded before response bytes and followed by fresh authority/source validation. Local work/read admission is nonwaiting 429; report dependency availability is closed 503. A ResourceLimit wrapper that cannot fit its policy uses a fixed categorical 503 error of at most 1024 bytes. All responses are no-store and carry no ETag.
     */
    get: operations["GetRetainedCatalogValidationReport"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/revisions/{revisionId}/reports/validation/ensure": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    get?: never;
    put?: never;
    /**
     * Explicitly ensure one scoped persisted validation report.
     * @description Requires fresh project ReadMetadata authority for the exact actor and full revision ancestry; all four project roles qualify. Enabled features are independent of organization project-creation capability and upload authority. All route and query UUIDs are canonical nonzero lowercase D strings. Unknown, repeated and escaped-duplicate query names are rejected; no actor, digest, backend profile, policy or private object inputs are accepted. No body or Content-Type is supported. Content-Encoding is absent or one identity token, Transfer-Encoding is absent, and Content-Length is zero or the server proves no body. Unknown/chunked/nonempty framing is rejected before body consumption. Conditional headers and Idempotency-Key are unsupported. A cookie-bound framework request token and exact configured Origin are required. Optional repairDerivationId selects only the exact current successful result; there is no implicit repair or projection-link selector. Completed or cached observations return 200; actual pending or retry-delayed observations return 202. ClaimLost, SelectionChanged and GenerationExhausted are distinct 409 errors. Lifecycle, backend status and read refusal are separate. False validation is a usable report. Missing, corrupt, unsupported or over-budget selected payloads retain honest available metadata with a categorical read refusal and null report. The complete wrapper including linked findings is bounded before response bytes and followed by fresh authority/source validation. Local work/read admission is nonwaiting 429; report dependency availability is closed 503. A ResourceLimit wrapper that cannot fit its policy uses a fixed categorical 503 error of at most 1024 bytes. All responses are no-store and carry no ETag.
     */
    post: operations["EnsureCatalogValidationReport"];
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
    AuthErrorV1: {
      /** @enum {string} */
      code:
        | "invalidRequest"
        | "authenticationRequired"
        | "requestRejected"
        | "forgetNotAvailable"
        | "unavailable";
      /** @enum {integer} */
      version: 1;
    };
    AuthForgetLocalV1: {
      /** @enum {string} */
      outcome: "revocationUnconfirmed";
      /** @enum {integer} */
      version: 1;
    };
    AuthLoginV1: {
      /** Format: uri */
      authorizationUrl: string;
      /** @enum {integer} */
      version: 1;
    };
    AuthModeStatusV1: {
      /** @enum {string} */
      mode: "development" | "disabled";
      /** @enum {integer} */
      version: 1;
    };
    AuthSessionV1:
      | {
          authActionRequestToken: string;
          identityContext: string;
          /** @enum {string} */
          mode: "session";
          requestToken: string;
          /** @enum {string} */
          state: "authenticated";
          /** @enum {integer} */
          version: 1;
        }
      | {
          authActionRequestToken: string;
          identityContext: null;
          /** @enum {string} */
          mode: "session";
          requestToken: null;
          /** @enum {string} */
          state: "anonymous";
          /** @enum {integer} */
          version: 1;
        }
      | {
          authActionRequestToken: string;
          identityContext: null;
          /** @enum {string} */
          mode: "session";
          requestToken: null;
          /** @enum {string} */
          state: "authenticationRequired";
          /** @enum {integer} */
          version: 1;
        };
    AuthStatusV1:
      | components["schemas"]["AuthSessionV1"]
      | components["schemas"]["AuthModeStatusV1"];
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
      deriveReports: boolean;
      editMetadata: boolean;
      manageMembership: boolean;
      preview: boolean;
      readReports: boolean;
      uploadRevisions: boolean;
      view: boolean;
    };
    /** @enum {unknown} */
    CatalogAdmissionV1: "valid" | "decodedInvalid";
    CatalogContextV1:
      | {
          actorPrincipalId: string;
          allowedOrganizationId: string;
          features: components["schemas"]["CatalogFeaturesV1"];
          /** @enum {string} */
          mode: "development";
          organizationActions: components["schemas"]["CatalogOrganizationActionsV1"];
          organizationName: string;
          requestToken: string;
        }
      | {
          actorPrincipalId: string;
          allowedOrganizationId: string;
          features: components["schemas"]["CatalogFeaturesV1"];
          identityContext: string;
          /** @enum {string} */
          mode: "session";
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
      | "projectCreationAdmissionBusy"
      | "revisionReportAdmissionBusy"
      | "revisionReportClaimLost"
      | "revisionReportSelectionChanged"
      | "revisionReportGenerationExhausted"
      | "revisionReportUnavailable"
      | "revisionReportResourceLimit";
    CatalogErrorV1: {
      code: components["schemas"]["CatalogErrorCodeV1"];
    };
    CatalogFeaturesV1: {
      chat: boolean;
      createMappings: boolean;
      createProjects: boolean;
      deriveReports: boolean;
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
    CatalogPreviewCancelResultV1: {
      outcome: string;
      run: components["schemas"]["CatalogPreviewRunV1"];
    };
    CatalogPreviewFailureV1: {
      code: string;
      message: string;
      messageTruncated: boolean;
      subject: null | string;
      subjectTruncated: boolean;
    };
    CatalogPreviewOutputV1: {
      boundaryId: string;
      /** Format: int32 */
      byteCount: number;
      mediaType: string;
      /** Format: int32 */
      ordinal: number;
      recordsWritten: string;
      sha256: string;
      targetName: string;
      targetNameTruncated: boolean;
    };
    CatalogPreviewProvenanceV1: {
      adapterBuild: string;
      backendBuild: string;
      confirmed: boolean;
      engineRevision: string;
      profile: string;
    };
    CatalogPreviewRunV1: {
      /** Format: int32 */
      attempt: number;
      cancelRequested: boolean;
      /** Format: date-time */
      createdAtUtc: string;
      currentDateTime: string;
      /** Format: date-time */
      expiresAtUtc: string;
      failure: null | components["schemas"]["CatalogPreviewFailureV1"];
      /** Format: date-time */
      finishedAtUtc: null | string;
      /** Format: int32 */
      maxAttempts: number;
      outcome: null | string;
      outputs: components["schemas"]["CatalogPreviewOutputV1"][];
      provenance: null | components["schemas"]["CatalogPreviewProvenanceV1"];
      recordsWritten: null | string;
      requestFingerprint: string;
      revision: components["schemas"]["CatalogRevisionScopeV1"];
      runId: string;
      /** Format: date-time */
      startedAtUtc: null | string;
      state: string;
    };
    CatalogPreviewSubmissionV1: {
      inputs: {
        content: string;
        name?: null | string;
      }[];
      parameters: {
        kind: string;
        name: string;
        value: string;
      }[];
      target: {
        kind: string;
        name?: null | string;
      };
    };
    CatalogPreviewSubmitResultV1: {
      outcome: string;
      run: components["schemas"]["CatalogPreviewRunV1"];
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
    CatalogProjectionReportV1: {
      currentAttempt:
        null | components["schemas"]["CatalogRevisionReportAttemptV1"];
      failure: null | components["schemas"]["CatalogRevisionReportFailureV1"];
      /** Format: date-time */
      nextAttemptAtUtc: null | string;
      /** @enum {string} */
      readonly operation: "projection";
      readOutcome: components["schemas"]["CatalogRevisionReportReadOutcomeV1"];
      report: null | components["schemas"]["ProjectionReportV1"];
      result: null | components["schemas"]["CatalogRevisionReportResultV1"];
      revision: components["schemas"]["CatalogRevisionScopeV1"];
      /** @enum {string} */
      readonly schemaVersion: "weft.catalog-revision-report.v1";
      state: components["schemas"]["CatalogRevisionReportStateV1"];
      status: null | components["schemas"]["OperationStatusV1"];
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
      committedAdmission: null | components["schemas"]["CatalogAdmissionV1"];
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
      receipt?:
        null | components["schemas"]["CatalogRevisionIngestionReceiptV1"];
      rejection?: components["schemas"]["CatalogRevisionIngestionRejectionV1"];
    };
    CatalogRevisionPageV1: {
      nextCursor?: null | string;
      revisions: components["schemas"]["CatalogRevisionV1"][];
    };
    CatalogRevisionReportAttemptV1: {
      /**
       * Format: uuid
       * @description Canonical nonzero lowercase D-format UUID. A failed or pending attempt ID is not a retained success.
       */
      derivationId: string;
      failure: null | components["schemas"]["CatalogRevisionReportFailureV1"];
      /** Format: date-time */
      finishedAtUtc: null | string;
      /** @description Exact canonical positive decimal generation in 1..9223372036854775807; never a JSON number. */
      generation: string;
      /** Format: date-time */
      startedAtUtc: string;
      status: null | components["schemas"]["OperationStatusV1"];
    };
    /** @enum {string} */
    CatalogRevisionReportFailureV1:
      | "objectUnavailable"
      | "storageIntegrity"
      | "storageIo"
      | "backendUnavailable"
      | "backendFailure"
      | "timedOut"
      | "canceled"
      | "isolationFailure"
      | "protocolFailure"
      | "profileUnavailable"
      | "resourceLimit"
      | "unsupportedArtifact";
    /** @enum {string} */
    CatalogRevisionReportReadOutcomeV1:
      | "observed"
      | "objectUnavailable"
      | "storageIntegrity"
      | "unsupportedFormat"
      | "resourceLimit";
    CatalogRevisionReportResultV1: {
      /**
       * Format: uuid
       * @description Canonical nonzero lowercase D-format UUID. A failed or pending attempt ID is not a retained success.
       */
      derivationId: string;
      /** Format: date-time */
      finishedAtUtc: string;
      /** @description Exact canonical positive decimal generation in 1..9223372036854775807; never a JSON number. */
      generation: string;
      /** Format: date-time */
      startedAtUtc: string;
    };
    /** @enum {string} */
    CatalogRevisionReportStateV1:
      "notDerived" | "pending" | "available" | "failed";
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
    CatalogValidationReportV1: {
      currentAttempt:
        null | components["schemas"]["CatalogRevisionReportAttemptV1"];
      failure: null | components["schemas"]["CatalogRevisionReportFailureV1"];
      /** Format: date-time */
      nextAttemptAtUtc: null | string;
      /** @enum {string} */
      readonly operation: "validation";
      /** Format: uuid */
      projectionDerivationId: null | string;
      readOutcome: components["schemas"]["CatalogRevisionReportReadOutcomeV1"];
      report: null | components["schemas"]["ValidationReportV1"];
      result: null | components["schemas"]["CatalogRevisionReportResultV1"];
      revision: components["schemas"]["CatalogRevisionScopeV1"];
      /** @enum {string} */
      readonly schemaVersion: "weft.catalog-revision-report.v1";
      state: components["schemas"]["CatalogRevisionReportStateV1"];
      status: null | components["schemas"]["OperationStatusV1"];
    };
    ChatAvailabilityV1: {
      limits: components["schemas"]["ChatLimitsV1"];
      profiles: components["schemas"]["ChatProfileV1"][];
      revision: components["schemas"]["CatalogRevisionScopeV1"];
      /** @enum {string} */
      readonly schemaVersion: "weft.chat.v1";
      toolsEnabled: boolean;
    };
    ChatConversationCreateV1: {
      conversationId: string;
    };
    ChatConversationDetailV1: {
      conversation: components["schemas"]["ChatConversationV1"];
      hasEarlierTurns: boolean;
      turns: components["schemas"]["ChatTurnV1"][];
    };
    ChatConversationPageV1: {
      conversations: components["schemas"]["ChatConversationV1"][];
      hasMore: boolean;
    };
    ChatConversationV1: {
      conversationId: string;
      /** Format: date-time */
      createdAtUtc: string;
      /** Format: date-time */
      lastTurnAtUtc: null | string;
      projectionDerivationId: string;
      /** Format: date-time */
      retainUntilUtc: string;
      revision: components["schemas"]["CatalogRevisionScopeV1"];
      title: null | string;
      /** Format: int32 */
      turnCount: number;
      validationDerivationId: string;
    };
    /** @enum {unknown} */
    ChatErrorCodeV1:
      | "unavailable"
      | "invalidRequest"
      | "notFound"
      | "identityRejected"
      | "persistenceFailure"
      | "internalFailure"
      | "unsupportedMediaType"
      | "bodyLimit"
      | "reportsUnavailable"
      | "profileUnavailable"
      | "turnActive"
      | "idempotencyConflict"
      | "turnNotStarted";
    ChatErrorV1: {
      code: components["schemas"]["ChatErrorCodeV1"];
    };
    ChatLimitsV1: {
      /** Format: int32 */
      maxSelectors: number;
      /** Format: int32 */
      maxTextBytes: number;
      /** Format: int32 */
      turnSeconds: number;
    };
    ChatProfileV1: {
      displayName: string;
      modelId: string;
      profileId: string;
      providerId: string;
      supportsTools: boolean;
      /** Format: int32 */
      version: number;
    };
    /** @enum {string} */
    ChatProviderFailureV1:
      | "invalidRequest"
      | "profileUnavailable"
      | "metadataDisallowed"
      | "capabilityUnavailable"
      | "credentialUnavailable"
      | "busy"
      | "budgetExceeded"
      | "cancelled"
      | "deadlineExceeded"
      | "transportFailure"
      | "providerAuthentication"
      | "providerRateLimited"
      | "providerRejected"
      | "providerUnavailable"
      | "unsupportedResponse"
      | "malformedResponse"
      | "truncatedResponse";
    /** @enum {unknown} */
    ChatSelectorKindV1:
      | "boundary"
      | "field"
      | "parameter"
      | "rule"
      | "control"
      | "projectionDiagnostic"
      | "validationDiagnostic"
      | "projectionReport"
      | "validationReport";
    ChatSelectorV1: {
      /** Format: int32 */
      diagnosticOrdinal: null | number;
      kind: components["schemas"]["ChatSelectorKindV1"];
      resourceId: null | string;
    };
    ChatTurnCreateV1: {
      profileId: string;
      /** Format: int32 */
      profileVersion: number;
      selectors: {
        /** Format: int32 */
        diagnosticOrdinal: null | number;
        /** @enum {unknown} */
        kind:
          | "boundary"
          | "field"
          | "parameter"
          | "rule"
          | "control"
          | "projectionDiagnostic"
          | "validationDiagnostic"
          | "projectionReport"
          | "validationReport";
        resourceId: null | string;
      }[];
      text: string;
      turnId: string;
    };
    ChatTurnEventV1:
      | components["schemas"]["ChatTurnEventV1ChatTurnStartedEventV1"]
      | components["schemas"]["ChatTurnEventV1ChatTextDeltaEventV1"]
      | components["schemas"]["ChatTurnEventV1ChatToolStartedEventV1"]
      | components["schemas"]["ChatTurnEventV1ChatToolCompletedEventV1"]
      | components["schemas"]["ChatTurnEventV1ChatUsageEventV1"]
      | components["schemas"]["ChatTurnEventV1ChatCitationEventV1"]
      | components["schemas"]["ChatTurnEventV1ChatTurnCompletedEventV1"]
      | components["schemas"]["ChatTurnEventV1ChatTurnFailedEventV1"]
      | components["schemas"]["ChatTurnEventV1ChatTurnCancelledEventV1"];
    ChatTurnEventV1ChatCitationEventV1: {
      reportDerivationId: string;
      roundId: string;
      /** Format: int32 */
      sequence: number;
      target: components["schemas"]["ChatSelectorV1"];
      /** @enum {string} */
      type: "citation";
    };
    ChatTurnEventV1ChatTextDeltaEventV1: {
      roundId: string;
      /** Format: int32 */
      sequence: number;
      text: string;
      /** @enum {string} */
      type: "text.delta";
    };
    ChatTurnEventV1ChatToolCompletedEventV1: {
      name: string;
      /** Format: int32 */
      ordinal: number;
      /** Format: int32 */
      resultBytes: number;
      /** Format: int32 */
      retrievedNodes: number;
      roundId: string;
      /** Format: int32 */
      sequence: number;
      /** @enum {string} */
      type: "tool.completed";
    };
    ChatTurnEventV1ChatToolStartedEventV1: {
      name: string;
      /** Format: int32 */
      ordinal: number;
      roundId: string;
      /** Format: int32 */
      sequence: number;
      /** @enum {string} */
      type: "tool.started";
    };
    ChatTurnEventV1ChatTurnCancelledEventV1: {
      /** Format: int32 */
      sequence: number;
      /** @enum {string} */
      type: "turn.cancelled";
    };
    ChatTurnEventV1ChatTurnCompletedEventV1: {
      /** Format: int32 */
      sequence: number;
      /** @enum {string} */
      type: "turn.completed";
    };
    ChatTurnEventV1ChatTurnFailedEventV1: {
      providerFailure: null | components["schemas"]["ChatProviderFailureV1"];
      reason: components["schemas"]["ChatTurnFailureV1"];
      /** Format: int32 */
      sequence: number;
      /** @enum {string} */
      type: "turn.failed";
    };
    ChatTurnEventV1ChatTurnStartedEventV1: {
      /** Format: int32 */
      sequence: number;
      /** @enum {string} */
      type: "turn.started";
    };
    ChatTurnEventV1ChatUsageEventV1: {
      /** Format: int64 */
      inputTokens: null | number;
      origin: components["schemas"]["ChatUsageOriginV1"];
      /** Format: int64 */
      outputTokens: null | number;
      roundId: string;
      /** Format: int32 */
      sequence: number;
      /** Format: int64 */
      totalTokens: null | number;
      /** @enum {string} */
      type: "usage";
    };
    /** @enum {unknown} */
    ChatTurnFailureV1:
      | "provider"
      | "outputLimit"
      | "refused"
      | "contentFiltered"
      | "interrupted"
      | "stopped";
    /** @enum {unknown} */
    ChatTurnPhaseV1:
      "active" | "completed" | "failed" | "cancelled" | "interrupted";
    ChatTurnProfileV1: {
      modelId: string;
      profileId: string;
      providerId: string;
      /** Format: int32 */
      version: number;
    };
    ChatTurnV1: {
      conversationId: string;
      /** Format: date-time */
      createdAtUtc: string;
      /** Format: date-time */
      deadlineUtc: string;
      /** Format: date-time */
      finishedAtUtc: null | string;
      /** Format: int32 */
      lastSequence: number;
      phase: components["schemas"]["ChatTurnPhaseV1"];
      profile: components["schemas"]["ChatTurnProfileV1"];
      selectors: components["schemas"]["ChatSelectorV1"][];
      text: string;
      turnId: string;
    };
    /** @enum {unknown} */
    ChatUsageOriginV1: "observation" | "roundTerminal" | "openRejection";
    /** @enum {unknown} */
    ComparisonAspectV1:
      | "shape"
      | "dataType"
      | "cardinality"
      | "requirement"
      | "nullability"
      | "value"
      | "condition"
      | "default"
      | "details"
      | "control"
      | "relationship"
      | "presentation"
      | "validationValidity"
      | "validationCounts";
    ComparisonCandidateV1: {
      node: null | components["schemas"]["ProjectionNodeReference"];
      relationshipId: null | string;
    };
    /** @enum {unknown} */
    ComparisonEntityV1:
      | "boundary"
      | "field"
      | "parameter"
      | "rule"
      | "control"
      | "relationship"
      | "validation";
    ComparisonFacetV1: {
      aspect: components["schemas"]["ComparisonAspectV1"];
      hasObservedDifference: boolean;
      hasUncertainty: boolean;
      reasons: components["schemas"]["ComparisonReasonV1"][];
    };
    ComparisonItemV1: {
      candidatesTruncated: boolean;
      entity: components["schemas"]["ComparisonEntityV1"];
      facets: components["schemas"]["ComparisonFacetV1"][];
      left: components["schemas"]["ComparisonCandidateV1"][];
      /** Format: int32 */
      leftCandidateCount: number;
      match: components["schemas"]["ComparisonMatchV1"];
      right: components["schemas"]["ComparisonCandidateV1"][];
      /** Format: int32 */
      rightCandidateCount: number;
    };
    /** @enum {unknown} */
    ComparisonMatchV1: "matched" | "onlyLeft" | "onlyRight" | "ambiguous";
    /** @enum {unknown} */
    ComparisonReasonV1:
      | "incompleteCoverage"
      | "unknownFact"
      | "unsupportedDetail"
      | "incompatibleProfile"
      | "ambiguousMatch"
      | "unresolvedReference"
      | "missingValidation"
      | "truncatedValidation"
      | "candidatesOmitted"
      | "itemsOmitted";
    ComparisonReportCitationV1: {
      derivationId: string;
      generation: string;
      keyFingerprint: string;
    };
    ComparisonSideCitationV1: {
      nativeByteCount: string;
      nativeDigest: components["schemas"]["ArtifactDigest"];
      projection: components["schemas"]["ComparisonReportCitationV1"];
      projectionProvenance: components["schemas"]["ProjectionProvenance"];
      revision: components["schemas"]["ScopedRevisionId"];
      sequence: string;
      validation: null | components["schemas"]["ComparisonReportCitationV1"];
    };
    ComparisonValidationSummaryV1: {
      /** Format: int32 */
      emittedIssueCount: number;
      hasTruncatedDiagnosticText: boolean;
      isValid: boolean;
      /** Format: int32 */
      totalIssueCount: number;
      truncated: boolean;
    };
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
    /** @enum {string} */
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
    RegressionCaseCapabilitiesV1: {
      comparisonPolicy: string;
      limits: components["schemas"]["RegressionLimitsV1"];
      publishCases: boolean;
      readSamples: boolean;
      runtime: components["schemas"]["RegressionRuntimeV1"];
    };
    RegressionCaseDetailV1: {
      caseId: string;
      definition: components["schemas"]["RegressionCasePutV1"];
      revision: components["schemas"]["CatalogRevisionScopeV1"];
      version: string;
    };
    RegressionCaseMetadataV1: {
      caseId: string;
      createdAtUtc: string;
      name: string;
      revision: components["schemas"]["CatalogRevisionScopeV1"];
      version: string;
    };
    RegressionCasePageV1: {
      cases: components["schemas"]["RegressionCaseMetadataV1"][];
      nextCaseId: null | string;
    };
    RegressionCasePutV1: {
      comparisonPolicy: string;
      currentDateTime: string;
      expected: components["schemas"]["RegressionDocumentV1"][];
      limits: components["schemas"]["RegressionLimitsV1"];
      name: string;
      namedInputs: components["schemas"]["RegressionDocumentV1"][];
      parameters: components["schemas"]["RegressionParameterV1"][];
      primaryBase64Url: string;
      revisionId: string;
      runtime: components["schemas"]["RegressionRuntimeV1"];
      target: components["schemas"]["RegressionTargetV1"];
    };
    RegressionCaseSavedV1: {
      caseId: string;
      outcome: string;
      revision: components["schemas"]["CatalogRevisionScopeV1"];
      version: string;
    };
    RegressionComparisonV1: {
      /** Format: int32 */
      actualOutputs: number;
      differences: components["schemas"]["RegressionDifferenceV1"][];
      /** Format: int32 */
      expectedOutputs: number;
      notEvaluatedReason: string;
      outcome: string;
      policy: string;
      /** Format: int32 */
      totalDifferences: number;
      truncated: boolean;
    };
    RegressionDifferenceV1: {
      /** Format: int32 */
      actualBytes: null | number;
      actualDigest: null | string;
      /** Format: int32 */
      expectedBytes: null | number;
      expectedDigest: null | string;
      /** Format: int32 */
      firstDifferentByte: null | number;
      kind: string;
      name: null | string;
    };
    RegressionDocumentV1: {
      bytesBase64Url: string;
      name: null | string;
    };
    RegressionLimitsV1: {
      /** Format: int32 */
      maximumArtifacts: number;
      /** Format: int32 */
      maximumInputBytes: number;
      /** Format: int32 */
      maximumOutputBytes: number;
      /** Format: int32 */
      maximumTextBytes: number;
    };
    RegressionParameterV1: {
      kind: string;
      name: string;
      value: string;
    };
    RegressionRunPageV1: {
      nextRunId: null | string;
      runs: components["schemas"]["RegressionRunSummaryV1"][];
    };
    RegressionRunPutV1: {
      caseId: string;
      version: string;
    };
    RegressionRunReceiptV1: {
      outcome: string;
      runId: string;
    };
    RegressionRunSummaryV1: {
      /** Format: int32 */
      attempt: number;
      caseId: string;
      createdAtUtc: string;
      revision: components["schemas"]["CatalogRevisionScopeV1"];
      runId: string;
      state: string;
      version: string;
    };
    RegressionRuntimeV1: {
      adapterBuild: string;
      backendBuild: string;
      backendId: string;
      engineRevision: string;
      profile: string;
    };
    RegressionRunV1: {
      comparison: null | components["schemas"]["RegressionComparisonV1"];
      executionOutcome: null | string;
      outputs: null | components["schemas"]["RegressionDocumentV1"][];
      previewStatus: null | string;
      run: components["schemas"]["RegressionRunSummaryV1"];
    };
    RegressionTargetV1: {
      kind: string;
      name: null | string;
    };
    /** @enum {unknown} */
    RelationshipKind:
      | "readsFrom"
      | "writesTo"
      | "governedBy"
      | "dependsOn"
      | "produces"
      | "boundaryMember";
    RevisionComparisonV1: {
      /** Format: int32 */
      emittedItemCount: number;
      hasObservedDifferences: boolean;
      hasUncertainty: boolean;
      items: components["schemas"]["ComparisonItemV1"][];
      left: components["schemas"]["ComparisonSideCitationV1"];
      leftCoverage: components["schemas"]["ProjectionCoverage"];
      leftValidation:
        null | components["schemas"]["ComparisonValidationSummaryV1"];
      policyVersion: string;
      right: components["schemas"]["ComparisonSideCitationV1"];
      rightCoverage: components["schemas"]["ProjectionCoverage"];
      rightValidation:
        null | components["schemas"]["ComparisonValidationSummaryV1"];
      schemaVersion: string;
      /** Format: int32 */
      totalItemCount: number;
      truncated: boolean;
      validationIncluded: boolean;
    };
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
  CompleteEntraLogin: {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    requestBody?: never;
    responses: {
      /** @description Completed only after the fixed protocol acknowledges success. */
      303: {
        headers: {
          "Cache-Control"?: "no-store";
          Location?: "/";
          "Referrer-Policy"?: "no-referrer";
          [name: string]: unknown;
        };
        content?: never;
      };
      /** @description Fixed categorical readable HTML; no dynamic request/provider details. */
      401: {
        headers: {
          "Cache-Control"?: "no-store";
          "Referrer-Policy"?: "no-referrer";
          [name: string]: unknown;
        };
        content: {
          "text/html": string;
        };
      };
      /** @description Closed categorical failure; no success or automatic retry. */
      405: {
        headers: {
          "Cache-Control"?: "no-store";
          "Referrer-Policy"?: "no-referrer";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AuthErrorV1"];
        };
      };
      /** @description Fixed categorical readable HTML; no dynamic request/provider details. */
      503: {
        headers: {
          "Cache-Control"?: "no-store";
          "Referrer-Policy"?: "no-referrer";
          [name: string]: unknown;
        };
        content: {
          "text/html": string;
        };
      };
    };
  };
  ForgetDeniedLocalSession: {
    parameters: {
      query?: never;
      header: {
        Origin: string;
        "X-Weft-Auth-Token": string;
        "X-Weft-Forget-Local": "1";
      };
      path?: never;
      cookie?: never;
    };
    requestBody?: never;
    responses: {
      /** @description Completed only after the fixed protocol acknowledges success. */
      200: {
        headers: {
          "Cache-Control"?: "no-store";
          "Referrer-Policy"?: "no-referrer";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AuthForgetLocalV1"];
        };
      };
      /** @description Closed categorical failure; no success or automatic retry. */
      400: {
        headers: {
          "Cache-Control"?: "no-store";
          "Referrer-Policy"?: "no-referrer";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AuthErrorV1"];
        };
      };
      /** @description Closed categorical failure; no success or automatic retry. */
      401: {
        headers: {
          "Cache-Control"?: "no-store";
          "Referrer-Policy"?: "no-referrer";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AuthErrorV1"];
        };
      };
      /** @description Closed categorical failure; no success or automatic retry. */
      403: {
        headers: {
          "Cache-Control"?: "no-store";
          "Referrer-Policy"?: "no-referrer";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AuthErrorV1"];
        };
      };
      /** @description Closed categorical failure; no success or automatic retry. */
      405: {
        headers: {
          "Cache-Control"?: "no-store";
          "Referrer-Policy"?: "no-referrer";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AuthErrorV1"];
        };
      };
      /** @description Closed categorical failure; no success or automatic retry. */
      409: {
        headers: {
          "Cache-Control"?: "no-store";
          "Referrer-Policy"?: "no-referrer";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AuthErrorV1"];
        };
      };
      /** @description Closed categorical failure; no success or automatic retry. */
      503: {
        headers: {
          "Cache-Control"?: "no-store";
          "Referrer-Policy"?: "no-referrer";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AuthErrorV1"];
        };
      };
    };
  };
  BeginBrowserLogin: {
    parameters: {
      query?: never;
      header: {
        Origin: string;
        "X-Weft-Auth-Token": string;
        "X-Weft-Login": "1";
        "X-XSRF-TOKEN"?: string;
      };
      path?: never;
      cookie?: never;
    };
    requestBody?: never;
    responses: {
      /** @description Completed only after the fixed protocol acknowledges success. */
      200: {
        headers: {
          "Cache-Control"?: "no-store";
          "Referrer-Policy"?: "no-referrer";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AuthLoginV1"];
        };
      };
      /** @description Closed categorical failure; no success or automatic retry. */
      400: {
        headers: {
          "Cache-Control"?: "no-store";
          "Referrer-Policy"?: "no-referrer";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AuthErrorV1"];
        };
      };
      /** @description Closed categorical failure; no success or automatic retry. */
      401: {
        headers: {
          "Cache-Control"?: "no-store";
          "Referrer-Policy"?: "no-referrer";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AuthErrorV1"];
        };
      };
      /** @description Closed categorical failure; no success or automatic retry. */
      403: {
        headers: {
          "Cache-Control"?: "no-store";
          "Referrer-Policy"?: "no-referrer";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AuthErrorV1"];
        };
      };
      /** @description Closed categorical failure; no success or automatic retry. */
      405: {
        headers: {
          "Cache-Control"?: "no-store";
          "Referrer-Policy"?: "no-referrer";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AuthErrorV1"];
        };
      };
      /** @description Closed categorical failure; no success or automatic retry. */
      409: {
        headers: {
          "Cache-Control"?: "no-store";
          "Referrer-Policy"?: "no-referrer";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AuthErrorV1"];
        };
      };
      /** @description Closed categorical failure; no success or automatic retry. */
      503: {
        headers: {
          "Cache-Control"?: "no-store";
          "Referrer-Policy"?: "no-referrer";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AuthErrorV1"];
        };
      };
    };
  };
  LogoutBrowserSession: {
    parameters: {
      query?: never;
      header: {
        Origin: string;
        "X-Weft-Auth-Token": string;
        "X-Weft-Logout": "1";
        "X-XSRF-TOKEN"?: string;
      };
      path?: never;
      cookie?: never;
    };
    requestBody?: never;
    responses: {
      /** @description Completed only after the fixed protocol acknowledges success. */
      204: {
        headers: {
          "Cache-Control"?: "no-store";
          "Referrer-Policy"?: "no-referrer";
          [name: string]: unknown;
        };
        content?: never;
      };
      /** @description Closed categorical failure; no success or automatic retry. */
      400: {
        headers: {
          "Cache-Control"?: "no-store";
          "Referrer-Policy"?: "no-referrer";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AuthErrorV1"];
        };
      };
      /** @description Closed categorical failure; no success or automatic retry. */
      401: {
        headers: {
          "Cache-Control"?: "no-store";
          "Referrer-Policy"?: "no-referrer";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AuthErrorV1"];
        };
      };
      /** @description Closed categorical failure; no success or automatic retry. */
      403: {
        headers: {
          "Cache-Control"?: "no-store";
          "Referrer-Policy"?: "no-referrer";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AuthErrorV1"];
        };
      };
      /** @description Closed categorical failure; no success or automatic retry. */
      405: {
        headers: {
          "Cache-Control"?: "no-store";
          "Referrer-Policy"?: "no-referrer";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AuthErrorV1"];
        };
      };
      /** @description Closed categorical failure; no success or automatic retry. */
      409: {
        headers: {
          "Cache-Control"?: "no-store";
          "Referrer-Policy"?: "no-referrer";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AuthErrorV1"];
        };
      };
      /** @description Closed categorical failure; no success or automatic retry. */
      503: {
        headers: {
          "Cache-Control"?: "no-store";
          "Referrer-Policy"?: "no-referrer";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AuthErrorV1"];
        };
      };
    };
  };
  ReadBrowserAuthentication: {
    parameters: {
      query?: never;
      header: {
        "X-Weft-Session": "1";
      };
      path?: never;
      cookie?: never;
    };
    requestBody?: never;
    responses: {
      /** @description Completed only after the fixed protocol acknowledges success. */
      200: {
        headers: {
          "Cache-Control"?: "no-store";
          "Referrer-Policy"?: "no-referrer";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AuthStatusV1"];
        };
      };
      /** @description Closed categorical failure; no success or automatic retry. */
      400: {
        headers: {
          "Cache-Control"?: "no-store";
          "Referrer-Policy"?: "no-referrer";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AuthErrorV1"];
        };
      };
      /** @description Closed categorical failure; no success or automatic retry. */
      401: {
        headers: {
          "Cache-Control"?: "no-store";
          "Referrer-Policy"?: "no-referrer";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AuthErrorV1"];
        };
      };
      /** @description Closed categorical failure; no success or automatic retry. */
      403: {
        headers: {
          "Cache-Control"?: "no-store";
          "Referrer-Policy"?: "no-referrer";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AuthErrorV1"];
        };
      };
      /** @description Closed categorical failure; no success or automatic retry. */
      405: {
        headers: {
          "Cache-Control"?: "no-store";
          "Referrer-Policy"?: "no-referrer";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AuthErrorV1"];
        };
      };
      /** @description Closed categorical failure; no success or automatic retry. */
      409: {
        headers: {
          "Cache-Control"?: "no-store";
          "Referrer-Policy"?: "no-referrer";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AuthErrorV1"];
        };
      };
      /** @description Closed categorical failure; no success or automatic retry. */
      503: {
        headers: {
          "Cache-Control"?: "no-store";
          "Referrer-Policy"?: "no-referrer";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AuthErrorV1"];
        };
      };
    };
  };
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
      /** @description Unauthorized */
      401: {
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
      /** @description Service Unavailable */
      503: {
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
  GetCatalogPreview: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        organizationId: string;
        runId: string;
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
          "application/json": components["schemas"]["CatalogPreviewRunV1"];
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
      /** @description Unauthorized */
      401: {
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
      /** @description Service Unavailable */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
    };
  };
  CancelCatalogPreview: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        organizationId: string;
        runId: string;
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
          "application/json": components["schemas"]["CatalogPreviewCancelResultV1"];
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
      /** @description Unauthorized */
      401: {
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
      /** @description Service Unavailable */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
    };
  };
  GetCatalogPreviewOutput: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        ordinal: string;
        organizationId: string;
        runId: string;
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
      /** @description Unauthorized */
      401: {
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
      /** @description Unauthorized */
      401: {
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
      /** @description Service Unavailable */
      503: {
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
      /** @description Unauthorized */
      401: {
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
      /** @description Unauthorized */
      401: {
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
      /** @description Service Unavailable */
      503: {
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
      /** @description Unauthorized */
      401: {
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
      /** @description Service Unavailable */
      503: {
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
      /** @description Unauthorized */
      401: {
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
      /** @description Unauthorized */
      401: {
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
      /** @description Service Unavailable */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
    };
  };
  GetCatalogRevisionComparison: {
    parameters: {
      query: {
        /** @description Exact retained successful projection derivation of the left revision. */
        leftProjection: string;
        /** @description Exact left revision of this mapping. */
        leftRevision: string;
        /** @description Exact retained validation derivation of the left revision, linked through its projection. */
        leftValidation?: string;
        /** @description Exact retained successful projection derivation of the right revision. */
        rightProjection: string;
        /** @description Exact right revision of this mapping. */
        rightRevision: string;
        /** @description Exact retained validation derivation of the right revision, linked through its projection. */
        rightValidation?: string;
      };
      header?: {
        /** @description Absent or a single identity token; no other coding or request body is accepted. */
        "Content-Encoding"?: string;
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
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["RevisionComparisonV1"];
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
      /** @description Unauthorized */
      401: {
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
      /** @description Unsupported Media Type */
      415: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Too Many Requests */
      429: {
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
      /** @description Service Unavailable */
      503: {
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
      /** @description Unauthorized */
      401: {
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
      /** @description Service Unavailable */
      503: {
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
      /** @description Unauthorized */
      401: {
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
      /** @description Service Unavailable */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
    };
  };
  GetRegressionCases: {
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
          "application/json": components["schemas"]["RegressionCasePageV1"];
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
      /** @description Unauthorized */
      401: {
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
      /** @description Service Unavailable */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
    };
  };
  GetRegressionCaseVersion: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        caseId: string;
        mappingId: string;
        organizationId: string;
        projectId: string;
        version: string;
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
          "application/json": components["schemas"]["RegressionCaseDetailV1"];
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
      /** @description Unauthorized */
      401: {
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
      /** @description Service Unavailable */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
    };
  };
  PutRegressionCaseVersion: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        caseId: string;
        mappingId: string;
        organizationId: string;
        projectId: string;
        version: string;
      };
      cookie?: never;
    };
    requestBody: {
      content: {
        "application/vnd.weft.regression-case.v1+json": components["schemas"]["RegressionCasePutV1"];
      };
    };
    responses: {
      /** @description OK */
      200: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["RegressionCaseSavedV1"];
        };
      };
      /** @description Created */
      201: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["RegressionCaseSavedV1"];
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
      /** @description Unauthorized */
      401: {
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
      /** @description Service Unavailable */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
    };
  };
  GetRegressionCaseCapabilities: {
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
          "application/json": components["schemas"]["RegressionCaseCapabilitiesV1"];
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
      /** @description Unauthorized */
      401: {
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
      /** @description Service Unavailable */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
    };
  };
  GetRegressionRuns: {
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
          "application/json": components["schemas"]["RegressionRunPageV1"];
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
      /** @description Unauthorized */
      401: {
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
      /** @description Service Unavailable */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
    };
  };
  GetRegressionRun: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        mappingId: string;
        organizationId: string;
        projectId: string;
        runId: string;
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
          "application/json": components["schemas"]["RegressionRunV1"];
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
      /** @description Unauthorized */
      401: {
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
      /** @description Service Unavailable */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
    };
  };
  PutRegressionRun: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        mappingId: string;
        organizationId: string;
        projectId: string;
        runId: string;
      };
      cookie?: never;
    };
    requestBody: {
      content: {
        "application/vnd.weft.regression-run.v1+json": components["schemas"]["RegressionRunPutV1"];
      };
    };
    responses: {
      /** @description OK */
      200: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["RegressionRunReceiptV1"];
        };
      };
      /** @description Created */
      201: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["RegressionRunReceiptV1"];
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
      /** @description Unauthorized */
      401: {
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
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Too Many Requests */
      429: {
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
      /** @description Service Unavailable */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
    };
  };
  CancelRegressionRun: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        mappingId: string;
        organizationId: string;
        projectId: string;
        runId: string;
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
          "application/json": components["schemas"]["RegressionRunV1"];
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
      /** @description Unauthorized */
      401: {
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
      /** @description Service Unavailable */
      503: {
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
      /** @description Unauthorized */
      401: {
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
      /** @description Service Unavailable */
      503: {
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
      /** @description Unauthorized */
      401: {
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
      /** @description Unauthorized */
      401: {
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
      /** @description Service Unavailable */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
    };
  };
  GetChatAvailability: {
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
          "application/json": components["schemas"]["ChatAvailabilityV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Payload Too Large */
      413: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unsupported Media Type */
      415: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Internal Server Error */
      500: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
    };
  };
  GetChatConversations: {
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
          "application/json": components["schemas"]["ChatConversationPageV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Payload Too Large */
      413: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unsupported Media Type */
      415: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Internal Server Error */
      500: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
    };
  };
  CreateChatConversation: {
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
    requestBody: {
      content: {
        "application/json": components["schemas"]["ChatConversationCreateV1"];
      };
    };
    responses: {
      /** @description OK */
      200: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatConversationV1"];
        };
      };
      /** @description Created */
      201: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatConversationV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Payload Too Large */
      413: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unsupported Media Type */
      415: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Internal Server Error */
      500: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
    };
  };
  GetChatConversation: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        conversationId: string;
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
          "application/json": components["schemas"]["ChatConversationDetailV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Payload Too Large */
      413: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unsupported Media Type */
      415: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Internal Server Error */
      500: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
    };
  };
  CreateChatTurn: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        conversationId: string;
        mappingId: string;
        organizationId: string;
        projectId: string;
        revisionId: string;
      };
      cookie?: never;
    };
    requestBody: {
      content: {
        "application/json": components["schemas"]["ChatTurnCreateV1"];
      };
    };
    responses: {
      /** @description OK */
      200: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatTurnV1"];
        };
      };
      /** @description Created */
      201: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatTurnV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Payload Too Large */
      413: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unsupported Media Type */
      415: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Internal Server Error */
      500: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
    };
  };
  CancelChatTurn: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        conversationId: string;
        mappingId: string;
        organizationId: string;
        projectId: string;
        revisionId: string;
        turnId: string;
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
          "application/json": components["schemas"]["ChatTurnV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Payload Too Large */
      413: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unsupported Media Type */
      415: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Internal Server Error */
      500: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
    };
  };
  StreamChatTurnEvents: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        conversationId: string;
        mappingId: string;
        organizationId: string;
        projectId: string;
        revisionId: string;
        turnId: string;
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
          "text/event-stream": components["schemas"]["ChatTurnEventV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Payload Too Large */
      413: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unsupported Media Type */
      415: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Internal Server Error */
      500: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
    };
  };
  SubmitCatalogPreview: {
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
    requestBody: {
      content: {
        "application/json": components["schemas"]["CatalogPreviewSubmissionV1"];
      };
    };
    responses: {
      /** @description OK */
      200: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogPreviewSubmitResultV1"];
        };
      };
      /** @description Created */
      201: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogPreviewSubmitResultV1"];
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
      /** @description Unauthorized */
      401: {
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
      /** @description Service Unavailable */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
    };
  };
  GetCatalogProjectionReport: {
    parameters: {
      query?: never;
      header?: {
        /** @description Absent or a single identity token; no other coding or request body is accepted. */
        "Content-Encoding"?: string;
      };
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
          /** @description Always no-store. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogProjectionReportV1"];
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
      /** @description Unauthorized */
      401: {
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
      /** @description Too Many Requests */
      429: {
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
  GetRetainedCatalogProjectionReport: {
    parameters: {
      query?: never;
      header?: {
        /** @description Absent or a single identity token; no other coding or request body is accepted. */
        "Content-Encoding"?: string;
      };
      path: {
        derivationId: string;
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
          /** @description Always no-store. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogProjectionReportV1"];
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
      /** @description Unauthorized */
      401: {
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
      /** @description Too Many Requests */
      429: {
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
  EnsureCatalogProjectionReport: {
    parameters: {
      query?: {
        /** @description Exact selected successful derivation to repair explicitly; no current-result substitution. */
        repairDerivationId?: string;
      };
      header: {
        /** @description Absent or a single identity token; no other coding or request body is accepted. */
        "Content-Encoding"?: string;
        /** @description One exact configured canonical browser origin. */
        Origin: string;
        /** @description Framework cookie-bound token issued by an admitted catalog context. */
        "X-XSRF-TOKEN": string;
      };
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
          /** @description Always no-store. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogProjectionReportV1"];
        };
      };
      /** @description Accepted */
      202: {
        headers: {
          /** @description Always no-store. */
          "Cache-Control"?: "no-store";
          /** @description Canonical full-scoped current report GET URL; future reads independently recheck authority. */
          Location?: string;
          /** @description Optional positive integer seconds from the actual database-observed next-attempt time. */
          "Retry-After"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogProjectionReportV1"];
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
      /** @description Unauthorized */
      401: {
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
      /** @description Too Many Requests */
      429: {
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
  GetCatalogValidationReport: {
    parameters: {
      query?: {
        /** @description Exact verified projection result for optional diagnostic linking. */
        projectionDerivationId?: string;
      };
      header?: {
        /** @description Absent or a single identity token; no other coding or request body is accepted. */
        "Content-Encoding"?: string;
      };
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
          /** @description Always no-store. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogValidationReportV1"];
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
      /** @description Unauthorized */
      401: {
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
      /** @description Too Many Requests */
      429: {
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
  GetRetainedCatalogValidationReport: {
    parameters: {
      query?: {
        /** @description Exact verified projection result for optional diagnostic linking. */
        projectionDerivationId?: string;
      };
      header?: {
        /** @description Absent or a single identity token; no other coding or request body is accepted. */
        "Content-Encoding"?: string;
      };
      path: {
        derivationId: string;
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
          /** @description Always no-store. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogValidationReportV1"];
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
      /** @description Unauthorized */
      401: {
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
      /** @description Too Many Requests */
      429: {
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
  EnsureCatalogValidationReport: {
    parameters: {
      query?: {
        /** @description Exact selected successful derivation to repair explicitly; no current-result substitution. */
        repairDerivationId?: string;
      };
      header: {
        /** @description Absent or a single identity token; no other coding or request body is accepted. */
        "Content-Encoding"?: string;
        /** @description One exact configured canonical browser origin. */
        Origin: string;
        /** @description Framework cookie-bound token issued by an admitted catalog context. */
        "X-XSRF-TOKEN": string;
      };
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
          /** @description Always no-store. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogValidationReportV1"];
        };
      };
      /** @description Accepted */
      202: {
        headers: {
          /** @description Always no-store. */
          "Cache-Control"?: "no-store";
          /** @description Canonical full-scoped current report GET URL; future reads independently recheck authority. */
          Location?: string;
          /** @description Optional positive integer seconds from the actual database-observed next-attempt time. */
          "Retry-After"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogValidationReportV1"];
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
      /** @description Unauthorized */
      401: {
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
      /** @description Too Many Requests */
      429: {
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
      /** @description Unauthorized */
      401: {
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
      /** @description Service Unavailable */
      503: {
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
      /** @description Unauthorized */
      401: {
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
