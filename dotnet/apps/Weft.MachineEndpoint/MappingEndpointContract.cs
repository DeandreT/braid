using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Weft.Application;

namespace Weft.MachineEndpoint;

/// <summary>
/// The wire types of the machine-facing contract, which <c>contracts/weft-endpoint-v1.json</c> describes. The endpoint
/// writes every refusal as <see cref="MappingInvocationErrorV1"/>. Its strict parser reads the request and the ledger
/// retains the completed response's exact bytes; tests hold both to these types.
/// </summary>
internal static class MappingEndpointContract
{
    internal const string MediaType = "application/json";
    internal const string ResponseSchema = "weft.mapping-invocation-response.v1";

    /// <summary>The JSON settings of the endpoint's own bodies and of its OpenAPI document.</summary>
    internal static readonly JsonSerializerOptions Json = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    internal static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.NumberHandling = JsonNumberHandling.Strict;
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }
}

/// <summary>
/// The <c>POST /v1/invocations</c> body. The first input is the unnamed primary document; every other input names a
/// declared source. Unknown or repeated members are refused.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record MappingInvocationRequestV1(
    [property: MinLength(1), MaxLength(PreviewPolicy.MaximumInputDocuments)]
    [property: Description("The unnamed primary document first, then each declared named source once.")]
    IReadOnlyList<MappingInvocationInputV1> Inputs)
{
    [MaxLength(PreviewPolicy.MaximumParameters)]
    [Description("Typed runtime parameters with unique names.")]
    public IReadOnlyList<MappingInvocationParameterV1> Parameters { get; init; } = [];
}

/// <summary>One input document; base64 keeps the exact bytes that are digested and run.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record MappingInvocationInputV1(
    [property: Description("The document's exact bytes, base64-encoded; at least one byte.")] byte[] Content)
{
    [Description("Absent or null for the primary document; the declared source name for every other input.")]
    public string? Name { get; init; }
}

/// <summary>One typed parameter; numbers and booleans keep their exact lexical form.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record MappingInvocationParameterV1(string Name, MappingInvocationParameterKindV1 Kind, string Value);

internal enum MappingInvocationParameterKindV1 { String, Integer, Float, Boolean }

/// <summary>
/// A completed call (<c>weft.mapping-invocation-response.v1</c>): the release, build and artifact that ran, the clock
/// it ran with and every output. A retry with the same request ID and body returns the same bytes.
/// </summary>
internal sealed record MappingInvocationResponseV1(
    string Schema,
    Guid RequestId,
    Guid ReleaseId,
    Guid BuildId,
    Guid RevisionId,
    [property: RegularExpression("^[0-9a-f]{64}$"), Description("SHA-256 of the compiled artifact that ran.")] string ArtifactDigest,
    [property: RegularExpression("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\\.[0-9]{7}Z$")]
    [property: Description("The server's UTC clock that the call ran with.")]
    string CurrentDateTime,
    [property: Description("The primary output first, then named outputs in ordinal name order.")]
    IReadOnlyList<MappingInvocationOutputV1> Outputs);

/// <summary>One output document.</summary>
internal sealed record MappingInvocationOutputV1(
    [property: Description("Null for the primary output; otherwise the target's name.")] string? Name,
    [property: RegularExpression("^[0-9a-f]{64}$"), Description("SHA-256 of the output's bytes.")] string Sha256,
    [property: Description("The output's exact bytes, base64-encoded.")] byte[] Content);

/// <summary>Every answer other than a completed call. The request ID is present once the endpoint admitted it.</summary>
internal sealed record MappingInvocationErrorV1(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? RequestId,
    MappingInvocationOutcomeV1 Outcome);

/// <summary>The fixed refusal and failure outcomes; see <c>docs/mapping-endpoint.md</c> for each status.</summary>
internal enum MappingInvocationOutcomeV1
{
    InvalidRequestId,
    InvalidRequest,
    Unauthorized,
    Refused,
    Conflict,
    InProgress,
    Expired,
    RequestTooLarge,
    UnsupportedMediaType,
    InvalidInput,
    InvalidParameter,
    ExecutionFailed,
    ResourceLimit,
    Unsupported,
    IntegrityFailure,
    Interrupted,
    AttemptsExhausted,
    Busy,
    Unavailable,
    ReleaseChanged,
    Deadline,
    NoRelease
}
