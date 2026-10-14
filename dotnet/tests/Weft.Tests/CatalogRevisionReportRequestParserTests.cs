using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;
using Weft.Api.Catalog;
using Weft.Contracts.Catalog;
using Weft.Domain;

namespace Weft.Tests;

/// <summary>Pure parser corpus; synthetic framing features do not claim Kestrel transport proof.</summary>
public sealed class CatalogRevisionReportRequestParserTests
{
    private const string Organization = "00112233-4455-6677-8899-aabbccddeeff";
    private const string Project = "10213243-5465-7687-98a9-bacbdcedfe0f";
    private const string Mapping = "20314253-6475-8697-a8b9-cadbecfd0e1f";
    private const string Revision = "30415263-7485-96a7-b8c9-daebfc0d1e2f";
    private const string Derivation = "40516273-8495-a6b7-c8d9-eafb0c1d2e3f";

    [Fact]
    public void OptionsAreIndependentDefaultOffAndRetainTrustedImmutableReadCaps()
    {
        var defaults = new CatalogRevisionReportOptions();
        Assert.False(defaults.Enabled);
        Assert.False(defaults.DerivationEnabled);
        Assert.Equal(new RevisionReportPolicy(), defaults.ActiveReadCaps);
        var caps = new RevisionReportPolicy(responseByteLimit: 1024, responseItemLimit: 500, jsonDepthLimit: 12);
        var reads = new CatalogRevisionReportOptions(enabled: true, activeReadCaps: caps);
        Assert.True(reads.Enabled);
        Assert.False(reads.DerivationEnabled);
        Assert.Same(caps, reads.ActiveReadCaps);
        Assert.Throws<ArgumentException>(() => new CatalogRevisionReportOptions(derivationEnabled: true));
        Assert.True(new CatalogRevisionReportOptions(enabled: true, derivationEnabled: true).DerivationEnabled);
    }

    [Fact]
    public void CanonicalScopeAndRetainedIdentityBindEachExactIdentifier()
    {
        var actual = CatalogRevisionReportRequestParser.Scope(Organization, Project, Mapping, Revision);
        Assert.Equal(Guid.Parse(Organization), actual.OrganizationId);
        Assert.Equal(Guid.Parse(Project), actual.ProjectId);
        Assert.Equal(Guid.Parse(Mapping), actual.MappingId);
        Assert.Equal(Guid.Parse(Revision), actual.RevisionId);
        Assert.Equal(Guid.Parse(Derivation), CatalogRevisionReportRequestParser.RetainedId(Derivation));
    }

    [Theory]
    [InlineData("")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("405162738495a6b7c8d9eafb0c1d2e3f")]
    [InlineData("{40516273-8495-a6b7-c8d9-eafb0c1d2e3f}")]
    [InlineData("40516273-8495-A6B7-C8D9-EAFB0C1D2E3F")]
    [InlineData(" 40516273-8495-a6b7-c8d9-eafb0c1d2e3f")]
    [InlineData("40516273-8495-a6b7-c8d9-eafb0c1d2e3f ")]
    [InlineData("40516273-8495-a6b7-c8d9-eafb0c1d2e3f,40516273-8495-a6b7-c8d9-eafb0c1d2e3f")]
    public void EveryRouteIdentityRejectsNoncanonicalOrEmptyValues(string invalid)
    {
        Invalid(() => CatalogRevisionReportRequestParser.Scope(invalid, Project, Mapping, Revision));
        Invalid(() => CatalogRevisionReportRequestParser.Scope(Organization, invalid, Mapping, Revision));
        Invalid(() => CatalogRevisionReportRequestParser.Scope(Organization, Project, invalid, Revision));
        Invalid(() => CatalogRevisionReportRequestParser.Scope(Organization, Project, Mapping, invalid));
        Invalid(() => CatalogRevisionReportRequestParser.RetainedId(invalid));
    }

    [Theory]
    [InlineData("")]
    [InlineData("?")]
    public void EmptyQueryHasNoImplicitProjectionOrRepairSelection(string query)
    {
        var request = Request(query: query);
        Assert.Null(CatalogRevisionReportRequestParser.ParseRead(request, RevisionReportOperation.Projection).ProjectionDerivationId);
        Assert.Null(CatalogRevisionReportRequestParser.ParseRead(request, RevisionReportOperation.Validation).ProjectionDerivationId);
        Assert.Null(CatalogRevisionReportRequestParser.ParseEnsure(request).RepairDerivationId);
    }

    [Theory]
    [InlineData("?projectionDerivationId=40516273-8495-a6b7-c8d9-eafb0c1d2e3f")]
    [InlineData("?%70rojectionDerivationId=40516273-8495-a6b7-c8d9-eafb0c1d2e3f")]
    [InlineData("?projectionDerivationId=%340516273-8495-a6b7-c8d9-eafb0c1d2e3f")]
    public void ValidationAcceptsOnlyTheDecodedCanonicalExplicitProjection(string query)
    {
        var request = Request(query: query);
        Assert.Equal(Guid.Parse(Derivation), CatalogRevisionReportRequestParser.ParseRead(request,
            RevisionReportOperation.Validation).ProjectionDerivationId);
        Invalid(() => CatalogRevisionReportRequestParser.ParseRead(request, RevisionReportOperation.Projection));
        Invalid(() => CatalogRevisionReportRequestParser.ParseEnsure(request));
        Assert.Equal(0, ((NoReadStream)request.Body).Reads);
    }

    [Theory]
    [InlineData("?repairDerivationId=40516273-8495-a6b7-c8d9-eafb0c1d2e3f")]
    [InlineData("?%72epairDerivationId=%340516273-8495-a6b7-c8d9-eafb0c1d2e3f")]
    public void EnsureAcceptsOnlyAnExplicitCanonicalRepairSelector(string query)
    {
        var request = Request(query: query);
        Assert.Equal(Guid.Parse(Derivation), CatalogRevisionReportRequestParser.ParseEnsure(request).RepairDerivationId);
        Invalid(() => CatalogRevisionReportRequestParser.ParseRead(request, RevisionReportOperation.Projection));
        Invalid(() => CatalogRevisionReportRequestParser.ParseRead(request, RevisionReportOperation.Validation));
    }

    [Theory]
    [InlineData("?projectionDerivationId=")]
    [InlineData("?projectionDerivationId=00000000-0000-0000-0000-000000000000")]
    [InlineData("?projectionDerivationId=405162738495a6b7c8d9eafb0c1d2e3f")]
    [InlineData("?projectionDerivationId=40516273-8495-A6B7-C8D9-EAFB0C1D2E3F")]
    [InlineData("?projectionDerivationId=%2040516273-8495-a6b7-c8d9-eafb0c1d2e3f")]
    [InlineData("?projectionDerivationId=40516273-8495-a6b7-c8d9-eafb0c1d2e3f+")]
    [InlineData("?projectionDerivationId=40516273-8495-a6b7-c8d9-eafb0c1d2e3f%00")]
    [InlineData("?ProjectionDerivationId=40516273-8495-a6b7-c8d9-eafb0c1d2e3f")]
    [InlineData("?projectionDerivationId")]
    [InlineData("?=40516273-8495-a6b7-c8d9-eafb0c1d2e3f")]
    [InlineData("?projectionDerivationId==40516273-8495-a6b7-c8d9-eafb0c1d2e3f")]
    [InlineData("?projectionDerivationId=40516273-8495-a6b7-c8d9-eafb0c1d2e3f&")]
    [InlineData("?projectionDerivationId=40516273-8495-a6b7-c8d9-eafb0c1d2e3f&projectionDerivationId=40516273-8495-a6b7-c8d9-eafb0c1d2e3f")]
    [InlineData("?projectionDerivationId=40516273-8495-a6b7-c8d9-eafb0c1d2e3f&%70rojectionDerivationId=40516273-8495-a6b7-c8d9-eafb0c1d2e3f")]
    [InlineData("?projectionDerivationId=%")]
    [InlineData("?projectionDerivationId=%3")]
    [InlineData("?projectionDerivationId=%gg")]
    [InlineData("?projectionDerivationId=%c0%af")]
    [InlineData("?projectionDerivationId=%ed%a0%80")]
    [InlineData("?projectionDerivationId=%f4%90%80%80")]
    [InlineData("?%ff=40516273-8495-a6b7-c8d9-eafb0c1d2e3f")]
    [InlineData("?projectionDerivationId=%26repairDerivationId%3d40516273-8495-a6b7-c8d9-eafb0c1d2e3f")]
    [InlineData("?projectionDerivationId=40516273-8495-a6b7-c8d9-eafb0c1d2e3f;actor=x")]
    public void MalformedAmbiguousEscapedDuplicateOrNoncanonicalQueriesAreRefused(string query)
    {
        var request = Request(query: query);
        Invalid(() => CatalogRevisionReportRequestParser.ParseRead(request, RevisionReportOperation.Validation));
        Invalid(() => CatalogRevisionReportRequestParser.ParseRead(request, RevisionReportOperation.Projection));
        Invalid(() => CatalogRevisionReportRequestParser.ParseEnsure(request));
        Assert.Equal(0, ((NoReadStream)request.Body).Reads);
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("principalId")]
    [InlineData("profile")]
    [InlineData("policy")]
    [InlineData("backendId")]
    [InlineData("nativeDigest")]
    [InlineData("objectId")]
    [InlineData("leaseToken")]
    public void IdentityProfilePolicyAndPrivateInputsCannotBeInjectedThroughQuery(string name)
    {
        var request = Request(query: "?" + name + "=" + Derivation);
        Invalid(() => CatalogRevisionReportRequestParser.ParseRead(request, RevisionReportOperation.Validation));
        Invalid(() => CatalogRevisionReportRequestParser.ParseEnsure(request));
    }

    [Fact]
    public void FullyEscapedAcceptedSelectorFitsTheFixedBoundButUnboundedInputIsRefused()
    {
        static string EscapeAscii(string value) => string.Concat(value.Select(character => "%" + ((int)character).ToString("x2")));
        var query = "?" + EscapeAscii("projectionDerivationId") + "=" + EscapeAscii(Derivation);
        Assert.True(query.Length < 256);
        Assert.Equal(Guid.Parse(Derivation), CatalogRevisionReportRequestParser.ParseRead(Request(query: query),
            RevisionReportOperation.Validation).ProjectionDerivationId);
        Invalid(() => CatalogRevisionReportRequestParser.ParseRead(Request(query: "?" + new string('a', 256)),
            RevisionReportOperation.Validation));
    }

    [Fact]
    public void OnlyExplicitZeroOrTheFrameworkNoBodyFeatureEstablishesEmptyFraming()
    {
        foreach (var request in new[] { Request(), Request(canHaveBody: null, contentLength: "0"), Request(canHaveBody: true, contentLength: "0") })
        {
            ParseAll(request);
            Assert.Equal(0, ((NoReadStream)request.Body).Reads);
        }
        foreach (var request in new[] { Request(canHaveBody: null), Request(canHaveBody: true) })
            InvalidAll(request);
        var streamNull = Request(canHaveBody: null);
        streamNull.Body = Stream.Null;
        InvalidAll(streamNull);
    }

    [Theory]
    [InlineData("If-Match")]
    [InlineData("If-None-Match")]
    [InlineData("If-Modified-Since")]
    [InlineData("If-Unmodified-Since")]
    [InlineData("If-Range")]
    [InlineData("Idempotency-Key")]
    public void ForbiddenConditionalAndIdempotencyHeadersAreRejectedEvenWhenEmpty(string name)
    {
        var request = Request();
        request.Headers[name] = "";
        InvalidAll(request);
    }

    [Theory]
    [InlineData("Content-Type", "")]
    [InlineData("Content-Type", "application/json")]
    [InlineData("Content-Type", "application/octet-stream")]
    [InlineData("Content-Encoding", "")]
    [InlineData("Content-Encoding", "gzip")]
    [InlineData("Content-Encoding", "identity, identity")]
    [InlineData("Content-Encoding", "identity; charset=utf-8")]
    public void MediaAndCodingAreUnsupportedEvenWhenTheEntityIsKnownEmpty(string name, string value)
    {
        var request = Request(contentLength: "0");
        request.Headers[name] = value;
        InvalidAll(request, StatusCodes.Status415UnsupportedMediaType, CatalogErrorCodeV1.UnsupportedMediaType);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("IDENTITY")]
    [InlineData(" \tidentity\t ")]
    public void OneIdentityCodingAllowsProtocolWhitespaceAndCase(string value)
    {
        var request = Request();
        request.Headers.ContentEncoding = value;
        ParseAll(request);
        Assert.Equal(0, ((NoReadStream)request.Body).Reads);
    }

    [Theory]
    [InlineData("Transfer-Encoding", "")]
    [InlineData("Transfer-Encoding", "chunked")]
    [InlineData("Transfer-Encoding", "identity")]
    [InlineData("Content-Length", "")]
    [InlineData("Content-Length", "1")]
    [InlineData("Content-Length", "01")]
    [InlineData("Content-Length", "-1")]
    [InlineData("Content-Length", "+0")]
    [InlineData("Content-Length", "0, 0")]
    [InlineData("Content-Length", "not-a-length")]
    public void UnsupportedOrAmbiguousFramingIsRefusedWithoutProbingOrDraining(string name, string value)
    {
        var request = Request();
        request.Headers[name] = value;
        InvalidAll(request);
    }

    [Fact]
    public void RepeatedCodingAndLengthAreNotCollapsedIntoAnAcceptedValue()
    {
        var coding = Request();
        coding.Headers.ContentEncoding = new StringValues(["identity", "identity"]);
        InvalidAll(coding, StatusCodes.Status415UnsupportedMediaType, CatalogErrorCodeV1.UnsupportedMediaType);
        var lengths = Request();
        lengths.Headers["Content-Length"] = new StringValues(["0", "0"]);
        InvalidAll(lengths);
    }

    [Fact]
    public void ParserRefusalsCarryOnlyFixedCategoricalDataAndNeverReadTheBody()
    {
        const string privateInput = "private-root-password-profile";
        var request = Request(query: "?policy=" + privateInput);
        var error = Invalid(() => CatalogRevisionReportRequestParser.ParseEnsure(request));
        Assert.DoesNotContain(privateInput, error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
        Assert.Equal(0, ((NoReadStream)request.Body).Reads);
    }

    [Fact]
    public void UnknownInternalOperationIsNotAcceptedAsAUserSelectedVariant()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CatalogRevisionReportRequestParser.ParseRead(Request(),
            (RevisionReportOperation)999));
    }

    private static HttpRequest Request(bool? canHaveBody = false, string? contentLength = null, string? query = null)
    {
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpRequestBodyDetectionFeature>(canHaveBody is { } value ? new BodyDetection(value) : null);
        context.Request.Body = new NoReadStream();
        context.Request.QueryString = new QueryString(query ?? "");
        if (contentLength is not null)
            context.Request.Headers["Content-Length"] = contentLength;
        return context.Request;
    }

    private static void ParseAll(HttpRequest request)
    {
        CatalogRevisionReportRequestParser.ParseRead(request, RevisionReportOperation.Projection);
        CatalogRevisionReportRequestParser.ParseRead(request, RevisionReportOperation.Validation);
        CatalogRevisionReportRequestParser.ParseEnsure(request);
    }

    private static void InvalidAll(HttpRequest request, int status = StatusCodes.Status400BadRequest,
        CatalogErrorCodeV1 code = CatalogErrorCodeV1.InvalidRequest)
    {
        Invalid(() => CatalogRevisionReportRequestParser.ParseRead(request, RevisionReportOperation.Projection), status, code);
        Invalid(() => CatalogRevisionReportRequestParser.ParseRead(request, RevisionReportOperation.Validation), status, code);
        Invalid(() => CatalogRevisionReportRequestParser.ParseEnsure(request), status, code);
        if (request.Body is NoReadStream stream)
            Assert.Equal(0, stream.Reads);
    }

    private static CatalogRevisionReportRequestException Invalid(Action action,
        int status = StatusCodes.Status400BadRequest, CatalogErrorCodeV1 code = CatalogErrorCodeV1.InvalidRequest)
    {
        var error = Assert.Throws<CatalogRevisionReportRequestException>(action);
        Assert.Equal(status, error.StatusCode);
        Assert.Equal(code, error.Code);
        return error;
    }

    private sealed record BodyDetection(bool CanHaveBody) : IHttpRequestBodyDetectionFeature;

    private sealed class NoReadStream : Stream
    {
        internal int Reads { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            Reads++;
            throw new InvalidOperationException("Report requests must never consume an entity.");
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reads++;
            throw new InvalidOperationException("Report requests must never consume an entity.");
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
