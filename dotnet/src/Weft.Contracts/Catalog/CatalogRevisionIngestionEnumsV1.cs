using System.Text.Json;
using System.Text.Json.Serialization;

namespace Weft.Contracts.Catalog;

[JsonConverter(typeof(CatalogRevisionIngestionOutcomeConverterV1))]
public enum CatalogRevisionIngestionOutcomeV1
{
    CommittedValid = 1,
    CommittedDecodedInvalid = 2,
    InProgress = 3,
    NeedsSource = 4,
    RetryableFailure = 5,
    Rejected = 6,
    HeadConflict = 7,
    AttemptExhausted = 8,
    IdempotencyConflict = 9,
    ClaimLost = 10,
    NotAuthorizedOrNotFound = 11
}

[JsonConverter(typeof(CatalogRevisionIngestionPhaseConverterV1))]
public enum CatalogRevisionIngestionPhaseV1
{
    Reserved = 1,
    Staged = 2,
    Ready = 3,
    Committed = 4,
    Rejected = 5,
    HeadConflict = 6,
    AttemptExhausted = 7
}

[JsonConverter(typeof(CatalogRevisionIngestionRejectionConverterV1))]
public enum CatalogRevisionIngestionRejectionV1
{
    EmptyNative = 1,
    NativeLimit = 2,
    UnregisteredBackend = 3,
    NativeDecode = 4,
    UnsupportedArtifact = 5,
    ResourceLimit = 6
}

[JsonConverter(typeof(CatalogRevisionIngestionFailureConverterV1))]
public enum CatalogRevisionIngestionFailureV1
{
    NeedsSource = 1,
    StorageIntegrity = 2,
    StorageIo = 3,
    BackendUnavailable = 4,
    BackendFailure = 5,
    TimedOut = 6,
    Canceled = 7,
    IsolationFailure = 8,
    ProtocolFailure = 9,
    ReservedProfileUnavailable = 10,
    EvidenceLimit = 11
}

internal sealed class CatalogRevisionIngestionOutcomeConverterV1 : JsonStringEnumConverter<CatalogRevisionIngestionOutcomeV1>
{
    public CatalogRevisionIngestionOutcomeConverterV1() : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false) { }
}

internal sealed class CatalogRevisionIngestionPhaseConverterV1 : JsonStringEnumConverter<CatalogRevisionIngestionPhaseV1>
{
    public CatalogRevisionIngestionPhaseConverterV1() : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false) { }
}

internal sealed class CatalogRevisionIngestionRejectionConverterV1 : JsonStringEnumConverter<CatalogRevisionIngestionRejectionV1>
{
    public CatalogRevisionIngestionRejectionConverterV1() : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false) { }
}

internal sealed class CatalogRevisionIngestionFailureConverterV1 : JsonStringEnumConverter<CatalogRevisionIngestionFailureV1>
{
    public CatalogRevisionIngestionFailureConverterV1() : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false) { }
}
