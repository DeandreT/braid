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
     * @description Supported contract; presence does not activate a runtime route or authentication mode. Bodyless, bounded, no-store/no-ETag/no-referrer. No bearer/ambient-claim identity. Only sign-in may select where it returns: an optional X-Weft-Return-To application path, validated by the shared browser return-path rule and carried in the protected state. Exactly code,state,session_state,client_info,iss,error,error_description,error_uri; protected state at most 12288 characters within the unchanged 16384-byte callback query bound. No duplicates, token fields or code+error. Success only 303 to the validated return path, or '/' when sign-in named none, after acknowledged fresh issuance; failure is fixed readable401/503 HTML, never provider text.
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
     * @description Supported contract; presence does not activate a runtime route or authentication mode. Bodyless, bounded, no-store/no-ETag/no-referrer. No bearer/ambient-claim identity. Only sign-in may select where it returns: an optional X-Weft-Return-To application path, validated by the shared browser return-path rule and carried in the protected state. One exact action header. Unsafe actions require exact Origin and separate standard auth-action AF; positive sessions additionally require112 epoch AF. Session fields/fingerprint are non-authority and memory-only. Forget success means revocation-unconfirmed only.
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
     * @description Supported contract; presence does not activate a runtime route or authentication mode. Bodyless, bounded, no-store/no-ETag/no-referrer. No bearer/ambient-claim identity. Only sign-in may select where it returns: an optional X-Weft-Return-To application path, validated by the shared browser return-path rule and carried in the protected state. One exact action header. Unsafe actions require exact Origin and separate standard auth-action AF; positive sessions additionally require112 epoch AF. Session fields/fingerprint are non-authority and memory-only. Forget success means revocation-unconfirmed only.
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
     * @description Supported contract; presence does not activate a runtime route or authentication mode. Bodyless, bounded, no-store/no-ETag/no-referrer. No bearer/ambient-claim identity. Only sign-in may select where it returns: an optional X-Weft-Return-To application path, validated by the shared browser return-path rule and carried in the protected state. One exact action header. Unsafe actions require exact Origin and separate standard auth-action AF; positive sessions additionally require112 epoch AF. Session fields/fingerprint are non-authority and memory-only. Forget success means revocation-unconfirmed only.
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
     * @description Supported contract; presence does not activate a runtime route or authentication mode. Bodyless, bounded, no-store/no-ETag/no-referrer. No bearer/ambient-claim identity. Only sign-in may select where it returns: an optional X-Weft-Return-To application path, validated by the shared browser return-path rule and carried in the protected state. One exact action header. Unsafe actions require exact Origin and separate standard auth-action AF; positive sessions additionally require112 epoch AF. Session fields/fingerprint are non-authority and memory-only. Forget success means revocation-unconfirmed only.
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
  "/api/v1/catalog/interchange": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read bounded interchange availability; no backend is enabled by default. */
    get: operations["GetCatalogInterchangeCapabilities"];
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
  "/api/v1/health/ready": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    get: operations["GetReadiness"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/help/sections": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read the user guide's sections in reading order. */
    get: operations["GetHelpSections"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/help/sections/{sectionId}": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read one user guide section as structured text. */
    get: operations["GetHelpSection"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/administration": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** GetOrganizationAdministration */
    get: operations["GetOrganizationAdministration"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/administration/bindings/{bindingId}/disable": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    get?: never;
    put?: never;
    /** DisableAdministrationBinding */
    post: operations["DisableAdministrationBinding"];
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/administration/invitations": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    get?: never;
    put?: never;
    /** CreateOrganizationInvitation */
    post: operations["CreateOrganizationInvitation"];
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/administration/invitations/{invitationId}/revoke": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    get?: never;
    put?: never;
    /** RevokeOrganizationInvitation */
    post: operations["RevokeOrganizationInvitation"];
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/administration/members/{principalId}": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    get?: never;
    /** PutOrganizationMember */
    put: operations["PutOrganizationMember"];
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/administration/principals/{principalId}/disable": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    get?: never;
    put?: never;
    /** DisableAdministrationPrincipal */
    post: operations["DisableAdministrationPrincipal"];
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/chat": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read the model profiles that can answer organization-wide questions, their data policy and the limits. */
    get: operations["GetWorkspaceChatAvailability"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/chat/conversations": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read the caller's conversations of both kinds in the organization, newest first, optionally about one revision. */
    get: operations["GetOrganizationChatConversations"];
    put?: never;
    /** Create or replay a private workspace conversation, optionally focused on a mapping or revision. */
    post: operations["CreateWorkspaceChatConversation"];
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/chat/conversations/{conversationId}": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read one private workspace conversation and its most recent turns. */
    get: operations["GetWorkspaceChatConversation"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/chat/conversations/{conversationId}/turns": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    get?: never;
    put?: never;
    /** Start or replay a workspace turn identified by its client-generated turn ID. */
    post: operations["CreateWorkspaceChatTurn"];
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/chat/conversations/{conversationId}/turns/{turnId}/cancel": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    get?: never;
    put?: never;
    /** Cancel an active workspace turn; a terminal turn is returned unchanged. */
    post: operations["CancelWorkspaceChatTurn"];
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/chat/conversations/{conversationId}/turns/{turnId}/events": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Stream the workspace turn's durable events after a sequence number using SSE framing. */
    get: operations["StreamWorkspaceChatTurnEvents"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/chat/conversations/{conversationId}/turns/{turnId}/mapping-edit-proposals": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read at most eight retained proposals with fresh creator and project authority, independently of stream delivery. */
    get: operations["GetChatMappingEditProposals"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/chat/conversations/{conversationId}/turns/{turnId}/mapping-edit-proposals/{proposalId}": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read one retained proposal with fresh creator and project authority. Does not approve or save. */
    get: operations["GetChatMappingEditProposal"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/chat/conversations/{conversationId}/turns/{turnId}/mapping-edit-proposals/{proposalId}/approve": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    get?: never;
    put?: never;
    /** Explicitly approve exactly the stored proposal and fingerprint, including after its turn ends. */
    post: operations["ApproveChatMappingEditProposal"];
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/chat/conversations/{conversationId}/turns/{turnId}/mapping-edit-proposals/{proposalId}/operations": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read the complete canonical edit script for exact review. Requires expectedFingerprint query; never returns native bytes. */
    get: operations["GetChatMappingEditOperations"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/chat/conversations/{conversationId}/turns/{turnId}/mapping-edit-proposals/{proposalId}/recover": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    get?: never;
    put?: never;
    /** Resume only an existing reservation for this exact proposal. Never approves, first-reserves or supplies source. */
    post: operations["RecoverChatMappingEditProposal"];
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/chat/conversations/{conversationId}/turns/{turnId}/mapping-writes": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read durable save receipts independently of turn streaming, with fresh creator and project access. */
    get: operations["GetChatMappingWriteReceipts"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/chat/conversations/{conversationId}/turns/{turnId}/mapping-writes/recover": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    get?: never;
    put?: never;
    /** Resume already reserved saves with current edit permission. Never reserve, resupply source, approve or rerun a turn. */
    post: operations["RecoverChatMappingWriteReceipts"];
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/notifications": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read visible project activity and recipient-only organization invitations. */
    get: operations["GetNotificationInbox"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/notifications/{notificationId}/read": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    get?: never;
    /** Mark one currently visible own notification read without changing its provenance. */
    put: operations["ReadInboxNotification"];
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
  "/api/v1/organizations/{organizationId}/projects/{projectId}/administration": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** GetProjectAdministration */
    get: operations["GetProjectAdministration"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/administration/invocation-grants/{grantId}": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    get?: never;
    /** PutAdministrationInvocationGrant */
    put: operations["PutAdministrationInvocationGrant"];
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/administration/invocation-grants/{grantId}/revoke": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    get?: never;
    put?: never;
    /** RevokeAdministrationInvocationGrant */
    post: operations["RevokeAdministrationInvocationGrant"];
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/administration/members/{principalId}": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    get?: never;
    /** PutProjectMember */
    put: operations["PutProjectMember"];
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/administration/members/{principalId}/payload-read": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    get?: never;
    /** PutProjectPayloadRead */
    put: operations["PutProjectPayloadRead"];
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/automation": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read scoped automation metadata under fresh project authority. */
    get: operations["GetAutomationPolicy"];
    /** Update scoped automation controls; no payload or secret is accepted. */
    put: operations["PutAutomationPolicy"];
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/build-environment-capabilities": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read the trusted selectable runtime profile and current environment-administrator capability. */
    get: operations["GetBuildEnvironmentCapabilities"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/build-environments": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read bounded retained environment versions. */
    get: operations["GetBuildEnvironments"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/build-environments/{environmentId}/versions/{version}": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read one exact retained environment version. */
    get: operations["GetBuildEnvironment"];
    /** Create a sequential environment version or recover its retained original profile without reactivation. */
    put: operations["PutBuildEnvironment"];
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/creation-capabilities": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** @description Report current server creation-runtime availability under authenticated current project metadata access. Returns available=false when runtime ports are absent or disabled. */
    get: operations["GetCreationCapabilities"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/creation-proposals": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** @description Discover creator-private retained proposals under current project content access, independent of chat history and source freshness. Exclusive ascending UUID cursor. */
    get: operations["ListCreationProposals"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/creation-proposals/{proposalId}": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** @description Creator-private exact retained proposal. Requires current project content and creation authority. Approval requires the exact fingerprint and every critical assumption ID; critical questions block ingestion. Recovery resumes an existing approved operation. Sample preview returns ephemeral verified output. */
    get: operations["ReadCreationProposal"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/creation-proposals/{proposalId}/approve": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    get?: never;
    put?: never;
    /** @description Creator-private exact retained proposal. Requires current project content and creation authority. Approval requires the exact fingerprint and every critical assumption ID; critical questions block ingestion. Recovery resumes an existing approved operation. Sample preview returns ephemeral verified output. */
    post: operations["ApproveCreationProposal"];
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/creation-proposals/{proposalId}/preview": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    get?: never;
    put?: never;
    /** @description Creator-private exact retained proposal. Requires current project content and creation authority. Approval requires the exact fingerprint and every critical assumption ID; critical questions block ingestion. Recovery resumes an existing approved operation. Sample preview returns ephemeral verified output. */
    post: operations["PreviewCreationProposal"];
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/creation-proposals/{proposalId}/recover": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    get?: never;
    put?: never;
    /** @description Creator-private exact retained proposal. Requires current project content and creation authority. Approval requires the exact fingerprint and every critical assumption ID; critical questions block ingestion. Recovery resumes an existing approved operation. Sample preview returns ephemeral verified output. */
    post: operations["RecoverCreationProposal"];
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
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/build-approvals": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read bounded own retained approvals without renewing admission. */
    get: operations["GetBuildApprovals"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/build-approvals/{approvalId}": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Recover an exact own approval, including revoked/inactive metadata. */
    get: operations["GetBuildApproval"];
    /** Create or recover one original immutable build approval. */
    put: operations["PutBuildApproval"];
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/build-approvals/{approvalId}/revoke": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    get?: never;
    put?: never;
    /** Explicitly revoke an own approval or recover its retained revoked status without renewing admission. */
    post: operations["RevokeBuildApproval"];
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/build-capabilities": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read current build-control capabilities without starting a worker. */
    get: operations["GetBuildCapabilities"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/build-runs": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read bounded own build history without artifact bodies. */
    get: operations["GetBuildRuns"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/build-runs/{buildId}": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read one exact own retained build result. */
    get: operations["GetBuildRun"];
    /** Queue an exact stored approval or recover the original submission. */
    put: operations["PutBuildRun"];
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/build-runs/{buildId}/cancel": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    get?: never;
    put?: never;
    /** Request cancellation without replacing a retained terminal result. */
    post: operations["CancelBuildRun"];
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
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/drafts": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** List the caller's own open drafts for this mapping. */
    get: operations["ListMappingDrafts"];
    put?: never;
    /** Create an author-private exact-base draft and version-zero checkpoint. */
    post: operations["CreateMappingDraft"];
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/drafts/{draftId}": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read author-private draft metadata and its strong version ETag. */
    get: operations["GetMappingDraft"];
    put?: never;
    post?: never;
    /** Discard the author's operation log and checkpoint content. */
    delete: operations["DiscardMappingDraft"];
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/drafts/{draftId}/base": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read the exact immutable base bytes, never an editable native upload. */
    get: operations["GetMappingDraftBase"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/drafts/{draftId}/checkpoints": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    get?: never;
    put?: never;
    /** Replay, encode, validate and project the exact draft version. */
    post: operations["CheckpointMappingDraft"];
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/drafts/{draftId}/checkpoints/current/projection": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read the current authoritative checkpoint projection. */
    get: operations["GetMappingDraftProjection"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/drafts/{draftId}/checkpoints/current/validation": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read the current authoritative checkpoint validation. */
    get: operations["GetMappingDraftValidation"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/drafts/{draftId}/comparison": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Compare the checkpoint's semantic facts and typed intent with the immutable base. */
    get: operations["CompareMappingDraft"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/drafts/{draftId}/operations": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read the closed ferrule.edit.v1 operation log. */
    get: operations["GetMappingDraftOperations"];
    /** Autosave a complete bounded operation log without starting a worker. */
    put: operations["PutMappingDraftOperations"];
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/drafts/{draftId}/previews": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    get?: never;
    put?: never;
    /** Preview an immutable copy of the current checkpoint, retained independently of this draft. */
    post: operations["PreviewMappingDraft"];
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/drafts/{draftId}/rebase": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    get?: never;
    put?: never;
    /** Explicitly replay all operations on the current head, or change nothing. */
    post: operations["RebaseMappingDraft"];
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/drafts/{draftId}/save": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    get?: never;
    put?: never;
    /** Save a nonempty current checkpoint through original-actor conditional ingestion. */
    post: operations["SaveMappingDraft"];
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
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/mfd-imports/review": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    get?: never;
    put?: never;
    /**
     * Convert an immutable MFD package for review without publishing a revision.
     * @description Consumes canonical application/vnd.weft.mfd-package.v1 framing. At most 128 files, 4194304 bytes each, 16777216 aggregate content bytes, 256 UTF-8 bytes per path. This route alone permits the bounded package body; native upload and global limits are unchanged. Fresh upload authority precedes body consumption. No Idempotency-Key or conditional headers. Nothing is published before explicit confirmation.
     */
    post: operations["ReviewMfdAppend"];
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/operations": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** List endpoint targets and active release identities. */
    get: operations["GetReleaseTargets"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/operations/{targetId}/{section}": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read paged release, history, actions, callers or invocation metadata. No payload bytes. */
    get: operations["GetReleaseOperations"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/operations/{targetId}/actions/{operationId}": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    get?: never;
    put?: never;
    /** Apply a version-conditional idempotent release operation. */
    post: operations["ApplyReleaseOperation"];
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/operations/{targetId}/summary": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read failure and latency summaries over a selected window. */
    get: operations["GetReleaseSummary"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
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
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/revisions/{revisionId}/mfd-exports/{reviewId}/download": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    get?: never;
    put?: never;
    /**
     * Download the exact reviewed export after explicit warning acknowledgement.
     * @description Strict JSON object with exactly reviewDigest (canonical lowercase SHA-256) and acknowledgeWarnings (boolean), at most 1024 bytes, application/json with no media parameters or compression. Unknown, duplicate or incorrectly typed fields are refused. No query or conditional headers. The original actor's current authority is checked again. No Idempotency-Key. Warnings must be acknowledged before bytes are returned; repeated downloads return the same reviewed package. Multiple-file packages download as a deterministic ZIP; a single design downloads as .mfd. No conversion is run on this route.
     */
    post: operations["DownloadMfdExport"];
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/revisions/{revisionId}/mfd-exports/review": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    get?: never;
    put?: never;
    /**
     * Review a revision's MFD export and all conversion warnings before download.
     * @description Empty body and no query, Idempotency-Key or conditional headers. Fresh scoped read authority is checked before and after private native storage and export. The returned review is original-actor scoped, immutable and expires after one hour.
     */
    post: operations["ReviewMfdExport"];
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
    /** Submit an idempotent bounded sample preview of this exact revision with explicit JSON, document or primary CSV/TSV admission. */
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
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/revisions/{revisionId}/restore": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    get?: never;
    put?: never;
    /** Save an earlier version's exact content as the next version through conditional ingestion on the expected latest version. */
    post: operations["RestoreMappingRevision"];
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/sandbox": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read the local sandbox targets, deployable builds and sample sources (local simulation only). */
    get: operations["GetLocalSandbox"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/sandbox/{targetId}/messages": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read the newest retained sandbox messages of a target, without document contents. */
    get: operations["GetLocalSandboxMessages"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/sandbox/{targetId}/messages/{messageId}": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read one retained sandbox message with its inputs and outputs. */
    get: operations["GetLocalSandboxMessage"];
    put?: never;
    /** Send a sample to the target's active release through the machine invocation path. Repeating a message ID returns its recorded result. */
    post: operations["SendLocalSandboxMessage"];
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/sandbox/deployments/{deploymentId}": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    get?: never;
    put?: never;
    /** Qualify a completed build, register or reuse the sandbox target and activate it through release operations. */
    post: operations["DeployLocalSandbox"];
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{resourceId}/automation": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read scoped automation metadata under fresh project authority. */
    get: operations["GetMappingAutomation"];
    /** Update scoped automation controls; no payload or secret is accepted. */
    put: operations["PutMappingAutomation"];
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{resourceId}/regression-suites": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read scoped automation metadata under fresh project authority. */
    get: operations["GetRegressionSuites"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mfd-imports/{reviewId}": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Restore an original-actor import review after fresh project and target authorization. */
    get: operations["GetMfdImportReview"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mfd-imports/{reviewId}/confirm": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    get?: never;
    put?: never;
    /**
     * Publish only server-owned native bytes from the exact reviewed import.
     * @description Strict JSON object with exactly reviewDigest (canonical lowercase SHA-256) and acknowledgeWarnings (boolean), at most 1024 bytes, application/json with no media parameters or compression. Unknown, duplicate or incorrectly typed fields are refused. No query or conditional headers. The original actor's current authority is checked again. Idempotency-Key must equal reviewId. Reviewed package, native bytes, complete warnings, engine identity and append head are immutable. Confirmation never accepts replacement native bytes or reruns conversion.
     */
    post: operations["ConfirmMfdImport"];
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/mfd-imports/review": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    get?: never;
    put?: never;
    /**
     * Convert an immutable MFD package for review without publishing a revision.
     * @description Consumes canonical application/vnd.weft.mfd-package.v1 framing. At most 128 files, 4194304 bytes each, 16777216 aggregate content bytes, 256 UTF-8 bytes per path. This route alone permits the bounded package body; native upload and global limits are unchanged. Fresh upload authority precedes body consumption. No Idempotency-Key or conditional headers. Nothing is published before explicit confirmation.
     */
    post: operations["ReviewMfdCreate"];
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/notification-deliveries": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read scoped automation metadata under fresh project authority. */
    get: operations["GetNotificationDeliveries"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/notification-destinations": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read scoped automation metadata under fresh project authority. */
    get: operations["GetNotificationDestinations"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/notification-destinations/enable": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    get?: never;
    /** Update scoped automation controls; no payload or secret is accepted. */
    put: operations["EnableNotificationDestination"];
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/notification-options": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read configured channel IDs; never addresses, URLs or secrets. */
    get: operations["GetNotificationOptions"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/notification-subscription": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read scoped automation metadata under fresh project authority. */
    get: operations["GetNotificationSubscription"];
    /** Update scoped automation controls; no payload or secret is accepted. */
    put: operations["PutNotificationSubscription"];
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/notifications": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read scoped automation metadata under fresh project authority. */
    get: operations["GetNotifications"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/notifications/{resourceId}/read": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    get?: never;
    /** Update scoped automation controls; no payload or secret is accepted. */
    put: operations["ReadNotification"];
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/regression-suites/{resourceId}": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read scoped automation metadata under fresh project authority. */
    get: operations["GetRegressionSuite"];
    /** Update scoped automation controls; no payload or secret is accepted. */
    put: operations["PutRegressionRecheck"];
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/regression-suites/{resourceId}/cases": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** Read scoped automation metadata under fresh project authority. */
    get: operations["GetRegressionEvaluations"];
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
  "/api/v1/organizations/{organizationId}/projects/{projectId}/spec-bundles/{bundleId}": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** @description Explicit project content grants are required. No AI-sharing authority is granted. Unavailable until the exact registry schema and private Blob backend are admitted. Development identity bypass is refused. On an uncertain mutation, retry the exact identities and bytes; do not allocate replacements. */
    get: operations["GetSpecBundleInventory"];
    /** @description Explicit project content grants are required. No AI-sharing authority is granted. Unavailable until the exact registry schema and private Blob backend are admitted. Development identity bypass is refused. On an uncertain mutation, retry the exact identities and bytes; do not allocate replacements. */
    put: operations["CreateSpecBundle"];
    post?: never;
    /** @description Explicit project content grants are required. No AI-sharing authority is granted. Unavailable until the exact registry schema and private Blob backend are admitted. Development identity bypass is refused. On an uncertain mutation, retry the exact identities and bytes; do not allocate replacements. */
    delete: operations["TombstoneSpecBundle"];
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/spec-bundles/{bundleId}/capabilities": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** @description Explicit project content grants are required. No AI-sharing authority is granted. Unavailable until the exact registry schema and private Blob backend are admitted. Development identity bypass is refused. On an uncertain mutation, retry the exact identities and bytes; do not allocate replacements. */
    get: operations["GetSpecIntakeCapabilities"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/spec-bundles/{bundleId}/extractions/{objectId}": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /** @description Explicit project content grants are required. No AI-sharing authority is granted. Unavailable until the exact registry schema and private Blob backend are admitted. Development identity bypass is refused. On an uncertain mutation, retry the exact identities and bytes; do not allocate replacements. */
    get: operations["ReadSpecExtraction"];
    put?: never;
    post?: never;
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/spec-bundles/{bundleId}/sources/{sourceId}/versions/{sourceVersion}": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    get?: never;
    put?: never;
    post?: never;
    /** @description Explicit project content grants are required. No AI-sharing authority is granted. Unavailable until the exact registry schema and private Blob backend are admitted. Development identity bypass is refused. On an uncertain mutation, retry the exact identities and bytes; do not allocate replacements. */
    delete: operations["TombstoneSpecSource"];
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/spec-bundles/{bundleId}/text": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    get?: never;
    put?: never;
    /** @description Explicit project content grants are required. No AI-sharing authority is granted. Unavailable until the exact registry schema and private Blob backend are admitted. Development identity bypass is refused. On an uncertain mutation, retry the exact identities and bytes; do not allocate replacements. */
    post: operations["PasteSpecText"];
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/projects/{projectId}/spec-bundles/{bundleId}/uploads": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    get?: never;
    put?: never;
    /** @description Explicit project content grants are required. No AI-sharing authority is granted. Unavailable until the exact registry schema and private Blob backend are admitted. Development identity bypass is refused. On an uncertain mutation, retry the exact identities and bytes; do not allocate replacements. */
    post: operations["UploadSpecSource"];
    delete?: never;
    options?: never;
    head?: never;
    patch?: never;
    trace?: never;
  };
  "/api/v1/organizations/{organizationId}/search": {
    parameters: {
      query?: never;
      header?: never;
      path?: never;
      cookie?: never;
    };
    /**
     * Search freshly authorized catalog metadata and retained report field/rule names and paths.
     * @description Literal substring search: ASCII case-insensitive, other Unicode exact. No query language, native derivation or sample payload reads. Fresh principal, organization and project authority is checked for each page. Results retain exact revision/report/resource identities and plain text UTF-16 highlight spans. Continuations bind the trimmed query and organization, not a permission grant or snapshot. A 503 response refuses incomplete report indexing; retry continues bounded backfill.
     */
    get: operations["SearchCatalog"];
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
    AdministrationAuditV1: {
      /** Format: uuid */
      actorId: string;
      after: null | components["schemas"]["JsonElement"];
      before: null | components["schemas"]["JsonElement"];
      operation: string;
      /** Format: uuid */
      projectId: null | string;
      /** Format: date-time */
      recordedAt: string;
      /** Format: int64 */
      sequence: number;
      /** Format: uuid */
      targetId: string;
    };
    AdministrationBindingV1: {
      authority: string;
      /** Format: uuid */
      bindingId: string;
      enabled: boolean;
      provider: string;
      subject: string;
      /** Format: int64 */
      version: number;
    };
    AdministrationErrorV1: {
      code: string;
    };
    AdministrationInvitationCreatedV1: {
      /** Format: uuid */
      invitationId: string;
      redemptionPath: string;
    };
    AdministrationInvitationPostV1: {
      /** Format: uuid */
      expectedObjectId: null | string;
      /** Format: date-time */
      expiresAt: string;
      /** Format: uuid */
      invitationId: string;
      projectRoles: components["schemas"]["InvitationProjectRoleV1"][];
      /** Format: uuid */
      tenantId: string;
    };
    AdministrationInvocationGrantPutV1: {
      /** Format: uuid */
      objectId: string;
      /** Format: uuid */
      targetId: string;
      /** Format: uuid */
      tenantId: string;
    };
    AdministrationInvocationGrantV1: {
      /** Format: date-time */
      grantedAt: string;
      /** Format: uuid */
      grantId: string;
      /** Format: uuid */
      mappingId: string;
      /** Format: uuid */
      objectId: string;
      /** Format: date-time */
      revokedAt: null | string;
      /** Format: uuid */
      targetId: string;
      /** Format: uuid */
      tenantId: string;
    };
    AdministrationMemberPutV1: {
      active: boolean;
      role: string;
    };
    AdministrationPayloadPutV1: {
      active: boolean;
    };
    AdministrationReceiptV1: {
      outcome: string;
    };
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
          /** @description Optional provider-supplied display text, never an identity or authority. At most 200 Unicode scalars; blank text is invalid. */
          displayName: null | string;
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
          displayName: null;
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
          displayName: null;
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
    AutomationPolicyV1: {
      /** Format: int32 */
      errorRatePercent: number;
      /** Format: int32 */
      minimumRequests: number;
      requirePassingRegression: boolean;
      /** Format: int32 */
      windowSeconds: number;
    };
    AutomationSavedV1: {
      saved: boolean;
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
    BuildApprovalPageV1: {
      approvals: components["schemas"]["BuildApprovalV1"][];
      nextApprovalId: null | string;
    };
    BuildApprovalPutV1: {
      buildId: string;
      environmentId: string;
      environmentVersion: string;
      regressionEvidenceId: string;
      revisionId: string;
    };
    BuildApprovalReceiptV1: {
      approval: components["schemas"]["BuildApprovalV1"];
      outcome: string;
    };
    BuildApprovalV1: {
      approvalId: string;
      backend: components["schemas"]["BuildBackendV1"];
      boundaries: components["schemas"]["BuildBoundaryV1"][];
      buildId: string;
      createdAtUtc: string;
      environment: components["schemas"]["BuildEnvironmentReferenceV1"];
      environmentActive: boolean;
      nativeDigest: string;
      profile: components["schemas"]["BuildRuntimeV1"];
      regressionEvidenceId: string;
      revision: components["schemas"]["CatalogRevisionScopeV1"];
      revoked: boolean;
    };
    BuildArtifactMetadataV1: {
      /** Format: int64 */
      byteCount: number;
      format: string;
      sha256: string;
    };
    BuildBackendV1: {
      adapterVersion: string;
      backendId: string;
      engineBuild: string;
      engineRevision: string;
    };
    BuildBoundaryV1: {
      csvDialect?: null | components["schemas"]["BuildCsvDialectV1"];
      format: string;
      kind: string;
      name: null | string;
    };
    BuildCapabilitiesV1: {
      approveBuilds: boolean;
      backend: components["schemas"]["BuildBackendV1"];
      csvBackend?: null | components["schemas"]["BuildBackendV1"];
      csvProfile?: null | components["schemas"]["BuildRuntimeV1"];
      documentBackend?: null | components["schemas"]["BuildBackendV1"];
      documentProfile?: null | components["schemas"]["BuildRuntimeV1"];
      interchangeProfile?: null | components["schemas"]["BuildRuntimeV1"];
      profile: components["schemas"]["BuildRuntimeV1"];
      readOwnMetadata: boolean;
      submitBuilds: boolean;
    };
    BuildCsvDialectV1: {
      delimiter: string;
      hasHeaderRow: boolean;
      preserveEmptyStrings: boolean;
      quote: null | string;
      quoteDisabled: boolean;
      schema: string;
      utf8Bom: boolean;
    };
    BuildEnvironmentCapabilitiesV1: {
      canCreateVersions: boolean;
      csvProfile?: null | components["schemas"]["BuildRuntimeV1"];
      documentProfile?: null | components["schemas"]["BuildRuntimeV1"];
      interchangeProfile?: null | components["schemas"]["BuildRuntimeV1"];
      profile: components["schemas"]["BuildRuntimeV1"];
    };
    BuildEnvironmentPageV1: {
      environments: components["schemas"]["BuildEnvironmentV1"][];
      nextEnvironmentId: null | string;
      nextVersion: null | string;
    };
    BuildEnvironmentPutV1: {
      profileId: string;
      profileVersion: string;
    };
    BuildEnvironmentReceiptV1: {
      environment: components["schemas"]["BuildEnvironmentV1"];
      outcome: string;
    };
    BuildEnvironmentReferenceV1: {
      environmentId: string;
      organizationId: string;
      projectId: string;
      version: string;
    };
    BuildEnvironmentV1: {
      active: boolean;
      createdAtUtc: string;
      creatorPrincipalId: string;
      profile: components["schemas"]["BuildRuntimeV1"];
      reference: components["schemas"]["BuildEnvironmentReferenceV1"];
    };
    BuildRunPageV1: {
      nextBuildId: null | string;
      runs: components["schemas"]["BuildRunV1"][];
    };
    BuildRunPutV1: {
      approvalId: string;
    };
    BuildRunReceiptV1: {
      outcome: string;
      run: components["schemas"]["BuildRunV1"];
    };
    BuildRuntimeV1: {
      artifactFormat: string;
      id: string;
      /** Format: int64 */
      maximumArtifactBytes: number;
      runtime: string;
      runtimeVersion: string;
      version: string;
    };
    BuildRunV1: {
      approvalId: string;
      artifact: null | components["schemas"]["BuildArtifactMetadataV1"];
      /** Format: int32 */
      attempt: number;
      buildId: string;
      createdAtUtc: string;
      outcome: null | string;
      revision: components["schemas"]["CatalogRevisionScopeV1"];
      state: string;
    };
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
      /** @default false */
      edit: boolean;
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
        }
      | {
          actorPrincipalId: string;
          allowedOrganizationId: string;
          features: components["schemas"]["CatalogFeaturesV1"];
          /** @enum {string} */
          mode: "bearer";
          organizationActions: components["schemas"]["CatalogOrganizationActionsV1"];
          organizationName: string;
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
      | "revisionReportResourceLimit"
      | "rateLimited";
    CatalogErrorV1: {
      code: components["schemas"]["CatalogErrorCodeV1"];
    };
    CatalogFeaturesV1: {
      chat: boolean;
      createMappings: boolean;
      createProjects: boolean;
      csvPreview?: null | boolean;
      deriveReports: boolean;
      /** @default false */
      edit: boolean;
      editMetadata: boolean;
      manageMembership: boolean;
      preview: boolean;
      readCatalog: boolean;
      readReports: boolean;
      uploadRevisions: boolean;
      /** @default false */
      xmlPreview: boolean;
    };
    CatalogHeadV1: {
      admission: components["schemas"]["CatalogAdmissionV1"];
      scope: components["schemas"]["CatalogRevisionScopeV1"];
      sequence: string;
    };
    CatalogInterchangeCapabilitiesV1: {
      enabled: boolean;
      /** Format: int32 */
      maximumFileBytes: number;
      /** Format: int32 */
      maximumFiles: number;
      /** Format: int32 */
      maximumPathBytes: number;
      /** Format: int32 */
      maximumTotalBytes: number;
    };
    CatalogInterchangeEngineV1: {
      adapterBuild: string;
      backendBuild: string;
      backendId: string;
      engineRevision: string;
    };
    CatalogInterchangeReviewV1: {
      engine: components["schemas"]["CatalogInterchangeEngineV1"];
      expectedHeadRevisionId: null | string;
      /** Format: date-time */
      expiresAt: string;
      mappingName: null | string;
      nativeDigest: string;
      packageDigest: string;
      reviewDigest: string;
      reviewId: string;
      summary: string;
      targetMappingId: null | string;
      warnings: components["schemas"]["CatalogInterchangeWarningV1"][];
    };
    CatalogInterchangeWarningV1: {
      code: string;
      message: string;
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
    CatalogPreviewDraftSubjectV1: {
      baseRevision: components["schemas"]["CatalogRevisionScopeV1"];
      draftId: string;
      mapping: components["schemas"]["CatalogMappingScopeV1"];
      /** Format: int32 */
      nativeByteCount: number;
      nativeDigest: string;
      version: string;
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
      csvDialect?: null | components["schemas"]["CsvDocumentDialectV1"];
      mediaType: string;
      /** Format: int32 */
      ordinal: number;
      recordsWritten: string;
      sha256: string;
      targetName: string;
      targetNameTruncated: boolean;
      xmlMetadata?: null | components["schemas"]["XmlDocumentMetadataV1"];
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
      draft?: null | components["schemas"]["CatalogPreviewDraftSubjectV1"];
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
      revision: null | components["schemas"]["CatalogRevisionScopeV1"];
      runId: string;
      /** Format: date-time */
      startedAtUtc: null | string;
      state: string;
      subjectKind?: null | string;
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
      /**
       * @description Omitted or null preserves legacy JSON admission. XML is a separately enabled safe native-preview profile with saved metadata and named targets. CSV/TSV is a separately enabled primary-only profile; an explicit empty content string is present input. Formats and settings come from saved native boundaries, never filenames or caller overrides. XML regression compares exact bytes; compiled XML execution is unavailable.
       * @enum {null|string}
       */
      profile?:
        | "weft.ferrule-json-preview.v1"
        | "weft.ferrule-document-preview.v1"
        | "weft.ferrule-csv-preview.v1"
        | "weft.ferrule-xml-preview.v1"
        | null;
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
    CatalogSearchHighlightV1: {
      /** Format: int32 */
      length: number;
      /** Format: int32 */
      start: number;
    };
    CatalogSearchHitV1: {
      description: components["schemas"]["CatalogSearchTextV1"];
      kind: components["schemas"]["CatalogSearchKindV1"];
      /** Format: uuid */
      mappingId: null | string;
      /** Format: uuid */
      organizationId: string;
      /** Format: uuid */
      projectId: string;
      /** Format: uuid */
      projectionDerivationId: null | string;
      resourceId: null | string;
      /** Format: uuid */
      revisionId: null | string;
      title: components["schemas"]["CatalogSearchTextV1"];
    };
    /** @enum {unknown} */
    CatalogSearchKindV1: "project" | "mapping" | "revision" | "field" | "rule";
    CatalogSearchPageV1: {
      hits: components["schemas"]["CatalogSearchHitV1"][];
      nextCursor: null | string;
      query: string;
    };
    CatalogSearchTextV1: {
      highlights: components["schemas"]["CatalogSearchHighlightV1"][];
      text: string;
    };
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
    CatalogXmlLocationV1: {
      /** Format: int32 */
      column: number;
      /** Format: int32 */
      line: number;
    };
    CatalogXmlPreviewProblemV1: {
      /** @enum {string} */
      code: "xmlPreflight";
      /** @enum {string} */
      kind: "invalidInput" | "resourceLimit" | "unsupportedCapability";
      /** @description Nonnegative Int64 as exact decimal text, or null when unavailable. */
      limit: null | string;
      location: null | components["schemas"]["CatalogXmlLocationV1"];
      /** @description Nonnegative Int64 as exact decimal text, or null when unavailable. */
      observed: null | string;
      /** @enum {string} */
      reason:
        | "invalidUtf8"
        | "invalidXml"
        | "invalidDeclaration"
        | "documentRequired"
        | "unsupportedName"
        | "byteLimit"
        | "depthLimit"
        | "nodeLimit"
        | "attributeLimit"
        | "documentCountLimit"
        | "invalidMetadata"
        | "metadataTooLarge"
        | "invalidNativeMetadata"
        | "nativeByteLimit";
      statistics: null | components["schemas"]["CatalogXmlStatisticsV1"];
    };
    CatalogXmlStatisticsV1: {
      /** Format: int32 */
      maximumAttributes: number;
      /** Format: int32 */
      maximumDepth: number;
      /** Format: int32 */
      originalBytes: number;
      /** Format: int32 */
      physicalNodes: number;
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
    /** @enum {unknown} */
    ChatConversationKindV1: "revision" | "workspace";
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
      | "turnNotStarted"
      | "rateLimited";
    ChatErrorV1: {
      code: components["schemas"]["ChatErrorCodeV1"];
    };
    ChatFocusV1: {
      mappingId: string;
      projectId: string;
      revisionId: null | string;
    };
    ChatLimitsV1: {
      /** Format: int32 */
      maxSelectors: number;
      /** Format: int32 */
      maxTextBytes: number;
      /** Format: int32 */
      turnSeconds: number;
    };
    ChatMappingChangeV1: {
      after: string;
      before: string;
      field: string;
    };
    ChatMappingEditApprovalV1: {
      expectedFingerprint: string;
    };
    ChatMappingEditOperationsV1: {
      canonicalScript: string;
      fingerprint: string;
      /** Format: int32 */
      operationCount: number;
      operationsDigest: string;
      proposalId: string;
    };
    ChatMappingEditProposalPageV1: {
      proposals: components["schemas"]["ChatMappingEditProposalV1"][];
    };
    ChatMappingEditProposalV1: {
      baseRevision: components["schemas"]["CatalogRevisionScopeV1"];
      /** Format: int64 */
      baseSequence: number;
      changes: components["schemas"]["ChatMappingChangeV1"][];
      changesTruncated: boolean;
      fingerprint: string;
      /** Format: int32 */
      operationCount: number;
      operationsDigest: string;
      proposalId: string;
      revisionId: null | string;
      /** Format: int64 */
      revisionSequence: null | number;
      /** @enum {string} */
      status:
        | "awaitingApproval"
        | "approved"
        | "pending"
        | "saved"
        | "notSaved"
        | "notPermitted"
        | "outOfDate";
    };
    /** @enum {unknown} */
    ChatMappingWriteOutcomeV1: "saved" | "proposed";
    /** @enum {unknown} */
    ChatMappingWriteReasonV1: "notPermitted" | "outOfDate" | null;
    ChatMappingWriteReceiptPageV1: {
      receipts: components["schemas"]["ChatMappingWriteReceiptV1"][];
    };
    ChatMappingWriteReceiptV1: {
      requestId: string;
      /** @enum {string} */
      status: "pending" | "saved" | "notSaved";
      write:
        null | components["schemas"]["ChatTurnEventV1ChatMappingWriteEventV1"];
    };
    ChatOrganizationConversationPageV1: {
      conversations: components["schemas"]["ChatOrganizationConversationV1"][];
      hasMore: boolean;
    };
    ChatOrganizationConversationV1: {
      conversationId: string;
      /** Format: date-time */
      createdAtUtc: string;
      focus: null | components["schemas"]["ChatFocusV1"];
      kind: components["schemas"]["ChatConversationKindV1"];
      /** Format: date-time */
      lastTurnAtUtc: null | string;
      /** Format: date-time */
      retainUntilUtc: string;
      revision: null | components["schemas"]["CatalogRevisionScopeV1"];
      title: null | string;
      /** Format: int32 */
      turnCount: number;
    };
    /** @enum {unknown} */
    ChatPayloadSourceV1:
      | "previewInput"
      | "previewOutput"
      | "regressionCase"
      | "regressionRunOutput"
      | "specification";
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
      | components["schemas"]["ChatTurnEventV1ChatWorkspaceCitationEventV1"]
      | components["schemas"]["ChatTurnEventV1ChatDataSharedEventV1"]
      | components["schemas"]["ChatTurnEventV1ChatMappingWriteEventV1"]
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
    ChatTurnEventV1ChatDataSharedEventV1: {
      /** Format: int32 */
      bytes: number;
      projectId: string;
      roundId: string;
      /** Format: int32 */
      sequence: number;
      source: components["schemas"]["ChatPayloadSourceV1"];
      subjectId: string;
      /** @enum {string} */
      type: "data.shared";
      version: null | string;
    };
    ChatTurnEventV1ChatMappingWriteEventV1: {
      baseRevision: components["schemas"]["CatalogRevisionScopeV1"];
      baseSequence: string;
      changes: components["schemas"]["ChatMappingChangeV1"][];
      changesTruncated: boolean;
      headRevisionId: null | string;
      headSequence: null | string;
      /** Format: int32 */
      operationCount: number;
      outcome: components["schemas"]["ChatMappingWriteOutcomeV1"];
      reason: components["schemas"]["ChatMappingWriteReasonV1"];
      revisionId: null | string;
      revisionSequence: null | string;
      roundId: string;
      /** Format: int32 */
      sequence: number;
      /** @enum {string} */
      type: "mapping.write";
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
    ChatTurnEventV1ChatWorkspaceCitationEventV1: {
      citation: components["schemas"]["ChatWorkspaceCitationV1"];
      roundId: string;
      /** Format: int32 */
      sequence: number;
      /** @enum {string} */
      type: "workspace.citation";
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
    ChatTurnFocusStateV1: "none" | "resolved" | "unavailable";
    ChatTurnFocusV1: {
      revision: null | components["schemas"]["CatalogRevisionScopeV1"];
      state: components["schemas"]["ChatTurnFocusStateV1"];
    };
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
      focus?: null | components["schemas"]["ChatTurnFocusV1"];
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
    ChatWorkspaceAvailabilityV1: {
      limits: components["schemas"]["ChatLimitsV1"];
      organizationId: string;
      profiles: components["schemas"]["ChatWorkspaceProfileV1"][];
      /** @enum {string} */
      readonly schemaVersion: "weft.chat.v1";
    };
    /** @enum {unknown} */
    ChatWorkspaceCitationKindV1:
      | "project"
      | "mapping"
      | "revision"
      | "report"
      | "previewRun"
      | "regressionCase"
      | "regressionRun"
      | "buildRun"
      | "buildApproval"
      | "buildEnvironment"
      | "guideSection"
      | "creationProposal";
    ChatWorkspaceCitationV1: {
      kind: components["schemas"]["ChatWorkspaceCitationKindV1"];
      mappingId: null | string;
      organizationId: string;
      projectId: null | string;
      proposalFingerprint?: null | string;
      reportDerivationId: null | string;
      revisionId: null | string;
      sectionId: null | string;
      subjectId: null | string;
      target: null | components["schemas"]["ChatSelectorV1"];
      version: null | string;
    };
    ChatWorkspaceConversationCreateV1: {
      conversationId: string;
      focus?: null | {
        mappingId: string;
        projectId: string;
        revisionId?: null | string;
      };
    };
    ChatWorkspaceConversationDetailV1: {
      conversation: components["schemas"]["ChatOrganizationConversationV1"];
      hasEarlierTurns: boolean;
      turns: components["schemas"]["ChatTurnV1"][];
    };
    ChatWorkspaceProfileV1: {
      payloadsAllowed: boolean;
      profile: components["schemas"]["ChatProfileV1"];
    };
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
    ComparisonDraftCitationV1: {
      draftId: string;
      mappingId: string;
      organizationId: string;
      projectId: string;
      version: string;
    };
    ComparisonEditOperationV1: {
      description: string;
      descriptionTruncated: boolean;
      index: string;
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
      draft?: null | components["schemas"]["ComparisonDraftCitationV1"];
      kind?: null | string;
      nativeByteCount: string;
      nativeDigest: components["schemas"]["ArtifactDigest"];
      projection: null | components["schemas"]["ComparisonReportCitationV1"];
      projectionProvenance: components["schemas"]["ProjectionProvenance"];
      revision: null | components["schemas"]["ScopedRevisionId"];
      sequence: null | string;
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
    CreationApprovalV1: {
      acceptedCriticalAssumptions: string[];
      fingerprint: string;
    };
    CreationAssumptionV1: {
      critical: boolean;
      id: string;
      text: string;
    };
    CreationCapabilitiesV1: {
      available: boolean;
    };
    CreationCitationV1: {
      bundleId: string;
      /** Format: int32 */
      chunkOrdinal: number;
      sourceId: string;
      /** Format: int64 */
      sourceVersion: number;
    };
    CreationErrorV1: {
      code: string;
    };
    CreationPreviewRequestV1: {
      fingerprint: string;
      sampleBase64: string;
      sampleDigest: string;
    };
    CreationPreviewV1: {
      fingerprint: string;
      format: null | string;
      mediaType: null | string;
      nativeDigest: string;
      outcome: string;
      outputBase64: null | string;
      outputDigest: null | string;
      proposalId: string;
      sampleDigest: string;
    };
    CreationProposalLinkV1: {
      fingerprint: string;
      proposalId: string;
      reviewPath: string;
    };
    CreationProposalPageV1: {
      afterProposal: null | string;
      proposals: components["schemas"]["CreationProposalLinkV1"][];
    };
    CreationProposalV1: {
      approved: boolean;
      assumptions: components["schemas"]["CreationAssumptionV1"][];
      description: null | string;
      evidence: components["schemas"]["CreationCitationV1"][];
      fingerprint: string;
      name: string;
      nativeDigest: string;
      planDigest: string;
      planJson: string;
      projectId: string;
      proposalId: string;
      questions: components["schemas"]["CreationQuestionV1"][];
      receipt: null | components["schemas"]["CreationReceiptV1"];
      snapshotDigest: string;
    };
    CreationQuestionV1: {
      critical: boolean;
      text: string;
    };
    CreationReceiptV1: {
      mappingId: null | string;
      operationId: string;
      phase: string;
      revisionId: null | string;
    };
    CreationRecoveryV1: {
      fingerprint: string;
    };
    CsvDocumentDialectV1: {
      delimiter: string;
      hasHeaderRow: boolean;
      preserveEmptyStrings: boolean;
      quote: null | string;
      quoteDisabled: boolean;
      schema: string;
      utf8Bom: boolean;
    };
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
    EditBindingV1: {
      field: string;
      scope: components["schemas"]["EditScopeV1"];
    };
    EditExpressionV1:
      | components["schemas"]["EditExpressionV1ExistingEditExpressionV1"]
      | components["schemas"]["EditExpressionV1SourceEditExpressionV1"]
      | components["schemas"]["EditExpressionV1OwnedSourceEditExpressionV1"]
      | components["schemas"]["EditExpressionV1ConstantEditExpressionV1"]
      | components["schemas"]["EditExpressionV1CallEditExpressionV1"];
    EditExpressionV1CallEditExpressionV1: {
      args: string[];
      function: string;
      id: string;
      /** @enum {string} */
      kind: "call";
    };
    EditExpressionV1ConstantEditExpressionV1: {
      id: string;
      /** @enum {string} */
      kind: "constant";
      value: components["schemas"]["EditScalarV1"];
    };
    EditExpressionV1ExistingEditExpressionV1: {
      /** @enum {string} */
      kind: "existing";
      node: string;
    };
    EditExpressionV1OwnedSourceEditExpressionV1: {
      frame?: null | string[];
      id: string;
      /** @enum {string} */
      kind: "owned_source";
      owner: components["schemas"]["EditSourceOwnerV1"];
      path: string[];
    };
    EditExpressionV1SourceEditExpressionV1: {
      frame?: null | string[];
      id: string;
      /** @enum {string} */
      kind: "source";
      path: string[];
    };
    EditFailureIterationV1:
      | components["schemas"]["EditFailureIterationV1SourceEditFailureIterationV1"]
      | components["schemas"]["EditFailureIterationV1GeneratedEditFailureIterationV1"];
    EditFailureIterationV1GeneratedEditFailureIterationV1: {
      /** @enum {string} */
      kind: "generated";
      sequence: components["schemas"]["EditGeneratedSequenceV1"];
    };
    EditFailureIterationV1SourceEditFailureIterationV1: {
      collection: components["schemas"]["EditSourceCollectionV1"];
      /** @enum {string} */
      kind: "source";
    };
    EditFailureRuleDefinitionV1: {
      iteration: components["schemas"]["EditFailureIterationV1"];
      message?: null | components["schemas"]["EditExpressionV1"];
      selection: components["schemas"]["EditFailureSelectionV1"];
    };
    EditFailureSelectionV1:
      | components["schemas"]["EditFailureSelectionV1AllEditFailuresV1"]
      | components["schemas"]["EditFailureSelectionV1TrueEditFailuresV1"]
      | components["schemas"]["EditFailureSelectionV1FalseEditFailuresV1"];
    EditFailureSelectionV1AllEditFailuresV1: {
      /** @enum {string} */
      kind: "all";
    };
    EditFailureSelectionV1FalseEditFailuresV1: {
      expression: components["schemas"]["EditExpressionV1"];
      /** @enum {string} */
      kind: "when_false";
    };
    EditFailureSelectionV1TrueEditFailuresV1: {
      expression: components["schemas"]["EditExpressionV1"];
      /** @enum {string} */
      kind: "when_true";
    };
    EditGeneratedSequenceV1:
      | components["schemas"]["EditGeneratedSequenceV1GenerateEditSequenceV1"]
      | components["schemas"]["EditGeneratedSequenceV1TokenizeEditSequenceV1"]
      | components["schemas"]["EditGeneratedSequenceV1TokenizeLengthEditSequenceV1"]
      | components["schemas"]["EditGeneratedSequenceV1TokenizeRegexEditSequenceV1"];
    EditGeneratedSequenceV1GenerateEditSequenceV1: {
      from?: null | components["schemas"]["EditExpressionV1"];
      item: string;
      /** @enum {string} */
      kind: "generate";
      to: components["schemas"]["EditExpressionV1"];
    };
    EditGeneratedSequenceV1TokenizeEditSequenceV1: {
      delimiter: components["schemas"]["EditExpressionV1"];
      input: components["schemas"]["EditExpressionV1"];
      item: string;
      /** @enum {string} */
      kind: "tokenize";
    };
    EditGeneratedSequenceV1TokenizeLengthEditSequenceV1: {
      input: components["schemas"]["EditExpressionV1"];
      item: string;
      /** @enum {string} */
      kind: "tokenize_by_length";
      length: components["schemas"]["EditExpressionV1"];
    };
    EditGeneratedSequenceV1TokenizeRegexEditSequenceV1: {
      flags?: null | components["schemas"]["EditExpressionV1"];
      input: components["schemas"]["EditExpressionV1"];
      item: string;
      /** @enum {string} */
      kind: "tokenize_regex";
      pattern: components["schemas"]["EditExpressionV1"];
    };
    EditGroupingV1:
      | components["schemas"]["EditGroupingV1NoEditGroupingV1"]
      | components["schemas"]["EditGroupingV1ByEditGroupingV1"]
      | components["schemas"]["EditGroupingV1AdjacentEditGroupingV1"]
      | components["schemas"]["EditGroupingV1StartingEditGroupingV1"]
      | components["schemas"]["EditGroupingV1EndingEditGroupingV1"]
      | components["schemas"]["EditGroupingV1BlocksEditGroupingV1"];
    EditGroupingV1AdjacentEditGroupingV1: {
      /** @enum {string} */
      kind: "adjacent_by";
      node: string;
    };
    EditGroupingV1BlocksEditGroupingV1: {
      /** @enum {string} */
      kind: "into_blocks";
      node: string;
    };
    EditGroupingV1ByEditGroupingV1: {
      /** @enum {string} */
      kind: "by";
      node: string;
    };
    EditGroupingV1EndingEditGroupingV1: {
      /** @enum {string} */
      kind: "ending_with";
      node: string;
    };
    EditGroupingV1NoEditGroupingV1: {
      /** @enum {string} */
      kind: "none";
    };
    EditGroupingV1StartingEditGroupingV1: {
      /** @enum {string} */
      kind: "starting_with";
      node: string;
    };
    EditOperationV1:
      | components["schemas"]["EditOperationV1BindEditV1"]
      | components["schemas"]["EditOperationV1RetargetBindingEditV1"]
      | components["schemas"]["EditOperationV1RemoveBindingEditV1"]
      | components["schemas"]["EditOperationV1ConnectEditV1"]
      | components["schemas"]["EditOperationV1DisconnectEditV1"]
      | components["schemas"]["EditOperationV1EditConstantV1"]
      | components["schemas"]["EditOperationV1EditCallV1"]
      | components["schemas"]["EditOperationV1EditValueMapV1"]
      | components["schemas"]["EditOperationV1RemoveNodesEditV1"]
      | components["schemas"]["EditOperationV1AddScopeEditV1"]
      | components["schemas"]["EditOperationV1RemoveScopeEditV1"]
      | components["schemas"]["EditOperationV1SetIterationEditV1"]
      | components["schemas"]["EditOperationV1SetFilterEditV1"]
      | components["schemas"]["EditOperationV1SetSortEditV1"]
      | components["schemas"]["EditOperationV1SetGroupingEditV1"]
      | components["schemas"]["EditOperationV1AddFailureRuleEditV1"]
      | components["schemas"]["EditOperationV1RemoveFailureRuleEditV1"]
      | components["schemas"]["EditOperationV1MoveFailureRuleEditV1"]
      | components["schemas"]["EditOperationV1EditFailureRuleV1"];
    EditOperationV1AddFailureRuleEditV1: {
      /** @enum {string} */
      op: "add_failure_rule";
      rule: components["schemas"]["EditFailureRuleDefinitionV1"];
    };
    EditOperationV1AddScopeEditV1: {
      field: string;
      /** @enum {string} */
      op: "add_scope";
      parent: components["schemas"]["EditScopeV1"];
    };
    EditOperationV1BindEditV1: {
      binding: components["schemas"]["EditBindingV1"];
      expression: components["schemas"]["EditExpressionV1"];
      /** @enum {string} */
      op: "bind";
    };
    EditOperationV1ConnectEditV1: {
      expression: components["schemas"]["EditExpressionV1"];
      input: string;
      node: string;
      /** @enum {string} */
      op: "connect";
    };
    EditOperationV1DisconnectEditV1: {
      input: string;
      node: string;
      /** @enum {string} */
      op: "disconnect";
      replacement?: null | string;
    };
    EditOperationV1EditCallV1: {
      args: string[];
      function: string;
      node: string;
      /** @enum {string} */
      op: "edit_call";
    };
    EditOperationV1EditConstantV1: {
      node: string;
      /** @enum {string} */
      op: "edit_constant";
      value: components["schemas"]["EditScalarV1"];
    };
    EditOperationV1EditFailureRuleV1: {
      index: string;
      /** @enum {string} */
      op: "edit_failure_rule";
      rule: components["schemas"]["EditFailureRuleDefinitionV1"];
    };
    EditOperationV1EditValueMapV1: {
      node: string;
      /** @enum {string} */
      op: "edit_value_map";
      value: components["schemas"]["EditValueMapDefinitionV1"];
    };
    EditOperationV1MoveFailureRuleEditV1: {
      from: string;
      /** @enum {string} */
      op: "move_failure_rule";
      to: string;
    };
    EditOperationV1RemoveBindingEditV1: {
      binding: components["schemas"]["EditBindingV1"];
      /** @enum {string} */
      op: "remove_binding";
    };
    EditOperationV1RemoveFailureRuleEditV1: {
      index: string;
      /** @enum {string} */
      op: "remove_failure_rule";
    };
    EditOperationV1RemoveNodesEditV1: {
      nodes: string[];
      /** @enum {string} */
      op: "remove_nodes";
      replacements: string[];
      scope: components["schemas"]["EditScopeV1"];
    };
    EditOperationV1RemoveScopeEditV1: {
      /** @enum {string} */
      op: "remove_scope";
      scope: components["schemas"]["EditScopeV1"];
    };
    EditOperationV1RetargetBindingEditV1: {
      binding: components["schemas"]["EditBindingV1"];
      node: string;
      /** @enum {string} */
      op: "retarget_binding";
      to: components["schemas"]["EditBindingV1"];
    };
    EditOperationV1SetFilterEditV1: {
      node?: null | string;
      /** @enum {string} */
      op: "set_filter";
      scope: components["schemas"]["EditScopeV1"];
    };
    EditOperationV1SetGroupingEditV1: {
      grouping: components["schemas"]["EditGroupingV1"];
      /** @enum {string} */
      op: "set_grouping";
      scope: components["schemas"]["EditScopeV1"];
    };
    EditOperationV1SetIterationEditV1: {
      /** @enum {string} */
      op: "set_iteration";
      scope: components["schemas"]["EditScopeV1"];
      source?: null | components["schemas"]["EditSourceCollectionV1"];
    };
    EditOperationV1SetSortEditV1: {
      keys: components["schemas"]["EditSortKeyV1"][];
      /** @enum {string} */
      op: "set_sort";
      scope: components["schemas"]["EditScopeV1"];
    };
    EditScalarTypeV1:
      | components["schemas"]["EditScalarTypeV1StringEditScalarTypeV1"]
      | components["schemas"]["EditScalarTypeV1IntEditScalarTypeV1"]
      | components["schemas"]["EditScalarTypeV1FloatEditScalarTypeV1"]
      | components["schemas"]["EditScalarTypeV1BoolEditScalarTypeV1"];
    EditScalarTypeV1BoolEditScalarTypeV1: {
      /** @enum {string} */
      kind: "bool";
    };
    EditScalarTypeV1FloatEditScalarTypeV1: {
      /** @enum {string} */
      kind: "float";
    };
    EditScalarTypeV1IntEditScalarTypeV1: {
      /** @enum {string} */
      kind: "int";
    };
    EditScalarTypeV1StringEditScalarTypeV1: {
      /** @enum {string} */
      kind: "string";
    };
    EditScalarV1:
      | components["schemas"]["EditScalarV1NullEditScalarV1"]
      | components["schemas"]["EditScalarV1JsonNullEditScalarV1"]
      | components["schemas"]["EditScalarV1XmlNilEditScalarV1"]
      | components["schemas"]["EditScalarV1BoolEditScalarV1"]
      | components["schemas"]["EditScalarV1IntEditScalarV1"]
      | components["schemas"]["EditScalarV1FloatEditScalarV1"]
      | components["schemas"]["EditScalarV1StringEditScalarV1"];
    EditScalarV1BoolEditScalarV1: {
      text: string;
      /** @enum {string} */
      type: "bool";
    };
    EditScalarV1FloatEditScalarV1: {
      text: string;
      /** @enum {string} */
      type: "float";
    };
    EditScalarV1IntEditScalarV1: {
      text: string;
      /** @enum {string} */
      type: "int";
    };
    EditScalarV1JsonNullEditScalarV1: {
      /** @enum {string} */
      type: "json_null";
    };
    EditScalarV1NullEditScalarV1: {
      /** @enum {string} */
      type: "null";
    };
    EditScalarV1StringEditScalarV1: {
      text: string;
      /** @enum {string} */
      type: "string";
    };
    EditScalarV1XmlNilEditScalarV1: {
      /** @enum {string} */
      type: "xml_nil";
    };
    EditScopeV1: {
      children: string[];
      target: components["schemas"]["EditTargetV1"];
    };
    EditScriptV1: {
      /** @enum {string} */
      format: "ferrule.edit.v1";
      operations: components["schemas"]["EditOperationV1"][];
    };
    EditSortKeyV1: {
      descending: boolean;
      node: string;
    };
    EditSourceCollectionV1:
      | components["schemas"]["EditSourceCollectionV1LegacyEditCollectionV1"]
      | components["schemas"]["EditSourceCollectionV1PrimaryEditCollectionV1"]
      | components["schemas"]["EditSourceCollectionV1NamedEditCollectionV1"];
    EditSourceCollectionV1LegacyEditCollectionV1: {
      /** @enum {string} */
      kind: "legacy";
      path: string[];
    };
    EditSourceCollectionV1NamedEditCollectionV1: {
      /** @enum {string} */
      kind: "named";
      name: string;
      path: string[];
    };
    EditSourceCollectionV1PrimaryEditCollectionV1: {
      /** @enum {string} */
      kind: "primary";
      path: string[];
    };
    EditSourceOwnerV1:
      | components["schemas"]["EditSourceOwnerV1PrimaryEditSourceV1"]
      | components["schemas"]["EditSourceOwnerV1NamedEditSourceV1"];
    EditSourceOwnerV1NamedEditSourceV1: {
      /** @enum {string} */
      kind: "named";
      name: string;
    };
    EditSourceOwnerV1PrimaryEditSourceV1: {
      /** @enum {string} */
      kind: "primary";
    };
    EditTargetV1:
      | components["schemas"]["EditTargetV1PrimaryEditTargetV1"]
      | components["schemas"]["EditTargetV1NamedEditTargetV1"];
    EditTargetV1NamedEditTargetV1: {
      /** @enum {string} */
      kind: "named";
      name: string;
    };
    EditTargetV1PrimaryEditTargetV1: {
      /** @enum {string} */
      kind: "primary";
    };
    EditValueMapDefinitionV1: {
      default?: null | components["schemas"]["EditScalarV1"];
      input_type?: null | components["schemas"]["EditScalarTypeV1"];
      table: components["schemas"]["EditValueMapEntryV1"][];
    };
    EditValueMapEntryV1: {
      from: components["schemas"]["EditScalarV1"];
      to: components["schemas"]["EditScalarV1"];
    };
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
    HelpBlockV1:
      | components["schemas"]["HelpBlockV1HelpHeadingV1"]
      | components["schemas"]["HelpBlockV1HelpParagraphV1"]
      | components["schemas"]["HelpBlockV1HelpListV1"]
      | components["schemas"]["HelpBlockV1HelpTableV1"]
      | components["schemas"]["HelpBlockV1HelpCodeBlockV1"];
    HelpBlockV1HelpCodeBlockV1: {
      /** @enum {string} */
      kind: "codeBlock";
      text: string;
    };
    HelpBlockV1HelpHeadingV1: {
      anchor: string;
      /** @enum {string} */
      kind: "heading";
      /** Format: int32 */
      level: number;
      text: string;
    };
    HelpBlockV1HelpListV1: {
      items: components["schemas"]["HelpListItemV1"][];
      /** @enum {string} */
      kind: "list";
      ordered: boolean;
    };
    HelpBlockV1HelpParagraphV1: {
      content: components["schemas"]["HelpInlineV1"][];
      /** @enum {string} */
      kind: "paragraph";
    };
    HelpBlockV1HelpTableV1: {
      header: components["schemas"]["HelpTableCellV1"][];
      /** @enum {string} */
      kind: "table";
      rows: components["schemas"]["HelpTableRowV1"][];
    };
    HelpInlineV1:
      | components["schemas"]["HelpInlineV1HelpTextV1"]
      | components["schemas"]["HelpInlineV1HelpStrongV1"]
      | components["schemas"]["HelpInlineV1HelpEmphasisV1"]
      | components["schemas"]["HelpInlineV1HelpCodeV1"]
      | components["schemas"]["HelpInlineV1HelpLinkV1"];
    HelpInlineV1HelpCodeV1: {
      /** @enum {string} */
      kind: "code";
      text: string;
    };
    HelpInlineV1HelpEmphasisV1: {
      /** @enum {string} */
      kind: "emphasis";
      text: string;
    };
    HelpInlineV1HelpLinkV1: {
      anchor: null | string;
      /** @enum {string} */
      kind: "link";
      sectionId: string;
      text: string;
    };
    HelpInlineV1HelpStrongV1: {
      /** @enum {string} */
      kind: "strong";
      text: string;
    };
    HelpInlineV1HelpTextV1: {
      /** @enum {string} */
      kind: "text";
      text: string;
    };
    HelpListItemV1: {
      content: components["schemas"]["HelpInlineV1"][];
    };
    HelpSectionListV1: {
      sections: components["schemas"]["HelpSectionSummaryV1"][];
    };
    HelpSectionSummaryV1: {
      id: string;
      summary: string;
      title: string;
    };
    HelpSectionV1: {
      blocks: components["schemas"]["HelpBlockV1"][];
      id: string;
      summary: string;
      title: string;
    };
    HelpTableCellV1: {
      content: components["schemas"]["HelpInlineV1"][];
    };
    HelpTableRowV1: {
      cells: components["schemas"]["HelpTableCellV1"][];
    };
    InvitationProjectRoleV1: {
      /** Format: uuid */
      projectId: string;
      role: string;
    };
    JsonElement: unknown;
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
    LocalSandboxBuildV1: {
      buildId: string;
      createdAtUtc: string;
      environmentId: string;
      environmentVersion: string;
      revisionId: string;
    };
    LocalSandboxDeploymentV1: {
      deploymentId: string;
      outcome: string;
      qualificationId: null | string;
      releaseId: null | string;
      target: null | components["schemas"]["LocalSandboxTargetV1"];
    };
    LocalSandboxDeployV1: {
      buildId: string;
    };
    LocalSandboxDocumentV1: {
      content: null | string;
      mediaType: null | string;
      name: null | string;
      sha256: null | string;
    };
    LocalSandboxMessagePageV1: {
      items: components["schemas"]["LocalSandboxMessageV1"][];
    };
    LocalSandboxMessageV1: {
      buildId: null | string;
      /** Format: int64 */
      durationMs: number;
      inputs: null | components["schemas"]["LocalSandboxDocumentV1"][];
      messageId: string;
      outcome: null | string;
      outputs: null | components["schemas"]["LocalSandboxDocumentV1"][];
      parameters: null | components["schemas"]["LocalSandboxParameterV1"][];
      releaseId: null | string;
      replayed: boolean;
      replayOf: null | string;
      sentAtUtc: string;
      source: string;
      status: string;
      targetId: string;
    };
    LocalSandboxParameterV1: {
      kind: string;
      name: string;
      value: string;
    };
    LocalSandboxSampleV1: {
      kind: string;
      name: string;
      reference: string;
    };
    LocalSandboxSendV1: {
      inputs: null | components["schemas"]["LocalSandboxDocumentV1"][];
      parameters: null | components["schemas"]["LocalSandboxParameterV1"][];
      reference: null | string;
      replayOf: null | string;
      source: string;
    };
    LocalSandboxStateV1: {
      builds: components["schemas"]["LocalSandboxBuildV1"][];
      mode: string;
      samples: components["schemas"]["LocalSandboxSampleV1"][];
      targets: components["schemas"]["LocalSandboxTargetV1"][];
    };
    LocalSandboxTargetV1: {
      activeReleaseId: null | string;
      enabled: boolean;
      environmentId: string;
      environmentVersion: string;
      runtimeImage: string;
      targetId: string;
      version: string;
    };
    MappingAutomationV1: {
      enabled: boolean;
    };
    MappingDraftBaseV1: {
      /** Format: int32 */
      nativeByteCount: number;
      nativeDigest: components["schemas"]["ArtifactDigest"];
      revision: components["schemas"]["CatalogRevisionScopeV1"];
      sequence: string;
    };
    MappingDraftCheckpointV1: {
      isValid: boolean;
      /** Format: int32 */
      nativeByteCount: number;
      nativeDigest: components["schemas"]["ArtifactDigest"];
      /** Format: int32 */
      totalIssueCount: number;
      version: string;
    };
    MappingDraftCreateV1: {
      baseRevisionId: string;
    };
    MappingDraftFailureV1: {
      code: string;
      message: string;
      messageTruncated: boolean;
      operationIndex: null | string;
      status: string;
    };
    MappingDraftListV1: {
      drafts: components["schemas"]["MappingDraftV1"][];
    };
    MappingDraftRebaseV1: {
      targetRevisionId: string;
    };
    MappingDraftResultV1: {
      draft: null | components["schemas"]["MappingDraftV1"];
      failure?: null | components["schemas"]["MappingDraftFailureV1"];
      outcome: string;
    };
    MappingDraftSaveV1: {
      originalFilename?: null | string;
      version: string;
    };
    MappingDraftV1: {
      authorPrincipalId: string;
      base: components["schemas"]["MappingDraftBaseV1"];
      /** Format: date-time */
      changedAtUtc: string;
      checkpoint: null | components["schemas"]["MappingDraftCheckpointV1"];
      /** Format: date-time */
      createdAtUtc: string;
      /** Format: date-time */
      expiresAtUtc: string;
      id: string;
      mapping: components["schemas"]["CatalogMappingScopeV1"];
      /** Format: int32 */
      operationCount: number;
      operationsDigest: null | components["schemas"]["ArtifactDigest"];
      savedRevisionId: null | string;
      savingFilename?: null | string;
      savingOperationId: null | string;
      savingRequestId?: null | string;
      state: string;
      version: string;
    };
    MappingDraftVersionV1: {
      version: string;
    };
    /** @enum {unknown} */
    MappingOperation: "inspect" | "project" | "validate" | "preview" | "build";
    MappingRevisionRestoreV1: {
      expectedHeadRevisionId: string;
    };
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
    NotificationDeliveriesV1: {
      deliveries: components["schemas"]["NotificationDeliveryV1"][];
      nextId: null | string;
    };
    NotificationDeliveryAttemptV1: {
      /** Format: int32 */
      attempt: number;
      outcome: string;
      recordedAtUtc: string;
    };
    NotificationDeliveryV1: {
      /** Format: int32 */
      attempts: number;
      destinationId: string;
      event: string;
      history: components["schemas"]["NotificationDeliveryAttemptV1"][];
      id: string;
      nextAttemptAtUtc: null | string;
      resourceId: string;
      state: string;
    };
    NotificationDestinationEnableV1: {
      destinationId: string;
    };
    NotificationDestinationsV1: {
      destinations: components["schemas"]["NotificationDestinationV1"][];
    };
    NotificationDestinationV1: {
      channel: string;
      enabled: boolean;
      id: string;
    };
    NotificationOptionsV1: {
      emailAvailable: boolean;
      events: string[];
      webhooks: string[];
    };
    NotificationReadV1: Record<string, never>;
    NotificationSubscriptionV1: {
      email: boolean;
      events: string[];
      inApp: boolean;
      webhooks: string[];
    };
    NotificationsV1: {
      nextId: null | string;
      notifications: components["schemas"]["NotificationV1"][];
    };
    NotificationV1: {
      createdAtUtc: string;
      event: string;
      id: string;
      link: string;
      mappingId: null | string;
      projectId: null | string;
      read: boolean;
      resourceId: string;
      revisionId: null | string;
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
    OrganizationAdministrationV1: {
      audit: components["schemas"]["AdministrationAuditV1"][];
      invitations: components["schemas"]["OrganizationInvitationV1"][];
      members: components["schemas"]["OrganizationMemberV1"][];
      /** Format: int64 */
      version: number;
    };
    OrganizationInvitationV1: {
      /** Format: uuid */
      expectedObjectId: null | string;
      /** Format: date-time */
      expiresAt: string;
      /** Format: uuid */
      invitationId: string;
      projectRoles: components["schemas"]["InvitationProjectRoleV1"][];
      /** Format: date-time */
      redeemedAt: null | string;
      /** Format: date-time */
      revokedAt: null | string;
      /** Format: uuid */
      tenantId: string;
    };
    OrganizationMemberV1: {
      active: boolean;
      bindings: components["schemas"]["AdministrationBindingV1"][];
      principalActive: boolean;
      /** Format: uuid */
      principalId: string;
      role: string;
      /** Format: int64 */
      version: number;
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
    ProjectAdministrationV1: {
      grants: components["schemas"]["AdministrationInvocationGrantV1"][];
      members: components["schemas"]["ProjectMemberV1"][];
      /** Format: int64 */
      version: number;
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
    ProjectMemberV1: {
      active: boolean;
      payloadRead: boolean;
      /** Format: int64 */
      payloadVersion: number;
      /** Format: uuid */
      principalId: string;
      role: string;
      /** Format: int64 */
      version: number;
    };
    RegressionCaseCapabilitiesV1: {
      comparisonPolicies?: null | string[];
      comparisonPolicy: string;
      csvRuntime?: null | components["schemas"]["RegressionRuntimeV1"];
      documentRuntime?: null | components["schemas"]["RegressionRuntimeV1"];
      limits: components["schemas"]["RegressionLimitsV1"];
      publishCases: boolean;
      readSamples: boolean;
      runtime: components["schemas"]["RegressionRuntimeV1"];
      xmlRuntime?: null | components["schemas"]["RegressionRuntimeV1"];
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
      comparisonOptions?:
        null | components["schemas"]["RegressionEdiComparisonOptionsV1"];
      comparisonPolicy: string;
      currentDateTime: string;
      expected: components["schemas"]["RegressionDocumentV1"][];
      limits: components["schemas"]["RegressionLimitsV1"];
      name: string;
      namedInputs: components["schemas"]["RegressionInputDocumentV1"][];
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
      csvDiagnostic?: null | components["schemas"]["RegressionCsvDiagnosticV1"];
      differences: components["schemas"]["RegressionDifferenceV1"][];
      documentDiagnostic?:
        null | components["schemas"]["RegressionDocumentDiagnosticV1"];
      /** Format: int32 */
      expectedOutputs: number;
      notEvaluatedReason: string;
      outcome: string;
      policy: string;
      /** Format: int32 */
      totalDifferences: number;
      truncated: boolean;
    };
    RegressionCsvDiagnosticV1: {
      location: components["schemas"]["RegressionCsvLocationV1"];
      name: null | string;
      side: string;
    };
    RegressionCsvLocationV1: {
      /** Format: int32 */
      column: number;
      /** Format: int32 */
      row: number;
    };
    RegressionDifferenceV1: {
      /** Format: int32 */
      actualBytes: null | number;
      actualDigest: null | string;
      csvLocation?: null | components["schemas"]["RegressionCsvLocationV1"];
      /** Format: int32 */
      expectedBytes: null | number;
      expectedDigest: null | string;
      /** Format: int32 */
      firstDifferentByte: null | number;
      kind: string;
      location?: null | components["schemas"]["RegressionDocumentLocationV1"];
      name: null | string;
    };
    RegressionDocumentDiagnosticV1: {
      location: components["schemas"]["RegressionDocumentLocationV1"];
      name: null | string;
      side: string;
    };
    RegressionDocumentLocationV1: {
      /** Format: int32 */
      component: number;
      /** Format: int32 */
      element: number;
      /** Format: int32 */
      interchange: number;
      /** Format: int32 */
      repetition: number;
      /** Format: int32 */
      segment: number;
      segmentId: string;
    };
    RegressionDocumentV1: {
      bytesBase64Url: string;
      csvDialect?: null | components["schemas"]["CsvDocumentDialectV1"];
      mediaType?: null | string;
      name: null | string;
      xmlMetadata?: null | components["schemas"]["XmlDocumentMetadataV1"];
    };
    RegressionEdiComparisonOptionsV1: {
      compareSeparators: boolean;
      ignoreControlNumbers: boolean;
      ignoreDates: boolean;
    };
    RegressionEvaluationsV1: {
      cases: components["schemas"]["RegressionEvaluationV1"][];
      nextCaseId: null | string;
    };
    RegressionEvaluationV1: {
      /** Format: int32 */
      attempts: number;
      caseId: string;
      originalRevisionId: string;
      state: string;
      version: string;
    };
    RegressionInputDocumentV1: {
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
    RegressionRecheckV1: {
      mappingId: string;
      revisionId: string;
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
    RegressionSuitesV1: {
      nextSuiteId: null | string;
      suites: components["schemas"]["RegressionSuiteV1"][];
    };
    RegressionSuiteV1: {
      createdAtUtc: string;
      /** Format: int32 */
      failedCases: number;
      /** Format: int32 */
      passedCases: number;
      /** Format: int32 */
      pendingCases: number;
      revisionId: string;
      state: string;
      suiteId: string;
      /** Format: int32 */
      totalCases: number;
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
    ReleaseActiveMetadataV1: {
      /** Format: uuid */
      actorId: string;
      approval: components["schemas"]["ReleaseApprovalMetadataV1"];
      /** Format: uuid */
      approvalId: string;
      artifactDigest: string;
      /** Format: uuid */
      buildId: string;
      /** Format: uuid */
      qualificationId: string;
      /** Format: uuid */
      releaseId: string;
    };
    ReleaseApprovalMetadataV1: {
      /** Format: uuid */
      approvalId: string;
      /** Format: date-time */
      createdAt: string;
      revoked: boolean;
    };
    ReleaseOperationReceiptV1: {
      operationId: string;
      outcome: string;
    };
    ReleaseOperationsItemV1:
      | components["schemas"]["ReleaseOperationsItemV1ReleaseTargetMetadataV1"]
      | components["schemas"]["ReleaseOperationsItemV1ReleaseMetadataV1"]
      | components["schemas"]["ReleaseOperationsItemV1ReleaseTargetEventV1"]
      | components["schemas"]["ReleaseOperationsItemV1ReleaseActionAuditV1"]
      | components["schemas"]["ReleaseOperationsItemV1ReleaseCallerGrantV1"]
      | components["schemas"]["ReleaseOperationsItemV1ReleaseInvocationMetadataV1"];
    ReleaseOperationsItemV1ReleaseActionAuditV1: {
      action: string;
      /** Format: uuid */
      actor_id: string;
      expected_version: string;
      /** @enum {string} */
      kind: "action";
      /** Format: uuid */
      operation_id: string;
      /** Format: int32 */
      outcome: number;
      /** Format: uuid */
      qualification_id: null | string;
      /** Format: date-time */
      recorded_at: string;
      /** Format: uuid */
      source_release_id: null | string;
    };
    ReleaseOperationsItemV1ReleaseCallerGrantV1: {
      /** Format: uuid */
      grant_id: string;
      /** Format: date-time */
      granted_at: string;
      /** Format: uuid */
      granted_by: string;
      /** @enum {string} */
      kind: "caller";
      /** Format: uuid */
      object_id: string;
      provider: string;
      /** Format: date-time */
      revoked_at: null | string;
      /** Format: uuid */
      revoked_by: null | string;
      /** Format: uuid */
      tenant_id: string;
    };
    ReleaseOperationsItemV1ReleaseInvocationMetadataV1: {
      /** Format: int32 */
      attempt: number;
      /** Format: uuid */
      caller_client_id: string;
      /** Format: date-time */
      finished_at: null | string;
      /** Format: int32 */
      input_bytes: null | number;
      /** @enum {string} */
      kind: "invocation";
      /** Format: double */
      latency_ms: null | number;
      /** Format: uuid */
      object_id: string;
      outcome: null | string;
      /** Format: int32 */
      output_bytes: null | number;
      provider: string;
      /** Format: uuid */
      release_id: null | string;
      /** Format: int64 */
      replay_count: null | number;
      replayed: null | boolean;
      /** Format: uuid */
      request_id: string;
      /** Format: date-time */
      started_at: string;
      state: string;
      /** Format: uuid */
      tenant_id: string;
    };
    ReleaseOperationsItemV1ReleaseMetadataV1: {
      /** Format: uuid */
      actor_id: string;
      /** Format: date-time */
      approval_created_at: string;
      /** Format: uuid */
      approval_id: string;
      approval_revoked: boolean;
      artifact_digest: string;
      /** Format: uuid */
      build_id: string;
      /** Format: date-time */
      created_at: string;
      /** Format: date-time */
      finished_at: null | string;
      /** @enum {string} */
      kind: "release";
      phase: string;
      /** Format: uuid */
      qualification_id: string;
      rejection: string;
      /** Format: uuid */
      release_id: string;
      /** Format: uuid */
      rollback_of_id: null | string;
      runtime_image: string;
    };
    ReleaseOperationsItemV1ReleaseTargetEventV1: {
      action: string;
      /** Format: uuid */
      active_release_id: null | string;
      /** Format: uuid */
      actor_id: null | string;
      enabled: boolean;
      /** @enum {string} */
      kind: "history";
      origin: string;
      /** Format: date-time */
      recorded_at: string;
      version: string;
    };
    ReleaseOperationsItemV1ReleaseTargetMetadataV1: {
      active: null | components["schemas"]["ReleaseActiveMetadataV1"];
      /** Format: uuid */
      active_release_id: null | string;
      enabled: boolean;
      /** Format: uuid */
      environment_id: string;
      environment_version: string;
      /** @enum {string} */
      kind: "target";
      profile: components["schemas"]["ReleaseRuntimeProfileV1"];
      runtime_image: string;
      /** Format: uuid */
      target_id: string;
      version: string;
    };
    ReleaseOperationsPageV1: {
      items: components["schemas"]["ReleaseOperationsItemV1"][];
      /** Format: int32 */
      nextOffset: null | number;
    };
    ReleaseOperationsSummaryV1: {
      /** Format: int64 */
      count: number;
      /** Format: int64 */
      failed: number;
      failuresByCategory: {
        [key: string]: number;
      };
      /** Format: double */
      meanLatencyMs: null | number;
      /** Format: double */
      p95LatencyMs: null | number;
      /** Format: int64 */
      retryable: number;
      /** Format: int64 */
      running: number;
    };
    ReleaseOperationV1: {
      action: string;
      environmentId: string;
      environmentVersion: string;
      qualificationId: null | string;
      sourceReleaseId: null | string;
    };
    ReleaseRuntimeProfileV1: {
      artifactFormat: string;
      id: string;
      /** Format: int64 */
      maximumArtifactBytes: number;
      runtime: string;
      runtimeVersion: string;
      /** Format: int64 */
      version: number;
    };
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
      operations?: null | components["schemas"]["ComparisonEditOperationV1"][];
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
    SpecCreateV1: {
      /** Format: uuid */
      creationKey: string;
    };
    SpecErrorV1: {
      code: string;
    };
    SpecIntakeCapabilitiesV1: {
      formats: components["schemas"]["SpecIntakeFormatV1"][];
      /** Format: int32 */
      maximumTextRequestBytes: number;
      /** Format: int32 */
      version: number;
    };
    SpecIntakeFormatV1: {
      evidenceFormat: string;
      /** Format: int32 */
      extractorVersion: number;
      format: string;
      /** Format: int32 */
      maximumSourceBytes: number;
    };
    SpecIntakeV1: {
      /** Format: uuid */
      extractionObjectId: string;
      /** Format: uuid */
      originalObjectId: string;
      outcome: string;
      /** Format: int64 */
      reservationVersion: number;
    };
    SpecInventoryV1: {
      bundle: components["schemas"]["SpecStatusV1"];
      objects: components["schemas"]["SpecObjectV1"][];
    };
    SpecObjectV1: {
      /** Format: int32 */
      byteCount: number;
      digest: string;
      format: string;
      kind: string;
      /** Format: uuid */
      objectId: string;
      outcome: null | string;
      /** Format: uuid */
      sourceId: string;
      /** Format: int64 */
      sourceVersion: number;
      state: string;
    };
    SpecStatusV1: {
      /** Format: uuid */
      bundleId: string;
      tombstoned: boolean;
      /** Format: int64 */
      version: number;
    };
    SpecTextV1: {
      text: string;
      upload: components["schemas"]["SpecUploadV1"];
    };
    SpecUploadV1: {
      /** Format: int64 */
      extractionExpectedVersion: number;
      /** Format: uuid */
      extractionKey: string;
      /** Format: uuid */
      extractionObjectId: string;
      format: string;
      /** Format: uuid */
      originalObjectId: string;
      /** Format: uuid */
      sourceId: string;
      /** Format: int64 */
      sourceVersion: number;
      /** Format: uuid */
      uploadKey: string;
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
    XmlDocumentMetadataV1: {
      safetyProfile: string;
      schema: string;
      schemaHints: null | components["schemas"]["XmlSchemaHintsV1"];
    };
    XmlSchemaHintsV1: {
      locations: components["schemas"]["XmlSchemaLocationV1"][];
      noNamespaceLocation: null | string;
    };
    XmlSchemaLocationV1: {
      location: string;
      namespace: string;
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
          Location?: string;
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
        "X-Weft-Invitation"?: string;
        "X-Weft-Login": "1";
        "X-Weft-Return-To"?: string;
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
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
  GetCatalogInterchangeCapabilities: {
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
          /** @description Always no-store; receipts and operation results are not cacheable. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogInterchangeCapabilitiesV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Always no-store; receipts and operation results are not cacheable. */
          "Cache-Control"?: "no-store";
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
  GetReadiness: {
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
      /** @description Service Unavailable */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["HealthResponseV1"];
        };
      };
    };
  };
  GetHelpSections: {
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
          "application/json": components["schemas"]["HelpSectionListV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
  GetHelpSection: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        sectionId: string;
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
          "application/json": components["schemas"]["HelpSectionV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
  GetOrganizationAdministration: {
    parameters: {
      query?: never;
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
          ETag?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["OrganizationAdministrationV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Identity refused: no session or a refused bearer token. */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Identity refused: a CSRF refusal, or a valid bearer token without an enabled binding. */
      403: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Precondition Failed */
      412: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Precondition Required */
      428: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
    };
  };
  DisableAdministrationBinding: {
    parameters: {
      query?: never;
      header: {
        /** @description Strong administration ETag from the latest authorized organization or project administration read. */
        "If-Match": string;
      };
      path: {
        bindingId: string;
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
          "application/json": components["schemas"]["AdministrationReceiptV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Identity refused: no session or a refused bearer token. */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Identity refused: a CSRF refusal, or a valid bearer token without an enabled binding. */
      403: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Precondition Failed */
      412: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Precondition Required */
      428: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
    };
  };
  CreateOrganizationInvitation: {
    parameters: {
      query?: never;
      header: {
        /** @description Strong administration ETag from the latest authorized organization or project administration read. */
        "If-Match": string;
      };
      path: {
        organizationId: string;
      };
      cookie?: never;
    };
    requestBody: {
      content: {
        "application/json": components["schemas"]["AdministrationInvitationPostV1"];
      };
    };
    responses: {
      /** @description OK */
      200: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationInvitationCreatedV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Identity refused: no session or a refused bearer token. */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Identity refused: a CSRF refusal, or a valid bearer token without an enabled binding. */
      403: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Precondition Failed */
      412: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Precondition Required */
      428: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
    };
  };
  RevokeOrganizationInvitation: {
    parameters: {
      query?: never;
      header: {
        /** @description Strong administration ETag from the latest authorized organization or project administration read. */
        "If-Match": string;
      };
      path: {
        invitationId: string;
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
          "application/json": components["schemas"]["AdministrationReceiptV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Identity refused: no session or a refused bearer token. */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Identity refused: a CSRF refusal, or a valid bearer token without an enabled binding. */
      403: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Precondition Failed */
      412: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Precondition Required */
      428: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
    };
  };
  PutOrganizationMember: {
    parameters: {
      query?: never;
      header: {
        /** @description Strong administration ETag from the latest authorized organization or project administration read. */
        "If-Match": string;
      };
      path: {
        organizationId: string;
        principalId: string;
      };
      cookie?: never;
    };
    requestBody: {
      content: {
        "application/json": components["schemas"]["AdministrationMemberPutV1"];
      };
    };
    responses: {
      /** @description OK */
      200: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationReceiptV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Identity refused: no session or a refused bearer token. */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Identity refused: a CSRF refusal, or a valid bearer token without an enabled binding. */
      403: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Precondition Failed */
      412: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Precondition Required */
      428: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
    };
  };
  DisableAdministrationPrincipal: {
    parameters: {
      query?: never;
      header: {
        /** @description Strong administration ETag from the latest authorized organization or project administration read. */
        "If-Match": string;
      };
      path: {
        organizationId: string;
        principalId: string;
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
          "application/json": components["schemas"]["AdministrationReceiptV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Identity refused: no session or a refused bearer token. */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Identity refused: a CSRF refusal, or a valid bearer token without an enabled binding. */
      403: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Precondition Failed */
      412: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Precondition Required */
      428: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
    };
  };
  GetWorkspaceChatAvailability: {
    parameters: {
      query?: never;
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
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatWorkspaceAvailabilityV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Payload Too Large */
      413: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unsupported Media Type */
      415: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Too Many Requests */
      429: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Internal Server Error */
      500: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
    };
  };
  GetOrganizationChatConversations: {
    parameters: {
      query?: {
        /** @description Continue after the last conversation identity from the previous page. */
        after?: string;
        /** @description Maximum conversations, newest first; canonical decimal. */
        limit?: number;
        /** @description Only conversations about this freshly readable exact revision. */
        revisionId?: string;
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
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatOrganizationConversationPageV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Payload Too Large */
      413: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unsupported Media Type */
      415: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Too Many Requests */
      429: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Internal Server Error */
      500: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
    };
  };
  CreateWorkspaceChatConversation: {
    parameters: {
      query?: never;
      header?: {
        /** @description Required with weftSession; omitted with entraBearer. The exact configured origin for this browser request. */
        Origin?: string;
        /** @description Required with weftSession; omitted with entraBearer. The antiforgery request token issued by the catalog context; session tokens bind to the session epoch. */
        "X-XSRF-TOKEN"?: string;
      };
      path: {
        organizationId: string;
      };
      cookie?: never;
    };
    requestBody: {
      content: {
        "application/json": components["schemas"]["ChatWorkspaceConversationCreateV1"];
      };
    };
    responses: {
      /** @description OK */
      200: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatOrganizationConversationV1"];
        };
      };
      /** @description Created */
      201: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatOrganizationConversationV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Payload Too Large */
      413: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unsupported Media Type */
      415: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Too Many Requests */
      429: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Internal Server Error */
      500: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
    };
  };
  GetWorkspaceChatConversation: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        conversationId: string;
        organizationId: string;
      };
      cookie?: never;
    };
    requestBody?: never;
    responses: {
      /** @description OK */
      200: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatWorkspaceConversationDetailV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Payload Too Large */
      413: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unsupported Media Type */
      415: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Too Many Requests */
      429: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Internal Server Error */
      500: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
    };
  };
  CreateWorkspaceChatTurn: {
    parameters: {
      query?: never;
      header?: {
        /** @description Required with weftSession; omitted with entraBearer. The exact configured origin for this browser request. */
        Origin?: string;
        /** @description Required with weftSession; omitted with entraBearer. The antiforgery request token issued by the catalog context; session tokens bind to the session epoch. */
        "X-XSRF-TOKEN"?: string;
      };
      path: {
        conversationId: string;
        organizationId: string;
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
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatTurnV1"];
        };
      };
      /** @description Created */
      201: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatTurnV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Payload Too Large */
      413: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unsupported Media Type */
      415: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Too Many Requests */
      429: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Internal Server Error */
      500: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
    };
  };
  CancelWorkspaceChatTurn: {
    parameters: {
      query?: never;
      header?: {
        /** @description Required with weftSession; omitted with entraBearer. The exact configured origin for this browser request. */
        Origin?: string;
        /** @description Required with weftSession; omitted with entraBearer. The antiforgery request token issued by the catalog context; session tokens bind to the session epoch. */
        "X-XSRF-TOKEN"?: string;
      };
      path: {
        conversationId: string;
        organizationId: string;
        turnId: string;
      };
      cookie?: never;
    };
    requestBody?: never;
    responses: {
      /** @description OK */
      200: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatTurnV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Payload Too Large */
      413: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unsupported Media Type */
      415: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Too Many Requests */
      429: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Internal Server Error */
      500: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
    };
  };
  StreamWorkspaceChatTurnEvents: {
    parameters: {
      query?: {
        /** @description Resume strictly after this durable event sequence; canonical decimal. */
        after?: number;
      };
      header?: never;
      path: {
        conversationId: string;
        organizationId: string;
        turnId: string;
      };
      cookie?: never;
    };
    requestBody?: never;
    responses: {
      /** @description OK */
      200: {
        headers: {
          "Cache-Control"?: "no-store";
          "X-Accel-Buffering"?: "no";
          [name: string]: unknown;
        };
        content: {
          "text/event-stream": components["schemas"]["ChatTurnEventV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Payload Too Large */
      413: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unsupported Media Type */
      415: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Too Many Requests */
      429: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Internal Server Error */
      500: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
    };
  };
  GetChatMappingEditProposals: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        conversationId: string;
        organizationId: string;
        turnId: string;
      };
      cookie?: never;
    };
    requestBody?: never;
    responses: {
      /** @description OK */
      200: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatMappingEditProposalPageV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Payload Too Large */
      413: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unsupported Media Type */
      415: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Too Many Requests */
      429: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Internal Server Error */
      500: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
    };
  };
  GetChatMappingEditProposal: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        conversationId: string;
        organizationId: string;
        proposalId: string;
        turnId: string;
      };
      cookie?: never;
    };
    requestBody?: never;
    responses: {
      /** @description OK */
      200: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatMappingEditProposalV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Payload Too Large */
      413: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unsupported Media Type */
      415: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Too Many Requests */
      429: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Internal Server Error */
      500: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
    };
  };
  ApproveChatMappingEditProposal: {
    parameters: {
      query?: never;
      header?: {
        /** @description Required with weftSession; omitted with entraBearer. The exact configured origin for this browser request. */
        Origin?: string;
        /** @description Required with weftSession; omitted with entraBearer. The antiforgery request token issued by the catalog context; session tokens bind to the session epoch. */
        "X-XSRF-TOKEN"?: string;
      };
      path: {
        conversationId: string;
        organizationId: string;
        proposalId: string;
        turnId: string;
      };
      cookie?: never;
    };
    requestBody: {
      content: {
        "application/json": components["schemas"]["ChatMappingEditApprovalV1"];
      };
    };
    responses: {
      /** @description OK */
      200: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatMappingEditProposalV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Payload Too Large */
      413: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unsupported Media Type */
      415: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Too Many Requests */
      429: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Internal Server Error */
      500: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
    };
  };
  GetChatMappingEditOperations: {
    parameters: {
      query: {
        /** @description The exact immutable proposal fingerprint being reviewed. */
        expectedFingerprint: string;
      };
      header?: never;
      path: {
        conversationId: string;
        organizationId: string;
        proposalId: string;
        turnId: string;
      };
      cookie?: never;
    };
    requestBody?: never;
    responses: {
      /** @description OK */
      200: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatMappingEditOperationsV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Payload Too Large */
      413: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unsupported Media Type */
      415: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Too Many Requests */
      429: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Internal Server Error */
      500: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
    };
  };
  RecoverChatMappingEditProposal: {
    parameters: {
      query?: never;
      header?: {
        /** @description Required with weftSession; omitted with entraBearer. The exact configured origin for this browser request. */
        Origin?: string;
        /** @description Required with weftSession; omitted with entraBearer. The antiforgery request token issued by the catalog context; session tokens bind to the session epoch. */
        "X-XSRF-TOKEN"?: string;
      };
      path: {
        conversationId: string;
        organizationId: string;
        proposalId: string;
        turnId: string;
      };
      cookie?: never;
    };
    requestBody: {
      content: {
        "application/json": components["schemas"]["ChatMappingEditApprovalV1"];
      };
    };
    responses: {
      /** @description OK */
      200: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatMappingEditProposalV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Payload Too Large */
      413: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unsupported Media Type */
      415: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Too Many Requests */
      429: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Internal Server Error */
      500: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
    };
  };
  GetChatMappingWriteReceipts: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        conversationId: string;
        organizationId: string;
        turnId: string;
      };
      cookie?: never;
    };
    requestBody?: never;
    responses: {
      /** @description OK */
      200: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatMappingWriteReceiptPageV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Payload Too Large */
      413: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unsupported Media Type */
      415: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Too Many Requests */
      429: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Internal Server Error */
      500: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
    };
  };
  RecoverChatMappingWriteReceipts: {
    parameters: {
      query?: never;
      header?: {
        /** @description Required with weftSession; omitted with entraBearer. The exact configured origin for this browser request. */
        Origin?: string;
        /** @description Required with weftSession; omitted with entraBearer. The antiforgery request token issued by the catalog context; session tokens bind to the session epoch. */
        "X-XSRF-TOKEN"?: string;
      };
      path: {
        conversationId: string;
        organizationId: string;
        turnId: string;
      };
      cookie?: never;
    };
    requestBody?: never;
    responses: {
      /** @description OK */
      200: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatMappingWriteReceiptPageV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Payload Too Large */
      413: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unsupported Media Type */
      415: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Too Many Requests */
      429: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Internal Server Error */
      500: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
    };
  };
  GetNotificationInbox: {
    parameters: {
      query?: {
        /** @description Continue after the previous page's notification ID. */
        afterId?: string;
        /** @description Page size: a canonical decimal from 1 to 100, 50 when absent. */
        limit?: number;
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
          "application/json": components["schemas"]["NotificationsV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
  ReadInboxNotification: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        notificationId: string;
        organizationId: string;
      };
      cookie?: never;
    };
    requestBody: {
      content: {
        "application/json": components["schemas"]["NotificationReadV1"];
      };
    };
    responses: {
      /** @description OK */
      200: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AutomationSavedV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
          "Cache-Control": "no-store";
          /** @description SHA-256 of the exact retained output bytes (sha-256=:base64:). */
          "Content-Digest": string;
          "Content-Disposition": "attachment";
          "X-Content-Type-Options": "nosniff";
          [name: string]: unknown;
        };
        content: {
          "application/edi-x12": string;
          "application/edifact": string;
          "application/json": string;
          "application/xml": string;
          "text/csv": string;
          "text/tab-separated-values": string;
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Identity refused: a CSRF refusal, or a valid bearer token without an enabled binding. */
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
      /** @description Identity validation, identity persistence or audit persistence is unavailable. */
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
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
        /** @description Required with weftSession; omitted with entraBearer. One exact canonical configured browser origin, checked before any body read. */
        Origin?: string;
        /** @description Required with weftSession; omitted with entraBearer. Cookie-bound framework request token from a successfully admitted catalog context, at most 4096 characters. */
        "X-XSRF-TOKEN"?: string;
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
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
  GetProjectAdministration: {
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
          ETag?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ProjectAdministrationV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Identity refused: no session or a refused bearer token. */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Identity refused: a CSRF refusal, or a valid bearer token without an enabled binding. */
      403: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Precondition Failed */
      412: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Precondition Required */
      428: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
    };
  };
  PutAdministrationInvocationGrant: {
    parameters: {
      query?: never;
      header: {
        /** @description Strong administration ETag from the latest authorized organization or project administration read. */
        "If-Match": string;
      };
      path: {
        grantId: string;
        organizationId: string;
        projectId: string;
      };
      cookie?: never;
    };
    requestBody: {
      content: {
        "application/json": components["schemas"]["AdministrationInvocationGrantPutV1"];
      };
    };
    responses: {
      /** @description OK */
      200: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationReceiptV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Identity refused: no session or a refused bearer token. */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Identity refused: a CSRF refusal, or a valid bearer token without an enabled binding. */
      403: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Precondition Failed */
      412: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Precondition Required */
      428: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
    };
  };
  RevokeAdministrationInvocationGrant: {
    parameters: {
      query?: never;
      header: {
        /** @description Strong administration ETag from the latest authorized organization or project administration read. */
        "If-Match": string;
      };
      path: {
        grantId: string;
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
          "application/json": components["schemas"]["AdministrationReceiptV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Identity refused: no session or a refused bearer token. */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Identity refused: a CSRF refusal, or a valid bearer token without an enabled binding. */
      403: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Precondition Failed */
      412: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Precondition Required */
      428: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
    };
  };
  PutProjectMember: {
    parameters: {
      query?: never;
      header: {
        /** @description Strong administration ETag from the latest authorized organization or project administration read. */
        "If-Match": string;
      };
      path: {
        organizationId: string;
        principalId: string;
        projectId: string;
      };
      cookie?: never;
    };
    requestBody: {
      content: {
        "application/json": components["schemas"]["AdministrationMemberPutV1"];
      };
    };
    responses: {
      /** @description OK */
      200: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationReceiptV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Identity refused: no session or a refused bearer token. */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Identity refused: a CSRF refusal, or a valid bearer token without an enabled binding. */
      403: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Precondition Failed */
      412: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Precondition Required */
      428: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
    };
  };
  PutProjectPayloadRead: {
    parameters: {
      query?: never;
      header: {
        /** @description Strong administration ETag from the latest authorized organization or project administration read. */
        "If-Match": string;
      };
      path: {
        organizationId: string;
        principalId: string;
        projectId: string;
      };
      cookie?: never;
    };
    requestBody: {
      content: {
        "application/json": components["schemas"]["AdministrationPayloadPutV1"];
      };
    };
    responses: {
      /** @description OK */
      200: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationReceiptV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Identity refused: no session or a refused bearer token. */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Identity refused: a CSRF refusal, or a valid bearer token without an enabled binding. */
      403: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Precondition Failed */
      412: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Precondition Required */
      428: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AdministrationErrorV1"];
        };
      };
    };
  };
  GetAutomationPolicy: {
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
          "application/json": components["schemas"]["AutomationPolicyV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
  PutAutomationPolicy: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        organizationId: string;
        projectId: string;
      };
      cookie?: never;
    };
    requestBody: {
      content: {
        "application/json": components["schemas"]["AutomationPolicyV1"];
      };
    };
    responses: {
      /** @description OK */
      200: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AutomationSavedV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
  GetBuildEnvironmentCapabilities: {
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
          "application/json": components["schemas"]["BuildEnvironmentCapabilitiesV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Identity refused: no session or a refused bearer token. */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Identity refused: a CSRF refusal, or a valid bearer token without an enabled binding. */
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
  GetBuildEnvironments: {
    parameters: {
      query?: {
        /** @description Exact cursor from the preceding actor/scope-bound page. */
        afterEnvironmentId?: string;
        /** @description Canonical positive version paired with afterEnvironmentId. */
        afterVersion?: string;
        /** @description Canonical decimal page limit, default 50 and maximum 100. */
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
          "application/json": components["schemas"]["BuildEnvironmentPageV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Identity refused: no session or a refused bearer token. */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Identity refused: a CSRF refusal, or a valid bearer token without an enabled binding. */
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
  GetBuildEnvironment: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        environmentId: string;
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
          "application/json": components["schemas"]["BuildEnvironmentV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Identity refused: no session or a refused bearer token. */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Identity refused: a CSRF refusal, or a valid bearer token without an enabled binding. */
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
  PutBuildEnvironment: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        environmentId: string;
        organizationId: string;
        projectId: string;
        version: string;
      };
      cookie?: never;
    };
    requestBody: {
      content: {
        "application/vnd.weft.build-environment.v1+json": components["schemas"]["BuildEnvironmentPutV1"];
      };
    };
    responses: {
      /** @description OK */
      200: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["BuildEnvironmentReceiptV1"];
        };
      };
      /** @description Created */
      201: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["BuildEnvironmentReceiptV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Identity refused: no session or a refused bearer token. */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Identity refused: a CSRF refusal, or a valid bearer token without an enabled binding. */
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
  GetCreationCapabilities: {
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
          "application/json": components["schemas"]["CreationCapabilitiesV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CreationErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CreationErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CreationErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CreationErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CreationErrorV1"];
        };
      };
      /** @description Payload Too Large */
      413: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CreationErrorV1"];
        };
      };
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CreationErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CreationErrorV1"];
        };
      };
    };
  };
  ListCreationProposals: {
    parameters: {
      query?: {
        afterProposal?: string;
        limit?: number;
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
          "application/json": components["schemas"]["CreationProposalPageV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CreationErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CreationErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CreationErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CreationErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CreationErrorV1"];
        };
      };
      /** @description Payload Too Large */
      413: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CreationErrorV1"];
        };
      };
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CreationErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CreationErrorV1"];
        };
      };
    };
  };
  ReadCreationProposal: {
    parameters: {
      query: {
        expectedFingerprint: string;
      };
      header?: never;
      path: {
        organizationId: string;
        projectId: string;
        proposalId: string;
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
          "application/json": components["schemas"]["CreationProposalV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CreationErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CreationErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CreationErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CreationErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CreationErrorV1"];
        };
      };
      /** @description Payload Too Large */
      413: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CreationErrorV1"];
        };
      };
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CreationErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CreationErrorV1"];
        };
      };
    };
  };
  ApproveCreationProposal: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        organizationId: string;
        projectId: string;
        proposalId: string;
      };
      cookie?: never;
    };
    requestBody: {
      content: {
        "application/json": components["schemas"]["CreationApprovalV1"];
      };
    };
    responses: {
      /** @description OK */
      200: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CreationProposalV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CreationErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CreationErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CreationErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CreationErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CreationErrorV1"];
        };
      };
      /** @description Payload Too Large */
      413: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CreationErrorV1"];
        };
      };
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CreationErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CreationErrorV1"];
        };
      };
    };
  };
  PreviewCreationProposal: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        organizationId: string;
        projectId: string;
        proposalId: string;
      };
      cookie?: never;
    };
    requestBody: {
      content: {
        "application/json": components["schemas"]["CreationPreviewRequestV1"];
      };
    };
    responses: {
      /** @description OK */
      200: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CreationPreviewV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CreationErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CreationErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CreationErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CreationErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CreationErrorV1"];
        };
      };
      /** @description Payload Too Large */
      413: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CreationErrorV1"];
        };
      };
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CreationErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CreationErrorV1"];
        };
      };
    };
  };
  RecoverCreationProposal: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        organizationId: string;
        projectId: string;
        proposalId: string;
      };
      cookie?: never;
    };
    requestBody: {
      content: {
        "application/json": components["schemas"]["CreationRecoveryV1"];
      };
    };
    responses: {
      /** @description OK */
      200: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CreationProposalV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CreationErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CreationErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CreationErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CreationErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CreationErrorV1"];
        };
      };
      /** @description Payload Too Large */
      413: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CreationErrorV1"];
        };
      };
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CreationErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CreationErrorV1"];
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
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json":
            | components["schemas"]["CatalogRevisionIngestionResultV1"]
            | components["schemas"]["CatalogErrorV1"];
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
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
  GetBuildApprovals: {
    parameters: {
      query?: {
        /** @description Exact cursor from the preceding actor/scope-bound page. */
        afterApprovalId?: string;
        /** @description Canonical decimal page limit, default 50 and maximum 100. */
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
          "application/json": components["schemas"]["BuildApprovalPageV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Identity refused: no session or a refused bearer token. */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Identity refused: a CSRF refusal, or a valid bearer token without an enabled binding. */
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
  GetBuildApproval: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        approvalId: string;
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
          "application/json": components["schemas"]["BuildApprovalV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Identity refused: no session or a refused bearer token. */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Identity refused: a CSRF refusal, or a valid bearer token without an enabled binding. */
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
  PutBuildApproval: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        approvalId: string;
        mappingId: string;
        organizationId: string;
        projectId: string;
      };
      cookie?: never;
    };
    requestBody: {
      content: {
        "application/vnd.weft.build-approval.v1+json": components["schemas"]["BuildApprovalPutV1"];
      };
    };
    responses: {
      /** @description OK */
      200: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["BuildApprovalReceiptV1"];
        };
      };
      /** @description Created */
      201: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["BuildApprovalReceiptV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Identity refused: no session or a refused bearer token. */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Identity refused: a CSRF refusal, or a valid bearer token without an enabled binding. */
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
  RevokeBuildApproval: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        approvalId: string;
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
          "application/json": components["schemas"]["BuildApprovalV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Identity refused: no session or a refused bearer token. */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Identity refused: a CSRF refusal, or a valid bearer token without an enabled binding. */
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
  GetBuildCapabilities: {
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
          "application/json": components["schemas"]["BuildCapabilitiesV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Identity refused: no session or a refused bearer token. */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Identity refused: a CSRF refusal, or a valid bearer token without an enabled binding. */
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
  GetBuildRuns: {
    parameters: {
      query?: {
        /** @description Exact cursor from the preceding actor/scope-bound page. */
        afterBuildId?: string;
        /** @description Canonical decimal page limit, default 50 and maximum 100. */
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
          "application/json": components["schemas"]["BuildRunPageV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Identity refused: no session or a refused bearer token. */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Identity refused: a CSRF refusal, or a valid bearer token without an enabled binding. */
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
  GetBuildRun: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        buildId: string;
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
          "application/json": components["schemas"]["BuildRunV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Identity refused: no session or a refused bearer token. */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Identity refused: a CSRF refusal, or a valid bearer token without an enabled binding. */
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
  PutBuildRun: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        buildId: string;
        mappingId: string;
        organizationId: string;
        projectId: string;
      };
      cookie?: never;
    };
    requestBody: {
      content: {
        "application/vnd.weft.build-run.v1+json": components["schemas"]["BuildRunPutV1"];
      };
    };
    responses: {
      /** @description OK */
      200: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["BuildRunReceiptV1"];
        };
      };
      /** @description Created */
      201: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["BuildRunReceiptV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Identity refused: no session or a refused bearer token. */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Identity refused: a CSRF refusal, or a valid bearer token without an enabled binding. */
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
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
  CancelBuildRun: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        buildId: string;
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
          "application/json": components["schemas"]["BuildRunV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Identity refused: no session or a refused bearer token. */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Identity refused: a CSRF refusal, or a valid bearer token without an enabled binding. */
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
  ListMappingDrafts: {
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
          "application/json": components["schemas"]["MappingDraftListV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
          "application/json": components["schemas"]["MappingDraftResultV1"];
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
      /** @description Unprocessable Entity */
      422: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["MappingDraftResultV1"];
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
      /** @description A bounded request, replay or ingestion outcome; no native bytes are accepted from the caller. */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json":
            | components["schemas"]["CatalogErrorV1"]
            | components["schemas"]["MappingDraftResultV1"];
        };
      };
    };
  };
  CreateMappingDraft: {
    parameters: {
      query?: never;
      header: {
        /** @description One canonical nonzero lowercase UUID. Repeating the same intent recovers its original receipt. */
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
        "application/json": components["schemas"]["MappingDraftCreateV1"];
      };
    };
    responses: {
      /** @description OK */
      200: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["MappingDraftResultV1"];
        };
      };
      /** @description Created */
      201: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["MappingDraftResultV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
          "application/json": components["schemas"]["MappingDraftResultV1"];
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
      /** @description Unprocessable Entity */
      422: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["MappingDraftResultV1"];
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
      /** @description A bounded request, replay or ingestion outcome; no native bytes are accepted from the caller. */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json":
            | components["schemas"]["CatalogErrorV1"]
            | components["schemas"]["MappingDraftResultV1"];
        };
      };
    };
  };
  GetMappingDraft: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        draftId: string;
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
          "application/json": components["schemas"]["MappingDraftV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
          "application/json": components["schemas"]["MappingDraftResultV1"];
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
      /** @description Unprocessable Entity */
      422: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["MappingDraftResultV1"];
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
      /** @description A bounded request, replay or ingestion outcome; no native bytes are accepted from the caller. */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json":
            | components["schemas"]["CatalogErrorV1"]
            | components["schemas"]["MappingDraftResultV1"];
        };
      };
    };
  };
  DiscardMappingDraft: {
    parameters: {
      query?: never;
      header: {
        /** @description The draft's current strong ETag. Missing is 428; a stale version is 412. Weak tags, wildcards, lists and If-None-Match are refused. */
        "If-Match": string;
      };
      path: {
        draftId: string;
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
          "application/json": components["schemas"]["MappingDraftResultV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
          "application/json": components["schemas"]["MappingDraftResultV1"];
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
      /** @description Unprocessable Entity */
      422: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["MappingDraftResultV1"];
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
      /** @description A bounded request, replay or ingestion outcome; no native bytes are accepted from the caller. */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json":
            | components["schemas"]["CatalogErrorV1"]
            | components["schemas"]["MappingDraftResultV1"];
        };
      };
    };
  };
  GetMappingDraftBase: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        draftId: string;
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
        content?: never;
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
          "application/json": components["schemas"]["MappingDraftResultV1"];
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
      /** @description Unprocessable Entity */
      422: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["MappingDraftResultV1"];
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
      /** @description A bounded request, replay or ingestion outcome; no native bytes are accepted from the caller. */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json":
            | components["schemas"]["CatalogErrorV1"]
            | components["schemas"]["MappingDraftResultV1"];
        };
      };
    };
  };
  CheckpointMappingDraft: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        draftId: string;
        mappingId: string;
        organizationId: string;
        projectId: string;
      };
      cookie?: never;
    };
    requestBody: {
      content: {
        "application/json": components["schemas"]["MappingDraftVersionV1"];
      };
    };
    responses: {
      /** @description OK */
      200: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["MappingDraftResultV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
          "application/json": components["schemas"]["MappingDraftResultV1"];
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
      /** @description Unprocessable Entity */
      422: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["MappingDraftResultV1"];
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
      /** @description A bounded request, replay or ingestion outcome; no native bytes are accepted from the caller. */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json":
            | components["schemas"]["CatalogErrorV1"]
            | components["schemas"]["MappingDraftResultV1"];
        };
      };
    };
  };
  GetMappingDraftProjection: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        draftId: string;
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
          "application/json": components["schemas"]["ProjectionReportV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
          "application/json": components["schemas"]["MappingDraftResultV1"];
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
      /** @description Unprocessable Entity */
      422: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["MappingDraftResultV1"];
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
      /** @description A bounded request, replay or ingestion outcome; no native bytes are accepted from the caller. */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json":
            | components["schemas"]["CatalogErrorV1"]
            | components["schemas"]["MappingDraftResultV1"];
        };
      };
    };
  };
  GetMappingDraftValidation: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        draftId: string;
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
          "application/json": components["schemas"]["ValidationReportV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
          "application/json": components["schemas"]["MappingDraftResultV1"];
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
      /** @description Unprocessable Entity */
      422: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["MappingDraftResultV1"];
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
      /** @description A bounded request, replay or ingestion outcome; no native bytes are accepted from the caller. */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json":
            | components["schemas"]["CatalogErrorV1"]
            | components["schemas"]["MappingDraftResultV1"];
        };
      };
    };
  };
  CompareMappingDraft: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        draftId: string;
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
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
          "application/json": components["schemas"]["MappingDraftResultV1"];
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
      /** @description Unprocessable Entity */
      422: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["MappingDraftResultV1"];
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
      /** @description A bounded request, replay or ingestion outcome; no native bytes are accepted from the caller. */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json":
            | components["schemas"]["CatalogErrorV1"]
            | components["schemas"]["MappingDraftResultV1"];
        };
      };
    };
  };
  GetMappingDraftOperations: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        draftId: string;
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
          "application/json": components["schemas"]["EditScriptV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
          "application/json": components["schemas"]["MappingDraftResultV1"];
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
      /** @description Unprocessable Entity */
      422: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["MappingDraftResultV1"];
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
      /** @description A bounded request, replay or ingestion outcome; no native bytes are accepted from the caller. */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json":
            | components["schemas"]["CatalogErrorV1"]
            | components["schemas"]["MappingDraftResultV1"];
        };
      };
    };
  };
  PutMappingDraftOperations: {
    parameters: {
      query?: never;
      header: {
        /** @description The draft's current strong ETag. Missing is 428; a stale version is 412. Weak tags, wildcards, lists and If-None-Match are refused. */
        "If-Match": string;
      };
      path: {
        draftId: string;
        mappingId: string;
        organizationId: string;
        projectId: string;
      };
      cookie?: never;
    };
    requestBody: {
      content: {
        "application/json": components["schemas"]["EditScriptV1"];
      };
    };
    responses: {
      /** @description OK */
      200: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["MappingDraftResultV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
          "application/json": components["schemas"]["MappingDraftResultV1"];
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
      /** @description Unprocessable Entity */
      422: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["MappingDraftResultV1"];
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
      /** @description A bounded request, replay or ingestion outcome; no native bytes are accepted from the caller. */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json":
            | components["schemas"]["CatalogErrorV1"]
            | components["schemas"]["MappingDraftResultV1"];
        };
      };
    };
  };
  PreviewMappingDraft: {
    parameters: {
      query?: never;
      header: {
        /** @description One canonical nonzero lowercase UUID. Repeating the same intent recovers its original receipt. */
        "Idempotency-Key": string;
      };
      path: {
        draftId: string;
        mappingId: string;
        organizationId: string;
        projectId: string;
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
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
          "application/json": components["schemas"]["MappingDraftResultV1"];
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
      /** @description Unprocessable Entity */
      422: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["MappingDraftResultV1"];
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
      /** @description A bounded request, replay or ingestion outcome; no native bytes are accepted from the caller. */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json":
            | components["schemas"]["CatalogErrorV1"]
            | components["schemas"]["MappingDraftResultV1"];
        };
      };
    };
  };
  RebaseMappingDraft: {
    parameters: {
      query?: never;
      header: {
        /** @description The draft's current strong ETag. Missing is 428; a stale version is 412. Weak tags, wildcards, lists and If-None-Match are refused. */
        "If-Match": string;
      };
      path: {
        draftId: string;
        mappingId: string;
        organizationId: string;
        projectId: string;
      };
      cookie?: never;
    };
    requestBody: {
      content: {
        "application/json": components["schemas"]["MappingDraftRebaseV1"];
      };
    };
    responses: {
      /** @description OK */
      200: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["MappingDraftResultV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
          "application/json": components["schemas"]["MappingDraftResultV1"];
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
      /** @description Unprocessable Entity */
      422: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["MappingDraftResultV1"];
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
      /** @description A bounded request, replay or ingestion outcome; no native bytes are accepted from the caller. */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json":
            | components["schemas"]["CatalogErrorV1"]
            | components["schemas"]["MappingDraftResultV1"];
        };
      };
    };
  };
  SaveMappingDraft: {
    parameters: {
      query?: never;
      header: {
        /** @description One canonical nonzero lowercase UUID. Repeating the same intent recovers its original receipt. */
        "Idempotency-Key": string;
      };
      path: {
        draftId: string;
        mappingId: string;
        organizationId: string;
        projectId: string;
      };
      cookie?: never;
    };
    requestBody: {
      content: {
        "application/json": components["schemas"]["MappingDraftSaveV1"];
      };
    };
    responses: {
      /** @description OK */
      200: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogRevisionIngestionResultV1"];
        };
      };
      /** @description Accepted */
      202: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogRevisionIngestionResultV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description A bounded request, replay or ingestion outcome; no native bytes are accepted from the caller. */
      409: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json":
            | components["schemas"]["CatalogErrorV1"]
            | components["schemas"]["MappingDraftResultV1"]
            | components["schemas"]["CatalogRevisionIngestionResultV1"];
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
      /** @description A bounded request, replay or ingestion outcome; no native bytes are accepted from the caller. */
      413: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json":
            | components["schemas"]["CatalogErrorV1"]
            | components["schemas"]["MappingDraftResultV1"]
            | components["schemas"]["CatalogRevisionIngestionResultV1"];
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
      /** @description A bounded request, replay or ingestion outcome; no native bytes are accepted from the caller. */
      422: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json":
            | components["schemas"]["CatalogErrorV1"]
            | components["schemas"]["MappingDraftResultV1"]
            | components["schemas"]["CatalogRevisionIngestionResultV1"];
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
      /** @description A bounded request, replay or ingestion outcome; no native bytes are accepted from the caller. */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json":
            | components["schemas"]["CatalogErrorV1"]
            | components["schemas"]["MappingDraftResultV1"]
            | components["schemas"]["CatalogRevisionIngestionResultV1"];
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
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
  ReviewMfdAppend: {
    parameters: {
      query: {
        /** @description Exact canonical committed head the reviewed append must still match at publication. */
        expectedHeadRevisionId: string;
      };
      header?: {
        /** @description Absent or exactly identity; compression is not accepted. */
        "Content-Encoding"?: string;
        /** @description Required with weftSession; omitted with entraBearer. Exact configured browser origin. */
        Origin?: string;
        /** @description Required with weftSession; omitted with entraBearer. Current cookie-bound request token obtained from catalog context; no state change is admitted without it. */
        "X-XSRF-TOKEN"?: string;
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
        "application/vnd.weft.mfd-package.v1": string;
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
          "application/json": components["schemas"]["CatalogInterchangeReviewV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Always no-store; receipts and operation results are not cacheable. */
          "Cache-Control"?: "no-store";
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description Precondition Failed */
      412: {
        headers: {
          /** @description Always no-store; receipts and operation results are not cacheable. */
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
          /** @description Always no-store; receipts and operation results are not cacheable. */
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
          /** @description Always no-store; receipts and operation results are not cacheable. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
  GetReleaseTargets: {
    parameters: {
      query?: {
        /** @description Operations page or window filter. */
        caller?: string;
        /** @description Operations page or window filter. */
        limit?: string;
        /** @description Operations page or window filter. */
        offset?: string;
        /** @description Operations page or window filter. */
        outcome?: string;
        /** @description Operations page or window filter. */
        since?: string;
        /** @description Operations page or window filter. */
        until?: string;
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
          "application/json": components["schemas"]["ReleaseOperationsPageV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
  GetReleaseOperations: {
    parameters: {
      query?: {
        /** @description Operations page or window filter. */
        caller?: string;
        /** @description Operations page or window filter. */
        limit?: string;
        /** @description Operations page or window filter. */
        offset?: string;
        /** @description Operations page or window filter. */
        outcome?: string;
        /** @description Operations page or window filter. */
        since?: string;
        /** @description Operations page or window filter. */
        until?: string;
      };
      header?: never;
      path: {
        mappingId: string;
        organizationId: string;
        projectId: string;
        section: string;
        targetId: string;
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
          "application/json": components["schemas"]["ReleaseOperationsPageV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
  ApplyReleaseOperation: {
    parameters: {
      query?: never;
      header: {
        /** @description Quoted target version. */
        "If-Match": string;
      };
      path: {
        mappingId: string;
        operationId: string;
        organizationId: string;
        projectId: string;
        targetId: string;
      };
      cookie?: never;
    };
    requestBody: {
      content: {
        "application/json": components["schemas"]["ReleaseOperationV1"];
      };
    };
    responses: {
      /** @description OK */
      200: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ReleaseOperationReceiptV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description Request Timeout */
      408: {
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
      /** @description Precondition Required */
      428: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
  GetReleaseSummary: {
    parameters: {
      query?: {
        /** @description Operations page or window filter. */
        caller?: string;
        /** @description Operations page or window filter. */
        limit?: string;
        /** @description Operations page or window filter. */
        offset?: string;
        /** @description Operations page or window filter. */
        outcome?: string;
        /** @description Operations page or window filter. */
        since?: string;
        /** @description Operations page or window filter. */
        until?: string;
      };
      header?: never;
      path: {
        mappingId: string;
        organizationId: string;
        projectId: string;
        targetId: string;
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
          "application/json": components["schemas"]["ReleaseOperationsSummaryV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
      query?: {
        /** @description Return cases after this case: the nextCaseId of the previous page. */
        afterCaseId?: string;
        /** @description Page size: a canonical decimal from 1 to 100, 50 when absent. */
        limit?: number;
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
          "application/json": components["schemas"]["RegressionCasePageV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
      query?: {
        /** @description Return runs after this run: the nextRunId of the previous page. */
        afterRunId?: string;
        /** @description Page size: a canonical decimal from 1 to 100, 50 when absent. */
        limit?: number;
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
          "application/json": components["schemas"]["RegressionRunPageV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json":
            | components["schemas"]["CatalogRevisionIngestionResultV1"]
            | components["schemas"]["CatalogErrorV1"];
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
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatAvailabilityV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Payload Too Large */
      413: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unsupported Media Type */
      415: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Too Many Requests */
      429: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Internal Server Error */
      500: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          "Cache-Control"?: "no-store";
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
      query?: {
        /** @description The number of most recent conversations to return. A canonical decimal from 1 to 100, 50 when absent. */
        limit?: number;
      };
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
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatConversationPageV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Payload Too Large */
      413: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unsupported Media Type */
      415: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Too Many Requests */
      429: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Internal Server Error */
      500: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          "Cache-Control"?: "no-store";
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
      header?: {
        /** @description Required with weftSession; omitted with entraBearer. The exact configured origin for this browser request. */
        Origin?: string;
        /** @description Required with weftSession; omitted with entraBearer. The antiforgery request token issued by the catalog context; session tokens bind to the session epoch. */
        "X-XSRF-TOKEN"?: string;
      };
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
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatConversationV1"];
        };
      };
      /** @description Created */
      201: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatConversationV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Payload Too Large */
      413: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unsupported Media Type */
      415: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Too Many Requests */
      429: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Internal Server Error */
      500: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          "Cache-Control"?: "no-store";
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
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatConversationDetailV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Payload Too Large */
      413: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unsupported Media Type */
      415: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Too Many Requests */
      429: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Internal Server Error */
      500: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          "Cache-Control"?: "no-store";
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
      header?: {
        /** @description Required with weftSession; omitted with entraBearer. The exact configured origin for this browser request. */
        Origin?: string;
        /** @description Required with weftSession; omitted with entraBearer. The antiforgery request token issued by the catalog context; session tokens bind to the session epoch. */
        "X-XSRF-TOKEN"?: string;
      };
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
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatTurnV1"];
        };
      };
      /** @description Created */
      201: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatTurnV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Payload Too Large */
      413: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unsupported Media Type */
      415: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Too Many Requests */
      429: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Internal Server Error */
      500: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          "Cache-Control"?: "no-store";
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
      header?: {
        /** @description Required with weftSession; omitted with entraBearer. The exact configured origin for this browser request. */
        Origin?: string;
        /** @description Required with weftSession; omitted with entraBearer. The antiforgery request token issued by the catalog context; session tokens bind to the session epoch. */
        "X-XSRF-TOKEN"?: string;
      };
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
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatTurnV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Payload Too Large */
      413: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unsupported Media Type */
      415: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Too Many Requests */
      429: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Internal Server Error */
      500: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          "Cache-Control"?: "no-store";
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
      query?: {
        /** @description Stream only events with a greater sequence number. A canonical decimal from 0 to 4096, 0 when absent. */
        after?: number;
      };
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
          "Cache-Control"?: "no-store";
          "X-Accel-Buffering"?: "no";
          [name: string]: unknown;
        };
        content: {
          "text/event-stream": components["schemas"]["ChatTurnEventV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Payload Too Large */
      413: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Unsupported Media Type */
      415: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Too Many Requests */
      429: {
        headers: {
          "Cache-Control"?: "no-store";
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Internal Server Error */
      500: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["ChatErrorV1"];
        };
      };
    };
  };
  DownloadMfdExport: {
    parameters: {
      query?: never;
      header?: {
        /** @description Required with weftSession; omitted with entraBearer. Exact configured browser origin. */
        Origin?: string;
        /** @description Required with weftSession; omitted with entraBearer. Current cookie-bound request token obtained from catalog context; no state change is admitted without it. */
        "X-XSRF-TOKEN"?: string;
      };
      path: {
        mappingId: string;
        organizationId: string;
        projectId: string;
        reviewId: string;
        revisionId: string;
      };
      cookie?: never;
    };
    requestBody: {
      content: {
        "application/json": {
          acknowledgeWarnings: boolean;
          reviewDigest: string;
        };
      };
    };
    responses: {
      /** @description OK */
      200: {
        headers: {
          /** @description Always no-store; receipts and operation results are not cacheable. */
          "Cache-Control"?: "no-store";
          /** @description Attachment named mapping.mfd or mapping.zip. */
          "Content-Disposition"?: string;
          /** @description SHA-256 of the exact download bytes. */
          "X-Content-Sha256"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/xml": string;
          "application/zip": string;
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Always no-store; receipts and operation results are not cacheable. */
          "Cache-Control"?: "no-store";
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description Precondition Failed */
      412: {
        headers: {
          /** @description Always no-store; receipts and operation results are not cacheable. */
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
          /** @description Always no-store; receipts and operation results are not cacheable. */
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
          /** @description Always no-store; receipts and operation results are not cacheable. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
  ReviewMfdExport: {
    parameters: {
      query?: never;
      header?: {
        /** @description Required with weftSession; omitted with entraBearer. Exact configured browser origin. */
        Origin?: string;
        /** @description Required with weftSession; omitted with entraBearer. Current cookie-bound request token obtained from catalog context; no state change is admitted without it. */
        "X-XSRF-TOKEN"?: string;
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
          /** @description Always no-store; receipts and operation results are not cacheable. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogInterchangeReviewV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Always no-store; receipts and operation results are not cacheable. */
          "Cache-Control"?: "no-store";
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description Precondition Failed */
      412: {
        headers: {
          /** @description Always no-store; receipts and operation results are not cacheable. */
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
          /** @description Always no-store; receipts and operation results are not cacheable. */
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
          /** @description Always no-store; receipts and operation results are not cacheable. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
  SubmitCatalogPreview: {
    parameters: {
      query?: never;
      header: {
        /** @description Exactly one nonzero canonical lowercase D-format UUID with only surrounding SP/HTAB allowed; If-Match and If-None-Match are rejected. The same key with the same submission replays the original run; with a different submission it is a 409 idempotency conflict. */
        "Idempotency-Key": string;
      };
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
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description Unprocessable Entity */
      422: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogXmlPreviewProblemV1"];
        };
      };
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
      header?: {
        /** @description Absent or a single identity token; no other coding or request body is accepted. */
        "Content-Encoding"?: string;
        /** @description Required with weftSession; omitted with entraBearer. One exact configured canonical browser origin. */
        Origin?: string;
        /** @description Required with weftSession; omitted with entraBearer. Framework cookie-bound token issued by an admitted catalog context. */
        "X-XSRF-TOKEN"?: string;
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
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
      header?: {
        /** @description Absent or a single identity token; no other coding or request body is accepted. */
        "Content-Encoding"?: string;
        /** @description Required with weftSession; omitted with entraBearer. One exact configured canonical browser origin. */
        Origin?: string;
        /** @description Required with weftSession; omitted with entraBearer. Framework cookie-bound token issued by an admitted catalog context. */
        "X-XSRF-TOKEN"?: string;
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
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
  RestoreMappingRevision: {
    parameters: {
      query?: never;
      header: {
        /** @description One canonical nonzero lowercase UUID. Repeating the same intent recovers its original receipt. */
        "Idempotency-Key": string;
      };
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
        "application/json": components["schemas"]["MappingRevisionRestoreV1"];
      };
    };
    responses: {
      /** @description OK */
      200: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogRevisionIngestionResultV1"];
        };
      };
      /** @description Accepted */
      202: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogRevisionIngestionResultV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description A bounded request, replay or ingestion outcome; no native bytes are accepted from the caller. */
      409: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json":
            | components["schemas"]["CatalogErrorV1"]
            | components["schemas"]["MappingDraftResultV1"]
            | components["schemas"]["CatalogRevisionIngestionResultV1"];
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
      /** @description A bounded request, replay or ingestion outcome; no native bytes are accepted from the caller. */
      413: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json":
            | components["schemas"]["CatalogErrorV1"]
            | components["schemas"]["MappingDraftResultV1"]
            | components["schemas"]["CatalogRevisionIngestionResultV1"];
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
      /** @description A bounded request, replay or ingestion outcome; no native bytes are accepted from the caller. */
      422: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json":
            | components["schemas"]["CatalogErrorV1"]
            | components["schemas"]["MappingDraftResultV1"]
            | components["schemas"]["CatalogRevisionIngestionResultV1"];
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
      /** @description A bounded request, replay or ingestion outcome; no native bytes are accepted from the caller. */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json":
            | components["schemas"]["CatalogErrorV1"]
            | components["schemas"]["MappingDraftResultV1"]
            | components["schemas"]["CatalogRevisionIngestionResultV1"];
        };
      };
    };
  };
  GetLocalSandbox: {
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
          "application/json": components["schemas"]["LocalSandboxStateV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
  GetLocalSandboxMessages: {
    parameters: {
      query?: {
        /** @description Canonical decimal page limit, default 20 and maximum 50. */
        limit?: string;
      };
      header?: never;
      path: {
        mappingId: string;
        organizationId: string;
        projectId: string;
        targetId: string;
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
          "application/json": components["schemas"]["LocalSandboxMessagePageV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
  GetLocalSandboxMessage: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        mappingId: string;
        messageId: string;
        organizationId: string;
        projectId: string;
        targetId: string;
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
          "application/json": components["schemas"]["LocalSandboxMessageV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
  SendLocalSandboxMessage: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        mappingId: string;
        messageId: string;
        organizationId: string;
        projectId: string;
        targetId: string;
      };
      cookie?: never;
    };
    requestBody: {
      content: {
        "application/json": components["schemas"]["LocalSandboxSendV1"];
      };
    };
    responses: {
      /** @description OK */
      200: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["LocalSandboxMessageV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description Request Timeout */
      408: {
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
  DeployLocalSandbox: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        deploymentId: string;
        mappingId: string;
        organizationId: string;
        projectId: string;
      };
      cookie?: never;
    };
    requestBody: {
      content: {
        "application/json": components["schemas"]["LocalSandboxDeployV1"];
      };
    };
    responses: {
      /** @description OK */
      200: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["LocalSandboxDeploymentV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description Request Timeout */
      408: {
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
  GetMappingAutomation: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        organizationId: string;
        projectId: string;
        /** @description Canonical mapping, revision or notification identifier for this operation. */
        resourceId: string;
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
          "application/json": components["schemas"]["MappingAutomationV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
  PutMappingAutomation: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        organizationId: string;
        projectId: string;
        /** @description Canonical mapping, revision or notification identifier for this operation. */
        resourceId: string;
      };
      cookie?: never;
    };
    requestBody: {
      content: {
        "application/json": components["schemas"]["MappingAutomationV1"];
      };
    };
    responses: {
      /** @description OK */
      200: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AutomationSavedV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
  GetRegressionSuites: {
    parameters: {
      query?: {
        /** @description Continue after the previous page's ID. */
        afterId?: string;
        /** @description Page size: a canonical decimal from 1 to 100, 50 when absent. */
        limit?: number;
      };
      header?: never;
      path: {
        organizationId: string;
        projectId: string;
        /** @description Canonical mapping, revision or notification identifier for this operation. */
        resourceId: string;
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
          "application/json": components["schemas"]["RegressionSuitesV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
  GetMfdImportReview: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        organizationId: string;
        projectId: string;
        reviewId: string;
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
          "application/json": components["schemas"]["CatalogInterchangeReviewV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Always no-store; receipts and operation results are not cacheable. */
          "Cache-Control"?: "no-store";
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
  ConfirmMfdImport: {
    parameters: {
      query?: never;
      header: {
        /** @description Canonical reviewId UUID; one value. */
        "Idempotency-Key": string;
        /** @description Required with weftSession; omitted with entraBearer. Exact configured browser origin. */
        Origin?: string;
        /** @description Required with weftSession; omitted with entraBearer. Current cookie-bound request token obtained from catalog context; no state change is admitted without it. */
        "X-XSRF-TOKEN"?: string;
      };
      path: {
        organizationId: string;
        projectId: string;
        reviewId: string;
      };
      cookie?: never;
    };
    requestBody: {
      content: {
        "application/json": {
          acknowledgeWarnings: boolean;
          reviewDigest: string;
        };
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
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description Precondition Failed */
      412: {
        headers: {
          /** @description Always no-store; receipts and operation results are not cacheable. */
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json":
            | components["schemas"]["CatalogRevisionIngestionResultV1"]
            | components["schemas"]["CatalogErrorV1"];
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
  ReviewMfdCreate: {
    parameters: {
      query: {
        /** @description Exact registered backend ID; at most 128 UTF-8 bytes. */
        backendId: string;
        /** @description Description, at most 4000 Unicode scalars; default empty. */
        description?: string;
        /** @description Mapping name, at most 200 Unicode scalars. */
        name: string;
      };
      header?: {
        /** @description Absent or exactly identity; compression is not accepted. */
        "Content-Encoding"?: string;
        /** @description Required with weftSession; omitted with entraBearer. Exact configured browser origin. */
        Origin?: string;
        /** @description Required with weftSession; omitted with entraBearer. Current cookie-bound request token obtained from catalog context; no state change is admitted without it. */
        "X-XSRF-TOKEN"?: string;
      };
      path: {
        organizationId: string;
        projectId: string;
      };
      cookie?: never;
    };
    requestBody: {
      content: {
        "application/vnd.weft.mfd-package.v1": string;
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
          "application/json": components["schemas"]["CatalogInterchangeReviewV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Always no-store; receipts and operation results are not cacheable. */
          "Cache-Control"?: "no-store";
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description Precondition Failed */
      412: {
        headers: {
          /** @description Always no-store; receipts and operation results are not cacheable. */
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
          /** @description Always no-store; receipts and operation results are not cacheable. */
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
          /** @description Always no-store; receipts and operation results are not cacheable. */
          "Cache-Control"?: "no-store";
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
  GetNotificationDeliveries: {
    parameters: {
      query?: {
        /** @description Continue after the previous page's ID. */
        afterId?: string;
        /** @description Page size: a canonical decimal from 1 to 100, 50 when absent. */
        limit?: number;
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
          "application/json": components["schemas"]["NotificationDeliveriesV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
  GetNotificationDestinations: {
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
          "application/json": components["schemas"]["NotificationDestinationsV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
  EnableNotificationDestination: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        organizationId: string;
        projectId: string;
      };
      cookie?: never;
    };
    requestBody: {
      content: {
        "application/json": components["schemas"]["NotificationDestinationEnableV1"];
      };
    };
    responses: {
      /** @description OK */
      200: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AutomationSavedV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
  GetNotificationOptions: {
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
          "application/json": components["schemas"]["NotificationOptionsV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
  GetNotificationSubscription: {
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
          "application/json": components["schemas"]["NotificationSubscriptionV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
  PutNotificationSubscription: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        organizationId: string;
        projectId: string;
      };
      cookie?: never;
    };
    requestBody: {
      content: {
        "application/json": components["schemas"]["NotificationSubscriptionV1"];
      };
    };
    responses: {
      /** @description OK */
      200: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AutomationSavedV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
  GetNotifications: {
    parameters: {
      query?: {
        /** @description Continue after the previous page's ID. */
        afterId?: string;
        /** @description Page size: a canonical decimal from 1 to 100, 50 when absent. */
        limit?: number;
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
          "application/json": components["schemas"]["NotificationsV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
  ReadNotification: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        organizationId: string;
        projectId: string;
        /** @description Canonical mapping, revision or notification identifier for this operation. */
        resourceId: string;
      };
      cookie?: never;
    };
    requestBody: {
      content: {
        "application/json": components["schemas"]["NotificationReadV1"];
      };
    };
    responses: {
      /** @description OK */
      200: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AutomationSavedV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
  GetRegressionSuite: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        organizationId: string;
        projectId: string;
        /** @description Canonical mapping, revision or notification identifier for this operation. */
        resourceId: string;
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
          "application/json": components["schemas"]["RegressionSuiteV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
  PutRegressionRecheck: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        organizationId: string;
        projectId: string;
        /** @description Canonical mapping, revision or notification identifier for this operation. */
        resourceId: string;
      };
      cookie?: never;
    };
    requestBody: {
      content: {
        "application/json": components["schemas"]["RegressionRecheckV1"];
      };
    };
    responses: {
      /** @description OK */
      200: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["AutomationSavedV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
  GetRegressionEvaluations: {
    parameters: {
      query?: {
        /** @description Continue after the previous page's ID. */
        afterId?: string;
        /** @description Page size: a canonical decimal from 1 to 100, 50 when absent. */
        limit?: number;
      };
      header?: never;
      path: {
        organizationId: string;
        projectId: string;
        /** @description Canonical mapping, revision or notification identifier for this operation. */
        resourceId: string;
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
          "application/json": components["schemas"]["RegressionEvaluationsV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json":
            | components["schemas"]["CatalogRevisionIngestionResultV1"]
            | components["schemas"]["CatalogErrorV1"];
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
  GetSpecBundleInventory: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        bundleId: string;
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
          /** @description Positive mutation CAS version, also in the response body. Not a representation validator: publication acknowledgements can change inventory without changing this version. No ETag is issued. */
          "X-Weft-Spec-Version"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecInventoryV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description Payload Too Large */
      413: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
    };
  };
  CreateSpecBundle: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        bundleId: string;
        organizationId: string;
        projectId: string;
      };
      cookie?: never;
    };
    requestBody: {
      content: {
        "application/json": components["schemas"]["SpecCreateV1"];
      };
    };
    responses: {
      /** @description OK */
      200: {
        headers: {
          /** @description Positive mutation CAS version, also in the response body. Not a representation validator: publication acknowledgements can change inventory without changing this version. No ETag is issued. */
          "X-Weft-Spec-Version"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecStatusV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description Payload Too Large */
      413: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
    };
  };
  TombstoneSpecBundle: {
    parameters: {
      query?: never;
      header: {
        /** @description Stable nonzero UUID for exact tombstone replay. Logical deletion only; retirement is asynchronous maintenance. */
        "Idempotency-Key": string;
        /** @description One quoted positive bundle version, for example "1". Required; stale CAS answers 409. Original reservation replay retains its original version. */
        "If-Match": string;
      };
      path: {
        bundleId: string;
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
          /** @description Positive mutation CAS version, also in the response body. Not a representation validator: publication acknowledgements can change inventory without changing this version. No ETag is issued. */
          "X-Weft-Spec-Version"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecStatusV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description Payload Too Large */
      413: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
    };
  };
  GetSpecIntakeCapabilities: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        bundleId: string;
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
          "application/json": components["schemas"]["SpecIntakeCapabilitiesV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description Payload Too Large */
      413: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
    };
  };
  ReadSpecExtraction: {
    parameters: {
      query?: never;
      header?: never;
      path: {
        bundleId: string;
        objectId: string;
        organizationId: string;
        projectId: string;
      };
      cookie?: never;
    };
    requestBody?: never;
    responses: {
      /** @description Exact canonical evidence JSON array: format marker, fixed outcome code, then chunks. Legacy v1 chunks are [ordinal,text,nullable JSON pointer,byte offset,byte length,start line,end line]. Structural v2 Office chunks are [ordinal,text,locator,formulaOrNull]; PDF chunks add missingText as a fifth field. PDF locators are [pdf-page,oneBasedPage]. Partial retains incomplete structural/page text; needs-OCR and encrypted are Unsupported refusals, with needs-OCR retaining blank page facts. Limit failures contain no chunks. */
      200: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": (
            | string
            | (
                | null
                | boolean
                | number
                | string
                | (null | boolean | number | string)[]
              )[]
          )[];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description Payload Too Large */
      413: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
    };
  };
  TombstoneSpecSource: {
    parameters: {
      query?: never;
      header: {
        /** @description Stable nonzero UUID for exact tombstone replay. Logical deletion only; retirement is asynchronous maintenance. */
        "Idempotency-Key": string;
        /** @description One quoted positive bundle version, for example "1". Required; stale CAS answers 409. Original reservation replay retains its original version. */
        "If-Match": string;
      };
      path: {
        bundleId: string;
        organizationId: string;
        projectId: string;
        sourceId: string;
        sourceVersion: string;
      };
      cookie?: never;
    };
    requestBody?: never;
    responses: {
      /** @description OK */
      200: {
        headers: {
          /** @description Positive mutation CAS version, also in the response body. Not a representation validator: publication acknowledgements can change inventory without changing this version. No ETag is issued. */
          "X-Weft-Spec-Version"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecStatusV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description Payload Too Large */
      413: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
    };
  };
  PasteSpecText: {
    parameters: {
      query?: never;
      header: {
        /** @description One quoted positive bundle version, for example "1". Required; stale CAS answers 409. Original reservation replay retains its original version. */
        "If-Match": string;
      };
      path: {
        bundleId: string;
        organizationId: string;
        projectId: string;
      };
      cookie?: never;
    };
    requestBody: {
      content: {
        "application/json": components["schemas"]["SpecTextV1"];
      };
    };
    responses: {
      /** @description OK */
      200: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecIntakeV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description Payload Too Large */
      413: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
    };
  };
  UploadSpecSource: {
    parameters: {
      query?: never;
      header: {
        /** @description One quoted positive bundle version, for example "1". Required; stale CAS answers 409. Original reservation replay retains its original version. */
        "If-Match": string;
        /** @description Strict JSON SpecUploadV1: sourceId, sourceVersion, originalObjectId, uploadKey, extractionObjectId, extractionKey, extractionExpectedVersion, format. All fields required; IDs nonzero and publication IDs/keys distinct. Format: text, json, openapi-json, docx, xlsx, pdf; use GetSpecIntakeCapabilities for currently admitted formats, extractor versions and byte caps. Structural formats require qualified workers and schema 46; unavailable formats return 503 DocumentWorkerUnavailable before retention. Raw body at most 10 MiB (PDF at most 8 MiB). The extraction CAS normally starts at If-Match+1; after a conflict before its reservation, refresh inventory and update only extractionExpectedVersion. Document refusals retain the original; process failures are 503, not document outcomes. */
        "X-Weft-Spec-Upload": string;
      };
      path: {
        bundleId: string;
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
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecIntakeV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description Forbidden */
      403: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description Not Found */
      404: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description Conflict */
      409: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description Payload Too Large */
      413: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
      /** @description Service Unavailable */
      503: {
        headers: {
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["SpecErrorV1"];
        };
      };
    };
  };
  SearchCatalog: {
    parameters: {
      query: {
        cursor?: string;
        /** @description Canonical decimal page size, 1–100; default 25. */
        limit?: string;
        /** @description Trimmed literal text containing 1–256 UTF-8 bytes without controls. Unicode is preserved; %, _, quotes and backslashes are literal. */
        q: string;
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
          "application/json": components["schemas"]["CatalogSearchPageV1"];
        };
      };
      /** @description Bad Request */
      400: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
      /** @description Unauthorized */
      401: {
        headers: {
          /** @description Sent only for a bearer credential refusal; session refusals and disabled bearer access send no challenge. */
          "WWW-Authenticate"?: string;
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
      /** @description This principal's bearer request rate is exceeded; retry after Retry-After seconds. */
      429: {
        headers: {
          /** @description Sent with the rateLimited code: whole seconds until this principal may send another bearer request. */
          "Retry-After"?: number;
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
          /** @description Two seconds when retained report indexing is incomplete or busy. */
          "Retry-After"?: string;
          [name: string]: unknown;
        };
        content: {
          "application/json": components["schemas"]["CatalogErrorV1"];
        };
      };
    };
  };
}
