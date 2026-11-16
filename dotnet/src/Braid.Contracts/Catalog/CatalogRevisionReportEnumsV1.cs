using System.Text.Json;
using System.Text.Json.Serialization;

namespace Braid.Contracts.Catalog;

[JsonConverter(typeof(CatalogRevisionReportStateConverterV1))]
public enum CatalogRevisionReportStateV1 { NotDerived = 1, Pending = 2, Available = 3, Failed = 4 }

[JsonConverter(typeof(CatalogRevisionReportReadOutcomeConverterV1))]
public enum CatalogRevisionReportReadOutcomeV1
{
    Observed = 1, ObjectUnavailable = 2, StorageIntegrity = 3, UnsupportedFormat = 4, ResourceLimit = 5
}

[JsonConverter(typeof(CatalogRevisionReportFailureConverterV1))]
public enum CatalogRevisionReportFailureV1
{
    ObjectUnavailable = 1, StorageIntegrity = 2, StorageIo = 3, BackendUnavailable = 4,
    BackendFailure = 5, TimedOut = 6, Canceled = 7, IsolationFailure = 8,
    ProtocolFailure = 9, ProfileUnavailable = 10, ResourceLimit = 11, UnsupportedArtifact = 12
}

internal abstract class CatalogRevisionReportEnumConverterV1<T> : JsonConverter<T> where T : struct, Enum
{
    public sealed override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String ? ReadName(reader.GetString()!) : throw Unknown();
    public sealed override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => writer.WriteStringValue(WriteName(value));
    protected abstract T ReadName(string name);
    protected abstract string WriteName(T value);
    protected static JsonException Unknown() => new("The catalog report enum value is unknown.");
}

internal sealed class CatalogRevisionReportStateConverterV1 : CatalogRevisionReportEnumConverterV1<CatalogRevisionReportStateV1>
{
    public CatalogRevisionReportStateConverterV1() { }
    protected override CatalogRevisionReportStateV1 ReadName(string name) => name switch
    {
        "notDerived" => CatalogRevisionReportStateV1.NotDerived,
        "pending" => CatalogRevisionReportStateV1.Pending,
        "available" => CatalogRevisionReportStateV1.Available,
        "failed" => CatalogRevisionReportStateV1.Failed,
        _ => throw Unknown()
    };
    protected override string WriteName(CatalogRevisionReportStateV1 value) => value switch
    {
        CatalogRevisionReportStateV1.NotDerived => "notDerived",
        CatalogRevisionReportStateV1.Pending => "pending",
        CatalogRevisionReportStateV1.Available => "available",
        CatalogRevisionReportStateV1.Failed => "failed",
        _ => throw Unknown()
    };
}

internal sealed class CatalogRevisionReportReadOutcomeConverterV1 : CatalogRevisionReportEnumConverterV1<CatalogRevisionReportReadOutcomeV1>
{
    public CatalogRevisionReportReadOutcomeConverterV1() { }
    protected override CatalogRevisionReportReadOutcomeV1 ReadName(string name) => name switch
    {
        "observed" => CatalogRevisionReportReadOutcomeV1.Observed,
        "objectUnavailable" => CatalogRevisionReportReadOutcomeV1.ObjectUnavailable,
        "storageIntegrity" => CatalogRevisionReportReadOutcomeV1.StorageIntegrity,
        "unsupportedFormat" => CatalogRevisionReportReadOutcomeV1.UnsupportedFormat,
        "resourceLimit" => CatalogRevisionReportReadOutcomeV1.ResourceLimit,
        _ => throw Unknown()
    };
    protected override string WriteName(CatalogRevisionReportReadOutcomeV1 value) => value switch
    {
        CatalogRevisionReportReadOutcomeV1.Observed => "observed",
        CatalogRevisionReportReadOutcomeV1.ObjectUnavailable => "objectUnavailable",
        CatalogRevisionReportReadOutcomeV1.StorageIntegrity => "storageIntegrity",
        CatalogRevisionReportReadOutcomeV1.UnsupportedFormat => "unsupportedFormat",
        CatalogRevisionReportReadOutcomeV1.ResourceLimit => "resourceLimit",
        _ => throw Unknown()
    };
}

internal sealed class CatalogRevisionReportFailureConverterV1 : CatalogRevisionReportEnumConverterV1<CatalogRevisionReportFailureV1>
{
    public CatalogRevisionReportFailureConverterV1() { }
    protected override CatalogRevisionReportFailureV1 ReadName(string name) => name switch
    {
        "objectUnavailable" => CatalogRevisionReportFailureV1.ObjectUnavailable,
        "storageIntegrity" => CatalogRevisionReportFailureV1.StorageIntegrity,
        "storageIo" => CatalogRevisionReportFailureV1.StorageIo,
        "backendUnavailable" => CatalogRevisionReportFailureV1.BackendUnavailable,
        "backendFailure" => CatalogRevisionReportFailureV1.BackendFailure,
        "timedOut" => CatalogRevisionReportFailureV1.TimedOut,
        "canceled" => CatalogRevisionReportFailureV1.Canceled,
        "isolationFailure" => CatalogRevisionReportFailureV1.IsolationFailure,
        "protocolFailure" => CatalogRevisionReportFailureV1.ProtocolFailure,
        "profileUnavailable" => CatalogRevisionReportFailureV1.ProfileUnavailable,
        "resourceLimit" => CatalogRevisionReportFailureV1.ResourceLimit,
        "unsupportedArtifact" => CatalogRevisionReportFailureV1.UnsupportedArtifact,
        _ => throw Unknown()
    };
    protected override string WriteName(CatalogRevisionReportFailureV1 value) => value switch
    {
        CatalogRevisionReportFailureV1.ObjectUnavailable => "objectUnavailable",
        CatalogRevisionReportFailureV1.StorageIntegrity => "storageIntegrity",
        CatalogRevisionReportFailureV1.StorageIo => "storageIo",
        CatalogRevisionReportFailureV1.BackendUnavailable => "backendUnavailable",
        CatalogRevisionReportFailureV1.BackendFailure => "backendFailure",
        CatalogRevisionReportFailureV1.TimedOut => "timedOut",
        CatalogRevisionReportFailureV1.Canceled => "canceled",
        CatalogRevisionReportFailureV1.IsolationFailure => "isolationFailure",
        CatalogRevisionReportFailureV1.ProtocolFailure => "protocolFailure",
        CatalogRevisionReportFailureV1.ProfileUnavailable => "profileUnavailable",
        CatalogRevisionReportFailureV1.ResourceLimit => "resourceLimit",
        CatalogRevisionReportFailureV1.UnsupportedArtifact => "unsupportedArtifact",
        _ => throw Unknown()
    };
}
