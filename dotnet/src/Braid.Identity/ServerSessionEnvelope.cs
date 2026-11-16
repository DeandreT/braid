using System.Text.Json.Serialization;
using Braid.Application;
using Braid.Domain;

namespace Braid.Identity;

internal sealed class ServerSessionEnvelope
{
    internal ServerSessionEnvelope(ServerSessionKey key, Guid bindingId, long bindingVersion,
        PrincipalId owner, Guid epoch, DateTimeOffset verifiedDeadlineUtc)
    {
        if (key is null || key.OrganizationId == Guid.Empty || bindingId == Guid.Empty || bindingVersion < 1 ||
            owner is null || owner.Value == Guid.Empty || epoch == Guid.Empty ||
            !ServerSessionIdentityValidation.IsDatabaseInstant(verifiedDeadlineUtc))
            throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.UntrustedIssuance);
        Key = new(key.OrganizationId, key.HandleDigest);
        BindingId = bindingId;
        BindingVersion = bindingVersion;
        Owner = new(owner.Value);
        Epoch = epoch;
        VerifiedDeadlineUtc = verifiedDeadlineUtc;
    }

    [JsonIgnore]
    internal ServerSessionKey Key { get; }
    [JsonIgnore]
    internal Guid BindingId { get; }
    [JsonIgnore]
    internal long BindingVersion { get; }
    [JsonIgnore]
    internal PrincipalId Owner { get; }
    [JsonIgnore]
    internal Guid Epoch { get; }
    [JsonIgnore]
    internal DateTimeOffset VerifiedDeadlineUtc { get; }
    public override string ToString() => nameof(ServerSessionEnvelope);
}

internal static class ServerSessionIdentityValidation
{
    internal static bool IsDatabaseInstant(DateTimeOffset value) => value.Offset == TimeSpan.Zero &&
        value.Ticks % 10 == 0 && value != DateTimeOffset.MinValue && value != DateTimeOffset.MaxValue;
}
