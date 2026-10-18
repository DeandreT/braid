using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class ServerHostPrivacyProcessTests(PostgresFixture postgres)
{
    [Fact]
    public async Task FreshUnprotectedProcessProvesActualIncomingOutgoingIdentityAndConsoleObserverSensitivity()
    {
        using var files = new ServerHostFiles(); string result = Path.Combine(files.Root, "reference-result.json");
        await using var probe = ServerHostProcess.Start(new Dictionary<string, string?>
        {
            ["WEFT_HOST_PROBE_OPERATION"] = "reference",
            ["WEFT_HOST_PROBE_RESULT"] = result,
            ["WEFT_HOST_PROBE_CHILD_ROOT"] = files.Root
        }, privacyProbe: true);
        int exit = await probe.WaitForExitAsync(TimeSpan.FromSeconds(100));
        using var document = Read(result); var root = document.RootElement;
        ServerHostCheck.That(exit == 0,
            "The isolated unprotected privacy sensitivity process failed. Closed category: " + FailureCategory(root) + ".");
        (string Name, bool Passed)[] channels =
        [
            ("operation", root.GetProperty("operation").GetString() == "reference"),
            ("operationSucceeded", root.GetProperty("operationSucceeded").GetBoolean()),
            ("observerFailed", !root.GetProperty("observerFailed").GetBoolean()),
            ("incomingGraphs", root.GetProperty("incomingGraphs").GetInt32() > 0),
            ("incomingMarker", root.GetProperty("incomingMarker").GetBoolean()),
            ("incomingActivities", root.GetProperty("incomingActivities").GetInt32() > 0),
            ("outgoingGraphs", root.GetProperty("outgoingGraphs").GetInt32() > 0),
            ("outgoingActivities", root.GetProperty("outgoingActivities").GetInt32() > 0),
            ("referenceContentGraph", root.GetProperty("referenceContentGraph").GetBoolean()),
            ("identityEventMarker", root.GetProperty("identityEventMarker").GetBoolean()),
            ("identityEvents", root.GetProperty("identityEvents").GetInt32() > 0),
            ("identityLogMarker", root.GetProperty("identityLogMarker").GetBoolean()),
            ("consoleMarker", root.GetProperty("consoleMarker").GetBoolean()),
            ("safeHttpStops", root.GetProperty("safeHttpStops").GetInt32() > 0),
            ("safeServerStops", root.GetProperty("safeServerStops").GetInt32() > 0),
            ("lateAttempts", root.GetProperty("lateAttempts").GetInt32() > 0)
        ];
        // Names come only from the fixed assertion list; values are counts/flags, never JSON text or operands.
        var missing = channels.Where(channel => !channel.Passed).Select(channel =>
        {
            var value = root.GetProperty(channel.Name);
            string scalar = value.ValueKind switch
            {
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Number => value.GetInt32().ToString(CultureInfo.InvariantCulture),
                _ => "mismatch"
            };
            return channel.Name + "=" + scalar;
        });
        ServerHostCheck.That(channels.All(channel => channel.Passed),
            "The real unprotected reference did not prove all privacy observer channels sensitive. Missing: " + string.Join(", ", missing) + ".");
    }

    [PostgresFact]
    public Task PublishedSessionAndNormal116ComponentKeepProtectedChannelsClosedUnderVerboseOverrides() =>
        ServerHostSessionCase.RunAsync(postgres, async test =>
        {
            string result = Path.Combine(test.Files.Root, "protected-result.json");
            var environment = test.Environment();
            environment["WEFT_HOST_PROBE_OPERATION"] = "protected";
            environment["WEFT_HOST_PROBE_RESULT"] = result;
            environment["WEFT_HOST_PROBE_COOKIE"] = test.Cookies.Header;
            environment["WEFT_HOST_PROBE_CHILD_ROOT"] = test.Files.Root;
            environment["WEFT_HOST_PROBE_SERVER_ASSEMBLY"] = Environment.GetEnvironmentVariable("WEFT_HOST_TEST_SERVER_PATH");
            foreach (string provider in new[] { "", "Console__", "EventSource__" })
            {
                environment["Logging__" + provider + "LogLevel__Default"] = "Trace";
                environment["Logging__" + provider + "LogLevel__Microsoft.AspNetCore.Hosting.Diagnostics"] = "Trace";
                environment["Logging__" + provider + "LogLevel__Microsoft.AspNetCore.Server.Kestrel"] = "Trace";
                environment["Logging__" + provider + "LogLevel__Microsoft.AspNetCore.Server.Kestrel.BadRequests"] = "Trace";
            }
            var probe = test.Start(environment, privacyProbe: true);
            int exit = await probe.WaitForExitAsync(TimeSpan.FromSeconds(100));
            using var document = Read(result); var root = document.RootElement;
            ServerHostCheck.That(exit == 0,
                "The protected published-host/component privacy process failed. Closed category: " + FailureCategory(root) + ".");
            ServerHostCheck.That(root.GetProperty("operation").GetString() == "protected"
                && root.GetProperty("operationSucceeded").GetBoolean() && !root.GetProperty("observerFailed").GetBoolean()
                && root.GetProperty("positiveSession").GetBoolean() && root.GetProperty("positiveCatalog").GetBoolean()
                && root.GetProperty("protocolSuccess").GetBoolean() && root.GetProperty("providerRequests").GetInt32() > 0
                && root.GetProperty("compositionClosed").GetBoolean()
                && root.GetProperty("malformedProviderRequests").GetInt32() >= 2
                && root.GetProperty("flagsClosed").GetBoolean() && root.GetProperty("completedRequests").GetInt32() >= 4
                && root.GetProperty("incomingGraphs").GetInt32() == 0 && !root.GetProperty("incomingMarker").GetBoolean()
                && root.GetProperty("incomingActivities").GetInt32() == 0 && root.GetProperty("outgoingGraphs").GetInt32() == 0
                && root.GetProperty("outgoingActivities").GetInt32() == 0 && !root.GetProperty("identityEventMarker").GetBoolean()
                && root.GetProperty("identityEvents").GetInt32() == 0
                && !root.GetProperty("identityLogMarker").GetBoolean() && !root.GetProperty("consoleMarker").GetBoolean()
                && root.GetProperty("safeHttpStops").GetInt32() > 0 && root.GetProperty("safeServerStops").GetInt32() > 0
                && root.GetProperty("lateAttempts").GetInt32() > 0,
                "Protected actual host/component diagnostics leaked a raw channel or lacked positive execution controls.");
            await probe.AssertPrivateOutputAsync(["weft-owned-host-private-marker", test.Cookies.Header, test.Files.Root,
                test.Data.Case.SessionReaderConnectionString, test.Data.Case.SessionWriterConnectionString, test.Data.Case.IdentityConnectionString]);
        });

    private static JsonDocument Read(string path)
    {
        JsonDocument? document = null;
        try
        {
            ServerHostCheck.That(File.Exists(path) && new FileInfo(path).Length is > 0 and <= 8192
                && new FileInfo(path).LinkTarget is null && File.GetUnixFileMode(path) == ServerHostFiles.PrivateFile,
                "The isolated privacy process did not produce its exact bounded private categorical result.");
            using var input = File.OpenRead(path); var bytes = new byte[8193];
            int count = input.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            ServerHostCheck.That(count is > 0 and <= 8192, "The private categorical privacy result exceeded its bound.");
            document = JsonDocument.Parse(bytes.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 4 });
            string[] names = ["version", "operation", "operationSucceeded", "failureCategory", "positiveSession", "positiveCatalog",
                "protocolSuccess", "compositionClosed", "flagsClosed", "completedRequests", "providerRequests", "malformedProviderRequests", "incomingGraphs",
                "incomingMarker", "incomingActivities", "outgoingGraphs", "outgoingActivities", "referenceContentGraph", "identityEventMarker",
                "identityEvents", "identityLogMarker", "consoleMarker", "safeHttpStops", "safeServerStops", "lateAttempts", "observerFailed",
                "unsafeEventMarkerObservedPrivately"];
            var root = document.RootElement;
            ServerHostCheck.That(root.ValueKind == JsonValueKind.Object && root.GetProperty("version").GetInt32() == 1
                && root.EnumerateObject().Count() == 27
                && names.ToHashSet(StringComparer.Ordinal).SetEquals(root.EnumerateObject().Select(property => property.Name)),
                "The privacy result had an incompatible closed shape.");
            foreach (string name in names)
            {
                var value = root.GetProperty(name);
                bool typed = name switch
                {
                    "version" or "completedRequests" or "providerRequests" or "malformedProviderRequests" or "incomingGraphs" or
                        "incomingActivities" or "outgoingGraphs" or "outgoingActivities" or "identityEvents" or "safeHttpStops" or
                        "safeServerStops" or "lateAttempts" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _),
                    "operation" => value.ValueKind == JsonValueKind.String && value.GetString() is "reference" or "protected" or "invalid",
                    "failureCategory" => value.ValueKind is JsonValueKind.Null or JsonValueKind.String,
                    _ => value.ValueKind is JsonValueKind.True or JsonValueKind.False
                };
                ServerHostCheck.That(typed, "The privacy result had a noncategorical field type.");
            }
            _ = FailureCategory(root);
            return document;
        }
        catch (ServerHostCaseFailure) { document?.Dispose(); throw; }
        catch (Exception)
        {
            document?.Dispose();
            throw new ServerHostCaseFailure("The private privacy result could not be read as an exact closed report.");
        }
    }

    private static string FailureCategory(JsonElement root)
    {
        var value = root.GetProperty("failureCategory");
        if (value.ValueKind == JsonValueKind.Null) return "none";
        string? category = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        ServerHostCheck.That(category is
            "operation" or "cleanup" or "initialization" or "required-setting" or
            "reference-start" or "reference-incoming" or "reference-stop" or "reference-outgoing" or
            "protected-load" or "protected-start" or "protected-startup" or "protected-incoming" or "protected-stop" or "protected-outgoing" or
            "bootstrap-origin" or "bootstrap-certificate" or "bootstrap-client" or "bootstrap-transport" or "bootstrap-shape" or
            "startup" or "startup-mode" or "startup-status" or "composition" or "entrypoint-exit" or "entrypoint-shape" or "entrypoint-invoke" or
            "callback" or "body" or "positive" or "body-bound" or
            "malformed-connect" or "malformed-tls" or "malformed-write" or "malformed-read" or "malformed-bound" or "malformed-ingress" or
            "reference-authority" or "reference-provider" or "malformed-provider" or "cancellation" or "provider-positive" or
            "malformed-provider-scenario" or "malformed-provider-status" or "malformed-provider-header" or "malformed-provider-cleanup" or
            "malformed-provider-bound" or "malformed-provider-eof" or "malformed-provider-request" or "malformed-provider-peer" or
            "identity" or "result-directory" or "result-bound",
            "The privacy result had a non-allowlisted failure category.");
        return category!;
    }
}
