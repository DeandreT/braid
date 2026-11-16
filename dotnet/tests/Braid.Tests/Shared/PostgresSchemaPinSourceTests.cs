using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

public sealed class PostgresSchemaPinSourceTests
{
    [Fact]
    public void Specification_temporary_discriminator_matches_the_domain_encoding()
    {
        var evidence = new SpecTemporaryEvidence(Guid.NewGuid(), Guid.NewGuid(), 7, 8, 1000,
            new(1, 2, 3), new(1, 2, 4), new(1, 2, 5), new(1, 2, 6), null);
        var bytes = evidence.Encode();
        var migration = MigrationRunner.EmbeddedMigrations.Single(m => m.Version == 44).Sql;
        var body = Assert.Single(Regex.Matches(migration,
            @"CREATE FUNCTION braid\.spec_temp_advance\(.*?AS \$body\$(.*?)\$body\$;",
            RegexOptions.Singleline).Cast<Match>()).Groups[1].Value;
        var discriminator = Assert.Single(Regex.Matches(body,
            @"substring\(p_evidence from 1 for 4\)<>convert_to\('([^']+)','UTF8'\)").Cast<Match>()).Groups[1].Value;
        Assert.Equal("BST1", discriminator);
        Assert.Equal(bytes[..4], Encoding.UTF8.GetBytes(discriminator));
        bytes[0] = (byte)'W';
        Assert.Throws<ArgumentException>(() => SpecTemporaryEvidence.Decode(bytes));
    }

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
