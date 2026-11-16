using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Braid.MappingBackends.Workers;
using Braid.Storage.FileSystem;

namespace Braid.Tests;

public sealed class HostedInspectionFactAttribute : FactAttribute
{
    public HostedInspectionFactAttribute()
    {
        if (!OperatingSystem.IsLinux() || !PostgresFixture.IsConfigured ||
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BRAID_FERRULE_WORKER_IMAGE")))
            Skip = "Configure PostgreSQL and BRAID_FERRULE_WORKER_IMAGE to run the hosted inspection worker with a published Session host.";
    }
}

/// <summary>
/// A published Session host without Docker access, the ordinary Braid.Worker build serving only hosted inspection
/// with the qualified Ferrule image, actual restricted PostgreSQL roles and private storage.
/// </summary>
[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class ServerHostSessionInspectionTests(PostgresFixture postgres)
{
    private static readonly TimeSpan Native = TimeSpan.FromSeconds(60);
    private static string Image => System.Environment.GetEnvironmentVariable("BRAID_FERRULE_WORKER_IMAGE")!.ToLowerInvariant();

    [HostedInspectionFact]
    public Task PublishedSessionUploadsAndDerivesThroughTheHostedWorker() =>
        ServerHostSessionCase.RunAsync(postgres, async test =>
        {
            using var storage = new PrivateStorageTestRoot();
            using (FileSystemPrivateObjectStore.InitializeV2(storage.Options)) { }
            var pair = HostedPair.Create(test);
            var port = HostedInspectionWorker.ReservePort();
            var host = test.Start(Environment(test, storage, pair, port));
            using var client = ServerHostHttp.Client(test.Origin, test.Files.TlsCertificate);
            // Native admission and derivation each include an isolated worker run and its cleanup.
            client.Timeout = Native;
            test.PositiveStatus(await ServerHostHttp.StartupStatusAsync(client, test.Cookies.Header));
            var context = await test.ContextAsync(client); test.PositiveContext(context);
            string token;
            using (var body = context.Json()) token = body.RootElement.GetProperty("requestToken").GetString()!;
            var unsafeHeaders = new Dictionary<string, string>
            {
                ["Cookie"] = test.Cookies.Header,
                ["Origin"] = test.Origin.GetLeftPart(UriPartial.Authority),
                ["X-XSRF-TOKEN"] = token
            };
            var native = RevisionIngestionTestSupport.Fixture("valid-basic");
            var upload = new Dictionary<string, string>(unsafeHeaders)
            {
                ["Idempotency-Key"] = Guid.NewGuid().ToString("D"),
                ["Content-Type"] = "application/octet-stream"
            };
            string create = RevisionUploadHttpTestSupport.CreatePath(test.Data.Project) +
                RevisionUploadHttpTestSupport.CreateQuery("Hosted inspection upload", "Exact bytes through the hosted worker");

            // With no worker listening, native admission is a retryable unavailable backend, not a local fallback.
            var unavailable = await ServerHostHttp.SendAsync(client, create, HttpMethod.Post, upload, native, budget: Native);
            Guid operation;
            using (var body = unavailable.Json())
            {
                ServerHostCheck.That(unavailable.Status == 503 && body.RootElement.GetProperty("outcome").GetString() == "retryableFailure",
                    "Session native admission did not refuse an unavailable hosted worker as retryable.");
                operation = body.RootElement.GetProperty("receipt").GetProperty("operationId").GetGuid();
            }

            await using var worker = HostedInspectionWorker.Start(test, pair, port);
            await worker.WaitUntilListeningAsync(port);
            var resumed = await ServerHostHttp.SendAsync(client,
                RevisionUploadHttpTestSupport.ReceiptPath(test.Data.Project, operation) + "/resume", HttpMethod.Post, unsafeHeaders, budget: Native);
            Guid revisionId, mappingId;
            using (var body = resumed.Json())
            {
                ServerHostCheck.That(resumed.Status == 200 && body.RootElement.GetProperty("outcome").GetString() == "committedValid",
                    "Session native admission did not recover through the hosted worker: " + Outcome(resumed, body) + ".");
                var receipt = body.RootElement.GetProperty("receipt");
                revisionId = receipt.GetProperty("futureRevision").GetProperty("revisionId").GetGuid();
                mappingId = receipt.GetProperty("futureRevision").GetProperty("mappingId").GetGuid();
            }
            ServerHostCheck.That(await test.Data.Case.ScalarAsync<string>(
                "SELECT backend_build FROM braid.revision_ingestions WHERE operation_id=$1", operation) == Image,
                "The admitted revision did not record the hosted worker's image identity.");

            var second = await ServerHostHttp.SendAsync(client, create, HttpMethod.Post,
                new Dictionary<string, string>(upload) { ["Idempotency-Key"] = Guid.NewGuid().ToString("D") },
                RevisionIngestionTestSupport.Fixture("invalid-function"), budget: Native);
            using (var body = second.Json())
                ServerHostCheck.That(second.Status == 200 && body.RootElement.GetProperty("outcome").GetString() == "committedDecodedInvalid",
                    "Session native admission did not retain hosted validation findings: " + Outcome(second, body) + ".");

            string revision = CatalogHttpTestSupport.ProjectPath(test.Data.Project) + "/mappings/" + mappingId.ToString("D") +
                "/revisions/" + revisionId.ToString("D");
            foreach (var operationName in new[] { "projection", "validation" })
            {
                var ensured = await ServerHostHttp.SendAsync(client, revision + "/reports/" + operationName + "/ensure", HttpMethod.Post,
                    unsafeHeaders, budget: Native);
                using var body = ensured.Json();
                ServerHostCheck.That(ensured.Status == 200 && body.RootElement.GetProperty("state").GetString() == "available"
                    && body.RootElement.GetProperty("report").ValueKind == System.Text.Json.JsonValueKind.Object,
                    "Session report derivation did not complete through the hosted worker: " + operationName + " " +
                    ensured.Status.ToString(CultureInfo.InvariantCulture) + ".");
            }
            var read = await ServerHostHttp.SendAsync(client, revision + "/reports/projection", headers: new Dictionary<string, string> { ["Cookie"] = test.Cookies.Header });
            using (var body = read.Json())
                ServerHostCheck.That(read.Status == 200 && body.RootElement.GetProperty("state").GetString() == "available",
                    "The retained projection was not readable through the Session host.");
            ServerHostCheck.That(await test.Data.Case.ScalarAsync<long>(
                "SELECT count(*) FROM braid.revision_derivation_attempts WHERE result_usable AND provenance_confirmed AND provenance_backend_build=$1",
                Image) == 2, "The retained reports did not record the hosted worker's confirmed image identity.");

            await host.StopAsync();
            await worker.StopAsync();
            await host.AssertPrivateOutputAsync([test.Files.Root, storage.RootPath, pair.ClientKeyText, test.Data.Case.IngestionConnectionString,
                test.Data.Case.DerivationConnectionString]);
        });

    [HostedInspectionFact]
    public Task PublishedSessionRefusesIncompleteHostedInspectionConfigurationBeforeListening() =>
        ServerHostSessionCase.RunAsync(postgres, async test =>
        {
            using var storage = new PrivateStorageTestRoot();
            var pair = HostedPair.Create(test);
            string loose = test.Files.Text("loose-inspection-client.key", File.ReadAllText(pair.ClientKey));
            File.SetUnixFileMode(loose, ServerHostFiles.PrivateFile | UnixFileMode.GroupRead);
            foreach (var (name, value) in new (string, string?)[]
            {
                ("BRAID_INSPECTION_WORKER_URL", null),
                ("BRAID_INSPECTION_WORKER_URL", "http://127.0.0.1:9"),
                ("BRAID_INSPECTION_WORKER_URL", "https://127.0.0.1:9/"),
                ("BRAID_INSPECTION_WORKER_CERTIFICATE_SHA256", null),
                ("BRAID_INSPECTION_WORKER_CERTIFICATE_SHA256", pair.WorkerPin.ToUpperInvariant()),
                ("BRAID_INSPECTION_CLIENT_CERTIFICATE_FILE", null),
                ("BRAID_INSPECTION_CLIENT_PRIVATE_KEY_FILE", loose),
                ("BRAID_FERRULE_WORKER_IMAGE", null),
                ("BRAID_FERRULE_WORKER_IMAGE", Image.ToUpperInvariant())
            })
            {
                var environment = Environment(test, storage, pair, 9);
                environment[name] = value;
                var host = test.Start(environment);
                ServerHostCheck.That(await host.WaitForExitAsync(TimeSpan.FromSeconds(35)) != 0,
                    "Session admitted incomplete hosted inspection configuration: " + name + ".");
                await host.AssertPrivateOutputAsync([test.Files.Root, pair.ClientKeyText]);
            }

            // Derivation alone also requires the hosted worker.
            var derivationOnly = Environment(test, storage, pair, 9);
            derivationOnly["BRAID_REVISION_INGESTION_ENABLED"] = null;
            derivationOnly["BRAID_INSPECTION_WORKER_URL"] = null;
            var refused = test.Start(derivationOnly);
            ServerHostCheck.That(await refused.WaitForExitAsync(TimeSpan.FromSeconds(35)) != 0,
                "Session admitted report derivation without the hosted inspection worker.");
            await refused.AssertPrivateOutputAsync([test.Files.Root, pair.ClientKeyText]);
        });

    [HostedInspectionFact]
    public async Task WorkerRefusesIncompleteInspectionServiceConfiguration()
    {
        var test = new ServerHostFiles();
        try
        {
            var worker = test.HistoricalPair("inspection-worker", expired: false);
            string loose = test.Text("loose-worker.key", File.ReadAllText(worker.PrivateKeyFile));
            File.SetUnixFileMode(loose, ServerHostFiles.PrivateFile | UnixFileMode.OtherRead);
            var pin = new string('a', 64);
            Dictionary<string, string?> Valid() => new(StringComparer.Ordinal)
            {
                ["BRAID_PREVIEW_WORKER_ENABLED"] = "false",
                ["BRAID_INSPECTION_WORKER_ENABLED"] = "true",
                ["BRAID_INSPECTION_LISTEN_ADDR"] = "127.0.0.1:" + HostedInspectionWorker.ReservePort().ToString(CultureInfo.InvariantCulture),
                ["BRAID_INSPECTION_SERVER_CERTIFICATE_FILE"] = worker.CertificateFile,
                ["BRAID_INSPECTION_SERVER_PRIVATE_KEY_FILE"] = worker.PrivateKeyFile,
                ["BRAID_INSPECTION_CLIENT_CERTIFICATE_SHA256"] = pin,
                ["BRAID_FERRULE_WORKER_IMAGE"] = Image
            };
            var cases = new List<Dictionary<string, string?>>();
            foreach (var (name, value) in new (string, string?)[]
            {
                ("BRAID_INSPECTION_WORKER_ENABLED", "yes"), ("BRAID_INSPECTION_WORKER_ENABLED", null),
                ("BRAID_INSPECTION_LISTEN_ADDR", null), ("BRAID_INSPECTION_LISTEN_ADDR", "127.0.0.1:0"), ("BRAID_INSPECTION_LISTEN_ADDR", "localhost:8443"),
                ("BRAID_INSPECTION_CLIENT_CERTIFICATE_SHA256", null), ("BRAID_INSPECTION_CLIENT_CERTIFICATE_SHA256", pin.ToUpperInvariant()),
                ("BRAID_INSPECTION_SERVER_PRIVATE_KEY_FILE", loose), ("BRAID_INSPECTION_SERVER_CERTIFICATE_FILE", null),
                ("BRAID_FERRULE_WORKER_IMAGE", Image.ToUpperInvariant()), ("BRAID_REGRESSION_WORKER_ENABLED", "true")
            })
            {
                var environment = Valid();
                environment[name] = value;
                cases.Add(environment);
            }
            foreach (var environment in cases)
            {
                await using var refused = HostedInspectionWorker.Start(environment);
                ServerHostCheck.That(await refused.WaitForExitAsync(TimeSpan.FromSeconds(30)) != 0,
                    "The worker admitted incomplete inspection service configuration.");
                ServerHostCheck.That(!refused.Output.Contains("PRIVATE KEY", StringComparison.Ordinal),
                    "The worker disclosed private key material.");
            }

            // A valid inspection-only worker starts without preview credentials and stops cleanly.
            var valid = Valid();
            int port = int.Parse(valid["BRAID_INSPECTION_LISTEN_ADDR"]!.Split(':')[1], CultureInfo.InvariantCulture);
            await using var started = HostedInspectionWorker.Start(valid);
            await started.WaitUntilListeningAsync(port);
            await started.StopAsync();
        }
        finally { test.Dispose(); }
    }

    /// <summary>Closed public categories only, for a failure message.</summary>
    private static string Outcome(ServerHostReply reply, System.Text.Json.JsonDocument body) =>
        reply.Status.ToString(CultureInfo.InvariantCulture) + " " +
        (body.RootElement.TryGetProperty("outcome", out var outcome) ? outcome.GetString() : "none") + " " +
        (body.RootElement.TryGetProperty("failure", out var failure) && failure.ValueKind == System.Text.Json.JsonValueKind.String
            ? failure.GetString() : "none");

    private static Dictionary<string, string?> Environment(ServerHostSessionCase test, PrivateStorageTestRoot storage, HostedPair pair, int port)
    {
        var environment = test.Environment();
        environment["BRAID_REVISION_INGESTION_ENABLED"] = "true";
        environment["BRAID_POSTGRES_INGESTION_CONNECTION_STRING"] = test.Data.Case.IngestionConnectionString;
        environment["BRAID_PRIVATE_STORAGE_ROOT"] = storage.RootPath;
        environment["BRAID_REVISION_REPORTS_ENABLED"] = "true";
        environment["BRAID_POSTGRES_REPORT_CONNECTION_STRING"] = test.Data.Case.ReportConnectionString;
        environment["BRAID_REVISION_REPORT_DERIVATION_ENABLED"] = "true";
        environment["BRAID_POSTGRES_DERIVATION_CONNECTION_STRING"] = test.Data.Case.DerivationConnectionString;
        environment["BRAID_FERRULE_WORKER_IMAGE"] = Image;
        environment["BRAID_INSPECTION_WORKER_URL"] = "https://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);
        environment["BRAID_INSPECTION_WORKER_CERTIFICATE_SHA256"] = pair.WorkerPin;
        environment["BRAID_INSPECTION_CLIENT_CERTIFICATE_FILE"] = pair.ClientCertificate;
        environment["BRAID_INSPECTION_CLIENT_PRIVATE_KEY_FILE"] = pair.ClientKey;
        return environment;
    }

    /// <summary>Separately delivered worker and Session client identities, each pinned by the other side.</summary>
    private sealed record HostedPair(string WorkerCertificate, string WorkerKey, string WorkerPin,
        string ClientCertificate, string ClientKey, string ClientPin, string ClientKeyText)
    {
        internal static HostedPair Create(ServerHostSessionCase test)
        {
            var worker = test.Files.HistoricalPair("inspection-worker", expired: false);
            var client = test.Files.HistoricalPair("inspection-client", expired: false);
            return new(worker.CertificateFile, worker.PrivateKeyFile, Pin(worker.CertificateFile),
                client.CertificateFile, client.PrivateKeyFile, Pin(client.CertificateFile), File.ReadAllText(client.PrivateKeyFile).Trim());
        }

        private static string Pin(string file)
        {
            using var certificate = X509Certificate2.CreateFromPem(File.ReadAllText(file));
            return InspectionTransport.CertificateDigest(certificate);
        }
    }

    /// <summary>The ordinary Braid.Worker build with an explicit, inherited-variable-free environment.</summary>
    private sealed class HostedInspectionWorker : IAsyncDisposable
    {
        private readonly Process process;
        private readonly StringBuilder output = new();
        private readonly Task capture;

        private HostedInspectionWorker(Process process)
        {
            this.process = process;
            capture = Task.WhenAll(CaptureAsync(process.StandardOutput), CaptureAsync(process.StandardError));
        }

        internal string Output
        {
            get { lock (output) return output.ToString(); }
        }

        internal static HostedInspectionWorker Start(ServerHostSessionCase test, HostedPair pair, int port) => Start(new Dictionary<string, string?>
        {
            ["BRAID_PREVIEW_WORKER_ENABLED"] = "false",
            ["BRAID_INSPECTION_WORKER_ENABLED"] = "true",
            ["BRAID_INSPECTION_LISTEN_ADDR"] = "127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture),
            ["BRAID_INSPECTION_SERVER_CERTIFICATE_FILE"] = pair.WorkerCertificate,
            ["BRAID_INSPECTION_SERVER_PRIVATE_KEY_FILE"] = pair.WorkerKey,
            ["BRAID_INSPECTION_CLIENT_CERTIFICATE_SHA256"] = pair.ClientPin,
            ["BRAID_FERRULE_WORKER_IMAGE"] = Image
        });

        internal static HostedInspectionWorker Start(IReadOnlyDictionary<string, string?> environment)
        {
            var dll = Path.Combine(AppContext.BaseDirectory, "PreviewWorker", "Braid.Worker.dll");
            ServerHostCheck.That(File.Exists(dll), "The worker build must be copied into the test output.");
            var start = new ProcessStartInfo(System.Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(dll)!
            };
            start.ArgumentList.Add(dll);
            start.Environment.Clear();
            foreach (var name in new[] { "PATH", "DOTNET_ROOT", "HOME", "TMPDIR", "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT" })
                if (System.Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
            start.Environment["DOTNET_ENVIRONMENT"] = "Production";
            start.Environment["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "false";
            start.Environment["BRAID_DOCKER_EXECUTABLE"] = "/usr/bin/docker";
            foreach (var pair in environment)
            {
                if (pair.Value is null) start.Environment.Remove(pair.Key);
                else start.Environment[pair.Key] = pair.Value;
            }
            return new(Process.Start(start) ?? throw new ServerHostCaseFailure("The worker did not start."));
        }

        internal static int ReservePort()
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            return ((IPEndPoint)reservation.LocalEndpoint).Port;
        }

        internal async Task WaitUntilListeningAsync(int port)
        {
            for (var attempt = 0; attempt < 150; attempt++)
            {
                ServerHostCheck.That(!process.HasExited, "The hosted inspection worker exited during startup.");
                using var probe = new TcpClient();
                try
                {
                    await probe.ConnectAsync(IPAddress.Loopback, port);
                    return;
                }
                catch (SocketException) { await Task.Delay(200); }
            }
            throw new ServerHostCaseFailure("The hosted inspection worker did not listen.");
        }

        internal async Task<int> WaitForExitAsync(TimeSpan budget)
        {
            using var bounded = new CancellationTokenSource(budget);
            await process.WaitForExitAsync(bounded.Token);
            await capture.WaitAsync(bounded.Token);
            return process.ExitCode;
        }

        internal async Task StopAsync()
        {
            if (process.HasExited) return;
            ServerHostCheck.That(SendSignal(process.Id, 15) == 0, "The worker could not receive graceful stop.");
            ServerHostCheck.That(await WaitForExitAsync(TimeSpan.FromSeconds(30)) == 0, "The worker did not stop cleanly.");
        }

        private async Task CaptureAsync(StreamReader reader)
        {
            var buffer = new char[1024];
            int count;
            while ((count = await reader.ReadAsync(buffer)) != 0)
                lock (output)
                    if (output.Length < 64 * 1024) output.Append(buffer, 0, count);
        }

        public async ValueTask DisposeAsync()
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            await capture;
            process.Dispose();
        }

        [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
        private static extern int SendSignal(int processId, int signal);
    }
}
