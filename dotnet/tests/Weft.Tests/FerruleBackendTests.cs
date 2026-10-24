using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using Weft.Contracts.Projections;
using Weft.Domain;
using Weft.MappingBackends;
using Weft.MappingBackends.Workers;

namespace Weft.Tests;

/// <summary>Protocol transcripts qualify the adapter; isolated process qualification is separate.</summary>
public sealed class FerruleBackendTests
{
    private static readonly RevisionScope Scope = new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Guid.Parse("33333333-3333-3333-3333-333333333333"),
        Guid.Parse("44444444-4444-4444-4444-444444444444"));

    [Theory]
    [InlineData("inspect")]
    [InlineData("validate")]
    public async Task RequestFramesPreserveExactNativeBytesAndServerOwnedContext(string operation)
    {
        var native = Encoding.UTF8.GetBytes("\uFEFF{\n  \"unknown\": \"a/b~c\", \"number\": 9007199254740993, \"bits\": \"8000000000000000\"\n}\n");
        var artifact = Artifact(native);
        var runner = ReplyRunner();
        var backend = new FerruleInspectionBackend(runner);
        using var cancellation = new CancellationTokenSource();

        if (operation == "inspect")
            Assert.Equal(MappingOperationStatus.Succeeded, (await backend.InspectAsync(artifact, cancellation.Token)).Status);
        else
            Assert.Equal(MappingOperationStatus.Succeeded, (await backend.ValidateAsync(artifact, cancellation.Token)).Status);

        Assert.Equal(1, runner.Calls);
        Assert.Equal(cancellation.Token, runner.LastCancellationToken);
        var frame = Assert.IsType<byte[]>(runner.LastRequest);
        var headerLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(frame));
        Assert.InRange(headerLength, 1, InspectionPolicy.MaximumHeaderBytes);
        var header = RequestHeader(frame);
        Assert.Equal(
            new[] { "documentId", "limits", "nativeDigest", "operation", "protocolVersion", "requestId", "revision" },
            header.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        Assert.Equal(FerruleInspectionBackend.ProtocolVersion, header["protocolVersion"]!.GetValue<string>());
        Assert.Equal(operation, header["operation"]!.GetValue<string>());
        var requestId = header["requestId"]!.GetValue<string>();
        Assert.True(Guid.TryParseExact(requestId, "D", out var id));
        Assert.NotEqual(Guid.Empty, id);
        Assert.Equal(id.ToString("D"), requestId);
        Assert.Equal(artifact.Digest.Value, header["nativeDigest"]!.GetValue<string>());
        Assert.Equal(InspectionPolicy.DocumentId, header["documentId"]!.GetValue<string>());
        var revision = header["revision"]!.AsObject();
        Assert.Equal(Scope.OrganizationId.ToString("D"), revision["organizationId"]!.GetValue<string>());
        Assert.Equal(Scope.ProjectId.ToString("D"), revision["projectId"]!.GetValue<string>());
        Assert.Equal(Scope.MappingId.ToString("D"), revision["mappingId"]!.GetValue<string>());
        Assert.Equal(Scope.RevisionId.ToString("D"), revision["revisionId"]!.GetValue<string>());
        var limits = header["limits"]!.AsObject();
        Assert.Equal(3, limits.Count);
        Assert.Equal(128, limits["maximumDiagnostics"]!.GetValue<int>());
        Assert.Equal(8192, limits["maximumItems"]!.GetValue<int>());
        Assert.Equal(2048, limits["maximumTextBytes"]!.GetValue<int>());
        var nativeOffset = 8 + headerLength;
        Assert.Equal((uint)native.Length, BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(4 + headerLength)));
        Assert.Equal(nativeOffset + native.Length, frame.Length);
        Assert.Equal(native, frame[nativeOffset..]);
        Assert.Equal(native, artifact.CopyBytes());
    }

    [Fact]
    public async Task NativeAdmissionAcceptsThePolicyCeilingAndRejectsTheNextByteBeforeStartingAWorker()
    {
        var atLimit = new byte[InspectionPolicy.MaximumNativeBytes];
        atLimit[^1] = 0x7f;
        var acceptedRunner = ReplyRunner();
        var accepted = await new FerruleInspectionBackend(acceptedRunner).InspectAsync(Artifact(atLimit));
        Assert.Equal(MappingOperationStatus.Succeeded, accepted.Status);
        Assert.Equal(1, acceptedRunner.Calls);
        Assert.Equal(atLimit, RequestNativeBytes(acceptedRunner.LastRequest!));

        var overLimit = new byte[InspectionPolicy.MaximumNativeBytes + 1];
        var rejectedArtifact = Artifact(overLimit);
        var rejectedRunner = ReplyRunner();
        var backend = new FerruleInspectionBackend(rejectedRunner);
        AssertLocalFailure(await backend.InspectAsync(rejectedArtifact), MappingOperationStatus.ResourceLimit, rejectedArtifact);
        AssertLocalFailure(await backend.ValidateAsync(rejectedArtifact), MappingOperationStatus.ResourceLimit, rejectedArtifact);
        Assert.Equal(0, rejectedRunner.Calls);
    }

    [Fact]
    public async Task PreCanceledOperationsDoNotAdmitAWorker()
    {
        var runner = ReplyRunner();
        var backend = new FerruleInspectionBackend(runner);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        AssertLocalFailure(await backend.InspectAsync(Artifact(), cancellation.Token), MappingOperationStatus.Canceled);
        AssertLocalFailure(await backend.ValidateAsync(Artifact(), cancellation.Token), MappingOperationStatus.Canceled);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task InspectionPreservesBoundaryOrderCountsResourcesAndNonExecutingCapabilities()
    {
        var artifact = Artifact();
        var result = await new FerruleInspectionBackend(ReplyRunner()).InspectAsync(artifact);

        Assert.Equal(MappingOperationStatus.Succeeded, result.Status);
        AssertProvenance(result.Provenance, artifact, true);
        Assert.Empty(result.Diagnostics);
        var inspection = Assert.IsType<MappingInspectionData>(result.Inspection);
        Assert.Equal(new MappingInspectionCounts(4, 2, 1, 1, 1, 14), inspection.Counts);
        Assert.Equal(new[] { "primary-input", "named-input:0", "primary-output", "named-output:0" }, inspection.Boundaries.Select(boundary => boundary.Id));
        Assert.Equal(new[] { BoundaryRole.PrimaryInput, BoundaryRole.NamedInput, BoundaryRole.PrimaryOutput, BoundaryRole.NamedOutput }, inspection.Boundaries.Select(boundary => boundary.Role));
        Assert.Equal(new[] { InspectionBoundaryShape.Object, InspectionBoundaryShape.ScalarUnion, InspectionBoundaryShape.Scalar, InspectionBoundaryShape.Object }, inspection.Boundaries.Select(boundary => boundary.Shape));
        Assert.True(inspection.Boundaries[3].Repeating);
        Assert.Equal("other/a~b.name", inspection.Boundaries[3].Name);
        Assert.All(inspection.Boundaries, boundary => Assert.Equal(new DocumentNativeLocator(InspectionPolicy.DocumentId), boundary.NativeLocator));
        var resource = Assert.Single(inspection.UnresolvedResources);
        Assert.Equal("named-input:0", resource.BoundaryId);
        Assert.Equal(UnresolvedResourceKind.DesignTimeInput, resource.Kind);
        Assert.Equal(new DocumentNativeLocator(InspectionPolicy.DocumentId), resource.NativeLocator);
        Assert.Equal(new[] { MappingOperation.Inspect, MappingOperation.Validate, MappingOperation.Project, MappingOperation.Preview, MappingOperation.Build }, inspection.Capabilities.Select(capability => capability.Operation));
        Assert.All(inspection.Capabilities, capability =>
        {
            var supported = capability.Operation is MappingOperation.Inspect or MappingOperation.Validate or MappingOperation.Project;
            Assert.Equal(supported ? CapabilityStatus.Supported : CapabilityStatus.NotQualified, capability.Status);
            Assert.Equal(capability.Operation == MappingOperation.Project ? "boundedReadOnlyProjection" : supported ? "readOnlyInspection" : "operationNotImplemented", Assert.Single(capability.Conditions).Code);
            Assert.Equal(capability.Operation is MappingOperation.Preview or MappingOperation.Build ? new[] { "primary-output", "named-output:0" } : [], capability.BoundaryIds);
        });
        Assert.False(inspection.Truncated);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("nullOutput")]
    [InlineData("shortPrefix")]
    [InlineData("zeroLength")]
    [InlineData("oversizedLength")]
    [InlineData("truncatedPayload")]
    [InlineData("wrongLength")]
    [InlineData("trailingBytes")]
    [InlineData("twoFrames")]
    [InlineData("plainJson")]
    [InlineData("invalidUtf8")]
    [InlineData("syntax")]
    [InlineData("nullJson")]
    [InlineData("arrayJson")]
    public async Task MalformedResponseFramesAreRejectedWithoutInterpretingUntrustedText(string corruption)
    {
        var runner = new RecordingRunner((request, _) =>
        {
            var valid = Frame(Response(RequestHeader(request.Span)));
            byte[] output = corruption switch
            {
                "empty" => [],
                "nullOutput" => null!,
                "shortPrefix" => [0, 0, 0],
                "zeroLength" => [0, 0, 0, 0],
                "oversizedLength" => LengthPrefix(InspectionPolicy.MaximumResponseBytes + 1),
                "truncatedPayload" => valid[..^1],
                "wrongLength" => WithLength(valid, valid.Length),
                "trailingBytes" => [.. valid, 0],
                "twoFrames" => [.. valid, .. valid],
                "plainJson" => Encoding.UTF8.GetBytes("{\"secret\":\"host-path\"}"),
                "invalidUtf8" => Frame([0xff]),
                "syntax" => Frame(Encoding.UTF8.GetBytes("{host-path")),
                "nullJson" => Frame("null"u8.ToArray()),
                "arrayJson" => Frame("[]"u8.ToArray()),
                _ => throw new ArgumentOutOfRangeException(nameof(corruption))
            };
            return Task.FromResult(new WorkerProcessResult(output, WorkerProcessFailure.None));
        });

        AssertLocalFailure(await new FerruleInspectionBackend(runner).InspectAsync(Artifact()), MappingOperationStatus.ProtocolFailure);
    }

    [Theory]
    [InlineData("protocolVersion")]
    [InlineData("revisionId")]
    [InlineData("schemaNodes")]
    [InlineData("name")]
    [InlineData("operation")]
    [InlineData("description")]
    public async Task DuplicateResponsePropertiesAreRejectedBeforeDeserialization(string property)
    {
        var runner = new RecordingRunner((request, _) =>
        {
            var json = Response(RequestHeader(request.Span)).ToJsonString();
            var marker = "\"" + property + "\":";
            Assert.Contains(marker, json, StringComparison.Ordinal);
            json = json.Replace(marker, marker + "\"untrusted duplicate\"," + marker, StringComparison.Ordinal);
            return Task.FromResult(new WorkerProcessResult(Frame(Encoding.UTF8.GetBytes(json)), WorkerProcessFailure.None));
        });
        AssertLocalFailure(await new FerruleInspectionBackend(runner).InspectAsync(Artifact()), MappingOperationStatus.ProtocolFailure);
    }

    [Theory]
    [InlineData(false, "nativeDigest", "NativeDigest")]
    [InlineData(true, "nativeDigest", "NativeDigest")]
    [InlineData(false, "result.capabilities.0.status", "Status")]
    [InlineData(true, "result.isValid", "IsValid")]
    public async Task WorkerPropertyCaseAliasesAreRejectedForInspectionAndValidation(bool validate, string path, string alias)
    {
        var runner = new RecordingRunner((request, _) =>
        {
            var response = JsonNode.Parse(Response(RequestHeader(request.Span)).ToJsonString(),
                new JsonNodeOptions { PropertyNameCaseInsensitive = false })!.AsObject();
            var separator = path.LastIndexOf('.');
            var owner = separator < 0 ? response : At(response, path[..separator]).AsObject();
            var property = path[(separator + 1)..];
            owner[alias] = At(response, path).DeepClone();
            Assert.Equal(2, owner.Count(entry => entry.Key == property || entry.Key == alias));
            var json = response.ToJsonString();
            Assert.Contains("\"" + property + "\":", json, StringComparison.Ordinal);
            Assert.Contains("\"" + alias + "\":", json, StringComparison.Ordinal);
            return Task.FromResult(new WorkerProcessResult(Frame(Encoding.UTF8.GetBytes(json)), WorkerProcessFailure.None));
        });
        var backend = new FerruleInspectionBackend(runner);

        if (validate)
            AssertLocalFailure(await backend.ValidateAsync(Artifact()), MappingOperationStatus.ProtocolFailure);
        else
            AssertLocalFailure(await backend.InspectAsync(Artifact()), MappingOperationStatus.ProtocolFailure);
    }

    [Fact]
    public async Task ResponseByteCeilingAllowsACompleteFrameAtTheLimit()
    {
        var runner = new RecordingRunner((request, _) =>
        {
            var json = Encoding.UTF8.GetBytes(Response(RequestHeader(request.Span)).ToJsonString());
            var payload = new byte[InspectionPolicy.MaximumResponseBytes];
            payload.AsSpan().Fill((byte)' ');
            json.CopyTo(payload.AsSpan());
            return Task.FromResult(new WorkerProcessResult(Frame(payload), WorkerProcessFailure.None));
        });
        var result = await new FerruleInspectionBackend(runner).InspectAsync(Artifact());
        Assert.Equal(MappingOperationStatus.Succeeded, result.Status);
        Assert.True(result.Provenance.Confirmed);
    }

    [Theory]
    [InlineData("protocolVersion")]
    [InlineData("requestId")]
    [InlineData("operation")]
    [InlineData("revision.organizationId")]
    [InlineData("revision.projectId")]
    [InlineData("revision.mappingId")]
    [InlineData("revision.revisionId")]
    [InlineData("nativeDigest")]
    [InlineData("documentId")]
    [InlineData("engineRevision")]
    public async Task EveryResponseContextComponentMustMatchTheInvocation(string path)
    {
        var runner = ReplyRunner(response => Set(response, path, "different-context"));
        AssertLocalFailure(await new FerruleInspectionBackend(runner).InspectAsync(Artifact()), MappingOperationStatus.ProtocolFailure);
    }

    [Theory]
    [InlineData("0495822e102bba3f97d9bffd3c665adfb6138bec")]
    [InlineData("577359a991a7dd44172a3c4af91fdcab7592457c")]
    [InlineData("ffffffffffffffffffffffffffffffffffffffff")]
    public async Task OldAndForgedLiveEngineRevisionsCannotConfirmInspectionOrValidation(string revision)
    {
        var runner = ReplyRunner(response => response["engineRevision"] = revision);
        var backend = new FerruleInspectionBackend(runner);
        AssertLocalFailure(await backend.InspectAsync(Artifact()), MappingOperationStatus.ProtocolFailure);
        AssertLocalFailure(await backend.ValidateAsync(Artifact()), MappingOperationStatus.ProtocolFailure);
        Assert.Equal(2, runner.Calls);
    }

    [Theory]
    [InlineData("protocolVersion")]
    [InlineData("requestId")]
    [InlineData("operation")]
    [InlineData("revision")]
    [InlineData("revision.organizationId")]
    [InlineData("revision.projectId")]
    [InlineData("revision.mappingId")]
    [InlineData("revision.revisionId")]
    [InlineData("nativeDigest")]
    [InlineData("documentId")]
    [InlineData("engineRevision")]
    [InlineData("result")]
    [InlineData("failure")]
    [InlineData("result.kind")]
    [InlineData("result.boundaries")]
    [InlineData("result.counts")]
    [InlineData("result.unresolvedResources")]
    [InlineData("result.capabilities")]
    [InlineData("result.truncated")]
    [InlineData("result.boundaries.0.id")]
    [InlineData("result.boundaries.0.role")]
    [InlineData("result.boundaries.0.name")]
    [InlineData("result.boundaries.0.nameTruncated")]
    [InlineData("result.boundaries.0.rootName")]
    [InlineData("result.boundaries.0.rootNameTruncated")]
    [InlineData("result.boundaries.0.shape")]
    [InlineData("result.boundaries.0.repeating")]
    [InlineData("result.boundaries.0.nativeLocator")]
    [InlineData("result.boundaries.0.nativeLocator.documentId")]
    [InlineData("result.counts.schemaNodes")]
    [InlineData("result.counts.graphNodes")]
    [InlineData("result.counts.scopeNodes")]
    [InlineData("result.counts.userFunctions")]
    [InlineData("result.counts.failureRules")]
    [InlineData("result.counts.totalItems")]
    [InlineData("result.unresolvedResources.0.boundaryId")]
    [InlineData("result.unresolvedResources.0.kind")]
    [InlineData("result.unresolvedResources.0.resolution")]
    [InlineData("result.unresolvedResources.0.nativeLocator")]
    [InlineData("result.capabilities.0.operation")]
    [InlineData("result.capabilities.0.status")]
    [InlineData("result.capabilities.0.profile")]
    [InlineData("result.capabilities.0.boundaryIds")]
    [InlineData("result.capabilities.0.conditions")]
    [InlineData("result.capabilities.0.conditions.0.code")]
    [InlineData("result.capabilities.0.conditions.0.description")]
    public async Task MissingRequiredInspectionFieldsAreProtocolFailures(string path)
    {
        var runner = ReplyRunner(response => Remove(response, path));
        AssertLocalFailure(await new FerruleInspectionBackend(runner).InspectAsync(Artifact()), MappingOperationStatus.ProtocolFailure);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("nativeDigest")]
    [InlineData("result.boundaries")]
    [InlineData("result.counts")]
    [InlineData("result.unresolvedResources")]
    [InlineData("result.capabilities")]
    [InlineData("result.boundaries.0")]
    [InlineData("result.boundaries.0.name")]
    [InlineData("result.boundaries.0.nativeLocator")]
    [InlineData("result.capabilities.0")]
    [InlineData("result.capabilities.0.conditions.0")]
    public async Task RequiredValuesCannotBeNull(string path)
    {
        var runner = ReplyRunner(response => Set(response, path, null));
        AssertLocalFailure(await new FerruleInspectionBackend(runner).InspectAsync(Artifact()), MappingOperationStatus.ProtocolFailure);
    }

    [Theory]
    [InlineData("")]
    [InlineData("revision")]
    [InlineData("result")]
    [InlineData("result.boundaries.0")]
    [InlineData("result.boundaries.0.nativeLocator")]
    [InlineData("result.counts")]
    [InlineData("result.unresolvedResources.0")]
    [InlineData("result.capabilities.0")]
    [InlineData("result.capabilities.0.conditions.0")]
    public async Task UnknownInspectionFieldsAreRejectedAtEveryObjectBoundary(string path)
    {
        var runner = ReplyRunner(response => At(response, path).AsObject()["unrecognized"] = "host-path");
        AssertLocalFailure(await new FerruleInspectionBackend(runner).InspectAsync(Artifact()), MappingOperationStatus.ProtocolFailure);
    }

    [Theory]
    [InlineData("neither")]
    [InlineData("both")]
    [InlineData("wrongResultKind")]
    [InlineData("unknownResultKind")]
    public async Task AnEnvelopeMustContainExactlyOneOperationSpecificResultOrFailure(string corruption)
    {
        var runner = ReplyRunner(response =>
        {
            switch (corruption)
            {
                case "neither": response["result"] = null; break;
                case "both": response["failure"] = Failure("nativeDecodeFailed"); break;
                case "wrongResultKind": response["result"] = Validation(); break;
                case "unknownResultKind": Set(response, "result.kind", "execute"); break;
            }
        });
        AssertLocalFailure(await new FerruleInspectionBackend(runner).InspectAsync(Artifact()), MappingOperationStatus.ProtocolFailure);

        var validationRunner = ReplyRunner(response => response["result"] = Inspection());
        AssertLocalFailure(await new FerruleInspectionBackend(validationRunner).ValidateAsync(Artifact()), MappingOperationStatus.ProtocolFailure);
    }

    [Theory]
    [InlineData("duplicateBoundary")]
    [InlineData("missingPrimaryInput")]
    [InlineData("wrongBoundaryId")]
    [InlineData("outOfOrderBoundaries")]
    [InlineData("unknownRole")]
    [InlineData("unknownShape")]
    [InlineData("wrongLocatorDocument")]
    [InlineData("unknownLocatorKind")]
    [InlineData("unknownResourceBoundary")]
    [InlineData("unknownResourceKind")]
    [InlineData("resolvedResource")]
    [InlineData("duplicateOperation")]
    [InlineData("missingOperation")]
    [InlineData("numericOperation")]
    [InlineData("numericStatus")]
    [InlineData("unsupportedInspection")]
    [InlineData("unqualifiedProjection")]
    [InlineData("unboundedProjection")]
    [InlineData("qualifiedPreview")]
    [InlineData("emptyConditions")]
    [InlineData("blankProfile")]
    [InlineData("unknownOutputReference")]
    [InlineData("wrongOutputOrder")]
    [InlineData("inspectionOutputScope")]
    [InlineData("countAboveLimit")]
    [InlineData("countBelowInventory")]
    [InlineData("negativeCount")]
    [InlineData("fractionalCount")]
    [InlineData("countOverflow")]
    [InlineData("missingTruncation")]
    public async Task InspectionInventoryAndCapabilitiesMustRemainInternallyConsistent(string corruption)
    {
        var runner = ReplyRunner(response => CorruptInspection(response, corruption));
        AssertLocalFailure(await new FerruleInspectionBackend(runner).InspectAsync(Artifact()), MappingOperationStatus.ProtocolFailure);
    }

    [Fact]
    public async Task InspectionTextLimitsMeasureUtf8BytesAndPreserveReportedTruncation()
    {
        var boundedName = new string('é', InspectionPolicy.MaximumTextBytes / 2);
        var acceptedRunner = ReplyRunner(response =>
        {
            Set(response, "result.boundaries.0.name", boundedName);
            Set(response, "result.boundaries.0.nameTruncated", true);
            Set(response, "result.truncated", true);
            Set(response, "result.counts.totalItems", InspectionPolicy.MaximumItems);
        });
        var accepted = await new FerruleInspectionBackend(acceptedRunner).InspectAsync(Artifact());
        Assert.Equal(MappingOperationStatus.Succeeded, accepted.Status);
        Assert.True(accepted.Inspection!.Truncated);
        Assert.True(accepted.Inspection.Boundaries[0].NameTruncated);
        Assert.Equal(boundedName, accepted.Inspection.Boundaries[0].Name);

        var rejectedRunner = ReplyRunner(response => Set(response, "result.boundaries.0.name", boundedName + "é"));
        AssertLocalFailure(await new FerruleInspectionBackend(rejectedRunner).InspectAsync(Artifact()), MappingOperationStatus.ProtocolFailure);
    }

    [Fact]
    public async Task ReportedNativePointerEscapesSurviveInspectionWithoutBeingRewritten()
    {
        var runner = ReplyRunner(response => Set(response, "result.boundaries.0.nativeLocator",
            ParseObject("""{"kind":"jsonPointer","documentId":"entry.native.json","pointer":"/schema/a~1b~0c.name"}""")));
        var result = await new FerruleInspectionBackend(runner).InspectAsync(Artifact());
        Assert.Equal(MappingOperationStatus.Succeeded, result.Status);
        Assert.Equal(new JsonPointerNativeLocator(InspectionPolicy.DocumentId, "/schema/a~1b~0c.name"), result.Inspection!.Boundaries[0].NativeLocator);
    }

    [Fact]
    public async Task EmptyValidationIsValidAndConfirmed()
    {
        var artifact = Artifact();
        var result = await new FerruleInspectionBackend(ReplyRunner()).ValidateAsync(artifact);
        Assert.Equal(MappingOperationStatus.Succeeded, result.Status);
        AssertProvenance(result.Provenance, artifact, true);
        Assert.True(result.IsValid);
        Assert.Equal(0, result.TotalIssueCount);
        Assert.Equal(0, result.EmittedIssueCount);
        Assert.False(result.Truncated);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task ValidationPreservesTypedOwnersExactIdsAndOnlyReportedNativeLocations()
    {
        const string functionId = "9007199254740993";
        const string nodeId = "4294967295";
        var issues = new JsonArray(
            Issue(ParseObject($$"""{"kind":"graphNode","functionId":"{{functionId}}","nodeId":"{{nodeId}}"}"""),
                ParseObject($$"""{"kind":"graphNode","documentId":"entry.native.json","nodeId":"{{nodeId}}","functionId":"{{functionId}}"}""")),
            Issue(ParseObject($$"""{"kind":"userFunction","functionId":"{{functionId}}"}""")),
            Issue(ParseObject("""{"kind":"endpoint","endpoint":{"kind":"source"}}""")),
            Issue(ParseObject("""{"kind":"endpoint","endpoint":{"kind":"target"}}""")),
            Issue(ParseObject("""{"kind":"endpoint","endpoint":{"kind":"namedSource","index":"2","name":"a/b~c"}}""")),
            Issue(ParseObject("""{"kind":"schemaNode","endpoint":{"kind":"namedTarget","index":"3","name":"other.name"},"path":[{"kind":"child","index":"0"},{"kind":"dynamicField"},{"kind":"contains","index":"1"},{"kind":"dependentSchema","index":"2"}]}""")),
            Issue(ParseObject("""{"kind":"scope","target":{"kind":"target"},"path":[{"kind":"child","index":"0"},{"kind":"dynamicChild","index":"1"},{"kind":"segment","index":"2"}]}""")),
            Issue(ParseObject("""{"kind":"failureRule","index":"4"}""")),
            Issue());
        var runner = ReplyRunner(response => response["result"] = Validation(issues));
        var artifact = Artifact();
        var result = await new FerruleInspectionBackend(runner).ValidateAsync(artifact);

        Assert.Equal(MappingOperationStatus.InvalidMapping, result.Status);
        AssertProvenance(result.Provenance, artifact, true);
        Assert.False(result.IsValid);
        Assert.Equal(9, result.TotalIssueCount);
        Assert.Equal(9, result.EmittedIssueCount);
        Assert.False(result.Truncated);
        Assert.Collection(result.Diagnostics,
            diagnostic =>
            {
                Assert.Equal(new GraphNodeDiagnosticOwner(functionId, nodeId), diagnostic.Owner);
                Assert.Equal(new GraphNodeNativeLocator(InspectionPolicy.DocumentId, nodeId, functionId), diagnostic.NativeLocator);
            },
            diagnostic => Assert.Equal(new UserFunctionDiagnosticOwner(functionId), diagnostic.Owner),
            diagnostic => Assert.IsType<SourceMappingEndpoint>(Assert.IsType<EndpointDiagnosticOwner>(diagnostic.Owner).Endpoint),
            diagnostic => Assert.IsType<TargetMappingEndpoint>(Assert.IsType<EndpointDiagnosticOwner>(diagnostic.Owner).Endpoint),
            diagnostic => Assert.Equal(new NamedSourceMappingEndpoint("2", "a/b~c"), Assert.IsType<EndpointDiagnosticOwner>(diagnostic.Owner).Endpoint),
            diagnostic =>
            {
                var owner = Assert.IsType<SchemaNodeDiagnosticOwner>(diagnostic.Owner);
                Assert.Equal(new NamedTargetMappingEndpoint("3", "other.name"), owner.Endpoint);
                Assert.Collection(owner.Path,
                    step => Assert.Equal(new ChildSchemaOwnerStep("0"), step),
                    step => Assert.IsType<DynamicFieldSchemaOwnerStep>(step),
                    step => Assert.Equal(new ContainsSchemaOwnerStep("1"), step),
                    step => Assert.Equal(new DependentSchemaOwnerStep("2"), step));
            },
            diagnostic =>
            {
                var owner = Assert.IsType<ScopeDiagnosticOwner>(diagnostic.Owner);
                Assert.IsType<TargetMappingEndpoint>(owner.Target);
                Assert.Collection(owner.Path,
                    step => Assert.Equal(new ChildScopeOwnerStep("0"), step),
                    step => Assert.Equal(new DynamicChildScopeOwnerStep("1"), step),
                    step => Assert.Equal(new SegmentScopeOwnerStep("2"), step));
            },
            diagnostic => Assert.Equal(new FailureRuleDiagnosticOwner("4"), diagnostic.Owner),
            diagnostic => Assert.Null(diagnostic.Owner));
        Assert.All(result.Diagnostics.Skip(1), diagnostic => Assert.Equal(new DocumentNativeLocator(InspectionPolicy.DocumentId), diagnostic.NativeLocator));
        Assert.All(result.Diagnostics, diagnostic =>
        {
            Assert.Equal("ferrule.validation", diagnostic.Code);
            Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
            Assert.Equal("reported native validation message", diagnostic.Message);
            Assert.Equal("native display location", diagnostic.Location);
        });
    }

    [Theory]
    [InlineData("kind")]
    [InlineData("isValid")]
    [InlineData("totalIssueCount")]
    [InlineData("emittedIssueCount")]
    [InlineData("truncated")]
    [InlineData("issues")]
    [InlineData("issues.0.code")]
    [InlineData("issues.0.location")]
    [InlineData("issues.0.locationTruncated")]
    [InlineData("issues.0.message")]
    [InlineData("issues.0.messageTruncated")]
    [InlineData("issues.0.owner")]
    [InlineData("issues.0.nativeLocator")]
    public async Task MissingRequiredValidationFieldsAreProtocolFailures(string path)
    {
        var runner = ReplyRunner(response =>
        {
            response["result"] = Validation(new JsonArray(Issue()));
            Remove(response, "result." + path);
        });
        AssertLocalFailure(await new FerruleInspectionBackend(runner).ValidateAsync(Artifact()), MappingOperationStatus.ProtocolFailure);
    }

    [Theory]
    [InlineData("numericFunctionId")]
    [InlineData("numericNodeId")]
    [InlineData("leadingZeroId")]
    [InlineData("signedId")]
    [InlineData("unicodeDigitId")]
    [InlineData("unknownOwner")]
    [InlineData("unknownOwnerField")]
    [InlineData("missingOwnerField")]
    [InlineData("nullOwnerField")]
    [InlineData("ownerLocatorMismatch")]
    [InlineData("functionLocatorMismatch")]
    [InlineData("graphOwnerDocumentLocator")]
    [InlineData("unknownIssueField")]
    [InlineData("nullIssue")]
    [InlineData("nullMessage")]
    [InlineData("wrongCode")]
    [InlineData("unknownEndpoint")]
    [InlineData("numericIndex")]
    [InlineData("unknownStep")]
    [InlineData("unknownStepField")]
    [InlineData("missingStepIndex")]
    [InlineData("malformedPointer")]
    [InlineData("unescapedPointer")]
    [InlineData("wrongDocument")]
    public async Task ValidationRejectsAmbiguousOwnersAndUntrustedLocatorClaims(string corruption)
    {
        var runner = ReplyRunner(response =>
        {
            response["result"] = Validation(new JsonArray(Issue(
                ParseObject("""{"kind":"graphNode","functionId":"9007199254740993","nodeId":"12"}"""),
                ParseObject("""{"kind":"graphNode","documentId":"entry.native.json","functionId":"9007199254740993","nodeId":"12"}"""))));
            CorruptValidation(response, corruption);
        });
        AssertLocalFailure(await new FerruleInspectionBackend(runner).ValidateAsync(Artifact()), MappingOperationStatus.ProtocolFailure);
    }

    [Theory]
    [InlineData("negativeTotal")]
    [InlineData("totalAboveLimit")]
    [InlineData("tooManyDiagnostics")]
    [InlineData("emittedMismatch")]
    [InlineData("emittedAboveTotal")]
    [InlineData("validWithIssue")]
    [InlineData("invalidWithoutIssue")]
    [InlineData("unreportedCountTruncation")]
    [InlineData("unreportedTextTruncation")]
    [InlineData("spuriousTruncation")]
    [InlineData("messageAboveLimit")]
    [InlineData("locationAboveLimit")]
    public async Task ValidationEnforcesCountsValidityDiagnosticAndTextLimits(string corruption)
    {
        var runner = ReplyRunner(response =>
        {
            response["result"] = Validation(new JsonArray(Issue()));
            switch (corruption)
            {
                case "negativeTotal": Set(response, "result.totalIssueCount", -1); break;
                case "totalAboveLimit": Set(response, "result.totalIssueCount", InspectionPolicy.MaximumValidationIssues + 1); break;
                case "tooManyDiagnostics": response["result"] = Validation(Issues(InspectionPolicy.MaximumDiagnostics + 1)); break;
                case "emittedMismatch": Set(response, "result.emittedIssueCount", 0); break;
                case "emittedAboveTotal": Set(response, "result.totalIssueCount", 0); break;
                case "validWithIssue": Set(response, "result.isValid", true); break;
                case "invalidWithoutIssue": response["result"] = Validation(); Set(response, "result.isValid", false); break;
                case "unreportedCountTruncation": Set(response, "result.totalIssueCount", 2); break;
                case "unreportedTextTruncation": Set(response, "result.issues.0.messageTruncated", true); break;
                case "spuriousTruncation": Set(response, "result.truncated", true); break;
                case "messageAboveLimit": Set(response, "result.issues.0.message", new string('é', 1025)); break;
                case "locationAboveLimit": Set(response, "result.issues.0.location", new string('x', InspectionPolicy.MaximumTextBytes + 1)); break;
            }
        });
        AssertLocalFailure(await new FerruleInspectionBackend(runner).ValidateAsync(Artifact()), MappingOperationStatus.ProtocolFailure);
    }

    [Fact]
    public async Task BoundedValidationRetainsTotalCountsAndExplicitTextTruncation()
    {
        var runner = ReplyRunner(response =>
        {
            response["result"] = Validation(Issues(InspectionPolicy.MaximumDiagnostics), InspectionPolicy.MaximumValidationIssues);
            Set(response, "result.issues.0.message", new string('é', 1024));
            Set(response, "result.issues.0.messageTruncated", true);
            Set(response, "result.issues.0.locationTruncated", true);
        });
        var result = await new FerruleInspectionBackend(runner).ValidateAsync(Artifact());

        Assert.Equal(MappingOperationStatus.InvalidMapping, result.Status);
        Assert.False(result.IsValid);
        Assert.Equal(32768, result.TotalIssueCount);
        Assert.Equal(128, result.EmittedIssueCount);
        Assert.Equal(128, result.Diagnostics.Count);
        Assert.True(result.Truncated);
        Assert.True(result.Diagnostics[0].MessageTruncated);
        Assert.True(result.Diagnostics[0].LocationTruncated);
        Assert.Equal(new string('é', 1024), result.Diagnostics[0].Message);

        var textOnlyRunner = ReplyRunner(response =>
        {
            response["result"] = Validation(new JsonArray(Issue()));
            Set(response, "result.issues.0.locationTruncated", true);
            Set(response, "result.truncated", true);
        });
        var textOnly = await new FerruleInspectionBackend(textOnlyRunner).ValidateAsync(Artifact());
        Assert.Equal(MappingOperationStatus.InvalidMapping, textOnly.Status);
        Assert.Equal(textOnly.TotalIssueCount, textOnly.EmittedIssueCount);
        Assert.True(textOnly.Truncated);
    }

    [Fact]
    public async Task ValidationOwnerPathsShareTheStructuralItemBudgetAcrossIssues()
    {
        var runner = ReplyRunner(response =>
        {
            var issues = new JsonArray();
            for (var index = 0; index < InspectionPolicy.MaximumDiagnostics; index++)
            {
                var path = new JsonArray();
                for (var step = 0; step < 64; step++)
                    path.Add(new JsonObject { ["kind"] = "child", ["index"] = "0" });
                issues.Add(Issue(new JsonObject
                {
                    ["kind"] = "schemaNode",
                    ["endpoint"] = new JsonObject { ["kind"] = "source" },
                    ["path"] = path
                }));
            }
            response["result"] = Validation(issues);
        });
        AssertLocalFailure(await new FerruleInspectionBackend(runner).ValidateAsync(Artifact()), MappingOperationStatus.ProtocolFailure);
    }

    [Theory]
    [InlineData("nativeDecodeFailed", MappingOperationStatus.InvalidMapping)]
    [InlineData("digestMismatch", MappingOperationStatus.InvalidMapping)]
    [InlineData("unsupportedNativeVersion", MappingOperationStatus.UnsupportedArtifact)]
    [InlineData("inspectionLimitExceeded", MappingOperationStatus.ResourceLimit)]
    [InlineData("resultLimitExceeded", MappingOperationStatus.ResourceLimit)]
    [InlineData("invalidFrame", MappingOperationStatus.ProtocolFailure)]
    [InlineData("invalidHeader", MappingOperationStatus.ProtocolFailure)]
    [InlineData("unsupportedProtocolVersion", MappingOperationStatus.ProtocolFailure)]
    [InlineData("unsupportedOperation", MappingOperationStatus.ProtocolFailure)]
    [InlineData("invalidContext", MappingOperationStatus.ProtocolFailure)]
    [InlineData("invalidLimits", MappingOperationStatus.ProtocolFailure)]
    public async Task VerifiedWorkerFailuresPreserveContextWithoutClaimingAValidationOutcome(string code, MappingOperationStatus status)
    {
        var runner = ReplyRunner(response =>
        {
            response["result"] = null;
            response["failure"] = Failure(code);
        });
        var backend = new FerruleInspectionBackend(runner);
        var artifact = Artifact("malformed native data"u8.ToArray());

        var inspected = await backend.InspectAsync(artifact);
        Assert.Equal(status, inspected.Status);
        Assert.Null(inspected.Inspection);
        AssertProvenance(inspected.Provenance, artifact, true);
        var diagnostic = Assert.Single(inspected.Diagnostics);
        Assert.Equal("ferrule." + code, diagnostic.Code);
        Assert.Equal("bounded worker failure", diagnostic.Message);
        Assert.Null(diagnostic.NativeLocator);
        Assert.Null(diagnostic.Owner);

        var validated = await backend.ValidateAsync(artifact);
        Assert.Equal(status, validated.Status);
        Assert.Null(validated.IsValid);
        Assert.Equal(0, validated.TotalIssueCount);
        Assert.Equal(0, validated.EmittedIssueCount);
        AssertProvenance(validated.Provenance, artifact, true);
        Assert.Equal(diagnostic, Assert.Single(validated.Diagnostics));
    }

    [Fact]
    public async Task VerifiedFailureRetainsOnlyItsReportedLocatorAndBoundedMessage()
    {
        var runner = ReplyRunner(response =>
        {
            response["result"] = null;
            response["failure"] = Failure("nativeDecodeFailed");
            Set(response, "failure.nativeLocator", DocumentLocator());
            Set(response, "failure.message", new string('é', InspectionPolicy.MaximumTextBytes / 2));
            Set(response, "failure.messageTruncated", true);
        });
        var result = await new FerruleInspectionBackend(runner).ValidateAsync(Artifact());
        Assert.Equal(MappingOperationStatus.InvalidMapping, result.Status);
        Assert.True(result.Provenance.Confirmed);
        Assert.Null(result.IsValid);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(new DocumentNativeLocator(InspectionPolicy.DocumentId), diagnostic.NativeLocator);
        Assert.True(diagnostic.MessageTruncated);
        Assert.Equal(new string('é', 1024), diagnostic.Message);
        Assert.Null(diagnostic.Owner);
        Assert.Null(diagnostic.Location);
    }

    [Theory]
    [InlineData("unknownCode")]
    [InlineData("missingCode")]
    [InlineData("missingMessage")]
    [InlineData("missingTruncation")]
    [InlineData("missingLocator")]
    [InlineData("unknownField")]
    [InlineData("nullMessage")]
    [InlineData("messageAboveLimit")]
    [InlineData("wrongDocument")]
    public async Task MalformedWorkerFailuresAreUnconfirmedProtocolFailures(string corruption)
    {
        var runner = ReplyRunner(response =>
        {
            response["result"] = null;
            response["failure"] = Failure("nativeDecodeFailed");
            switch (corruption)
            {
                case "unknownCode": Set(response, "failure.code", "host-path"); break;
                case "missingCode": Remove(response, "failure.code"); break;
                case "missingMessage": Remove(response, "failure.message"); break;
                case "missingTruncation": Remove(response, "failure.messageTruncated"); break;
                case "missingLocator": Remove(response, "failure.nativeLocator"); break;
                case "unknownField": Set(response, "failure.stderr", "host-path"); break;
                case "nullMessage": Set(response, "failure.message", null); break;
                case "messageAboveLimit": Set(response, "failure.message", new string('x', InspectionPolicy.MaximumTextBytes + 1)); break;
                case "wrongDocument": Set(response, "failure.nativeLocator", DocumentLocator("host-path")); break;
            }
        });
        AssertLocalFailure(await new FerruleInspectionBackend(runner).InspectAsync(Artifact()), MappingOperationStatus.ProtocolFailure);
    }

    [Theory]
    [InlineData(WorkerProcessFailure.Canceled, MappingOperationStatus.Canceled)]
    [InlineData(WorkerProcessFailure.TimedOut, MappingOperationStatus.TimedOut)]
    [InlineData(WorkerProcessFailure.StartFailed, MappingOperationStatus.BackendUnavailable)]
    [InlineData(WorkerProcessFailure.SandboxUnavailable, MappingOperationStatus.BackendUnavailable)]
    [InlineData(WorkerProcessFailure.WorkerFailed, MappingOperationStatus.BackendFailure)]
    [InlineData(WorkerProcessFailure.OutputLimitExceeded, MappingOperationStatus.ResourceLimit)]
    [InlineData(WorkerProcessFailure.CleanupFailed, MappingOperationStatus.IsolationFailure)]
    public async Task TransportFailuresNormalizeSafelyAndIgnoreOutput(WorkerProcessFailure failure, MappingOperationStatus status)
    {
        var runner = new RecordingRunner((_, _) => Task.FromResult(new WorkerProcessResult("secret stderr /host/private/path"u8.ToArray(), failure)));
        var backend = new FerruleInspectionBackend(runner);
        AssertLocalFailure(await backend.InspectAsync(Artifact()), status);
        AssertLocalFailure(await backend.ValidateAsync(Artifact()), status);
    }

    [Theory]
    [InlineData("canceled", MappingOperationStatus.Canceled)]
    [InlineData("io", MappingOperationStatus.BackendFailure)]
    [InlineData("invalidOperation", MappingOperationStatus.BackendFailure)]
    public async Task ExpectedRunnerExceptionsNormalizeWithoutExposingExceptionMessages(string failure, MappingOperationStatus status)
    {
        var runner = new RecordingRunner((_, _) => throw failure switch
        {
            "canceled" => new OperationCanceledException("secret stderr /host/private/path"),
            "io" => new IOException("secret stderr /host/private/path"),
            _ => new InvalidOperationException("secret stderr /host/private/path")
        });
        AssertLocalFailure(await new FerruleInspectionBackend(runner).InspectAsync(Artifact()), status);
    }

    private static void CorruptInspection(JsonObject response, string corruption)
    {
        switch (corruption)
        {
            case "duplicateBoundary": Set(response, "result.boundaries.1", At(response, "result.boundaries.0").DeepClone()); break;
            case "missingPrimaryInput": At(response, "result.boundaries").AsArray().RemoveAt(0); break;
            case "wrongBoundaryId": Set(response, "result.boundaries.1.id", "named-input:01"); break;
            case "outOfOrderBoundaries":
                var boundaries = At(response, "result.boundaries").AsArray();
                var last = boundaries[3]!.DeepClone();
                boundaries.RemoveAt(3);
                boundaries.Insert(0, last);
                break;
            case "unknownRole": Set(response, "result.boundaries.0.role", "future"); break;
            case "unknownShape": Set(response, "result.boundaries.0.shape", "array"); break;
            case "wrongLocatorDocument": Set(response, "result.boundaries.0.nativeLocator.documentId", "host-path"); break;
            case "unknownLocatorKind": Set(response, "result.boundaries.0.nativeLocator.kind", "future"); break;
            case "unknownResourceBoundary": Set(response, "result.unresolvedResources.0.boundaryId", "missing"); break;
            case "unknownResourceKind": Set(response, "result.unresolvedResources.0.kind", "future"); break;
            case "resolvedResource": Set(response, "result.unresolvedResources.0.resolution", "resolved"); break;
            case "duplicateOperation": Set(response, "result.capabilities.1.operation", "inspect"); break;
            case "missingOperation": At(response, "result.capabilities").AsArray().RemoveAt(4); break;
            case "numericOperation": Set(response, "result.capabilities.0.operation", 0); break;
            case "numericStatus": Set(response, "result.capabilities.0.status", 0); break;
            case "unsupportedInspection": Set(response, "result.capabilities.0.status", "unsupported"); break;
            case "unqualifiedProjection": Set(response, "result.capabilities.2.status", "notQualified"); break;
            case "unboundedProjection": Set(response, "result.capabilities.2.conditions.0.code", "unrestricted"); break;
            case "qualifiedPreview": Set(response, "result.capabilities.3.status", "supported"); break;
            case "emptyConditions": Set(response, "result.capabilities.0.conditions", new JsonArray()); break;
            case "blankProfile": Set(response, "result.capabilities.0.profile", " "); break;
            case "unknownOutputReference": Set(response, "result.capabilities.3.boundaryIds.0", "missing"); break;
            case "wrongOutputOrder": Set(response, "result.capabilities.3.boundaryIds", new JsonArray("named-output:0", "primary-output")); break;
            case "inspectionOutputScope": Set(response, "result.capabilities.0.boundaryIds", new JsonArray("primary-output")); break;
            case "countAboveLimit": Set(response, "result.counts.totalItems", InspectionPolicy.MaximumItems + 1); break;
            case "countBelowInventory": Set(response, "result.counts.totalItems", 13); break;
            case "negativeCount": Set(response, "result.counts.graphNodes", -1); break;
            case "fractionalCount": Set(response, "result.counts.graphNodes", 0.5); break;
            case "countOverflow": Set(response, "result.counts.graphNodes", uint.MaxValue); break;
            case "missingTruncation": Set(response, "result.boundaries.0.rootNameTruncated", true); break;
            default: throw new ArgumentOutOfRangeException(nameof(corruption));
        }
    }

    private static void CorruptValidation(JsonObject response, string corruption)
    {
        const string issue = "result.issues.0";
        switch (corruption)
        {
            case "numericFunctionId": Set(response, issue + ".owner.functionId", 9007199254740993L); break;
            case "numericNodeId": Set(response, issue + ".nativeLocator.nodeId", 12); break;
            case "leadingZeroId": Set(response, issue + ".owner.nodeId", "012"); Set(response, issue + ".nativeLocator.nodeId", "012"); break;
            case "signedId": Set(response, issue + ".owner.functionId", "-1"); break;
            case "unicodeDigitId": Set(response, issue + ".owner.functionId", "١"); break;
            case "unknownOwner": Set(response, issue + ".owner.kind", "future"); break;
            case "unknownOwnerField": Set(response, issue + ".owner.extra", true); break;
            case "missingOwnerField": Remove(response, issue + ".owner.nodeId"); break;
            case "nullOwnerField": Set(response, issue + ".owner.nodeId", null); break;
            case "ownerLocatorMismatch": Set(response, issue + ".nativeLocator.nodeId", "13"); break;
            case "functionLocatorMismatch": Set(response, issue + ".nativeLocator.functionId", null); break;
            case "graphOwnerDocumentLocator": Set(response, issue + ".nativeLocator", DocumentLocator()); break;
            case "unknownIssueField": Set(response, issue + ".extra", true); break;
            case "nullIssue": Set(response, issue, null); break;
            case "nullMessage": Set(response, issue + ".message", null); break;
            case "wrongCode": Set(response, issue + ".code", "future"); break;
            case "unknownEndpoint": Set(response, issue + ".owner", ParseObject("""{"kind":"endpoint","endpoint":{"kind":"future"}}""")); Set(response, issue + ".nativeLocator", DocumentLocator()); break;
            case "numericIndex": Set(response, issue + ".owner", ParseObject("""{"kind":"failureRule","index":9007199254740993}""")); Set(response, issue + ".nativeLocator", DocumentLocator()); break;
            case "unknownStep":
            case "unknownStepField":
            case "missingStepIndex":
                Set(response, issue + ".owner", ParseObject("""{"kind":"schemaNode","endpoint":{"kind":"source"},"path":[{"kind":"child","index":"0"}]}"""));
                Set(response, issue + ".nativeLocator", DocumentLocator());
                if (corruption == "unknownStep") Set(response, issue + ".owner.path.0.kind", "future");
                else if (corruption == "unknownStepField") Set(response, issue + ".owner.path.0.extra", true);
                else Remove(response, issue + ".owner.path.0.index");
                break;
            case "malformedPointer":
            case "unescapedPointer":
                Set(response, issue + ".owner", null);
                Set(response, issue + ".nativeLocator", ParseObject("""{"kind":"jsonPointer","documentId":"entry.native.json","pointer":"/schema/~2"}"""));
                if (corruption == "unescapedPointer") Set(response, issue + ".nativeLocator.pointer", "schema/field");
                break;
            case "wrongDocument": Set(response, issue + ".nativeLocator.documentId", "host-path"); break;
            default: throw new ArgumentOutOfRangeException(nameof(corruption));
        }
    }

    private static NativeArtifact Artifact(byte[]? bytes = null)
    {
        bytes ??= "{}"u8.ToArray();
        return NativeArtifact.Admit(Scope, bytes, bytes.Length);
    }

    private static RecordingRunner ReplyRunner(Action<JsonObject>? alter = null) => new((request, _) =>
    {
        var response = Response(RequestHeader(request.Span));
        alter?.Invoke(response);
        return Task.FromResult(new WorkerProcessResult(Frame(response), WorkerProcessFailure.None));
    });

    private static JsonObject RequestHeader(ReadOnlySpan<byte> frame)
    {
        var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(frame));
        return JsonNode.Parse(frame.Slice(4, length))!.AsObject();
    }

    private static byte[] RequestNativeBytes(byte[] frame)
    {
        var headerLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(frame));
        return frame[(8 + headerLength)..];
    }

    private static JsonObject Response(JsonObject request) => new()
    {
        ["protocolVersion"] = FerruleInspectionBackend.ProtocolVersion,
        ["requestId"] = request["requestId"]!.DeepClone(),
        ["operation"] = request["operation"]!.DeepClone(),
        ["revision"] = request["revision"]!.DeepClone(),
        ["nativeDigest"] = request["nativeDigest"]!.DeepClone(),
        ["documentId"] = InspectionPolicy.DocumentId,
        ["engineRevision"] = FerruleInspectionBackend.EngineRevision,
        ["result"] = request["operation"]!.GetValue<string>() == "inspect" ? Inspection() : Validation(),
        ["failure"] = null
    };

    private static JsonObject Inspection() => new()
    {
        ["kind"] = "inspect",
        ["boundaries"] = new JsonArray(
            Boundary("primary-input", "primaryInput", "Input", "object"),
            Boundary("named-input:0", "namedInput", "Lookup", "scalarUnion"),
            Boundary("primary-output", "primaryOutput", "Output", "scalar"),
            Boundary("named-output:0", "namedOutput", "other/a~b.name", "object", true)),
        ["counts"] = new JsonObject
        {
            ["schemaNodes"] = 4,
            ["graphNodes"] = 2,
            ["scopeNodes"] = 1,
            ["userFunctions"] = 1,
            ["failureRules"] = 1,
            ["totalItems"] = 14
        },
        ["unresolvedResources"] = new JsonArray(new JsonObject
        {
            ["boundaryId"] = "named-input:0",
            ["kind"] = "designTimeInput",
            ["resolution"] = "unresolved",
            ["nativeLocator"] = DocumentLocator()
        }),
        ["capabilities"] = new JsonArray(
            Capability("inspect", "supported", "ferrule.native-json.inspect.v1"),
            Capability("validate", "supported", "ferrule.native-json.validate.v1"),
            Capability("project", "supported", "ferrule.projection.v1"),
            Capability("preview", "notQualified", "ferrule.interpreter.v1", true),
            Capability("build", "notQualified", "ferrule.generated.v1", true)),
        ["truncated"] = false
    };

    private static JsonObject Boundary(string id, string role, string name, string shape, bool repeating = false) => new()
    {
        ["id"] = id,
        ["role"] = role,
        ["name"] = name,
        ["nameTruncated"] = false,
        ["rootName"] = "Root",
        ["rootNameTruncated"] = false,
        ["shape"] = shape,
        ["repeating"] = repeating,
        ["nativeLocator"] = DocumentLocator()
    };

    private static JsonObject Capability(string operation, string status, string profile, bool outputScope = false) => new()
    {
        ["operation"] = operation,
        ["status"] = status,
        ["profile"] = profile,
        ["boundaryIds"] = outputScope ? new JsonArray("primary-output", "named-output:0") : new JsonArray(),
        ["conditions"] = new JsonArray(new JsonObject
        {
            ["code"] = operation == "project" ? "boundedReadOnlyProjection" : status == "supported" ? "readOnlyInspection" : "operationNotImplemented",
            ["description"] = operation == "project"
                ? "Bounded read-only view; coverage may be partial or unavailable."
                : status == "supported"
                ? "Metadata and validation only; resource resolution and mapping execution are unavailable."
                : "This worker does not implement this operation."
        })
    };

    private static JsonObject Validation(JsonArray? issues = null, int? totalIssueCount = null)
    {
        issues ??= new JsonArray();
        var total = totalIssueCount ?? issues.Count;
        return new JsonObject
        {
            ["kind"] = "validate",
            ["isValid"] = total == 0,
            ["totalIssueCount"] = total,
            ["emittedIssueCount"] = issues.Count,
            ["truncated"] = total > issues.Count,
            ["issues"] = issues
        };
    }

    private static JsonObject Issue(JsonObject? owner = null, JsonObject? locator = null) => new()
    {
        ["code"] = "ferrule.validation",
        ["location"] = "native display location",
        ["locationTruncated"] = false,
        ["message"] = "reported native validation message",
        ["messageTruncated"] = false,
        ["owner"] = owner,
        ["nativeLocator"] = locator ?? DocumentLocator()
    };

    private static JsonArray Issues(int count)
    {
        var issues = new JsonArray();
        for (var index = 0; index < count; index++) issues.Add(Issue());
        return issues;
    }

    private static JsonObject Failure(string code) => new()
    {
        ["code"] = code,
        ["message"] = "bounded worker failure",
        ["messageTruncated"] = false,
        ["nativeLocator"] = null
    };

    private static JsonObject DocumentLocator(string documentId = InspectionPolicy.DocumentId) => new()
    {
        ["kind"] = "document",
        ["documentId"] = documentId
    };

    private static JsonObject ParseObject(string json) => JsonNode.Parse(json)!.AsObject();

    private static JsonNode At(JsonObject root, string path)
    {
        JsonNode current = root;
        if (path.Length == 0) return current;
        foreach (var part in path.Split('.'))
            current = current is JsonArray array ? array[int.Parse(part, System.Globalization.CultureInfo.InvariantCulture)]! : current[part]!;
        return current;
    }

    private static void Set(JsonObject root, string path, JsonNode? value)
    {
        var separator = path.LastIndexOf('.');
        var parent = separator < 0 ? root : At(root, path[..separator]);
        var key = path[(separator + 1)..];
        if (parent is JsonArray array) array[int.Parse(key, System.Globalization.CultureInfo.InvariantCulture)] = value;
        else parent[key] = value;
    }

    private static void Remove(JsonObject root, string path)
    {
        var separator = path.LastIndexOf('.');
        var parent = separator < 0 ? root : At(root, path[..separator]);
        Assert.True(parent.AsObject().Remove(path[(separator + 1)..]));
    }

    private static byte[] Frame(JsonObject response) => Frame(Encoding.UTF8.GetBytes(response.ToJsonString()));

    private static byte[] Frame(byte[] payload)
    {
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame.AsSpan(4));
        return frame;
    }

    private static byte[] LengthPrefix(int length)
    {
        var prefix = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(prefix, (uint)length);
        return prefix;
    }

    private static byte[] WithLength(byte[] frame, int length)
    {
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)length);
        return frame;
    }

    private static void AssertProvenance(MappingOperationProvenance provenance, NativeArtifact artifact, bool confirmed)
    {
        Assert.Equal(artifact.Scope, provenance.Scope);
        Assert.Equal(artifact.Digest, provenance.NativeDigest);
        Assert.Equal("ferrule", provenance.BackendId);
        Assert.Equal(FerruleInspectionBackend.EngineRevision, provenance.EngineRevision);
        Assert.Equal("test.ferrule-worker.v1", provenance.BackendBuild);
        Assert.Equal(FerruleInspectionBackend.AdapterBuild, provenance.AdapterBuild);
        Assert.Equal(confirmed, provenance.Confirmed);
    }

    private static void AssertLocalFailure(MappingInspectionResult result, MappingOperationStatus expected, NativeArtifact? artifact = null)
    {
        Assert.Equal(expected, result.Status);
        Assert.Null(result.Inspection);
        AssertProvenance(result.Provenance, artifact ?? Artifact(), false);
        AssertSafeDiagnostic(Assert.Single(result.Diagnostics));
    }

    private static void AssertLocalFailure(MappingValidationResult result, MappingOperationStatus expected, NativeArtifact? artifact = null)
    {
        Assert.Equal(expected, result.Status);
        Assert.Null(result.IsValid);
        Assert.Equal(0, result.TotalIssueCount);
        Assert.Equal(0, result.EmittedIssueCount);
        Assert.False(result.Truncated);
        AssertProvenance(result.Provenance, artifact ?? Artifact(), false);
        AssertSafeDiagnostic(Assert.Single(result.Diagnostics));
    }

    private static void AssertSafeDiagnostic(MappingDiagnostic diagnostic)
    {
        Assert.StartsWith("mapping.inspection.", diagnostic.Code, StringComparison.Ordinal);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.False(diagnostic.MessageTruncated);
        Assert.NotEmpty(diagnostic.Message);
        Assert.DoesNotContain("secret", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("host-path", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/host/private/path", diagnostic.Message, StringComparison.Ordinal);
        Assert.Null(diagnostic.NativeLocator);
        Assert.Null(diagnostic.Owner);
        Assert.Null(diagnostic.Location);
    }

    private sealed class RecordingRunner(Func<ReadOnlyMemory<byte>, CancellationToken, Task<WorkerProcessResult>> run) : IInspectionWorkerRunner
    {
        public string BuildIdentity => "test.ferrule-worker.v1";
        public int Calls { get; private set; }
        public byte[]? LastRequest { get; private set; }
        public CancellationToken LastCancellationToken { get; private set; }

        public Task<WorkerProcessResult> RunAsync(ReadOnlyMemory<byte> requestFrame, CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = requestFrame.ToArray();
            LastCancellationToken = cancellationToken;
            return run(requestFrame, cancellationToken);
        }
    }
}
