using Npgsql;
using Weft.DatabaseTool;
using Weft.Domain;
using Weft.Persistence.Postgres;

if (ExternalIdentityOwnerCommands.IsCommand(args))
    return await ExternalIdentityOwnerCommands.RunAsync(args);

if (ProjectPayloadOwnerCommands.IsCommand(args))
    return await ProjectPayloadOwnerCommands.RunAsync(args);

var existingCommand = args.Length == 1 && args[0] is "migrate" or "provision-development-identity";
var capabilityCommand = args.Length == 4 && args[0] == "set-project-creation-capability" && args[3] is "true" or "false";
if (!existingCommand && !capabilityCommand)
{
    Console.Error.WriteLine("Usage: Weft.DatabaseTool migrate | provision-development-identity | set-project-creation-capability ORG PRINCIPAL true|false");
    return 2;
}

var connectionString = Environment.GetEnvironmentVariable("WEFT_MIGRATION_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("Set WEFT_MIGRATION_CONNECTION_STRING for the dedicated migration login.");
    return 2;
}

try
{
    if (args[0] == "migrate")
    {
        await using var migrations = new MigrationRunner(connectionString);
        await migrations.MigrateAsync();
        Console.WriteLine("The Weft database migrations are current.");
    }
    else if (capabilityCommand)
    {
        var organization = RequiredArgumentId(args[1]);
        var principal = new PrincipalId(RequiredArgumentId(args[2]));
        await using var provisioning = new OrganizationProjectCreationCapabilityProvisioner(connectionString);
        await provisioning.SetAsync(organization, principal, args[3] == "true");
        Console.WriteLine(args[3] == "true" ? "Project creation capability granted." : "Project creation capability revoked.");
    }
    else
    {
        var organization = RequiredId("WEFT_DEV_ORGANIZATION_ID");
        var principal = RequiredId("WEFT_DEV_PRINCIPAL_ID");
        var organizationName = Environment.GetEnvironmentVariable("WEFT_DEV_ORGANIZATION_NAME");
        if (string.IsNullOrWhiteSpace(organizationName))
            throw new ArgumentException("Set the explicit local organization name.");
        ProjectScope? project = null;
        ProjectRole? role = null;
        if (Environment.GetEnvironmentVariable("WEFT_DEV_PROJECT_ID") is not null ||
            Environment.GetEnvironmentVariable("WEFT_DEV_PROJECT_ROLE") is not null)
        {
            project = new ProjectScope(organization, RequiredId("WEFT_DEV_PROJECT_ID"));
            role = Environment.GetEnvironmentVariable("WEFT_DEV_PROJECT_ROLE") switch
            {
                "viewer" => ProjectRole.Viewer,
                "runner" => ProjectRole.Runner,
                "publisher" => ProjectRole.Publisher,
                "administrator" => ProjectRole.Administrator,
                _ => throw new ArgumentException("Set an explicit known local project role.")
            };
        }
        await using var provisioning = new LocalCatalogIdentityProvisioner(connectionString);
        await provisioning.ProvisionAsync(organization, organizationName, principal, project, role);
        Console.WriteLine("The explicitly configured local identity and requested admission are current.");
    }
    return 0;
}
catch (NpgsqlException)
{
    Console.Error.WriteLine("PostgreSQL could not complete the owner operation. Check the configured database and migration login.");
    return 1;
}
catch (InvalidOperationException error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
catch (ArgumentException)
{
    Console.Error.WriteLine(capabilityCommand
        ? "Project creation capability configuration requires canonical nonempty organization and principal IDs and an explicit true|false value."
        : "The owner-tool configuration is invalid; local provisioning requires explicit nonempty IDs, organization name and paired project/role options.");
    return 2;
}

static Guid RequiredId(string variable)
{
    var value = Environment.GetEnvironmentVariable(variable);
    if (!Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty || value != id.ToString("D"))
        throw new ArgumentException("Set the explicit canonical local identity identifiers.");
    return id;
}

static Guid RequiredArgumentId(string value)
{
    if (!Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty || value != id.ToString("D"))
        throw new ArgumentException("Owner capability identifiers must be canonical and nonempty.");
    return id;
}
