using System.Text.Json.Serialization;

namespace Braid.WorkerTransport;

/// <summary>
/// The <c>GET /v1/capabilities</c> document of <c>braid.ferrule-service.v1</c>. The server writes it field by field;
/// tests hold those bytes to these types, which the service's OpenAPI document describes.
/// </summary>
public sealed record FerruleServiceCapabilitiesV1(
    string Protocol,
    string BuildIdentity,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EngineRevision,
    int Concurrency,
    int Capacity,
    long AdmissionWaitMilliseconds,
    IReadOnlyList<FerruleServiceProfileV1> Profiles,
    IReadOnlyList<string> Reserved);

/// <summary>One served profile and its fixed limits, in ordinal name order.</summary>
public sealed record FerruleServiceProfileV1(
    string Name,
    int MaximumRequestBytes,
    int MaximumResponseBytes,
    long WallSeconds,
    int CpuSeconds,
    long MemoryBytes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FerruleServiceToolchainV1? Toolchain,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FerruleServiceRuntimeV1? Runtime);

/// <summary>The compile profile's toolchain.</summary>
public sealed record FerruleServiceToolchainV1(string Protocol, string Profile, IReadOnlyList<string> SupportedProfiles, string SdkVersion, string RuntimeVersion, string Identity,
    string BuildIdentity);

/// <summary>The execute profile's compiled runtime.</summary>
public sealed record FerruleServiceRuntimeV1(string Protocol, IReadOnlyList<string> SupportedProtocols, string Version, string Identity, long ManagedHeapBytes, long MaximumAddressSpaceBytes);
