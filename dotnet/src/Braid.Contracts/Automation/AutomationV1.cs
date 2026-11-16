namespace Braid.Contracts.Automation;

public sealed record AutomationSavedV1(bool Saved);

public sealed record AutomationPolicyV1(bool RequirePassingRegression, int ErrorRatePercent, int MinimumRequests, int WindowSeconds);
public sealed record MappingAutomationV1(bool Enabled);
public sealed record RegressionEvaluationV1(string CaseId, string Version, string OriginalRevisionId, string State, int Attempts);
public sealed record RegressionSuiteV1(string SuiteId, string RevisionId, string State, string CreatedAtUtc,
    int TotalCases, int PassedCases, int FailedCases, int PendingCases);
public sealed record RegressionEvaluationsV1(IReadOnlyList<RegressionEvaluationV1> Cases, string? NextCaseId);
public sealed record RegressionRecheckV1(string MappingId, string RevisionId);
public sealed record RegressionSuitesV1(IReadOnlyList<RegressionSuiteV1> Suites, string? NextSuiteId);
public sealed record NotificationSubscriptionV1(IReadOnlyList<string> Events, bool InApp, bool Email, IReadOnlyList<string> Webhooks);
public sealed record NotificationV1(string Id, string Event, string? ProjectId, string? MappingId, string? RevisionId,
    string ResourceId, string Link, string CreatedAtUtc, bool Read);
public sealed record NotificationsV1(IReadOnlyList<NotificationV1> Notifications, string? NextId);
public sealed record NotificationDestinationV1(string Id, string Channel, bool Enabled);
public sealed record NotificationDestinationsV1(IReadOnlyList<NotificationDestinationV1> Destinations);
public sealed record NotificationOptionsV1(IReadOnlyList<string> Events, bool EmailAvailable, IReadOnlyList<string> Webhooks);
public sealed record NotificationDestinationEnableV1(string DestinationId);
public sealed record NotificationReadV1;
public sealed record NotificationDeliveryV1(string Id, string Event, string ResourceId, string DestinationId,
    string State, int Attempts, string? NextAttemptAtUtc, IReadOnlyList<NotificationDeliveryAttemptV1> History);
public sealed record NotificationDeliveryAttemptV1(int Attempt, string Outcome, string RecordedAtUtc);
public sealed record NotificationDeliveriesV1(IReadOnlyList<NotificationDeliveryV1> Deliveries, string? NextId);
