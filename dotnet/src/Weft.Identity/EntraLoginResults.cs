using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Weft.Application;

namespace Weft.Identity;

public sealed class EntraLoginChallenge
{
    internal EntraLoginChallenge(string authorizationUrl) => AuthorizationUrl = authorizationUrl;
    [JsonIgnore] public string AuthorizationUrl { get; }
    public override string ToString() => nameof(EntraLoginChallenge);
}

public sealed class EntraVerifiedLogin
{
    private readonly EntraBrowserClient owner;
    private readonly HttpContext context;
    private readonly object operation;

    internal EntraVerifiedLogin(EntraBrowserClient owner, HttpContext context, object operation,
        Guid organizationId, ExternalIdentityKey externalIdentity, DateTimeOffset deadlineUtc, string returnPath,
        string? displayName = null, DateTimeOffset observedAtUtc = default)
    {
        this.owner = owner;
        this.context = context;
        this.operation = operation;
        OrganizationId = organizationId;
        ExternalIdentity = externalIdentity;
        VerifiedDeadlineUtc = deadlineUtc;
        ReturnPath = returnPath;
        DisplayName = displayName;
        ObservedAtUtc = observedAtUtc;
    }

    [JsonIgnore] public Guid OrganizationId { get; }
    [JsonIgnore] public ExternalIdentityKey ExternalIdentity { get; }
    [JsonIgnore] public DateTimeOffset VerifiedDeadlineUtc { get; }
    /// <summary>The validated application path the sign-in returns to, or the application root.</summary>
    [JsonIgnore] public string ReturnPath { get; }
    [JsonIgnore] internal string? DisplayName { get; }
    [JsonIgnore] internal DateTimeOffset ObservedAtUtc { get; }
    internal bool HasProvenance(EntraBrowserClient expectedOwner, HttpContext expectedContext,
        object expectedOperation) => ReferenceEquals(owner, expectedOwner) && ReferenceEquals(context, expectedContext) &&
        ReferenceEquals(operation, expectedOperation);
    public override string ToString() => nameof(EntraVerifiedLogin);
}
