using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Weft.Application;
using Weft.Domain;

namespace Weft.Tests;

/// <summary>
/// These contracts use synthetic admission evidence. They do not qualify a
/// native backend invocation, private object storage, or a metadata database.
/// </summary>
public sealed class IngestionContractTests
{
    private static Guid OrganizationId => Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
    private static Guid ProjectId => Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f");
    private static Guid MappingId => Guid.Parse("20314253-6475-8697-a8b9-cadbecfd0e1f");
    private static Guid RevisionId => Guid.Parse("30415263-7485-96a7-b8c9-daebfc0d1e2f");
    private static Guid OtherId => Guid.Parse("40516273-8495-a6b7-c8d9-eafb0c1d2e3f");
    private static PrincipalId Actor => new(Guid.Parse("ffeeddcc-bbaa-9988-7766-554433221100"));
    private static ProjectScope Project => new(OrganizationId, ProjectId);
    private static MappingScope Target => new(OrganizationId, ProjectId, MappingId);
    private static RevisionScope Scope => new(OrganizationId, ProjectId, MappingId, RevisionId);
    private static Sha256Digest Digest =>
        Sha256Digest.Parse("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    private static IngestionBackendProfile Profile => new("ferrule", "synthetic-engine-v1", "synthetic-backend-v1", "synthetic-adapter-v1");

    [Fact]
    public void CanonicalFingerprintMatchesAnIndependentLiteralNetworkOrderVector()
    {
        // Frozen vector encoded independently with Python UUID.bytes and big-endian packing.
        const string encodedHex =
            "00000015776566742e696e67657374696f6e2d696e74656e740000000100000001" +
            "00112233445566778899aabbccddeeff102132435465768798a9bacbdcedfe0f" +
            "ffeeddccbbaa9988776655443322110000000000000766657272756c65" +
            "00000011656e7472792e6e61746976652e6a736f6e" +
            "0000001a776566742e646570656e64656e636965732e656d7074792e7631" +
            "00000012e6b8ace5ae9a2e6e61746976652e6a736f6e" +
            "010000000852c3a973756dc3a90100000000" +
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad" +
            "0000000000000003";
        const string expectedDigest = "f0d917ff93850d32beba5be4c1e351cabbd043c070ba7e185aea53312f7b028b";
        var bytes = Convert.FromHexString(encodedHex);
        var intent = IngestionIntent.ForCreate(new IngestionCreateMetadata("Résumé", ""),
            "ferrule", "測定.native.json", Digest, 3);

        Assert.Equal(225, bytes.Length);
        Assert.Equal(expectedDigest, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        var fingerprint = CanonicalFingerprint.Create(Actor, Project, intent);
        Assert.Equal(1, fingerprint.Version);
        Assert.Equal(expectedDigest, fingerprint.Digest.Value);
        Assert.Equal(fingerprint, CanonicalFingerprint.Restore(1, Sha256Digest.Parse(expectedDigest.ToUpperInvariant())));
    }

    [Fact]
    public void FingerprintIncludesEveryImmutableIntentAndScopedIdentityFact()
    {
        var baseline = CreateIntent();
        var fingerprint = CanonicalFingerprint.Create(Actor, Project, baseline);
        IngestionIntent[] changedIntents =
        [
            CreateIntent(name: "Another name"),
            CreateIntent(description: "Another description"),
            CreateIntent(backendId: "another-backend"),
            CreateIntent(filename: "other.native.json"),
            CreateIntent(digest: Sha256Digest.FromBytes("abd"u8)),
            CreateIntent(byteCount: 4),
            IngestionIntent.ForAppend(Target, "ferrule", "entry.native.json", new AnyHead(), Digest, 3)
        ];
        Assert.All(changedIntents, intent => Assert.NotEqual(fingerprint, CanonicalFingerprint.Create(Actor, Project, intent)));
        Assert.NotEqual(fingerprint, CanonicalFingerprint.Create(new PrincipalId(OtherId), Project, baseline));
        Assert.NotEqual(fingerprint, CanonicalFingerprint.Create(Actor, new ProjectScope(OtherId, ProjectId), baseline));
        Assert.NotEqual(fingerprint, CanonicalFingerprint.Create(Actor, new ProjectScope(OrganizationId, OtherId), baseline));

        var append = IngestionIntent.ForAppend(Target, "ferrule", null, new AnyHead(), Digest, 3);
        var appendFingerprint = CanonicalFingerprint.Create(Actor, Project, append);
        Assert.NotEqual(appendFingerprint, CanonicalFingerprint.Create(Actor, Project,
            IngestionIntent.ForAppend(new MappingScope(OrganizationId, ProjectId, OtherId), "ferrule", null, new AnyHead(), Digest, 3)));
        Assert.NotEqual(appendFingerprint, CanonicalFingerprint.Create(Actor, Project,
            IngestionIntent.ForAppend(Target, "ferrule", null, new EqualHead(RevisionId), Digest, 3)));
        Assert.NotEqual(CanonicalFingerprint.Create(Actor, Project,
                IngestionIntent.ForAppend(Target, "ferrule", null, new EqualHead(RevisionId), Digest, 3)),
            CanonicalFingerprint.Create(Actor, Project,
                IngestionIntent.ForAppend(Target, "ferrule", null, new EqualHead(OtherId), Digest, 3)));
        AssertRejects(() => CanonicalFingerprint.Create(Actor, new ProjectScope(OtherId, ProjectId), append));
        AssertRejects(() => CanonicalFingerprint.Create(Actor, new ProjectScope(OrganizationId, OtherId), append));
    }

    [Fact]
    public void AcceptedFilenameDefaultsAndTrimmedNamesAreCanonicalButUnicodeIsNotNormalized()
    {
        var absent = IngestionIntent.ForCreate(new IngestionCreateMetadata("  Résumé  ", ""), "ferrule", null, Digest, 3);
        var explicitDefault = IngestionIntent.ForCreate(new IngestionCreateMetadata("Résumé", ""), "ferrule", "entry.native.json", Digest, 3);
        var decomposed = IngestionIntent.ForCreate(new IngestionCreateMetadata("Re\u0301sume\u0301", ""), "ferrule", null, Digest, 3);

        Assert.Equal("Résumé", absent.CreateMetadata!.Name);
        Assert.Equal("", absent.CreateMetadata!.Description);
        Assert.Equal("entry.native.json", absent.OriginalFilename);
        Assert.Equal("entry.native.json", absent.LogicalDocument);
        Assert.Equal("weft.dependencies.empty.v1", absent.DependencyIdentity);
        Assert.Equal(CanonicalFingerprint.Create(Actor, Project, absent), CanonicalFingerprint.Create(Actor, Project, explicitDefault));
        Assert.NotEqual(CanonicalFingerprint.Create(Actor, Project, absent), CanonicalFingerprint.Create(Actor, Project, decomposed));
        Assert.NotEqual(CanonicalFingerprint.Create(Actor, Project, absent),
            CanonicalFingerprint.Create(Actor, Project, CreateIntent(name: "Résumé", description: " ")));
        Assert.Null(IngestionIntent.ForAppend(Target, "ferrule", null, new AnyHead(), Digest, 3).CreateMetadata);
    }

    [Fact]
    public void FingerprintRestorationAndCreationRejectMissingOrUnknownFacts()
    {
        foreach (var version in new[] { -1, 0, 2, int.MaxValue })
            AssertRejects(() => CanonicalFingerprint.Restore(version, Digest));
        AssertRejects(() => CanonicalFingerprint.Restore(1, null!));
        AssertRejects(() => CanonicalFingerprint.Create(null!, Project, CreateIntent()));
        AssertRejects(() => CanonicalFingerprint.Create(Actor, null!, CreateIntent()));
        AssertRejects(() => CanonicalFingerprint.Create(Actor, Project, null!));
    }

    [Fact]
    public void MappingScopeIncludesEveryAncestorAndRejectsEmptyIdentifiers()
    {
        Assert.Equal(Target, new MappingScope(OrganizationId, ProjectId, MappingId));
        Assert.NotEqual(Target, new MappingScope(OtherId, ProjectId, MappingId));
        Assert.NotEqual(Target, new MappingScope(OrganizationId, OtherId, MappingId));
        Assert.NotEqual(Target, new MappingScope(OrganizationId, ProjectId, OtherId));
        AssertRejects(() => new MappingScope(Guid.Empty, ProjectId, MappingId));
        AssertRejects(() => new MappingScope(OrganizationId, Guid.Empty, MappingId));
        AssertRejects(() => new MappingScope(OrganizationId, ProjectId, Guid.Empty));
        Assert.Equal(RevisionId, new EqualHead(RevisionId).RevisionId);
        AssertRejects(() => new EqualHead(Guid.Empty));
    }

    [Fact]
    public void IntentFactoriesKeepTheirClosedCreateAndAppendShapes()
    {
        var create = CreateIntent();
        var append = IngestionIntent.ForAppend(Target, "ferrule", "Original.JSON", new EqualHead(RevisionId), Digest, 3);

        Assert.Equal(IngestionOperationKind.Create, create.Kind);
        Assert.Null(create.Target);
        Assert.NotNull(create.CreateMetadata);
        Assert.IsType<AnyHead>(create.HeadCondition);
        Assert.Equal(IngestionOperationKind.Append, append.Kind);
        Assert.Equal(Target, append.Target);
        Assert.Null(append.CreateMetadata);
        Assert.Equal(new EqualHead(RevisionId), append.HeadCondition);
        Assert.Equal("Original.JSON", append.OriginalFilename);
        Assert.Equal(Digest, append.NativeDigest);
        Assert.Equal(3, append.NativeByteCount);
        AssertRejects(() => IngestionIntent.ForCreate(null!, "ferrule", null, Digest, 3));
        AssertRejects(() => IngestionIntent.ForAppend(null!, "ferrule", null, new AnyHead(), Digest, 3));
        AssertRejects(() => IngestionIntent.ForAppend(Target, "ferrule", null, null!, Digest, 3));
        AssertRejects(() => IngestionIntent.ForCreate(new IngestionCreateMetadata("Name", ""), "ferrule", null, null!, 3));
    }

    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(-1L)]
    [InlineData(0L)]
    [InlineData(4194305L)]
    [InlineData(long.MaxValue)]
    public void IntentAndAdmissionPolicyRejectNativeCountsOutsideTheHardCeiling(long count)
    {
        AssertRejects(() => CreateIntent(byteCount: count));
        AssertRejects(() => IngestionIntent.ForAppend(Target, "ferrule", null, new AnyHead(), Digest, count));
        AssertRejects(() => new IngestionAdmissionPolicy("weft.native-admission.v1", count));
    }

    [Fact]
    public void NativeAndPolicyCountBoundariesAreInclusive()
    {
        foreach (var count in new[] { 1L, 4194304L })
        {
            Assert.Equal(count, CreateIntent(byteCount: count).NativeByteCount);
            Assert.Equal(count, new IngestionAdmissionPolicy("weft.native-admission.v1", count).EffectiveNativeByteLimit);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("dir/file.json")]
    [InlineData("dir\\file.json")]
    [InlineData("file\0.json")]
    [InlineData("file\n.json")]
    [InlineData("file\u007f.json")]
    public void FilenameRejectsPathsAndControlCharacters(string filename) =>
        AssertRejects(() => CreateIntent(filename: filename));

    [Fact]
    public void FilenameAndServerProvenanceUseStrictUtf8ByteBounds()
    {
        var acceptedFilename = new string('é', 127) + "a";
        Assert.Equal(255, Encoding.UTF8.GetByteCount(acceptedFilename));
        Assert.Equal(acceptedFilename, CreateIntent(filename: acceptedFilename).OriginalFilename);
        AssertRejects(() => CreateIntent(filename: new string('é', 128)));
        Assert.Equal(new string('é', 64), CreateIntent(backendId: new string('é', 64)).BackendId);
        AssertRejects(() => CreateIntent(backendId: new string('é', 65)));

        var profile = new IngestionBackendProfile(new string('é', 64), new string('é', 128), new string('é', 1024), new string('é', 128));
        Assert.Equal(2048, Encoding.UTF8.GetByteCount(profile.BackendBuild));
        AssertRejects(() => new IngestionBackendProfile(new string('é', 65), "e", "b", "a"));
        AssertRejects(() => new IngestionBackendProfile("ferrule", new string('é', 129), "b", "a"));
        AssertRejects(() => new IngestionBackendProfile("ferrule", "e", new string('é', 1025), "a"));
        AssertRejects(() => new IngestionBackendProfile("ferrule", "e", "b", new string('é', 129)));
        Assert.Equal(new string('é', 64), new IngestionAdmissionPolicy(new string('é', 64), 3).Identity);
        AssertRejects(() => new IngestionAdmissionPolicy(new string('é', 65), 3));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\n")]
    [InlineData("nul\0value")]
    public void RequiredProvenanceTextRejectsNullBlankAndNul(string? value) =>
        AssertRejectsProvenance(value);

    [Fact]
    public void FilenameAndProvenanceRejectRuntimeConstructedUnpairedSurrogates()
    {
        // Attribute/discovery serialization can replace lone surrogates before a theory executes.
        // Build these invalid UTF16 strings at runtime so the strict encoder receives the actual input.
        foreach (var surrogate in new[] { (char)0xd800, (char)0xdfff })
        {
            var value = new string(surrogate, 1);
            Assert.Equal(surrogate, value[0]);
            AssertRejects(() => CreateIntent(filename: "file" + value + ".json"));
            AssertRejectsProvenance(value);
        }
    }

    private static void AssertRejectsProvenance(string? value)
    {
        AssertRejects(() => IngestionIntent.ForCreate(new IngestionCreateMetadata("Name", ""), value!, null, Digest, 3));
        AssertRejects(() => new IngestionBackendProfile(value!, "e", "b", "a"));
        AssertRejects(() => new IngestionBackendProfile("ferrule", value!, "b", "a"));
        AssertRejects(() => new IngestionBackendProfile("ferrule", "e", value!, "a"));
        AssertRejects(() => new IngestionBackendProfile("ferrule", "e", "b", value!));
        AssertRejects(() => new IngestionAdmissionPolicy(value!, 3));
    }

    [Fact]
    public void MetadataBoundsCountUnicodeScalarsAndPreserveDescriptionExactly()
    {
        var name = string.Concat(Enumerable.Repeat("😀", 200));
        var description = string.Concat(Enumerable.Repeat("😀", 4000));
        var metadata = new IngestionCreateMetadata("  " + name + "  ", description);

        Assert.Equal(name, metadata.Name);
        Assert.Equal(description, metadata.Description);
        Assert.Equal(" \t\n ", new IngestionCreateMetadata("Name", " \t\n ").Description);
        AssertRejects(() => new IngestionCreateMetadata(name + "😀", ""));
        AssertRejects(() => new IngestionCreateMetadata("Name", description + "😀"));
        AssertRejects(() => new IngestionCreateMetadata(null!, ""));
        AssertRejects(() => new IngestionCreateMetadata("Name", null!));
        AssertRejects(() => new IngestionCreateMetadata(" \t\n ", ""));
        AssertRejects(() => new IngestionCreateMetadata("\ud800", ""));
        AssertRejects(() => new IngestionCreateMetadata("Name", "\udfff"));
        AssertRejects(() => new IngestionCreateMetadata("nul\0name", ""));
        AssertRejects(() => new IngestionCreateMetadata("Name", "nul\0description"));
    }

    [Fact]
    public void SyntheticEvidenceOwnsItsInputAndEveryReturnedBufferOrDocument()
    {
        var facts = ValidationFacts(IngestionBackendStatus.Succeeded, true, total: 1, emitted: 1, truncated: true);
        IngestionEvidenceDocument evidence;
        using (var diagnostics = JsonDocument.Parse("[{\"code\":\"synthetic.warning\",\"message\":\"kept\"}]"))
            evidence = IngestionEvidenceDocument.Create(facts, diagnostics.RootElement);

        Assert.Equal("weft.ingestion-validation.v1", evidence.Version);
        Assert.Equal(facts, evidence.Facts);
        Assert.Equal("kept", evidence.CopyDocument().GetProperty("diagnostics")[0].GetProperty("message").GetString());
        var firstBytes = evidence.CopyUtf8();
        var preserved = firstBytes.ToArray();
        Array.Fill(firstBytes, (byte)'x');
        Assert.Equal(preserved, evidence.CopyUtf8());

        var restoredInput = evidence.CopyUtf8();
        var restored = IngestionEvidenceDocument.Restore(facts, restoredInput);
        Array.Fill(restoredInput, (byte)'x');
        Assert.Equal(preserved, restored.CopyUtf8());
        var copy = restored.CopyDocument();
        Assert.True(copy.GetProperty("isValid").GetBoolean());
        Assert.True(copy.GetProperty("truncated").GetBoolean());
        Assert.Equal("kept", copy.GetProperty("diagnostics")[0].GetProperty("message").GetString());
    }

    [Fact]
    public void EvidenceRestoresSemanticEnvelopeFactsAfterRepresentationChanges()
    {
        var facts = ValidationFacts(IngestionBackendStatus.InvalidMapping, false, total: 0, emitted: 0);
        var original = Evidence(facts);
        var node = JsonNode.Parse(original.CopyUtf8())!.AsObject();
        var reordered = new JsonObject();
        foreach (var pair in node.Reverse())
            reordered.Add(pair.Key, pair.Value?.DeepClone());
        var bytes = Encoding.UTF8.GetBytes(reordered.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        Assert.NotEqual(Convert.ToHexString(original.CopyUtf8()), Convert.ToHexString(bytes));
        var restored = IngestionEvidenceDocument.Restore(facts, bytes);
        Assert.Equal(facts, restored.Facts);
        Assert.Equal("invalid_mapping", restored.CopyDocument().GetProperty("status").GetString());
        Assert.False(restored.CopyDocument().GetProperty("isValid").GetBoolean());
    }

    [Fact]
    public void EvidenceRestoreChecksEveryTypedEnvelopeFactAndRejectsUnknownOrDuplicateFields()
    {
        var facts = ValidationFacts(IngestionBackendStatus.Succeeded, true);
        var evidence = Evidence(facts);
        Action<JsonObject>[] changes =
        [
            node => node["version"] = "weft.ingestion-validation.v2",
            node => node["scope"]!["organizationId"] = OtherId.ToString("D"),
            node => node["scope"]!["projectId"] = OtherId.ToString("D"),
            node => node["scope"]!["mappingId"] = OtherId.ToString("D"),
            node => node["scope"]!["revisionId"] = OtherId.ToString("D"),
            node => node["scope"]!["revisionId"] = "invalid-uuid",
            node => node["nativeDigest"] = Sha256Digest.FromBytes("abd"u8).Value,
            node => node["nativeDigest"] = "invalid-digest",
            node => node["backendProfile"]!["backendId"] = "another-backend",
            node => node["backendProfile"]!["engineRevision"] = "another-engine",
            node => node["backendProfile"]!["backendBuild"] = "another-build",
            node => node["backendProfile"]!["adapterBuild"] = "another-adapter",
            node => node["confirmed"] = false,
            node => node["status"] = "invalid_mapping",
            node => node["status"] = "Succeeded",
            node => node["isValid"] = false,
            node => node["isValid"] = null,
            node => node["totalIssueCount"] = 1,
            node => node["emittedIssueCount"] = 1,
            node => node["truncated"] = true,
            node => node["confirmed"] = "true",
            node => node["totalIssueCount"] = "0",
            node => node["unexpected"] = true,
            node => node["scope"]!["unexpected"] = true,
            node => node["backendProfile"]!["unexpected"] = true,
            node => node.Remove("isValid"),
            node => node["diagnostics"] = new JsonObject()
        ];
        foreach (var change in changes)
        {
            var changed = JsonNode.Parse(evidence.CopyUtf8())!.AsObject();
            change(changed);
            AssertRejects(() => IngestionEvidenceDocument.Restore(facts, Encoding.UTF8.GetBytes(changed.ToJsonString())));
        }

        var text = Encoding.UTF8.GetString(evidence.CopyUtf8());
        AssertRejects(() => IngestionEvidenceDocument.Restore(facts, Encoding.UTF8.GetBytes(text.Insert(1, "\"confirmed\":true,"))));
        AssertRejects(() => IngestionEvidenceDocument.Restore(facts, Encoding.UTF8.GetBytes(text.Replace(
            "\"organizationId\":", "\"organizationId\":\"" + OrganizationId.ToString("D") + "\",\"organizationId\":", StringComparison.Ordinal))));
        AssertRejects(() => IngestionEvidenceDocument.Restore(facts, [0xff]));
        AssertRejects(() => IngestionEvidenceDocument.Restore(facts, []));
        AssertRejects(() => IngestionEvidenceDocument.Restore(null!, evidence.CopyUtf8()));
    }

    [Fact]
    public void DiagnosticArrayAgreementIsConditionalOnANonnullValidatorResult()
    {
        var warning = ParseDiagnostics("[{\"code\":\"synthetic.warning\"}]");
        Assert.Equal(1, IngestionEvidenceDocument.Create(ValidationFacts(IngestionBackendStatus.Succeeded, true, 1, 1), warning)
            .CopyDocument().GetProperty("diagnostics").GetArrayLength());
        AssertRejects(() => IngestionEvidenceDocument.Create(ValidationFacts(IngestionBackendStatus.Succeeded, true, 1, 1), ParseDiagnostics("[]")));
        AssertRejects(() => IngestionEvidenceDocument.Create(ValidationFacts(IngestionBackendStatus.InvalidMapping, false), warning));

        var operational = IngestionEvidenceDocument.Create(ValidationFacts(IngestionBackendStatus.BackendFailure, null), warning);
        Assert.Null(operational.Facts.IsValid);
        Assert.Equal(0, operational.Facts.EmittedIssueCount);
        Assert.Equal(1, operational.CopyDocument().GetProperty("diagnostics").GetArrayLength());
        Assert.Equal(operational.Facts, IngestionEvidenceDocument.Restore(operational.Facts, operational.CopyUtf8()).Facts);
    }

    [Fact]
    public void TruncatedIsAnIndependentFactRatherThanAnInferenceFromIssueCounts()
    {
        var textTruncated = ValidationFacts(IngestionBackendStatus.Succeeded, true, 1, 1, true);
        var report = IngestionEvidenceDocument.Create(textTruncated, ParseDiagnostics("[{\"code\":\"synthetic.warning\"}]"));
        Assert.True(report.Facts.Truncated);
        Assert.True(IngestionEvidenceDocument.Restore(textTruncated, report.CopyUtf8()).Facts.Truncated);

        var notMarked = ValidationFacts(IngestionBackendStatus.InvalidMapping, false, 2, 1, false);
        Assert.False(IngestionEvidenceDocument.Create(notMarked, ParseDiagnostics("[{\"code\":\"synthetic.issue\"}]")).Facts.Truncated);
    }

    [Fact]
    public void ValidationFactsRejectUnknownStatusesMissingProvenanceAndIncoherentCounts()
    {
        foreach (var status in new[] { -1, 0, 11, int.MaxValue })
            AssertRejects(() => ValidationFacts((IngestionBackendStatus)status, null));
        AssertRejects(() => new IngestionValidationFacts(IngestionBackendStatus.Succeeded, null!, true, 0, 0, false));
        AssertRejects(() => ValidationFacts(IngestionBackendStatus.Succeeded, true, -1, 0));
        AssertRejects(() => ValidationFacts(IngestionBackendStatus.Succeeded, true, 32769, 0));
        AssertRejects(() => ValidationFacts(IngestionBackendStatus.Succeeded, true, 1, -1));
        AssertRejects(() => ValidationFacts(IngestionBackendStatus.Succeeded, true, 0, 1));
        AssertRejects(() => ValidationFacts(IngestionBackendStatus.Succeeded, true, 129, 129));
        Assert.Equal(32768, ValidationFacts(IngestionBackendStatus.InvalidMapping, false, 32768, 128).TotalIssueCount);
        AssertRejects(() => new IngestionProvenance(null!, Digest, Profile, true));
        AssertRejects(() => new IngestionProvenance(Scope, null!, Profile, true));
        AssertRejects(() => new IngestionProvenance(Scope, Digest, null!, true));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[1]")]
    [InlineData("[{}]")]
    [InlineData("[{\"code\":null}]")]
    [InlineData("[{\"code\":1}]")]
    [InlineData("[{\"code\":\"\"}]")]
    [InlineData("[{\"code\":\"synthetic.issue\",\"message\":\"\\ud800\"}]")]
    [InlineData("[{\"code\":\"synthetic.issue\",\"message\":\"\\u0000\"}]")]
    [InlineData("[{\"code\":\"synthetic.issue\",\"\\u0000\":\"value\"}]")]
    public void EvidenceRequiresDiagnosticObjectsWithBoundedCodesAndStrictUnicode(string diagnostics) =>
        AssertRejects(() => IngestionEvidenceDocument.Create(ValidationFacts(IngestionBackendStatus.BackendFailure, null), ParseDiagnostics(diagnostics)));

    [Fact]
    public void DiagnosticCountAndUtf8StringValueBoundsAreInclusive()
    {
        var facts = ValidationFacts(IngestionBackendStatus.InvalidMapping, false, 128, 128);
        var allowed = JsonSerializer.Serialize(Enumerable.Range(0, 128).Select(_ => new { code = "synthetic.issue" }));
        Assert.Equal(128, IngestionEvidenceDocument.Create(facts, ParseDiagnostics(allowed)).CopyDocument().GetProperty("diagnostics").GetArrayLength());
        var excessive = JsonSerializer.Serialize(Enumerable.Range(0, 129).Select(_ => new { code = "synthetic.issue" }));
        AssertRejects(() => IngestionEvidenceDocument.Create(ValidationFacts(IngestionBackendStatus.BackendFailure, null), ParseDiagnostics(excessive)));

        var value = new string('é', 1024);
        var diagnostics = JsonSerializer.Serialize(new[] { new { code = "synthetic.issue", message = value } });
        Assert.Equal(value, IngestionEvidenceDocument.Create(ValidationFacts(IngestionBackendStatus.InvalidMapping, false, 1, 1), ParseDiagnostics(diagnostics))
            .CopyDocument().GetProperty("diagnostics")[0].GetProperty("message").GetString());
        var tooLong = JsonSerializer.Serialize(new[] { new { code = "synthetic.issue", message = value + "a" } });
        AssertRejects(() => IngestionEvidenceDocument.Create(ValidationFacts(IngestionBackendStatus.BackendFailure, null), ParseDiagnostics(tooLong)));
        var tooLongCode = JsonSerializer.Serialize(new[] { new { code = new string('x', 2049) } });
        AssertRejects(() => IngestionEvidenceDocument.Create(ValidationFacts(IngestionBackendStatus.BackendFailure, null), ParseDiagnostics(tooLongCode)));
    }

    [Fact]
    public void EvidenceRejectsExcessiveDepthAggregateNodesAndSerializedBytesWithoutTruncating()
    {
        var facts = ValidationFacts(IngestionBackendStatus.BackendFailure, null);
        var atDepthLimit = "[{\"code\":\"synthetic.issue\",\"details\":" + new string('[', 61) + "0" + new string(']', 61) + "}]";
        Assert.Equal(facts, IngestionEvidenceDocument.Create(facts, ParseDiagnostics(atDepthLimit)).Facts);
        var aboveDepthLimit = "[{\"code\":\"synthetic.issue\",\"details\":" + new string('[', 62) + "0" + new string(']', 62) + "}]";
        AssertRejects(() => IngestionEvidenceDocument.Create(facts, ParseDiagnostics(aboveDepthLimit)));
        var tooDeep = "[{\"code\":\"synthetic.issue\",\"details\":" + new string('[', 65) + "0" + new string(']', 65) + "}]";
        AssertRejects(() => IngestionEvidenceDocument.Create(facts, ParseDiagnostics(tooDeep)));
        var tooManyNodes = JsonSerializer.Serialize(new[] { new { code = "synthetic.issue", details = Enumerable.Repeat(0, 32768).ToArray() } });
        AssertRejects(() => IngestionEvidenceDocument.Create(facts, ParseDiagnostics(tooManyNodes)));
        var boundedNodes = JsonSerializer.Serialize(new[] { new { code = "synthetic.issue", details = Enumerable.Repeat(0, 32000).ToArray() } });
        Assert.Equal(32000, IngestionEvidenceDocument.Create(facts, ParseDiagnostics(boundedNodes))
            .CopyDocument().GetProperty("diagnostics")[0].GetProperty("details").GetArrayLength());

        var largeValue = new string('x', 2048);
        var tooManyBytes = JsonSerializer.Serialize(new[] { new { code = "synthetic.issue", details = Enumerable.Repeat(largeValue, 4096).ToArray() } });
        AssertRejects(() => IngestionEvidenceDocument.Create(facts, ParseDiagnostics(tooManyBytes)));
        var boundedBytes = JsonSerializer.Serialize(new[] { new { code = "synthetic.issue", details = Enumerable.Repeat(largeValue, 4000).ToArray() } });
        var bounded = IngestionEvidenceDocument.Create(facts, ParseDiagnostics(boundedBytes));
        Assert.InRange(bounded.CopyUtf8().Length, 8_000_000, 8_388_608);
        Assert.Equal(4000, IngestionEvidenceDocument.Restore(facts, bounded.CopyUtf8()).CopyDocument()
            .GetProperty("diagnostics")[0].GetProperty("details").GetArrayLength());
        var oversizedInput = new byte[8_388_609];
        Array.Fill(oversizedInput, (byte)' ');
        Evidence(facts).CopyUtf8().CopyTo(oversizedInput, 0);
        AssertRejects(() => IngestionEvidenceDocument.Restore(facts, oversizedInput));
    }

    [Fact]
    public void ReservedFactsPreserveOriginalIdentityAndDeploymentFactsIndependentlyOfTheFingerprint()
    {
        var original = Reserved();
        var upgraded = Reserved(operationId: Guid.Parse("50617283-94a5-b6c7-d8e9-fa0b1c2d3e4f"),
            idempotencyKey: Guid.Parse("60718293-a4b5-c6d7-e8f9-0a1b2c3d4e5f"),
            profile: new IngestionBackendProfile("ferrule", "synthetic-engine-v2", "synthetic-backend-v2", "synthetic-adapter-v2"),
            policy: new IngestionAdmissionPolicy("synthetic-policy-v2", 4));

        Assert.Equal(original.Fingerprint, upgraded.Fingerprint);
        Assert.NotEqual(original.OperationId, upgraded.OperationId);
        Assert.NotEqual(original.IdempotencyKey, upgraded.IdempotencyKey);
        Assert.Equal(Profile, original.BackendProfile);
        Assert.Equal("weft.native-admission.v1", original.AdmissionPolicy.Identity);
        Assert.Equal(3, original.NativeObject.ByteCount);
        Assert.Equal(Digest, original.NativeObject.Digest);
        Assert.Equal(Scope, original.FutureRevision);
        Assert.Equal(TimeSpan.Zero, original.ReservedAtUtc.Offset);
        Assert.Equal(Timestamp.ToUniversalTime(), original.ReservedAtUtc);
        Assert.Equal(original, new ReservedIngestionFacts(original.Project, original.OperationId, original.OriginalActor,
            original.IdempotencyKey, original.Fingerprint, original.Intent, original.FutureRevision, original.NativeObject,
            original.BackendProfile, original.AdmissionPolicy, Timestamp));
    }

    [Fact]
    public void ReservedFactsRejectDisagreementAcrossScopeNativeIdentityIntentProfileAndPolicy()
    {
        var baseline = Reserved();
        Action[] invalid =
        [
            () => Reserved(operationId: Guid.Empty),
            () => Reserved(idempotencyKey: Guid.Empty),
            () => Reserved(fingerprint: CanonicalFingerprint.Restore(1, Sha256Digest.FromBytes("other"u8))),
            () => Reserved(futureRevision: new RevisionScope(OtherId, ProjectId, MappingId, RevisionId)),
            () => Reserved(futureRevision: new RevisionScope(OrganizationId, OtherId, MappingId, RevisionId)),
            () => Reserved(nativeObject: NativeReference(new RevisionScope(OrganizationId, ProjectId, OtherId, RevisionId))),
            () => Reserved(nativeObject: NativeReference(new RevisionScope(OrganizationId, ProjectId, MappingId, OtherId))),
            () => Reserved(nativeObject: NativeReference(@class: PrivateObjectClass.DerivedArtifact)),
            () => Reserved(nativeObject: NativeReference(digest: Sha256Digest.FromBytes("abd"u8))),
            () => Reserved(nativeObject: NativeReference(byteCount: 4)),
            () => Reserved(profile: new IngestionBackendProfile("another-backend", "e", "b", "a")),
            () => Reserved(policy: new IngestionAdmissionPolicy("weft.native-admission.v1", 2)),
            () => Reserved(intent: IngestionIntent.ForAppend(new MappingScope(OrganizationId, ProjectId, OtherId),
                "ferrule", null, new AnyHead(), Digest, 3)),
            () => new ReservedIngestionFacts(null!, baseline.OperationId, Actor, baseline.IdempotencyKey, baseline.Fingerprint,
                baseline.Intent, Scope, baseline.NativeObject, Profile, baseline.AdmissionPolicy, Timestamp),
            () => new ReservedIngestionFacts(Project, baseline.OperationId, null!, baseline.IdempotencyKey, baseline.Fingerprint,
                baseline.Intent, Scope, baseline.NativeObject, Profile, baseline.AdmissionPolicy, Timestamp),
            () => new ReservedIngestionFacts(Project, baseline.OperationId, Actor, baseline.IdempotencyKey, null!,
                baseline.Intent, Scope, baseline.NativeObject, Profile, baseline.AdmissionPolicy, Timestamp),
            () => new ReservedIngestionFacts(Project, baseline.OperationId, Actor, baseline.IdempotencyKey, baseline.Fingerprint,
                null!, Scope, baseline.NativeObject, Profile, baseline.AdmissionPolicy, Timestamp),
            () => new ReservedIngestionFacts(Project, baseline.OperationId, Actor, baseline.IdempotencyKey, baseline.Fingerprint,
                baseline.Intent, null!, baseline.NativeObject, Profile, baseline.AdmissionPolicy, Timestamp),
            () => new ReservedIngestionFacts(Project, baseline.OperationId, Actor, baseline.IdempotencyKey, baseline.Fingerprint,
                baseline.Intent, Scope, null!, Profile, baseline.AdmissionPolicy, Timestamp),
            () => new ReservedIngestionFacts(Project, baseline.OperationId, Actor, baseline.IdempotencyKey, baseline.Fingerprint,
                baseline.Intent, Scope, baseline.NativeObject, null!, baseline.AdmissionPolicy, Timestamp),
            () => new ReservedIngestionFacts(Project, baseline.OperationId, Actor, baseline.IdempotencyKey, baseline.Fingerprint,
                baseline.Intent, Scope, baseline.NativeObject, Profile, null!, Timestamp)
        ];
        Assert.All(invalid, AssertRejects);
        Assert.Equal(Target, Reserved(intent: IngestionIntent.ForAppend(Target, "ferrule", null, new AnyHead(), Digest, 3)).Intent.Target);
    }

    [Fact]
    public void SyntheticEligibleAdmissionDependsOnMatchedConfirmedValidityRatherThanIssueCounts()
    {
        var reserved = Reserved();
        var validWithWarning = IngestionAttemptCompletion.FromValidation(Evidence(
            ValidationFacts(IngestionBackendStatus.Succeeded, true, 1, 1, true), "[{\"code\":\"synthetic.warning\"}]"));
        var invalidWithoutFindings = IngestionAttemptCompletion.FromValidation(Evidence(
            ValidationFacts(IngestionBackendStatus.InvalidMapping, false)));

        Assert.Equal(IngestionAdmissionClassification.EligibleValid, validWithWarning.Classify(reserved));
        Assert.Equal(IngestionAdmissionClassification.EligibleDecodedInvalid, invalidWithoutFindings.Classify(reserved));
        Assert.True(validWithWarning.Evidence!.Facts.Truncated);
        Assert.Equal(0, invalidWithoutFindings.Evidence!.Facts.TotalIssueCount);
    }

    [Theory]
    [InlineData(IngestionBackendStatus.Succeeded, null)]
    [InlineData(IngestionBackendStatus.Succeeded, false)]
    [InlineData(IngestionBackendStatus.InvalidMapping, true)]
    [InlineData(IngestionBackendStatus.InvalidMapping, null)]
    [InlineData(IngestionBackendStatus.UnsupportedArtifact, true)]
    [InlineData(IngestionBackendStatus.ResourceLimit, false)]
    [InlineData(IngestionBackendStatus.ProtocolFailure, true)]
    [InlineData(IngestionBackendStatus.BackendUnavailable, null)]
    [InlineData(IngestionBackendStatus.BackendFailure, false)]
    [InlineData(IngestionBackendStatus.TimedOut, null)]
    [InlineData(IngestionBackendStatus.Canceled, true)]
    [InlineData(IngestionBackendStatus.IsolationFailure, false)]
    public void NullContradictoryAndFailedSyntheticOutcomesCannotBecomeEligible(IngestionBackendStatus status, bool? isValid)
    {
        var completion = IngestionAttemptCompletion.FromValidation(Evidence(ValidationFacts(status, isValid)));
        Assert.Equal(IngestionAdmissionClassification.RetryableFailure, completion.Classify(Reserved()));
    }

    [Fact]
    public void EveryProvenanceMismatchAndUnconfirmedResultFailsClosedBeforeEligibilityOrRejection()
    {
        IngestionProvenance[] mismatches =
        [
            new(new RevisionScope(OtherId, ProjectId, MappingId, RevisionId), Digest, Profile, true),
            new(new RevisionScope(OrganizationId, OtherId, MappingId, RevisionId), Digest, Profile, true),
            new(new RevisionScope(OrganizationId, ProjectId, OtherId, RevisionId), Digest, Profile, true),
            new(new RevisionScope(OrganizationId, ProjectId, MappingId, OtherId), Digest, Profile, true),
            new(Scope, Sha256Digest.FromBytes("abd"u8), Profile, true),
            new(Scope, Digest, new IngestionBackendProfile("another-backend", "e", "b", "a"), true),
            new(Scope, Digest, new IngestionBackendProfile("ferrule", "another-engine", Profile.BackendBuild, Profile.AdapterBuild), true),
            new(Scope, Digest, new IngestionBackendProfile("ferrule", Profile.EngineRevision, "another-build", Profile.AdapterBuild), true),
            new(Scope, Digest, new IngestionBackendProfile("ferrule", Profile.EngineRevision, Profile.BackendBuild, "another-adapter"), true)
        ];
        foreach (var provenance in mismatches)
        {
            Assert.Equal(IngestionAdmissionClassification.RetryableFailure,
                IngestionAttemptCompletion.FromValidation(Evidence(ValidationFacts(IngestionBackendStatus.Succeeded, true, provenance: provenance))).Classify(Reserved()));
            Assert.Equal(IngestionAdmissionClassification.RetryableFailure,
                IngestionAttemptCompletion.FromValidation(Evidence(ValidationFacts(IngestionBackendStatus.UnsupportedArtifact, null, provenance: provenance))).Classify(Reserved()));
        }
        var unconfirmed = new IngestionProvenance(Scope, Digest, Profile, false);
        foreach (var pair in new[] { (IngestionBackendStatus.Succeeded, true), (IngestionBackendStatus.InvalidMapping, false) })
            Assert.Equal(IngestionAdmissionClassification.RetryableFailure,
                IngestionAttemptCompletion.FromValidation(Evidence(ValidationFacts(pair.Item1, pair.Item2, provenance: unconfirmed))).Classify(Reserved()));
        Assert.Equal(IngestionAdmissionClassification.RetryableFailure, IngestionAttemptCompletion.FromValidation(Evidence(
            ValidationFacts(IngestionBackendStatus.Succeeded, true, 1, 1), "[{\"code\":\"ferrule.digestMismatch\"}]")).Classify(Reserved()));
        Assert.Equal(IngestionAdmissionClassification.RetryableFailure, IngestionAttemptCompletion.FromValidation(Evidence(
            ValidationFacts(IngestionBackendStatus.InvalidMapping, false, 1, 1), "[{\"code\":\"ferrule.digestMismatch\"}]")).Classify(Reserved()));
    }

    [Fact]
    public void NativeDecodeUnsupportedAndKnownResourceRejectionsPreserveActualProvenanceWithoutValidatorFacts()
    {
        var reserved = Reserved();
        var decode = IngestionAttemptCompletion.FromValidation(Evidence(ValidationFacts(IngestionBackendStatus.InvalidMapping, null),
            "[{\"code\":\"ferrule.nativeDecodeFailed\"}]"));
        Assert.Equal(IngestionAdmissionClassification.RejectedNativeDecode, decode.Classify(reserved));
        var unconfirmed = new IngestionProvenance(Scope, Digest, Profile, false);
        Assert.Equal(IngestionAdmissionClassification.RetryableFailure,
            IngestionAttemptCompletion.FromValidation(Evidence(ValidationFacts(IngestionBackendStatus.InvalidMapping, null, provenance: unconfirmed),
                "[{\"code\":\"ferrule.nativeDecodeFailed\"}]")).Classify(reserved));

        foreach (var pair in new[]
        {
            (IngestionBackendStatus.UnsupportedArtifact, IngestionAdmissionClassification.RejectedUnsupported),
            (IngestionBackendStatus.ResourceLimit, IngestionAdmissionClassification.RejectedResourceLimit)
        })
        {
            var completion = IngestionAttemptCompletion.FromValidation(Evidence(ValidationFacts(pair.Item1, null, provenance: unconfirmed),
                "[{\"code\":\"synthetic.operational\"}]"));
            Assert.Equal(pair.Item2, completion.Classify(reserved));
            Assert.False(completion.Evidence!.Facts.Provenance.Confirmed);
            Assert.Null(completion.Evidence!.Facts.IsValid);
        }
    }

    [Fact]
    public void CompletionFactoriesKeepValidationOperationalAndRecoveryBranchesSeparate()
    {
        var validation = IngestionAttemptCompletion.FromValidation(Evidence(ValidationFacts(IngestionBackendStatus.Succeeded, true)));
        Assert.Equal(IngestionAttemptCompletionKind.Validation, validation.Kind);
        Assert.NotNull(validation.Evidence);
        Assert.Null(validation.Failure);
        Assert.Null(validation.ReusedAttemptGeneration);
        Assert.Null(validation.ObservedProvenance);

        foreach (var failure in Enum.GetValues<IngestionFailureCode>())
        {
            var operational = IngestionAttemptCompletion.FromOperationalFailure(failure);
            Assert.Equal(IngestionAttemptCompletionKind.OperationalFailure, operational.Kind);
            Assert.Equal(failure, operational.Failure);
            Assert.Null(operational.Evidence);
            Assert.Null(operational.ReusedAttemptGeneration);
            Assert.Null(operational.ObservedProvenance);
            Assert.Equal(failure == IngestionFailureCode.EvidenceLimit
                ? IngestionAdmissionClassification.RejectedResourceLimit : IngestionAdmissionClassification.RetryableFailure,
                operational.Classify(Reserved()));
        }
        Assert.Equal(11, (int)IngestionFailureCode.EvidenceLimit);
        var recovery = IngestionAttemptCompletion.ReuseEligibleEvidence(1);
        Assert.Equal(IngestionAttemptCompletionKind.ReusedEvidence, recovery.Kind);
        Assert.Equal(1, recovery.ReusedAttemptGeneration);
        Assert.Equal(IngestionAdmissionClassification.ReusedEvidence, recovery.Classify(Reserved()));
        Assert.Null(recovery.Evidence);
        Assert.Null(recovery.Failure);
        Assert.Null(recovery.ObservedProvenance);
        AssertRejects(() => IngestionAttemptCompletion.FromValidation(null!));
        foreach (var failure in new[] { -1, 0, 12, int.MaxValue })
            AssertRejects(() => IngestionAttemptCompletion.FromOperationalFailure((IngestionFailureCode)failure));
        foreach (var generation in new[] { long.MinValue, -1L, 0L })
            AssertRejects(() => IngestionAttemptCompletion.ReuseEligibleEvidence(generation));
        AssertRejects(() => IngestionAttemptCompletion.ReuseEligibleEvidence(9));
        AssertRejects(() => recovery.Classify(null!));
    }

    [Fact]
    public void EvidenceLimitIsASeparatePolicyFailureWithOptionalActualObservedProvenance()
    {
        var observed = new IngestionProvenance(Scope, Digest, Profile, false);
        var failure = IngestionAttemptCompletion.FromOperationalFailure(IngestionFailureCode.EvidenceLimit, observed);

        Assert.Equal(IngestionAdmissionClassification.RejectedResourceLimit, failure.Classify(Reserved()));
        Assert.Equal(observed, failure.ObservedProvenance);
        Assert.False(failure.ObservedProvenance!.Confirmed);
        Assert.Null(failure.Evidence);
        Assert.Null(failure.ReusedAttemptGeneration);
        var actualMismatchedProvenance = new IngestionProvenance(
            new RevisionScope(OtherId, ProjectId, MappingId, RevisionId), Sha256Digest.FromBytes("observed"u8), Profile, false);
        var mismatched = IngestionAttemptCompletion.FromOperationalFailure(IngestionFailureCode.EvidenceLimit, actualMismatchedProvenance);
        Assert.Equal(IngestionAdmissionClassification.RejectedResourceLimit, mismatched.Classify(Reserved()));
        Assert.Equal(actualMismatchedProvenance, mismatched.ObservedProvenance);
        Assert.Null(mismatched.Evidence);
        foreach (var category in Enum.GetValues<IngestionFailureCode>().Where(value => value != IngestionFailureCode.EvidenceLimit))
            AssertRejects(() => IngestionAttemptCompletion.FromOperationalFailure(category, observed));
    }

    [Fact]
    public void ClaimOwnsOnlyFencingFactsAndNormalizesDatabaseTimeToUtc()
    {
        var facts = Reserved();
        var claim = Claim(facts, 1);

        Assert.Equal(facts.Project, claim.Project);
        Assert.Equal(facts.OperationId, claim.OperationId);
        Assert.Equal(facts.OriginalActor, claim.OriginalActor);
        Assert.NotEqual(Guid.Empty, claim.ServerInstanceId);
        Assert.NotEqual(Guid.Empty, claim.LeaseToken);
        Assert.Equal(1, claim.Generation);
        Assert.Equal(TimeSpan.Zero, claim.LeaseExpiresAtUtc.Offset);
        Assert.Equal(Timestamp.AddMinutes(2).ToUniversalTime(), claim.LeaseExpiresAtUtc);
        AssertRejects(() => new IngestionClaim(null!, facts.OperationId, Actor, OtherId, OtherId, 1, Timestamp));
        AssertRejects(() => new IngestionClaim(Project, Guid.Empty, Actor, OtherId, OtherId, 1, Timestamp));
        AssertRejects(() => new IngestionClaim(Project, facts.OperationId, null!, OtherId, OtherId, 1, Timestamp));
        AssertRejects(() => new IngestionClaim(Project, facts.OperationId, Actor, Guid.Empty, OtherId, 1, Timestamp));
        AssertRejects(() => new IngestionClaim(Project, facts.OperationId, Actor, OtherId, Guid.Empty, 1, Timestamp));
        AssertRejects(() => Claim(facts, 0));
        AssertRejects(() => Claim(facts, -1));
        Assert.Equal(8, Claim(facts, 8).Generation);
        AssertRejects(() => Claim(facts, 9));
        Assert.DoesNotContain(typeof(IngestionReceipt).GetProperties(), property => property.Name is "LeaseToken" or "ServerInstanceId");
        Assert.DoesNotContain(typeof(IngestionAttemptReceipt).GetProperties(), property => property.Name is "LeaseToken" or "ServerInstanceId");
    }

    [Fact]
    public void PendingAndCompletedAttemptReceiptsHaveConsistentImmutableCompletionFacts()
    {
        var pending = new IngestionAttemptReceipt(1, Timestamp, null, null, null);
        Assert.Null(pending.FinishedAtUtc);
        Assert.Null(pending.Completion);
        Assert.Null(pending.Classification);
        Assert.Equal(TimeSpan.Zero, pending.StartedAtUtc.Offset);
        var completion = IngestionAttemptCompletion.FromOperationalFailure(IngestionFailureCode.StorageIo);
        var completed = new IngestionAttemptReceipt(1, Timestamp, Timestamp.AddSeconds(1), completion,
            IngestionAdmissionClassification.RetryableFailure);
        Assert.Equal(completion, completed.Completion);
        Assert.Equal(IngestionAdmissionClassification.RetryableFailure, completed.Classification);
        Assert.Equal(TimeSpan.Zero, completed.FinishedAtUtc!.Value.Offset);
        AssertRejects(() => new IngestionAttemptReceipt(0, Timestamp, null, null, null));
        AssertRejects(() => new IngestionAttemptReceipt(-1, Timestamp, null, null, null));
        AssertRejects(() => new IngestionAttemptReceipt(1, Timestamp, Timestamp.AddSeconds(-1), completion, IngestionAdmissionClassification.RetryableFailure));
        AssertRejects(() => new IngestionAttemptReceipt(1, Timestamp, Timestamp.AddSeconds(1), null, null));
        AssertRejects(() => new IngestionAttemptReceipt(1, Timestamp, null, completion, IngestionAdmissionClassification.RetryableFailure));
        AssertRejects(() => new IngestionAttemptReceipt(1, Timestamp, Timestamp.AddSeconds(1), completion, null));
        AssertRejects(() => new IngestionAttemptReceipt(1, Timestamp, Timestamp.AddSeconds(1), completion, (IngestionAdmissionClassification)0));
    }

    [Fact]
    public void ReceiptOwnsAnAscendingAttemptSnapshotAndKeepsOriginalEligibilitySeparateFromRecovery()
    {
        var facts = Reserved();
        var eligible = EligibleAttempt();
        var pending = new IngestionAttemptReceipt(2, Timestamp.AddSeconds(3), null, null, null);
        var source = new List<IngestionAttemptReceipt> { pending, eligible };
        var reclaimed = new IngestionReceipt(facts, IngestionPhase.Ready, 2, 2, 1, Timestamp.AddSeconds(1), 1, source);
        source.Clear();

        Assert.Equal(new long[] { 1, 2 }, reclaimed.Attempts.Select(attempt => attempt.Generation));
        Assert.Equal(1, reclaimed.EligibleAttemptGeneration);
        Assert.Equal(1, reclaimed.VerifiedStageGeneration);
        Assert.Null(reclaimed.Attempts[1].Completion);
        Assert.Null(reclaimed.CommittedRevision);
        Assert.Null(reclaimed.CompletedAtUtc);
        var exposed = Assert.IsAssignableFrom<IList<IngestionAttemptReceipt>>(reclaimed.Attempts);
        Assert.Throws<NotSupportedException>(() => exposed.Clear());

        var recovery = new IngestionAttemptReceipt(2, Timestamp.AddSeconds(3), Timestamp.AddSeconds(5),
            IngestionAttemptCompletion.ReuseEligibleEvidence(1), IngestionAdmissionClassification.ReusedEvidence);
        var recovered = new IngestionReceipt(facts, IngestionPhase.Ready, 2, 2, 2, Timestamp.AddSeconds(4), 1,
            [eligible, recovery]);
        Assert.Equal(1, recovered.EligibleAttemptGeneration);
        Assert.Equal(2, recovered.VerifiedStageGeneration);
        Assert.Equal(IngestionAdmissionClassification.ReusedEvidence, recovered.Attempts[1].Classification);
        Assert.Equal(1, recovered.Attempts[1].Completion!.ReusedAttemptGeneration);
        Assert.Null(recovered.Attempts[1].Completion!.Evidence);
        Assert.Same(eligible.Completion!.Evidence, recovered.Attempts[0].Completion!.Evidence);

        var committed = Committed(facts);
        AssertRejects(() => new IngestionReceipt(facts, IngestionPhase.Committed, 2, 2, 1, Timestamp.AddSeconds(1), 1,
            [eligible, recovery], committed, Timestamp.AddSeconds(6)));
        AssertRejects(() => new IngestionReceipt(facts, IngestionPhase.Committed, 2, 2, 2, Timestamp.AddSeconds(4), 1,
            [eligible, pending], committed, Timestamp.AddSeconds(6)));
        var published = new IngestionReceipt(facts, IngestionPhase.Committed, 2, 2, 2, Timestamp.AddSeconds(4), 1,
            [eligible, recovery], committed, Timestamp.AddSeconds(6));
        Assert.Equal(1, published.CommittedRevision!.InitialAdmissionAttemptGeneration);
        Assert.Equal(IngestionAdmissionClassification.EligibleValid, published.CommittedRevision!.InitialAdmission);
        Assert.Equal(TimeSpan.Zero, published.CompletedAtUtc!.Value.Offset);
        AssertRejects(() => IngestionStoreResult.ForClaim(published, Claim(facts, 2)));
    }

    [Fact]
    public void ReceiptsRejectUnprovenEligibilityIncorrectRecoveryAndIncoherentGenerationOrStageFacts()
    {
        var facts = Reserved();
        var pending = new IngestionAttemptReceipt(1, Timestamp, null, null, null);
        var eligible = EligibleAttempt();
        var failure = new IngestionAttemptReceipt(1, Timestamp, Timestamp.AddSeconds(2),
            IngestionAttemptCompletion.FromOperationalFailure(IngestionFailureCode.StorageIo), IngestionAdmissionClassification.RetryableFailure);
        var relabeled = new IngestionAttemptReceipt(1, Timestamp, Timestamp.AddSeconds(2),
            failure.Completion, IngestionAdmissionClassification.EligibleValid);
        AssertRejects(() => new IngestionAttemptReceipt(2, Timestamp.AddSeconds(3), Timestamp.AddSeconds(5),
            IngestionAttemptCompletion.ReuseEligibleEvidence(2), IngestionAdmissionClassification.ReusedEvidence));
        var recovery = new IngestionAttemptReceipt(2, Timestamp.AddSeconds(3), Timestamp.AddSeconds(5),
            IngestionAttemptCompletion.ReuseEligibleEvidence(1), IngestionAdmissionClassification.ReusedEvidence);
        var laterEligible = new IngestionAttemptReceipt(2, Timestamp.AddSeconds(3), Timestamp.AddSeconds(5),
            eligible.Completion, IngestionAdmissionClassification.EligibleValid);
        Action[] invalid =
        [
            () => new IngestionReceipt(null!, IngestionPhase.Reserved, 0, 0, null, null, null, []),
            () => new IngestionReceipt(facts, (IngestionPhase)0, 0, 0, null, null, null, []),
            () => new IngestionReceipt(facts, IngestionPhase.Reserved, -1, 0, null, null, null, []),
            () => new IngestionReceipt(facts, IngestionPhase.Reserved, 0, -1, null, null, null, []),
            () => new IngestionReceipt(facts, IngestionPhase.Reserved, 9, 9, null, null, null, []),
            () => new IngestionReceipt(facts, IngestionPhase.Reserved, 1, 0, null, null, null, [pending]),
            () => new IngestionReceipt(facts, IngestionPhase.Reserved, 1, 1, null, null, null, []),
            () => new IngestionReceipt(facts, IngestionPhase.Staged, 1, 1, 1, null, null, [pending]),
            () => new IngestionReceipt(facts, IngestionPhase.Staged, 1, 1, null, Timestamp, null, [pending]),
            () => new IngestionReceipt(facts, IngestionPhase.Staged, 1, 1, 2, Timestamp, null, [pending]),
            () => new IngestionReceipt(facts, IngestionPhase.Ready, 1, 1, null, null, 1, [eligible]),
            () => new IngestionReceipt(facts, IngestionPhase.Ready, 1, 1, 1, Timestamp.AddSeconds(1), 1, [pending]),
            () => new IngestionReceipt(facts, IngestionPhase.Ready, 1, 1, 1, Timestamp.AddSeconds(1), 1, [failure]),
            () => new IngestionReceipt(facts, IngestionPhase.Ready, 1, 1, 1, Timestamp.AddSeconds(1), 1, [relabeled]),
            () => new IngestionReceipt(facts, IngestionPhase.Ready, 1, 1, 1, Timestamp.AddSeconds(1), null, [eligible]),
            () => new IngestionReceipt(facts, IngestionPhase.Ready, 2, 2, 2, Timestamp.AddSeconds(4), 1, [failure, recovery]),
            () => new IngestionReceipt(facts, IngestionPhase.Ready, 2, 2, 2, Timestamp.AddSeconds(4), null, [eligible, recovery]),
            () => new IngestionReceipt(facts, IngestionPhase.Ready, 2, 2, 2, Timestamp.AddSeconds(4), 2, [eligible, laterEligible]),
            () => new IngestionReceipt(facts, IngestionPhase.Ready, 2, 2, 2, Timestamp.AddSeconds(4), null, [eligible, laterEligible]),
            () => new IngestionReceipt(facts, IngestionPhase.Ready, 2, 2, 2, Timestamp.AddSeconds(4), 1, [eligible, eligible])
        ];
        Assert.All(invalid, AssertRejects);
        var unconfirmed = ValidationFacts(IngestionBackendStatus.Succeeded, true,
            provenance: new IngestionProvenance(Scope, Digest, Profile, false));
        var unconfirmedAttempt = new IngestionAttemptReceipt(1, Timestamp, Timestamp.AddSeconds(2),
            IngestionAttemptCompletion.FromValidation(Evidence(unconfirmed)), IngestionAdmissionClassification.RetryableFailure);
        AssertRejects(() => new IngestionReceipt(facts, IngestionPhase.Ready, 1, 1, 1, Timestamp.AddSeconds(1), 1, [unconfirmedAttempt]));
    }

    [Fact]
    public void TerminalReceiptAndCommittedRevisionGuardsPreserveReservedFactsAndOriginalAdmission()
    {
        var facts = Reserved();
        var eligible = EligibleAttempt();
        var committed = Committed(facts);
        Assert.Equal(facts.FutureRevision, committed.Scope);
        Assert.Equal(facts.NativeObject, committed.NativeObject);
        Assert.Equal(facts.BackendProfile, committed.BackendProfile);
        Assert.Equal(facts.OriginalActor, committed.Creator);
        Assert.Equal(facts.OperationId, committed.OperationId);
        Assert.Equal(TimeSpan.Zero, committed.CommittedAtUtc.Offset);
        AssertRejects(() => new IngestionReceipt(facts, IngestionPhase.Committed, 1, 1, 1, Timestamp.AddSeconds(1), 1, [eligible], committed));
        AssertRejects(() => new IngestionReceipt(facts, IngestionPhase.Committed, 1, 1, 1, Timestamp.AddSeconds(1), 1, [eligible], null, Timestamp.AddSeconds(6)));
        AssertRejects(() => new IngestionReceipt(facts, IngestionPhase.Ready, 1, 1, 1, Timestamp.AddSeconds(1), 1, [eligible], committed));
        AssertRejects(() => new IngestionReceipt(facts, IngestionPhase.Ready, 1, 1, 1, Timestamp.AddSeconds(1), 1, [eligible], completedAtUtc: Timestamp.AddSeconds(6)));
        AssertRejects(() => new IngestionReceipt(facts, IngestionPhase.HeadConflict, 1, 1, 1, Timestamp.AddSeconds(1), 1, [eligible]));

        var rejection = new IngestionAttemptReceipt(1, Timestamp, Timestamp.AddSeconds(2),
            IngestionAttemptCompletion.FromOperationalFailure(IngestionFailureCode.EvidenceLimit), IngestionAdmissionClassification.RejectedResourceLimit);
        var rejected = new IngestionReceipt(facts, IngestionPhase.Rejected, 1, 1, null, null, null, [rejection], completedAtUtc: Timestamp.AddSeconds(3));
        Assert.Null(rejected.CommittedRevision);
        Assert.NotNull(rejected.CompletedAtUtc);
        AssertRejects(() => new IngestionReceipt(facts, IngestionPhase.Rejected, 1, 1, null, null, null, [rejection]));
        AssertRejects(() => new IngestionReceipt(facts, IngestionPhase.Rejected, 1, 1, null, null, null, [rejection], committed, Timestamp.AddSeconds(3)));
        AssertRejects(() => Committed(facts, sequence: 0));
        AssertRejects(() => Committed(facts, initialGeneration: 0));
        Assert.Equal(8, Committed(facts, initialGeneration: 8).InitialAdmissionAttemptGeneration);
        AssertRejects(() => Committed(facts, initialGeneration: 9));
        AssertRejects(() => Committed(facts, initialGeneration: long.MaxValue));
        AssertRejects(() => Committed(facts, operationId: Guid.Empty));
        AssertRejects(() => Committed(facts, nativeObject: NativeReference(byteCount: 0)));
        AssertRejects(() => Committed(facts, nativeObject: NativeReference(byteCount: 4194305)));
        AssertRejects(() => new CommittedRevisionFacts(null!, 1, facts.NativeObject, Profile, "entry.native.json",
            "weft.dependencies.empty.v1", Actor, Timestamp, facts.OperationId, 1, IngestionAdmissionClassification.EligibleValid));
        AssertRejects(() => new CommittedRevisionFacts(Scope, 1, null!, Profile, "entry.native.json",
            "weft.dependencies.empty.v1", Actor, Timestamp, facts.OperationId, 1, IngestionAdmissionClassification.EligibleValid));
        AssertRejects(() => new CommittedRevisionFacts(Scope, 1, facts.NativeObject, null!, "entry.native.json",
            "weft.dependencies.empty.v1", Actor, Timestamp, facts.OperationId, 1, IngestionAdmissionClassification.EligibleValid));
        AssertRejects(() => new CommittedRevisionFacts(Scope, 1, facts.NativeObject, Profile, "entry.native.json",
            "weft.dependencies.empty.v1", null!, Timestamp, facts.OperationId, 1, IngestionAdmissionClassification.EligibleValid));
        foreach (var classification in Enum.GetValues<IngestionAdmissionClassification>().Where(value =>
            value is not (IngestionAdmissionClassification.EligibleValid or IngestionAdmissionClassification.EligibleDecodedInvalid)))
            AssertRejects(() => Committed(facts, classification: classification));
        AssertRejects(() => Committed(facts, classification: (IngestionAdmissionClassification)0));
        AssertRejects(() => Committed(facts, nativeObject: NativeReference(@class: PrivateObjectClass.DerivedArtifact)));
        AssertRejects(() => Committed(facts, nativeObject: NativeReference(new RevisionScope(OrganizationId, ProjectId, MappingId, OtherId))));
        AssertRejects(() => Committed(facts, logicalDocument: "path/entry.native.json"));
        AssertRejects(() => Committed(facts, dependencyIdentity: "another-dependency"));

        CommittedRevisionFacts[] changed =
        [
            Committed(facts, operationId: OtherId),
            Committed(facts, creator: new PrincipalId(OtherId)),
            Committed(facts, profile: new IngestionBackendProfile("ferrule", "changed", "b", "a")),
            Committed(facts, nativeObject: NativeReference(objectId: Guid.Parse("b5c6d7e8-f901-1234-5678-5f60718293a4"))),
            Committed(facts, nativeObject: NativeReference(digest: Sha256Digest.FromBytes("abd"u8))),
            Committed(facts, nativeObject: NativeReference(byteCount: 4)),
            Committed(facts, initialGeneration: 2)
        ];
        Assert.All(changed, revision => AssertRejects(() => new IngestionReceipt(facts, IngestionPhase.Committed, 1, 1,
            1, Timestamp.AddSeconds(1), 1, [eligible], revision, Timestamp.AddSeconds(6))));
    }

    [Fact]
    public void ResultFactoriesExposeOnlyTheirGuardedReceiptAndClaimCombinations()
    {
        var facts = Reserved();
        var reserved = new IngestionReceipt(facts, IngestionPhase.Reserved, 0, 0, null, null, null, []);
        foreach (var outcome in new[] { IngestionStoreOutcome.Reserved, IngestionStoreOutcome.Existing, IngestionStoreOutcome.Applied, IngestionStoreOutcome.InProgress })
        {
            var result = IngestionStoreResult.ForReceipt(outcome, reserved);
            Assert.Equal(outcome, result.Outcome);
            Assert.Same(reserved, result.Receipt);
            Assert.Null(result.Claim);
        }
        var active = new IngestionReceipt(facts, IngestionPhase.Reserved, 1, 1, null, null, null,
            [new IngestionAttemptReceipt(1, Timestamp, null, null, null)]);
        var claimed = IngestionStoreResult.ForClaim(active, Claim(facts, 1));
        Assert.Equal(IngestionStoreOutcome.Claimed, claimed.Outcome);
        Assert.Same(active, claimed.Receipt);
        Assert.NotNull(claimed.Claim);
        foreach (var outcome in new[] { IngestionStoreOutcome.ClaimLost, IngestionStoreOutcome.IdempotencyConflict, IngestionStoreOutcome.NotAuthorizedOrNotFound })
        {
            var result = IngestionStoreResult.WithoutReceipt(outcome);
            Assert.Equal(outcome, result.Outcome);
            Assert.Null(result.Receipt);
            Assert.Null(result.Claim);
            AssertRejects(() => IngestionStoreResult.ForReceipt(outcome, reserved));
        }
        foreach (var outcome in new[] { IngestionStoreOutcome.Reserved, IngestionStoreOutcome.Existing, IngestionStoreOutcome.Claimed, IngestionStoreOutcome.Applied, IngestionStoreOutcome.InProgress })
            AssertRejects(() => IngestionStoreResult.WithoutReceipt(outcome));
        AssertRejects(() => IngestionStoreResult.ForReceipt(IngestionStoreOutcome.Claimed, reserved));
        AssertRejects(() => IngestionStoreResult.ForReceipt((IngestionStoreOutcome)0, reserved));
        AssertRejects(() => IngestionStoreResult.WithoutReceipt((IngestionStoreOutcome)0));
        AssertRejects(() => IngestionStoreResult.ForReceipt(IngestionStoreOutcome.Existing, null!));
        AssertRejects(() => IngestionStoreResult.ForClaim(null!, Claim(facts, 1)));
        AssertRejects(() => IngestionStoreResult.ForClaim(active, null!));
        AssertRejects(() => IngestionStoreResult.ForClaim(active, Claim(facts, 2)));
        AssertRejects(() => IngestionStoreResult.ForClaim(active,
            new IngestionClaim(new ProjectScope(OtherId, ProjectId), facts.OperationId, Actor, OtherId, OtherId, 1, Timestamp)));
        AssertRejects(() => IngestionStoreResult.ForClaim(active,
            new IngestionClaim(Project, OtherId, Actor, OtherId, OtherId, 1, Timestamp)));
        AssertRejects(() => IngestionStoreResult.ForClaim(active,
            new IngestionClaim(Project, facts.OperationId, new PrincipalId(OtherId), OtherId, OtherId, 1, Timestamp)));
    }

    [Fact]
    public void AttemptExhaustionRequiresTheWholeBoundedClaimHistoryAndACompletionTime()
    {
        var facts = Reserved();
        var attempts = Enumerable.Range(1, 8).Select(generation => new IngestionAttemptReceipt(generation,
            Timestamp.AddSeconds(generation * 2), Timestamp.AddSeconds(generation * 2 + 1),
            IngestionAttemptCompletion.FromOperationalFailure(IngestionFailureCode.NeedsSource),
            IngestionAdmissionClassification.RetryableFailure)).ToArray();
        var exhausted = new IngestionReceipt(facts, IngestionPhase.AttemptExhausted, 8, 8, null, null, null,
            attempts, completedAtUtc: Timestamp.AddMinutes(1));

        Assert.Equal(8, exhausted.ClaimCount);
        Assert.Equal(8, IngestionWorkflowLimits.MaximumClaims);
        Assert.Equal(120, IngestionWorkflowLimits.ClaimLeaseSeconds);
        Assert.Equal(8, exhausted.CurrentGeneration);
        Assert.Equal(8, exhausted.Attempts.Count);
        Assert.Null(exhausted.CommittedRevision);
        Assert.Null(exhausted.EligibleAttemptGeneration);
        AssertRejects(() => new IngestionReceipt(facts, IngestionPhase.AttemptExhausted, 8, 8, null, null, null, attempts));
        AssertRejects(() => new IngestionReceipt(facts, IngestionPhase.AttemptExhausted, 7, 7, null, null, null,
            attempts.Take(7), completedAtUtc: Timestamp.AddMinutes(1)));
        AssertRejects(() => new IngestionReceipt(facts, IngestionPhase.Reserved, 8, 8, null, null, null,
            attempts.Append(attempts[0])));
        AssertRejects(() => new IngestionReceipt(facts, IngestionPhase.Reserved, 1, 1, null, null, null, null!));
        AssertRejects(() => new IngestionReceipt(facts, IngestionPhase.Reserved, 1, 1, null, null, null, [null!]));
    }

    [Fact]
    public void PublicContractPropertiesHaveNoWritableSetters()
    {
        Type[] contracts =
        [
            typeof(MappingScope), typeof(EqualHead), typeof(IngestionCreateMetadata), typeof(IngestionIntent),
            typeof(IngestionBackendProfile), typeof(IngestionAdmissionPolicy), typeof(CanonicalFingerprint),
            typeof(ReservedIngestionFacts), typeof(CommittedRevisionFacts), typeof(IngestionProvenance),
            typeof(IngestionValidationFacts), typeof(IngestionEvidenceDocument), typeof(IngestionClaim),
            typeof(IngestionAttemptCompletion), typeof(IngestionAttemptReceipt), typeof(IngestionReceipt), typeof(IngestionStoreResult)
        ];
        Assert.All(contracts, contract => Assert.All(contract.GetProperties(), property => Assert.Null(property.SetMethod)));
    }

    private static IngestionIntent CreateIntent(string name = "Name", string description = "Description",
        string backendId = "ferrule", string? filename = null, Sha256Digest? digest = null, long byteCount = 3) =>
        IngestionIntent.ForCreate(new IngestionCreateMetadata(name, description), backendId, filename, digest ?? Digest, byteCount);

    private static IngestionValidationFacts ValidationFacts(IngestionBackendStatus status, bool? isValid,
        int total = 0, int emitted = 0, bool truncated = false, IngestionProvenance? provenance = null) =>
        new(status, provenance ?? new IngestionProvenance(Scope, Digest, Profile, true), isValid, total, emitted, truncated);

    private static IngestionEvidenceDocument Evidence(IngestionValidationFacts facts, string diagnostics = "[]") =>
        IngestionEvidenceDocument.Create(facts, ParseDiagnostics(diagnostics));

    private static DateTimeOffset Timestamp => new(2026, 10, 12, 12, 0, 0, TimeSpan.FromHours(-7));

    private static PrivateObjectReference NativeReference(RevisionScope? scope = null, Sha256Digest? digest = null,
        long byteCount = 3, PrivateObjectClass @class = PrivateObjectClass.NativeArtifact, Guid? objectId = null) =>
        new(PrivateObjectId.Restore(scope ?? Scope, objectId ?? OtherId, @class), digest ?? Digest, byteCount);

    private static ReservedIngestionFacts Reserved(IngestionIntent? intent = null, Guid? operationId = null,
        Guid? idempotencyKey = null, CanonicalFingerprint? fingerprint = null, RevisionScope? futureRevision = null,
        PrivateObjectReference? nativeObject = null, IngestionBackendProfile? profile = null, IngestionAdmissionPolicy? policy = null)
    {
        intent ??= CreateIntent();
        return new(Project, operationId ?? Guid.Parse("718293a4-b5c6-d7e8-f901-1b2c3d4e5f60"), Actor,
            idempotencyKey ?? Guid.Parse("8293a4b5-c6d7-e8f9-0123-2c3d4e5f6071"),
            fingerprint ?? CanonicalFingerprint.Create(Actor, Project, intent), intent, futureRevision ?? Scope,
            nativeObject ?? NativeReference(), profile ?? Profile,
            policy ?? new IngestionAdmissionPolicy("weft.native-admission.v1", 4194304), Timestamp);
    }

    private static IngestionClaim Claim(ReservedIngestionFacts facts, long generation) =>
        new(facts.Project, facts.OperationId, facts.OriginalActor, Guid.Parse("93a4b5c6-d7e8-f901-2345-3d4e5f607182"),
            Guid.Parse("a4b5c6d7-e8f9-0123-4567-4e5f60718293"), generation, Timestamp.AddMinutes(2));

    private static IngestionAttemptReceipt EligibleAttempt() => new(1, Timestamp, Timestamp.AddSeconds(2),
        IngestionAttemptCompletion.FromValidation(Evidence(ValidationFacts(IngestionBackendStatus.Succeeded, true))),
        IngestionAdmissionClassification.EligibleValid);

    private static CommittedRevisionFacts Committed(ReservedIngestionFacts facts, long sequence = 1,
        long initialGeneration = 1, IngestionAdmissionClassification classification = IngestionAdmissionClassification.EligibleValid,
        Guid? operationId = null, PrincipalId? creator = null, IngestionBackendProfile? profile = null,
        PrivateObjectReference? nativeObject = null, string logicalDocument = "entry.native.json",
        string dependencyIdentity = "weft.dependencies.empty.v1") =>
        new(facts.FutureRevision, sequence, nativeObject ?? facts.NativeObject, profile ?? facts.BackendProfile,
            logicalDocument, dependencyIdentity, creator ?? facts.OriginalActor, Timestamp.AddSeconds(6),
            operationId ?? facts.OperationId, initialGeneration, classification);

    private static JsonElement ParseDiagnostics(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 256 });
        return document.RootElement.Clone();
    }

    private static void AssertRejects(Action action) => Assert.ThrowsAny<ArgumentException>(action);
}
