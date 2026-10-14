using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Weft.Api.Catalog;
using Weft.Application;
using Weft.Domain;
using Weft.MappingBackends;
using Weft.MappingBackends.Workers;
using Weft.Persistence.Postgres;
using Weft.Server;
using Weft.Storage.FileSystem;

namespace Weft.Tests;

/// <summary>Pure composition and synthetic owned-port tests; no database, filesystem opening or Docker execution.</summary>
public sealed class CatalogRevisionReportHostConfigurationTests
{
    private const string ReadFlag = "WEFT_REVISION_REPORTS_ENABLED";
    private const string DeriveFlag = "WEFT_REVISION_REPORT_DERIVATION_ENABLED";
    private const string ReaderCredential = "WEFT_POSTGRES_REPORT_CONNECTION_STRING";
    private const string WriterCredential = "WEFT_POSTGRES_DERIVATION_CONNECTION_STRING";
    private const string Image = "sha256:3333333333333333333333333333333333333333333333333333333333333333";
    private const string Secret = "synthetic-never-connected-report-secret";
    private const string Connection = "Host=127.0.0.1;Database=weft;Username=weft_report_reader;Password=" + Secret;
    private static readonly string[] ReportDependencies = [ReaderCredential, WriterCredential,
        "WEFT_PRIVATE_STORAGE_ROOT", "WEFT_DERIVED_READ_BYTE_LIMIT", "WEFT_FERRULE_WORKER_IMAGE"];

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public void DisabledReportFeatureReadsNoReportDependenciesAndRegistersNoPorts(string? flag)
    {
        var configuration = new Dictionary<string, string>();
        if (flag is not null) configuration[ReadFlag] = flag;
        var read = new List<string>();
        var builder = Builder();
        CatalogHostConfiguration.Configure(builder, Listener(), variable =>
        {
            read.Add(variable);
            if (ReportDependencies.Contains(variable)) throw new InvalidOperationException("Unused report dependency was read.");
            return configuration.GetValueOrDefault(variable);
        });
        var options = Options(builder.Services);
        Assert.False(options.Enabled);
        Assert.False(options.DerivationEnabled);
        Assert.DoesNotContain(read, ReportDependencies.Contains);
        foreach (var type in new[] { typeof(RevisionReportService), typeof(IRevisionReportReader), typeof(IRevisionReportDeriver),
            typeof(PostgresRevisionReportDatabase), typeof(PostgresRevisionDerivationDatabase), typeof(IPrivateObjectStore) })
            Assert.DoesNotContain(builder.Services, descriptor => descriptor.ServiceType == type);
    }

    [Theory]
    [InlineData(ReadFlag, "TRUE")]
    [InlineData(ReadFlag, "")]
    [InlineData(ReadFlag, "1")]
    [InlineData(DeriveFlag, "False")]
    [InlineData(DeriveFlag, " true")]
    [InlineData(DeriveFlag, "false ")]
    public void ExplicitReportFlagsAreStrictAndNeverReadDependenciesOnFailure(string variable, string value)
    {
        var configuration = CatalogConfiguration();
        configuration[variable] = value;
        var read = new List<string>();
        var error = Assert.Throws<ArgumentException>(() => Configure(Builder(), configuration, read));
        Assert.DoesNotContain(read, ReportDependencies.Contains);
        Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DerivationRequiresReadsAndReadsRequireCatalogBeforeDependencyResolution()
    {
        var read = new List<string>();
        Assert.Throws<ArgumentException>(() => Configure(Builder(),
            new Dictionary<string, string> { [DeriveFlag] = "true" }, read));
        Assert.DoesNotContain(read, ReportDependencies.Contains);
        read.Clear();
        Assert.Throws<InvalidOperationException>(() => Configure(Builder(),
            new Dictionary<string, string> { [ReadFlag] = "true" }, read));
        Assert.DoesNotContain(read, ReportDependencies.Contains);
    }

    [Fact]
    public async Task EnablingReportsDoesNotReadDependenciesOrPreventUnrelatedCatalogReaderResolution()
    {
        var configuration = ReportConfiguration(derive: true);
        foreach (var dependency in ReportDependencies) configuration[dependency] = "malformed-unused-" + Secret;
        var read = new List<string>();
        var builder = Builder();
        Configure(builder, configuration, read);
        Assert.DoesNotContain(read, ReportDependencies.Contains);
        await using var provider = builder.Services.BuildServiceProvider();
        Assert.IsType<PostgresCatalogReader>(provider.GetRequiredService<ICatalogReader>());
        Assert.DoesNotContain(read, ReportDependencies.Contains);
    }

    [Fact]
    public async Task ReaderAndDeriverAliasOneSingletonWithoutWriterRootOrWorkerResolution()
    {
        var configuration = ReportConfiguration(derive: true);
        var read = new List<string>();
        var builder = Builder();
        Configure(builder, configuration, read);
        var runnerResolutions = 0;
        builder.Services.AddSingleton<IInspectionWorkerRunner>(_ =>
        {
            runnerResolutions++;
            throw new InvalidOperationException("A reader must not resolve a worker.");
        });
        await using var provider = builder.Services.BuildServiceProvider();
        var reader = provider.GetRequiredService<IRevisionReportReader>();
        var deriver = provider.GetRequiredService<IRevisionReportDeriver>();
        Assert.IsType<RevisionReportService>(reader);
        Assert.Same(reader, deriver);
        Assert.Same(reader, provider.GetRequiredService<RevisionReportService>());
        Assert.Same(reader, provider.GetRequiredService<IRevisionReportReader>());
        Assert.Equal(0, runnerResolutions);
        Assert.DoesNotContain(WriterCredential, read);
        Assert.DoesNotContain("WEFT_PRIVATE_STORAGE_ROOT", read);
        Assert.IsType<LazyRevisionReportMetadataStore>(provider.GetRequiredService<IRevisionReportMetadataStore>());
        foreach (var type in new[] { typeof(RevisionReportService), typeof(IRevisionReportReader), typeof(IRevisionReportDeriver) })
            Assert.Equal(ServiceLifetime.Singleton, Assert.Single(builder.Services, item => item.ServiceType == type).Lifetime);
    }

    [Fact]
    public async Task ReadOnlyReportsRegisterNoWriterPoolAndRetainPureHistoricalFormatsWithoutAnImage()
    {
        var builder = Builder();
        var read = new List<string>();
        Configure(builder, ReportConfiguration(), read);
        Assert.DoesNotContain(builder.Services, item => item.ServiceType == typeof(PostgresRevisionDerivationDatabase) ||
            item.ServiceType == typeof(PostgresRevisionDerivationStore));
        await using var provider = builder.Services.BuildServiceProvider();
        Assert.IsType<UnavailableRevisionReportMetadataStore>(provider.GetRequiredService<IRevisionReportMetadataStore>());
        var registry = provider.GetRequiredService<IRevisionReportProfileRegistry>();
        Assert.Null(registry.FindCurrent("ferrule", RevisionReportOperation.Projection));
        Assert.NotNull(registry.FindStored(ProjectionFormat()));
        Assert.NotNull(registry.FindStored(ValidationFormat()));
        Assert.DoesNotContain(WriterCredential, read);
        Assert.DoesNotContain("WEFT_PRIVATE_STORAGE_ROOT", read);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ferrule:latest")]
    [InlineData("sha256:BAD")]
    public void MissingOrInvalidCurrentImageKeepsHistoricalValidatorsWithoutResolvingARunner(string? image)
    {
        var calls = 0;
        var registry = CatalogRevisionReportHostConfiguration.CreateRegistry(image, new(), () =>
        {
            calls++;
            throw new InvalidOperationException("Pure selection must not resolve a runner.");
        });
        Assert.Null(registry.FindCurrent("ferrule", RevisionReportOperation.Projection));
        Assert.Null(registry.FindCurrent("ferrule", RevisionReportOperation.Validation));
        Assert.IsType<FerruleRevisionReportFormatValidator>(registry.FindStored(ProjectionFormat()));
        Assert.IsType<FerruleRevisionReportFormatValidator>(registry.FindStored(ValidationFormat()));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void ValidCurrentProfileAndReducedPolicyRemainPureEvenWhenItsBackendWrapperIsResolved()
    {
        var calls = 0;
        var policy = new RevisionReportPolicy(storedObjectByteLimit: 512, responseItemLimit: 256);
        var registry = CatalogRevisionReportHostConfiguration.CreateRegistry(Image, policy, () =>
        {
            calls++;
            throw new InvalidOperationException("Only actual derivation may resolve the shared runner.");
        });
        foreach (var operation in new[] { RevisionReportOperation.Projection, RevisionReportOperation.Validation })
        {
            var registration = Assert.IsType<RevisionReportRegistration>(registry.FindCurrent("ferrule", operation));
            Assert.Equal(Image, registration.Profile.BackendBuild);
            Assert.Equal(policy, registration.Policy);
            Assert.NotNull(registration.GetBackend());
        }
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("01")]
    [InlineData("+1")]
    [InlineData("16777217")]
    [InlineData("1 ")]
    [InlineData("")]
    public async Task InvalidReportReadCapFailsOnlyLazyReportResolutionAndLeavesCatalogReadable(string value)
    {
        var configuration = ReportConfiguration();
        configuration["WEFT_DERIVED_READ_BYTE_LIMIT"] = value;
        var read = new List<string>();
        var builder = Builder();
        Configure(builder, configuration, read);
        Assert.DoesNotContain("WEFT_DERIVED_READ_BYTE_LIMIT", read);
        await using var provider = builder.Services.BuildServiceProvider();
        Assert.IsType<PostgresCatalogReader>(provider.GetRequiredService<ICatalogReader>());
        var error = Assert.Throws<RevisionReportServiceException>(() => provider.GetRequiredService<IRevisionReportReader>());
        Assert.Equal(RevisionReportFailureCategory.ResourceLimit, error.Failure);
        Assert.Null(error.InnerException);
        Assert.IsType<PostgresCatalogReader>(provider.GetRequiredService<ICatalogReader>());
    }

    [Fact]
    public async Task DerivedReadReductionUsesFixedPhysicalCapsAndTrustedActivePolicy()
    {
        var configuration = ReportConfiguration();
        configuration["WEFT_DERIVED_READ_BYTE_LIMIT"] = "512";
        configuration["WEFT_FERRULE_WORKER_IMAGE"] = Image;
        var builder = Builder();
        Configure(builder, configuration);
        await using var provider = builder.Services.BuildServiceProvider();
        var storage = provider.GetRequiredService<RevisionReportStorageRegistration>();
        Assert.Equal(4194304, storage.NativeByteLimit);
        Assert.Equal(16777216, storage.DerivedByteLimit);
        Assert.Equal(512, storage.DerivedReadByteLimit);
        var registration = Assert.IsType<RevisionReportRegistration>(provider.GetRequiredService<IRevisionReportProfileRegistry>()
            .FindCurrent("ferrule", RevisionReportOperation.Projection));
        Assert.Equal(512, registration.Policy.StoredObjectByteLimit);

        var direct = Builder();
        var caps = new RevisionReportPolicy(storedObjectByteLimit: 256, responseByteLimit: 1024, responseItemLimit: 128);
        CatalogRevisionReportHostConfiguration.Configure(direct, new(true, false, caps), variable => configuration.GetValueOrDefault(variable));
        await using var directProvider = direct.Services.BuildServiceProvider();
        var reduced = Assert.IsType<RevisionReportRegistration>(directProvider.GetRequiredService<IRevisionReportProfileRegistry>()
            .FindCurrent("ferrule", RevisionReportOperation.Projection));
        Assert.Equal(256, reduced.Policy.StoredObjectByteLimit);
        Assert.Equal(1024, reduced.Policy.ResponseByteLimit);
        Assert.Equal(128, reduced.Policy.ResponseItemLimit);
    }

    [Fact]
    public async Task IngestionAndReportsShareOneLazyPhysicalStoreWithoutReportCapChangingIngestion()
    {
        var configuration = ReportConfiguration();
        configuration["WEFT_REVISION_INGESTION_ENABLED"] = "true";
        configuration["WEFT_POSTGRES_INGESTION_CONNECTION_STRING"] = Connection;
        configuration["WEFT_PRIVATE_STORAGE_ROOT"] = "/var/tmp/weft-host-test-existing-root";
        configuration["WEFT_NATIVE_BYTE_LIMIT"] = "1024";
        configuration["WEFT_DERIVED_READ_BYTE_LIMIT"] = "invalid-unused-report-cap";
        var builder = Builder();
        Configure(builder, configuration);
        Assert.Single(builder.Services, item => item.ServiceType == typeof(FileSystemStorageOptions));
        Assert.Single(builder.Services, item => item.ServiceType == typeof(LazyPrivateObjectStore));
        Assert.Single(builder.Services, item => item.ServiceType == typeof(IPrivateObjectStore));
        await using var provider = builder.Services.BuildServiceProvider();
        var physicalOptions = provider.GetRequiredService<FileSystemStorageOptions>();
        Assert.Equal(4194304L, physicalOptions.NativeByteLimit);
        Assert.Equal(16777216L, physicalOptions.DerivedByteLimit);
        Assert.Same(provider.GetRequiredService<IPrivateObjectStore>(), provider.GetRequiredService<LazyPrivateObjectStore>());
        var ingestion = provider.GetRequiredService<IngestionPrivateStoreRegistration>();
        Assert.Equal(4194304L, ingestion.NativeByteLimit);
        Assert.IsType<RevisionIngestionService>(provider.GetRequiredService<IRevisionIngestionService>());
        Assert.Throws<RevisionReportServiceException>(() => provider.GetRequiredService<IRevisionReportReader>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("UnsupportedSyntheticSecretKey=" + Secret)]
    [InlineData(Connection + ";Multiplexing=true")]
    public async Task InvalidDedicatedReaderConfigurationIsSanitizedAtReportResolution(string? value)
    {
        var configuration = ReportConfiguration();
        configuration.Remove(ReaderCredential);
        if (value is not null) configuration[ReaderCredential] = value;
        var builder = Builder();
        Configure(builder, configuration);
        await using var provider = builder.Services.BuildServiceProvider();
        Assert.IsType<PostgresCatalogReader>(provider.GetRequiredService<ICatalogReader>());
        var error = Assert.Throws<RevisionReportServiceException>(() => provider.GetRequiredService<IRevisionReportReader>());
        Assert.Equal(RevisionReportFailureCategory.BackendUnavailable, error.Failure);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("UnsupportedSyntheticSecretKey", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SyntheticCachedCurrentRetainedAndEnsureHitsNeverResolveWriterRootOrRunner(bool derivationEnabled)
    {
        var configuration = ReportConfiguration(derive: derivationEnabled);
        var reads = new List<string>();
        var builder = Builder();
        Configure(builder, configuration, reads);
        var fixture = new SyntheticCachedReport();
        builder.Services.AddSingleton<IRevisionReportMetadataReader>(fixture);
        builder.Services.AddSingleton<IRevisionReportProfileRegistry>(fixture.Registry);
        builder.Services.AddSingleton<IPrivateObjectStore>(fixture);
        builder.Services.AddSingleton<IInspectionWorkerRunner>(_ => throw new InvalidOperationException("Cached reads must not resolve a runner."));
        await using var provider = builder.Services.BuildServiceProvider();
        var reader = provider.GetRequiredService<IRevisionReportReader>();
        var deriver = provider.GetRequiredService<IRevisionReportDeriver>();
        var current = await reader.GetCurrentAsync(fixture.Actor, fixture.Key.Scope, RevisionReportOperation.Projection);
        var retained = await reader.GetRetainedAsync(fixture.Actor, fixture.Key.Scope, RevisionReportOperation.Projection,
            fixture.Result.DerivationId);
        var ensured = await deriver.EnsureAsync(fixture.Actor, fixture.Key.Scope, RevisionReportOperation.Projection);
        foreach (var report in new[] { current.Report, retained.Report, ensured.Read?.Report })
            Assert.Equal(fixture.Result, Assert.IsType<VerifiedProjectionReport>(report).Metadata);
        Assert.Equal(3, fixture.DerivedReads);
        Assert.Equal(0, fixture.NativeReads);
        Assert.Equal(0, fixture.BackendResolutions);
        Assert.DoesNotContain(WriterCredential, reads);
        Assert.DoesNotContain("WEFT_PRIVATE_STORAGE_ROOT", reads);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("relative-private-root", true)]
    [InlineData("/var/tmp/weft-host-test-existing-root", false)]
    public async Task MissingNewWorkDependenciesRefuseBeforeResolvingWriterCredentials(string? root, bool runnerPresent)
    {
        var configuration = ReportConfiguration(derive: true);
        configuration["WEFT_FERRULE_WORKER_IMAGE"] = Image;
        if (root is not null) configuration["WEFT_PRIVATE_STORAGE_ROOT"] = root;
        var reads = new List<string>();
        var builder = Builder();
        Configure(builder, configuration, reads);
        if (runnerPresent) builder.Services.AddSingleton<IInspectionWorkerRunner>(new NeverRunRunner(Image));
        await using var provider = builder.Services.BuildServiceProvider();
        var store = provider.GetRequiredService<IRevisionReportMetadataStore>();
        var error = await Assert.ThrowsAsync<RevisionReportServiceException>(() => store.TryClaimAsync(
            Actor(), RevisionReportCodecTestSupport.ProjectionKey(), Guid.NewGuid()));
        Assert.True(error.Failure is RevisionReportFailureCategory.StorageIo or RevisionReportFailureCategory.BackendUnavailable);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(WriterCredential, reads);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("UnsupportedSyntheticSecretKey=" + Secret)]
    [InlineData(Connection + ";No Reset On Close=true")]
    public async Task WriterConfigurationIsDeferredUntilFirstWriteAndItsErrorsAreSanitized(string? value)
    {
        var configuration = ReportConfiguration(derive: true);
        configuration["WEFT_FERRULE_WORKER_IMAGE"] = Image;
        configuration["WEFT_PRIVATE_STORAGE_ROOT"] = "/var/tmp/weft-host-test-existing-root";
        if (value is not null) configuration[WriterCredential] = value;
        var reads = new List<string>();
        var builder = Builder();
        Configure(builder, configuration, reads);
        var runner = new NeverRunRunner(Image);
        var runnerResolutions = 0;
        builder.Services.AddSingleton<IInspectionWorkerRunner>(_ => { runnerResolutions++; return runner; });
        await using var provider = builder.Services.BuildServiceProvider();
        _ = provider.GetRequiredService<IRevisionReportReader>();
        Assert.DoesNotContain(WriterCredential, reads);
        var error = await Assert.ThrowsAsync<RevisionReportServiceException>(() => provider.GetRequiredService<IRevisionReportMetadataStore>()
            .TryClaimAsync(Actor(), RevisionReportCodecTestSupport.ProjectionKey(), Guid.NewGuid()));
        Assert.Equal(RevisionReportFailureCategory.BackendUnavailable, error.Failure);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
        Assert.Contains(WriterCredential, reads);
        Assert.Equal(0, runnerResolutions);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task LazyWriterForwardsAllThreeExactPortsThroughOneFactory()
    {
        var factories = 0;
        var actual = new RecordingStore();
        var lazy = new LazyRevisionReportMetadataStore(() => { factories++; return actual; });
        var claim = RevisionReportCodecTestSupport.Claim(RevisionReportCodecTestSupport.ProjectionKey());
        var completion = RevisionReportCompletion.ForFailure(RevisionReportFailureCategory.BackendFailure);
        var target = new RevisionReportRepairTarget(claim.Key, claim.CacheEntryId, claim.DerivationId,
            new(claim.CandidateObjectId, Sha256Digest.Parse(new string('3', 64)), 1), RevisionReportRepairReason.Missing);
        var actor = Actor();
        var instance = Guid.NewGuid();
        Assert.Equal(0, factories);
        foreach (var result in new[] { await lazy.TryClaimAsync(actor, claim.Key, instance),
            await lazy.FinishAsync(actor, claim, completion), await lazy.RepairAsync(actor, target, instance) })
            Assert.Equal(RevisionReportStoreOutcome.ClaimLost, result.Outcome);
        Assert.Equal(1, factories);
        Assert.Same(actor, actual.Actor);
        Assert.Same(claim.Key, actual.Key);
        Assert.Equal(instance, actual.Instance);
        Assert.Same(claim, actual.Claim);
        Assert.Same(completion, actual.Completion);
        Assert.Same(target, actual.Target);
        Assert.Equal(3, actual.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task PreCanceledWriterCallsResolveNoFactoryAndPreserveCallerCancellation(int operation)
    {
        var factories = 0;
        var lazy = new LazyRevisionReportMetadataStore(() => { factories++; throw new InvalidOperationException(); });
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Write(lazy, operation, canceled.Token));
        Assert.Equal(0, factories);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Write(new UnavailableRevisionReportMetadataStore(), operation, canceled.Token));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task DisabledWriterAndDishonestFactoryFailuresReturnClosedAvailability(int operation)
    {
        var unavailable = await Assert.ThrowsAsync<RevisionReportServiceException>(() =>
            Write(new UnavailableRevisionReportMetadataStore(), operation));
        Assert.Equal(RevisionReportFailureCategory.BackendUnavailable, unavailable.Failure);
        var lazy = new LazyRevisionReportMetadataStore(() => throw new InvalidOperationException(Secret));
        var error = await Assert.ThrowsAsync<RevisionReportServiceException>(() => Write(lazy, operation));
        Assert.Equal(RevisionReportFailureCategory.BackendUnavailable, error.Failure);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
    }

    private static Task<RevisionReportStoreResult> Write(IRevisionReportMetadataStore store, int operation,
        CancellationToken token = default)
    {
        var claim = RevisionReportCodecTestSupport.Claim(RevisionReportCodecTestSupport.ProjectionKey());
        return operation switch
        {
            0 => store.TryClaimAsync(Actor(), claim.Key, Guid.NewGuid(), token),
            1 => store.FinishAsync(Actor(), claim, RevisionReportCompletion.ForFailure(RevisionReportFailureCategory.BackendFailure), token),
            2 => store.RepairAsync(Actor(), new(claim.Key, claim.CacheEntryId, claim.DerivationId,
                new(claim.CandidateObjectId, Sha256Digest.Parse(new string('3', 64)), 1), RevisionReportRepairReason.Missing), Guid.NewGuid(), token),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
    }

    private static RevisionReportFormat ProjectionFormat() => new("ferrule", RevisionReportOperation.Projection,
        "weft.mapping-projection.v1", FerruleInspectionBackend.ProjectionVersion, FerruleRevisionReportBackendFactory.CodecVersion);
    private static RevisionReportFormat ValidationFormat() => new("ferrule", RevisionReportOperation.Validation,
        "weft.mapping-validation.v1", FerruleRevisionReportBackendFactory.ValidationAlgorithm, FerruleRevisionReportBackendFactory.CodecVersion);
    private static ActorContext Actor() => new(RevisionReportCodecTestSupport.Claim(RevisionReportCodecTestSupport.ProjectionKey()).OriginalActor);
    private static CatalogRevisionReportOptions Options(IServiceCollection services) =>
        Assert.IsType<CatalogRevisionReportOptions>(Assert.Single(services, item => item.ServiceType == typeof(CatalogRevisionReportOptions)).ImplementationInstance);
    private static IPEndPoint Listener() => new(IPAddress.Loopback, 4100);
    private static WebApplicationBuilder Builder() => WebApplication.CreateBuilder(new WebApplicationOptions
    { EnvironmentName = "Development", Args = [] });
    private static void Configure(WebApplicationBuilder builder, Dictionary<string, string> configuration, List<string>? read = null) =>
        CatalogHostConfiguration.Configure(builder, Listener(), variable => { read?.Add(variable); return configuration.GetValueOrDefault(variable); });
    private static Dictionary<string, string> CatalogConfiguration() => new()
    {
        ["WEFT_CATALOG_ENABLED"] = "true",
        ["WEFT_DEV_IDENTITY_ENABLED"] = "true",
        ["WEFT_DEV_PRINCIPAL_ID"] = "33333333-3333-3333-3333-333333333333",
        ["WEFT_DEV_ORGANIZATION_ID"] = "11111111-1111-1111-1111-111111111111",
        ["WEFT_DEV_BROWSER_ORIGIN"] = "http://127.0.0.1:4100",
        ["WEFT_CATALOG_CONNECTION_STRING"] = Connection
    };
    private static Dictionary<string, string> ReportConfiguration(bool derive = false)
    {
        var configuration = CatalogConfiguration();
        configuration[ReadFlag] = "true";
        if (derive) configuration[DeriveFlag] = "true";
        configuration[ReaderCredential] = Connection;
        return configuration;
    }

    private sealed class NeverRunRunner(string identity) : IInspectionWorkerRunner
    {
        internal int Calls { get; private set; }
        public string BuildIdentity => identity;
        public Task<WorkerProcessResult> RunAsync(ReadOnlyMemory<byte> requestFrame, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("Pure host tests must not run a worker.");
        }
    }

    private sealed class RecordingStore : IRevisionReportMetadataStore
    {
        internal int Calls { get; private set; }
        internal ActorContext? Actor { get; private set; }
        internal RevisionReportKey? Key { get; private set; }
        internal Guid Instance { get; private set; }
        internal RevisionReportClaim? Claim { get; private set; }
        internal RevisionReportCompletion? Completion { get; private set; }
        internal RevisionReportRepairTarget? Target { get; private set; }
        public Task<RevisionReportStoreResult> TryClaimAsync(ActorContext actor, RevisionReportKey key, Guid instance,
            CancellationToken cancellationToken = default)
        {
            Calls++; Actor = actor; Key = key; Instance = instance;
            return Task.FromResult(RevisionReportStoreResult.WithoutData(RevisionReportStoreOutcome.ClaimLost));
        }
        public Task<RevisionReportStoreResult> FinishAsync(ActorContext actor, RevisionReportClaim claim,
            RevisionReportCompletion completion, CancellationToken cancellationToken = default)
        {
            Calls++; Actor = actor; Claim = claim; Completion = completion;
            return Task.FromResult(RevisionReportStoreResult.WithoutData(RevisionReportStoreOutcome.ClaimLost));
        }
        public Task<RevisionReportStoreResult> RepairAsync(ActorContext actor, RevisionReportRepairTarget target, Guid instance,
            CancellationToken cancellationToken = default)
        {
            Calls++; Actor = actor; Target = target; Instance = instance;
            return Task.FromResult(RevisionReportStoreResult.WithoutData(RevisionReportStoreOutcome.ClaimLost));
        }
    }

    private sealed class SyntheticCachedReport : IRevisionReportMetadataReader, IPrivateObjectStore
    {
        private readonly byte[] bytes;
        private readonly RevisionReportReadSnapshot snapshot;
        private readonly AuthorizedRevisionReportSource source;
        private readonly AuthorizedRevisionReportAttempt retained;
        internal ProjectionCacheKey Key { get; } = RevisionReportCodecTestSupport.ProjectionKey();
        internal ActorContext Actor { get; }
        internal RevisionReportResult Result { get; }
        internal IRevisionReportProfileRegistry Registry { get; }
        internal int NativeReads { get; private set; }
        internal int DerivedReads { get; private set; }
        internal int BackendResolutions { get; private set; }

        internal SyntheticCachedReport()
        {
            var claim = RevisionReportCodecTestSupport.Claim(Key);
            Actor = new(claim.OriginalActor);
            bytes = RevisionReportCodecTestSupport.Bytes(RevisionReportCodecTestSupport.Envelope(claim,
                RevisionReportCodecTestSupport.ProjectionJson(Key)));
            Result = RevisionReportCodecTestSupport.Metadata(claim, bytes);
            source = new(Key.Source, claim.OriginalActor, Result.FinishedAtUtc.AddSeconds(1));
            var attempt = new RevisionReportAttemptSummary(Result.DerivationId, Result.Generation, Result.StartedAtUtc,
                claim.LeaseExpiresAtUtc, Result.FinishedAtUtc, RevisionReportCompletion.ForUsable(Key, Result.Observation, Result.DerivedObject));
            snapshot = new(source, Key, Result.CacheEntryId, Result.Generation, Result, attempt);
            retained = new(source, Key, Result.CacheEntryId, attempt, Result);
            Registry = new RevisionReportProfileRegistry([new(Key.Profile, RevisionReportFormat.FromKey(Key), Key.Policy, () =>
            {
                BackendResolutions++;
                throw new InvalidOperationException("Synthetic cached reports must not resolve a backend.");
            })], [new(RevisionReportFormat.FromKey(Key), new RevisionReportCodecTestSupport.StoredOnlyRegistry())]);
        }

        public Task<AuthorizedRevisionReportSource?> GetSourceAsync(ActorContext actor, RevisionScope scope,
            CancellationToken cancellationToken = default) => Task.FromResult<AuthorizedRevisionReportSource?>(source);
        public Task<RevisionReportReadSnapshot?> GetCurrentAsync(ActorContext actor, RevisionReportKey key,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(Key, key);
            return Task.FromResult<RevisionReportReadSnapshot?>(snapshot);
        }
        public Task<AuthorizedRevisionReportAttempt?> GetRetainedAsync(ActorContext actor, RevisionScope scope,
            RevisionReportOperation operation, Guid derivationId, CancellationToken cancellationToken = default)
        {
            Assert.Equal(Result.DerivationId, derivationId);
            return Task.FromResult<AuthorizedRevisionReportAttempt?>(retained);
        }
        public Task<byte[]> ReadVerifiedAsync(RevisionScope scope, PrivateObjectReference reference,
            CancellationToken cancellationToken = default)
        {
            if (reference.Id.Class == PrivateObjectClass.NativeArtifact) { NativeReads++; throw new InvalidOperationException(); }
            Assert.Equal(Result.DerivedObject, reference);
            DerivedReads++;
            return Task.FromResult(bytes.ToArray());
        }
        public Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(RevisionScope scope, PrivateObjectReference reference,
            Stream content, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
}
