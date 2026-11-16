using System.Text.Json;
using Braid.Domain;

namespace Braid.Application.Automation;

public static class NotificationEvents
{
    public static readonly IReadOnlyList<string> All = Array.AsReadOnly(new[]
    {
        "revisionUploaded", "regressionFailed", "regressionRecovered", "buildQualified", "buildFailed",
        "releaseActivated", "releaseRolledBack", "endpointErrorRate", "invitation"
    });
}

/// <summary>Metadata-only scoped operations. The persistence boundary owns fresh authority and atomic writes.</summary>
public interface IAutomationStore
{
    Task<JsonElement?> ReadInboxAsync(ActorContext actor, Guid organizationId, Guid? after, int limit, CancellationToken token = default);
    Task<bool> MarkInboxReadAsync(ActorContext actor, Guid organizationId, Guid eventId, CancellationToken token = default);
    Task<JsonElement?> ReadAsync(ActorContext actor, ProjectScope project, string operation, Guid? resource,
        Guid? after, int limit, CancellationToken token = default);
    Task<bool> WriteAsync(ActorContext actor, ProjectScope project, string operation, Guid? resource,
        JsonElement value, CancellationToken token = default);
}

public sealed record AutomationClaim(Guid OrganizationId, Guid SuiteId, Guid CaseId, long CaseVersion,
    PrincipalId Actor, Regression.RegressionCaseReference Original, Regression.RegressionEvaluationTarget Target,
    Guid Token, int Attempt);
public sealed record NotificationMessage(Guid Id, Guid OrganizationId, Guid ProjectId, string Event,
    Guid? MappingId, Guid? RevisionId, Guid ResourceId, string Link, DateTimeOffset CreatedAt);
public sealed record NotificationDeliveryClaim(Guid OrganizationId, Guid Id, Guid ProjectId, PrincipalId Recipient,
    string Destination, NotificationMessage Message, Guid Token, int Attempt);

public interface IAutomationQueue
{
    Task<AutomationClaim?> ClaimEvaluationAsync(CancellationToken token);
    Task<bool> FinishEvaluationAsync(AutomationClaim claim, Regression.RegressionCaseExecution? result, CancellationToken token);
    Task<NotificationDeliveryClaim?> ClaimDeliveryAsync(CancellationToken token);
    Task<bool> FinishDeliveryAsync(NotificationDeliveryClaim claim, NotificationDeliveryOutcome outcome, CancellationToken token);
}

/// <summary>Trusted comparison authority. Callers supply no pass decision or result bytes.</summary>
public interface IAutomationComparisonSettler
{
    Task<bool> SettleNextAsync(CancellationToken token);
}

public enum NotificationDeliveryOutcome { Delivered, Unavailable, Refused, TransportFailure }

public interface INotificationTransport
{
    Task<NotificationDeliveryOutcome> SendAsync(NotificationDeliveryClaim claim, CancellationToken token);
}
