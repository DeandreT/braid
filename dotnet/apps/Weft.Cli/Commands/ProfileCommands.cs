using System.CommandLine;
using Weft.Cli.Authentication;
using Weft.Cli.Configuration;
using Weft.Cli.Http;
using Weft.Contracts.Catalog;
using static Weft.Cli.Commands.CommandSupport;

namespace Weft.Cli.Commands;

internal static class ProfileCommands
{
    public static Command Profile(CliGlobals globals)
    {
        var name = Argument("name", "The profile name: lowercase letters, digits and hyphens");
        var server = Required("--server", "The Weft server origin, for example https://weft.example.com");
        var tenant = Required("--tenant", "The Microsoft Entra tenant ID");
        var client = Required("--client-id", "The application (client) ID of the Weft CLI registration");
        var scope = Required("--scope", "The delegated Weft API scope, for example api://<application ID>/weft.api");
        var makeDefault = new Option<bool>("--default") { Description = "Also make this the default profile" };
        var set = new Command("set", "Create or replace a profile").With(name, server, tenant, client, scope, makeDefault);
        set.Handle(globals, invocation =>
        {
            var profile = new CliProfile(invocation.Value(name)!, CliProfile.ParseServer(invocation.Value(server)!),
                CliProfile.ParseId(invocation.Value(tenant)!, "tenant"), CliProfile.ParseId(invocation.Value(client)!, "client ID"),
                invocation.Value(scope)!);
            var configuration = invocation.Configuration;
            var profiles = new SortedDictionary<string, CliProfile>(configuration.Profiles.ToDictionary(), StringComparer.Ordinal)
            {
                [profile.Name] = profile
            };
            var selected = invocation.Value(makeDefault) || configuration.DefaultProfile is null ? profile.Name : configuration.DefaultProfile;
            invocation.Store.Save(new CliConfiguration(selected, profiles));
            invocation.Environment.Error.WriteLine($"Saved profile {profile.Name}{(selected == profile.Name ? " (default)" : "")}.");
            return Task.CompletedTask;
        });

        var list = new Command("list", "List profiles");
        list.Handle(globals, invocation =>
        {
            var configuration = invocation.Configuration;
            var profiles = configuration.Profiles.Values.Select(profile => View(profile, configuration.DefaultProfile)).ToArray();
            invocation.Output.Write(profiles, output => output.Table(["DEFAULT", "NAME", "SERVER", "TENANT", "CLIENT ID"],
                profiles.Select(profile => (IReadOnlyList<object?>)[profile.Default ? "*" : "", profile.Name, profile.Server,
                    profile.TenantId, profile.ClientId]), "No profiles. Create one with: weft profile set"));
            return Task.CompletedTask;
        });

        var showName = new Argument<string?>("name") { Description = "The profile (default: the selected profile)", Arity = ArgumentArity.ZeroOrOne };
        var show = new Command("show", "Show one profile's settings").With(showName);
        show.Handle(globals, invocation =>
        {
            var configuration = invocation.Configuration;
            var profile = invocation.Value(showName) is { } requested
                ? CliConfigurationStore.Select(configuration, requested)
                : invocation.Profile;
            var view = View(profile, configuration.DefaultProfile);
            invocation.Output.Write(view, output => output.Fields(("Name", view.Name), ("Default", view.Default),
                ("Server", view.Server), ("Tenant", view.TenantId), ("Client ID", view.ClientId), ("Scope", view.Scope),
                ("Authority", view.Authority)));
            return Task.CompletedTask;
        });

        var useName = Argument("name", "The profile to make the default");
        var use = new Command("use", "Make a profile the default").With(useName);
        use.Handle(globals, invocation =>
        {
            var configuration = invocation.Configuration;
            var profile = CliConfigurationStore.Select(configuration, invocation.Value(useName));
            invocation.Store.Save(configuration with { DefaultProfile = profile.Name });
            invocation.Environment.Error.WriteLine($"The default profile is now {profile.Name}.");
            return Task.CompletedTask;
        });

        var removeName = Argument("name", "The profile to remove");
        var remove = new Command("remove", "Remove a profile and sign it out").With(removeName);
        remove.Handle(globals, async invocation =>
        {
            var configuration = invocation.Configuration;
            var profile = CliConfigurationStore.Select(configuration, invocation.Value(removeName));
            try
            {
                await new CliSignIn(profile, invocation.Environment).LogoutAsync(invocation.Cancellation);
            }
            catch (CliException error) when (error.Code == CliExitCode.ProtectedStoreUnavailable)
            {
                // Nothing can have been cached without a protected store.
            }
            var profiles = new SortedDictionary<string, CliProfile>(configuration.Profiles.ToDictionary(), StringComparer.Ordinal);
            profiles.Remove(profile.Name);
            invocation.Store.Save(new CliConfiguration(configuration.DefaultProfile == profile.Name ? null : configuration.DefaultProfile, profiles));
            invocation.Environment.Error.WriteLine($"Removed profile {profile.Name}.");
        });

        return new Command("profile", "Manage named server profiles").With(set, list, show, use, remove);
    }

    public static IEnumerable<Command> SignIn(CliGlobals globals)
    {
        var browser = new Option<bool>("--browser")
        {
            Description = "Sign in in the system browser (authorization code with PKCE on a loopback redirect) instead of with a device code"
        };
        var login = new Command("login", "Sign in to the selected profile as yourself").With(browser);
        login.Handle(globals, async invocation =>
        {
            var account = invocation.Value(browser)
                ? await invocation.SignIn.LoginWithBrowserAsync(invocation.Cancellation)
                : await invocation.SignIn.LoginWithDeviceCodeAsync(invocation.Cancellation);
            invocation.Output.Write(account, output => output.Line($"Signed in to {invocation.Profile.Name} as {CliOutputText(account.Username)}."));
        });
        yield return login;

        var logout = new Command("logout", "Sign out of the selected profile and remove its cached tokens");
        logout.Handle(globals, async invocation =>
        {
            var removed = await invocation.SignIn.LogoutAsync(invocation.Cancellation);
            invocation.Environment.Error.WriteLine(removed == 0
                ? $"No account was signed in to {invocation.Profile.Name}."
                : $"Signed out of {invocation.Profile.Name}.");
        });
        yield return logout;

        var whoami = new Command("whoami", "Show the signed-in account and the identity the server admits");
        whoami.Handle(globals, async invocation =>
        {
            var account = await invocation.SignIn.AccountAsync(invocation.Cancellation) ?? throw CliSignIn.SignInRequired();
            var context = await invocation.Api.ReadAsync<CatalogContextV1>(new ApiRequest(ApiOperations.GetCatalogContext), invocation.Cancellation);
            var view = new WhoAmIView(invocation.Profile.Name, invocation.Profile.Server.GetLeftPart(UriPartial.Authority), account.Username,
                account.TenantId, context);
            invocation.Output.Write(view, output => output.Fields(("Profile", view.Profile), ("Server", view.Server),
                ("Account", view.Account), ("Tenant", view.TenantId), ("Principal", context.ActorPrincipalId),
                ("Organization", $"{context.OrganizationName} ({context.AllowedOrganizationId})"), ("Mode", context.Mode),
                ("Features", Features(context.Features))));
        });
        yield return whoami;

        var contextCommand = new Command("context", "Show the catalog context: organization, features and actions");
        contextCommand.Handle(globals, async invocation =>
        {
            var context = await invocation.Api.ReadAsync<CatalogContextV1>(new ApiRequest(ApiOperations.GetCatalogContext), invocation.Cancellation);
            invocation.Output.Write(context, output => output.Fields(("Mode", context.Mode), ("Principal", context.ActorPrincipalId),
                ("Organization ID", context.AllowedOrganizationId), ("Organization", context.OrganizationName),
                ("Features", Features(context.Features)), ("May create projects", context.OrganizationActions.CreateProjects)));
        });
        yield return contextCommand;
    }

    internal static string Features(CatalogFeaturesV1 features)
    {
        var names = new List<string>();
        void Add(bool enabled, string name) { if (enabled) names.Add(name); }
        Add(features.ReadCatalog, "readCatalog");
        Add(features.CreateProjects, "createProjects");
        Add(features.CreateMappings, "createMappings");
        Add(features.EditMetadata, "editMetadata");
        Add(features.UploadRevisions, "uploadRevisions");
        Add(features.ReadReports, "readReports");
        Add(features.DeriveReports, "deriveReports");
        Add(features.Preview, "preview");
        Add(features.Chat, "chat");
        Add(features.ManageMembership, "manageMembership");
        return names.Count == 0 ? "none" : string.Join(", ", names);
    }

    private static string CliOutputText(string text) => Output.CliOutput.Safe(text.Length == 0 ? "(unnamed account)" : text);

    private static ProfileView View(CliProfile profile, string? selected) => new(profile.Name, profile.Name == selected,
        profile.Server.GetLeftPart(UriPartial.Authority), profile.TenantId.ToString("D"), profile.ClientId.ToString("D"), profile.Scope,
        profile.Authority.AbsoluteUri);

    internal sealed record ProfileView(string Name, bool Default, string Server, string TenantId, string ClientId, string Scope, string Authority);

    internal sealed record WhoAmIView(string Profile, string Server, string Account, string TenantId, CatalogContextV1 Context);
}
