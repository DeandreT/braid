using Npgsql;

namespace Braid.Tests;

internal static class SchemaRemovalTestSupport
{
    internal static async Task RemoveAsync(PostgresCase test, string[] tables, string[] functions, string[] triggers)
    {
        var statements = await test.ScalarAsync<string[]>("""
            SELECT array_agg(format('DROP FUNCTION %s CASCADE',p.oid::regprocedure) ORDER BY p.proname)
            FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='braid' AND p.proname=ANY($1)
            """, (object)functions);
        foreach (var statement in statements) await test.ExecuteAsync(statement);
        using var quotes = new NpgsqlCommandBuilder();
        foreach (var table in tables) await test.ExecuteAsync("DROP TABLE braid." + quotes.QuoteIdentifier(table) + " CASCADE");
        Assert.True(await test.ScalarAsync<bool>("""
            SELECT NOT EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='braid' AND c.relname=ANY($1))
                AND NOT EXISTS(SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='braid' AND p.proname=ANY($2))
                AND NOT EXISTS(SELECT 1 FROM pg_trigger t JOIN pg_class c ON c.oid=t.tgrelid JOIN pg_namespace n ON n.oid=c.relnamespace
                    WHERE n.nspname='braid' AND t.tgname=ANY($3))
            """, tables, functions, triggers));
    }
}
