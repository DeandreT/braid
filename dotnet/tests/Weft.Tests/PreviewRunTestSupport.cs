using System.Runtime.Versioning;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;
using Weft.Storage.FileSystem;

namespace Weft.Tests;

/// <summary>Actual PostgreSQL preview runs over a committed revision whose native bytes are stored privately.</summary>
[SupportedOSPlatform("linux")]
internal sealed class PreviewRunTestSupport : IAsyncDisposable
{
    internal static readonly byte[] Order =
        "{\"id\":\"SO-1\",\"lines\":[{\"sku\":\"A\",\"qty\":2},{\"sku\":\"B\",\"qty\":9007199254740993}]}"u8.ToArray();
    internal static readonly byte[] Rates = "{\"currency\":\"EUR\"}"u8.ToArray();
    internal static readonly DateTimeOffset Now = new(2026, 10, 20, 8, 30, 0, TimeSpan.Zero);

    private readonly PrivateStorageTestRoot root = new();
    private readonly FileSystemPrivateObjectStore objects;
    private readonly PostgresPreviewDatabase reader;
    private readonly PostgresPreviewDatabase writer;

    private PreviewRunTestSupport(PostgresCatalogTestSupport catalog, IngestionReceipt receipt)
    {
        Catalog = catalog;
        Revision = receipt.Facts.FutureRevision;
        Native = receipt.Facts.NativeObject;
        objects = root.Initialize();
        reader = new(catalog.Case.PreviewReaderConnectionString, PreviewDatabaseProfile.Reader);
        writer = new(catalog.Case.PreviewWriterConnectionString, PreviewDatabaseProfile.Writer);
        Store = new(writer);
        Reader = new(reader);
    }

    internal PostgresCatalogTestSupport Catalog { get; }
    internal PostgresCase Case => Catalog.Case;
    internal ActorContext Alice => Catalog.Actor;
    internal RevisionScope Revision { get; }
    internal PrivateObjectReference Native { get; }
    internal PostgresPreviewRunStore Store { get; }
    internal PostgresPreviewRunReader Reader { get; }
    internal PostgresPreviewDatabase ReaderDatabase => reader;
    internal PostgresPreviewDatabase WriterDatabase => writer;
    internal static byte[] NativeBytes => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "FerruleFixtures", "preview-orders.json"));

    internal static async Task<PreviewRunTestSupport> CreateAsync(PostgresFixture fixture, byte[]? nativeBytes = null)
    {
        var catalog = await PostgresCatalogTestSupport.CreateAsync(fixture);
        try
        {
            var native = nativeBytes ?? NativeBytes;
            var receipt = await catalog.CommitCreateAsync("Preview mapping", "JSON preview source", body: native);
            var support = new PreviewRunTestSupport(catalog, receipt);
            using var input = new MemoryStream(native);
            Assert.Equal(PrivateObjectPublishResult.Created,
                await support.objects.PublishCreateOnlyAsync(support.Revision, support.Native, input));
            return support;
        }
        catch
        {
            await catalog.DisposeAsync();
            throw;
        }
    }

    internal PreviewRunSubmission Submission() => new(Revision, Guid.NewGuid(), PreviewTargetSelection.All,
        new PreviewInputDocument(null, Order), [new PreviewInputDocument("rates", Rates)],
        [new("region", PreviewParameterKind.String, "west"), new("express", PreviewParameterKind.Boolean, "true")]);

    internal PreviewRunService Service(PreviewRunOptions? options = null, IPreviewRunStore? store = null) =>
        new(store ?? Store, Reader, new FixedTime(), options);

    internal PreviewRunExecutor Executor(IMappingPreviewBackend backend, PreviewRunOptions? options = null) =>
        new(Store, objects, backend, new FixedTime(), options);

    public async ValueTask DisposeAsync()
    {
        await reader.DisposeAsync();
        await writer.DisposeAsync();
        await objects.DisposeAsync();
        root.Dispose();
        await Catalog.DisposeAsync();
    }

    internal sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Returns one status, or with <c>blockUntilCanceled</c> waits for its token like a running worker.</summary>
    internal sealed class ScriptedBackend(MappingPreviewStatus status, bool blockUntilCanceled = false) : IMappingPreviewBackend
    {
        public int Calls { get; private set; }
        public MappingPreviewRequest? LastRequest { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<MappingPreviewResult> PreviewAsync(MappingPreviewRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastRequest = request;
            Started.TrySetResult();
            var provenance = new PreviewProvenance(request.Artifact.Scope, request.Artifact.Digest, "ferrule", "engine", "image", "adapter", false);
            var result = status;
            if (blockUntilCanceled)
            {
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                catch (OperationCanceledException) { result = MappingPreviewStatus.Canceled; }
            }
            return new MappingPreviewResult(result, provenance, PreviewPolicy.Profile, 0, [],
                new MappingPreviewFailure("preview.scripted", result.ToString(), false, null, false));
        }
    }
}
