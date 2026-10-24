using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Weft.Contracts.Projections;
using Weft.Domain;
using Weft.MappingBackends.Ferrule;
using Weft.MappingBackends.Workers;

namespace Weft.MappingBackends;

public sealed partial class FerruleInspectionBackend(IInspectionWorkerRunner runner) : IMappingInspectionBackend, IMappingProjectionBackend, Weft.Application.IMappingEditBackend
{
    public const string ProtocolVersion = "weft.ferrule-host.v1";
    public const string EngineRevision = "576012aa17c403d9c3fcb8659ed4d3a47ba06864";
    public const string AdapterBuild = "weft.ferrule-adapter.v3";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = CreateOptions();
    private readonly IInspectionWorkerRunner _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    private readonly string _backendBuild = TrustedBuildIdentity(runner);

    public async Task<MappingInspectionResult> InspectAsync(NativeArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var invocation = await InvokeAsync(artifact, "inspect", cancellationToken).ConfigureAwait(false);
        if (invocation.Response?.Result is HostInspectionResult inspection)
        {
            return new MappingInspectionResult(MappingOperationStatus.Succeeded, Provenance(artifact, true),
                new MappingInspectionData(
                    inspection.Boundaries.Select(boundary => new MappingBoundaryDescription(boundary.Id, Role(boundary.Role), boundary.Name,
                        boundary.NameTruncated, boundary.RootName, boundary.RootNameTruncated, Shape(boundary.Shape), boundary.Repeating, boundary.NativeLocator)).ToArray(),
                    new MappingInspectionCounts((int)inspection.Counts.SchemaNodes, (int)inspection.Counts.GraphNodes, (int)inspection.Counts.ScopeNodes,
                        (int)inspection.Counts.UserFunctions, (int)inspection.Counts.FailureRules, (int)inspection.Counts.TotalItems),
                    inspection.UnresolvedResources.Select(resource => new UnresolvedMappingResource(resource.BoundaryId, ResourceKind(resource.Kind), resource.NativeLocator)).ToArray(),
                    inspection.Capabilities.ToArray(), inspection.Truncated), []);
        }

        return new MappingInspectionResult(invocation.Status, Provenance(artifact, invocation.Confirmed), null, [invocation.Diagnostic!]);
    }

    public async Task<MappingValidationResult> ValidateAsync(NativeArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var invocation = await InvokeAsync(artifact, "validate", cancellationToken).ConfigureAwait(false);
        if (invocation.Response?.Result is HostValidationResult validation)
        {
            var diagnostics = validation.Issues.Select(issue => new MappingDiagnostic(issue.Code, DiagnosticSeverity.Error, issue.Message,
                issue.MessageTruncated, issue.NativeLocator, issue.Owner, issue.Location, issue.LocationTruncated)).ToArray();
            return new MappingValidationResult(validation.IsValid ? MappingOperationStatus.Succeeded : MappingOperationStatus.InvalidMapping,
                Provenance(artifact, true), validation.IsValid, (int)validation.TotalIssueCount, (int)validation.EmittedIssueCount,
                validation.Truncated, diagnostics);
        }

        return new MappingValidationResult(invocation.Status, Provenance(artifact, invocation.Confirmed), null, 0, 0, false, [invocation.Diagnostic!]);
    }

    private async Task<Invocation> InvokeAsync(NativeArtifact artifact, string operation, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return LocalFailure(MappingOperationStatus.Canceled);
        if (artifact.ByteCount > InspectionPolicy.MaximumNativeBytes)
            return LocalFailure(MappingOperationStatus.ResourceLimit);

        var request = new HostRequest(ProtocolVersion, Guid.NewGuid().ToString("D"), operation,
            new HostRevision(artifact.Scope.OrganizationId.ToString("D"), artifact.Scope.ProjectId.ToString("D"),
                artifact.Scope.MappingId.ToString("D"), artifact.Scope.RevisionId.ToString("D")),
            artifact.Digest.Value, InspectionPolicy.DocumentId,
            new HostLimits(InspectionPolicy.MaximumDiagnostics, InspectionPolicy.MaximumItems, InspectionPolicy.MaximumTextBytes));
        WorkerProcessResult process;
        try
        {
            process = await _runner.RunAsync(FrameRequest(request, artifact), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return LocalFailure(MappingOperationStatus.Canceled);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            return LocalFailure(MappingOperationStatus.BackendFailure);
        }

        if (process.Failure != WorkerProcessFailure.None)
            return LocalFailure(ProcessStatus(process.Failure));
        try
        {
            var response = DecodeResponse(process.Output, request);
            if (response.Failure is { } failure)
            {
                var status = FailureStatus(failure.Code);
                return new Invocation(null, status, true,
                    new MappingDiagnostic("ferrule." + failure.Code, DiagnosticSeverity.Error, failure.Message, failure.MessageTruncated, failure.NativeLocator));
            }
            return new Invocation(response, MappingOperationStatus.Succeeded, true, null);
        }
        catch (ProjectionLimitException)
        {
            return LocalFailure(MappingOperationStatus.ResourceLimit);
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or EncoderFallbackException or ProtocolException or NotSupportedException)
        {
            return LocalFailure(MappingOperationStatus.ProtocolFailure);
        }
    }

    private static byte[] FrameRequest(HostRequest request, NativeArtifact artifact)
    {
        var header = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);
        if (header.Length > InspectionPolicy.MaximumHeaderBytes)
            throw new InvalidOperationException("The fixed inspection header exceeds its bound.");
        var native = artifact.CopyBytes();
        var frame = new byte[8 + header.Length + native.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)header.Length);
        header.CopyTo(frame.AsSpan(4));
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4 + header.Length), (uint)native.Length);
        native.CopyTo(frame.AsSpan(8 + header.Length));
        return frame;
    }

    private static HostResponse DecodeResponse(byte[] output, HostRequest request)
    {
        Require(output is not null && output.Length >= 4);
        var length = BinaryPrimitives.ReadUInt32BigEndian(output);
        var isProjection = request.Operation == "project";
        if (isProjection && length > InspectionPolicy.MaximumResponseBytes)
            throw new ProjectionLimitException();
        Require(length is > 0 and <= InspectionPolicy.MaximumResponseBytes);
        Require(output.Length == 4L + length);
        PreflightJson(output.AsSpan(4), isProjection);
        var json = StrictUtf8.GetString(output, 4, (int)length);
        var response = JsonSerializer.Deserialize<HostResponse>(json, JsonOptions) ?? throw new ProtocolException();
        Require(response.ProtocolVersion == ProtocolVersion && response.RequestId == request.RequestId && response.Operation == request.Operation);
        Require(response.Revision == request.Revision && response.NativeDigest == request.NativeDigest && response.DocumentId == request.DocumentId);
        Require(response.EngineRevision == EngineRevision);
        Require((response.Result is null) != (response.Failure is null));
        if (response.Failure is { } failure)
        {
            _ = FailureStatus(failure.Code);
            Text(failure.Message);
            if (failure.NativeLocator is not null)
                Locator(failure.NativeLocator);
        }
        else if (request.Operation == "inspect" && response.Result is HostInspectionResult inspection)
            ValidateInspection(inspection);
        else if (request.Operation == "validate" && response.Result is HostValidationResult validation)
            ValidateValidation(validation);
        else if (isProjection && response.Result is HostProjectionResult projection)
            ValidateProjection(projection);
        else
            throw new ProtocolException();
        return response;
    }

    private static void PreflightJson(ReadOnlySpan<byte> json, bool isProjection)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = isProjection ? int.MaxValue : InspectionPolicy.MaximumJsonDepth });
        var containers = new Stack<HashSet<string>?>();
        var arrayItems = 0;
        while (reader.Read())
        {
            if (reader.TokenType is not (JsonTokenType.EndObject or JsonTokenType.EndArray or JsonTokenType.PropertyName)
                && containers.Count > 0 && containers.Peek() is null)
            {
                arrayItems++;
                if (isProjection && arrayItems > InspectionPolicy.MaximumResponseItems)
                    throw new ProjectionLimitException();
                Require(arrayItems <= InspectionPolicy.MaximumResponseItems);
            }
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    if (isProjection && containers.Count >= InspectionPolicy.MaximumJsonDepth)
                        throw new ProjectionLimitException();
                    containers.Push(new HashSet<string>(StringComparer.Ordinal));
                    break;
                case JsonTokenType.StartArray:
                    if (isProjection && containers.Count >= InspectionPolicy.MaximumJsonDepth)
                        throw new ProjectionLimitException();
                    containers.Push(null);
                    break;
                case JsonTokenType.EndObject or JsonTokenType.EndArray:
                    containers.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    var properties = containers.Peek() ?? throw new ProtocolException();
                    Require(reader.ValueSpan.Length <= 128);
                    Require(properties.Count < 32 && properties.Add(reader.GetString()!));
                    break;
            }
        }
    }

    private static void ValidateInspection(HostInspectionResult result)
    {
        List(result.Boundaries); List(result.UnresolvedResources); List(result.Capabilities);
        Require(result.Counts is not null && result.Counts.TotalItems <= InspectionPolicy.MaximumItems);
        var minimumItems = (ulong)result.Counts.SchemaNodes + result.Counts.GraphNodes + result.Counts.ScopeNodes
            + result.Counts.UserFunctions + result.Counts.FailureRules + (uint)result.Boundaries.Count + (uint)result.UnresolvedResources.Count;
        Require(minimumItems <= result.Counts.TotalItems);
        var boundaries = new Dictionary<string, BoundaryRole>(StringComparer.Ordinal);
        var previousRole = -1;
        foreach (var boundary in result.Boundaries)
        {
            Require(boundary is not null);
            var role = Role(boundary.Role);
            Text(boundary.Id, false); Text(boundary.Name); Text(boundary.RootName);
            _ = Shape(boundary.Shape);
            Locator(boundary.NativeLocator);
            Require(boundaries.TryAdd(boundary.Id, role));
            Require(BoundaryIdMatchesRole(boundary.Id, role));
            var rank = role switch { BoundaryRole.PrimaryInput => 0, BoundaryRole.NamedInput => 1, BoundaryRole.PrimaryOutput => 2, BoundaryRole.NamedOutput => 3, _ => -1 };
            Require(rank >= previousRole);
            previousRole = rank;
        }
        Require(boundaries.Values.Count(role => role == BoundaryRole.PrimaryInput) == 1);
        Require(boundaries.Values.Count(role => role == BoundaryRole.PrimaryOutput) == 1);
        foreach (var resource in result.UnresolvedResources)
        {
            Require(resource is not null && boundaries.ContainsKey(resource.BoundaryId));
            _ = ResourceKind(resource.Kind);
            Require(resource.Resolution == "unresolved");
            Locator(resource.NativeLocator);
        }
        Require(result.Truncated || !result.Boundaries.Any(boundary => boundary.NameTruncated || boundary.RootNameTruncated));
        Require(result.Capabilities.Count == Enum.GetValues<MappingOperation>().Length);
        var operations = new HashSet<MappingOperation>();
        foreach (var capability in result.Capabilities)
        {
            Require(capability is not null && Enum.IsDefined(capability.Operation) && operations.Add(capability.Operation));
            Require(capability.Status == (capability.Operation is MappingOperation.Inspect or MappingOperation.Validate or MappingOperation.Project ? CapabilityStatus.Supported : CapabilityStatus.NotQualified));
            Text(capability.Profile, false); List(capability.BoundaryIds); List(capability.Conditions);
            var outputIds = result.Boundaries.Where(boundary => Role(boundary.Role) is BoundaryRole.PrimaryOutput or BoundaryRole.NamedOutput).Select(boundary => boundary.Id);
            Require(capability.BoundaryIds.SequenceEqual(capability.Operation is MappingOperation.Preview or MappingOperation.Build ? outputIds : [], StringComparer.Ordinal));
            Require(capability.Conditions.Count > 0);
            foreach (var condition in capability.Conditions)
            {
                Require(condition is not null);
                Text(condition.Code, false); Text(condition.Description, false);
            }
            if (capability.Operation == MappingOperation.Project)
                Require(capability.Profile == "ferrule.projection.v1" && capability.Conditions.Any(condition => condition.Code == "boundedReadOnlyProjection"));
        }
    }

    private static void ValidateValidation(HostValidationResult result)
    {
        List(result.Issues, InspectionPolicy.MaximumDiagnostics);
        Require(result.TotalIssueCount <= InspectionPolicy.MaximumValidationIssues && result.EmittedIssueCount == result.Issues.Count);
        Require(result.EmittedIssueCount <= result.TotalIssueCount && result.IsValid == (result.TotalIssueCount == 0));
        var anyTextTruncated = false;
        var ownerItems = result.Issues.Count;
        foreach (var issue in result.Issues)
        {
            Require(issue is not null && issue.Code == "ferrule.validation");
            Text(issue.Location); Text(issue.Message, false); Locator(issue.NativeLocator);
            if (issue.Owner is not null)
                ownerItems += Owner(issue.Owner);
            Require(ownerItems <= InspectionPolicy.MaximumItems);
            if (issue.Owner is GraphNodeDiagnosticOwner graph)
                Require(issue.NativeLocator is GraphNodeNativeLocator locator && locator.NodeId == graph.NodeId && locator.FunctionId == graph.FunctionId);
            anyTextTruncated |= issue.LocationTruncated || issue.MessageTruncated;
        }
        Require(result.Truncated == (result.EmittedIssueCount < result.TotalIssueCount || anyTextTruncated));
    }

    private static int Owner(MappingDiagnosticOwner owner)
    {
        switch (owner)
        {
            case GraphNodeDiagnosticOwner graph: DecimalId(graph.NodeId); if (graph.FunctionId is not null) DecimalId(graph.FunctionId); return 1;
            case UserFunctionDiagnosticOwner function: DecimalId(function.FunctionId); return 1;
            case EndpointDiagnosticOwner endpoint: Endpoint(endpoint.Endpoint); return 1;
            case FailureRuleDiagnosticOwner failure: DecimalId(failure.Index); return 1;
            case SchemaNodeDiagnosticOwner schema:
                Endpoint(schema.Endpoint); List(schema.Path);
                foreach (var step in schema.Path)
                {
                    switch (step)
                    {
                        case ChildSchemaOwnerStep child: DecimalId(child.Index); break;
                        case ContainsSchemaOwnerStep contains: DecimalId(contains.Index); break;
                        case DependentSchemaOwnerStep dependent: DecimalId(dependent.Index); break;
                        case DynamicFieldSchemaOwnerStep: break;
                        default: throw new ProtocolException();
                    }
                }
                return 1 + schema.Path.Count;
            case ScopeDiagnosticOwner scope:
                Endpoint(scope.Target); List(scope.Path);
                foreach (var step in scope.Path)
                {
                    switch (step)
                    {
                        case ChildScopeOwnerStep child: DecimalId(child.Index); break;
                        case DynamicChildScopeOwnerStep dynamicStep: DecimalId(dynamicStep.Index); break;
                        case SegmentScopeOwnerStep segment: DecimalId(segment.Index); break;
                        default: throw new ProtocolException();
                    }
                }
                return 1 + scope.Path.Count;
            default: throw new ProtocolException();
        }
    }

    private static void Endpoint(MappingEndpoint endpoint)
    {
        switch (endpoint)
        {
            case SourceMappingEndpoint or TargetMappingEndpoint: break;
            case NamedSourceMappingEndpoint source: DecimalId(source.Index); Text(source.Name); break;
            case NamedTargetMappingEndpoint target: DecimalId(target.Index); Text(target.Name); break;
            default: throw new ProtocolException();
        }
    }

    private static void Locator(NativeLocator locator)
    {
        Require(locator is not null && locator.DocumentId == InspectionPolicy.DocumentId);
        switch (locator)
        {
            case DocumentNativeLocator: break;
            case GraphNodeNativeLocator graph: DecimalId(graph.NodeId); if (graph.FunctionId is not null) DecimalId(graph.FunctionId); break;
            case JsonPointerNativeLocator pointer:
                Text(pointer.Pointer);
                Require(pointer.Pointer.Length == 0 || pointer.Pointer[0] == '/');
                for (var index = 0; index < pointer.Pointer.Length; index++)
                    if (pointer.Pointer[index] == '~') Require(++index < pointer.Pointer.Length && pointer.Pointer[index] is '0' or '1');
                break;
            case TextSpanNativeLocator span: DecimalId(span.StartOffset); DecimalId(span.Length); Require(Enum.IsDefined(span.Unit)); break;
            case OpaqueNativeLocator opaque: Text(opaque.Scheme, false); Text(opaque.Value); break;
            default: throw new ProtocolException();
        }
    }

    private static void Text(string value, bool allowEmpty = true) =>
        Require(value is not null && (allowEmpty || !string.IsNullOrWhiteSpace(value)) && StrictUtf8.GetByteCount(value) <= InspectionPolicy.MaximumTextBytes);

    private static void List<T>(IReadOnlyList<T> values, int maximum = InspectionPolicy.MaximumItems) => Require(values is not null && values.Count <= maximum);

    private static void DecimalId(string value)
    {
        Text(value, false);
        Require(value.Length > 0 && (value == "0" || value[0] != '0') && value.All(char.IsAsciiDigit));
    }

    private static bool BoundaryIdMatchesRole(string id, BoundaryRole role)
    {
        if (role == BoundaryRole.PrimaryInput) return id == "primary-input";
        if (role == BoundaryRole.PrimaryOutput) return id == "primary-output";
        var prefix = role == BoundaryRole.NamedInput ? "named-input:" : "named-output:";
        if (!id.StartsWith(prefix, StringComparison.Ordinal)) return false;
        DecimalId(id[prefix.Length..]);
        return true;
    }

    private static BoundaryRole Role(string role) => role switch
    {
        "primaryInput" => BoundaryRole.PrimaryInput,
        "namedInput" => BoundaryRole.NamedInput,
        "primaryOutput" => BoundaryRole.PrimaryOutput,
        "namedOutput" => BoundaryRole.NamedOutput,
        _ => throw new ProtocolException()
    };

    private static InspectionBoundaryShape Shape(string shape) => shape switch
    {
        "scalar" => InspectionBoundaryShape.Scalar,
        "scalarUnion" => InspectionBoundaryShape.ScalarUnion,
        "object" => InspectionBoundaryShape.Object,
        _ => throw new ProtocolException()
    };

    private static UnresolvedResourceKind ResourceKind(string kind) => kind switch
    {
        "designTimeInput" => UnresolvedResourceKind.DesignTimeInput,
        "designTimeOutput" => UnresolvedResourceKind.DesignTimeOutput,
        "dynamicInput" => UnresolvedResourceKind.DynamicInput,
        "dynamicOutput" => UnresolvedResourceKind.DynamicOutput,
        "ediConfiguration" => UnresolvedResourceKind.EdiConfiguration,
        "externalSource" => UnresolvedResourceKind.ExternalSource,
        "xmlSchemaHint" => UnresolvedResourceKind.XmlSchemaHint,
        "schemaReference" => UnresolvedResourceKind.SchemaReference,
        _ => throw new ProtocolException()
    };

    private static MappingOperationStatus FailureStatus(string code) => code switch
    {
        "nativeDecodeFailed" or "digestMismatch" => MappingOperationStatus.InvalidMapping,
        "unsupportedNativeVersion" => MappingOperationStatus.UnsupportedArtifact,
        "inspectionLimitExceeded" or "resultLimitExceeded" => MappingOperationStatus.ResourceLimit,
        "invalidFrame" or "invalidHeader" or "unsupportedProtocolVersion" or "unsupportedOperation" or "invalidContext" or "invalidLimits" => MappingOperationStatus.ProtocolFailure,
        _ => throw new ProtocolException()
    };

    private static MappingOperationStatus ProcessStatus(WorkerProcessFailure failure) => failure switch
    {
        WorkerProcessFailure.Canceled => MappingOperationStatus.Canceled,
        WorkerProcessFailure.TimedOut => MappingOperationStatus.TimedOut,
        WorkerProcessFailure.StartFailed or WorkerProcessFailure.SandboxUnavailable => MappingOperationStatus.BackendUnavailable,
        WorkerProcessFailure.CleanupFailed => MappingOperationStatus.IsolationFailure,
        WorkerProcessFailure.OutputLimitExceeded => MappingOperationStatus.ResourceLimit,
        _ => MappingOperationStatus.BackendFailure
    };

    private MappingOperationProvenance Provenance(NativeArtifact artifact, bool confirmed) =>
        new(artifact.Scope, artifact.Digest, "ferrule", EngineRevision, _backendBuild, AdapterBuild, confirmed);

    private static string TrustedBuildIdentity(IInspectionWorkerRunner worker)
    {
        ArgumentNullException.ThrowIfNull(worker);
        var identity = worker.BuildIdentity;
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        if (StrictUtf8.GetByteCount(identity) > InspectionPolicy.MaximumTextBytes)
            throw new ArgumentException("The trusted worker build identity exceeds its bound.", nameof(worker));
        return identity;
    }

    private static Invocation LocalFailure(MappingOperationStatus status)
    {
        var (code, message) = status switch
        {
            MappingOperationStatus.ResourceLimit => ("mapping.inspection.limit", "Inspection exceeded the server resource policy."),
            MappingOperationStatus.Canceled => ("mapping.inspection.canceled", "Inspection was canceled."),
            MappingOperationStatus.TimedOut => ("mapping.inspection.timeout", "Inspection exceeded its deadline."),
            MappingOperationStatus.ProtocolFailure => ("mapping.inspection.protocol", "The inspection worker returned an invalid bounded response."),
            MappingOperationStatus.IsolationFailure => ("mapping.inspection.cleanup", "Worker cleanup could not be confirmed; further worker admission is blocked."),
            MappingOperationStatus.BackendUnavailable => ("mapping.inspection.unavailable", "The configured isolated inspection worker is unavailable."),
            _ => ("mapping.inspection.failure", "The isolated inspection worker failed.")
        };
        return new Invocation(null, status, false, new MappingDiagnostic(code, DiagnosticSeverity.Error, message, false));
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = ProjectionJson.CreateOptions();
        options.PropertyNameCaseInsensitive = false;
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        options.MaxDepth = InspectionPolicy.MaximumJsonDepth;
        return options;
    }

    private static void Require([DoesNotReturnIf(false)] bool condition)
    {
        if (!condition) throw new ProtocolException();
    }

    private sealed class ProtocolException : Exception { }
    private sealed record Invocation(HostResponse? Response, MappingOperationStatus Status, bool Confirmed, MappingDiagnostic? Diagnostic);
}
