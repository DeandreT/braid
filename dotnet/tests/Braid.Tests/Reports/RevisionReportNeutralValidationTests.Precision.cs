using System.Text;
using System.Text.Json;
using Braid.Application;
using Braid.Contracts.Operations;
using Braid.Contracts.Projections;
using Braid.Domain;

namespace Braid.Tests;

public sealed partial class RevisionReportNeutralValidationTests
{
    [Theory]
    [InlineData(ExactNumberFlavor.Integer, "-0")]
    [InlineData(ExactNumberFlavor.Integer, "900719925474099312345678901234567890")]
    [InlineData(ExactNumberFlavor.Integer, "-900719925474099312345678901234567890")]
    [InlineData(ExactNumberFlavor.Decimal, "-0.000E+07")]
    [InlineData(ExactNumberFlavor.Decimal, "1e-0000123")]
    [InlineData(ExactNumberFlavor.Decimal, "123456789012345678901234567890.00100")]
    [InlineData(ExactNumberFlavor.BinaryFloat, "0000000000000000")]
    [InlineData(ExactNumberFlavor.BinaryFloat, "8000000000000000")]
    [InlineData(ExactNumberFlavor.BinaryFloat, "3ff0000000000000")]
    [InlineData(ExactNumberFlavor.BinaryFloat, "7ff0000000000000")]
    [InlineData(ExactNumberFlavor.BinaryFloat, "fff0000000000000")]
    [InlineData(ExactNumberFlavor.BinaryFloat, "7ff8000000000001")]
    [InlineData(ExactNumberFlavor.BinaryFloat, "7ff8000000000042")]
    [InlineData(ExactNumberFlavor.BinaryFloat, "fff8000000000001")]
    [InlineData(ExactNumberFlavor.BinaryFloat, "7ff0000000000001")]
    [InlineData(ExactNumberFlavor.BinaryFloat, "fff0000000000042")]
    public void ExactNumbersRetainEveryLexicalCharacterAndBinary64PayloadBit(ExactNumberFlavor flavor, string value)
    {
        var report = Graph();
        var literal = new ExactNumberLiteralValue(flavor, value);
        report = WithDocument(report, report.Document! with { Parameters = R(report.Document.Parameters[0] with { DefaultValue = literal }) });
        Assert.NotNull(CheckProjection(report).Projection);
        using var json = JsonDocument.Parse(RevisionReportResponseSerializer.Serialize(report, RevisionReportPolicy.MaximumResponseByteLimit));
        Assert.Equal(value, json.RootElement.GetProperty("document").GetProperty("parameters")[0].GetProperty("defaultValue").GetProperty("value").GetString());
        Assert.Equal(value, Assert.IsType<ExactNumberLiteralValue>(report.Document!.Parameters[0].DefaultValue).Value);
    }

    [Theory]
    [InlineData(ExactNumberFlavor.Integer, "+1")]
    [InlineData(ExactNumberFlavor.Integer, "01")]
    [InlineData(ExactNumberFlavor.Integer, "1.0")]
    [InlineData(ExactNumberFlavor.Integer, "1e1")]
    [InlineData(ExactNumberFlavor.Decimal, ".1")]
    [InlineData(ExactNumberFlavor.Decimal, "1.")]
    [InlineData(ExactNumberFlavor.Decimal, "1e")]
    [InlineData(ExactNumberFlavor.Decimal, "1e+")]
    [InlineData(ExactNumberFlavor.Decimal, "NaN")]
    [InlineData(ExactNumberFlavor.BinaryFloat, "7ff800000000001")]
    [InlineData(ExactNumberFlavor.BinaryFloat, "07ff8000000000001")]
    [InlineData(ExactNumberFlavor.BinaryFloat, "7FF8000000000001")]
    [InlineData(ExactNumberFlavor.BinaryFloat, "7ff800000000000g")]
    public void MalformedExactNumbersAreRefusedWithoutNumericCoercion(ExactNumberFlavor flavor, string value)
    {
        var report = Graph();
        report = WithDocument(report, report.Document! with
        { Parameters = R(report.Document.Parameters[0] with { DefaultValue = new ExactNumberLiteralValue(flavor, value) }) });
        Integrity(() => CheckProjection(report));
    }

    [Fact]
    public void ExactStringAndPathTextRetainLegalEscapedControlCharacters()
    {
        const string exact = "a\0\t\n\r\b\f\"\\😀";
        var report = Graph();
        report = WithDocument(report, report.Document! with
        {
            Parameters = R(report.Document.Parameters[0] with { DefaultValue = new StringLiteralValue(exact) }),
            Fields = R(report.Document.Fields[0] with { Path = R<PathSegment>(new FieldPathSegment(exact, "urn:\0exact")) }, report.Document.Fields[1])
        });
        Assert.NotNull(CheckProjection(report).Projection);
        using var json = JsonDocument.Parse(RevisionReportResponseSerializer.Serialize(report, RevisionReportPolicy.MaximumResponseByteLimit));
        var document = json.RootElement.GetProperty("document");
        Assert.Equal(exact, document.GetProperty("parameters")[0].GetProperty("defaultValue").GetProperty("value").GetString());
        Assert.Equal(exact, document.GetProperty("fields")[0].GetProperty("path")[0].GetProperty("name").GetString());
        Assert.Equal("urn:\0exact", document.GetProperty("fields")[0].GetProperty("path")[0].GetProperty("namespaceUri").GetString());
    }

    [Theory]
    [InlineData("00")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("1.0")]
    [InlineData(" 1")]
    public void ExplicitIndexAndCardinalityVariantsRequireCanonicalUnsignedIntegerText(string value)
    {
        var report = Graph();
        var field = report.Document!.Fields[0];
        Integrity(() => CheckProjection(WithDocument(report, report.Document with
        { Fields = R(field with { Path = R<PathSegment>(new IndexPathSegment(value)) }, report.Document.Fields[1]) })));
        Integrity(() => CheckProjection(WithDocument(report, report.Document with
        { Fields = R(field with { Cardinality = new(CardinalityKind.Bounded, value, null) }, report.Document.Fields[1]) })));
        Integrity(() => CheckProjection(WithDocument(report, report.Document with
        { Fields = R(field with { NativeLocator = new TextSpanNativeLocator("provider.document", value, "1") }, report.Document.Fields[1]) })));
    }

    [Fact]
    public void ScalarUnionAndCardinalityContradictionsAreRefused()
    {
        var report = Graph();
        var doc = report.Document!;
        var field = doc.Fields[0];
        FieldDataType[] badTypes =
        [new(FieldDataTypeKind.Scalar, R<ScalarDataType>()), new(FieldDataTypeKind.Scalar, R(ScalarDataType.String, ScalarDataType.Boolean)),
            new(FieldDataTypeKind.ScalarUnion, R(ScalarDataType.String)), new(FieldDataTypeKind.ScalarUnion, R(ScalarDataType.String, ScalarDataType.String)),
            new(FieldDataTypeKind.Object, R(ScalarDataType.String)), new(FieldDataTypeKind.Unknown, R((ScalarDataType)999))];
        foreach (var type in badTypes)
            Integrity(() => CheckProjection(WithDocument(report, doc with { Fields = R(field with { DataType = type }, doc.Fields[1]) })));
        FieldCardinality[] badCardinalities =
        [new(CardinalityKind.Unknown, "0"), new(CardinalityKind.ExactlyOne, "0", "1"),
            new(CardinalityKind.ZeroOrMore, "0", "3"), new(CardinalityKind.Bounded),
            new(CardinalityKind.Bounded, "9007199254740994", "9007199254740993")];
        foreach (var cardinality in badCardinalities)
            Integrity(() => CheckProjection(WithDocument(report, doc with { Fields = R(field with { Cardinality = cardinality }, doc.Fields[1]) })));
    }

    [Fact]
    public void AggregateNestedItemBudgetIsSeparateFromTopLevelMetadataCount()
    {
        var report = Graph();
        // 16 top-level + 5 path + 3 scalar type + 3 dependencies + 4 rule details +
        // 1 control target + 1 control dependency + 2 control details + 2 capability scope/condition = 37.
        Assert.Equal(16, CheckProjection(report, new(itemLimit: 37)).Projection!.DocumentItemCount);
        Limit(() => CheckProjection(report, new(itemLimit: 36)));
        var nested = report.Document!.Rules[0] with
        {
            Details = R<DetailNode>(new GroupDetail("level0", R<DetailNode>(new ExpressionDetail("level1",
            R<DetailNode>(new LiteralDetail(new BooleanLiteralValue(true)))))))
        };
        Assert.NotNull(CheckProjection(WithDocument(report, report.Document with { Rules = R(nested) }), new(detailDepthLimit: 2)).Projection);
        Limit(() => CheckProjection(WithDocument(report, report.Document with { Rules = R(nested) }), new(detailDepthLimit: 1)));
    }

    [Fact]
    public void TextBudgetUsesExactUtf8BytesAndRefusesMalformedUnicode()
    {
        var report = Graph();
        ProjectionReportV1 WithLiteral(string value) => WithDocument(report, report.Document! with
        { Parameters = R(report.Document.Parameters[0] with { DefaultValue = new StringLiteralValue(value) }) });
        Assert.NotNull(CheckProjection(WithLiteral(new string('a', 128)), new(textByteLimit: 128)).Projection);
        Assert.NotNull(CheckProjection(WithLiteral(new string('é', 64)), new(textByteLimit: 128)).Projection);
        Limit(() => CheckProjection(WithLiteral(new string('é', 65)), new(textByteLimit: 128)));
        Limit(() => CheckProjection(WithLiteral(new string('a', 100_000)), new(textByteLimit: 128)));
        Integrity(() => CheckProjection(WithLiteral("\ud800")));
        Integrity(() => CheckProjection(WithLiteral("\udfff")));
    }

    [Fact]
    public void AuthoredReportBytesAndValueTokenCountsHaveExactInclusiveCeilings()
    {
        const string wire = """{"status":"backendFailure","provenance":null,"document":null,"diagnostics":[]}""";
        var report = new ProjectionReportV1(OperationStatusV1.BackendFailure, null, null, R<OperationDiagnosticV1>());
        var byteCount = Encoding.UTF8.GetByteCount(wire);
        Assert.Equal(RevisionReportBackendStatus.BackendFailure, CheckProjection(report, new(responseByteLimit: byteCount, responseItemLimit: 5, jsonDepthLimit: 2)).Status);
        Limit(() => CheckProjection(report, new(responseByteLimit: byteCount - 1)));
        Limit(() => CheckProjection(report, new(responseItemLimit: 4)));
        Limit(() => CheckProjection(report, new(jsonDepthLimit: 1)));
        var nested = report with { Diagnostics = R(new OperationDiagnosticV1("x", DiagnosticSeverity.Warning, "leaf", false)) };
        Assert.NotNull(CheckProjection(nested, new(jsonDepthLimit: 3)).Projection);
        Limit(() => CheckProjection(nested, new(jsonDepthLimit: 2)));
    }

    [Fact]
    public void HistoricalNativeByteCountIsNotSubjectToANewDerivedOnlyReadIoLimit()
    {
        var report = Graph();
        Assert.Equal(3, ProjectionKey().Source.NativeObject.ByteCount);
        Assert.NotNull(CheckProjection(report, new(nativeByteLimit: 1)).Projection);
    }

    [Fact]
    public void CancellationIsObservedDuringIndexedGraphTraversalAndAtEntry()
    {
        using var source = new CancellationTokenSource();
        var report = Graph();
        var values = new CancelOnFirstRead<ProjectionField>(report.Document!.Fields, source);
        report = WithDocument(report, report.Document with { Fields = values });
        Assert.Throws<OperationCanceledException>(() => CheckProjection(report, token: source.Token));
        Assert.Equal(1, values.Reads);
        using var alreadyCanceled = new CancellationTokenSource();
        alreadyCanceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => CheckProjection(Graph(), token: alreadyCanceled.Token));
    }
}
