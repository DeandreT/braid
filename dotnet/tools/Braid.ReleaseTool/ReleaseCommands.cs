using System.Globalization;
using System.Text.Json;
using Npgsql;
using Braid.Application;
using Braid.Application.Regression;
using Braid.Deployment;
using Braid.Domain;
using Braid.MachineEndpoint;
using Braid.MappingBackends;
using Braid.MappingBackends.Workers;
using Braid.Persistence.Postgres;

namespace Braid.ReleaseTool;

/// <summary>
/// Release operator commands for a mapping endpoint's target. Each command acts as one named principal whose current
/// authority the database checks, through restricted logins only. Qualification executes the build's compiled artifact
/// in the isolated compiled runtime; no command provisions, deploys or changes anything outside the release records.
/// </summary>
internal static class ReleaseCommands
{
    internal const string RuntimeImageSetting = "BRAID_COMPILED_RUNTIME_IMAGE";
    internal const string CompilerImageSetting = "BRAID_FERRULE_WORKER_IMAGE";
    /// <summary>How long a canceled service execution may take to settle; the command has no caller deadline.</summary>
    private static readonly TimeSpan Settlement = TimeSpan.FromSeconds(10);
    internal const string ReleaseWriter = "BRAID_RELEASE_WRITER_CONNECTION_STRING";
    internal const string QualificationReader = "BRAID_QUALIFICATION_READER_CONNECTION_STRING";

    /// <summary>The logins qualification uses: the evidence writer and the readers the compiled execution path admits with.</summary>
    internal static readonly (string Setting, string Role)[] QualificationLogins =
    [
        ("BRAID_QUALIFICATION_WRITER_CONNECTION_STRING", "braid_qualification_writer"),
        ("BRAID_QUALIFICATION_BUILD_READER_CONNECTION_STRING", "braid_build_reader"),
        ("BRAID_QUALIFICATION_BUILD_RUN_READER_CONNECTION_STRING", "braid_build_run_reader"),
        ("BRAID_QUALIFICATION_ACCESS_CONNECTION_STRING", "braid_runtime"),
        ("BRAID_QUALIFICATION_REPORT_READER_CONNECTION_STRING", "braid_report_reader"),
        ("BRAID_QUALIFICATION_RUN_READER_CONNECTION_STRING", "braid_run_reader"),
        ("BRAID_QUALIFICATION_CASE_READER_CONNECTION_STRING", "braid_case_reader")
    ];

    private const string Usage = """
        Usage: Braid.ReleaseTool qualify-build ACTOR ORGANIZATION PROJECT MAPPING BUILD QUALIFICATION
               Braid.ReleaseTool register-release-target ACTOR ORGANIZATION PROJECT MAPPING ENVIRONMENT VERSION TARGET
               Braid.ReleaseTool activate-release ACTOR ORGANIZATION PROJECT MAPPING ENVIRONMENT VERSION TARGET RELEASE QUALIFICATION EXPECTED|none
               Braid.ReleaseTool rollback-release ACTOR ORGANIZATION PROJECT MAPPING ENVIRONMENT VERSION TARGET RELEASE SOURCE EXPECTED
               Braid.ReleaseTool disable-release-target ACTOR ORGANIZATION PROJECT MAPPING ENVIRONMENT VERSION TARGET
        """;

    internal static async Task<int> RunAsync(string[] arguments, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(arguments); ArgumentNullException.ThrowIfNull(environment);
        Command command;
        try { command = Parse(arguments); }
        catch (ArgumentException)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, signal) =>
        {
            signal.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancel;
        try
        {
            var (outcome, values) = command.Name switch
            {
                "qualify-build" => await QualifyAsync(command, environment, cancellation.Token),
                "register-release-target" => await RegisterAsync(command, environment, cancellation.Token),
                "activate-release" or "rollback-release" => await ActivateAsync(command, environment, cancellation.Token),
                _ => await DisableAsync(command, environment, cancellation.Token)
            };
            // The acknowledged operation settles before any outcome is printed.
            var line = new Dictionary<string, string?> { ["outcome"] = outcome };
            foreach (var (key, value) in values) line[key] = value;
            Console.Out.WriteLine(JsonSerializer.Serialize(line));
            return outcome switch
            {
                "qualified" or "registered" or "activated" or "disabled" or "replayed" => 0,
                "unavailable" => 1,
                _ => 3
            };
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return Error("canceled", 130); }
        catch (RuntimeCleanupException) { return Error("runtimeCleanupUnconfirmed", 1); }
        catch (ArgumentException) { return Error("configurationInvalid", 2); }
        catch (Exception error) when (error is NpgsqlException or InvalidOperationException or OperationCanceledException or TimeoutException)
        { return Error("persistenceFailure", 1); }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        {
            // A runtime that cannot start or confirm cleanup; diagnostics never carry request or credential values.
            return Error("failure", 1);
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    private static async Task<(string, (string, string?)[])> QualifyAsync(Command command, Func<string, string?> environment, CancellationToken token)
    {
        // Exactly the endpoint's runtime selection: the same runner kind, execution identity and compiler identity.
        MappingRuntimeSelection selection;
        try { selection = MappingRuntimeSelection.Read(environment, Image(environment(RuntimeImageSetting)), Image(environment(CompilerImageSetting))); }
        catch (InvalidOperationException error) { throw new ArgumentException("The compiled runtime selection is invalid.", error); }
        var logins = QualificationLogins.ToDictionary(login => login.Role, login => Login(environment, login.Setting, login.Role), StringComparer.Ordinal);
        await using var qualifications = new PostgresBuildQualificationStore(logins["braid_qualification_writer"], writer: true);
        await using var builds = new PostgresBuildStore(logins["braid_build_reader"], writer: false);
        await using var runs = new PostgresBuildRunStore(logins["braid_build_run_reader"], BuildRunDatabaseProfile.Reader);
        await using var access = new PostgresDatabase(logins["braid_runtime"]);
        await using var reports = new PostgresRevisionReportDatabase(logins["braid_report_reader"]);
        await using var regressionRuns = new PostgresRegressionRunStore(logins["braid_run_reader"], writer: false);
        await using var cases = new PostgresRegressionCaseStore(logins["braid_case_reader"], RegressionCaseDatabaseProfile.Reader);
        MappingRuntimeSelection.Owned runner;
        try { runner = selection.CreateRunner(Settlement); }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        { throw new ArgumentException("The compiled runtime client configuration is invalid.", error); }
        BuildQualificationRequestResult result;
        try
        {
            var runtime = new FerruleCompiledBuildRuntime(runner.Runner,
                new("ferrule", FerruleInspectionBackend.EngineRevision, selection.CompilerIdentity, FerrulePreviewBackend.AdapterBuild));
            var projects = new PostgresProjectReader(access);
            var evidence = new RegressionRunReadService(regressionRuns, cases);
            var execution = new CompiledBuildExecution(runs, new BuildControlService(builds, projects, new PostgresRevisionReportMetadataReader(reports), evidence),
                builds, new PostgresBuildObjectStore(builds, NoNativeObjects.Instance, command.Actor), runtime);
            var service = new BuildQualificationService(qualifications, runs, regressionRuns, cases, execution, projects);
            result = await service.PutAsync(command.Actor, command.Mapping, command.Values[1], command.Values[0], token);
        }
        finally
        {
            // The runner joins its compiled-runtime work before the stores close. A stored qualification stays
            // stored; repeating the command with the same IDs replays it without running again.
            try { await runner.DisposeAsync(); }
            catch (InvalidOperationException error) { throw new RuntimeCleanupException(error); }
        }
        var outcome = result.Outcome switch
        {
            BuildQualificationRequestOutcome.Created => "qualified",
            BuildQualificationRequestOutcome.Replayed => "replayed",
            BuildQualificationRequestOutcome.Conflict => "conflict",
            BuildQualificationRequestOutcome.NotQualified => "notQualified",
            BuildQualificationRequestOutcome.Unavailable => "unavailable",
            _ => "refused"
        };
        return result.Qualification is { } qualification
            ? (outcome, [("qualificationId", qualification.Id.ToString("D")), ("buildId", qualification.Artifact.Intent.BuildId.ToString("D")),
                ("artifactSha256", qualification.Artifact.ContentDigest.Value), ("runtimeImage", qualification.RuntimeImage)])
            : (outcome, []);
    }

    private static async Task<(string, (string, string?)[])> RegisterAsync(Command command, Func<string, string?> environment, CancellationToken token)
    {
        var target = new LocalReleaseTarget(command.Target!, FerruleBuildCompiler.Profile, Image(environment(RuntimeImageSetting)));
        await using var releases = new PostgresLocalReleaseStore(Login(environment, ReleaseWriter, "braid_release_writer"), writer: true);
        var outcome = await releases.RegisterAsync(command.Actor, target, token);
        return (outcome switch
        {
            ReleaseWriteOutcome.Created => "registered",
            ReleaseWriteOutcome.Replayed => "replayed",
            ReleaseWriteOutcome.Conflict => "conflict",
            _ => "refused"
        }, [("targetId", target.Reference.TargetId.ToString("D")), ("runtimeImage", target.RuntimeImage)]);
    }

    private static async Task<(string, (string, string?)[])> ActivateAsync(Command command, Func<string, string?> environment, CancellationToken token)
    {
        var target = command.Target!;
        var releaseId = command.Values[0];
        await using var releases = new PostgresLocalReleaseStore(Login(environment, ReleaseWriter, "braid_release_writer"), writer: true);
        await using var qualifications = new PostgresBuildQualificationStore(Login(environment, QualificationReader, "braid_qualification_reader"), writer: false);
        LocalReleaseRequest request;
        if (command.Name == "activate-release") request = new(releaseId, command.Values[1], command.Expected);
        else
        {
            // A rollback retains its source release's qualification; it never recompiles or requalifies.
            var source = await releases.ReadAsync(command.Actor, target, command.Values[1], token);
            if (source is null) return ("refused", [("releaseId", releaseId.ToString("D"))]);
            request = new(releaseId, source.Intent.Qualification.Id, command.Expected, ReleaseAction.Rollback, command.Values[1]);
        }
        var service = new LocalReleaseService(releases, qualifications);
        var requested = await service.RequestAsync(command.Actor, target, request, token);
        if (requested.Outcome is not (ReleaseWriteOutcome.Created or ReleaseWriteOutcome.Replayed))
            return (requested.Outcome == ReleaseWriteOutcome.Conflict ? "conflict" : "refused", [("releaseId", releaseId.ToString("D"))]);
        var applied = await service.ApplyAsync(command.Actor, target, releaseId, token);
        return (applied switch
        {
            ReleaseTransitionOutcome.Activated => "activated",
            ReleaseTransitionOutcome.Replayed => "replayed",
            ReleaseTransitionOutcome.Canceled => "canceled",
            ReleaseTransitionOutcome.Conflict => "conflict",
            _ => "refused"
        }, [("releaseId", releaseId.ToString("D")), ("qualificationId", request.QualificationId.ToString("D"))]);
    }

    private static async Task<(string, (string, string?)[])> DisableAsync(Command command, Func<string, string?> environment, CancellationToken token)
    {
        await using var releases = new PostgresLocalReleaseStore(Login(environment, ReleaseWriter, "braid_release_writer"), writer: true);
        return (await releases.DisableAsync(command.Actor, command.Target!, token) ? "disabled" : "refused",
            [("targetId", command.Target!.TargetId.ToString("D"))]);
    }

    /// <summary>One parsed command: its acting principal, mapping, optional target and trailing identifiers.</summary>
    internal sealed record Command(string Name, ActorContext Actor, MappingScope Mapping, ReleaseTargetReference? Target, Guid[] Values, Guid? Expected);

    internal static Command Parse(string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        // COMMAND ACTOR ORGANIZATION PROJECT MAPPING, then the build and qualification, or the target and its values.
        var shape = arguments.Length > 0 ? arguments[0] switch
        {
            "qualify-build" => (Target: false, Values: 2),
            "register-release-target" or "disable-release-target" => (Target: true, Values: 0),
            "activate-release" or "rollback-release" => (Target: true, Values: 3),
            _ => (Target: false, Values: -1)
        } : (Target: false, Values: -1);
        if (shape.Values < 0 || arguments.Length != 5 + (shape.Target ? 3 : 0) + shape.Values) throw new ArgumentException("Invalid release command arguments.");
        var actor = new ActorContext(new PrincipalId(Identifier(arguments[1])));
        var mapping = new MappingScope(Identifier(arguments[2]), Identifier(arguments[3]), Identifier(arguments[4]));
        if (!shape.Target) return new(arguments[0], actor, mapping, null, [Identifier(arguments[5]), Identifier(arguments[6])], null);
        if (!long.TryParse(arguments[6], NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version < 1 ||
            arguments[6] != version.ToString(CultureInfo.InvariantCulture))
            throw new ArgumentException("Invalid environment version.");
        var target = new ReleaseTargetReference(mapping, new(new(mapping.OrganizationId, mapping.ProjectId), Identifier(arguments[5]), version),
            Identifier(arguments[7]));
        if (shape.Values == 0) return new(arguments[0], actor, mapping, target, [], null);
        Guid? expected = arguments[10] == "none" && arguments[0] == "activate-release" ? null : Identifier(arguments[10]);
        var values = new[] { Identifier(arguments[8]), Identifier(arguments[9]) };
        if (arguments[0] == "rollback-release" && (values[1] == values[0] || values[1] == expected)) throw new ArgumentException("Invalid rollback source.");
        return new(arguments[0], actor, mapping, target, values, expected);
    }

    /// <summary>Exactly the expected restricted login with an explicit password; never another role's credential.</summary>
    internal static string Login(Func<string, string?> environment, string setting, string role)
    {
        var value = environment(setting);
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A required login is missing.");
        NpgsqlConnectionStringBuilder connection;
        try { connection = new(value); }
        catch (Exception error) when (error is ArgumentException or FormatException or KeyNotFoundException)
        { throw new ArgumentException("A login is invalid.", error); }
        if (connection.Username != role || string.IsNullOrEmpty(connection.Password) || connection.Passfile is not null)
            throw new ArgumentException("A login does not name its restricted role.");
        return value;
    }

    private static string Image(string? value) =>
        value is { Length: 71 } && value.StartsWith("sha256:", StringComparison.Ordinal) && value[7..].All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'))
            ? value : throw new ArgumentException("An immutable image identity is required.");

    private static Guid Identifier(string value) =>
        Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty && value == id.ToString("D") ? id : throw new ArgumentException("Invalid identifier.");

    private static int Error(string category, int exitCode)
    {
        Console.Error.WriteLine("{\"error\":\"" + category + "\"}");
        return exitCode;
    }

    /// <summary>The compiled runtime could not confirm removal of a container it started.</summary>
    private sealed class RuntimeCleanupException(Exception inner) : Exception("Compiled runtime cleanup was not confirmed.", inner);

    /// <summary>Qualification reads only the compiled artifact; native mapping bytes are never read here.</summary>
    private sealed class NoNativeObjects : IPrivateObjectStore
    {
        internal static readonly NoNativeObjects Instance = new();
        public Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(RevisionScope scope, PrivateObjectReference reference, Stream bytes,
            CancellationToken cancellationToken = default) => throw new PrivateObjectStorageException(PrivateObjectStorageError.ScopeMismatch);
        public Task<byte[]> ReadVerifiedAsync(RevisionScope scope, PrivateObjectReference reference, CancellationToken cancellationToken = default) =>
            throw new PrivateObjectStorageException(PrivateObjectStorageError.ScopeMismatch);
    }
}
