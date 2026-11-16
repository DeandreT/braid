using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Braid.Api;
using Braid.Api.Catalog;
using Braid.Api.Specs;
using Braid.Application;
using Braid.Contracts.Specs;
using Braid.Domain;

namespace Braid.Tests;

/// <summary>Actual routes and service orchestration with trusted transport/storage/worker fixture ports.
/// Identity infrastructure, schema qualification and Linux containment are separate qualifications.</summary>
public sealed class SpecDocumentIntakeHttpTests
{
    [Theory]
    [InlineData("docx")]
    [InlineData("xlsx")]
    [InlineData("pdf")]
    public async Task Unavailable_formats_are_not_advertised_and_refuse_before_publication(string format)
    {
        await using var h = await Host.Start();
        var caps = await h.Client.GetFromJsonAsync<SpecIntakeCapabilitiesV1>(h.Path + "/capabilities");
        Assert.Equal(1, caps!.Version); Assert.Equal(new[] { "text", "json", "openapi-json" }, caps.Formats.Select(f => f.Format));
        var response = await h.Upload(format, "{}"u8.ToArray());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("DocumentWorkerUnavailable", (await response.Content.ReadFromJsonAsync<SpecErrorV1>())!.Code);
        Assert.Empty(h.Ports.Reservations); Assert.Equal(0, h.Workers.Calls);
    }

    [Theory]
    [InlineData("docx")]
    [InlineData("xlsx")]
    [InlineData("pdf")]
    public async Task Admitted_document_routes_publish_exact_partial_evidence_and_replay(string format)
    {
        await using var h = await Host.Start(); h.Admit();
        var caps = await h.Client.GetFromJsonAsync<SpecIntakeCapabilitiesV1>(h.Path + "/capabilities");
        var admitted = Assert.Single(caps!.Formats, f => f.Format == format);
        Assert.Equal(format == "pdf" ? 8388608 : 10485760, admitted.MaximumSourceBytes);
        Assert.Equal(format == "pdf" ? 4 : 3, admitted.ExtractorVersion); Assert.Equal("braid.spec-evidence.v2", admitted.EvidenceFormat);
        var bytes = format switch
        {
            "docx" => SpecOfficeExtractionTests.Zip(SpecOfficeExtractionTests.Word()),
            "xlsx" => SpecOfficeExtractionTests.Zip(SpecOfficeExtractionTests.Excel()),
            _ => "%PDF fixture bytes"u8.ToArray()
        };
        var request = h.Request(format);
        var first = await h.Upload(format, bytes, request);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var receipt = await first.Content.ReadFromJsonAsync<SpecIntakeV1>(); Assert.Equal("Partial", receipt!.Outcome);
        var evidence = await h.Client.GetByteArrayAsync(h.Path + "/extractions/" + request.ExtractionObjectId);
        using var json = JsonDocument.Parse(evidence); Assert.Equal("braid.spec-evidence.v2", json.RootElement[0].GetString());
        var saved = h.Ports.Reservations[request.ExtractionKey].Claim.Reference;
        Assert.Equal(Sha256Digest.FromBytes(evidence), saved.Digest);
        Assert.Equal(bytes.Length, h.Ports.Reservations[request.UploadKey].Claim.Reference.ByteCount);
        Assert.Equal(receipt, await (await h.Upload(format, bytes, request)).Content.ReadFromJsonAsync<SpecIntakeV1>());
        Assert.Equal(evidence, await h.Client.GetByteArrayAsync(h.Path + "/extractions/" + request.ExtractionObjectId));
        Assert.Equal(2, h.Ports.Reservations.Count);
    }

    [Theory]
    [InlineData("needsOcr", "pdf-needs-ocr", 3)]
    [InlineData("encrypted", "pdf-encrypted", 2)]
    public async Task Pdf_refusals_remain_honest_with_page_facts(string status, string code, int evidenceItems)
    {
        await using var h = await Host.Start(); h.Admit(); h.Workers.Status = status;
        var request = h.Request("pdf"); var response = await h.Upload("pdf", "%PDF"u8.ToArray(), request);
        Assert.Equal("Unsupported", (await response.Content.ReadFromJsonAsync<SpecIntakeV1>())!.Outcome);
        using var evidence = JsonDocument.Parse(await h.Client.GetByteArrayAsync(h.Path + "/extractions/" + request.ExtractionObjectId));
        Assert.Equal(code, evidence.RootElement[1].GetString()); Assert.Equal(evidenceItems, evidence.RootElement.GetArrayLength());
        if (status == "needsOcr") Assert.True(evidence.RootElement[2][4].GetBoolean());
    }

    [Fact]
    public async Task Schema_and_worker_availability_are_rechecked_and_legacy_remains_available()
    {
        await using var h = await Host.Start(); h.Admit();
        h.Schema = false;
        Assert.Equal(3, (await h.Client.GetFromJsonAsync<SpecIntakeCapabilitiesV1>(h.Path + "/capabilities"))!.Formats.Count);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await h.Upload("docx", [])).StatusCode);
        h.Schema = true; h.Available = false;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await h.Upload("pdf", [])).StatusCode);
        Assert.Empty(h.Ports.Reservations);
        Assert.Equal("Complete", (await (await h.Upload("json", "{}"u8.ToArray())).Content.ReadFromJsonAsync<SpecIntakeV1>())!.Outcome);
    }

    [Fact]
    public async Task Authorization_caps_and_worker_failure_cannot_publish_extraction()
    {
        await using var h = await Host.Start(); h.Admit(); h.Ports.Denied = true;
        Assert.Equal(HttpStatusCode.NotFound, (await h.Client.GetAsync(h.Path + "/capabilities")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await h.Upload("pdf", [])).StatusCode);
        h.Ports.Denied = false;
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await h.Upload("pdf", new byte[8388609])).StatusCode);
        Assert.Empty(h.Ports.Reservations); Assert.Equal(0, h.Workers.Calls);
        h.Workers.Fail = true;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await h.Upload("pdf", "%PDF"u8.ToArray())).StatusCode);
        Assert.Single(h.Ports.Reservations);
        Assert.All(h.Ports.Reservations.Values, r => Assert.Equal(SpecObjectKind.Original, r.Claim.Reference.Id.Kind));
    }

    [Fact]
    public async Task Pdf_wrong_profile_fails_before_extraction_reservation()
    {
        await using var h = await Host.Start(); h.Admit(); h.Workers.WrongProfile = true;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await h.Upload("pdf", "%PDF"u8.ToArray())).StatusCode);
        Assert.Single(h.Ports.Reservations);
    }

    [Theory]
    [InlineData("docx")]
    [InlineData("pdf")]
    public async Task Schema_loss_during_worker_execution_prevents_evidence_publication(string format)
    {
        await using var h = await Host.Start(); h.Admit(); h.Workers.After = () => h.Schema = false;
        var bytes = format == "pdf" ? "%PDF"u8.ToArray() : SpecOfficeExtractionTests.Zip(SpecOfficeExtractionTests.Word());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await h.Upload(format, bytes)).StatusCode);
        Assert.Single(h.Ports.Reservations);
    }

    [Fact]
    public void Public_admission_api_has_no_constructor_that_can_enable_document_workers()
    {
        Assert.Empty(typeof(SpecDocumentIntakeAdmission).GetConstructors());
        var services = new ServiceCollection();
        Braid.Server.SpecHostConfiguration.Configure(services, _ => null, false);
        using var provider = services.BuildServiceProvider();
        Assert.Same(SpecDocumentIntakeAdmission.Unavailable, provider.GetRequiredService<SpecDocumentIntakeAdmission>());
    }

    private sealed class Workers : ISpecOfficeExtractionWorker, ISpecPdfExtractionWorker
    {
        internal SpecPdfExtractionProfile Profile = new(Sha256Digest.FromBytes("approved fixture build"u8));
        internal int Calls; internal string Status = "partial"; internal bool Fail, WrongProfile;
        internal Action? After;
        public Task<SpecOfficeExtractionResult> ExtractAsync(SpecCapturedSource source, SpecOfficeExtractionLimits limits, CancellationToken token)
        { Calls++; token.ThrowIfCancellationRequested(); if (Fail) throw new SpecOfficeWorkerUnavailableException(); var result = SpecOfficeParser.Extract(source, limits, token); After?.Invoke(); return Task.FromResult(result); }
        public Task<SpecPdfExtractionResult> ExtractAsync(SpecCapturedSource source, CancellationToken token)
        {
            Calls++; token.ThrowIfCancellationRequested(); if (Fail) throw new SpecPdfWorkerUnavailableException();
            var response = JsonSerializer.SerializeToUtf8Bytes(new
            {
                format = SpecPdfWorkerResponse.Format,
                extractor = SpecPdfWorkerResponse.Extractor,
                sourceSha256 = source.Digest.Value,
                sourceByteCount = source.ByteCount,
                status = Status,
                pages = Status == "encrypted" ? [] : new[] { new { page = 1, text = Status == "needsOcr" ? "" : "PDF text", missingText = Status == "needsOcr" } }
            });
            var result = SpecPdfExtractionResult.Decode(response, 0, source, WrongProfile ? new(Sha256Digest.FromBytes("wrong build"u8)) : Profile);
            After?.Invoke(); return Task.FromResult(result);
        }
    }
    private sealed class Host : IAsyncDisposable
    {
        private WebApplication app = null!;
        internal HttpClient Client = null!;
        internal SpecBundleScope Scope = new(new(Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid());
        internal SpecHttpTests.Ports Ports = null!;
        internal Workers Workers = new();
        internal bool Schema = true, Available = true;
        internal string Path => $"/api/v1/organizations/{Scope.Project.OrganizationId}/projects/{Scope.Project.ProjectId}/spec-bundles/{Scope.BundleId}";
        internal static async Task<Host> Start()
        {
            var h = new Host(); h.Ports = new(h.Scope);
            var b = WebApplication.CreateBuilder(); b.Logging.ClearProviders(); b.WebHost.UseTestServer(); b.Services.AddBraidApi();
            b.Services.AddSingleton(new SpecApiOptions(true)); b.Services.AddSingleton<ISpecBundleServiceFactory>(h.Ports);
            h.app = b.Build();
            SpecEndpoints.MapBraidSpecs(h.app, (_, _) => Task.FromResult(CatalogRequestAdmission.AcceptedBearer(new(h.Ports.Actor), h.Scope.Project.OrganizationId)));
            await h.app.StartAsync(); h.Client = h.app.GetTestClient(); return h;
        }
        internal void Admit() => Ports.Documents = new(_ => Task.FromResult(Schema), Workers, new(), () => Available, Workers, Workers.Profile, () => Available);
        internal SpecUploadV1 Request(string format) => new(Guid.NewGuid(), 1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 2, format);
        internal async Task<HttpResponseMessage> Upload(string format, byte[] bytes, SpecUploadV1? upload = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Path + "/uploads") { Content = new ByteArrayContent(bytes) };
            request.Content.Headers.ContentType = new("application/octet-stream");
            request.Headers.TryAddWithoutValidation(SpecEndpoints.UploadHeader, JsonSerializer.Serialize(upload ?? Request(format), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            request.Headers.TryAddWithoutValidation("If-Match", "\"1\""); return await Client.SendAsync(request);
        }
        public async ValueTask DisposeAsync() { Client.Dispose(); await app.DisposeAsync(); }
    }
}
