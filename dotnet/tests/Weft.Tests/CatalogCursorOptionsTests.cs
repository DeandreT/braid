using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Weft.Api.Catalog;
using Weft.Application;
using Weft.Domain;

namespace Weft.Tests;

/// <summary>Wire parsing and explicit configuration only; actual peer/token/authority proof uses Kestrel and PostgreSQL.</summary>
public sealed class CatalogCursorOptionsTests
{
    private static readonly Guid Organization = Guid.Parse("abcdef01-2345-6789-abcd-ef0123456789");
    private static readonly Guid Project = Guid.Parse("bcdef012-3456-789a-bcde-f0123456789a");
    private static readonly Guid Mapping = Guid.Parse("cdef0123-4567-89ab-cdef-0123456789ab");
    private static readonly Guid Last = Guid.Parse("def01234-5678-9abc-def0-123456789abc");
    private static ProjectScope ProjectScope => new(Organization, Project);
    private static MappingScope MappingScope => new(Organization, Project, Mapping);

    [Theory]
    [InlineData("http://127.0.0.1:5050")]
    [InlineData("http://127.0.0.2:5050")]
    [InlineData("http://127.0.0.1")]
    [InlineData("http://[::1]")]
    [InlineData("http://[::1]:5050")]
    public void ConcreteCanonicalLoopbackHttpOriginCanBeExplicitlyConfigured(string origin)
    {
        var options = new CatalogApiOptions(true, true, Last, Organization, origin);
        var services = new ServiceCollection();

        Assert.Same(services, services.AddWeftCatalog(options, Environment("Development")));
        Assert.Equal(origin, options.BrowserOrigin);
    }

    [Theory]
    [InlineData("http://localhost:5050")]
    [InlineData("https://127.0.0.1:5050")]
    [InlineData("http://127.0.0.1:80")]
    [InlineData("http://[::1]:80")]
    [InlineData("http://127.0.0.1:5050/")]
    [InlineData("HTTP://127.0.0.1:5050")]
    [InlineData("http://127.1:5050")]
    [InlineData("http://127.0.0.1:0")]
    [InlineData("http://192.0.2.1:5050")]
    [InlineData("http://user@127.0.0.1:5050")]
    [InlineData("http://127.0.0.1:5050?query=1")]
    [InlineData("http://127.0.0.1:5050#fragment")]
    [InlineData("http://127.0.0.1:5050/other")]
    [InlineData("http://[0:0:0:0:0:0:0:1]:5050")]
    public void NoncanonicalHostedOrAmbiguousOriginIsRejected(string origin) =>
        Assert.Throws<ArgumentException>(() => new CatalogApiOptions(true, true, Last, Organization, origin));

    [Theory]
    [InlineData("http://127.0.0.1")]
    [InlineData("http://[::1]")]
    public void CanonicalDefaultPortOriginRetainsEffectiveHttpPortEighty(string origin)
    {
        var options = new CatalogApiOptions(true, true, Last, Organization, origin);
        var parsed = new Uri(options.BrowserOrigin!);

        Assert.True(parsed.IsDefaultPort);
        Assert.Equal(80, parsed.Port);
        Assert.Equal(origin, parsed.GetLeftPart(UriPartial.Authority));
    }

    [Fact]
    public void EnabledIdentityRequiresEveryExplicitSettingAndDevelopmentEnvironment()
    {
        var services = new ServiceCollection();
        var enabled = new CatalogApiOptions(true, true, Last, Organization, "http://127.0.0.1:5050");
        Assert.Throws<InvalidOperationException>(() => services.AddWeftCatalog(enabled, Environment("Production")));
        Assert.Throws<InvalidOperationException>(() => services.AddWeftCatalog(new(true), Environment("Development")));
        Assert.Throws<InvalidOperationException>(() => services.AddWeftCatalog(
            new(true, false, Last, Organization, "http://127.0.0.1:5050"), Environment("Development")));
        Assert.Throws<InvalidOperationException>(() => services.AddWeftCatalog(
            new(true, true, Last, Organization), Environment("Development")));
        Assert.Throws<ArgumentException>(() => new CatalogApiOptions(principalId: Guid.Empty));
        Assert.Throws<ArgumentException>(() => new CatalogApiOptions(organizationId: Guid.Empty));
        Assert.Same(services, services.AddWeftCatalog(new(), Environment("Production")));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void ExplicitDevelopmentIdentityIsRejectedInHostedEnvironmentEvenWhenCatalogDisabled(string environment)
    {
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() => services.AddWeftCatalog(
            new(enabled: false, developmentIdentityEnabled: true), Environment(environment)));
    }

    [Fact]
    public void EveryCursorOwnsCompleteScopeAndKeepsLargeSequenceAsAnExactDecimalString()
    {
        var project = new CatalogProjectCursor(Organization, Last);
        var mapping = new CatalogMappingCursor(ProjectScope, Last);
        var revision = new CatalogRevisionCursor(MappingScope, long.MaxValue);

        Assert.Equal(project, CatalogCursorCodec.DecodeProjects(CatalogCursorCodec.Encode(project), Organization));
        Assert.Equal(mapping, CatalogCursorCodec.DecodeMappings(CatalogCursorCodec.Encode(mapping), ProjectScope));
        var encoded = CatalogCursorCodec.Encode(revision);
        Assert.Equal(revision, CatalogCursorCodec.DecodeRevisions(encoded, MappingScope));
        Assert.DoesNotContain('=', encoded);
        Assert.Contains("\"last\":\"9223372036854775807\"", Decode(encoded), StringComparison.Ordinal);
    }

    [Fact]
    public void CrossKindOrAnyForeignAncestorCannotBecomeAContinuation()
    {
        var projects = CatalogCursorCodec.Encode(new CatalogProjectCursor(Organization, Last));
        var mappings = CatalogCursorCodec.Encode(new CatalogMappingCursor(ProjectScope, Last));
        var revisions = CatalogCursorCodec.Encode(new CatalogRevisionCursor(MappingScope, 4));
        Assert.Throws<ArgumentException>(() => CatalogCursorCodec.DecodeProjects(projects, Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => CatalogCursorCodec.DecodeProjects(mappings, Organization));
        Assert.Throws<ArgumentException>(() => CatalogCursorCodec.DecodeMappings(projects, ProjectScope));
        Assert.Throws<ArgumentException>(() => CatalogCursorCodec.DecodeMappings(mappings, new(Organization, Guid.NewGuid())));
        Assert.Throws<ArgumentException>(() => CatalogCursorCodec.DecodeRevisions(revisions, new(Guid.NewGuid(), Project, Mapping)));
        Assert.Throws<ArgumentException>(() => CatalogCursorCodec.DecodeRevisions(revisions, new(Organization, Guid.NewGuid(), Mapping)));
        Assert.Throws<ArgumentException>(() => CatalogCursorCodec.DecodeRevisions(revisions, new(Organization, Project, Guid.NewGuid())));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("floatingVersion")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("missing")]
    [InlineData("uppercase")]
    [InlineData("emptyId")]
    [InlineData("order")]
    [InlineData("lastType")]
    [InlineData("padding")]
    [InlineData("whitespace")]
    [InlineData("oversized")]
    [InlineData("invalidUtf8")]
    public void StrictCursorShapeEncodingAndBudgetRejectAmbiguousInput(string malformed)
    {
        var cursor = CatalogCursorCodec.Encode(new CatalogProjectCursor(Organization, Last));
        var json = Decode(cursor);
        var bad = malformed switch
        {
            "version" => Encode(json.Replace("\"v\":1", "\"v\":2", StringComparison.Ordinal)),
            "floatingVersion" => Encode(json.Replace("\"v\":1", "\"v\":1.0", StringComparison.Ordinal)),
            "duplicate" => Encode(json.Replace("\"v\":1", "\"v\":1,\"v\":1", StringComparison.Ordinal)),
            "unknown" => Encode(json.Replace("\"v\":1", "\"v\":1,\"extra\":false", StringComparison.Ordinal)),
            "missing" => Encode(json.Replace("\"kind\":\"projects\",", "", StringComparison.Ordinal)),
            "uppercase" => Encode(json.Replace(Last.ToString("D"), Last.ToString("D").ToUpperInvariant(), StringComparison.Ordinal)),
            "emptyId" => Encode(json.Replace(Last.ToString("D"), Guid.Empty.ToString("D"), StringComparison.Ordinal)),
            "order" => Encode(json.Replace("uuid-ascending", "uuid-descending", StringComparison.Ordinal)),
            "lastType" => Encode(json.Replace("\"last\":\"" + Last.ToString("D") + "\"", "\"last\":7", StringComparison.Ordinal)),
            "padding" => cursor + "=",
            "whitespace" => cursor + " ",
            "oversized" => Convert.ToBase64String(new byte[513]).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
            _ => Convert.ToBase64String(new byte[] { 0xff }).TrimEnd('=').Replace('+', '-').Replace('/', '_')
        };

        Assert.Throws<ArgumentException>(() => CatalogCursorCodec.DecodeProjects(bad, Organization));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("01")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("1.0")]
    [InlineData("9223372036854775808")]
    public void RevisionCursorRejectsNoncanonicalOrOutOfRangeDecimalSequence(string value)
    {
        var json = Decode(CatalogCursorCodec.Encode(new CatalogRevisionCursor(MappingScope, 1)));
        var bad = Encode(json.Replace("\"last\":\"1\"", "\"last\":\"" + value + "\"", StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(() => CatalogCursorCodec.DecodeRevisions(bad, MappingScope));
    }

    private static string Encode(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Decode(string encoded) => Encoding.UTF8.GetString(Convert.FromBase64String(
        encoded.Replace('-', '+').Replace('_', '/') + new string('=', (4 - encoded.Length % 4) % 4)));
    private static IHostEnvironment Environment(string name) => new TestEnvironment
    {
        EnvironmentName = name,
        ApplicationName = "Catalog cursor/options unit tests",
        ContentRootPath = "/tmp",
        ContentRootFileProvider = new NullFileProvider()
    };
    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "";
        public string ApplicationName { get; set; } = "";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
