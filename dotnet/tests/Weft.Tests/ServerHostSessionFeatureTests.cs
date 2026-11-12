using System.Runtime.Versioning;
using System.Text.Json;

namespace Weft.Tests;

/// <summary>
/// The published Session host with chat, previews and comparison composed through protected file
/// deliveries against actual restricted PostgreSQL roles. No provider call or native worker is claimed.
/// </summary>
[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class ServerHostSessionFeatureTests(PostgresFixture postgres)
{
    [PostgresFact]
    public Task PublishedSessionComposesChatPreviewsAndComparisonThroughProtectedDeliveries() =>
        ServerHostSessionCase.RunAsync(postgres, async test =>
        {
            var host = test.Start(FeatureEnvironment(test)); using var client = ServerHostHttp.Client(test.Origin, test.Files.TlsCertificate);
            test.PositiveStatus(await ServerHostHttp.StartupStatusAsync(client, test.Cookies.Header));
            // The submitter's message runtime verifies its storage with the delivered writer login at startup.
            ServerHostCheck.That(await test.Data.Case.ScalarAsync<long>(
                "SELECT count(*) FROM pg_catalog.pg_tables WHERE schemaname='weft_messages'") > 0,
                "The Session preview submitter did not start its message storage.");
            var context = await test.ContextAsync(client); test.PositiveContext(context);
            string token;
            using (var body = context.Json())
            {
                var features = body.RootElement.GetProperty("features");
                ServerHostCheck.That(features.GetProperty("chat").GetBoolean() && features.GetProperty("preview").GetBoolean()
                    && features.GetProperty("readReports").GetBoolean(), "The Session catalog did not advertise its composed features.");
                token = body.RootElement.GetProperty("requestToken").GetString()!;
            }
            var revision = test.Data.Original.Facts.FutureRevision;
            string revisionPath = CatalogRevisionReportHttpTestSupport.RevisionPath(revision);
            var cookies = new Dictionary<string, string> { ["Cookie"] = test.Cookies.Header };

            // Chat: availability through the composed profile policy, then the delivered chat reader login.
            var availability = await ServerHostHttp.SendAsync(client, revisionPath + "/chat", headers: cookies);
            using (var body = availability.Json())
                ServerHostCheck.That(availability.Status == 200 && body.RootElement.GetProperty("profiles").GetArrayLength() == 1,
                    "Session chat availability did not use its admitted profile document.");
            var conversations = await ServerHostHttp.SendAsync(client, revisionPath + "/chat/conversations", headers: cookies);
            ServerHostCheck.That(conversations.Status == 200, "Session chat did not read through its delivered reader login.");

            // Previews: the Wolverine submitter outbox and both delivered preview logins.
            var unsafeHeaders = new Dictionary<string, string>
            {
                ["Cookie"] = test.Cookies.Header,
                ["Origin"] = test.Origin.GetLeftPart(UriPartial.Authority),
                ["X-XSRF-TOKEN"] = token
            };
            var submission = JsonSerializer.SerializeToUtf8Bytes(new
            {
                target = new { kind = "all" },
                parameters = Array.Empty<object>(),
                inputs = new[] { new { name = (string?)null, content = Convert.ToBase64String("{\"orders\":[]}"u8.ToArray()) } }
            });
            var submitted = await ServerHostHttp.SendAsync(client, revisionPath + "/previews", HttpMethod.Post,
                new Dictionary<string, string>(unsafeHeaders) { ["Idempotency-Key"] = Guid.NewGuid().ToString("D"), ["Content-Type"] = "application/json" },
                submission);
            string run;
            using (var body = submitted.Json())
            {
                ServerHostCheck.That(submitted.Status == 201 && body.RootElement.GetProperty("outcome").GetString() == "created"
                    && body.RootElement.GetProperty("run").GetProperty("state").GetString() == "queued",
                    "Session preview submission did not record a queued run through its outbox.");
                run = body.RootElement.GetProperty("run").GetProperty("runId").GetString()!;
            }
            string runPath = "/api/v1/organizations/" + revision.OrganizationId.ToString("D") + "/previews/" + run;
            var read = await ServerHostHttp.SendAsync(client, runPath, headers: cookies);
            ServerHostCheck.That(read.Status == 200, "Session preview read did not use its delivered reader login.");
            var canceled = await ServerHostHttp.SendAsync(client, runPath + "/cancel", HttpMethod.Post, unsafeHeaders);
            using (var body = canceled.Json())
                ServerHostCheck.That(canceled.Status == 200 && body.RootElement.GetProperty("outcome").GetString() == "canceled",
                    "Session preview cancellation did not complete the queued run.");
            ServerHostCheck.That(await test.Data.Case.ScalarAsync<long>(
                "SELECT count(*) FROM weft.preview_runs WHERE state=4") == 1, "The canceled preview was not durable.");

            // Comparison: the catalog's admitted report read, not the unknown-route fallback.
            string query = "?leftRevision=" + revision.RevisionId.ToString("D") + "&leftProjection=" + Guid.NewGuid().ToString("D")
                + "&rightRevision=" + revision.RevisionId.ToString("D") + "&rightProjection=" + Guid.NewGuid().ToString("D");
            var comparison = await ServerHostHttp.SendAsync(client, CatalogHttpTestSupport.MappingPath(revision) + "/comparisons" + query, headers: cookies);
            using (var body = comparison.Json())
                ServerHostCheck.That(comparison.Status == 404 && body.RootElement.GetProperty("code").GetString() == "notFound",
                    "Session comparison was not composed over retained report reads.");
            await host.StopAsync();
            // The message runtime's own diagnostics stay inside the Session logging boundary.
            await host.AssertPrivateOutputAsync([test.Files.Root, test.Data.Case.ChatReaderConnectionString,
                test.Data.Case.ChatWriterConnectionString, test.Data.Case.PreviewWriterConnectionString,
                test.Data.Case.PreviewReaderConnectionString, run]);
        });

    [PostgresFact]
    public Task PublishedSessionRefusesPlainOrUnprotectedFeatureCredentialsBeforeListening() =>
        ServerHostSessionCase.RunAsync(postgres, async test =>
        {
            foreach (string kind in new[] { "plain-chat", "plain-preview", "loose-preview", "chat-without-reports", "same-preview-login" })
            {
                var environment = FeatureEnvironment(test);
                switch (kind)
                {
                    case "plain-chat":
                        environment["WEFT_CHAT_READER_CONNECTION_STRING"] = test.Data.Case.ChatReaderConnectionString;
                        break;
                    case "plain-preview":
                        environment["WEFT_POSTGRES_PREVIEW_WRITER_CONNECTION_STRING"] = test.Data.Case.PreviewWriterConnectionString;
                        break;
                    case "loose-preview":
                        string loose = test.Files.Text("loose-preview-reader.txt", test.Data.Case.PreviewReaderConnectionString + "\n");
                        File.SetUnixFileMode(loose, ServerHostFiles.PrivateFile | UnixFileMode.GroupRead);
                        environment["WEFT_POSTGRES_PREVIEW_READER_CONNECTION_STRING_FILE"] = loose;
                        break;
                    case "chat-without-reports":
                        environment["WEFT_REVISION_REPORTS_ENABLED"] = null;
                        break;
                    case "same-preview-login":
                        environment["WEFT_POSTGRES_PREVIEW_READER_CONNECTION_STRING_FILE"] =
                            environment["WEFT_POSTGRES_PREVIEW_WRITER_CONNECTION_STRING_FILE"];
                        break;
                }
                var host = test.Start(environment);
                ServerHostCheck.That(await host.WaitForExitAsync(TimeSpan.FromSeconds(35)) != 0,
                    "Session admitted a plain, unprotected or inconsistent feature credential: " + kind + ".");
                await host.AssertPrivateOutputAsync([test.Files.Root, test.Data.Case.ChatReaderConnectionString,
                    test.Data.Case.PreviewWriterConnectionString, test.Data.Case.PreviewReaderConnectionString]);
            }
        });

    private Dictionary<string, string?> FeatureEnvironment(ServerHostSessionCase test)
    {
        var environment = test.Environment();
        string Delivered(string label, string connection)
        {
            string existing = Path.Combine(test.Files.Root, label);
            return File.Exists(existing) ? existing : test.Files.Text(label, connection + "\n");
        }
        string credential = Path.Combine(test.Files.Root, "chat-provider-credential");
        if (!File.Exists(credential)) test.Files.Text("chat-provider-credential", "synthetic-unused-provider-credential\n");
        string profiles = Path.Combine(test.Files.Root, "chat-profiles.json");
        if (!File.Exists(profiles))
            test.Files.Text("chat-profiles.json", "{\"version\":1,\"retentionDays\":30,\"profiles\":[{\"id\":\"hosted-fixture\",\"version\":1," +
                "\"displayName\":\"Hosted fixture\",\"provider\":\"fixture\",\"dialect\":\"chatCompletionsSse\",\"model\":\"fixture-model\"," +
                "\"endpoint\":\"http://127.0.0.1:9/v1/chat/completions\",\"loopbackFixture\":true,\"credentialReference\":\"hosted-key\"," +
                "\"capabilities\":[\"text\",\"functionTools\"],\"metadataPolicy\":{\"id\":\"mapping-metadata\",\"version\":1,\"mappingMetadataAllowed\":true}}]," +
                "\"credentials\":[{\"reference\":\"hosted-key\",\"file\":\"" + credential + "\"}]}");
        environment["WEFT_REVISION_REPORTS_ENABLED"] = "true";
        environment["WEFT_POSTGRES_REPORT_CONNECTION_STRING"] = test.Data.Case.ReportConnectionString;
        environment["WEFT_CHAT_ENABLED"] = "true";
        environment["WEFT_CHAT_PROFILES_FILE"] = profiles;
        environment["WEFT_CHAT_READER_CONNECTION_STRING_FILE"] = Delivered("chat-reader.txt", test.Data.Case.ChatReaderConnectionString);
        environment["WEFT_CHAT_WRITER_CONNECTION_STRING_FILE"] = Delivered("chat-writer.txt", test.Data.Case.ChatWriterConnectionString);
        environment["WEFT_CHAT_APPROVER_CONNECTION_STRING_FILE"] = Delivered("chat-approver.txt", postgres.ChatApproverConnectionString(test.Data.Case));
        environment["WEFT_PREVIEWS_ENABLED"] = "true";
        environment["WEFT_POSTGRES_PREVIEW_WRITER_CONNECTION_STRING_FILE"] = Delivered("preview-writer.txt", test.Data.Case.PreviewWriterConnectionString);
        environment["WEFT_POSTGRES_PREVIEW_READER_CONNECTION_STRING_FILE"] = Delivered("preview-reader.txt", test.Data.Case.PreviewReaderConnectionString);
        return environment;
    }
}
