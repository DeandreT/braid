using Weft.Contracts;
using Weft.Contracts.Automation;
using Weft.Contracts.Builds;
using Weft.Contracts.Catalog;
using Weft.Contracts.Chat;
using Weft.Contracts.Comparisons;
using Weft.Contracts.Editing;
using Weft.Contracts.Help;
using Weft.Contracts.Operations;
using Weft.Contracts.Regression;

namespace Weft.Cli.Http;

/// <summary>
/// One documented Weft API operation exactly as the CLI sends it: the only query parameters and headers it may set,
/// its request media and body contract, and the success statuses it accepts with their response contracts.
/// The contract drift test compares every entry with <c>contracts/weft-v1.json</c>. Reads and idempotent operations
/// may be repeated; conditional administration edits explicitly disable retries after uncertain failures.
/// </summary>
internal sealed record ApiOperation(
    string OperationId,
    string Method,
    string Path,
    IReadOnlyList<string> Query,
    IReadOnlyList<string> Headers,
    string? RequestMediaType,
    Type? RequestBody,
    IReadOnlyDictionary<int, Type?> Success,
    Type ErrorBody,
    string ResponseMediaType = "application/json",
    IReadOnlyList<string>? AlternateResponseMediaTypes = null,
    bool RequiresAuthentication = true,
    bool RetryOnFailure = true,
    IReadOnlyDictionary<int, Type>? AlternateErrorBodies = null);

internal static partial class ApiOperations
{
    private static readonly string[] None = [];
    private static readonly string[] Page = ["limit", "cursor"];
    private static readonly string[] Key = ["Idempotency-Key"];
    public static readonly ApiOperation GetAutomationPolicy = Get("GetAutomationPolicy", Project + "/automation", typeof(AutomationPolicyV1));
    public static readonly ApiOperation PutAutomationPolicy = Send("PutAutomationPolicy", "PUT", Project + "/automation", "application/json", typeof(AutomationPolicyV1), Ok(typeof(AutomationSavedV1)));
    public static readonly ApiOperation GetMappingAutomation = Get("GetMappingAutomation", Project + "/mappings/{resourceId}/automation", typeof(MappingAutomationV1));
    public static readonly ApiOperation PutMappingAutomation = Send("PutMappingAutomation", "PUT", Project + "/mappings/{resourceId}/automation", "application/json", typeof(MappingAutomationV1), Ok(typeof(AutomationSavedV1)));
    public static readonly ApiOperation GetRegressionSuites = Get("GetRegressionSuites", Project + "/mappings/{resourceId}/regression-suites", typeof(RegressionSuitesV1), ["limit", "afterId"]);
    public static readonly ApiOperation GetRegressionSuite = Get("GetRegressionSuite", Project + "/regression-suites/{resourceId}", typeof(RegressionSuiteV1));
    public static readonly ApiOperation PutRegressionRecheck = Send("PutRegressionRecheck", "PUT", Project + "/regression-suites/{resourceId}", "application/json", typeof(RegressionRecheckV1), Ok(typeof(AutomationSavedV1)));
    public static readonly ApiOperation GetRegressionEvaluations = Get("GetRegressionEvaluations", Project + "/regression-suites/{resourceId}/cases", typeof(RegressionEvaluationsV1), ["limit", "afterId"]);
    public static readonly ApiOperation GetNotificationSubscription = Get("GetNotificationSubscription", Project + "/notification-subscription", typeof(NotificationSubscriptionV1));
    public static readonly ApiOperation PutNotificationSubscription = Send("PutNotificationSubscription", "PUT", Project + "/notification-subscription", "application/json", typeof(NotificationSubscriptionV1), Ok(typeof(AutomationSavedV1)));
    public static readonly ApiOperation GetNotifications = Get("GetNotifications", Project + "/notifications", typeof(NotificationsV1), ["limit", "afterId"]);
    public static readonly ApiOperation GetNotificationInbox = Get("GetNotificationInbox", Organization + "/notifications", typeof(NotificationsV1), ["limit", "afterId"]);
    public static readonly ApiOperation ReadInboxNotification = Send("ReadInboxNotification", "PUT", Organization + "/notifications/{notificationId}/read", "application/json", typeof(NotificationReadV1), Ok(typeof(AutomationSavedV1)));
    public static readonly ApiOperation ReadNotification = Send("ReadNotification", "PUT", Project + "/notifications/{resourceId}/read", "application/json", typeof(NotificationReadV1), Ok(typeof(AutomationSavedV1)));
    public static readonly ApiOperation GetNotificationOptions = Get("GetNotificationOptions", Project + "/notification-options", typeof(NotificationOptionsV1));
    public static readonly ApiOperation GetNotificationDeliveries = Get("GetNotificationDeliveries", Project + "/notification-deliveries", typeof(NotificationDeliveriesV1), ["limit", "afterId"]);
    public static readonly ApiOperation GetNotificationDestinations = Get("GetNotificationDestinations", Project + "/notification-destinations", typeof(NotificationDestinationsV1));
    public static readonly ApiOperation EnableNotificationDestination = Send("EnableNotificationDestination", "PUT", Project + "/notification-destinations/enable", "application/json", typeof(NotificationDestinationEnableV1), Ok(typeof(AutomationSavedV1)));
    private const string Organization = "/api/v1/organizations/{organizationId}";
    private const string Project = Organization + "/projects/{projectId}";
    private const string Mapping = Project + "/mappings/{mappingId}";
    private const string Revision = Mapping + "/revisions/{revisionId}";
    private const string Chat = Revision + "/chat";
    private const string Drafts = Mapping + "/drafts";
    private const string Draft = Drafts + "/{draftId}";

    private static readonly string[] OperationsQuery = ["offset", "limit", "since", "until", "caller", "outcome"];
    public static readonly ApiOperation GetReleaseTargets = Get("GetReleaseTargets", Mapping + "/operations", typeof(ReleaseOperationsPageV1), OperationsQuery);
    public static readonly ApiOperation GetReleaseOperations = Get("GetReleaseOperations", Mapping + "/operations/{targetId}/{section}", typeof(ReleaseOperationsPageV1), OperationsQuery);
    public static readonly ApiOperation GetReleaseSummary = Get("GetReleaseSummary", Mapping + "/operations/{targetId}/summary", typeof(ReleaseOperationsSummaryV1), OperationsQuery);
    public static readonly ApiOperation ApplyReleaseOperation = Send("ApplyReleaseOperation", "POST", Mapping + "/operations/{targetId}/actions/{operationId}",
        "application/json", typeof(ReleaseOperationV1), Ok(typeof(ReleaseOperationReceiptV1)), headers: ["If-Match"]);

    public static readonly ApiOperation GetHealth = new("GetHealth", "GET", "/api/v1/health", None, None,
        null, null, Ok(typeof(HealthResponseV1)), typeof(HealthResponseV1), RequiresAuthentication: false);
    public static readonly ApiOperation GetReadiness = new("GetReadiness", "GET", "/api/v1/health/ready", None, None,
        null, null, Ok(typeof(HealthResponseV1)), typeof(HealthResponseV1), RequiresAuthentication: false);

    public static readonly ApiOperation GetCatalogContext = Get("GetCatalogContext", "/api/v1/catalog/context", typeof(CatalogContextV1));
    public static readonly ApiOperation SearchCatalog = Get("SearchCatalog", Organization + "/search", typeof(CatalogSearchPageV1), ["q", "limit", "cursor"]);
    public static readonly ApiOperation GetHelpSections = Get("GetHelpSections", "/api/v1/help/sections", typeof(HelpSectionListV1));
    public static readonly ApiOperation GetHelpSection = Get("GetHelpSection", "/api/v1/help/sections/{sectionId}", typeof(HelpSectionV1));
    public static readonly ApiOperation GetCatalogProjects = Get("GetCatalogProjects", Organization + "/projects", typeof(CatalogProjectPageV1), Page);
    public static readonly ApiOperation CreateCatalogProject = Send("CreateCatalogProject", "POST", Organization + "/projects",
        "application/vnd.weft.project-create.v1+json", typeof(CatalogProjectCreateV1), Created(typeof(CatalogProjectCreationResultV1)), headers: Key);
    public static readonly ApiOperation GetCatalogProject = Get("GetCatalogProject", Project, typeof(CatalogProjectV1));
    public static readonly ApiOperation GetCatalogMappings = Get("GetCatalogMappings", Project + "/mappings", typeof(CatalogMappingPageV1), Page);
    public static readonly ApiOperation CreateCatalogMapping = Send("CreateCatalogMapping", "POST", Project + "/mappings",
        "application/octet-stream", null, Accepted(typeof(CatalogRevisionIngestionResultV1)),
        query: ["name", "backendId", "description", "originalFilename"], headers: Key);
    public static readonly ApiOperation GetCatalogMapping = Get("GetCatalogMapping", Mapping, typeof(CatalogMappingV1));
    public static readonly ApiOperation GetCatalogMappingMetadata = Get("GetCatalogMappingMetadata", Mapping + "/metadata",
        typeof(CatalogMappingMetadataV1));
    public static readonly ApiOperation EditCatalogMappingMetadata = Send("EditCatalogMappingMetadata", "PATCH", Mapping + "/metadata",
        "application/vnd.weft.mapping-metadata-edit.v1+json", typeof(CatalogMappingMetadataEditV1),
        Ok(typeof(CatalogMappingMetadataV1)), headers: ["If-Match"]);
    public static readonly ApiOperation GetCatalogRevisions = Get("GetCatalogRevisions", Mapping + "/revisions", typeof(CatalogRevisionPageV1), Page);
    public static readonly ApiOperation AppendCatalogRevision = Send("AppendCatalogRevision", "POST", Mapping + "/revisions",
        "application/octet-stream", null, Accepted(typeof(CatalogRevisionIngestionResultV1)),
        query: ["head", "expectedHeadRevisionId", "originalFilename"], headers: Key);
    public static readonly ApiOperation GetCatalogRevision = Get("GetCatalogRevision", Revision, typeof(CatalogRevisionV1));
    public static readonly ApiOperation CreateMappingDraft = Send("CreateMappingDraft", "POST", Drafts, "application/json", typeof(MappingDraftCreateV1), Created(typeof(MappingDraftResultV1)), headers: Key);
    public static readonly ApiOperation ListMappingDrafts = Get("ListMappingDrafts", Drafts, typeof(MappingDraftListV1));
    public static readonly ApiOperation GetMappingDraft = Get("GetMappingDraft", Draft, typeof(MappingDraftV1));
    public static readonly ApiOperation GetMappingDraftOperations = Get("GetMappingDraftOperations", Draft + "/operations", typeof(EditScriptV1));
    public static readonly ApiOperation PutMappingDraftOperations = Send("PutMappingDraftOperations", "PUT", Draft + "/operations", "application/json", typeof(EditScriptV1), Ok(typeof(MappingDraftResultV1)), headers: ["If-Match"]);
    public static readonly ApiOperation CheckpointMappingDraft = Send("CheckpointMappingDraft", "POST", Draft + "/checkpoints", "application/json", typeof(MappingDraftVersionV1), Ok(typeof(MappingDraftResultV1)));
    public static readonly ApiOperation CompareMappingDraft = Get("CompareMappingDraft", Draft + "/comparison", typeof(RevisionComparisonV1));
    public static readonly ApiOperation PreviewMappingDraft = Send("PreviewMappingDraft", "POST", Draft + "/previews", "application/json", typeof(CatalogPreviewSubmissionV1), Created(typeof(CatalogPreviewSubmitResultV1)), headers: Key);
    public static readonly ApiOperation SaveMappingDraft = Send("SaveMappingDraft", "POST", Draft + "/save", "application/json", typeof(MappingDraftSaveV1), Accepted(typeof(CatalogRevisionIngestionResultV1)), headers: Key);
    public static readonly ApiOperation RebaseMappingDraft = Send("RebaseMappingDraft", "POST", Draft + "/rebase", "application/json", typeof(MappingDraftRebaseV1), Ok(typeof(MappingDraftResultV1)), headers: ["If-Match"]);
    public static readonly ApiOperation DiscardMappingDraft = Send("DiscardMappingDraft", "DELETE", Draft, null, null, Ok(typeof(MappingDraftResultV1)), headers: ["If-Match"]);
    public static readonly ApiOperation GetCatalogRevisionIngestionReceipt = Get("GetCatalogRevisionIngestionReceipt",
        Project + "/revision-ingestions/{operationId}", typeof(CatalogRevisionIngestionReceiptV1));
    public static readonly ApiOperation ResumeCatalogRevisionIngestion = Send("ResumeCatalogRevisionIngestion", "POST",
        Project + "/revision-ingestions/{operationId}/resume", null, null, Accepted(typeof(CatalogRevisionIngestionResultV1)));

    public static readonly ApiOperation GetCatalogInterchangeCapabilities = Get("GetCatalogInterchangeCapabilities", "/api/v1/catalog/interchange", typeof(CatalogInterchangeCapabilitiesV1));
    public static readonly ApiOperation ReviewMfdCreate = Send("ReviewMfdCreate", "POST", Project + "/mfd-imports/review",
        "application/vnd.weft.mfd-package.v1", null, Ok(typeof(CatalogInterchangeReviewV1)), query: ["name", "description", "backendId"]);
    public static readonly ApiOperation ReviewMfdAppend = Send("ReviewMfdAppend", "POST", Mapping + "/mfd-imports/review",
        "application/vnd.weft.mfd-package.v1", null, Ok(typeof(CatalogInterchangeReviewV1)), query: ["expectedHeadRevisionId"]);
    public static readonly ApiOperation GetMfdImportReview = Get("GetMfdImportReview", Project + "/mfd-imports/{reviewId}", typeof(CatalogInterchangeReviewV1));
    public static readonly ApiOperation ConfirmMfdImport = Send("ConfirmMfdImport", "POST", Project + "/mfd-imports/{reviewId}/confirm",
        "application/json", typeof(CatalogInterchangeConfirmationV1), Accepted(typeof(CatalogRevisionIngestionResultV1)), headers: Key);
    public static readonly ApiOperation ReviewMfdExport = Send("ReviewMfdExport", "POST", Revision + "/mfd-exports/review",
        null, null, Ok(typeof(CatalogInterchangeReviewV1)));
    public static readonly ApiOperation DownloadMfdExport = new("DownloadMfdExport", "POST", Revision + "/mfd-exports/{reviewId}/download",
        None, None, "application/json", typeof(CatalogInterchangeConfirmationV1), Ok(null), typeof(CatalogErrorV1),
        "application/xml", ["application/zip"]);

    public static readonly ApiOperation GetCatalogProjectionReport = Get("GetCatalogProjectionReport", Revision + "/reports/projection",
        typeof(CatalogProjectionReportV1));
    public static readonly ApiOperation GetCatalogValidationReport = Get("GetCatalogValidationReport", Revision + "/reports/validation",
        typeof(CatalogValidationReportV1), ["projectionDerivationId"]);
    public static readonly ApiOperation GetRetainedCatalogProjectionReport = Get("GetRetainedCatalogProjectionReport",
        Revision + "/reports/projection/{derivationId}", typeof(CatalogProjectionReportV1));
    public static readonly ApiOperation GetRetainedCatalogValidationReport = Get("GetRetainedCatalogValidationReport",
        Revision + "/reports/validation/{derivationId}", typeof(CatalogValidationReportV1), ["projectionDerivationId"]);
    public static readonly ApiOperation EnsureCatalogProjectionReport = Send("EnsureCatalogProjectionReport", "POST",
        Revision + "/reports/projection/ensure", null, null, Accepted(typeof(CatalogProjectionReportV1)), query: ["repairDerivationId"]);
    public static readonly ApiOperation EnsureCatalogValidationReport = Send("EnsureCatalogValidationReport", "POST",
        Revision + "/reports/validation/ensure", null, null, Accepted(typeof(CatalogValidationReportV1)), query: ["repairDerivationId"]);
    public static readonly ApiOperation GetCatalogRevisionComparison = Get("GetCatalogRevisionComparison", Mapping + "/comparisons",
        typeof(RevisionComparisonV1), ["leftRevision", "leftProjection", "leftValidation", "rightRevision", "rightProjection", "rightValidation"]);

    public static readonly ApiOperation SubmitCatalogPreview = Send("SubmitCatalogPreview", "POST", Revision + "/previews",
        "application/json", typeof(CatalogPreviewSubmissionV1), Created(typeof(CatalogPreviewSubmitResultV1)), headers: Key) with
    { AlternateErrorBodies = new Dictionary<int, Type> { [422] = typeof(CatalogXmlPreviewProblemV1) } };
    public static readonly ApiOperation GetCatalogPreview = Get("GetCatalogPreview", Organization + "/previews/{runId}", typeof(CatalogPreviewRunV1));
    public static readonly ApiOperation GetCatalogPreviewOutput = new("GetCatalogPreviewOutput", "GET",
        Organization + "/previews/{runId}/outputs/{ordinal}", None, None, null, null, Ok(null), typeof(CatalogErrorV1),
        AlternateResponseMediaTypes: ["application/edi-x12", "application/edifact", "text/csv", "text/tab-separated-values", "application/xml"]);
    public static readonly ApiOperation CancelCatalogPreview = Send("CancelCatalogPreview", "POST", Organization + "/previews/{runId}/cancel",
        null, null, Ok(typeof(CatalogPreviewCancelResultV1)));

    public static readonly ApiOperation GetRegressionCaseCapabilities = Get("GetRegressionCaseCapabilities",
        Mapping + "/regression-cases/capabilities", typeof(RegressionCaseCapabilitiesV1));
    public static readonly ApiOperation GetRegressionCases = Get("GetRegressionCases", Mapping + "/regression-cases",
        typeof(RegressionCasePageV1), ["limit", "afterCaseId"]);
    public static readonly ApiOperation GetRegressionCaseVersion = Get("GetRegressionCaseVersion",
        Mapping + "/regression-cases/{caseId}/versions/{version}", typeof(RegressionCaseDetailV1));
    public static readonly ApiOperation PutRegressionCaseVersion = Send("PutRegressionCaseVersion", "PUT",
        Mapping + "/regression-cases/{caseId}/versions/{version}", "application/vnd.weft.regression-case.v1+json",
        typeof(RegressionCasePutV1), Created(typeof(RegressionCaseSavedV1)));
    public static readonly ApiOperation GetRegressionRuns = Get("GetRegressionRuns", Mapping + "/regression-runs",
        typeof(RegressionRunPageV1), ["limit", "afterRunId"]);
    public static readonly ApiOperation GetRegressionRun = Get("GetRegressionRun", Mapping + "/regression-runs/{runId}", typeof(RegressionRunV1));
    public static readonly ApiOperation PutRegressionRun = Send("PutRegressionRun", "PUT", Mapping + "/regression-runs/{runId}",
        "application/vnd.weft.regression-run.v1+json", typeof(RegressionRunPutV1), Created(typeof(RegressionRunReceiptV1)));
    public static readonly ApiOperation CancelRegressionRun = Send("CancelRegressionRun", "POST", Mapping + "/regression-runs/{runId}/cancel",
        null, null, Ok(typeof(RegressionRunV1)));

    public static readonly ApiOperation GetBuildCapabilities = Get("GetBuildCapabilities", Mapping + "/build-capabilities", typeof(BuildCapabilitiesV1));
    public static readonly ApiOperation GetBuildEnvironments = Get("GetBuildEnvironments", Project + "/build-environments",
        typeof(BuildEnvironmentPageV1), ["limit", "afterEnvironmentId", "afterVersion"]);
    public static readonly ApiOperation GetBuildEnvironment = Get("GetBuildEnvironment",
        Project + "/build-environments/{environmentId}/versions/{version}", typeof(BuildEnvironmentV1));
    public static readonly ApiOperation PutBuildEnvironment = Send("PutBuildEnvironment", "PUT",
        Project + "/build-environments/{environmentId}/versions/{version}", "application/vnd.weft.build-environment.v1+json",
        typeof(BuildEnvironmentPutV1), Created(typeof(BuildEnvironmentReceiptV1)));
    public static readonly ApiOperation GetBuildEnvironmentCapabilities = Get("GetBuildEnvironmentCapabilities",
        Project + "/build-environment-capabilities", typeof(BuildEnvironmentCapabilitiesV1));
    public static readonly ApiOperation GetBuildApprovals = Get("GetBuildApprovals", Mapping + "/build-approvals",
        typeof(BuildApprovalPageV1), ["limit", "afterApprovalId"]);
    public static readonly ApiOperation GetBuildApproval = Get("GetBuildApproval", Mapping + "/build-approvals/{approvalId}", typeof(BuildApprovalV1));
    public static readonly ApiOperation PutBuildApproval = Send("PutBuildApproval", "PUT", Mapping + "/build-approvals/{approvalId}",
        "application/vnd.weft.build-approval.v1+json", typeof(BuildApprovalPutV1), Created(typeof(BuildApprovalReceiptV1)));
    public static readonly ApiOperation RevokeBuildApproval = Send("RevokeBuildApproval", "POST", Mapping + "/build-approvals/{approvalId}/revoke",
        null, null, Ok(typeof(BuildApprovalV1)));
    public static readonly ApiOperation GetBuildRuns = Get("GetBuildRuns", Mapping + "/build-runs", typeof(BuildRunPageV1), ["limit", "afterBuildId"]);
    public static readonly ApiOperation GetBuildRun = Get("GetBuildRun", Mapping + "/build-runs/{buildId}", typeof(BuildRunV1));
    public static readonly ApiOperation PutBuildRun = Send("PutBuildRun", "PUT", Mapping + "/build-runs/{buildId}",
        "application/vnd.weft.build-run.v1+json", typeof(BuildRunPutV1), Created(typeof(BuildRunReceiptV1)));
    public static readonly ApiOperation CancelBuildRun = Send("CancelBuildRun", "POST", Mapping + "/build-runs/{buildId}/cancel",
        null, null, Ok(typeof(BuildRunV1)));

    public static readonly ApiOperation GetChatAvailability = Get("GetChatAvailability", Chat, typeof(ChatAvailabilityV1), error: typeof(ChatErrorV1));
    public static readonly ApiOperation GetChatConversations = Get("GetChatConversations", Chat + "/conversations",
        typeof(ChatConversationPageV1), ["limit"], error: typeof(ChatErrorV1));
    public static readonly ApiOperation CreateChatConversation = Send("CreateChatConversation", "POST", Chat + "/conversations",
        "application/json", typeof(ChatConversationCreateV1), Created(typeof(ChatConversationV1)), error: typeof(ChatErrorV1));
    public static readonly ApiOperation GetChatConversation = Get("GetChatConversation", Chat + "/conversations/{conversationId}",
        typeof(ChatConversationDetailV1), error: typeof(ChatErrorV1));
    public static readonly ApiOperation CreateChatTurn = Send("CreateChatTurn", "POST", Chat + "/conversations/{conversationId}/turns",
        "application/json", typeof(ChatTurnCreateV1), Created(typeof(ChatTurnV1)), error: typeof(ChatErrorV1));
    public static readonly ApiOperation CancelChatTurn = Send("CancelChatTurn", "POST", Chat + "/conversations/{conversationId}/turns/{turnId}/cancel",
        null, null, Ok(typeof(ChatTurnV1)), error: typeof(ChatErrorV1));
    public static readonly ApiOperation StreamChatTurnEvents = new("StreamChatTurnEvents", "GET",
        Chat + "/conversations/{conversationId}/turns/{turnId}/events", ["after"], None, null, null, Ok(typeof(ChatTurnEventV1)),
        typeof(ChatErrorV1), "text/event-stream");

    public static IReadOnlyList<ApiOperation> All =>
    [
        GetAutomationPolicy, PutAutomationPolicy, GetMappingAutomation, PutMappingAutomation, GetRegressionSuites, GetRegressionSuite, GetRegressionEvaluations, PutRegressionRecheck,
        GetNotificationSubscription, PutNotificationSubscription, GetNotifications, GetNotificationInbox, ReadInboxNotification, ReadNotification, GetNotificationOptions,
        GetNotificationDeliveries, GetNotificationDestinations, EnableNotificationDestination,
        GetHealth, GetReadiness, GetCatalogContext, SearchCatalog, GetHelpSections, GetHelpSection, GetCatalogProjects, CreateCatalogProject, GetCatalogProject, GetCatalogMappings, CreateCatalogMapping,
        GetCatalogMapping, GetCatalogMappingMetadata, EditCatalogMappingMetadata, GetCatalogRevisions, AppendCatalogRevision,
        GetCatalogRevision, GetCatalogRevisionIngestionReceipt, ResumeCatalogRevisionIngestion, GetCatalogProjectionReport,
        CreateMappingDraft, ListMappingDrafts, GetMappingDraft, GetMappingDraftOperations, PutMappingDraftOperations,
        CheckpointMappingDraft, CompareMappingDraft, PreviewMappingDraft, SaveMappingDraft, RebaseMappingDraft, DiscardMappingDraft,
        GetCatalogInterchangeCapabilities, ReviewMfdCreate, ReviewMfdAppend, GetMfdImportReview, ConfirmMfdImport, ReviewMfdExport, DownloadMfdExport,
        GetCatalogValidationReport, GetRetainedCatalogProjectionReport, GetRetainedCatalogValidationReport,
        EnsureCatalogProjectionReport, EnsureCatalogValidationReport, GetCatalogRevisionComparison, SubmitCatalogPreview,
        GetCatalogPreview, GetCatalogPreviewOutput, CancelCatalogPreview, GetRegressionCaseCapabilities, GetRegressionCases,
        GetRegressionCaseVersion, PutRegressionCaseVersion, GetRegressionRuns, GetRegressionRun, PutRegressionRun,
        CancelRegressionRun, GetBuildCapabilities, GetBuildEnvironments, GetBuildEnvironment, PutBuildEnvironment,
        GetBuildEnvironmentCapabilities, GetBuildApprovals, GetBuildApproval, PutBuildApproval, RevokeBuildApproval,
        GetBuildRuns, GetBuildRun, PutBuildRun, CancelBuildRun, GetChatAvailability, GetChatConversations,
        CreateChatConversation, GetChatConversation, CreateChatTurn, CancelChatTurn, StreamChatTurnEvents,
        GetWorkspaceChatAvailability, GetWorkspaceChatConversations, CreateWorkspaceChatConversation, GetWorkspaceChatConversation,
        CreateWorkspaceChatTurn, CancelWorkspaceChatTurn, StreamWorkspaceChatTurnEvents,
        GetReleaseTargets, GetReleaseOperations, GetReleaseSummary, ApplyReleaseOperation,
        .. AdministrationApiOperations.All
    ];

    /// <summary>
    /// Documented operations the CLI deliberately does not call, with the reason. Every other operation in the
    /// specification must appear in <see cref="All"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, string> NotUsed { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["GetProjectionFixture"] = "a Development-only fixture route that the Session host does not map",
        ["CreateSpecBundle"] = "specification intake is currently exposed through the browser, not a CLI command",
        ["GetSpecBundleInventory"] = "specification intake is currently exposed through the browser, not a CLI command",
        ["TombstoneSpecBundle"] = "specification intake is currently exposed through the browser, not a CLI command",
        ["UploadSpecSource"] = "specification intake is currently exposed through the browser, not a CLI command",
        ["PasteSpecText"] = "specification intake is currently exposed through the browser, not a CLI command",
        ["ReadSpecExtraction"] = "specification intake is currently exposed through the browser, not a CLI command",
        ["TombstoneSpecSource"] = "specification intake is currently exposed through the browser, not a CLI command",
        ["GetMappingDraftBase"] = "the browser edit module consumes opaque native base bytes; the CLI submits typed operation files and has no local core",
        ["GetMappingDraftValidation"] = "the browser issues view consumes the detailed checkpoint report; the CLI checkpoint command reports its validity and issue count",
        ["GetMappingDraftProjection"] = "the browser workbench consumes the detailed checkpoint projection; the CLI submits typed intent rather than rendering mappings",
        ["RestoreMappingRevision"] = "the CLI does not expose revision restoration",
        ["GetChatMappingWriteReceipts"] = "the CLI does not expose durable chat mapping-write receipt discovery",
        ["RecoverChatMappingWriteReceipts"] = "the CLI does not expose chat mapping-write recovery",
        ["GetChatMappingEditProposals"] = "the CLI does not expose the exact chat proposal review workflow",
        ["GetChatMappingEditProposal"] = "the CLI does not expose the exact chat proposal review workflow",
        ["GetChatMappingEditOperations"] = "the CLI does not expose the exact chat proposal review workflow",
        ["ApproveChatMappingEditProposal"] = "the CLI has no exact proposal review and approval command",
        ["RecoverChatMappingEditProposal"] = "the CLI does not expose approved chat proposal recovery",
        ["ReadBrowserAuthentication"] = "browser cookie-session sign-in; the CLI signs in with MSAL",
        ["BeginBrowserLogin"] = "browser cookie-session sign-in; the CLI signs in with MSAL",
        ["LogoutBrowserSession"] = "browser cookie-session sign-in; the CLI signs in with MSAL",
        ["ForgetDeniedLocalSession"] = "browser cookie-session sign-in; the CLI signs in with MSAL",
        ["CompleteEntraLogin"] = "the browser sign-in callback; the CLI signs in with MSAL"
    };

    /// <summary>Headers that only authenticate a browser cookie session; a bearer request never sends them.</summary>
    public static IReadOnlySet<string> CookieSessionHeaders { get; } = new HashSet<string>(["Origin", "X-XSRF-TOKEN"], StringComparer.OrdinalIgnoreCase);

    private static ApiOperation Get(string id, string path, Type response, string[]? query = null, string[]? headers = null,
        Type? error = null) =>
        new(id, "GET", path, query ?? None, headers ?? None, null, null, Ok(response), error ?? typeof(CatalogErrorV1));

    private static ApiOperation Send(string id, string method, string path, string? mediaType, Type? body,
        IReadOnlyDictionary<int, Type?> success, string[]? query = null, string[]? headers = null, Type? error = null) =>
        new(id, method, path, query ?? None, headers ?? None, mediaType, body, success, error ?? typeof(CatalogErrorV1));

    private static Dictionary<int, Type?> Ok(Type? type) => new() { [200] = type };
    private static Dictionary<int, Type?> Created(Type type) => new() { [200] = type, [201] = type };
    private static Dictionary<int, Type?> Accepted(Type type) => new() { [200] = type, [202] = type };
}
