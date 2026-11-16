using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

public sealed class PostgresSchemaPinSourceTests
{
    [Fact]
    public void Document_disclosure_requires_the_exact_predecessor_validator_body()
    {
        var predecessor = MigrationRunner.EmbeddedMigrations.Single(m => m.Version == 39).Sql;
        var bodies = Regex.Matches(predecessor, @"AS \$chat_metadata_valid\$(.*?)\$chat_metadata_valid\$;", RegexOptions.Singleline);
        var body = Assert.Single(bodies.Cast<Match>()).Groups[1].Value;
        var migration = MigrationRunner.EmbeddedMigrations.Single(m => m.Version == 46).Sql;
        var pins = Regex.Matches(migration, @"IF encode\(sha256\(convert_to\(body,'UTF8'\)\),'hex'\)<>'([0-9a-f]{64})'");
        var pin = Assert.Single(pins.Cast<Match>()).Groups[1].Value;
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body))), pin);
        Assert.Contains("Document disclosure requires the exact prior validator.", migration);
    }

    [Fact]
    public void Revision_derivation_bodies_match_their_pinned_source_checksums()
    {
        foreach (var name in RevisionDerivationSchemaContract.NewTables)
        {
            var table = RevisionDerivationSchemaContract.Tables[name];
            Assert.Equal(RevisionDerivationSchemaContract.BodyChecksums["guard_" + name + "_transition"],
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(table.FunctionBody!))));
        }
    }
}
