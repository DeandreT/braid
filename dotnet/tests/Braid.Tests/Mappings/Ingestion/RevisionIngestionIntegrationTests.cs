using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Braid.Application;
using Braid.Domain;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class RevisionIngestionIntegrationTests(PostgresFixture fixture)
{
    [RevisionIngestionFact]
    public async Task ActualWorkerPublicationPreservesCurrentPositionWhitespaceUnknownMetadataAndExactNumericBytes()
    {
        await using var test = await RevisionIngestionTestSupport.CreateAsync(fixture);
        var legacy = RevisionIngestionTestSupport.Fixture("valid-basic");
        var versioned = RevisionIngestionTestSupport.Fixture("versioned-exact");
        var text = Encoding.UTF8.GetString(legacy);
        var end = text.LastIndexOf('}');
        // Legacy project metadata is ignored by the native codec; the versioned
        // envelope intentionally has a strict member set and stays unchanged.
        var unknown = Encoding.UTF8.GetBytes(" \r\n" + text[..end] + ",\"braid_unknown_metadata\":{\"decimal\":9007199254740993,\"spelling\":\"a/b~c\"}}\r\n ");
        foreach (var (name, bytes) in new[]
        {
            ("legacy-basic", legacy),
            ("versioned-exact", versioned),
            ("legacy-unknown-whitespace", unknown)
        })
        {
            var prefix = Encoding.UTF8.GetBytes("caller-position-prefix");
            using var source = new MemoryStream(prefix.Concat(bytes).ToArray());
            source.Position = prefix.Length;
            var result = await test.Service.CreateAsync(test.Actor, test.Project, Guid.NewGuid(),
                RevisionIngestionTestSupport.Request(filename: "preserved.native.json"), source);
            Assert.True(result.Outcome == RevisionIngestionOutcome.CommittedValid,
                $"Native fixture {name}: expected CommittedValid, actual {result.Outcome}; rejection {result.Rejection}; failure {result.Failure}.");
            Assert.True(source.CanRead);
            Assert.Equal(prefix.Length + bytes.Length, source.Position);
            var receipt = (await test.PrivateReceiptAsync(result.Receipt!.OperationId))!;
            Assert.Equal(bytes.LongLength, receipt.Facts.NativeObject.ByteCount);
            Assert.Equal(Sha256Digest.FromBytes(bytes), receipt.Facts.NativeObject.Digest);
            Assert.Equal(bytes, await test.PhysicalStore.ReadVerifiedAsync(receipt.Facts.FutureRevision, receipt.Facts.NativeObject));
            Assert.Equal(bytes, test.Runner.NativeBodies[^1]);
            Assert.Equal("preserved.native.json", result.Receipt.OriginalFilename);
            Assert.Equal("entry.native.json", receipt.CommittedRevision!.LogicalDocument);
            var actual = receipt.Attempts.Single().Completion!.Evidence!.Facts;
            Assert.True(actual.Provenance.Confirmed);
            Assert.Equal(test.Registration.Profile, actual.Provenance.BackendProfile);
            Assert.Equal(receipt.Facts.FutureRevision, actual.Provenance.Scope);
            Assert.True(actual.IsValid);
        }
        foreach (var header in test.Runner.Headers)
        {
            using var parsed = JsonDocument.Parse(header);
            Assert.Equal(new[] { "documentId", "limits", "nativeDigest", "operation", "protocolVersion", "requestId", "revision" },
                parsed.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
            Assert.DoesNotContain(test.PhysicalRoot.RootPath, header, StringComparison.Ordinal);
            Assert.DoesNotContain(test.Seed.Alice.ToString("D"), header, StringComparison.Ordinal);
            Assert.DoesNotContain("braid_ingest", header, StringComparison.Ordinal);
        }
        AssertWorkerHasNoServerConfiguration(test);
    }

    [RevisionIngestionFact]
    public async Task ActualDecodedInvalidDecodeFailureUnsupportedVersionAndWorkerLimitHaveDistinctHonestOutcomes()
    {
        await using var test = await RevisionIngestionTestSupport.CreateAsync(fixture);
        var invalid = await test.CreateAsync(RevisionIngestionTestSupport.Fixture("invalid-function"));
        Assert.Equal(RevisionIngestionOutcome.CommittedDecodedInvalid, invalid.Outcome);
        var retained = (await test.PrivateReceiptAsync(invalid.Receipt!.OperationId))!;
        var evidence = retained.Attempts.Single().Completion!.Evidence!;
        Assert.False(evidence.Facts.IsValid);
        Assert.True(evidence.Facts.TotalIssueCount > 0);
        Assert.Contains("18446744073709551615", Encoding.UTF8.GetString(evidence.CopyUtf8()), StringComparison.Ordinal);
        var decode = await test.CreateAsync("{not native JSON"u8.ToArray());
        Assert.Equal(RevisionIngestionOutcome.Rejected, decode.Outcome);
        Assert.Equal(RevisionIngestionRejection.NativeDecode, decode.Rejection);
        Assert.Null((await test.PrivateReceiptAsync(decode.Receipt!.OperationId))!.Attempts.Single().Completion!.Evidence!.Facts.IsValid);
        var unsupportedBytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(RevisionIngestionTestSupport.Fixture("versioned-exact"))
            .Replace("\"version\": 2", "\"version\": 999", StringComparison.Ordinal));
        var unsupported = await test.CreateAsync(unsupportedBytes);
        Assert.Equal(RevisionIngestionRejection.UnsupportedArtifact, unsupported.Rejection);
        var oversizedGraph = JsonNode.Parse(RevisionIngestionTestSupport.Fixture("valid-basic"))!;
        var nodes = new JsonObject();
        for (var index = 1; index <= 9000; index++)
            nodes[index.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new JsonObject { ["kind"] = "const", ["value"] = 0 };
        oversizedGraph["graph"]!["nodes"] = nodes;
        var limited = await test.CreateAsync(JsonSerializer.SerializeToUtf8Bytes(oversizedGraph));
        Assert.Equal(RevisionIngestionOutcome.Rejected, limited.Outcome);
        Assert.Equal(RevisionIngestionRejection.ResourceLimit, limited.Rejection);
        Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.mapping_revisions"));
        Assert.Equal(4, test.BackendPort.Calls);
    }

    [RevisionIngestionFact]
    public async Task NativeCeilingAndConfiguredReductionRejectBeforeReservationAndExactFourMiBRoundTrips()
    {
        await using var test = await RevisionIngestionTestSupport.CreateAsync(fixture);
        var empty = await test.CreateAsync([]);
        Assert.Equal(RevisionIngestionRejection.EmptyNative, empty.Rejection);
        Assert.Null(empty.Receipt);
        var fixtureBytes = RevisionIngestionTestSupport.Fixture("valid-basic");
        var exact = new byte[4 * 1024 * 1024];
        Array.Fill(exact, (byte)' ');
        fixtureBytes.CopyTo(exact, 0);
        var excess = exact.Concat(new byte[] { (byte)' ' }).ToArray();
        using var tooLarge = new PrivateStorageInput(excess);
        var rejected = await test.Service.CreateAsync(test.Actor, test.Project, Guid.NewGuid(), RevisionIngestionTestSupport.Request(), tooLarge);
        Assert.Equal(RevisionIngestionRejection.NativeLimit, rejected.Rejection);
        Assert.Null(rejected.Receipt);
        using var reducedInput = new PrivateStorageInput(fixtureBytes);
        var reduced = await test.NewService(options: new(1)).CreateAsync(test.Actor, test.Project, Guid.NewGuid(), RevisionIngestionTestSupport.Request(), reducedInput);
        Assert.Equal(RevisionIngestionRejection.NativeLimit, reduced.Rejection);
        Assert.InRange(reducedInput.MaximumRequested, 1, 2);
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.revision_ingestions"));
        Assert.Equal(0, test.BackendPort.Calls);
        var committed = await test.CreateAsync(exact);
        Assert.Equal(RevisionIngestionOutcome.CommittedValid, committed.Outcome);
        var receipt = (await test.PrivateReceiptAsync(committed.Receipt!.OperationId))!;
        Assert.Equal(exact, await test.PhysicalStore.ReadVerifiedAsync(receipt.Facts.FutureRevision, receipt.Facts.NativeObject));
        Assert.Equal(exact, test.Runner.NativeBodies.Single());
    }

    [RevisionIngestionFact]
    public async Task ConcurrentSameKeyCommitsOnceAndTerminalReplayIgnoresCurrentRegistryLimitAndObjectDamage()
    {
        await using var test = await RevisionIngestionTestSupport.CreateAsync(fixture);
        var bytes = RevisionIngestionTestSupport.Fixture("valid-basic");
        var key = Guid.NewGuid();
        var request = RevisionIngestionTestSupport.Request();
        var contenders = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => test.CreateAsync(bytes, key, request)));
        Assert.Contains(contenders, result => result.Outcome == RevisionIngestionOutcome.CommittedValid);
        Assert.All(contenders, result => Assert.True(result.Outcome is RevisionIngestionOutcome.CommittedValid or RevisionIngestionOutcome.InProgress));
        Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.revision_ingestions"));
        Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.mapping_revisions"));
        Assert.Equal(1, test.BackendPort.Calls);
        var committed = contenders.First(result => result.Outcome == RevisionIngestionOutcome.CommittedValid);
        var privateReceipt = (await test.PrivateReceiptAsync(committed.Receipt!.OperationId))!;
        var originalProfile = privateReceipt.Facts.BackendProfile;
        var path = test.PhysicalRoot.FinalPath(privateReceipt.Facts.NativeObject);
        File.SetUnixFileMode(path, PrivateStorageTestRoot.AttemptMode);
        File.WriteAllBytes(path, [99]);
        File.SetUnixFileMode(path, PrivateStorageTestRoot.FinalMode);
        var publications = test.StorePort.PublishCalls;
        var reads = test.StorePort.ReadCalls;
        var replayService = test.NewService(new IngestionBackendRegistry([]), new(1), storeLimit: 1);
        var replay = await replayService.CreateAsync(test.Actor, test.Project, key, request, new MemoryStream(bytes));
        Assert.Equal(RevisionIngestionOutcome.CommittedValid, replay.Outcome);
        Assert.Equal(committed.Receipt, replay.Receipt);
        Assert.Equal(publications, test.StorePort.PublishCalls);
        Assert.Equal(reads, test.StorePort.ReadCalls);
        Assert.Equal(1, test.BackendPort.Calls);
        Assert.Equal(new byte[] { 99 }, File.ReadAllBytes(path));
        Assert.Equal(originalProfile, (await test.PrivateReceiptAsync(committed.Receipt.OperationId))!.Facts.BackendProfile);
        foreach (var (changedRequest, changedBody) in new[]
        {
            (RevisionIngestionTestSupport.Request(name: "changed"), bytes),
            (RevisionIngestionTestSupport.Request(description: "changed"), bytes),
            (RevisionIngestionTestSupport.Request(filename: "changed.native.json"), bytes),
            (request, bytes.Concat(new byte[] { (byte)' ' }).ToArray()),
            (new CreateRevisionRequest(request.Metadata, "other-backend"), bytes)
        })
        {
            using var source = new PrivateStorageInput(changedBody);
            var conflict = await replayService.CreateAsync(test.Actor, test.Project, key, changedRequest, source);
            Assert.Equal(RevisionIngestionOutcome.IdempotencyConflict, conflict.Outcome);
            Assert.True(source.ReadCalls > 0);
        }
        Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.mapping_revisions"));
    }

    [RevisionIngestionFact]
    public async Task ControlledActualWorkerOrderAllocatesAnyHeadSequencesAndEqualHeadHasOneWinner()
    {
        await using var test = await RevisionIngestionTestSupport.CreateAsync(fixture);
        var bytes = RevisionIngestionTestSupport.Fixture("valid-basic");
        var initial = await test.CreateAsync(bytes);
        var target = initial.Receipt!.FutureMapping;
        var gateA = new RevisionIngestionGate();
        var gateB = new RevisionIngestionGate();
        test.BackendPort.AfterValidationGate = call => call == 2 ? gateA : call == 3 ? gateB : null;
        var a = test.Service.AppendAsync(test.Actor, target, Guid.NewGuid(), new(), new MemoryStream(bytes));
        await gateA.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var b = test.Service.AppendAsync(test.Actor, target, Guid.NewGuid(), new(), new MemoryStream(bytes));
        try
        {
            await gateB.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            gateB.Release();
            Assert.Equal(2L, (await b).Receipt!.CommittedSequence);
            gateA.Release();
            var head = await a;
            Assert.Equal(3L, head.Receipt!.CommittedSequence);
            var gateC = new RevisionIngestionGate();
            var gateD = new RevisionIngestionGate();
            test.BackendPort.AfterValidationGate = call => call == 4 ? gateC : call == 5 ? gateD : null;
            var condition = new EqualHead(head.Receipt.FutureRevision.RevisionId);
            var c = test.Service.AppendAsync(test.Actor, target, Guid.NewGuid(), new(condition), new MemoryStream(bytes));
            await gateC.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var d = test.Service.AppendAsync(test.Actor, target, Guid.NewGuid(), new(condition), new MemoryStream(bytes));
            try
            {
                await gateD.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                gateC.Release();
                gateD.Release();
                var results = await Task.WhenAll(c, d);
                Assert.Single(results, result => result.Outcome == RevisionIngestionOutcome.CommittedValid);
                Assert.Single(results, result => result.Outcome == RevisionIngestionOutcome.HeadConflict);
            }
            finally { gateC.Release(); gateD.Release(); await Task.WhenAll(c, d).WaitAsync(TimeSpan.FromSeconds(15)); }
        }
        finally { gateA.Release(); gateB.Release(); await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(15)); }
        test.BackendPort.AfterValidationGate = null;
        var rejected = await test.Service.AppendAsync(test.Actor, target, Guid.NewGuid(), new(), new MemoryStream("{broken"u8.ToArray()));
        Assert.Equal(RevisionIngestionOutcome.Rejected, rejected.Outcome);
        Assert.Equal(4L, await test.Case.ScalarAsync<long>("SELECT head_sequence FROM braid.mappings"));
        Assert.Equal(4L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.mapping_revisions"));
        Assert.Equal("Actual native integration", await test.Case.ScalarAsync<string>("SELECT name FROM braid.mappings"));
        Assert.Equal(test.Registration.Profile.BackendId, await test.Case.ScalarAsync<string>("SELECT backend_id FROM braid.mappings"));
    }

    [RevisionIngestionFact]
    public async Task MissingUncommittedObjectNeedsSourceAndResubmissionRecreatesOnlyTheSameReservedReference()
    {
        await using var test = await RevisionIngestionTestSupport.CreateAsync(fixture);
        var bytes = RevisionIngestionTestSupport.Fixture("valid-basic");
        var key = Guid.NewGuid();
        test.StorePort.FailAfterNextRealPublication();
        var interrupted = await test.CreateAsync(bytes, key);
        Assert.Equal(RevisionIngestionOutcome.RetryableFailure, interrupted.Outcome);
        Assert.Equal(IngestionFailureCode.StorageIo, interrupted.Failure);
        var facts = (await test.PrivateReceiptAsync(interrupted.Receipt!.OperationId))!.Facts;
        var path = test.PhysicalRoot.FinalPath(facts.NativeObject);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        File.Delete(path);
        var missing = await test.Service.ResumeAsync(test.Actor, test.Project, facts.OperationId);
        Assert.Equal(RevisionIngestionOutcome.NeedsSource, missing.Outcome);
        Assert.Equal(0, test.BackendPort.Calls);
        var recovered = await test.CreateAsync(bytes, key);
        Assert.Equal(RevisionIngestionOutcome.CommittedValid, recovered.Outcome);
        var after = (await test.PrivateReceiptAsync(facts.OperationId))!;
        Assert.Equal(facts, after.Facts);
        Assert.Equal(bytes, await test.PhysicalStore.ReadVerifiedAsync(facts.FutureRevision, facts.NativeObject));
        Assert.Equal(3, after.ClaimCount);
        Assert.Equal(1, test.BackendPort.Calls);
    }

    [RevisionIngestionFact]
    public async Task CorruptTruncatedAndExtendedUncommittedObjectsRemainUnchangedAcrossResumeAndMatchingResubmit()
    {
        await using var test = await RevisionIngestionTestSupport.CreateAsync(fixture);
        var bytes = RevisionIngestionTestSupport.Fixture("valid-basic");
        foreach (var damage in new[] { bytes[..^1], bytes.Concat(new byte[] { 7 }).ToArray(), bytes.Select(value => (byte)(value ^ 1)).ToArray() })
        {
            var key = Guid.NewGuid();
            test.StorePort.FailAfterNextRealPublication();
            var interrupted = await test.CreateAsync(bytes, key);
            var facts = (await test.PrivateReceiptAsync(interrupted.Receipt!.OperationId))!.Facts;
            var path = test.PhysicalRoot.FinalPath(facts.NativeObject);
            File.SetUnixFileMode(path, PrivateStorageTestRoot.AttemptMode);
            File.WriteAllBytes(path, damage);
            File.SetUnixFileMode(path, PrivateStorageTestRoot.FinalMode);
            var resume = await test.Service.ResumeAsync(test.Actor, test.Project, facts.OperationId);
            Assert.Equal(RevisionIngestionOutcome.RetryableFailure, resume.Outcome);
            Assert.Equal(IngestionFailureCode.StorageIntegrity, resume.Failure);
            var resubmit = await test.CreateAsync(bytes, key);
            Assert.Equal(IngestionFailureCode.StorageIntegrity, resubmit.Failure);
            Assert.Equal(damage, File.ReadAllBytes(path));
            Assert.Equal(facts, (await test.PrivateReceiptAsync(facts.OperationId))!.Facts);
        }
        Assert.Equal(0, test.BackendPort.Calls);
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.mapping_revisions"));
    }

    [RevisionIngestionFact]
    public async Task RevocationWhileActualWorkerAwaitsProtocolEofDeniesFreshPublicationAndAnotherPublisherCannotAdopt()
    {
        await using var test = await RevisionIngestionTestSupport.CreateAsync(fixture);
        var bytes = RevisionIngestionTestSupport.Fixture("valid-basic");
        foreach (var (revoke, restore) in new[]
        {
            ("UPDATE braid.principals SET active=false WHERE id=$1", "UPDATE braid.principals SET active=true WHERE id=$1"),
            ("UPDATE braid.organization_memberships SET active=false WHERE principal_id=$1 AND organization_id=$2", "UPDATE braid.organization_memberships SET active=true WHERE principal_id=$1 AND organization_id=$2"),
            ("UPDATE braid.project_memberships SET active=false WHERE principal_id=$1 AND organization_id=$2 AND project_id=$3", "UPDATE braid.project_memberships SET active=true WHERE principal_id=$1 AND organization_id=$2 AND project_id=$3")
        })
        {
            var gate = new RevisionIngestionGate();
            test.Launcher.NextProtocolGate = gate;
            var key = Guid.NewGuid();
            var operation = test.CreateAsync(bytes, key);
            try
            {
                await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var id = await test.Case.ScalarAsync<Guid>("SELECT operation_id FROM braid.revision_ingestions WHERE idempotency_key=$1", key);
                var other = new ActorContext(new PrincipalId(test.Seed.Admitted));
                Assert.Null(await test.Service.GetOwnReceiptAsync(other, test.Project, id));
                Assert.Equal(RevisionIngestionOutcome.NotAuthorizedOrNotFound, (await test.Service.ResumeAsync(other, test.Project, id)).Outcome);
                object[] values = revoke.Contains("$3", StringComparison.Ordinal) ? [test.Seed.Alice, test.Seed.OrganizationA, test.Seed.ProjectA]
                    : revoke.Contains("$2", StringComparison.Ordinal) ? [test.Seed.Alice, test.Seed.OrganizationA] : [test.Seed.Alice];
                await test.Case.ExecuteAsync(revoke, values);
                try
                {
                    gate.Release();
                    Assert.Equal(RevisionIngestionOutcome.NotAuthorizedOrNotFound, (await operation.WaitAsync(TimeSpan.FromSeconds(12))).Outcome);
                    Assert.Null(await test.Service.GetOwnReceiptAsync(test.Actor, test.Project, id));
                }
                finally { await test.Case.ExecuteAsync(restore, values); }
                var actualCompletion = test.BackendPort.Completions[^1];
                var actualEvidence = Assert.IsType<IngestionEvidenceDocument>(actualCompletion.Evidence);
                var reserved = (await test.PrivateReceiptAsync(id))!.Facts;
                Assert.Equal(IngestionAttemptCompletionKind.Validation, actualCompletion.Kind);
                Assert.Equal(IngestionBackendStatus.Succeeded, actualEvidence.Facts.Status);
                Assert.True(actualEvidence.Facts.IsValid);
                Assert.True(actualEvidence.Facts.Provenance.Confirmed);
                Assert.Equal(reserved.FutureRevision, actualEvidence.Facts.Provenance.Scope);
                Assert.Equal(reserved.NativeObject.Digest, actualEvidence.Facts.Provenance.NativeDigest);
                Assert.Equal(reserved.BackendProfile, actualEvidence.Facts.Provenance.BackendProfile);
                Assert.Equal(test.Registration.Profile, actualEvidence.Facts.Provenance.BackendProfile);
            }
            finally { gate.Release(); await operation.WaitAsync(TimeSpan.FromSeconds(12)); }
        }
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.mapping_revisions"));
        Assert.Equal(3, test.BackendPort.Calls);
        AssertWorkerHasNoServerConfiguration(test);
    }

    [RevisionIngestionFact]
    public async Task SourcePrivateStoreAndActualWorkerGatesHoldNoMetadataTransactionOrRowLockWithPoolSizeOne()
    {
        await using var test = await RevisionIngestionTestSupport.CreateAsync(fixture);
        var bytes = RevisionIngestionTestSupport.Fixture("valid-basic");
        var otherProject = new ProjectScope(test.Seed.OrganizationB, test.Seed.ProjectB);
        var other = await test.Service.CreateAsync(test.Actor, otherProject, Guid.NewGuid(), RevisionIngestionTestSupport.Request(), new MemoryStream(bytes));
        Assert.Equal(RevisionIngestionOutcome.CommittedValid, other.Outcome);
        foreach (var point in new[] { "source", "store", "worker" })
        {
            var gate = new RevisionIngestionGate();
            if (point == "store") test.StorePort.PublishReadGate = gate;
            if (point == "worker") test.Launcher.NextProtocolGate = gate;
            using Stream source = point == "source" ? new RevisionIngestionSource(new MemoryStream(bytes), gate) : new MemoryStream(bytes);
            var operation = test.Service.CreateAsync(test.Actor, test.Project, Guid.NewGuid(), RevisionIngestionTestSupport.Request(), source);
            try
            {
                await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                if (point == "store") Assert.NotEmpty(test.PhysicalRoot.AttemptFiles());
                await test.AssertNoHeldMetadataAsync(other.Receipt!.OperationId, otherProject);
                gate.Release();
                Assert.Equal(RevisionIngestionOutcome.CommittedValid, (await operation.WaitAsync(TimeSpan.FromSeconds(15))).Outcome);
            }
            finally
            {
                gate.Release();
                test.StorePort.PublishReadGate = null;
                await operation.WaitAsync(TimeSpan.FromSeconds(15));
            }
        }
    }

    [RevisionIngestionFact]
    public async Task CooperativeCancellationAtEveryRealPortCleansConnectionsAndAdmitsTheNextActualUpload()
    {
        await using var test = await RevisionIngestionTestSupport.CreateAsync(fixture);
        var bytes = RevisionIngestionTestSupport.Fixture("valid-basic");
        foreach (var point in new[] { "source", "publish", "read", "worker", "completion", "finalize" })
        {
            var gate = new RevisionIngestionGate();
            using var cancellation = new CancellationTokenSource();
            if (point == "publish") test.StorePort.PublishReadGate = gate;
            if (point == "read") test.StorePort.AfterReadGate = gate;
            if (point == "worker") test.Launcher.NextProtocolGate = gate;
            if (point == "completion") test.MetadataPort.BeforeValidationCompletion = gate;
            if (point == "finalize") test.MetadataPort.BeforeFinalize = gate;
            using Stream source = point == "source" ? new RevisionIngestionSource(new MemoryStream(bytes), gate) : new MemoryStream(bytes);
            var operation = test.Service.CreateAsync(test.Actor, test.Project, Guid.NewGuid(), RevisionIngestionTestSupport.Request(), source, cancellation.Token);
            try
            {
                await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(12));
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(12)));
                Assert.True(source.CanRead);
                await test.AssertNoHeldTransactionsAsync();
                Assert.Empty(test.PhysicalRoot.AttemptFiles());
            }
            finally
            {
                cancellation.Cancel(); gate.Release();
                test.StorePort.PublishReadGate = null; test.StorePort.AfterReadGate = null;
                test.MetadataPort.BeforeValidationCompletion = null; test.MetadataPort.BeforeFinalize = null;
                try { await operation.WaitAsync(TimeSpan.FromSeconds(12)); } catch (OperationCanceledException) { }
            }
        }
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.mapping_revisions"));
        var succeeding = await test.CreateAsync(bytes);
        Assert.Equal(RevisionIngestionOutcome.CommittedValid, succeeding.Outcome);
        AssertWorkerHasNoServerConfiguration(test);
    }

    [RevisionIngestionFact]
    public async Task EightMissingSourceCallsUseOrdinaryReleasedClaimsThenServiceReturnsAttemptExhausted()
    {
        await using var test = await RevisionIngestionTestSupport.CreateAsync(fixture);
        var key = Guid.NewGuid();
        test.StorePort.FailAfterNextRealPublication();
        var first = await test.CreateAsync(RevisionIngestionTestSupport.Fixture("valid-basic"), key);
        var facts = (await test.PrivateReceiptAsync(first.Receipt!.OperationId))!.Facts;
        File.Delete(test.PhysicalRoot.FinalPath(facts.NativeObject));
        for (var claim = 2; claim <= 8; claim++)
        {
            var missing = await test.Service.ResumeAsync(test.Actor, test.Project, facts.OperationId);
            Assert.Equal(RevisionIngestionOutcome.NeedsSource, missing.Outcome);
            Assert.Equal(claim, missing.Receipt!.ClaimCount);
        }
        var exhausted = await test.Service.ResumeAsync(test.Actor, test.Project, facts.OperationId);
        Assert.Equal(RevisionIngestionOutcome.AttemptExhausted, exhausted.Outcome);
        Assert.Equal(8, exhausted.Receipt!.ClaimCount);
        Assert.Equal(0, test.BackendPort.Calls);
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.mapping_revisions"));
    }

    private static void AssertWorkerHasNoServerConfiguration(RevisionIngestionTestSupport test)
    {
        foreach (var command in test.Launcher.Commands)
        {
            Assert.DoesNotContain(command.ArgumentList, argument => argument.Contains(test.PhysicalRoot.RootPath, StringComparison.Ordinal)
                || argument.Contains("braid_ingest", StringComparison.Ordinal) || argument.Contains("braid_runtime", StringComparison.Ordinal));
            Assert.DoesNotContain(command.ArgumentList, argument => argument is "--mount" or "--volume" or "-v");
            Assert.DoesNotContain(command.Environment.Keys, key => key.StartsWith("BRAID_", StringComparison.Ordinal) || key.StartsWith("PG", StringComparison.Ordinal));
            if (command.ArgumentList.Contains("--name", StringComparer.Ordinal))
            {
                Assert.Contains("none", command.ArgumentList);
                Assert.Contains("65532:65532", command.ArgumentList);
            }
        }
    }
}
