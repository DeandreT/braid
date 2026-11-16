using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Braid.Api.Catalog;
using Braid.Application;
using Braid.Contracts.Catalog;
using Braid.Domain;
using Braid.Identity;

namespace Braid.Tests;

/// <summary>Pure contracts and synthetic registration/Development collaborators; no cookie, HTTPS or database qualification.</summary>
public sealed class CatalogBrowserSessionContractTests
{
    private static readonly Guid Organization = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Principal = Guid.Parse("20000000-0000-0000-0000-000000000002");
    private const string Token = "owned-synthetic-request-token";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    [Theory]
    [InlineData("development")]
    [InlineData("session")]
    public void ExactIdentityModesRoundtripTheirBoundedPublicShapes(string mode)
    {
        var value = Context(mode);
        var encoded = JsonSerializer.Serialize(value, Json);
        using var document = JsonDocument.Parse(encoded);
        string[] expected = ["actorPrincipalId", "allowedOrganizationId", "features", "mode", "organizationActions", "organizationName", "requestToken"];
        if (mode == "session") expected = expected.Append("identityContext").Order(StringComparer.Ordinal).ToArray();
        Assert.True(document.RootElement.EnumerateObject().Select(item => item.Name).Order(StringComparer.Ordinal).SequenceEqual(expected),
            "Catalog context shape changed.");
        var roundtrip = JsonSerializer.Deserialize<CatalogContextV1>(encoded, Json);
        Assert.True(roundtrip is not null && roundtrip == value && roundtrip.Mode == mode && !roundtrip.Features.CreateProjects &&
            !roundtrip.Features.Chat && !roundtrip.Features.Preview && !roundtrip.OrganizationActions.CreateProjects,
            "Identity mode changed context authority or roundtrip values.");
        Assert.True(!value.ToString().Contains(Token, StringComparison.Ordinal), "Context diagnostics exposed its request token.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Development")]
    [InlineData("SESSION")]
    [InlineData("cookie")]
    [InlineData("hosted")]
    [InlineData(" development")]
    [InlineData("session ")]
    [InlineData("Session")]
    public void ModeSelectionRejectsAliasesAndUnselectedProfiles(string? mode) =>
        Assert.True(Capture(() => Context(mode!)) is ArgumentException, "An unsupported catalog mode was admitted.");

    [Theory]
    [InlineData("development")]
    [InlineData("session")]
    public void BothModesRetainRequiredRequestTokenAndExactByteBudget(string mode)
    {
        var maximum = new string('a', 4096);
        Assert.True(Context(mode, maximum).RequestToken!.Length == 4096, "The admitted token bound changed.");
        foreach (var invalid in new[] { "", " ", new string('a', 4097), new string('é', 2049) })
            Assert.True(Capture(() => Context(mode, invalid)) is ArgumentException, "An invalid token escaped the existing DTO bound.");
    }

    [Fact]
    public void SessionOptionsDiscloseNoOrganizationThroughDefaultDiagnosticsOrSerialization()
    {
        var options = new CatalogBrowserSessionOptions(Organization);
        Assert.True(options.OrganizationId == Organization, "The configured organization changed.");
        Assert.True(string.Equals(options.ToString(), nameof(CatalogBrowserSessionOptions), StringComparison.Ordinal) &&
            string.Equals(JsonSerializer.Serialize(options, Json), "{}", StringComparison.Ordinal),
            "Session options disclosed configuration through default diagnostics.");
    }

    [Fact]
    public void SessionOptionsRequireAnExplicitNonemptyOrganization() =>
        Assert.True(Capture(() => new CatalogBrowserSessionOptions(Guid.Empty)) is ArgumentException,
            "An empty configured organization was admitted.");

    [Fact]
    public void RegistrationDefersTheExactBrowserAndAllBusinessConstruction()
    {
        var services = new ServiceCollection();
        var factoryCalls = 0;
        services.AddSingleton<ServerSessionBrowser>(_ =>
        {
            factoryCalls++;
            throw new InvalidOperationException("The owned synthetic browser factory must stay uncalled.");
        });
        services.AddSingleton<ICatalogReader>(_ => throw new InvalidOperationException("Business construction must stay deferred."));
        var metadata = new CatalogMetadataEditOptions();
        var ingestion = new CatalogRevisionIngestionOptions();
        var creation = new CatalogProjectCreationOptions();
        var reports = new CatalogRevisionReportOptions();
        var result = services.AddBraidSessionCatalog(new(Organization), metadata, ingestion, creation, reports);
        var boundaries = services.Where(item => item.ServiceType == typeof(CatalogRequestBoundary)).ToArray();
        Assert.True(ReferenceEquals(result, services) && factoryCalls == 0 && boundaries.Length == 1 &&
            boundaries[0].Lifetime == ServiceLifetime.Singleton && boundaries[0].ImplementationFactory is not null,
            "Catalog registration constructed or replaced the browser boundary.");
        Assert.True(services.Count(item => item.ServiceType == typeof(ServerSessionBrowser)) == 1 &&
            !services.Any(item => item.ServiceType == typeof(CatalogApiOptions) || item.ServiceType == typeof(IAntiforgery)),
            "Session registration installed a Development profile or replaced browser ownership.");
        Assert.True(services.Any(item => item.ServiceType == typeof(CatalogMetadataEditOptions) && ReferenceEquals(item.ImplementationInstance, metadata)) &&
            services.Any(item => item.ServiceType == typeof(CatalogRevisionIngestionOptions) && ReferenceEquals(item.ImplementationInstance, ingestion)) &&
            services.Any(item => item.ServiceType == typeof(CatalogProjectCreationOptions) && ReferenceEquals(item.ImplementationInstance, creation)) &&
            services.Any(item => item.ServiceType == typeof(CatalogRevisionReportOptions) && ReferenceEquals(item.ImplementationInstance, reports)),
            "Catalog feature selection was replaced by implicit defaults.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void MissingAmbiguousKeyedOrTransientBrowserRefusesBeforeMutation(int shape)
    {
        var services = new ServiceCollection();
        var calls = 0;
        ServerSessionBrowser Forbidden()
        {
            calls++;
            throw new InvalidOperationException("The owned synthetic browser factory must stay uncalled.");
        }
        if (shape == 1) services.AddTransient<ServerSessionBrowser>(_ => Forbidden());
        if (shape == 2) services.AddKeyedSingleton<ServerSessionBrowser>("unexpected", (_, _) => Forbidden());
        if (shape == 3)
        {
            services.AddSingleton<ServerSessionBrowser>(_ => Forbidden());
            services.AddSingleton<ServerSessionBrowser>(_ => Forbidden());
        }
        var count = services.Count;
        var failure = Capture(() => AddSession(services));
        Assert.True(failure is InvalidOperationException && calls == 0 && services.Count == count,
            "An invalid browser descriptor was admitted or registration partially mutated services.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void RequiredRegistrationArgumentsRejectBeforeServiceMutation(int missing)
    {
        var services = SyntheticBrowserServices();
        var count = services.Count;
        var failure = Capture(() => CatalogEndpoints.AddBraidSessionCatalog(missing == 0 ? null! : services,
            missing == 1 ? null! : new(Organization), missing == 2 ? null! : new(),
            missing == 3 ? null! : new(), missing == 4 ? null! : new(), missing == 5 ? null! : new()));
        Assert.True(failure is ArgumentNullException && services.Count == count,
            "A missing registration argument was admitted or changed service ownership.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DevelopmentAndSessionProfilesCannotReplaceEachOtherInEitherOrder(bool sessionFirst)
    {
        var services = SyntheticBrowserServices();
        if (sessionFirst) AddSession(services);
        else AddDevelopment(services);
        var count = services.Count;
        var failure = Capture(() =>
        {
            if (sessionFirst) AddDevelopment(services);
            else AddSession(services);
        });
        Assert.True(failure is InvalidOperationException && services.Count == count,
            "A second catalog identity profile replaced or partially changed the first.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedCatalogProfileRegistrationIsClosedBeforeMutation(bool session)
    {
        var services = SyntheticBrowserServices();
        if (session) AddSession(services);
        else AddDevelopment(services);
        var count = services.Count;
        var failure = Capture(() =>
        {
            if (session) AddSession(services);
            else AddDevelopment(services);
        });
        Assert.True(failure is InvalidOperationException && services.Count == count,
            "Duplicate catalog registration changed the first profile.");
    }

    [Fact]
    public async Task DevelopmentAsyncTokenPathPreservesTheExistingTrustedActorAndMapper()
    {
        var antiforgery = new CountingAntiforgery();
        var boundary = new CatalogRequestBoundary(DevelopmentOptions(), antiforgery);
        var context = DevelopmentContext();
        var actor = new ActorContext(new(Principal));
        var result = await boundary.IssueRequestTokenAsync(context, actor, default);
        var mapped = CatalogPublicMapper.Context(new(Organization, actor.PrincipalId, "Owned organization"), result);
        Assert.True(string.Equals(result, Token, StringComparison.Ordinal) && antiforgery.Issued == 1 &&
            mapped.Mode == "development" && !mapped.Features.CreateProjects && !mapped.Features.Chat && !mapped.Features.Preview,
            "The Development token or context contract changed.");
        Assert.True(context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value == Organization.ToString("D") + ":" + Principal.ToString("D"),
            "The Development antiforgery identity changed.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CanceledAsyncTokenIssuancePreservesOriginalCauseAndNeverCallsAntiforgery(int cause)
    {
        var antiforgery = new CountingAntiforgery();
        var boundary = new CatalogRequestBoundary(DevelopmentOptions(), antiforgery);
        var context = DevelopmentContext();
        using var caller = new CancellationTokenSource();
        using var request = new CancellationTokenSource();
        if (cause != 1) caller.Cancel();
        if (cause != 0) request.Cancel();
        context.RequestAborted = request.Token;
        OperationCanceledException? canceled = null;
        try { await boundary.IssueRequestTokenAsync(context, new(new(Principal)), caller.Token); }
        catch (OperationCanceledException error) { canceled = error; }
        var expected = cause == 1 ? request.Token : caller.Token;
        Assert.True(canceled is not null && canceled.CancellationToken == expected && antiforgery.Issued == 0,
            "Token cancellation changed its cause or exposed a token.");
    }

    [Fact]
    public async Task AsyncDevelopmentIssuanceCannotUseAnotherActor()
    {
        var antiforgery = new CountingAntiforgery();
        var boundary = new CatalogRequestBoundary(DevelopmentOptions(), antiforgery);
        Exception? failure = null;
        try { await boundary.IssueRequestTokenAsync(DevelopmentContext(), new(new(Organization)), default); }
        catch (Exception error) { failure = error; }
        Assert.True(failure is InvalidOperationException && antiforgery.Issued == 0,
            "The Development boundary exposed a token to another actor.");
    }

    private static CatalogContextV1 Context(string mode, string token = Token) =>
        new(mode, Principal.ToString("D"), Organization.ToString("D"), "Owned organization", CatalogFeaturesV1.ReadOnly,
            CatalogOrganizationActionsV1.None, token, mode == "session" ? "v1." + new string('A', 43) : null);

    private static Exception? Capture(Action action)
    {
        try { action(); return null; }
        catch (Exception error) { return error; }
    }

    private static ServiceCollection SyntheticBrowserServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ServerSessionBrowser>(_ =>
            throw new InvalidOperationException("The owned synthetic browser factory must stay uncalled."));
        return services;
    }

    private static void AddSession(IServiceCollection services) =>
        services.AddBraidSessionCatalog(new(Organization), new(), new(), new(), new());

    private static void AddDevelopment(IServiceCollection services) =>
        services.AddBraidCatalog(DevelopmentOptions(), new DevelopmentEnvironment());

    private static CatalogApiOptions DevelopmentOptions() =>
        new(true, true, Principal, Organization, "http://127.0.0.1:4100");

    private static DefaultHttpContext DevelopmentContext()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Scheme = "http";
        context.Request.Host = new("127.0.0.1", 4100);
        return context;
    }

    private sealed class CountingAntiforgery : IAntiforgery
    {
        internal int Issued { get; private set; }
        public AntiforgeryTokenSet GetAndStoreTokens(HttpContext context)
        {
            Issued++;
            return new(Token, "owned-synthetic-cookie", "__RequestVerificationToken", CatalogRequestBoundary.RequestTokenHeader);
        }
        public AntiforgeryTokenSet GetTokens(HttpContext context) => GetAndStoreTokens(context);
        public Task<bool> IsRequestValidAsync(HttpContext context) => Task.FromResult(false);
        public Task ValidateRequestAsync(HttpContext context) => throw new InvalidOperationException("No synthetic request validation is permitted.");
        public void SetCookieTokenAndHeader(HttpContext context) => throw new InvalidOperationException("No separate synthetic cookie write is permitted.");
    }

    private sealed class DevelopmentEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Braid.CatalogContractTests";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
