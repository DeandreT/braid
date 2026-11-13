using System.Security.Cryptography.Xml;
using System.Text;
using Weft.Application;
using Weft.Application.Regression;
using Weft.Contracts.Operations;
using Weft.Domain;

namespace Weft.Tests;

/// <summary>Self-authored unit definitions, not official W3C vectors or worker/resource qualification.</summary>
public sealed class XmlCanonicalComparisonTests
{
    private static readonly XmlCanonicalizationLimits Limits = new(1024 * 1024, 1024 * 1024, 2 * 1024 * 1024);

    [Fact]
    public void Exact_inclusive_comments_algorithm_is_fixed_and_not_exposed_by_the_existing_dispatch()
    {
        Assert.Equal("http://www.w3.org/TR/2001/REC-xml-c14n-20010315#WithComments", XmlCanonicalDocument.AlgorithmUri);
        Assert.Equal(XmlCanonicalDocument.AlgorithmUri, new XmlDsigC14NWithCommentsTransform().Algorithm);
        var json = RegressionOutputSet.Capture([RegressionOutput.Capture(RegressionOutputKey.Primary, "{}"u8)]);
        var result = new RegressionComparisonService().Compare(json, new(OperationStatusV1.Succeeded, json),
            XmlCanonicalComparison.Policy);
        Assert.Equal(RegressionNotEvaluatedReason.UnsupportedPolicy, result.NotEvaluatedReason);
        Assert.DoesNotContain(typeof(RegressionComparisonService).Assembly.GetExportedTypes(),
            type => type.Name.StartsWith("XmlCanonical", StringComparison.Ordinal) ||
                type.Name.StartsWith("XmlComparison", StringComparison.Ordinal));
    }

    [Fact]
    public void Declaration_bom_quotes_attribute_order_and_empty_tag_spelling_normalize()
    {
        const string source = "\ufeff<?xml version='1.0' encoding='UTF-8'?><r z='2' a='1'/>";
        Assert.Equal("<r a=\"1\" z=\"2\"></r>", Canonical(source));
        var result = Compare(source, "<r a=\"1\" z=\"2\"></r>");
        Assert.Equal(RegressionComparisonOutcome.Passed, result.Outcome);
        Assert.True(result.ComparisonComplete);
        Assert.Empty(result.Differences);
        Assert.Equal((long)Encoding.UTF8.GetByteCount(source), result.ExpectedOriginalBytes);
        Assert.Equal(1, result.ExpectedCanonical!.Documents);
        Assert.Equal(19L, result.ExpectedCanonical.Bytes);
    }

    [Fact]
    public void Inclusive_namespaces_retain_unused_bindings_and_remove_redundant_declarations()
    {
        const string source = "<r xmlns='urn:r' xmlns:q='urn:q'><c xmlns='urn:r' xmlns:q='urn:q'/><d xmlns=''><e/></d></r>";
        Assert.Equal("<r xmlns=\"urn:r\" xmlns:q=\"urn:q\"><c></c><d xmlns=\"\"><e></e></d></r>", Canonical(source));
        Assert.Equal("<r xmlns:a=\"urn:a\" xmlns:z=\"urn:z\" plain=\"0\" a:z=\"1\" z:a=\"2\"></r>",
            Canonical("<r z:a='2' xmlns:z='urn:z' a:z='1' plain='0' xmlns:a='urn:a'/>"));
    }

    [Fact]
    public void Bmp_namespace_uri_order_uses_uri_before_local_attribute_name()
    {
        Assert.Equal("<r xmlns:a=\"urn:\uE000\" xmlns:z=\"urn:\uD7FF\" plain=\"0\" z:b=\"1\" a:a=\"2\"></r>",
            Canonical("<r xmlns:a='urn:\uE000' xmlns:z='urn:\uD7FF' a:a='2' z:b='1' plain='0'/>"));
    }

    [Fact]
    public void Entity_cdata_and_line_endings_follow_canonical_rules()
    {
        Assert.Equal("<r a=\"&#x9;&#xA;&#xD;&quot;&amp;&lt;>\">A&#xD;\n&lt;&amp;&gt;'</r>",
            Canonical("<r a='&#x9;&#xA;&#xD;&quot;&amp;&lt;&gt;'>A&#13;\r\n<![CDATA[<&>]]>&apos;</r>"));
        Assert.Equal(RegressionComparisonOutcome.Passed,
            Compare("<r><![CDATA[A&B]]>&lt;</r>", "<r>A&amp;B&lt;</r>").Outcome);
        Assert.Equal(RegressionComparisonOutcome.Passed,
            Compare("<r>&#65;&#x42;</r>", "<r>AB</r>").Outcome);
        Assert.Equal(RegressionComparisonOutcome.Failed,
            Compare("<r>&#13;</r>", "<r>\r\n</r>").Outcome);
    }

    [Fact]
    public void Whole_document_comments_and_processing_instructions_get_prescribed_newlines()
    {
        Assert.Equal("<!--before-->\n<?before x?>\n<r><!--inside--><?inside y?></r>\n<?after z?>\n<!--after-->",
            Canonical(" \r\n<!--before-->\r\n<?before x?><r><!--inside--><?inside y?></r>\r\n<?after z?> <!--after-->\n"));
        Assert.Equal(RegressionComparisonOutcome.Passed, Compare(" \r\n<r/>\t\r\n", "<r></r>").Outcome);
        Assert.Equal("<!-- <!DOCTYPE r SYSTEM 'file:///never-open'> -->\n<r>&lt;!DOCTYPE r&gt;</r>",
            Canonical("<!-- <!DOCTYPE r SYSTEM 'file:///never-open'> --><r><![CDATA[<!DOCTYPE r>]]></r>"));
    }

    [Theory]
    [InlineData("<r><!--one--></r>", "<r><!--two--></r>")]
    [InlineData("<?p one?><r/>", "<r/><?p one?>")]
    [InlineData("<r><?p one?></r>", "<r><?p two?></r>")]
    [InlineData("<a:r xmlns:a='urn:r'/>", "<b:r xmlns:b='urn:r'/>")]
    [InlineData("<r><a/><b/></r>", "<r><b/><a/></r>")]
    [InlineData("<r><a/></r>", "<r>\n<a/>\n</r>")]
    [InlineData("<r xml:space='preserve'/>", "<r xml:space='default'/>")]
    [InlineData("<r xml:base='urn:one'/>", "<r xml:base='urn:two'/>")]
    [InlineData("<r xmlns:a='urn:k' xmlns:b='urn:k' value='a:Kind'/>", "<r xmlns:a='urn:k' xmlns:b='urn:k' value='b:Kind'/>")]
    [InlineData("<r>01</r>", "<r>1</r>")]
    [InlineData("<r>\u00e9</r>", "<r>e\u0301</r>")]
    [InlineData("<r xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance' xsi:nil='true'/>", "<r xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance' xsi:nil='1'/>")]
    [InlineData("<r xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance' xsi:noNamespaceSchemaLocation='one.xsd'/>", "<r xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance' xsi:noNamespaceSchemaLocation='two.xsd'/>")]
    public void Significant_content_is_not_rewritten(string expected, string actual)
    {
        var result = Compare(expected, actual);
        Assert.Equal(RegressionComparisonOutcome.Failed, result.Outcome);
        var difference = Assert.Single(result.Differences);
        Assert.Equal(XmlCanonicalDifferenceKind.CanonicalBytesDiffer, difference.Kind);
        Assert.NotNull(difference.CanonicalByteOffset);
        Assert.Null(difference.FirstDifferentByte);
    }

    [Theory]
    [InlineData("<r xmlns='relative'/>")]
    [InlineData("<r xmlns:p='../relative'/>")]
    [InlineData("<r xmlns:p='#fragment'/>")]
    [InlineData("<r xmlns:p='//example.invalid/path'/>")]
    [InlineData("<r xmlns:p='1bad:scheme'/>")]
    [InlineData("<r xmlns='urn:\U0001f680'/>")]
    [InlineData("<r xmlns:p='urn:&#x1F680;'/>")]
    public void Namespace_domain_refusal_is_fixed_and_never_falls_back(string xml)
    {
        var result = Compare(xml, xml);
        AssertNotEvaluated(result, XmlCanonicalFailure.UnsupportedDomain);
        Assert.Equal(XmlCanonicalPhase.NamespaceDomain, result.Diagnostic!.Phase);
        Assert.Equal(XmlComparisonSide.Expected, result.Diagnostic.Side);
        Assert.DoesNotContain("relative", result.Diagnostic.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("urn:", result.Diagnostic.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Supplementary_ordinary_values_are_allowed_but_physical_names_are_not()
    {
        const string xml = "<r value='\U0001f680'>\U0001f680</r>";
        Assert.Equal(xml.Replace("'", "\"", StringComparison.Ordinal), Canonical(xml));
        Assert.Equal(RegressionComparisonOutcome.Passed, Compare(xml, xml).Outcome);
        AssertNotEvaluated(Compare("<\U00010400/>", "<\U00010400/>"), XmlCanonicalFailure.InvalidDocument);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<broken>")]
    [InlineData("<!DOCTYPE r><r/>")]
    [InlineData("<!DOCTYPE r [<!ATTLIST r a CDATA 'default'>]><r/>")]
    [InlineData("<!DOCTYPE r SYSTEM 'file:///private-secret'><r/>")]
    [InlineData("<!DOCTYPE r [<!ENTITY % e SYSTEM 'https://never-fetch.invalid/'>%e;]><r/>")]
    [InlineData("<r>&unknown;</r>")]
    [InlineData("<r>&#0;</r>")]
    [InlineData("<?xml version='1.1'?><r/>")]
    [InlineData("<?xml version='1.0' encoding='UTF-16'?><r/>")]
    [InlineData("<r a='1' a='2'/>")]
    [InlineData("<r/><s/>")]
    public void Unsafe_identical_documents_are_not_an_equality_shortcut(string xml)
    {
        var result = Compare(xml, xml);
        AssertNotEvaluated(result, XmlCanonicalFailure.InvalidDocument);
        Assert.Equal(XmlCanonicalPhase.Safety, result.Diagnostic!.Phase);
        Assert.DoesNotContain("private-secret", result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-secret", result.Diagnostic.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_utf8_is_refused_without_transcoding_even_when_identical()
    {
        byte[][] invalid = [[0xc0, 0xaf], [0xed, 0xa0, 0x80], [0xe2, 0x82], Encoding.Unicode.GetBytes("<r/>")];
        foreach (var bytes in invalid)
        {
            var output = XmlComparisonOutput.Capture(RegressionOutputKey.Primary, bytes, XmlDocumentMetadata.WithoutHints);
            var set = XmlComparisonOutputSet.Capture([output]);
            AssertNotEvaluated(Compare(set, set), XmlCanonicalFailure.InvalidDocument);
        }
    }

    [Fact]
    public void Physical_limits_are_enforced_before_the_canonical_dom()
    {
        var atDepth = string.Concat(Enumerable.Repeat("<r>", 64)) + string.Concat(Enumerable.Repeat("</r>", 64));
        Assert.Equal(RegressionComparisonOutcome.Passed, Compare(atDepth, atDepth).Outcome);
        var overDepth = "<r>" + atDepth + "</r>";
        var refused = Compare(overDepth, overDepth);
        AssertNotEvaluated(refused, XmlCanonicalFailure.ResourceLimit);
        Assert.Equal(XmlCanonicalPhase.Safety, refused.Diagnostic!.Phase);
        var overAttributes = "<r" + string.Concat(Enumerable.Range(0, 257).Select(index => $" a{index}='x'")) + "/>";
        AssertNotEvaluated(Compare(overAttributes, overAttributes), XmlCanonicalFailure.ResourceLimit);
    }

    [Fact]
    public void Metadata_equality_precedes_canonical_content_differences_and_preserves_ordered_hints()
    {
        var first = new XmlDocumentMetadata(XmlDocumentPolicy.MetadataSchema, XmlDocumentPolicy.SafetyProfile,
            new(null, [new("urn:a", "one.xsd"), new("urn:b", "two.xsd")]));
        var equal = new XmlDocumentMetadata(XmlDocumentPolicy.MetadataSchema, XmlDocumentPolicy.SafetyProfile,
            new(null, [new("urn:a", "one.xsd"), new("urn:b", "two.xsd")]));
        var reordered = new XmlDocumentMetadata(XmlDocumentPolicy.MetadataSchema, XmlDocumentPolicy.SafetyProfile,
            new(null, [new("urn:b", "two.xsd"), new("urn:a", "one.xsd")]));
        Assert.Equal(RegressionComparisonOutcome.Passed,
            Compare(Set(Output("<r/>", metadata: first)), Set(Output("<r/>", metadata: equal))).Outcome);
        var result = Compare(Set(Output("<r>one</r>", metadata: first)), Set(Output("<r>two</r>", metadata: reordered)));
        var difference = Assert.Single(result.Differences);
        Assert.Equal(XmlCanonicalDifferenceKind.XmlMetadataDiffer, difference.Kind);
        Assert.Null(difference.CanonicalByteOffset);
        Assert.NotNull(difference.ExpectedCanonicalByteCount);
        Assert.Equal(1, result.ActualCanonical!.Documents);
    }

    [Fact]
    public void Missing_or_wrong_metadata_and_foreign_media_are_not_admitted()
    {
        var missing = XmlComparisonOutput.Capture(RegressionOutputKey.Primary, "<r/>"u8, null);
        AssertNotEvaluated(Compare(Set(missing), Set(Output("<r/>"))), XmlCanonicalFailure.InvalidMetadata);
        var wrong = new XmlDocumentMetadata("future", XmlDocumentPolicy.SafetyProfile, null);
        AssertNotEvaluated(Compare(Set(Output("<r/>", metadata: wrong)), Set(Output("<r/>"))), XmlCanonicalFailure.InvalidMetadata);
        var foreign = XmlComparisonOutput.Capture(RegressionOutputKey.Primary, "<r/>"u8,
            XmlDocumentMetadata.WithoutHints, "application/json");
        AssertNotEvaluated(Compare(Set(foreign), Set(foreign)), XmlCanonicalFailure.InvalidMediaType);
    }

    [Fact]
    public void A_mismatched_media_label_precedes_metadata_and_content_but_never_bypasses_safe_parsing()
    {
        var metadata = new XmlDocumentMetadata(XmlDocumentPolicy.MetadataSchema, XmlDocumentPolicy.SafetyProfile,
            new("urn:hint", []));
        var actual = XmlComparisonOutput.Capture(RegressionOutputKey.Primary, "<r>two</r>"u8,
            metadata, "application/json");
        var result = Compare(Set(Output("<r>one</r>")), Set(actual));
        Assert.Equal(RegressionComparisonOutcome.Failed, result.Outcome);
        var difference = Assert.Single(result.Differences);
        Assert.Equal(XmlCanonicalDifferenceKind.MediaTypeDiffer, difference.Kind);
        Assert.Null(difference.CanonicalByteOffset);
        Assert.Equal(1, result.ActualCanonical!.Documents);
        var unsafeActual = XmlComparisonOutput.Capture(RegressionOutputKey.Primary, "<!DOCTYPE r><r/>"u8,
            metadata, "application/json");
        AssertNotEvaluated(Compare(Set(Output("<r/>")), Set(unsafeActual)), XmlCanonicalFailure.InvalidDocument);
    }

    [Fact]
    public void Unmatched_outputs_are_canonicalized_and_all_counts_survive_detail_truncation()
    {
        var expected = Set(Output("<r/>"), Output("<left/>", "z"));
        var actual = Set(Output("<r></r>"), Output("<right/>", "a"));
        var result = Compare(expected, actual, comparison: new(1024 * 1024, 1));
        Assert.Equal(RegressionComparisonOutcome.Failed, result.Outcome);
        Assert.True(result.ComparisonComplete);
        Assert.True(result.Truncated);
        Assert.Equal(2, result.TotalDifferenceCount);
        var difference = Assert.Single(result.Differences);
        Assert.Equal("a", difference.Key.Name);
        Assert.Equal(XmlCanonicalDifferenceKind.UnexpectedOutput, difference.Kind);
        Assert.Equal(2, result.ExpectedCanonical!.Documents);
        Assert.Equal(2, result.ActualCanonical!.Documents);
        Assert.Equal(7L + Encoding.UTF8.GetByteCount("<left></left>"), result.ExpectedCanonical.Bytes);
        Assert.Equal(7L + Encoding.UTF8.GetByteCount("<right></right>"), result.ActualCanonical.Bytes);
    }

    [Fact]
    public void Late_unsafe_unmatched_output_clears_all_partial_differences_and_canonical_totals()
    {
        var expected = Set(Output("<r>one</r>"), Output("<left/>", "a"));
        var actual = Set(Output("<r>two</r>"), Output("<broken>", "z"));
        var result = Compare(expected, actual);
        AssertNotEvaluated(result, XmlCanonicalFailure.InvalidDocument);
        Assert.Equal("z", result.Diagnostic!.Key!.Name);
        Assert.Equal(XmlComparisonSide.Actual, result.Diagnostic.Side);
        var unsafeWithDifferentMetadata = Output("<!DOCTYPE r><r/>", metadata:
            new(XmlDocumentPolicy.MetadataSchema, XmlDocumentPolicy.SafetyProfile, new("file:///never-open.xsd", [])));
        AssertNotEvaluated(Compare(Set(Output("<r/>")), Set(unsafeWithDifferentMetadata)), XmlCanonicalFailure.InvalidDocument);
    }

    [Fact]
    public void Offsets_refer_to_canonical_bytes_not_source_bytes_and_lengths_remain_distinct()
    {
        const string expected = "<r b='2' a='x'/>";
        const string actual = "<r a='y' b='2'></r>";
        var result = Compare(expected, actual);
        var difference = Assert.Single(result.Differences);
        Assert.Equal(6L, difference.CanonicalByteOffset);
        Assert.NotEqual((long?)expected.IndexOf('x'), difference.CanonicalByteOffset);
        Assert.Null(difference.FirstDifferentByte);
        Assert.Equal(Encoding.UTF8.GetByteCount(expected), difference.ExpectedByteCount);
        Assert.Equal((long)Encoding.UTF8.GetByteCount("<r a=\"x\" b=\"2\"></r>"), difference.ExpectedCanonicalByteCount);
        Assert.Equal(Sha256Digest.FromBytes(Encoding.UTF8.GetBytes(expected)), difference.ExpectedDigest);
        var prefix = Assert.Single(Compare("<r/>", "<r/><!--after-->").Differences);
        Assert.Equal(7L, prefix.CanonicalByteOffset);
    }

    [Fact]
    public void Byte_comparison_crosses_chunks_and_checks_after_an_early_difference()
    {
        var content = new string('x', 10_000);
        var actual = content[..7000] + "y" + content[7001..];
        var result = Compare("<r>" + content + "</r>", "<r>" + actual + "</r>");
        Assert.Equal(7003L, Assert.Single(result.Differences).CanonicalByteOffset);
        Assert.Equal(10_007L, result.ActualCanonical!.Bytes);
        var early = Compare("<r>x" + content + "</r>", "<r>y" + content + "</r>");
        Assert.Equal(3L, Assert.Single(early.Differences).CanonicalByteOffset);
        Assert.Equal(10_008L, early.ExpectedCanonical!.Bytes);
    }

    [Fact]
    public void Original_payload_and_digest_remain_unchanged_after_canonical_comparison()
    {
        var bytes = Encoding.UTF8.GetBytes("\ufeff<r b='2' a='1'/>");
        var original = (byte[])bytes.Clone();
        var output = XmlComparisonOutput.Capture(RegressionOutputKey.Primary, bytes, XmlDocumentMetadata.WithoutHints);
        bytes[0] = 0;
        var exported = output.CopyBytes();
        exported[^1] = 0;
        Assert.Equal(RegressionComparisonOutcome.Passed, Compare(Set(output), Set(Output("<r a='1' b='2'></r>"))).Outcome);
        Assert.Equal(original, output.CopyBytes());
        Assert.Equal(Sha256Digest.FromBytes(original), output.Digest);
    }

    [Fact]
    public void Explicit_canonical_limits_accept_the_boundary_and_clear_late_resource_failures()
    {
        Assert.Equal(RegressionComparisonOutcome.Passed, Compare(Set(Output("<r/>")), Set(Output("<r/>")),
            canonical: new(4, 7, 14), comparison: new(8, 1)).Outcome);
        AssertNotEvaluated(Compare(Set(Output("<r/>")), Set(Output("<r/>")), canonical: new(4, 6, 14)),
            XmlCanonicalFailure.ResourceLimit);
        var total = Compare(Set(Output("<r/>")), Set(Output("<r/>")), canonical: new(4, 7, 13));
        AssertNotEvaluated(total, XmlCanonicalFailure.ResourceLimit);
        Assert.Equal(XmlComparisonSide.Actual, total.Diagnostic!.Side);
        Assert.Equal(XmlCanonicalPhase.Canonicalization, total.Diagnostic.Phase);
        var late = Compare(Set(Output("<r>one</r>"), Output("<z/>", "z")),
            Set(Output("<r>two</r>"), Output("<z/>", "z")), canonical: new(1024, 1024, 26));
        AssertNotEvaluated(late, XmlCanonicalFailure.ResourceLimit);
    }

    [Fact]
    public void All_original_bytes_including_unmatched_outputs_are_charged_before_parsing()
    {
        var result = Compare(Set(Output("<broken>")), Set(Output("<r/>"), Output("<r/>", "extra")),
            comparison: new(15, 1));
        AssertNotEvaluated(result, XmlCanonicalFailure.ResourceLimit);
        Assert.Equal(XmlCanonicalPhase.Admission, result.Diagnostic!.Phase);
        Assert.Equal(16L, result.Diagnostic.Observed);
        Assert.Null(result.Diagnostic.Key);
        var side = Compare(Set(Output("<broken>")), Set(Output("<r/>")), canonical: new(7, 100, 200));
        AssertNotEvaluated(side, XmlCanonicalFailure.ResourceLimit);
        Assert.Equal(XmlComparisonSide.Expected, side.Diagnostic!.Side);
    }

    [Fact]
    public void No_unapproved_canonical_limit_defaults_are_installed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new XmlCanonicalizationLimits(0, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new XmlCanonicalizationLimits(XmlDocumentPolicy.MaximumBytes + 1, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new XmlCanonicalizationLimits(1, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new XmlCanonicalizationLimits(1, 1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new XmlCanonicalizationLimits(1, long.MaxValue, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new XmlCanonicalizationLimits(1, 1, long.MaxValue));
        Assert.All(typeof(XmlCanonicalizationLimits).GetConstructors().Single().GetParameters(),
            parameter => Assert.False(parameter.HasDefaultValue));
    }

    [Fact]
    public void Output_sets_use_sixty_four_not_sixteen_and_validate_keys_before_comparison()
    {
        var outputs = Enumerable.Range(0, 64).Select(index => Output("<r/>", $"o{index:D2}")).ToArray();
        var set = Set(outputs);
        var result = Compare(set, set);
        Assert.Equal(RegressionComparisonOutcome.Passed, result.Outcome);
        Assert.Equal(64, result.ExpectedCanonical!.Documents);
        Assert.Equal(RegressionComparisonFailure.ResourceLimit,
            Assert.Throws<RegressionComparisonException>(() => Set([.. outputs, Output("<r/>", "extra")])).Failure);
        Assert.Equal(RegressionComparisonFailure.InvalidInput,
            Assert.Throws<RegressionComparisonException>(() => Set(Output("<r/>"), Output("<r/>"))).Failure);
    }

    [Fact]
    public void Cancellation_is_propagated_before_comparison_and_during_bounded_reads()
    {
        using var cancellation = new CancellationTokenSource();
        using var canonical = XmlCanonicalDocument.Open("<r/>"u8, 4, 7, CancellationToken.None);
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => canonical.Read(new byte[32], cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => XmlCanonicalComparison.Compare(Set(Output("<r/>")),
            Set(Output("<r/>")), OperationStatusV1.Succeeded, new(100, 1), new(100, 100, 200), cancellation.Token));
        // Polls do not prove interruptibility inside LoadInput/GetOutput. That needs an actual terminable worker.
    }

    [Fact]
    public void Missing_baseline_or_unsuccessful_execution_cannot_pass()
    {
        AssertNotEvaluated(Compare(Set(), Set(Output("<r/>"))), XmlCanonicalFailure.MissingBaseline);
        AssertNotEvaluated(XmlCanonicalComparison.Compare(Set(Output("<r/>")), Set(Output("<r/>")),
            OperationStatusV1.BackendFailure, new(100, 1), new(100, 100, 200)), XmlCanonicalFailure.ExecutionUnsuccessful);
    }

    private static XmlComparisonOutput Output(string xml, string? name = null, XmlDocumentMetadata? metadata = null) =>
        XmlComparisonOutput.Capture(name is null ? RegressionOutputKey.Primary : RegressionOutputKey.Named(name),
            Encoding.UTF8.GetBytes(xml), metadata ?? XmlDocumentMetadata.WithoutHints);

    private static XmlComparisonOutputSet Set(params XmlComparisonOutput[] outputs) => XmlComparisonOutputSet.Capture(outputs);

    private static XmlCanonicalComparisonResult Compare(string expected, string actual) => Compare(Set(Output(expected)), Set(Output(actual)));

    private static XmlCanonicalComparisonResult Compare(XmlComparisonOutputSet expected, XmlComparisonOutputSet actual,
        XmlCanonicalizationLimits? canonical = null, RegressionComparisonLimits? comparison = null) =>
        XmlCanonicalComparison.Compare(expected, actual, OperationStatusV1.Succeeded,
            comparison ?? new(2 * 1024 * 1024, 64), canonical ?? Limits);

    private static string Canonical(string xml)
    {
        using var document = XmlCanonicalDocument.Open(Encoding.UTF8.GetBytes(xml), 1024 * 1024,
            1024 * 1024, CancellationToken.None);
        using var output = new MemoryStream();
        var buffer = new byte[127];
        while (true)
        {
            var count = document.Read(buffer, CancellationToken.None);
            if (count == 0) break;
            output.Write(buffer, 0, count);
        }
        Assert.Equal(document.Length, output.Length);
        return Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
    }

    private static void AssertNotEvaluated(XmlCanonicalComparisonResult result, XmlCanonicalFailure reason)
    {
        Assert.Equal(RegressionComparisonOutcome.NotEvaluated, result.Outcome);
        Assert.Equal(reason, result.NotEvaluatedReason);
        Assert.False(result.ComparisonComplete);
        Assert.Equal(0, result.TotalDifferenceCount);
        Assert.Empty(result.Differences);
        Assert.Null(result.ExpectedCanonical);
        Assert.Null(result.ActualCanonical);
    }
}
