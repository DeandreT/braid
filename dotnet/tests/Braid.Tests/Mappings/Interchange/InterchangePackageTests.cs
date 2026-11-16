using Braid.Application;
using Braid.Domain;

namespace Braid.Tests;

public sealed class InterchangePackageTests
{
    [Fact]
    public void SupplementaryUnicodeNamesUseUtf8OrderingSharedWithRust()
    {
        var package = new InterchangePackage("design.mfd", [new("\U00010000.json", "a"u8),
            new("\ue000.json", "b"u8), new("design.mfd", "<mapping/>"u8)]);
        Assert.Equal(["design.mfd", "\ue000.json", "\U00010000.json"], package.Files.Select(file => file.Path));
        Assert.Equal(package.Digest, InterchangePackage.Decode(package.Encode()).Digest);
    }
    [Theory]
    [InlineData("/design.mfd")]
    [InlineData("../design.mfd")]
    [InlineData("a/../design.mfd")]
    [InlineData("a//design.mfd")]
    [InlineData("./design.mfd")]
    [InlineData("a\\design.mfd")]
    [InlineData("C:design.mfd")]
    [InlineData("https://example.com/schema")]
    [InlineData("file\0.mfd")]
    public void UnsafePackageNamesAreRejected(string name) =>
        Assert.Throws<ArgumentException>(() => new InterchangeFile(name, "x"u8));

    [Fact]
    public void CanonicalPackageOwnsExactBytesAndBindsDesignAndEveryName()
    {
        byte[] bytes = [1, 2, 3];
        var file = new InterchangeFile("schema.json", bytes);
        var design = new InterchangeFile("design.mfd", "<mapping/>"u8);
        var package = new InterchangePackage(design.Path, [file, design]);
        bytes[0] = 9;
        Assert.Equal(new byte[] { 1, 2, 3 }, file.Bytes.ToArray());
        Assert.Equal(package.Digest, new InterchangePackage(design.Path, [design, file]).Digest);
        Assert.Equal(package.Digest, InterchangePackage.Decode(package.Encode()).Digest);
        Assert.NotEqual(package.Digest, new InterchangePackage(file.Path, [design, file]).Digest);
        Assert.NotEqual(package.Digest, new InterchangePackage(design.Path,
            [design, new InterchangeFile("renamed.json", file.Bytes)]).Digest);
    }

    [Fact]
    public void LimitsAndDuplicateMissingAndTrailingEntriesAreRefused()
    {
        var design = new InterchangeFile("design.mfd", "x"u8);
        Assert.Throws<ArgumentException>(() => new InterchangePackage(design.Path, [design, design]));
        Assert.Throws<ArgumentException>(() => new InterchangePackage("missing.mfd", [design]));
        Assert.Throws<ArgumentException>(() => new InterchangePackage(design.Path,
            Enumerable.Range(0, 129).Select(index => new InterchangeFile($"{index}.mfd", "x"u8))));
        Assert.Throws<ArgumentException>(() => new InterchangeFile(new string('x', 257), []));
        Assert.Throws<ArgumentException>(() => new InterchangeFile("large", new byte[InterchangePackage.MaximumFileBytes + 1]));
        Assert.Throws<ArgumentException>(() => new InterchangePackage("0", Enumerable.Range(0, 5)
            .Select(index => new InterchangeFile(index.ToString(), new byte[InterchangePackage.MaximumFileBytes]))));
        var encoded = new InterchangePackage(design.Path, [design]).Encode();
        Assert.Throws<ArgumentException>(() => InterchangePackage.Decode([.. encoded, 0]));
        Assert.Throws<ArgumentException>(() => InterchangePackage.Decode(encoded.AsSpan(0, encoded.Length - 1)));
    }

    [Fact]
    public void ProvenanceRoundTripsAndExtendsFingerprintWithoutChangingNativeOnlyIdentity()
    {
        var package = new InterchangePackage("design.mfd", [new("design.mfd", "<mapping/>"u8)]);
        var digest = Sha256Digest.FromBytes("native"u8);
        var report = Sha256Digest.FromBytes("warnings"u8);
        var engine = new IngestionBackendProfile("fake", "engine", "build", "adapter");
        var provenance = new InterchangeImportProvenance(package, digest, report, report, engine);
        Assert.Equal(provenance, InterchangeImportProvenance.Decode(provenance.Encode()));
        var project = new ProjectScope(Guid.NewGuid(), Guid.NewGuid());
        var actor = new PrincipalId(Guid.NewGuid());
        var native = IngestionIntent.ForCreate(new("Name", ""), "fake", "design.mfd", digest, 6);
        var imported = IngestionIntent.ForCreate(new("Name", ""), "fake", "design.mfd", digest, 6, provenance);
        Assert.NotEqual(CanonicalFingerprint.Create(actor, project, native), CanonicalFingerprint.Create(actor, project, imported));
        Assert.Equal(imported, IngestionIntent.ForCreate(new("Name", ""), "fake", "design.mfd", digest, 6,
            InterchangeImportProvenance.Decode(provenance.Encode())));
    }

    [Fact]
    public void CompleteWarningsCannotBeSilentlyTruncated()
    {
        Assert.Throws<ArgumentException>(() => new InterchangeReport("Summary", Enumerable.Repeat(
            new InterchangeWarning("unsupported", "Unsupported construct"), 257)));
        Assert.Throws<ArgumentException>(() => new InterchangeWarning("code", new string('x', 2049)));
    }
}
