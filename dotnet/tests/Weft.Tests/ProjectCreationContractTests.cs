using System.Reflection;
using Weft.Application;
using Weft.Domain;

namespace Weft.Tests;

/// <summary>Pure contract vectors only; actual creation, authority and atomicity require PostgreSQL qualification.</summary>
public sealed class ProjectCreationContractTests
{
    private static readonly Guid Organization = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
    private static readonly PrincipalId Actor = new(Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f"));
    private static readonly Guid Key = Guid.Parse("ffeeddcc-bbaa-9988-7766-554433221100");
    private static readonly ProjectScope Project = new(Organization, Guid.Parse("abcdef01-2345-6789-abcd-ef0123456789"));
    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 13, 15, 20, 30, TimeSpan.FromHours(-7));

    [Fact]
    public void FrozenWhitespaceSetMatchesTheRuntimeAndEveryMemberTrimsOnlyTheName()
    {
        int[] expected =
        [
            0x0009, 0x000A, 0x000B, 0x000C, 0x000D, 0x0020, 0x0085, 0x00A0, 0x1680,
            0x2000, 0x2001, 0x2002, 0x2003, 0x2004, 0x2005, 0x2006, 0x2007, 0x2008, 0x2009, 0x200A,
            0x2028, 0x2029, 0x202F, 0x205F, 0x3000
        ];
        Assert.Equal(expected, Enumerable.Range(0, char.MaxValue + 1).Where(value => char.IsWhiteSpace((char)value)));
        foreach (var value in expected)
        {
            var text = (char)value + "Project" + (char)value;
            var metadata = new ProjectCreationMetadata(text, text);
            Assert.Equal("Project", metadata.Name);
            Assert.Equal(text, metadata.Description);
        }
        var retained = new ProjectCreationMetadata("\u200BProject\uFEFF", " \r\nExact\t ");
        Assert.Equal("\u200BProject\uFEFF", retained.Name);
        Assert.Equal(" \r\nExact\t ", retained.Description);
    }

    [Fact]
    public void MetadataSharesExistingAcceptanceWithoutPreTrimLimitsOrUnicodeNormalization()
    {
        var name = new string(' ', 1000) + Repeat("😀", 200) + new string('\u3000', 1000);
        var description = Repeat("😀", 4000);
        var metadata = new ProjectCreationMetadata(name, description);
        var existing = new IngestionCreateMetadata(name, description);
        Assert.Equal(existing.Name, metadata.Name);
        Assert.Equal(existing.Description, metadata.Description);
        Assert.Equal(Repeat("😀", 200), metadata.Name);
        Assert.Equal(description, metadata.Description);
        Assert.Equal("", new ProjectCreationMetadata("Project", "").Description);
        Assert.NotEqual(new ProjectCreationMetadata("é", ""), new ProjectCreationMetadata("e\u0301", ""));
        Assert.ThrowsAny<ArgumentException>(() => new ProjectCreationMetadata(Repeat("😀", 201), ""));
        Assert.ThrowsAny<ArgumentException>(() => new ProjectCreationMetadata("Project", Repeat("😀", 4001)));
        Assert.ThrowsAny<ArgumentException>(() => new ProjectCreationMetadata(new string('a', 201), ""));
        Assert.ThrowsAny<ArgumentException>(() => new ProjectCreationMetadata("Project", new string('a', 4001)));
    }

    [Fact]
    public void MetadataRejectsNullBlankNulAndActualMalformedUnicode()
    {
        Assert.ThrowsAny<ArgumentException>(() => new ProjectCreationMetadata(null!, ""));
        Assert.ThrowsAny<ArgumentException>(() => new ProjectCreationMetadata("Project", null!));
        foreach (var name in new[] { "", " \t\r\n\u3000", "Pro\0ject" })
            Assert.ThrowsAny<ArgumentException>(() => new ProjectCreationMetadata(name, ""));
        Assert.ThrowsAny<ArgumentException>(() => new ProjectCreationMetadata("Project", "Before\0After"));

        // Construct malformed UTF16 at runtime; discovery serialization must not replace the input.
        foreach (var malformed in new[] { new string((char)0xD800, 1), new string((char)0xDC00, 1) })
        {
            Assert.ThrowsAny<ArgumentException>(() => new ProjectCreationMetadata("Project" + malformed, ""));
            Assert.ThrowsAny<ArgumentException>(() => new ProjectCreationMetadata("Project", malformed));
        }
    }

    // Independently computed with Python hashlib/uuid.UUID.bytes and the frozen
    // literal framing specification; none of these expected digests call product encoding.
    [Theory]
    [InlineData("Project", "Exact description\r\n", "c4677036d6500835f720dd50872c60cfdf52fb21ef03fb2c5e428258b2c5269e")]
    [InlineData(" \u00A0Café😀\u3000", " \r\nCafe\u0301|😀\t ", "efd4f3c7127e7626fe5c660d6c8d0e4d1986ef2cd6a2a104764bac8bc2d0c970")]
    [InlineData("ab", "c", "8b3cb61b1c76c79f8f85bdd1702d6c66b59e5ed234da41a04f95227a0b9f45f8")]
    [InlineData("a", "bc", "66167ca5bef3862bd4d3f086eab6abd9910de12e7e839a02e9edc68d6f662283")]
    [InlineData("é", "", "44f1c63b46acd86ff45b5c178efc47daeb1eb1045ad8675e0759e75d299e8cb4")]
    [InlineData("e\u0301", "", "beb163ff8fc9cce54e93840d58ce7f0ca9a455257475bf2f98853fab7283b2dc")]
    public void FingerprintsMatchIndependentLengthFramedNetworkUuidAndExactUnicodeVectors(
        string name, string description, string expected)
    {
        var fingerprint = Fingerprint(new(name, description));
        Assert.Equal(1, fingerprint.Version);
        Assert.Equal(expected, fingerprint.Digest.Value);
        Assert.Equal(fingerprint, ProjectCreationFingerprint.Restore(1, Sha256Digest.Parse(expected)));
    }

    [Fact]
    public void FingerprintBindsEveryAcceptedIntentFieldAndTrimsOnlySurroundingNameWhitespace()
    {
        var metadata = new ProjectCreationMetadata("Project", "Exact description");
        var fingerprint = Fingerprint(metadata);
        var different = Guid.Parse("99999999-8888-7777-6666-555555555555");
        Assert.Equal(fingerprint, Fingerprint(new(" \tProject\u3000", metadata.Description)));
        Assert.NotEqual(fingerprint, ProjectCreationFingerprint.Create(different, Actor, Key, metadata));
        Assert.NotEqual(fingerprint, ProjectCreationFingerprint.Create(Organization, new(different), Key, metadata));
        Assert.NotEqual(fingerprint, ProjectCreationFingerprint.Create(Organization, Actor, different, metadata));
        Assert.NotEqual(fingerprint, Fingerprint(new("project", metadata.Description)));
        Assert.NotEqual(fingerprint, Fingerprint(new(metadata.Name, metadata.Description + " ")));
        Assert.NotEqual(fingerprint, Fingerprint(new(metadata.Name, "exact description")));
    }

    [Fact]
    public void FingerprintCreationAndRestoreRefuseEmptyIdsNullFactsAndUnknownVersions()
    {
        var metadata = new ProjectCreationMetadata("Project", "");
        Assert.ThrowsAny<ArgumentException>(() => ProjectCreationFingerprint.Create(Guid.Empty, Actor, Key, metadata));
        Assert.ThrowsAny<ArgumentException>(() => ProjectCreationFingerprint.Create(Organization, null!, Key, metadata));
        Assert.ThrowsAny<ArgumentException>(() => ProjectCreationFingerprint.Create(Organization, Actor, Guid.Empty, metadata));
        Assert.ThrowsAny<ArgumentException>(() => ProjectCreationFingerprint.Create(Organization, Actor, Key, null!));
        Assert.ThrowsAny<ArgumentException>(() => ProjectCreationFingerprint.Restore(1, null!));
        foreach (var version in new[] { int.MinValue, -1, 0, 2, int.MaxValue })
            Assert.ThrowsAny<ArgumentException>(() => ProjectCreationFingerprint.Restore(version, Fingerprint(metadata).Digest));
    }

    [Fact]
    public void RestoredReceiptsRecomputeTheirOrganizationActorKeyAndExactMetadataFingerprint()
    {
        var metadata = new ProjectCreationMetadata("Project", "Exact description");
        var fingerprint = Fingerprint(metadata);
        var different = Guid.Parse("99999999-8888-7777-6666-555555555555");
        var restored = ProjectCreationFingerprint.Restore(1, fingerprint.Digest);
        var receipt = new ProjectCreationReceipt(Project, Key, Actor, metadata, restored, CreatedAt);
        Assert.Equal(fingerprint, receipt.Fingerprint);
        Assert.Equal(CreatedAt.ToUniversalTime(), receipt.CreatedAtUtc);
        Assert.Equal(TimeSpan.Zero, receipt.CreatedAtUtc.Offset);
        Assert.ThrowsAny<ArgumentException>(() => new ProjectCreationReceipt(new(different, Project.ProjectId), Key, Actor, metadata, restored, CreatedAt));
        Assert.ThrowsAny<ArgumentException>(() => new ProjectCreationReceipt(Project, different, Actor, metadata, restored, CreatedAt));
        Assert.ThrowsAny<ArgumentException>(() => new ProjectCreationReceipt(Project, Key, new(different), metadata, restored, CreatedAt));
        Assert.ThrowsAny<ArgumentException>(() => new ProjectCreationReceipt(Project, Key, Actor, new("Other", metadata.Description), restored, CreatedAt));
        Assert.ThrowsAny<ArgumentException>(() => new ProjectCreationReceipt(Project, Key, Actor, new(metadata.Name, metadata.Description + " "), restored, CreatedAt));
        Assert.ThrowsAny<ArgumentException>(() => new ProjectCreationReceipt(Project, Key, Actor, metadata,
            ProjectCreationFingerprint.Restore(1, Sha256Digest.Parse(new string('0', 64))), CreatedAt));
    }

    [Fact]
    public void ReceiptInitialFactsAreFixedHistoryAndGeneratedProjectOrClockDoesNotChangeIntent()
    {
        var metadata = new ProjectCreationMetadata("Project", "");
        var fingerprint = Fingerprint(metadata);
        var first = new ProjectCreationReceipt(Project, Key, Actor, metadata, fingerprint, CreatedAt);
        var second = new ProjectCreationReceipt(new(Organization, Guid.Parse("99999999-8888-7777-6666-555555555555")),
            Key, Actor, metadata, fingerprint, CreatedAt.AddDays(1));
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.NotEqual(first.Project, second.Project);
        Assert.NotEqual(first.CreatedAtUtc, second.CreatedAtUtc);
        Assert.Equal(1L, first.InitialProjectVersion);
        Assert.Equal(ProjectRole.Administrator, first.InitialMembershipRole);
        Assert.True(first.InitialMembershipActive);
        Assert.Equal(1L, first.InitialMembershipVersion);

        foreach (var version in new[] { long.MinValue, 0L, 2L, long.MaxValue })
        {
            Assert.ThrowsAny<ArgumentException>(() => new ProjectCreationReceipt(Project, Key, Actor, metadata, fingerprint, CreatedAt,
                initialProjectVersion: version));
            Assert.ThrowsAny<ArgumentException>(() => new ProjectCreationReceipt(Project, Key, Actor, metadata, fingerprint, CreatedAt,
                initialMembershipVersion: version));
        }
        foreach (var role in new[] { (ProjectRole)0, ProjectRole.Viewer, ProjectRole.Runner, ProjectRole.Publisher, (ProjectRole)5, (ProjectRole)999 })
            Assert.ThrowsAny<ArgumentException>(() => new ProjectCreationReceipt(Project, Key, Actor, metadata, fingerprint, CreatedAt,
                initialMembershipRole: role));
        Assert.ThrowsAny<ArgumentException>(() => new ProjectCreationReceipt(Project, Key, Actor, metadata, fingerprint, CreatedAt,
            initialMembershipActive: false));
        Assert.DoesNotContain(typeof(ProjectCreationReceipt).GetProperties(), property => property.PropertyType == typeof(ProjectAccess));
    }

    [Fact]
    public void ReceiptRejectsMissingTypedFactsAndEmptyKeys()
    {
        var metadata = new ProjectCreationMetadata("Project", "");
        var fingerprint = Fingerprint(metadata);
        Assert.ThrowsAny<ArgumentException>(() => new ProjectCreationReceipt(null!, Key, Actor, metadata, fingerprint, CreatedAt));
        Assert.ThrowsAny<ArgumentException>(() => new ProjectCreationReceipt(Project, Guid.Empty, Actor, metadata, fingerprint, CreatedAt));
        Assert.ThrowsAny<ArgumentException>(() => new ProjectCreationReceipt(Project, Key, null!, metadata, fingerprint, CreatedAt));
        Assert.ThrowsAny<ArgumentException>(() => new ProjectCreationReceipt(Project, Key, Actor, null!, fingerprint, CreatedAt));
        Assert.ThrowsAny<ArgumentException>(() => new ProjectCreationReceipt(Project, Key, Actor, metadata, null!, CreatedAt));
    }

    [Fact]
    public void FalseCapabilityIsAnAdmittedObservationWithoutInventingAProjectGrant()
    {
        var denied = new OrganizationProjectCreationAdmission(Organization, Actor, false);
        Assert.Equal(Organization, denied.OrganizationId);
        Assert.Equal(Actor, denied.PrincipalId);
        Assert.False(denied.CanCreateProjects);
        Assert.True(new OrganizationProjectCreationAdmission(Organization, Actor, true).CanCreateProjects);
        Assert.ThrowsAny<ArgumentException>(() => new OrganizationProjectCreationAdmission(Guid.Empty, Actor, false));
        Assert.ThrowsAny<ArgumentException>(() => new OrganizationProjectCreationAdmission(Organization, null!, false));
        Assert.DoesNotContain(typeof(OrganizationProjectCreationAdmission).GetProperties(), property => property.PropertyType == typeof(ProjectAccess));
    }

    [Fact]
    public void OnlyClosedSuccessfulOutcomesExposeAReceiptAndDenialsOrConflictsStayOpaque()
    {
        var metadata = new ProjectCreationMetadata("Project", "");
        var receipt = new ProjectCreationReceipt(Project, Key, Actor, metadata, Fingerprint(metadata), CreatedAt);
        foreach (var outcome in new[] { ProjectCreationOutcome.Created, ProjectCreationOutcome.Replayed })
        {
            var result = ProjectCreationResult.ForReceipt(outcome, receipt);
            Assert.Equal(outcome, result.Outcome);
            Assert.Same(receipt, result.Receipt);
            Assert.ThrowsAny<ArgumentException>(() => ProjectCreationResult.WithoutReceipt(outcome));
            Assert.ThrowsAny<ArgumentException>(() => ProjectCreationResult.ForReceipt(outcome, null!));
        }
        foreach (var outcome in new[] { ProjectCreationOutcome.NotAdmitted, ProjectCreationOutcome.CapabilityDenied, ProjectCreationOutcome.IdempotencyConflict })
        {
            var result = ProjectCreationResult.WithoutReceipt(outcome);
            Assert.Equal(outcome, result.Outcome);
            Assert.Null(result.Receipt);
            Assert.ThrowsAny<ArgumentException>(() => ProjectCreationResult.ForReceipt(outcome, receipt));
        }
        foreach (var value in new[] { int.MinValue, -1, 0, 6, 999, int.MaxValue })
        {
            Assert.ThrowsAny<ArgumentException>(() => ProjectCreationResult.ForReceipt((ProjectCreationOutcome)value, receipt));
            Assert.ThrowsAny<ArgumentException>(() => ProjectCreationResult.WithoutReceipt((ProjectCreationOutcome)value));
        }
    }

    [Fact]
    public void AcceptedMetadataFingerprintAdmissionReceiptAndResultsHaveNoWritableFacts()
    {
        foreach (var type in new[] { typeof(ProjectCreationMetadata), typeof(ProjectCreationFingerprint),
            typeof(OrganizationProjectCreationAdmission), typeof(ProjectCreationReceipt), typeof(ProjectCreationResult) })
        {
            Assert.True(type.IsSealed);
            Assert.All(type.GetProperties(BindingFlags.Public | BindingFlags.Instance), property => Assert.Null(property.SetMethod));
        }
    }

    private static ProjectCreationFingerprint Fingerprint(ProjectCreationMetadata metadata) =>
        ProjectCreationFingerprint.Create(Organization, Actor, Key, metadata);

    private static string Repeat(string value, int count) => string.Concat(Enumerable.Repeat(value, count));
}
