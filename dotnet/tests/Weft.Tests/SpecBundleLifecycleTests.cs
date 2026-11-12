using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

public sealed class SpecBundleLifecycleTests
{
    [Fact]
    public void Intake_inventory_migration_checksum_supersedes_the_reviewed_registry()
    {
        Assert.Equal("4d0d549c31cd208ea672c11273928f2b5d6dd3d977f427c6e3772911ec18db9b",
            Sha256Digest.FromBytes(System.Text.Encoding.UTF8.GetBytes(SpecSchemaAdmission.MigrationSql)).Value);
    }

    [Fact]
    public async Task Pending_schema_refuses_service_composition_before_opening_backend()
    {
        if (MigrationRunner.EmbeddedMigrations.Any(m => m.Version == 44)) return;
        await using var database = new PostgresSpecBundleDatabase("Host=127.0.0.1;Port=1;Database=unavailable;Username=weft_spec_writer;Password=unused", SpecDatabaseProfile.Writer);
        var session = database.ForActor(new(new(Guid.NewGuid())), new(Guid.NewGuid(), SpecStoreKind.Blob, Sha256Digest.FromBytes([1])));
        var opened = false;
        var error = await Assert.ThrowsAsync<SpecStorageException>(() => session.CreateServiceAsync((_, _) =>
        { opened = true; throw new InvalidOperationException(); }));
        Assert.Equal(SpecStorageError.Unavailable, error.Error);
        Assert.False(opened);
    }

    internal static SpecTemporaryEvidence Evidence(bool bound = false) => new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"), 7, 8, 1000,
        new(1, 2, 3), new(1, 2, 4), new(1, 2, 5), new(1, 2, 6), bound ? new(1, 2, 9) : null);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Physical_evidence_is_fixed_size_canonical_and_defensively_encoded(bool bound)
    {
        var evidence = Evidence(bound);
        var bytes = evidence.Encode();
        Assert.Equal(SpecTemporaryEvidence.EncodedLength, bytes.Length);
        Assert.Equal(bytes, SpecTemporaryEvidence.Decode(bytes).Encode());
        Assert.Equal(evidence.File, SpecTemporaryEvidence.Decode(bytes).File);
        bytes[0] = 0;
        Assert.Equal((byte)'W', evidence.Encode()[0]);
        Assert.Throws<ArgumentException>(() => SpecTemporaryEvidence.Decode(bytes));
        Assert.Throws<ArgumentException>(() => SpecTemporaryEvidence.Decode(evidence.Encode()[..^1]));
        Assert.Throws<ArgumentException>(() => SpecTemporaryEvidence.Decode([.. evidence.Encode(), 0]));
        var reserved = evidence.Encode(); reserved[80] = 1;
        Assert.Throws<ArgumentException>(() => SpecTemporaryEvidence.Decode(reserved));
        var missingMount = evidence.Encode(); Array.Clear(missingMount, 64, 8);
        Assert.Throws<ArgumentException>(() => SpecTemporaryEvidence.Decode(missingMount));
    }

    [Fact]
    public void Seal_identity_binds_logical_owner_physical_evidence_and_predecessor()
    {
        var fixture = new FakeLifecycle();
        var reference = new SpecObjectReference(new(fixture.Source, SpecObjectKind.Original, Guid.NewGuid()), Sha256Digest.FromBytes([1]), 1);
        var claim = new SpecPublicationClaim(Guid.NewGuid(), Guid.NewGuid(), reference);
        var temp = Guid.NewGuid();
        var absence = SpecTemporarySeal.Digest(claim, temp, SpecTemporaryPhase.Reserved, null, Evidence());
        var bound = SpecTemporarySeal.Digest(claim, temp, SpecTemporaryPhase.Bound, absence, Evidence(true));
        Assert.NotEqual(absence, bound);
        Assert.NotEqual(bound, SpecTemporarySeal.Digest(claim, temp, SpecTemporaryPhase.Bound, Sha256Digest.FromBytes([2]), Evidence(true)));
        Assert.NotEqual(absence, SpecTemporarySeal.Digest(new(claim.StoreId, Guid.NewGuid(), reference), temp, SpecTemporaryPhase.Reserved, null, Evidence()));
        Assert.NotEqual(absence, SpecTemporarySeal.Digest(claim, Guid.NewGuid(), SpecTemporaryPhase.Reserved, null, Evidence()));
        Assert.Throws<ArgumentException>(() => SpecTemporarySeal.Digest(claim, temp, SpecTemporaryPhase.Bound, absence, Evidence()));
        Assert.Throws<ArgumentException>(() => SpecTemporarySeal.Digest(claim, temp, SpecTemporaryPhase.Reserved, null, Evidence(true)));
    }

    [Fact]
    public async Task Upload_requires_content_authority_before_reading_and_reservation_before_publishing()
    {
        var lifecycle = new FakeLifecycle { Deny = true };
        var storage = new RecordingStorage(lifecycle);
        var service = new SpecBundleService(lifecycle, storage);
        using var input = new MemoryStream([1, 2]);
        await Assert.ThrowsAsync<SpecStorageException>(() => service.UploadAsync(lifecycle.Source, SpecSourceFormat.Json, Guid.NewGuid(), Guid.NewGuid(), 1, input));
        Assert.Equal(0, input.Position);
        Assert.Null(lifecycle.Reservation);
        lifecycle.Deny = false;
        var result = await service.UploadAsync(lifecycle.Source, SpecSourceFormat.Json, Guid.NewGuid(), Guid.NewGuid(), 1, input);
        Assert.Equal(result.Claim, storage.Published);
        Assert.Equal(Sha256Digest.FromBytes([1, 2]), result.Claim.Reference.Digest);
        Assert.True(input.CanRead);
    }

    [Fact]
    public async Task Reservation_failure_does_not_write_and_publication_failure_preserves_recovery_identity()
    {
        var lifecycle = new FakeLifecycle { RejectReservation = true };
        var storage = new RecordingStorage(lifecycle);
        var service = new SpecBundleService(lifecycle, storage);
        var objectId = Guid.NewGuid(); var attemptId = Guid.NewGuid();
        await Assert.ThrowsAsync<SpecStorageException>(() => service.UploadAsync(lifecycle.Source, SpecSourceFormat.Text, objectId, attemptId, 1, new MemoryStream([1])));
        Assert.Null(storage.Published);
        lifecycle.RejectReservation = false; storage.FailPublication = true;
        await Assert.ThrowsAsync<SpecStorageException>(() => service.UploadAsync(lifecycle.Source, SpecSourceFormat.Text, objectId, attemptId, 1, new MemoryStream([1])));
        var durable = lifecycle.Reservation;
        storage.FailPublication = false;
        var retry = await service.UploadAsync(lifecycle.Source, SpecSourceFormat.Text, objectId, attemptId, 1, new MemoryStream([1]));
        Assert.Equal(durable, retry);
    }

    [Fact]
    public async Task Logical_tombstone_precedes_separate_physical_retirement()
    {
        var lifecycle = new FakeLifecycle(); var storage = new RecordingStorage(lifecycle);
        var service = new SpecBundleService(lifecycle, storage);
        await service.UploadAsync(lifecycle.Source, SpecSourceFormat.Text, Guid.NewGuid(), Guid.NewGuid(), 1, new MemoryStream([1]));
        await service.TombstoneAsync(lifecycle.Source.Bundle, null, 2, Guid.NewGuid());
        Assert.False(storage.Retired);
        Assert.Equal(1, await service.RetirePendingAsync(lifecycle.Source.Bundle));
        Assert.True(storage.Retired);
    }

    [Fact]
    public async Task Extraction_digest_mismatch_is_not_reserved_or_published()
    {
        var lifecycle = new FakeLifecycle(); var storage = new RecordingStorage(lifecycle);
        var extraction = SpecSourceExtractor.Extract(new(lifecycle.Source, SpecSourceFormat.Json, "{}"u8));
        var service = new SpecBundleService(lifecycle, storage);
        var error = await Assert.ThrowsAsync<SpecStorageException>(() => service.PublishExtractionAsync(extraction.Identity, Guid.NewGuid(), Guid.NewGuid(), 1, new MemoryStream([1])));
        Assert.Equal(SpecStorageError.Integrity, error.Error);
        Assert.Null(lifecycle.Reservation); Assert.Null(storage.Published);
    }

    [Fact]
    public async Task Extracted_evidence_can_be_published_without_recreating_its_canonical_encoding()
    {
        var lifecycle = new FakeLifecycle(); var storage = new RecordingStorage(lifecycle);
        var extraction = SpecSourceExtractor.Extract(new(lifecycle.Source, SpecSourceFormat.Json, "{}"u8));
        var copy = extraction.CopyEvidenceBytes();
        Assert.Equal(extraction.Identity.EvidenceDigest, Sha256Digest.FromBytes(copy));
        copy[0] = 0;
        Assert.Equal(extraction.Identity.EvidenceDigest, Sha256Digest.FromBytes(extraction.CopyEvidenceBytes()));
        var result = await new SpecBundleService(lifecycle, storage).PublishExtractionAsync(extraction, Guid.NewGuid(), Guid.NewGuid(), 1);
        Assert.Equal(SpecObjectKind.Extraction, result.Claim.Reference.Id.Kind);
        Assert.Equal(extraction.Identity.EvidenceDigest, result.Claim.Reference.Digest);
    }

    [Fact]
    public void Pending_0044_does_not_invent_missing_predecessors_or_change_the_active_sequence()
    {
        var names = typeof(PostgresSpecBundleDatabase).Assembly.GetManifestResourceNames();
        Assert.Single(names, n => n.EndsWith(".0044_spec_bundles.sql", StringComparison.Ordinal));
        var migrations = MigrationRunner.EmbeddedMigrations;
        Assert.Equal(Enumerable.Range(1, migrations.Count), migrations.Select(m => m.Version));
        if (names.Contains("Weft.Persistence.Postgres.PendingMigrations.0044_spec_bundles.sql")) Assert.DoesNotContain(migrations, m => m.Version == 44);
        Assert.Contains("version=42", SpecSchemaAdmission.MigrationSql);
        Assert.Contains("version=43", SpecSchemaAdmission.MigrationSql);
    }

    private sealed class FakeLifecycle : ISpecBundleLifecycle
    {
        internal SpecSourceIdentity Source { get; } = new(new(new(Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid()), Guid.NewGuid(), 1);
        internal SpecObjectReservation? Reservation;
        internal bool Deny, RejectReservation, Tombstoned;
        public Task<SpecBundleStatus> CreateAsync(SpecBundleScope scope, Guid key, CancellationToken token) => Task.FromResult(new SpecBundleStatus(scope, 1, false));
        public Task<SpecBundleStatus> AuthorizeAsync(SpecBundleScope scope, bool write, CancellationToken token) => Deny || Tombstoned
            ? throw new SpecStorageException(SpecStorageError.NotAdmitted) : Task.FromResult(new SpecBundleStatus(scope, 1, false));
        public Task<SpecObjectReservation> ReserveAsync(SpecObjectReference reference, SpecSourceFormat format, Guid attemptId, long version, SpecExtractionIdentity? extraction, CancellationToken token)
        {
            if (RejectReservation) throw new SpecStorageException(SpecStorageError.Conflict);
            return Task.FromResult(Reservation ??= new(new(Guid.NewGuid(), attemptId, reference), version + 1));
        }
        public Task<SpecObjectReference> FindAsync(SpecBundleScope scope, Guid objectId, CancellationToken token) => Task.FromResult(Reservation!.Claim.Reference);
        public Task<SpecBundleStatus> TombstoneAsync(SpecBundleScope scope, SpecSourceIdentity? source, long version, Guid key, CancellationToken token)
        { Tombstoned = true; return Task.FromResult(new SpecBundleStatus(scope, version + 1, true)); }
        public Task<IReadOnlyList<SpecRetirementClaim>> RetirementsAsync(SpecBundleScope scope, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<SpecRetirementClaim>>(Tombstoned ? [new(Reservation!.Claim.StoreId, Guid.NewGuid(), Reservation.Claim.Reference)] : []);
    }
    private sealed class RecordingStorage(FakeLifecycle lifecycle) : ISpecObjectStore
    {
        internal SpecPublicationClaim? Published;
        internal bool FailPublication, Retired;
        public Task<SpecPublishResult> PublishAsync(SpecBundleScope scope, SpecPublicationClaim claim, Stream source, CancellationToken token)
        {
            Assert.Equal(lifecycle.Reservation!.Claim, claim);
            if (FailPublication) throw new SpecStorageException(SpecStorageError.Io);
            Published = claim; return Task.FromResult(SpecPublishResult.Created);
        }
        public Task<byte[]> ReadAsync(SpecBundleScope scope, SpecObjectReference reference, CancellationToken token) => throw new NotSupportedException();
        public Task<SpecRetireResult> RetireAsync(SpecBundleScope scope, SpecRetirementClaim claim, CancellationToken token)
        { Assert.True(lifecycle.Tombstoned); Retired = true; return Task.FromResult(SpecRetireResult.Retired); }
    }
}
