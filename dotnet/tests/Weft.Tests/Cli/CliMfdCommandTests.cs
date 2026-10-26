using System.Text;
using Weft.Cli;
using Weft.Contracts.Catalog;
using Weft.Domain;

namespace Weft.Tests.Cli;

public sealed class CliMfdCommandTests : IAsyncLifetime
{
    private CliTestIssuer issuer = null!;
    private CliApiStub stub = null!;
    private CliHarness cli = null!;
    private CliStubData data = null!;
    private readonly string reviewId = Guid.NewGuid().ToString("D");
    private readonly string reviewDigest = new('a', 64);

    public async Task InitializeAsync()
    {
        issuer = await CliTestIssuer.StartAsync();
        stub = await CliApiStub.StartAsync(issuer);
        cli = new(issuer, stub.Handler);
        data = new(stub.OrganizationId);
        await cli.ConfigureAsync();
        await cli.LoginAsync();
    }
    public async Task DisposeAsync()
    {
        stub.AssertClean();
        cli.Dispose();
        await stub.DisposeAsync();
        await issuer.DisposeAsync();
    }
    private CatalogInterchangeReviewV1 Review(string? packageDigest = null, bool append = false) => new(
        reviewId, reviewDigest, packageDigest ?? new string('b', 64), new string('c', 64),
        new("ferrule", "reviewed-engine", "reviewed-build", "adapter"), "Source to target; 3 nodes",
        [new("unsupported", "Read this complete warning")], DateTimeOffset.UtcNow.AddHours(1),
        append ? data.Mapping : null, append ? data.Revision : null, append ? null : "Imported");

    [Fact]
    public async Task ImportUploadsClosedPackageAndNeverConfirmsWithoutSeparateCommand()
    {
        var file = Path.Combine(cli.ConfigurationDirectory, "design.mfd");
        var asset = Path.Combine(cli.ConfigurationDirectory, "schema.json");
        await File.WriteAllTextAsync(file, "<self-authored/>");
        await File.WriteAllTextAsync(asset, "{}");
        stub.Reply("ReviewMfdCreate", request =>
        {
            var package = InterchangePackage.Decode(request.Body);
            Assert.Equal("maps/design.mfd", package.DesignPath);
            Assert.Equal(2, package.Files.Count);
            Assert.Equal("Imported", request.Query["name"]);
            return CliStubReply.Of(200, Review(package.Digest.Value));
        });
        var result = await cli.RunAsync("mfd", "import", "--project", data.Project, "--name", "Imported", file,
            "--design-path", "maps/design.mfd", "--asset", "maps/schema.json=" + asset);
        Assert.True(result.Exit == 0, result.ToString());
        Assert.Contains("Read this complete warning", result.Out, StringComparison.Ordinal);
        Assert.Contains(reviewDigest, result.Out, StringComparison.Ordinal);
        Assert.Empty(stub.Calls("ConfirmMfdImport"));
    }

    [Fact]
    public async Task AppendRequiresExactHeadAndKeepsItsDestinationInReview()
    {
        stub.Reply("ReviewMfdAppend", request =>
        {
            Assert.Equal(data.Mapping, request.Path["mappingId"]);
            Assert.Equal(data.Revision, request.Query["expectedHeadRevisionId"]);
            return CliStubReply.Of(200, Review(InterchangePackage.Decode(request.Body).Digest.Value, append: true));
        });
        var refused = await cli.RunWithInputAsync("<self-authored/>"u8.ToArray(), "mfd", "import", "-", "--design-path", "design.mfd",
            "--project", data.Project, "--mapping", data.Mapping);
        Assert.Equal(CliExitCode.Usage, refused.Code);
        Assert.Empty(stub.Calls("ReviewMfdAppend"));
        var result = await cli.RunWithInputAsync("<self-authored/>"u8.ToArray(), "mfd", "import", "-", "--design-path", "design.mfd",
            "--project", data.Project, "--mapping", data.Mapping, "--expected-head", data.Revision);
        Assert.True(result.Exit == 0, result.ToString());
    }

    [Fact]
    public async Task ConfirmationRequiresDigestAndWarningConsentAndReusesTheSameKeyWhenWaiting()
    {
        stub.Reply("GetMfdImportReview", _ => CliStubReply.Of(200, Review()));
        var noConsent = await cli.RunAsync("mfd", "confirm", "--project", data.Project, reviewId, "--review-digest", reviewDigest);
        Assert.Equal(CliExitCode.Usage, noConsent.Code);
        Assert.Empty(stub.Calls("ConfirmMfdImport"));
        var changed = await cli.RunAsync("mfd", "confirm", "--project", data.Project, reviewId, "--review-digest", new string('d', 64), "--acknowledge-warnings");
        Assert.Equal(CliExitCode.PreconditionFailed, changed.Code);
        Assert.Empty(stub.Calls("ConfirmMfdImport"));
        stub.Enqueue("ConfirmMfdImport", CliStubReply.Of(202, data.InProgress(reviewId)), CliStubReply.Of(200, data.Committed(reviewId)));
        var result = await cli.RunAsync("mfd", "confirm", "--project", data.Project, reviewId, "--review-digest", reviewDigest, "--acknowledge-warnings", "--wait");
        Assert.True(result.Exit == 0, result.ToString());
        var calls = stub.Calls("ConfirmMfdImport");
        Assert.Equal(2, calls.Count);
        Assert.All(calls, request => Assert.Equal(reviewId, request.Headers["Idempotency-Key"]));
        Assert.Equal(calls[0].Body, calls[1].Body);
        Assert.True(calls[0].Json!["acknowledgeWarnings"]!.GetValue<bool>());
        stub.Reply("ConfirmMfdImport", _ => CliStubReply.Of(503, data.Committed(reviewId)));
        var forged = await cli.RunAsync("mfd", "confirm", "--project", data.Project, reviewId, "--review-digest", reviewDigest, "--acknowledge-warnings");
        Assert.NotEqual(CliExitCode.Success, forged.Code);
        Assert.Empty(forged.Out);
    }

    [Fact]
    public async Task ExportReviewsFirstAndDownloadVerifiesBytesBeforeWritingOrOverwriting()
    {
        stub.Reply("ReviewMfdExport", _ => CliStubReply.Of(200, Review()));
        var result = await cli.RunAsync("mfd", "export", "--project", data.Project, "--mapping", data.Mapping, "--revision", data.Revision);
        Assert.True(result.Exit == 0, result.ToString());
        Assert.Empty(stub.Calls("DownloadMfdExport"));
        var bytes = Encoding.UTF8.GetBytes("<self-authored/>");
        stub.Reply("DownloadMfdExport", _ => new CliStubReply
        {
            Bytes = bytes,
            ContentType = "application/xml",
            Headers = { ["X-Content-Sha256"] = Sha256Digest.FromBytes(bytes).Value }
        });
        var path = Path.Combine(cli.ConfigurationDirectory, "download.mfd");
        string[] command = ["mfd", "download", "--project", data.Project, "--mapping", data.Mapping, "--revision", data.Revision,
            reviewId, "--review-digest", reviewDigest, "--acknowledge-warnings", "--output", path];
        var downloaded = await cli.RunAsync(command);
        Assert.True(downloaded.Exit == 0, downloaded.ToString());
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Equal(CliExitCode.Usage, (await cli.RunAsync(command)).Code);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        command[^1] = "-";
        Assert.Equal(bytes, (await cli.RunAsync(command)).Binary);
        command[^1] = path;
        File.Delete(path);
        stub.Reply("DownloadMfdExport", _ => new CliStubReply
        { Bytes = bytes, ContentType = "application/xml", Headers = { ["X-Content-Sha256"] = new string('0', 64) } });
        Assert.Equal(CliExitCode.Transport, (await cli.RunAsync(command)).Code);
        Assert.False(File.Exists(path));
        command[^1] = "-";
        Assert.Empty((await cli.RunAsync(command)).Binary);
        Assert.Empty(Directory.GetFiles(cli.ConfigurationDirectory, "*.partial"));
    }

    [Fact]
    public async Task OversizedOrUnsafePackagesAreRefusedBeforeSending()
    {
        var unsafePath = await cli.RunWithInputAsync("<self-authored/>"u8.ToArray(), "mfd", "import", "-", "--design-path", "../design.mfd",
            "--project", data.Project, "--name", "Imported");
        Assert.Equal(CliExitCode.Usage, unsafePath.Code);
        var oversized = await cli.RunWithInputAsync(new byte[InterchangePackage.MaximumFileBytes + 1], "mfd", "import", "-", "--design-path", "design.mfd",
            "--project", data.Project, "--name", "Imported");
        Assert.Equal(CliExitCode.Usage, oversized.Code);
        Assert.Empty(stub.Calls("ReviewMfdCreate"));
    }
}
