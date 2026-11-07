using System.Text;
using Weft.Application;

namespace Weft.Tests;

public sealed class EdiEnvelopeValidationTests
{
    [Theory]
    [InlineData("x12", "SE*3*0330", "SE*9*0330", "application/edi-x12")]
    [InlineData("x12", "SE*3*0330", "SE*3*9999", "application/edi-x12")]
    [InlineData("edifact", "UNT+3+330", "UNT+9+330", "application/edifact")]
    [InlineData("edifact", "UNT+3+330", "UNT+3+999", "application/edifact")]
    public void Admission_checks_counts_and_references_before_execution(string family, string from, string to, string media)
    {
        var bytes = DocumentPreviewTestData.Input(family);
        Assert.True(EdiEnvelopeValidation.IsValid(bytes, media));
        Assert.False(EdiEnvelopeValidation.IsValid(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace(from, to, StringComparison.Ordinal)), media));
        Assert.False(EdiEnvelopeValidation.IsValid([], media));
    }

    [Fact]
    public void Admission_requires_explicit_edi_media_and_preserves_cancellation()
    {
        Assert.Throws<ArgumentException>(() => EdiEnvelopeValidation.IsValid("{}"u8, "application/json"));
        Assert.Throws<OperationCanceledException>(() => EdiEnvelopeValidation.IsValid([], "application/edi-x12", new(true)));
    }
}
