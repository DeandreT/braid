using Npgsql;
using Weft.Persistence.Postgres;

if (args.Length != 1 || args[0] != "migrate")
{
    Console.Error.WriteLine("Usage: Weft.DatabaseTool migrate");
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
    await using var migrations = new MigrationRunner(connectionString);
    await migrations.MigrateAsync();
    Console.WriteLine("The Weft database migrations are current.");
    return 0;
}
catch (NpgsqlException)
{
    Console.Error.WriteLine("PostgreSQL could not complete the migration. Check the configured database and migration login.");
    return 1;
}
catch (InvalidOperationException error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
catch (ArgumentException)
{
    Console.Error.WriteLine("The migration configuration is invalid.");
    return 2;
}
