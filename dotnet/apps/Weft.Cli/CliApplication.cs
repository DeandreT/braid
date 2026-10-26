using System.CommandLine;
using Weft.Cli.Commands;
using Weft.Cli.Output;

namespace Weft.Cli;

/// <summary>The <c>weft</c> command tree and its process exit codes.</summary>
internal static class CliApplication
{
    public static RootCommand Build(CliEnvironment environment)
    {
        var globals = new CliGlobals(environment);
        var root = new RootCommand("Weft command-line client: works with the Weft API as the signed-in user.");
        root.Options.Add(globals.Profile);
        root.Options.Add(globals.Json);
        root.Options.Add(globals.Organization);
        root.Subcommands.Add(ProfileCommands.Profile(globals));
        foreach (var command in ProfileCommands.SignIn(globals))
            root.Subcommands.Add(command);
        root.Subcommands.Add(CatalogCommands.Projects(globals));
        root.Subcommands.Add(CatalogCommands.Mappings(globals));
        root.Subcommands.Add(CatalogCommands.Revisions(globals));
        root.Subcommands.Add(CatalogCommands.Ingestions(globals));
        root.Subcommands.Add(MfdCommands.Create(globals));
        root.Subcommands.Add(ReportCommands.Reports(globals));
        root.Subcommands.Add(ReportCommands.Compare(globals));
        root.Subcommands.Add(PreviewCommands.Previews(globals));
        root.Subcommands.Add(RegressionCommands.Regression(globals));
        root.Subcommands.Add(BuildCommands.Builds(globals));
        root.Subcommands.Add(OperationsCommands.Operations(globals));
        root.Subcommands.Add(ChatCommands.Chat(globals));
        root.Subcommands.Add(GuideCommands.Guide(globals));
        root.Subcommands.Add(SearchCommands.Search(globals));
        root.Subcommands.Add(AdministrationCommands.Administration(globals));
        root.Subcommands.Add(HealthCommands.Health(globals));
        root.Subcommands.Add(AutomationCommands.Automation(globals));
        root.Subcommands.Add(AutomationCommands.Notifications(globals));
        return root;
    }

    public static async Task<int> RunAsync(string[] args, CliEnvironment environment, CancellationToken cancellationToken = default)
    {
        var parse = Build(environment).Parse(args);
        if (parse.Errors.Count > 0)
        {
            foreach (var error in parse.Errors)
                environment.Error.WriteLine("error: " + CliRedaction.Text(error.Message));
            environment.Error.WriteLine("Run weft --help for usage.");
            return (int)CliExitCode.Usage;
        }
        return await parse.InvokeAsync(new InvocationConfiguration
        {
            Output = environment.Out,
            Error = environment.Error,
            EnableDefaultExceptionHandler = false,
            // Time for an interrupted command to finish its own cleanup, such as stopping a chat turn on the server.
            ProcessTerminationTimeout = TimeSpan.FromSeconds(20)
        }, cancellationToken);
    }
}
