using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Weft.Api.Catalog;
using Weft.Application;
using Weft.Application.Regression;
using Weft.Contracts.Operations;

namespace Weft.Tests;

public sealed partial class RegressionCaseHttpTests
{
    [PostgresFact]
    public async Task Run_HTTP_queues_an_exact_case_then_reopens_verified_outputs_and_comparison()
    {
        await using var data = await RegressionCasePostgresTestSupport.CreateAsync(fixture);
        var value = data.Version(1);
        await data.Writer.SaveAsync(data.Actor, value);
        await using var http = await CaseHttp.StartAsync(data);
        var run = Guid.NewGuid();
        using var created = await http.PutRunAsync(run, RunBody(value), await http.TokenAsync());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("created", (await Json(created)).GetProperty("outcome").GetString());
        var queued = await http.RunReader.ReadAsync(data.Actor, data.Mapping.OrganizationId, run);
        Assert.Equal(RegressionRunState.Queued, queued!.State);
        Assert.Equal(new RegressionCaseEnvelopeCodec().Encode(value).Reference, queued.Case);
        var claim = (await http.RunWriter.ClaimAsync(data.Mapping.OrganizationId, run, Guid.NewGuid())).Claim!;
        var outputs = value.Expected;
        var comparison = new RegressionComparisonService().Compare(value.Expected, new(OperationStatusV1.Succeeded, outputs));
        Assert.True(await http.RunWriter.CompleteAsync(claim, RegressionCaseExecution.Complete(MappingPreviewStatus.Succeeded, outputs, comparison)));
        using var read = await http.Client.GetAsync(http.RunsPath + "/" + run.ToString("D"));
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var result = await Json(read);
        Assert.Equal("completed", result.GetProperty("run").GetProperty("state").GetString());
        Assert.Equal("passed", result.GetProperty("comparison").GetProperty("outcome").GetString());
        Assert.Equal(1, result.GetProperty("outputs").GetArrayLength());
        Assert.True(read.Headers.CacheControl?.NoStore);
        Assert.Null(read.Headers.ETag);
    }

    [PostgresFact]
    public async Task Run_HTTP_retry_recovers_one_run_and_changed_case_reference_conflicts()
    {
        await using var data = await RegressionCasePostgresTestSupport.CreateAsync(fixture);
        await data.Writer.SaveAsync(data.Actor, data.Version(1));
        await data.Writer.SaveAsync(data.Actor, data.Version(2));
        await using var http = await CaseHttp.StartAsync(data);
        var id = Guid.NewGuid();
        var token = await http.TokenAsync();
        using var first = await http.PutRunAsync(id, RunBody(data.Version(1)), token);
        using var replay = await http.PutRunAsync(id, RunBody(data.Version(1)), token);
        using var conflict = await http.PutRunAsync(id, RunBody(data.Version(2)), token);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("replayed", (await Json(replay)).GetProperty("outcome").GetString());
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(1L, await data.Case.ScalarAsync<long>("SELECT count(*) FROM weft.regression_runs"));
    }

    [PostgresFact]
    public async Task Run_HTTP_history_is_metadata_only_and_paginates_the_original_submitter()
    {
        await using var data = await RegressionCasePostgresTestSupport.CreateAsync(fixture);
        var value = data.Version(1);
        await data.Writer.SaveAsync(data.Actor, value);
        await using var http = await CaseHttp.StartAsync(data);
        var ids = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var id = Guid.NewGuid();
            ids.Insert(0, id.ToString("D"));
            Assert.Equal(RegressionRunSubmitOutcome.Created, await http.RunWriter.SubmitAsync(new(data.Actor, id, new RegressionCaseEnvelopeCodec().Encode(value).Reference)));
        }
        using var first = await http.Client.GetAsync(http.RunsPath + "?limit=2");
        var page = await Json(first);
        Assert.Equal(ids.Take(2), page.GetProperty("runs").EnumerateArray().Select(row => row.GetProperty("runId").GetString()));
        Assert.DoesNotContain("bytesBase64Url", await first.Content.ReadAsStringAsync());
        using var second = await http.Client.GetAsync(http.RunsPath + "?limit=2&afterRunId=" + page.GetProperty("nextRunId").GetString());
        Assert.Equal(ids[2], (await Json(second)).GetProperty("runs")[0].GetProperty("runId").GetString());
    }

    [PostgresFact]
    public async Task Run_HTTP_requires_mutation_token_before_reading_the_body()
    {
        await using var data = await RegressionCasePostgresTestSupport.CreateAsync(fixture);
        await using var http = await CaseHttp.StartAsync(data);
        using var result = await http.PutRunAsync(Guid.NewGuid(), RunBody(data.Version(1)), null);
        Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        Assert.Equal(0, http.BodyReads);
    }

    [PostgresFact]
    public async Task Run_HTTP_denies_viewers_without_loading_private_case_content()
    {
        await using var data = await RegressionCasePostgresTestSupport.CreateAsync(fixture);
        await data.RoleAsync("viewer");
        await using var http = await CaseHttp.StartAsync(data);
        using var result = await http.PutRunAsync(Guid.NewGuid(), RunBody(data.Version(1)), await http.TokenAsync());
        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
        Assert.Equal(0, http.BodyReads);
        using var history = await http.Client.GetAsync(http.RunsPath);
        Assert.Equal(HttpStatusCode.NotFound, history.StatusCode);
    }

    [PostgresFact]
    public async Task Run_HTTP_revocation_during_body_read_prevents_any_queue_record()
    {
        await using var data = await RegressionCasePostgresTestSupport.CreateAsync(fixture);
        await data.Writer.SaveAsync(data.Actor, data.Version(1));
        await using var http = await CaseHttp.StartAsync(data);
        var token = await http.TokenAsync();
        http.GateBody = true;
        var pending = http.PutRunAsync(Guid.NewGuid(), RunBody(data.Version(1)), token);
        try
        {
            await http.BodyEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await data.RoleAsync("viewer");
            http.ReleaseBody.TrySetResult();
            using var result = await pending;
            Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
            Assert.Equal(0L, await data.Case.ScalarAsync<long>("SELECT count(*) FROM weft.regression_runs"));
        }
        finally { http.ReleaseBody.TrySetResult(); }
    }

    [PostgresFact]
    public async Task Run_HTTP_cancel_is_explicit_and_cannot_replace_a_terminal_result()
    {
        await using var data = await RegressionCasePostgresTestSupport.CreateAsync(fixture);
        await data.Writer.SaveAsync(data.Actor, data.Version(1));
        await using var http = await CaseHttp.StartAsync(data);
        var id = Guid.NewGuid();
        var token = await http.TokenAsync();
        using var put = await http.PutRunAsync(id, RunBody(data.Version(1)), token);
        Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Post, http.RunsPath + "/" + id.ToString("D") + "/cancel");
        request.Headers.TryAddWithoutValidation("Origin", http.Client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        request.Headers.TryAddWithoutValidation(CatalogRequestBoundary.RequestTokenHeader, token);
        using var canceled = await http.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, canceled.StatusCode);
        Assert.Equal("canceled", (await Json(canceled)).GetProperty("run").GetProperty("state").GetString());
    }

    [Fact]
    public async Task Run_HTTP_disabled_routes_never_resolve_run_stores()
    {
        await using var http = await CaseHttp.StartAsync(null);
        using var result = await http.Client.GetAsync(http.RunsPath);
        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"caseId\":\"invalid\",\"version\":\"1\"}")]
    [InlineData("{\"caseId\":\"00000000-0000-0000-0000-000000000001\",\"version\":1}")]
    [InlineData("{\"caseId\":\"00000000-0000-0000-0000-000000000001\",\"version\":\"1\",\"version\":\"2\"}")]
    public async Task Run_request_parser_rejects_noncanonical_and_duplicate_fields(string json)
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = CatalogRegressionRunRequest.MediaType;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var failure = await Assert.ThrowsAsync<CatalogRegressionCaseRequestException>(() => CatalogRegressionRunRequest.ReadAsync(context.Request, CancellationToken.None));
        Assert.Equal(StatusCodes.Status400BadRequest, failure.Status);
    }

    private static JsonObject RunBody(RegressionCaseVersion value) => new()
    {
        ["caseId"] = value.CaseId.ToString("D"),
        ["version"] = value.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)
    };
}
