using Weft.Persistence.Postgres;

namespace Weft.Tests;

public sealed class ChatDatabaseConfigurationTests
{
    [Fact]
    public void UnsafePoolingAndUndefinedProfilesRefuseWithoutOpeningAConnection()
    {
        const string route = "Host=unopened.invalid;Database=weft;Username=weft_chat_reader;Password=synthetic-unused";
        foreach (var option in new[] { ";No Reset On Close=true", ";Multiplexing=true", ";Enlist=true" })
            Assert.Throws<ArgumentException>(() => new PostgresChatDatabase(route + option, ChatDatabaseProfile.Reader));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PostgresChatDatabase(route, (ChatDatabaseProfile)100));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PostgresChatDatabase(route, ChatDatabaseProfile.Lifecycle));
        foreach (var option in new[] { ";No Reset On Close=true", ";Multiplexing=true", ";Enlist=true" })
            Assert.Throws<ArgumentException>(() => new PostgresChatLifecycleDatabase(route + option));
    }

    [Fact]
    public void ExactPinnedSourceHasSeparateReadOnlyAndWriterColumnMatricesWithoutSessionOrPayloadRights()
    {
        var scripts = MigrationRunner.EmbeddedMigrations;
        Assert.True(scripts.Take(10).Select(s => s.Checksum).SequenceEqual(ChatSchemaContract.PrefixChecksums, StringComparer.Ordinal));
        Assert.Equal(ChatSchemaContract.MigrationChecksum, scripts[10].Checksum);
        var reader = ChatSchemaContract.ColumnGrants(ChatDatabaseProfile.Reader);
        var writer = ChatSchemaContract.ColumnGrants(ChatDatabaseProfile.Writer);
        Assert.All(reader, grant => Assert.EndsWith("|SELECT", grant, StringComparison.Ordinal));
        Assert.True(reader.All(g => writer.Contains(g, StringComparer.Ordinal)));
        foreach (var grants in new[] { reader, writer })
        {
            Assert.DoesNotContain(grants, g => g.StartsWith("server_sessions|", StringComparison.Ordinal) ||
                g.StartsWith("external_identity_bindings|", StringComparison.Ordinal) || g.StartsWith("project_payload_read_grants|", StringComparison.Ordinal) ||
                g.StartsWith("mapping_revisions|", StringComparison.Ordinal));
            Assert.Contains("schema_migrations|version|SELECT", grants);
            Assert.Contains("schema_migrations|checksum|SELECT", grants);
            Assert.DoesNotContain("schema_migrations|applied_at|SELECT", grants);
        }
        Assert.DoesNotContain("chat_events|private_text|UPDATE", writer);
        Assert.DoesNotContain("chat_turns|original_request|UPDATE", writer);
        Assert.Contains("chat_turns|version|UPDATE", writer);
        Assert.Contains("chat_conversations|retain_until|UPDATE", writer); // Row-lock privilege only; immutable trigger guards edits.
        Assert.True(ChatSchemaContract.ColumnGrants(ChatDatabaseProfile.Lifecycle).Order(StringComparer.Ordinal).SequenceEqual(
            new[] { "schema_migrations|checksum|SELECT", "schema_migrations|version|SELECT" }, StringComparer.Ordinal));
        Assert.DoesNotContain(writer, g => g.StartsWith("chat_identities|", StringComparison.Ordinal) && g.EndsWith("|INSERT", StringComparison.Ordinal));
        Assert.Equal(new[] { "chat_conversations", "chat_events", "chat_identities", "chat_payload_audit", "chat_rounds", "chat_turns" }, ChatSchemaContract.ChatTables.Order(StringComparer.Ordinal));
        // The workspace migration adds the kind and focus columns; the payload audit is written only by its owner-defined trigger.
        Assert.Equal(ChatSchemaContract.WorkspaceMigrationChecksum, scripts[ChatSchemaContract.WorkspaceMigrationVersion - 1].Checksum);
        foreach (var column in new[] { "conversation_kind", "focus_project_id", "focus_mapping_id", "focus_revision_id" })
        {
            Assert.Contains($"chat_conversations|{column}|SELECT", reader);
            Assert.Contains($"chat_conversations|{column}|INSERT", writer);
        }
        Assert.DoesNotContain(reader.Concat(writer), g => g.StartsWith("chat_payload_audit|", StringComparison.Ordinal));
    }
}
