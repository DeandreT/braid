using System.Runtime.Versioning;
using Braid.Application;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class MappingMetadataNativeIntegrationTests(PostgresFixture fixture)
{
    [RevisionIngestionFact]
    public async Task ActualPrivateStorageAndFerruleAppendPreserveEditedMetadataAndExactNativeBytesInBothOrders()
    {
        foreach (var metadataFirst in new[] { true, false })
        {
            await using var test = await RevisionIngestionTestSupport.CreateAsync(fixture);
            var originalBytes = RevisionIngestionTestSupport.Fixture("valid-basic");
            var appendBytes = RevisionIngestionTestSupport.Fixture("versioned-exact");
            var created = await test.CreateAsync(originalBytes);
            Assert.Equal(RevisionIngestionOutcome.CommittedValid, created.Outcome);
            var original = (await test.PrivateReceiptAsync(created.Receipt!.OperationId))!;
            var scope = created.Receipt.FutureMapping;
            await using var database = new PostgresMappingMetadataDatabase(test.Case.MetadataConnectionString);
            await using var readerDatabase = new PostgresCatalogDatabase(test.Case.CatalogConnectionString);
            var writer = new PostgresMappingMetadataWriter(database);
            var reader = new PostgresCatalogReader(readerDatabase);
            var initial = (await reader.GetMappingAsync(test.Actor, scope))!.Summary;
            var gate = new RevisionIngestionGate();
            RevisionIngestionResult appended;
            MappingMetadataWriteResult edited;
            Task? pending = null;
            try
            {
                if (metadataFirst)
                {
                    // The actual backend and store have finished before this port
                    // gate holds real metadata finalization. No worker timing changes.
                    test.MetadataPort.BeforeFinalize = gate;
                    var append = test.Service.AppendAsync(test.Actor, scope, Guid.NewGuid(), new(), new MemoryStream(appendBytes));
                    pending = append;
                    await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    Assert.Equal(IngestionAttemptCompletionKind.Validation, test.BackendPort.Completions[^1].Kind);
                    var actual = Assert.IsType<IngestionEvidenceDocument>(test.BackendPort.Completions[^1].Evidence);
                    Assert.Equal(IngestionBackendStatus.Succeeded, actual.Facts.Status);
                    Assert.True(actual.Facts.IsValid);
                    Assert.True(actual.Facts.Provenance.Confirmed);
                    Assert.Equal(test.Registration.Profile, actual.Facts.Provenance.BackendProfile);
                    edited = await writer.ReplaceAsync(test.Actor, scope, new(scope, 1), new("actual metadata edit", "metadata precedes actual append"));
                    gate.Release();
                    appended = await append.WaitAsync(TimeSpan.FromSeconds(15));
                }
                else
                {
                    var gatedWriter = new PostgresMetadataWriterGate(writer, gate);
                    var edit = gatedWriter.ReplaceAsync(test.Actor, scope, new(scope, 1), new("actual metadata edit", "actual append precedes metadata"));
                    pending = edit;
                    await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    appended = await test.Service.AppendAsync(test.Actor, scope, Guid.NewGuid(), new(), new MemoryStream(appendBytes));
                    gate.Release();
                    edited = await edit.WaitAsync(TimeSpan.FromSeconds(15));
                }
            }
            finally
            {
                gate.Release();
                if (pending is not null) await pending.WaitAsync(TimeSpan.FromSeconds(15));
                test.MetadataPort.BeforeFinalize = null;
            }
            Assert.Equal(MappingMetadataWriteOutcome.Updated, edited.Outcome);
            Assert.Equal(RevisionIngestionOutcome.CommittedValid, appended.Outcome);
            var committed = (await test.PrivateReceiptAsync(appended.Receipt!.OperationId))!;
            var validation = Assert.IsType<IngestionEvidenceDocument>(committed.Attempts.Single().Completion!.Evidence);
            Assert.Equal(IngestionBackendStatus.Succeeded, validation.Facts.Status);
            Assert.True(validation.Facts.IsValid);
            Assert.True(validation.Facts.Provenance.Confirmed);
            Assert.Equal(committed.Facts.FutureRevision, validation.Facts.Provenance.Scope);
            Assert.Equal(committed.Facts.NativeObject.Digest, validation.Facts.Provenance.NativeDigest);
            Assert.Equal(committed.Facts.BackendProfile, validation.Facts.Provenance.BackendProfile);
            Assert.Equal(test.Registration.Profile, validation.Facts.Provenance.BackendProfile);
            var current = (await reader.GetMappingAsync(test.Actor, scope))!.Summary;
            Assert.Equal(2, current.Version);
            Assert.Equal(2, current.Head.Sequence);
            Assert.Equal(committed.Facts.FutureRevision, current.Head.Scope);
            Assert.Equal(edited.Metadata, await reader.GetMappingMetadataAsync(test.Actor, scope));
            PostgresMappingMetadataTestSupport.AssertIdentityUnchanged(initial, current);
            Assert.Equal(originalBytes, await test.PhysicalStore.ReadVerifiedAsync(original.Facts.FutureRevision, original.Facts.NativeObject));
            Assert.Equal(appendBytes, await test.PhysicalStore.ReadVerifiedAsync(committed.Facts.FutureRevision, committed.Facts.NativeObject));
            Assert.Equal(Sha256Digest.FromBytes(originalBytes), original.Facts.NativeObject.Digest);
            Assert.Equal(Sha256Digest.FromBytes(appendBytes), committed.Facts.NativeObject.Digest);
            Assert.Equal(original.CommittedRevision, (await test.PrivateReceiptAsync(original.Facts.OperationId))!.CommittedRevision);
            Assert.Equal(originalBytes, test.Runner.NativeBodies[0]);
            Assert.Equal(appendBytes, test.Runner.NativeBodies[1]);
            Assert.Equal(2, test.BackendPort.Calls);
        }
    }
}
