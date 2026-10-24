using Weft.Server;

namespace Weft.Tests;

/// <summary>Bearer access is explicit and complete, or absent; every partial or mixed profile refuses before any delivery opens.</summary>
public sealed class BearerHostConfigurationTests
{
    private const string Client = "39ceba91-5e4d-4ead-9be2-ddbf843be1c6";
    private const string Cli = "8f3c1d2a-4b5e-4f60-9a7b-1c2d3e4f5a6b";
    private const string SecondCli = "2b4d6f80-1a3c-4e5f-8a9b-0c1d2e3f4a5b";

    [Fact]
    public void Bearer_access_is_off_unless_explicitly_enabled()
    {
        Assert.Null(ServerHostOptions.Read(Session().GetValueOrDefault).Bearer);
        var values = Session(); values["WEFT_API_BEARER_ENABLED"] = "false";
        Assert.Null(ServerHostOptions.Read(values.GetValueOrDefault).Bearer);
    }

    [Fact]
    public void A_complete_profile_admits_the_scope_audience_and_clients_without_reading_the_audit_delivery()
    {
        var values = Enabled();
        values["WEFT_API_BEARER_CLIENT_IDS"] = Cli + "," + SecondCli;
        var reads = new List<string>();
        var bearer = ServerHostOptions.Read(key => { reads.Add(key); return values.GetValueOrDefault(key); }).Bearer;
        Assert.NotNull(bearer);
        Assert.Equal(Guid.Parse("e6492421-de0b-4626-87a1-773d7f77e0aa"), bearer.TenantId);
        Assert.Equal(Guid.Parse(Client), bearer.Audience);
        Assert.Equal("weft.api", bearer.Scope);
        Assert.Equal([Guid.Parse(Cli), Guid.Parse(SecondCli)], bearer.ClientIds);
        // The scalar stage only checks that the protected audit delivery is named; it never opens it.
        Assert.Contains("WEFT_API_AUDIT_WRITER_CONNECTION_STRING_FILE", reads);
    }

    [Theory]
    [InlineData("WEFT_API_BEARER_ENABLED", "True")]
    [InlineData("WEFT_API_BEARER_ENABLED", "1")]
    [InlineData("WEFT_API_BEARER_AUDIENCE", null)]
    [InlineData("WEFT_API_BEARER_AUDIENCE", "b2617125-6fe6-4447-a993-c10c1c3e8f35")]
    [InlineData("WEFT_API_BEARER_AUDIENCE", "39CEBA91-5E4D-4EAD-9BE2-DDBF843BE1C6")]
    [InlineData("WEFT_API_BEARER_AUDIENCE", "api://39ceba91-5e4d-4ead-9be2-ddbf843be1c6")]
    [InlineData("WEFT_API_BEARER_SCOPE", null)]
    [InlineData("WEFT_API_BEARER_SCOPE", "")]
    [InlineData("WEFT_API_BEARER_SCOPE", "weft api")]
    [InlineData("WEFT_API_BEARER_SCOPE", "api://39ceba91-5e4d-4ead-9be2-ddbf843be1c6/weft.api")]
    [InlineData("WEFT_API_BEARER_CLIENT_IDS", null)]
    [InlineData("WEFT_API_BEARER_CLIENT_IDS", "")]
    [InlineData("WEFT_API_BEARER_CLIENT_IDS", Cli + ",")]
    [InlineData("WEFT_API_BEARER_CLIENT_IDS", Cli + ", " + SecondCli)]
    [InlineData("WEFT_API_BEARER_CLIENT_IDS", Cli + "," + Cli)]
    [InlineData("WEFT_API_BEARER_CLIENT_IDS", Client)]
    [InlineData("WEFT_API_BEARER_CLIENT_IDS", "00000000-0000-0000-0000-000000000000")]
    [InlineData("WEFT_API_BEARER_CLIENT_IDS", "8F3C1D2A-4B5E-4F60-9A7B-1C2D3E4F5A6B")]
    [InlineData("WEFT_API_AUDIT_WRITER_CONNECTION_STRING_FILE", null)]
    [InlineData("WEFT_API_AUDIT_WRITER_CONNECTION_STRING", "Host=postgres;Username=weft_api_audit_writer;Password=plain")]
    public void Every_partial_or_malformed_enabled_profile_refuses(string name, string? value)
    {
        var values = Enabled();
        if (value is null) values.Remove(name); else values[name] = value;
        Assert.Throws<InvalidOperationException>(() => ServerHostOptions.Read(values.GetValueOrDefault));
    }

    [Fact]
    public void Too_many_allowed_clients_refuse()
    {
        var values = Enabled();
        values["WEFT_API_BEARER_CLIENT_IDS"] = string.Join(',', Enumerable.Range(0, 17).Select(_ => Guid.NewGuid().ToString("D")));
        Assert.Throws<InvalidOperationException>(() => ServerHostOptions.Read(values.GetValueOrDefault));
    }

    [Theory]
    [InlineData("WEFT_API_BEARER_AUDIENCE", Client)]
    [InlineData("WEFT_API_BEARER_SCOPE", "weft.api")]
    [InlineData("WEFT_API_BEARER_CLIENT_IDS", Cli)]
    [InlineData("WEFT_API_AUDIT_WRITER_CONNECTION_STRING_FILE", "/run/weft/secrets/api-audit-writer")]
    [InlineData("WEFT_API_AUDIT_WRITER_CONNECTION_STRING", "Host=postgres")]
    public void A_dormant_bearer_setting_without_enablement_refuses(string name, string value)
    {
        var values = Session(); values[name] = value;
        Assert.Throws<InvalidOperationException>(() => ServerHostOptions.Read(values.GetValueOrDefault));
        values["WEFT_API_BEARER_ENABLED"] = "false";
        Assert.Throws<InvalidOperationException>(() => ServerHostOptions.Read(values.GetValueOrDefault));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("development")]
    public void Bearer_settings_refuse_outside_the_session_profile(string? mode)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["WEFT_DEV_LISTEN_ADDR"] = "127.0.0.1:0",
            ["WEFT_DEV_BROWSER_ORIGIN"] = "http://127.0.0.1:4200"
        };
        if (mode is not null) { values["WEFT_CATALOG_ENABLED"] = "true"; values["WEFT_CATALOG_AUTH_MODE"] = mode; }
        _ = ServerHostOptions.Read(values.GetValueOrDefault);
        values["WEFT_API_BEARER_ENABLED"] = "false";
        _ = ServerHostOptions.Read(values.GetValueOrDefault);
        foreach (var (name, value) in Enabled().Where(pair => pair.Key.StartsWith("WEFT_API_", StringComparison.Ordinal)))
        {
            var copy = new Dictionary<string, string>(values) { [name] = value };
            if (name == "WEFT_API_BEARER_ENABLED") copy[name] = "true";
            Assert.Throws<InvalidOperationException>(() => ServerHostOptions.Read(copy.GetValueOrDefault));
        }
    }

    private static Dictionary<string, string> Enabled()
    {
        var values = Session();
        values["WEFT_API_BEARER_ENABLED"] = "true";
        values["WEFT_API_BEARER_AUDIENCE"] = Client;
        values["WEFT_API_BEARER_SCOPE"] = "weft.api";
        values["WEFT_API_BEARER_CLIENT_IDS"] = Cli;
        values["WEFT_API_AUDIT_WRITER_CONNECTION_STRING_FILE"] = "/run/weft/secrets/api-audit-writer";
        return values;
    }

    private static Dictionary<string, string> Session() => new(StringComparer.Ordinal)
    {
        ["WEFT_CATALOG_ENABLED"] = "true",
        ["WEFT_CATALOG_AUTH_MODE"] = "session",
        ["WEFT_PUBLIC_ORIGIN"] = "https://127.0.0.1:58413",
        ["WEFT_LISTEN_ADDR"] = "127.0.0.1:58413",
        ["WEFT_HOST_TRANSPORT"] = "directHttps",
        ["WEFT_SESSION_ORGANIZATION_ID"] = "97b79dcc-d078-4994-9a06-f75147842811",
        ["WEFT_ENTRA_TENANT_ID"] = "e6492421-de0b-4626-87a1-773d7f77e0aa",
        ["WEFT_ENTRA_CLIENT_ID"] = Client,
        ["WEFT_SESSION_APPLICATION_NAME"] = "Bearer profile admission",
        ["WEFT_SESSION_KEY_DIRECTORY"] = Path.Combine(Path.GetTempPath(), "weft-unopened-test-key-ring")
    };
}
