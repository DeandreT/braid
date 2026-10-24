using System.CommandLine;
using System.Globalization;
using Weft.Cli.Authentication;
using Weft.Cli.Configuration;
using Weft.Cli.Http;
using Weft.Cli.Output;
using Weft.Contracts.Catalog;

namespace Weft.Cli.Commands;

/// <summary>The options every command shares, created once per command tree.</summary>
internal sealed class CliGlobals(CliEnvironment environment)
{
    public CliEnvironment Environment { get; } = environment;

    public Option<string?> Profile { get; } = new("--profile")
    {
        Description = "The profile to use (default: WEFT_PROFILE, then the default profile)",
        Recursive = true
    };

    public Option<bool> Json { get; } = new("--json") { Description = "Write the exact response document as JSON", Recursive = true };

    public Option<string?> Organization { get; } = new("--organization")
    {
        Description = "The organization ID (default: the organization the server admits you to)",
        Recursive = true
    };
}

/// <summary>One command run: its parsed values, the selected profile and, on demand, sign-in and the API client.</summary>
internal sealed class CliInvocation(CliGlobals globals, ParseResult parse, CancellationToken cancellationToken) : IDisposable
{
    private CliConfiguration? _configuration;
    private CliProfile? _profile;
    private CliSignIn? _signIn;
    private WeftApiClient? _api;
    private string? _organization;
    private CancellationToken _cancellation = cancellationToken;

    public CliEnvironment Environment => globals.Environment;
    public ParseResult Parse => parse;
    public CancellationToken Cancellation => _cancellation;

    internal IDisposable WithCancellation(CancellationToken token)
    {
        var previous = _cancellation;
        _cancellation = token;
        return new CancellationScope(this, previous);
    }

    private sealed class CancellationScope(CliInvocation invocation, CancellationToken previous) : IDisposable
    {
        public void Dispose() => invocation._cancellation = previous;
    }
    public CliOutput Output { get; } = new(globals.Environment.Out, parse.GetValue(globals.Json));
    public CliConfigurationStore Store { get; } = new(globals.Environment.ConfigurationDirectory);
    public CliConfiguration Configuration => _configuration ??= Store.Load();
    public CliProfile Profile => _profile ??= CliConfigurationStore.Select(Configuration,
        parse.GetValue(globals.Profile) ?? Environment.ProfileVariable);
    public CliSignIn SignIn => _signIn ??= new CliSignIn(Profile, Environment);
    public WeftApiClient Api => _api ??= new WeftApiClient(Profile.Server, SignIn, Environment.ApiHandler, Environment.Time, Environment.Error);

    public T? Value<T>(Option<T> option) => parse.GetValue(option);
    public T? Value<T>(Argument<T> argument) => parse.GetValue(argument);

    /// <summary>A canonical lowercase identifier from an option or argument value.</summary>
    public static string Id(string? text, string name)
    {
        if (text is null || !Guid.TryParseExact(text, "D", out var id) || id == Guid.Empty)
            throw CliException.Usage($"{name} must be a nonzero UUID such as 00000000-0000-0000-0000-000000000000.");
        return id.ToString("D");
    }

    public string Id(Option<string> option) => Id(parse.GetValue(option), option.Name);
    public string Id(Argument<string> argument) => Id(parse.GetValue(argument), argument.Name);
    public string? OptionalId(Option<string?> option) => parse.GetValue(option) is { } text ? Id(text, option.Name) : null;

    public static string Positive(long value, string name) =>
        value >= 1 ? value.ToString(CultureInfo.InvariantCulture) : throw CliException.Usage($"{name} must be a positive integer.");

    /// <summary>The explicit organization, or the one the server admits the signed-in user to.</summary>
    public async Task<string> OrganizationAsync()
    {
        if (_organization is not null)
            return _organization;
        if (parse.GetValue(globals.Organization) is { } explicitId)
            return _organization = Id(explicitId, "--organization");
        var context = await Api.ReadAsync<CatalogContextV1>(new ApiRequest(ApiOperations.GetCatalogContext), Cancellation);
        return _organization = context.AllowedOrganizationId;
    }

    /// <summary>A request with the organization path value already applied.</summary>
    public async Task<ApiRequest> RequestAsync(ApiOperation operation)
    {
        var request = new ApiRequest(operation);
        return request.Path("organizationId", await OrganizationAsync());
    }

    public void Dispose() => _api?.Dispose();
}
