using Weft.Persistence.Postgres;

namespace Weft.Tests;

/// <summary>
/// The chat mapping-write supplement (0039, 0040, 0043) is admitted like the automation supplement:
/// an older prefix has none of its objects and is not validated, while any one of its objects is a
/// partial installation that refuses admission until the exact complete supplement is present.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ChatMappingWriteAdmissionTests(PostgresFixture fixture)
{
    [PostgresTheory]
    [InlineData("table")]
    [InlineData("function")]
    [InlineData("commitTrigger")]
    [InlineData("eventTrigger")]
    public async Task Pre_chat_write_prefix_is_admitted_but_any_partial_supplement_is_refused(string partial)
    {
        await using var test = await fixture.CreateCaseAsync(migrate: false);
        await using (var prefix = new MigrationRunner(test.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(36).ToArray()))
            await prefix.MigrateAsync();
        Assert.False(await InstalledAsync(test));
        await ValidateAsync(test);
        var (mutation, repair) = partial switch
        {
            "table" => ("CREATE TABLE weft.chat_edit_proposals(id integer)", "DROP TABLE weft.chat_edit_proposals"),
            "function" => ("CREATE FUNCTION weft.chat_edit_read() RETURNS integer LANGUAGE sql AS 'SELECT 1'; REVOKE ALL ON FUNCTION weft.chat_edit_read() FROM PUBLIC",
                "DROP FUNCTION weft.chat_edit_read()"),
            "commitTrigger" => ("""
                CREATE FUNCTION weft.partial_chat_probe() RETURNS trigger LANGUAGE plpgsql AS 'BEGIN RETURN NULL; END';
                REVOKE ALL ON FUNCTION weft.partial_chat_probe() FROM PUBLIC;
                CREATE TRIGGER chat_mapping_write_commit AFTER UPDATE ON weft.revision_ingestions FOR EACH ROW EXECUTE FUNCTION weft.partial_chat_probe();
                """, "DROP TRIGGER chat_mapping_write_commit ON weft.revision_ingestions; DROP FUNCTION weft.partial_chat_probe()"),
            _ => ("""
                CREATE FUNCTION weft.partial_chat_probe() RETURNS trigger LANGUAGE plpgsql AS 'BEGIN RETURN NULL; END';
                REVOKE ALL ON FUNCTION weft.partial_chat_probe() FROM PUBLIC;
                CREATE TRIGGER chat_mapping_write_audit AFTER INSERT ON weft.chat_events FOR EACH ROW EXECUTE FUNCTION weft.partial_chat_probe();
                """, "DROP TRIGGER chat_mapping_write_audit ON weft.chat_events; DROP FUNCTION weft.partial_chat_probe()")
        };
        await test.ExecuteAsync(mutation);
        Assert.True(await InstalledAsync(test));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ValidateAsync(test));
        await test.ExecuteAsync(repair);
        Assert.False(await InstalledAsync(test));
        await ValidateAsync(test);
    }

    [PostgresFact]
    public async Task Current_schema_requires_the_exact_commit_trigger()
    {
        await using var test = await fixture.CreateCaseAsync();
        Assert.True(await InstalledAsync(test));
        await ValidateAsync(test);
        var definition = await test.ScalarAsync<string>(
            "SELECT pg_get_triggerdef(oid) FROM pg_trigger WHERE tgrelid='weft.revision_ingestions'::regclass AND tgname='chat_mapping_write_commit'");
        await test.ExecuteAsync("DROP TRIGGER chat_mapping_write_commit ON weft.revision_ingestions");
        // The remaining supplement objects keep it installed; the missing trigger is refused, never treated as an old schema.
        Assert.True(await InstalledAsync(test));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ValidateAsync(test));
        await test.ExecuteAsync(definition);
        await ValidateAsync(test);
    }

    private static async Task<bool> InstalledAsync(PostgresCase test)
    {
        await using var connection = await test.Owner.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        return await ChatSchemaContract.MappingWritesInstalledAsync(connection, transaction, default);
    }

    private static async Task ValidateAsync(PostgresCase test)
    {
        await using var connection = await test.Owner.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SupplementalMaintenanceSchemaAdmission.ValidateAsync(connection, transaction, default);
    }
}
