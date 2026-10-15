using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;
using Weft.Storage.FileSystem;

namespace Weft.MaintenanceTool;

internal static class Program
{
    private const string Usage = "Usage: Weft.MaintenanceTool run-once ORGANIZATION_ID PROJECT_ID ROOT APPLICATION_ROOT [--repository-root REPOSITORY_ROOT] [--cleanup-after JSON] [--status-after JSON]";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--help")
        {
            Console.Out.WriteLine(Usage);
            return 0;
        }
        Command command;
        try { command = Parse(args); }
        catch (UsageException)
        {
            Console.Error.WriteLine("Usage");
            Console.Error.WriteLine(Usage);
            return 2;
        }

        var connectionString = Environment.GetEnvironmentVariable("WEFT_MAINTENANCE_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.Error.WriteLine("InvalidConfiguration");
            return 2;
        }

        using var canceled = new CancellationTokenSource();
        ConsoleCancelEventHandler stop = (_, signal) => { signal.Cancel = true; canceled.Cancel(); };
        Console.CancelKeyPress += stop;
        var configuring = true;
        try
        {
            PrivateObjectMaintenanceRunResult result;
            // Constructors parse trusted configuration. The pool is lazy; only
            // the dedicated admitted maintenance methods ever open a connection.
            await using (var database = new PostgresPrivateObjectMaintenanceDatabase(connectionString))
            {
                var storageOptions = new FileSystemStorageOptions(command.Root, command.ApplicationRoot, command.RepositoryRoot);
                await using var storage = FileSystemV2PrivateObjectStore.Open(storageOptions);
                var metadata = new PostgresPrivateObjectMaintenanceStore(database);
                var service = new PrivateObjectMaintenanceService(metadata, metadata, metadata,
                    storage, storage, Guid.NewGuid());
                configuring = false;
                result = await service.RunOnceAsync(command.Project, command.CleanupAfter,
                    command.StatusAfter, canceled.Token).ConfigureAwait(false);
            }

            // Emit only the closed public result after the original operation and
            // both owned resources have settled. No options/exception is serialized.
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                MaxDepth = 16
            };
            options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
            var bytes = JsonSerializer.SerializeToUtf8Bytes(result, options);
            if (bytes.Length > 32768) throw new InvalidOperationException("The bounded maintenance result is invalid.");
            Console.Out.WriteLine(StrictUtf8.GetString(bytes));
            return ExitCode(result);
        }
        catch (ArgumentException) when (configuring)
        {
            Console.Error.WriteLine("InvalidConfiguration");
            return 2;
        }
        catch (PrivateObjectStorageException error) when (configuring && error.Error is
            (PrivateObjectStorageError.Missing or PrivateObjectStorageError.UnsafeStorage or PrivateObjectStorageError.Unsupported))
        {
            Console.Error.WriteLine("InvalidConfiguration");
            return 2;
        }
        catch (PrivateObjectStorageException) when (configuring)
        {
            Console.Error.WriteLine("StorageUnavailable");
            return 1;
        }
        catch (Exception)
        {
            Console.Error.WriteLine("MaintenanceFailure");
            return 1;
        }
        finally { Console.CancelKeyPress -= stop; }
    }

    private static int ExitCode(PrivateObjectMaintenanceRunResult result)
    {
        if (result.Outcome != PrivateObjectMaintenanceRunOutcome.Finished) return 1;
        foreach (var target in result.Targets)
            if (target.Acknowledgement is not (PrivateObjectMaintenanceAcknowledgement.Applied or
                PrivateObjectMaintenanceAcknowledgement.AlreadyApplied or PrivateObjectMaintenanceAcknowledgement.Recovered))
                return 3;
        return 0;
    }

    private static Command Parse(string[] args)
    {
        if (args.Length is < 5 or > 11 || args[0] != "run-once" || (args.Length - 5) % 2 != 0)
            throw new UsageException();
        try
        {
            var project = new ProjectScope(Id(args[1]), Id(args[2]));
            string? repository = null;
            ObjectCleanupScanCursor? cleanup = null;
            ObjectCleanupStatusCursor? status = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 5; index < args.Length; index += 2)
            {
                if (!seen.Add(args[index])) throw new UsageException();
                switch (args[index])
                {
                    case "--repository-root": repository = args[index + 1]; break;
                    case "--cleanup-after": cleanup = CleanupCursor(args[index + 1], project); break;
                    case "--status-after": status = StatusCursor(args[index + 1], project); break;
                    default: throw new UsageException();
                }
            }
            return new(project, args[3], args[4], repository, cleanup, status);
        }
        catch (ArgumentException) { throw new UsageException(); }
        catch (JsonException) { throw new UsageException(); }
    }

    private static ObjectCleanupScanCursor CleanupCursor(string text, ProjectScope project)
    {
        using var document = CursorDocument(text, 2048);
        var row = document.RootElement;
        RequireProperties(row, "project", "stream", "eligibleAtUtc", "target");
        var cursorProject = Project(row.GetProperty("project"));
        if (cursorProject != project) throw new UsageException();
        var stream = Text(row.GetProperty("stream")) switch
        {
            "dueRetirement" => ObjectCleanupScanStream.DueRetirement,
            "nativeStage" => ObjectCleanupScanStream.NativeStage,
            "derivedCandidate" => ObjectCleanupScanStream.DerivedCandidate,
            "trackedTemporary" => ObjectCleanupScanStream.TrackedTemporary,
            _ => throw new UsageException()
        };
        var time = row.GetProperty("eligibleAtUtc");
        if (time.ValueKind != JsonValueKind.String || !time.TryGetDateTimeOffset(out var eligible) ||
            eligible.Offset != TimeSpan.Zero) throw new UsageException();
        return new(cursorProject, stream, eligible, Target(row.GetProperty("target")));
    }

    private static ObjectCleanupStatusCursor StatusCursor(string text, ProjectScope project)
    {
        using var document = CursorDocument(text, 512);
        var row = document.RootElement;
        RequireProperties(row, "project", "afterRetirementId");
        var cursorProject = Project(row.GetProperty("project"));
        if (cursorProject != project) throw new UsageException();
        return new(cursorProject, Id(Text(row.GetProperty("afterRetirementId"))));
    }

    private static JsonDocument CursorDocument(string text, int byteLimit)
    {
        if (text.Length > byteLimit || StrictUtf8.GetByteCount(text) > byteLimit) throw new UsageException();
        return JsonDocument.Parse(StrictUtf8.GetBytes(text), new JsonDocumentOptions
        {
            MaxDepth = 8,
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow
        });
    }

    private static ObjectCleanupTarget Target(JsonElement row)
    {
        RequireProperties(row, "object", "kind", "publicationAttemptId", "scope");
        var source = row.GetProperty("object");
        RequireProperties(source, "scope", "objectId", "class");
        var scope = Scope(source.GetProperty("scope"));
        if (Scope(row.GetProperty("scope")) != scope) throw new UsageException();
        var objectClass = Text(source.GetProperty("class")) switch
        {
            "nativeArtifact" => PrivateObjectClass.NativeArtifact,
            "derivedArtifact" => PrivateObjectClass.DerivedArtifact,
            _ => throw new UsageException()
        };
        var kind = Text(row.GetProperty("kind")) switch
        {
            "finalObject" => CleanupTargetKind.FinalObject,
            "trackedTemporary" => CleanupTargetKind.TrackedTemporary,
            _ => throw new UsageException()
        };
        var publication = row.GetProperty("publicationAttemptId");
        Guid? publicationId = publication.ValueKind == JsonValueKind.Null ? null : Id(Text(publication));
        return new(PrivateObjectId.Restore(scope, Id(Text(source.GetProperty("objectId"))), objectClass), kind, publicationId);
    }

    private static ProjectScope Project(JsonElement row)
    {
        RequireProperties(row, "organizationId", "projectId");
        return new(Id(Text(row.GetProperty("organizationId"))), Id(Text(row.GetProperty("projectId"))));
    }

    private static RevisionScope Scope(JsonElement row)
    {
        RequireProperties(row, "organizationId", "projectId", "mappingId", "revisionId");
        return new(Id(Text(row.GetProperty("organizationId"))), Id(Text(row.GetProperty("projectId"))),
            Id(Text(row.GetProperty("mappingId"))), Id(Text(row.GetProperty("revisionId"))));
    }

    private static string Text(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? value.GetString()! : throw new UsageException();

    private static Guid Id(string value)
    {
        if (!Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty || value != id.ToString("D"))
            throw new UsageException();
        return id;
    }

    private static void RequireProperties(JsonElement row, params string[] names)
    {
        if (row.ValueKind != JsonValueKind.Object) throw new UsageException();
        var remaining = new HashSet<string>(names, StringComparer.Ordinal);
        foreach (var property in row.EnumerateObject())
            if (!remaining.Remove(property.Name)) throw new UsageException();
        if (remaining.Count != 0) throw new UsageException();
    }

    private sealed record Command(ProjectScope Project, string Root, string ApplicationRoot, string? RepositoryRoot,
        ObjectCleanupScanCursor? CleanupAfter, ObjectCleanupStatusCursor? StatusAfter);

    private sealed class UsageException : Exception { }
}
