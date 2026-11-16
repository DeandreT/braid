using System.Text;
using System.Text.Json;
using Braid.Contracts.Operations;
using Braid.Contracts.Projections;
using Braid.Domain;

namespace Braid.Application;

/// <summary>Bounds traversal of an already owned report without changing any exact text.</summary>
internal sealed class RevisionReportValidationBudget(RevisionReportPolicy caps, CancellationToken cancellationToken)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private int items;

    internal RevisionReportPolicy Caps { get; } = caps;
    internal void Check() => cancellationToken.ThrowIfCancellationRequested();
    internal static void Require(bool condition)
    {
        if (!condition) throw Integrity();
    }
    internal static RevisionReportValidationException Integrity() => new(RevisionReportValidationFailure.Integrity);
    internal static RevisionReportValidationException Limit() => new(RevisionReportValidationFailure.ResourceLimit);

    internal T Required<T>(T? value) where T : class
    {
        Check();
        return value ?? throw Integrity();
    }

    internal void Known<T>(T value) where T : struct, Enum
    {
        Check();
        Require(Enum.IsDefined(value));
    }

    internal int Array<T>(IReadOnlyList<T>? values, int? maximum = null)
    {
        Check();
        var count = Required(values).Count;
        Require(count >= 0);
        if (count > (maximum ?? Caps.ItemLimit) || count > Caps.ItemLimit - items) throw Limit();
        items += count;
        return count;
    }

    internal void Count(int count, int maximum)
    {
        Check();
        Require(count >= 0);
        if (count > maximum) throw Limit();
    }

    internal void Text(string? value, bool allowEmpty = true)
    {
        Check();
        var text = Required(value);
        // Strict UTF8 is never shorter than UTF16 code-unit count. Bound before any scan.
        if (text.Length > Caps.TextByteLimit) throw Limit();
        try
        {
            if (StrictUtf8.GetByteCount(text) > Caps.TextByteLimit) throw Limit();
        }
        catch (EncoderFallbackException) { throw Integrity(); }
        Require(allowEmpty || !string.IsNullOrWhiteSpace(text));
        Check();
    }

    internal void OptionalText(string? value)
    {
        if (value is not null) Text(value);
    }

    internal void Unsigned(string? value)
    {
        Text(value, false);
        Require(value![0] is >= '0' and <= '9' && (value.Length == 1 || value[0] != '0'));
        foreach (var digit in value)
        {
            Check();
            Require(digit is >= '0' and <= '9');
        }
    }

    internal void Literal(LiteralValue? value)
    {
        Check();
        switch (value)
        {
            case StringLiteralValue text: Text(text.Value); break;
            case BooleanLiteralValue: break;
            case NullLiteralValue nil: Known(nil.NullKind); break;
            case ExactNumberLiteralValue number:
                Known(number.Flavor);
                Text(number.Value, false);
                if (number.Flavor == ExactNumberFlavor.BinaryFloat)
                {
                    Require(number.Value.Length == 16);
                    foreach (var digit in number.Value)
                    {
                        Check();
                        Require(digit is >= '0' and <= '9' or >= 'a' and <= 'f');
                    }
                }
                else Number(number.Value, number.Flavor == ExactNumberFlavor.Decimal);
                break;
            default: throw Integrity();
        }
    }

    private void Number(string value, bool decimalAllowed)
    {
        var index = value[0] == '-' ? 1 : 0;
        Require(index < value.Length);
        if (value[index] == '0') index++;
        else
        {
            Require(value[index] is >= '1' and <= '9');
            Digits(value, ref index);
        }
        if (decimalAllowed && index < value.Length && value[index] == '.')
        {
            index++;
            Require(index < value.Length && value[index] is >= '0' and <= '9');
            Digits(value, ref index);
        }
        if (decimalAllowed && index < value.Length && value[index] is 'e' or 'E')
        {
            index++;
            if (index < value.Length && value[index] is '+' or '-') index++;
            Require(index < value.Length && value[index] is >= '0' and <= '9');
            Digits(value, ref index);
        }
        Require(index == value.Length);
    }

    private void Digits(string value, ref int index)
    {
        while (index < value.Length && value[index] is >= '0' and <= '9')
        {
            Check();
            index++;
        }
    }

    internal void Locator(NativeLocator? value)
    {
        Required(value);
        Text(value!.DocumentId, false);
        switch (value)
        {
            case DocumentNativeLocator: break;
            case JsonPointerNativeLocator pointer:
                Text(pointer.Pointer);
                Require(pointer.Pointer.Length == 0 || pointer.Pointer[0] == '/');
                for (var index = 0; index < pointer.Pointer.Length; index++)
                {
                    Check();
                    if (pointer.Pointer[index] == '~')
                        Require(++index < pointer.Pointer.Length && pointer.Pointer[index] is '0' or '1');
                }
                break;
            case GraphNodeNativeLocator graph:
                Text(graph.NodeId, false);
                if (graph.FunctionId is not null) Text(graph.FunctionId, false);
                break;
            case TextSpanNativeLocator span:
                Known(span.Unit); Unsigned(span.StartOffset); Unsigned(span.Length);
                break;
            case OpaqueNativeLocator opaque: Text(opaque.Scheme, false); Text(opaque.Value); break;
            default: throw Integrity();
        }
    }

    internal string ReferenceId(ProjectionNodeReference? value)
    {
        Check();
        var id = value switch
        {
            BoundaryNodeReference boundary => boundary.BoundaryId,
            FieldNodeReference field => field.FieldId,
            ParameterNodeReference parameter => parameter.ParameterId,
            RuleNodeReference rule => rule.RuleId,
            ControlNodeReference control => control.ControlId,
            _ => throw Integrity()
        };
        Text(id, false);
        return id;
    }

    internal RevisionReportProvenance? Provenance(RevisionReportKey key, OperationProvenanceV1? value)
    {
        if (value is null) return null;
        Provenance(key, value.Revision, value.NativeArtifactDigest, value.Backend, value.AdapterVersion);
        return new(key.Scope, key.NativeDigest, key.Profile, value.Confirmed);
    }

    internal void Provenance(RevisionReportKey key, ScopedRevisionId? scope, ArtifactDigest? digest,
        BackendIdentity? backend, string? adapter)
    {
        Required(scope); Required(digest); Required(backend);
        Text(scope!.OrganizationId, false); Text(scope.ProjectId, false);
        Text(scope.MappingId, false); Text(scope.RevisionId, false);
        Text(digest!.Algorithm, false); Text(digest.Value, false);
        Text(backend!.Id, false); Text(backend.Revision, false); Text(backend.BuildId, false); Text(adapter, false);
        Require(scope.OrganizationId == key.Scope.OrganizationId.ToString("D") &&
            scope.ProjectId == key.Scope.ProjectId.ToString("D") && scope.MappingId == key.Scope.MappingId.ToString("D") &&
            scope.RevisionId == key.Scope.RevisionId.ToString("D") && digest.Algorithm == "sha256" &&
            digest.Value == key.NativeDigest.Value && backend.Id == key.BackendId &&
            backend.Revision == key.BackendRevision && backend.BuildId == key.BackendBuild && adapter == key.AdapterBuild);
    }

    internal RevisionReportBackendStatus Status(OperationStatusV1 status)
    {
        Known(status);
        return status switch
        {
            OperationStatusV1.Succeeded => RevisionReportBackendStatus.Succeeded,
            OperationStatusV1.InvalidMapping => RevisionReportBackendStatus.InvalidMapping,
            OperationStatusV1.UnsupportedArtifact => RevisionReportBackendStatus.UnsupportedArtifact,
            OperationStatusV1.ResourceLimit => RevisionReportBackendStatus.ResourceLimit,
            OperationStatusV1.ProtocolFailure => RevisionReportBackendStatus.ProtocolFailure,
            OperationStatusV1.BackendUnavailable => RevisionReportBackendStatus.BackendUnavailable,
            OperationStatusV1.BackendFailure => RevisionReportBackendStatus.BackendFailure,
            OperationStatusV1.TimedOut => RevisionReportBackendStatus.TimedOut,
            OperationStatusV1.Canceled => RevisionReportBackendStatus.Canceled,
            OperationStatusV1.IsolationFailure => RevisionReportBackendStatus.IsolationFailure,
            _ => throw Integrity()
        };
    }

    internal void ReportJson<T>(T ownedReport)
    {
        Check();
        var bytes = RevisionReportResponseSerializer.Serialize(ownedReport, Caps.ResponseByteLimit, cancellationToken);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = RevisionReportPolicy.MaximumJsonDepthLimit });
        var values = 0;
        while (reader.Read())
        {
            Check();
            if (reader.TokenType is JsonTokenType.PropertyName or JsonTokenType.EndObject or JsonTokenType.EndArray) continue;
            // Scalar values belong to their current container; starts introduce one additional container level.
            var depth = reader.CurrentDepth + (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray ? 1 : 0);
            if (++values > Caps.ResponseItemLimit || depth > Caps.JsonDepthLimit) throw Limit();
        }
        Check();
    }
}
