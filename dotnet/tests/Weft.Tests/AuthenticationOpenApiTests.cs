using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Weft.Api;
using Weft.Api.Auth;
using Weft.Api.Catalog;
using Weft.Identity;

namespace Weft.Tests;

/// <summary>Static supported-contract generation only; no session, database, provider or live-host qualification.</summary>
public sealed class AuthenticationOpenApiTests
{
    private const string Origin = "http://127.0.0.1:4100";
    private static readonly (string Path, string Method, string Id, string? Header, string Success, string? Schema)[] Routes =
    [
        ("/api/auth/session", "get", "ReadBrowserAuthentication", "X-Weft-Session", "200", "AuthStatusV1"),
        ("/api/auth/login", "post", "BeginBrowserLogin", "X-Weft-Login", "200", "AuthLoginV1"),
        ("/api/auth/callback/entra", "get", "CompleteEntraLogin", null, "303", null),
        ("/api/auth/logout", "post", "LogoutBrowserSession", "X-Weft-Logout", "204", null),
        ("/api/auth/forget-local", "post", "ForgetDeniedLocalSession", "X-Weft-Forget-Local", "200", "AuthForgetLocalV1")
    ];

    [Fact]
    public async Task DefaultApiPublishesAllSupportedAuthWithoutRuntimeServicesEndpointsCookiesOrRedirects()
    {
        await using var app = await StartAsync(catalog: false);
        using var client = app.GetTestClient();
        using var document = await ReadAsync(client);
        AuthenticationContract(document.RootElement);
        foreach (var route in Routes)
        {
            using var request = new HttpRequestMessage(new HttpMethod(route.Method.ToUpperInvariant()), route.Path);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            NoEffects(response);
            Assert.Equal("", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task DefaultOffCatalogRetainsClosedDevelopmentAndSessionContextArms()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        using var document = await ReadAsync(client);
        AuthenticationContract(document.RootElement);
        CatalogContext(document.RootElement);
        using var unavailable = await client.GetAsync("/api/v1/catalog/context");
        Assert.Equal(HttpStatusCode.NotFound, unavailable.StatusCode);
        NoEffects(unavailable);
    }

    [Theory]
    [InlineData(AuthenticationHttpMode.Disabled, true)]
    [InlineData(AuthenticationHttpMode.Disabled, false)]
    [InlineData(AuthenticationHttpMode.Development, true)]
    [InlineData(AuthenticationHttpMode.Development, false)]
    public async Task RepeatedGenerationAndExplicitNonSessionRegistrationOrderKeepTheSameSupportedDocument(
        AuthenticationHttpMode mode, bool apiFirst)
    {
        await using var baseline = await StartAsync();
        using var baselineClient = baseline.GetTestClient();
        using var expected = await ReadAsync(baselineClient);
        foreach (var mapAuthentication in new[] { false, true })
        {
            await using var app = await StartAsync(mode, apiFirst, mapAuthentication: mapAuthentication);
            using var client = app.GetTestClient();
            for (var repeat = 0; repeat < 2; repeat++)
            {
                using var actual = await ReadAsync(client);
                AuthenticationContract(actual.RootElement);
                CatalogContext(actual.RootElement);
                Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected.RootElement.GetRawText()),
                    JsonNode.Parse(actual.RootElement.GetRawText())), "Supported contract changed with generation or runtime composition order.");
            }
        }
    }

    private static void AuthenticationContract(JsonElement root)
    {
        Assert.StartsWith("3.1.", root.GetProperty("openapi").GetString(), StringComparison.Ordinal);
        var schemas = root.GetProperty("components").GetProperty("schemas");
        var session = schemas.GetProperty("AuthSessionV1");
        Assert.Equal(new[] { "oneOf" }, session.EnumerateObject().Select(property => property.Name));
        var arms = session.GetProperty("oneOf").EnumerateArray().ToArray();
        Assert.Equal(3, arms.Length);
        Assert.Equal(new[] { "anonymous", "authenticated", "authenticationRequired" }, arms.Select(arm =>
            arm.GetProperty("properties").GetProperty("state").GetProperty("enum")[0].GetString()!).Order(StringComparer.Ordinal));
        foreach (var arm in arms)
        {
            Shape(arm, ["version", "mode", "state", "authActionRequestToken", "requestToken", "identityContext", "displayName"]);
            var properties = arm.GetProperty("properties");
            Version(properties.GetProperty("version"));
            Text(properties.GetProperty("mode"), "session");
            var state = properties.GetProperty("state").GetProperty("enum")[0].GetString()!;
            Text(properties.GetProperty("state"), state);
            Token(properties.GetProperty("authActionRequestToken"));
            if (state == "authenticated")
            {
                Token(properties.GetProperty("requestToken"));
                Fingerprint(properties.GetProperty("identityContext"));
                Assert.Equal(200, properties.GetProperty("displayName").GetProperty("maxLength").GetInt32());
            }
            else
            {
                Null(properties.GetProperty("requestToken"));
                Null(properties.GetProperty("identityContext"));
                Null(properties.GetProperty("displayName"));
            }
        }
        var mode = schemas.GetProperty("AuthModeStatusV1");
        Shape(mode, ["version", "mode"]);
        Version(mode.GetProperty("properties").GetProperty("version"));
        Text(mode.GetProperty("properties").GetProperty("mode"), "development", "disabled");
        var status = schemas.GetProperty("AuthStatusV1");
        Assert.Equal(new[] { "oneOf" }, status.EnumerateObject().Select(property => property.Name));
        Assert.Equal(new[] { "#/components/schemas/AuthSessionV1", "#/components/schemas/AuthModeStatusV1" },
            status.GetProperty("oneOf").EnumerateArray().Select(arm => arm.GetProperty("$ref").GetString()!));
        var login = schemas.GetProperty("AuthLoginV1");
        Shape(login, ["version", "authorizationUrl"]);
        Version(login.GetProperty("properties").GetProperty("version"));
        var url = login.GetProperty("properties").GetProperty("authorizationUrl");
        Assert.Equal("string", url.GetProperty("type").GetString());
        Assert.Equal("uri", url.GetProperty("format").GetString());
        Assert.Equal(16384, url.GetProperty("maxLength").GetInt32());
        var forget = schemas.GetProperty("AuthForgetLocalV1");
        Shape(forget, ["version", "outcome"]);
        Version(forget.GetProperty("properties").GetProperty("version"));
        Text(forget.GetProperty("properties").GetProperty("outcome"), "revocationUnconfirmed");
        var error = schemas.GetProperty("AuthErrorV1");
        Shape(error, ["version", "code"]);
        Version(error.GetProperty("properties").GetProperty("version"));
        Text(error.GetProperty("properties").GetProperty("code"), "invalidRequest", "authenticationRequired",
            "requestRejected", "forgetNotAvailable", "unavailable");

        var paths = root.GetProperty("paths");
        Assert.Equal(Routes.Select(route => route.Path).Order(StringComparer.Ordinal), paths.EnumerateObject()
            .Where(property => property.Name.StartsWith("/api/auth/", StringComparison.Ordinal))
            .Select(property => property.Name).Order(StringComparer.Ordinal));
        foreach (var route in Routes)
        {
            var item = paths.GetProperty(route.Path);
            Assert.Equal(new[] { route.Method }, item.EnumerateObject().Select(property => property.Name));
            var operation = item.GetProperty(route.Method);
            Assert.Equal(route.Id, operation.GetProperty("operationId").GetString());
            Assert.False(operation.TryGetProperty("requestBody", out _));
            Assert.Contains("presence does not activate", operation.GetProperty("description").GetString(), StringComparison.Ordinal);
            Parameters(operation, route.Path, route.Method, route.Header);
            var responses = operation.GetProperty("responses");
            var callback = route.Header is null;
            string[] failures = callback ? ["401", "405", "503"] : ["400", "401", "403", "405", "409", "503"];
            Assert.Equal(failures.Append(route.Success).Order(StringComparer.Ordinal), responses.EnumerateObject()
                .Select(property => property.Name).Order(StringComparer.Ordinal));
            foreach (var response in responses.EnumerateObject())
            {
                var headers = response.Value.GetProperty("headers");
                Text(headers.GetProperty("Cache-Control").GetProperty("schema"), "no-store");
                Text(headers.GetProperty("Referrer-Policy").GetProperty("schema"), "no-referrer");
                Assert.False(headers.TryGetProperty("Set-Cookie", out _));
                Assert.False(headers.TryGetProperty("ETag", out _));
                Assert.False(headers.TryGetProperty("Last-Modified", out _));
                if (callback && response.Name == "303")
                    ReturnPath(headers.GetProperty("Location").GetProperty("schema"));
                else Assert.False(headers.TryGetProperty("Location", out _));
                if (callback && response.Name is "401" or "503")
                {
                    var content = response.Value.GetProperty("content");
                    Assert.Equal(new[] { "text/html" }, content.EnumerateObject().Select(property => property.Name));
                    var html = content.GetProperty("text/html").GetProperty("schema");
                    Assert.Equal("string", html.GetProperty("type").GetString());
                    Assert.Equal(1024, html.GetProperty("maxLength").GetInt32());
                }
                else if (response.Name == route.Success && route.Schema is null)
                    Assert.False(response.Value.TryGetProperty("content", out _));
                else
                {
                    var content = response.Value.GetProperty("content");
                    Assert.Equal(new[] { "application/json" }, content.EnumerateObject().Select(property => property.Name));
                    Assert.Equal("#/components/schemas/" + (response.Name == route.Success ? route.Schema : "AuthErrorV1"),
                        content.GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
                }
            }
        }
    }

    [Fact]
    public async Task Every_bearer_operation_declares_validation_availability_and_challenge_headers()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        using var document = await ReadAsync(client);
        var count = 0;
        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
            foreach (var operation in path.Value.EnumerateObject())
            {
                if (!operation.Value.TryGetProperty("security", out var security) ||
                    !security.EnumerateArray().Any(requirement => requirement.TryGetProperty("entraBearer", out _))) continue;
                count++;
                var responses = operation.Value.GetProperty("responses");
                foreach (var status in new[] { "400", "401", "403", "429", "503" })
                    Assert.True(responses.TryGetProperty(status, out _), $"{operation.Name} {path.Name} lacks {status}.");
                foreach (var (status, error) in new[] { ("400", "invalid_request"), ("401", "invalid_token") })
                {
                    var schema = responses.GetProperty(status).GetProperty("headers").GetProperty("WWW-Authenticate").GetProperty("schema");
                    Assert.Equal("string", schema.GetProperty("type").GetString());
                    Assert.Equal("^Bearer error=\"" + error + "\"$", schema.GetProperty("pattern").GetString());
                }
            }
        Assert.True(count > 0);
    }

    private static void CatalogContext(JsonElement root)
    {
        var schema = root.GetProperty("components").GetProperty("schemas").GetProperty("CatalogContextV1");
        Assert.Equal(new[] { "oneOf" }, schema.EnumerateObject().Select(property => property.Name));
        var arms = schema.GetProperty("oneOf").EnumerateArray().ToArray();
        Assert.Equal(3, arms.Length);
        string[] common = ["mode", "actorPrincipalId", "allowedOrganizationId", "organizationName", "features", "organizationActions"];
        foreach (var mode in new[] { "development", "session", "bearer" })
        {
            var arm = Assert.Single(arms, value => value.GetProperty("properties").GetProperty("mode")
                .GetProperty("enum")[0].GetString() == mode);
            // A bearer request has no cookie, so it gets neither an antiforgery token nor an identity fingerprint.
            Shape(arm, mode switch { "session" => [.. common, "requestToken", "identityContext"], "bearer" => common, _ => [.. common, "requestToken"] });
            var properties = arm.GetProperty("properties");
            Text(properties.GetProperty("mode"), mode);
            if (mode == "session") Fingerprint(properties.GetProperty("identityContext"));
            else Assert.False(properties.TryGetProperty("identityContext", out _));
        }
    }

    private static void Parameters(JsonElement operation, string path, string method, string? action)
    {
        JsonElement[] parameters = operation.TryGetProperty("parameters", out var declared)
            ? declared.EnumerateArray().ToArray() : [];
        string[] expected = action is null ? [] : method == "get" ? [action] :
            path == "/api/auth/forget-local" ? [action, "Origin", "X-Weft-Auth-Token"] :
            path == "/api/auth/login" ? [action, "X-Weft-Invitation", "X-Weft-Return-To", "Origin", "X-Weft-Auth-Token", "X-XSRF-TOKEN"] :
            [action, "Origin", "X-Weft-Auth-Token", "X-XSRF-TOKEN"];
        Assert.Equal(expected, parameters.Select(parameter => parameter.GetProperty("name").GetString()!));
        foreach (var parameter in parameters)
        {
            Assert.Equal("header", parameter.GetProperty("in").GetString());
            var name = parameter.GetProperty("name").GetString();
            var required = parameter.TryGetProperty("required", out var flag) && flag.GetBoolean();
            Assert.Equal(name is not ("X-XSRF-TOKEN" or "X-Weft-Invitation" or "X-Weft-Return-To"), required);
            var value = parameter.GetProperty("schema");
            if (name == action) Text(value, "1");
            else if (name == "X-Weft-Return-To") ReturnPath(value);
            else if (name == "Origin") Assert.Equal("string", value.GetProperty("type").GetString());
            else if (name == "X-Weft-Invitation")
            {
                Assert.Equal("string", value.GetProperty("type").GetString());
                Assert.Equal(64, value.GetProperty("minLength").GetInt32());
                Assert.Equal(64, value.GetProperty("maxLength").GetInt32());
                Assert.Equal("^[0-9a-f]{64}$", value.GetProperty("pattern").GetString());
            }
            else Token(value);
        }
    }

    private static void ReturnPath(JsonElement schema)
    {
        Assert.Equal("string", schema.GetProperty("type").GetString());
        Assert.Equal(1, schema.GetProperty("minLength").GetInt32());
        Assert.Equal(Weft.Identity.BrowserReturnPath.MaximumLength, schema.GetProperty("maxLength").GetInt32());
        var pattern = schema.GetProperty("pattern").GetString()!;
        Assert.Equal("^/[A-Za-z0-9\\-._~!$&'()*+,;=:@/?%#]*$", pattern);
        Assert.Matches(pattern, "/help/reports#coverage");
    }

    private static void Shape(JsonElement schema, string[] properties)
    {
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(properties.Order(StringComparer.Ordinal), schema.GetProperty("properties").EnumerateObject()
            .Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(properties.Order(StringComparer.Ordinal), schema.GetProperty("required").EnumerateArray()
            .Select(property => property.GetString()!).Order(StringComparer.Ordinal));
    }

    private static void Version(JsonElement schema)
    {
        Assert.Equal("integer", schema.GetProperty("type").GetString());
        Assert.Equal(1, Assert.Single(schema.GetProperty("enum").EnumerateArray()).GetInt32());
    }

    private static void Text(JsonElement schema, params string[] values)
    {
        Assert.Equal("string", schema.GetProperty("type").GetString());
        Assert.Equal(values, schema.GetProperty("enum").EnumerateArray().Select(value => value.GetString()!));
    }

    private static void Token(JsonElement schema)
    {
        Assert.Equal("string", schema.GetProperty("type").GetString());
        Assert.Equal(1, schema.GetProperty("minLength").GetInt32());
        Assert.Equal(4096, schema.GetProperty("maxLength").GetInt32());
        Assert.Equal("^[A-Za-z0-9_-]+$", schema.GetProperty("pattern").GetString());
    }

    private static void Fingerprint(JsonElement schema)
    {
        Assert.Equal("string", schema.GetProperty("type").GetString());
        Assert.Equal(46, schema.GetProperty("minLength").GetInt32());
        Assert.Equal(46, schema.GetProperty("maxLength").GetInt32());
        Assert.Equal("^v1\\.[A-Za-z0-9_-]{43}$", schema.GetProperty("pattern").GetString());
    }

    private static void Null(JsonElement schema)
    {
        Assert.Equal(new[] { "type" }, schema.EnumerateObject().Select(property => property.Name));
        Assert.Equal("null", schema.GetProperty("type").GetString());
    }

    private static void NoEffects(HttpResponseMessage response)
    {
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Null(response.Headers.Location);
    }

    private static async Task<JsonDocument> ReadAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        NoEffects(response);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<WebApplication> StartAsync(AuthenticationHttpMode? mode = null, bool apiFirst = true,
        bool catalog = true, bool mapAuthentication = false)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        if (apiFirst) builder.Services.AddWeftApi();
        if (catalog)
        {
            var options = mode == AuthenticationHttpMode.Development
                ? new CatalogApiOptions(true, true, Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    Guid.Parse("22222222-2222-2222-2222-222222222222"), Origin)
                : new CatalogApiOptions();
            builder.Services.AddWeftCatalog(options, builder.Environment);
        }
        if (mode is { } explicitMode)
            builder.Services.AddWeftAuthenticationHttp(new(explicitMode, Origin), builder.Environment);
        if (!apiFirst) builder.Services.AddWeftApi();
        Type[] sensitive = [typeof(EntraBrowserAuthentication), typeof(BrowserAuthenticationOptions), typeof(EntraBrowserClient),
            typeof(ServerSessionBrowser), typeof(DurableSessionKeyProfile), typeof(IAuthenticationSchemeProvider),
            typeof(IAuthenticationService), typeof(IAuthenticationHandlerProvider)];
        Assert.DoesNotContain(builder.Services, descriptor => sensitive.Contains(descriptor.ServiceType));
        if (mode is null) Assert.DoesNotContain(builder.Services, descriptor => descriptor.ServiceType == typeof(AuthenticationHttpOptions));
        var app = builder.Build();
        try
        {
            if (catalog) app.MapWeftCatalog();
            if (mapAuthentication) app.MapWeftAuthentication();
            app.MapOpenApi();
            await app.StartAsync();
            var defaults = app.Services.GetRequiredService<IOptions<AuthenticationOptions>>().Value;
            Assert.Empty(defaults.Schemes);
            Assert.Null(defaults.DefaultScheme);
            Assert.Null(defaults.DefaultAuthenticateScheme);
            Assert.Null(defaults.DefaultChallengeScheme);
            Assert.Null(defaults.DefaultForbidScheme);
            Assert.Null(defaults.DefaultSignInScheme);
            Assert.Null(defaults.DefaultSignOutScheme);
            string[] expected = mapAuthentication ? ["/api/auth/session"] : [];
            Assert.Equal(expected, ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>().Select(endpoint => endpoint.RoutePattern.RawText!)
                .Where(path => path.StartsWith("/api/auth/", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
            return app;
        }
        catch (Exception primary)
        {
            try { await app.DisposeAsync(); }
            catch (Exception cleanup) { throw new AggregateException("OpenAPI host setup and original disposal failed.", primary, cleanup); }
            throw;
        }
    }
}
