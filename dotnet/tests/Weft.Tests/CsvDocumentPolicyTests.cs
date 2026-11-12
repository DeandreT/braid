using Weft.Application;
using Weft.Deployment;
using Weft.Domain;

namespace Weft.Tests;

public sealed class CsvDocumentPolicyTests
{
    [Theory]
    [InlineData("\t", "\\t")]
    [InlineData(" ", " ")]
    [InlineData("\b", "\\b")]
    [InlineData("\f", "\\f")]
    [InlineData("\v", "\\u000B")]
    [InlineData("\u0001", "\\u0001")]
    [InlineData("\u007f", "\\u007F")]
    [InlineData("\"", "\\u0022")]
    [InlineData("&", "\\u0026")]
    [InlineData("'", "\\u0027")]
    [InlineData("+", "\\u002B")]
    [InlineData("<", "\\u003C")]
    [InlineData(">", "\\u003E")]
    [InlineData("`", "\\u0060")]
    [InlineData("\\", "\\\\")]
    public void Dialect_canonical_vectors_use_the_default_writer_escaping(string delimiter, string escaped)
    {
        var dialect = new CsvDocumentDialect(CsvDocumentPolicy.DialectSchema, delimiter, null, true, false, false, false);
        Assert.Equal("{\"schema\":\"weft.csv-dialect.v1\",\"delimiter\":\"" + escaped +
            "\",\"quote\":null,\"quoteDisabled\":true,\"hasHeaderRow\":false,\"utf8Bom\":false,\"preserveEmptyStrings\":false}", CsvDialectJson.Encode(dialect));
    }
    [Theory]
    [InlineData(",", "\"", false, "text/csv", true)]
    [InlineData("\t", "'", false, "text/tab-separated-values", true)]
    [InlineData(";", null, true, "text/csv", true)]
    [InlineData(",", "\"", false, "text/tab-separated-values", false)]
    [InlineData("\n", "\"", false, "text/csv", false)]
    [InlineData("é", "\"", false, "text/csv", false)]
    [InlineData(",", "\"", true, "text/csv", false)]
    [InlineData(",", ",", false, "text/csv", false)]
    public void Dialect_is_explicit_versioned_and_media_bound(string delimiter, string? quote, bool disabled, string media, bool accepted)
    {
        var dialect = new CsvDocumentDialect(CsvDocumentPolicy.DialectSchema, delimiter, quote, disabled, true, true, true);
        Assert.Equal(accepted, CsvDocumentPolicy.Admits(dialect, media));
        Assert.False(CsvDocumentPolicy.Admits(dialect with { Schema = "future" }, media));
    }

    [Fact]
    public void Existing_persisted_profiles_are_not_silently_expanded()
    {
        Assert.False(DocumentPreviewPolicy.AdmitsMediaType("text/csv"));
        Assert.False(BuildWorkerProtocol.AdmitsFormat(BuildWorkerProtocol.DocumentProfile, "csv"));
        var scope = new RevisionScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var dialect = new CsvDocumentDialect(CsvDocumentPolicy.DialectSchema, ",", "\"", false, true, false, true);
        var submission = new PreviewRunSubmission(scope, Guid.NewGuid(), PreviewTargetSelection.All, new(null, [], dialect), [], [])
        { Profile = PreviewExecutionProfile.CsvDocument };
        var document = PreviewRequestDocument.Encode(submission, PreviewLimits.Default);
        Assert.Equal(PreviewExecutionProfile.CsvDocument, PreviewRequestDocument.Decode(document).Profile);
        Assert.Contains("\"present\":true", document, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_artifact_has_a_digest_and_does_not_mean_no_artifact()
    {
        var dialect = new CsvDocumentDialect(CsvDocumentPolicy.DialectSchema, ",", "\"", false, false, false, false);
        var artifact = new PreviewOutputArtifact("primary-output", "Row", false, "text/csv", 0, [], dialect);
        Assert.Equal(0, artifact.ByteCount);
        Assert.Equal(Sha256Digest.FromBytes([]), artifact.Digest);
        Assert.Same(dialect, artifact.CsvDialect);
    }
}
