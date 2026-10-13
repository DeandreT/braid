using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Weft.Application;
using Weft.Contracts.Projections;
using Weft.Domain;

namespace Weft.MappingBackends.Ingestion;

/// <summary>Copies actual validator metadata into bounded evidence; it never decides eligibility.</summary>
public static class IngestionValidationMapper
{
    public static IngestionAttemptCompletion Map(MappingValidationResult result)
    {
        IngestionProvenance? provenance = null;
        try
        {
            Require(result is not null && result.Provenance is not null);
            provenance = Provenance(result!.Provenance);
            Require(Enum.IsDefined(result.Status) && result.Diagnostics is not null);
            Require(result.TotalIssueCount is >= 0 and <= 32768 && result.EmittedIssueCount >= 0 &&
                result.EmittedIssueCount <= result.TotalIssueCount);
            Require(result.IsValid is null || result.Diagnostics!.Count == result.EmittedIssueCount);
            if (result.Diagnostics!.Count > IngestionEvidenceDocument.MaximumDiagnostics)
                throw new EvidenceLimitException();
            Require(result.EmittedIssueCount <= IngestionEvidenceDocument.MaximumDiagnostics);
            var facts = new IngestionValidationFacts(Status(result.Status), provenance, result.IsValid,
                result.TotalIssueCount, result.EmittedIssueCount, result.Truncated);
            using var buffer = new EvidenceBuffer();
            using (var writer = new EvidenceWriter(buffer))
                WriteEvidence(writer, facts, result.Diagnostics);
            var evidence = IngestionEvidenceDocument.Restore(facts, buffer.ToArray());
            return IngestionAttemptCompletion.FromValidation(evidence);
        }
        catch (EvidenceLimitException)
        {
            return IngestionAttemptCompletion.FromOperationalFailure(IngestionFailureCode.EvidenceLimit, provenance);
        }
        catch (Exception error) when (error is MalformedModelException or ArgumentException or JsonException or
            EncoderFallbackException or InvalidOperationException or NotSupportedException or OverflowException)
        {
            // Constructor/parser failures are not proof that a policy budget was exceeded.
            return IngestionAttemptCompletion.FromOperationalFailure(IngestionFailureCode.ProtocolFailure);
        }
    }

    private static IngestionProvenance Provenance(MappingOperationProvenance value)
    {
        Require(value.Scope is not null && value.NativeDigest is not null);
        return new(value.Scope!, value.NativeDigest!, new IngestionBackendProfile(value.BackendId,
            value.EngineRevision, value.BackendBuild, value.AdapterBuild), value.Confirmed);
    }

    private static IngestionBackendStatus Status(MappingOperationStatus status) => status switch
    {
        MappingOperationStatus.Succeeded => IngestionBackendStatus.Succeeded,
        MappingOperationStatus.InvalidMapping => IngestionBackendStatus.InvalidMapping,
        MappingOperationStatus.UnsupportedArtifact => IngestionBackendStatus.UnsupportedArtifact,
        MappingOperationStatus.ResourceLimit => IngestionBackendStatus.ResourceLimit,
        MappingOperationStatus.ProtocolFailure => IngestionBackendStatus.ProtocolFailure,
        MappingOperationStatus.BackendUnavailable => IngestionBackendStatus.BackendUnavailable,
        MappingOperationStatus.BackendFailure => IngestionBackendStatus.BackendFailure,
        MappingOperationStatus.TimedOut => IngestionBackendStatus.TimedOut,
        MappingOperationStatus.Canceled => IngestionBackendStatus.Canceled,
        MappingOperationStatus.IsolationFailure => IngestionBackendStatus.IsolationFailure,
        _ => throw new MalformedModelException()
    };

    private static void WriteEvidence(EvidenceWriter writer, IngestionValidationFacts facts,
        IReadOnlyList<MappingDiagnostic> diagnostics)
    {
        writer.Object();
        writer.String("version", IngestionEvidenceDocument.SchemaVersion);
        writer.Property("scope"); writer.Object();
        writer.String("organizationId", facts.Provenance.Scope.OrganizationId.ToString("D"));
        writer.String("projectId", facts.Provenance.Scope.ProjectId.ToString("D"));
        writer.String("mappingId", facts.Provenance.Scope.MappingId.ToString("D"));
        writer.String("revisionId", facts.Provenance.Scope.RevisionId.ToString("D"));
        writer.EndObject();
        writer.String("nativeDigest", facts.Provenance.NativeDigest.Value);
        writer.Property("backendProfile"); writer.Object();
        writer.String("backendId", facts.Provenance.BackendProfile.BackendId);
        writer.String("engineRevision", facts.Provenance.BackendProfile.EngineRevision);
        writer.String("backendBuild", facts.Provenance.BackendProfile.BackendBuild);
        writer.String("adapterBuild", facts.Provenance.BackendProfile.AdapterBuild);
        writer.EndObject();
        writer.Boolean("confirmed", facts.Provenance.Confirmed);
        writer.String("status", IngestionPersistenceNames.BackendStatus(facts.Status));
        writer.Property("isValid");
        if (facts.IsValid is { } validity) writer.Boolean(validity); else writer.Null();
        writer.Number("totalIssueCount", facts.TotalIssueCount);
        writer.Number("emittedIssueCount", facts.EmittedIssueCount);
        writer.Boolean("truncated", facts.Truncated);
        writer.Property("diagnostics"); writer.Array();
        foreach (var diagnostic in diagnostics)
            WriteDiagnostic(writer, diagnostic);
        writer.EndArray(); writer.EndObject();
    }

    private static void WriteDiagnostic(EvidenceWriter writer, MappingDiagnostic diagnostic)
    {
        Require(diagnostic is not null);
        writer.Object();
        writer.String("code", diagnostic!.Code, nonempty: true);
        writer.String("severity", diagnostic.Severity switch
        {
            DiagnosticSeverity.Information => "information",
            DiagnosticSeverity.Warning => "warning",
            DiagnosticSeverity.Error => "error",
            _ => throw new MalformedModelException()
        });
        writer.String("message", diagnostic.Message);
        writer.Boolean("messageTruncated", diagnostic.MessageTruncated);
        writer.Property("nativeLocator");
        if (diagnostic.NativeLocator is { } locator) WriteLocator(writer, locator); else writer.Null();
        writer.Property("owner");
        if (diagnostic.Owner is { } owner) WriteOwner(writer, owner); else writer.Null();
        writer.NullableString("location", diagnostic.Location);
        writer.Boolean("locationTruncated", diagnostic.LocationTruncated);
        writer.EndObject();
    }

    private static void WriteLocator(EvidenceWriter writer, NativeLocator locator)
    {
        writer.Object();
        writer.String("kind", locator switch
        {
            DocumentNativeLocator => "document",
            JsonPointerNativeLocator => "jsonPointer",
            GraphNodeNativeLocator => "graphNode",
            TextSpanNativeLocator => "textSpan",
            OpaqueNativeLocator => "opaque",
            _ => throw new MalformedModelException()
        });
        writer.String("documentId", locator.DocumentId, nonempty: true);
        switch (locator)
        {
            case DocumentNativeLocator: break;
            case GraphNodeNativeLocator graph:
                writer.Decimal("nodeId", graph.NodeId);
                writer.NullableDecimal("functionId", graph.FunctionId);
                break;
            case JsonPointerNativeLocator pointer:
                Require(pointer.Pointer is not null && (pointer.Pointer.Length == 0 || pointer.Pointer[0] == '/'));
                for (var index = 0; index < pointer.Pointer!.Length; index++)
                    if (pointer.Pointer[index] == '~')
                        Require(++index < pointer.Pointer.Length && pointer.Pointer[index] is '0' or '1');
                writer.String("pointer", pointer.Pointer);
                break;
            case TextSpanNativeLocator span:
                writer.Decimal("startOffset", span.StartOffset); writer.Decimal("length", span.Length);
                writer.String("unit", span.Unit switch
                {
                    TextSpanUnit.Utf8Bytes => "utf8Bytes",
                    TextSpanUnit.Utf16CodeUnits => "utf16CodeUnits",
                    TextSpanUnit.UnicodeScalars => "unicodeScalars",
                    _ => throw new MalformedModelException()
                });
                break;
            case OpaqueNativeLocator opaque:
                writer.String("scheme", opaque.Scheme, nonempty: true); writer.String("value", opaque.Value); break;
        }
        writer.EndObject();
    }

    private static void WriteOwner(EvidenceWriter writer, MappingDiagnosticOwner owner)
    {
        writer.Object();
        writer.String("kind", owner switch
        {
            GraphNodeDiagnosticOwner => "graphNode",
            UserFunctionDiagnosticOwner => "userFunction",
            EndpointDiagnosticOwner => "endpoint",
            SchemaNodeDiagnosticOwner => "schemaNode",
            ScopeDiagnosticOwner => "scope",
            FailureRuleDiagnosticOwner => "failureRule",
            _ => throw new MalformedModelException()
        });
        switch (owner)
        {
            case GraphNodeDiagnosticOwner graph:
                writer.NullableDecimal("functionId", graph.FunctionId); writer.Decimal("nodeId", graph.NodeId); break;
            case UserFunctionDiagnosticOwner function: writer.Decimal("functionId", function.FunctionId); break;
            case EndpointDiagnosticOwner endpoint:
                writer.Property("endpoint"); WriteEndpoint(writer, endpoint.Endpoint); break;
            case FailureRuleDiagnosticOwner failure: writer.Decimal("index", failure.Index); break;
            case SchemaNodeDiagnosticOwner schema:
                writer.Property("endpoint"); WriteEndpoint(writer, schema.Endpoint);
                Require(schema.Path is not null);
                writer.Property("path"); writer.Array();
                foreach (var step in schema.Path!) WriteSchemaStep(writer, step);
                writer.EndArray(); break;
            case ScopeDiagnosticOwner scope:
                writer.Property("target"); WriteEndpoint(writer, scope.Target);
                Require(scope.Path is not null);
                writer.Property("path"); writer.Array();
                foreach (var step in scope.Path!) WriteScopeStep(writer, step);
                writer.EndArray(); break;
        }
        writer.EndObject();
    }

    private static void WriteEndpoint(EvidenceWriter writer, MappingEndpoint endpoint)
    {
        Require(endpoint is not null);
        writer.Object();
        writer.String("kind", endpoint switch
        {
            SourceMappingEndpoint => "source",
            TargetMappingEndpoint => "target",
            NamedSourceMappingEndpoint => "namedSource",
            NamedTargetMappingEndpoint => "namedTarget",
            _ => throw new MalformedModelException()
        });
        switch (endpoint)
        {
            case NamedSourceMappingEndpoint source:
                writer.Decimal("index", source.Index); writer.String("name", source.Name); break;
            case NamedTargetMappingEndpoint target:
                writer.Decimal("index", target.Index); writer.String("name", target.Name); break;
        }
        writer.EndObject();
    }

    private static void WriteSchemaStep(EvidenceWriter writer, SchemaOwnerStep step)
    {
        writer.Object();
        writer.String("kind", step switch
        {
            ChildSchemaOwnerStep => "child",
            DynamicFieldSchemaOwnerStep => "dynamicField",
            ContainsSchemaOwnerStep => "contains",
            DependentSchemaOwnerStep => "dependentSchema",
            _ => throw new MalformedModelException()
        });
        switch (step)
        {
            case ChildSchemaOwnerStep child: writer.Decimal("index", child.Index); break;
            case ContainsSchemaOwnerStep contains: writer.Decimal("index", contains.Index); break;
            case DependentSchemaOwnerStep dependent: writer.Decimal("index", dependent.Index); break;
        }
        writer.EndObject();
    }

    private static void WriteScopeStep(EvidenceWriter writer, ScopeOwnerStep step)
    {
        writer.Object();
        writer.String("kind", step switch
        {
            ChildScopeOwnerStep => "child",
            DynamicChildScopeOwnerStep => "dynamicChild",
            SegmentScopeOwnerStep => "segment",
            _ => throw new MalformedModelException()
        });
        writer.Decimal("index", step switch
        {
            ChildScopeOwnerStep child => child.Index,
            DynamicChildScopeOwnerStep dynamicChild => dynamicChild.Index,
            SegmentScopeOwnerStep segment => segment.Index,
            _ => throw new MalformedModelException()
        });
        writer.EndObject();
    }

    private static void Require([DoesNotReturnIf(false)] bool condition)
    {
        if (!condition) throw new MalformedModelException();
    }

    private sealed class MalformedModelException : Exception { }
    private sealed class EvidenceLimitException : Exception { }

    private sealed class EvidenceWriter(EvidenceBuffer buffer) : IDisposable
    {
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);
        private readonly Utf8JsonWriter _writer = new(buffer);
        private int _nodes;
        private int _containers;

        public void Object() { Container(); _writer.WriteStartObject(); }
        public void Array() { Container(); _writer.WriteStartArray(); }
        public void EndObject() { _writer.WriteEndObject(); _containers--; }
        public void EndArray() { _writer.WriteEndArray(); _containers--; }
        public void Property(string name) { Text(name, false); _writer.WritePropertyName(name); }
        public void Null() { Node(); _writer.WriteNullValue(); }
        public void Boolean(bool value) { Node(); _writer.WriteBooleanValue(value); }
        public void Boolean(string name, bool value) { Property(name); Boolean(value); }
        public void Number(string name, int value) { Property(name); Node(); _writer.WriteNumberValue(value); }
        public void String(string name, string value, bool nonempty = false)
        {
            Property(name); Text(value, nonempty); Node(); _writer.WriteStringValue(value);
        }
        public void NullableString(string name, string? value)
        {
            if (value is null) { Property(name); Null(); } else String(name, value);
        }
        public void Decimal(string name, string value)
        {
            Require(value is not null && value.Length > 0 && (value == "0" || value[0] != '0') && value.All(char.IsAsciiDigit));
            String(name, value!);
        }
        public void NullableDecimal(string name, string? value)
        {
            if (value is null) { Property(name); Null(); } else Decimal(name, value);
        }
        private static void Text(string value, bool nonempty)
        {
            Require(value is not null && !value.Contains('\0') && (!nonempty || !string.IsNullOrWhiteSpace(value)));
            if (StrictUtf8.GetByteCount(value!) > IngestionEvidenceDocument.MaximumStringBytes)
                throw new EvidenceLimitException();
        }
        private void Node()
        {
            if (++_nodes > IngestionEvidenceDocument.MaximumNodes)
                throw new EvidenceLimitException();
        }
        private void Container()
        {
            if (++_containers > IngestionEvidenceDocument.MaximumDepth)
                throw new EvidenceLimitException();
            Node();
        }
        public void Dispose() => _writer.Dispose();
    }

    private sealed class EvidenceBuffer : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            RequireSpace(count); base.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            RequireSpace(buffer.Length); base.Write(buffer);
        }
        private void RequireSpace(int count)
        {
            if (Length + count > IngestionEvidenceDocument.MaximumUtf8Bytes)
                throw new EvidenceLimitException();
        }
    }
}
