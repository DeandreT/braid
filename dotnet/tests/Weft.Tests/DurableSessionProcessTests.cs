using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Weft.Application;
using Weft.Identity;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

/// <summary>Fresh independent processes/providers/pools against actual PG and a standard certificate-encrypted ring.</summary>
[SupportedOSPlatform("linux")]
[Collection(PostgresCollection.Name)]
public sealed class DurableSessionProcessTests(PostgresFixture fixture)
{
    [PostgresFact]
    public Task IndependentProcessesReadRenewAndRevokeInBothDirectionsAndReconstructAfterSamePgRestart() =>
        TicketQualification.RunAsync(fixture, async test =>
        {
            var parentKey = await test.IssueAsync();
            var parentFacts = await test.Data.StoredAsync(test.Writer.LastCreate!.Key);
            var firstChild = await ProbeAsync(test, "retrieve", parentKey);
            Positive(firstChild, parentFacts, test.Data.Principal.Value);
            Assert.True(firstChild.ReaderCalls == 1 && firstChild.CreateCalls == 0 && firstChild.RenewCalls == 0,
                "The independent read performed a hidden mutation.");
            var renewingChild = await ProbeAsync(test, "renew", parentKey);
            Assert.True(renewingChild.Succeeded && renewingChild.PreliminaryRetrieve && renewingChild.ReaderCalls == 2 &&
                renewingChild.RenewCalls == 1 && renewingChild.CreateCalls == 0 && renewingChild.RevokeCalls == 0,
                "The independent renewal did not distinguish its preliminary read and one real CAS.");
            var renewed = await test.Data.StoredAsync(parentFacts.Key);
            PostgresServerSessionTestSupport.SameImmutable(parentFacts, renewed);
            Assert.True(renewed.Version == 2 && renewed.Lifetime.IdleExpiresAtUtc > parentFacts.Lifetime.IdleExpiresAtUtc,
                "The independent renewal did not retain one forward durable transition.");
            using (var fresh = DurableSessionKeyProfile.Open(test.Files.Options(test.Data.Organization), test.Files.Certificate, []))
            {
                ITicketStore freshParent = new ServerSessionTicketStore(fresh, test.Data.Reader, test.Data.Writer);
                TicketQualification.Canonical(await freshParent.RetrieveAsync(parentKey, new DefaultHttpContext(), CancellationToken.None),
                    (await test.Data.Reader.ObserveAsync(renewed.Key))!);
            }
            var issuingChild = await ProbeAsync(test, "issue");
            Assert.True(issuingChild.Succeeded && issuingChild.CreateCalls == 1 && issuingChild.ReaderCalls == 0,
                "The independent creation did not use exactly one real restricted write.");
            var childKey = issuingChild.LearnedKey;
            Assert.True(ServerSessionHandle.TryParse(childKey, out var childHandle),
                "The independent creation returned no canonical learned handle.");
            var childFacts = await test.Data.StoredAsync(new(test.Data.Organization, childHandle!.Digest));
            Positive(issuingChild, childFacts, test.Data.Principal.Value);
            Assert.True(firstChild.ProcessIdentity != issuingChild.ProcessIdentity,
                "Independent observations did not use distinct process lifetimes.");
            TicketQualification.Canonical(await test.Adapter.RetrieveAsync(childKey!, new DefaultHttpContext(), CancellationToken.None),
                (await test.Data.Reader.ObserveAsync(childFacts.Key))!);
            var revokedByChild = await ProbeAsync(test, "revoke", parentKey);
            Assert.True(revokedByChild.Succeeded && revokedByChild.ReaderCalls == 0 && revokedByChild.RevokeCalls == 1,
                "Independent revocation acquired a live-reader prerequisite or retried.");
            Assert.True(await test.Adapter.RetrieveAsync(parentKey, new DefaultHttpContext(), CancellationToken.None) is null,
                "The parent did not freshly observe the child's real tombstone.");
            await test.Adapter.RemoveAsync(childKey!, new DefaultHttpContext(), CancellationToken.None);
            var observedParentRevoke = await ProbeAsync(test, "retrieve", childKey);
            Assert.True(observedParentRevoke.Succeeded && !observedParentRevoke.Found && observedParentRevoke.ReaderCalls == 1,
                "A fresh process did not observe the parent's real tombstone.");

            var restartChild = await ProbeAsync(test, "issue");
            Assert.True(restartChild.Succeeded && ServerSessionHandle.TryParse(restartChild.LearnedKey, out _),
                "The restart setup did not create a genuine durable protected session.");
            var connection = test.Data.Case.MigrationConnectionString;
            var before = await RowsAsync(connection);
            // Every child is already exited/reaped. Close only caller-owned pools;
            // restart the same fixture container+volume, without remigration/adoption.
            await test.Data.ReaderDatabase.DisposeAsync();
            await test.Data.WriterDatabase.DisposeAsync();
            await test.Data.IdentityOwner.DisposeAsync();
            await test.Data.Case.CloseOwnerPoolAsync();
            await fixture.RestartAsync();
            var afterRestart = await ProbeAsync(test, "retrieve", restartChild.LearnedKey);
            Assert.True(afterRestart.Succeeded && afterRestart.Found && afterRestart.Canonical,
                "A fresh process/provider could not reconstruct the original protected row after same-PG restart.");
            Assert.True(afterRestart.Principal == restartChild.Principal && afterRestart.Organization == restartChild.Organization &&
                afterRestart.Epoch == restartChild.Epoch && afterRestart.IssuedUtc == restartChild.IssuedUtc &&
                afterRestart.ExpiresUtc == restartChild.ExpiresUtc,
                "Independent reconstruction changed immutable canonical private facts.");
            PostgresServerSessionTestSupport.SamePrivate(before, await RowsAsync(connection));
            test.Files.AssertEncryptedRing();
        });

    [PostgresFact]
    public Task FreshProcessesRequireRetainedCertificateOverlapAndFailClosedOnWrongRealmCorruptionAndKeyLoss() =>
        TicketQualification.RunAsync(fixture, async test =>
        {
            var original = await ProbeAsync(test, "issue");
            Assert.True(original.Succeeded && original.LearnedKey is not null, "The original protected fixture was not really committed.");
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=Weft owned overlap qualification", rsa,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var nextCertificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(100));
            var nextPath = Path.Combine(test.Files.Root, "next.pfx");
            var pfx = nextCertificate.Export(X509ContentType.Pfx, test.Files.Password);
            try { SessionTicketQualificationRoot.WritePrivate(nextPath, pfx); }
            finally { CryptographicOperations.ZeroMemory(pfx); }
            var unrotatedFiles = test.Files.RingSnapshot();
            Refused(await ProbeAsync(test, "retrieve", original.LearnedKey, current: nextPath), "ConfigurationFailure");
            PostgresServerSessionTestSupport.SamePrivate(unrotatedFiles, test.Files.RingSnapshot());
            var services = new ServiceCollection();
            services.AddDataProtection().SetApplicationName(test.Files.ApplicationName)
                .PersistKeysToFileSystem(new DirectoryInfo(test.Files.Keys))
                .ProtectKeysWithCertificate(nextCertificate)
                .UnprotectKeysWithAnyCertificate(test.Files.Certificate, nextCertificate);
            using (var provider = services.BuildServiceProvider())
            {
                var manager = provider.GetRequiredService<IKeyManager>();
                var beforeKeys = manager.GetAllKeys().Select(k => k.KeyId).ToHashSet();
                var created = manager.CreateNewKey(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(90));
                Assert.True(!beforeKeys.Contains(created.KeyId) && manager.GetAllKeys().Count == beforeKeys.Count + 1,
                    "The standard key manager did not retain its new distinct certificate-protected key.");
            }
            test.Files.AssertEncryptedRing();
            var rotated = await ProbeAsync(test, "issue", current: nextPath, overlap: [test.Files.CertificatePath]);
            Assert.True(rotated.Succeeded && rotated.LearnedKey is not null,
                "A fresh rotated provider could not create its own protected session.");
            var oldRead = await ProbeAsync(test, "retrieve", original.LearnedKey, current: nextPath, overlap: [test.Files.CertificatePath]);
            var newRead = await ProbeAsync(test, "retrieve", rotated.LearnedKey, overlap: [nextPath]);
            Assert.True(oldRead.Succeeded && newRead.Succeeded && oldRead.Found && newRead.Found && oldRead.Canonical && newRead.Canonical &&
                oldRead.Principal == original.Principal && oldRead.Epoch == original.Epoch &&
                newRead.Principal == rotated.Principal && newRead.Epoch == rotated.Epoch,
                "Fresh processes did not retain both live identities across certificate rotation.");
            var beforeRows = await RowsAsync(test.Data.Case.MigrationConnectionString);
            var beforeFiles = test.Files.RingSnapshot();
            var missingOverlap = await ProbeAsync(test, "retrieve", original.LearnedKey, current: nextPath);
            Refused(missingOverlap, "ConfigurationFailure");
            PostgresServerSessionTestSupport.SamePrivate(beforeFiles, test.Files.RingSnapshot());
            var wrongApp = await ProbeAsync(test, "retrieve", original.LearnedKey, overlap: [nextPath], app: "Weft different owned realm");
            Refused(wrongApp, "IntegrityFailure");
            PostgresServerSessionTestSupport.SamePrivate(beforeFiles, test.Files.RingSnapshot());

            // A separate owned corrupted copy is negative material only. The live
            // admitted ring and original key files remain unchanged and retained.
            var corrupt = Path.Combine(test.Files.Root, "corrupt");
            Directory.CreateDirectory(corrupt, SessionTicketQualificationRoot.PrivateDirectory);
            foreach (var path in Directory.GetFiles(test.Files.Keys))
            {
                var copy = File.ReadAllBytes(path);
                try { SessionTicketQualificationRoot.WritePrivate(Path.Combine(corrupt, Path.GetFileName(path)), copy); }
                finally { CryptographicOperations.ZeroMemory(copy); }
            }
            var broken = Directory.GetFiles(corrupt).Order(StringComparer.Ordinal).First();
            File.WriteAllText(broken, "<key malformed-owned-negative='true' />");
            var corruptBefore = PrivateFiles(corrupt);
            Refused(await ProbeAsync(test, "retrieve", original.LearnedKey, overlap: [nextPath], directory: corrupt), "ConfigurationFailure");
            PostgresServerSessionTestSupport.SamePrivate(corruptBefore, PrivateFiles(corrupt));
            PostgresServerSessionTestSupport.SamePrivate(beforeFiles, test.Files.RingSnapshot());

            // Fresh-process key loss with the configured directory still existing;
            // never use the parent's warm provider as persistence evidence.
            var retained = Path.Combine(test.Files.Root, "retained-during-loss");
            Directory.CreateDirectory(retained, SessionTicketQualificationRoot.PrivateDirectory);
            Exception? lossFailure = null;
            var restoreFailures = new List<Exception>();
            try
            {
                foreach (var path in Directory.GetFiles(test.Files.Keys))
                    File.Move(path, Path.Combine(retained, Path.GetFileName(path)));
                Refused(await ProbeAsync(test, "retrieve", original.LearnedKey, overlap: [nextPath]), "ConfigurationFailure");
                Assert.True(Directory.GetFileSystemEntries(test.Files.Keys).Length == 0,
                    "Fresh key-loss startup silently initialized replacement key material.");
            }
            catch (Exception error) { lossFailure = TicketQualification.Safe(error, "The owned fresh key-loss check failed."); }
            finally
            {
                foreach (var path in Directory.GetFiles(retained))
                    try { File.Move(path, Path.Combine(test.Files.Keys, Path.GetFileName(path))); }
                    catch (Exception) { restoreFailures.Add(new InvalidOperationException("An owned retained key file was not restored.")); }
            }
            TicketQualification.Throw(lossFailure, restoreFailures);
            PostgresServerSessionTestSupport.SamePrivate(beforeFiles, test.Files.RingSnapshot());
            PostgresServerSessionTestSupport.SamePrivate(beforeRows, await RowsAsync(test.Data.Case.MigrationConnectionString));
        });

    private static void Positive(ProbeObservation result, ServerSessionFacts facts, Guid owner)
    {
        Assert.True(result.Succeeded && result.Found && result.Canonical && result.Principal == owner.ToString("D") &&
            result.Organization == facts.Key.OrganizationId.ToString("D") && result.Epoch == facts.LoginEpoch.ToString("D") &&
            result.IssuedUtc == ServerSessionCanonicalTicket.ToFrameworkTime(facts.Lifetime.IssuedAtUtc) &&
            result.ExpiresUtc == ServerSessionCanonicalTicket.ToFrameworkTime(facts.Lifetime.AbsoluteExpiresAtUtc),
            "The independent private proof differs from exact canonical durable identity.");
    }

    private static void Refused(ProbeObservation result, string kind) => Assert.True(result.Refused && result.FailureKind == kind &&
        !result.PrivateOutputCreated && result.CreateCalls == 0 && result.RenewCalls == 0 && result.RevokeCalls == 0 &&
        (kind != "ConfigurationFailure" || result.ReaderCalls == 0),
        "A fresh negative profile was not a categorical no-write failure.");

    private static string PrivateFiles(string directory) => JsonSerializer.Serialize(Directory.GetFiles(directory).Order(StringComparer.Ordinal)
        .Select(p => new { Name = Path.GetFileName(p), Mode = (int)File.GetUnixFileMode(p), Digest = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p))) }));

    private static async Task<string> RowsAsync(string connectionString)
    {
        await using var owner = PostgresServerSessionTestSupport.Unpooled(connectionString);
        await owner.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT COALESCE(jsonb_agg(to_jsonb(s) ORDER BY organization_id,handle_digest),'[]'::jsonb)::text FROM ONLY weft.server_sessions s", owner)
        { CommandTimeout = 3 };
        return (string)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Owned rows are absent."));
    }

    private static async Task<ProbeObservation> ProbeAsync(TicketQualification test, string action, string? key = null,
        string? current = null, string[]? overlap = null, string? app = null, string? directory = null)
    {
        var output = Path.Combine(test.Files.Root, "result-" + Guid.NewGuid().ToString("N") + ".json");
        var deadline = action == "issue" ? await test.Data.DeadlineAsync() : default;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = "weft.session-ticket-process-input.v1",
            action,
            privateRoot = test.Files.Root,
            outputFile = output,
            keyDirectory = directory ?? test.Files.Keys,
            applicationName = app ?? test.Files.ApplicationName,
            organizationId = test.Data.Organization,
            readerConnectionString = test.Data.Case.SessionReaderConnectionString,
            writerConnectionString = test.Data.Case.SessionWriterConnectionString,
            currentCertificate = current ?? test.Files.CertificatePath,
            overlapCertificates = overlap ?? [],
            certificatePassword = test.Files.Password,
            key,
            bindingId = action == "issue" ? test.Data.Binding.BindingId : Guid.Empty,
            bindingVersion = action == "issue" ? test.Data.Binding.Version : 0,
            principalId = action == "issue" ? test.Data.Principal.Value : Guid.Empty,
            verifiedDeadlineUtc = deadline
        });
        Assert.True(bytes.Length <= 65536, "The private process input exceeded its fixed bound.");
        var executable = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        Assert.True(executable is not null && Path.IsPathFullyQualified(executable),
            "The exact test host did not supply an absolute dotnet executable.");
        var probe = Path.Combine(AppContext.BaseDirectory, "SessionTicketProcessProbe", "Weft.SessionTicketProcessProbe.dll");
        Assert.True(File.Exists(probe), "The ordinary test-only session probe output is absent.");
        var start = new ProcessStartInfo(executable!)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(probe); // Only the public probe path; all private values use stdin.
        start.Environment.Clear();
        start.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(executable!)!;
        foreach (var name in new[] { "HOME", "TMPDIR", "DOTNET_PROCESSOR_COUNT", "DOTNET_CLI_TELEMETRY_OPTOUT", "DOTNET_NOLOGO" })
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        using var child = new Process { StartInfo = start };
        Task? wait = null, inputWrite = null;
        Task<Drain>? stdout = null, stderr = null;
        ProbeObservation? result = null;
        Exception? primary = null;
        var secondary = new List<Exception>();
        var forced = false;
        try
        {
            Assert.True(child.Start(), "The owned no-HTTP process did not start.");
            var identity = (child.Id, child.StartTime.ToUniversalTime().Ticks);
            wait = child.WaitForExitAsync();
            stdout = DrainAsync(child.StandardOutput.BaseStream, retain: true);
            stderr = DrainAsync(child.StandardError.BaseStream, retain: false);
            inputWrite = child.StandardInput.BaseStream.WriteAsync(bytes).AsTask();
            await inputWrite.WaitAsync(TimeSpan.FromSeconds(5));
            child.StandardInput.Close();
            await wait.WaitAsync(TimeSpan.FromSeconds(35));
            var outData = await stdout;
            var errData = await stderr;
            Assert.True(!outData.Overflow && !errData.Overflow && errData.Count == 0,
                "The owned process exceeded its closed stdout/stderr protocol.");
            using var report = JsonDocument.Parse(outData.Bytes);
            var r = report.RootElement;
            string[] expected = ["schemaVersion", "outcome", "failureKind", "preliminaryRetrievePerformed", "readerCalls", "createCalls", "renewCalls", "revokeCalls", "cleanupFailed"];
            Assert.True(r.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal)) &&
                r.GetProperty("schemaVersion").GetString() == "weft.session-ticket-process-result.v1",
                "The process reply did not match its closed versioned schema.");
            Assert.True((r.GetProperty("outcome").GetString() is "succeeded" or "refused" or "failed") &&
                !r.GetProperty("cleanupFailed").GetBoolean(), "The owned process cleanup or closed outcome was invalid.");
            var succeeded = r.GetProperty("outcome").GetString() == "succeeded";
            var refused = r.GetProperty("outcome").GetString() == "refused";
            var kind = r.GetProperty("failureKind").GetString();
            Assert.True(succeeded ? kind is null : kind is "ContextRequired" or "UntrustedIssuance" or "Denied" or
                "HandleCollision" or "VersionExhausted" or "UnsupportedFormat" or "IntegrityFailure" or
                "ConfigurationFailure" or "Unavailable" or "Canceled" or "ProbeFailure" or "CleanupFailure",
                "The process failure kind left its closed category set.");
            Assert.True(child.ExitCode == (succeeded ? 0 : 2), "The exact owned process exit disagrees with its closed reply.");
            result = new(identity, succeeded, refused, kind,
                r.GetProperty("preliminaryRetrievePerformed").GetBoolean(), r.GetProperty("readerCalls").GetInt32(),
                r.GetProperty("createCalls").GetInt32(), r.GetProperty("renewCalls").GetInt32(), r.GetProperty("revokeCalls").GetInt32(), File.Exists(output));
            if (succeeded)
            {
                Assert.True(new FileInfo(output).LinkTarget is null && new FileInfo(output).Length <= 8192 &&
                    File.GetUnixFileMode(output) == SessionTicketQualificationRoot.PrivateFile,
                    "The process private output did not preserve its fixed path/mode/length bound.");
                var privateBytes = await File.ReadAllBytesAsync(output);
                try
                {
                    using var privateReport = JsonDocument.Parse(privateBytes);
                    var p = privateReport.RootElement;
                    Assert.True(p.GetProperty("schemaVersion").GetString() == "weft.session-ticket-process-private.v1",
                        "The private process reply schema changed.");
                    result.LearnedKey = p.GetProperty("key").GetString(); result.Found = p.GetProperty("found").GetBoolean();
                    result.Canonical = p.GetProperty("canonical").GetBoolean(); result.Principal = p.GetProperty("principal").GetString();
                    result.Organization = p.GetProperty("organization").GetString(); result.Epoch = p.GetProperty("epoch").GetString();
                    result.IssuedUtc = p.GetProperty("issuedUtc").ValueKind == JsonValueKind.Null ? null : p.GetProperty("issuedUtc").GetDateTimeOffset();
                    result.ExpiresUtc = p.GetProperty("expiresUtc").ValueKind == JsonValueKind.Null ? null : p.GetProperty("expiresUtc").GetDateTimeOffset();
                }
                finally { CryptographicOperations.ZeroMemory(privateBytes); }
            }
        }
        catch (Exception error) { primary = TicketQualification.Safe(error, "The owned private process qualification failed."); }
        finally
        {
            try { child.StandardInput.Close(); } catch (Exception) { /* Start may itself have failed. */ }
            if (wait is not null)
            {
                try
                {
                    if (!child.HasExited) { forced = true; child.Kill(entireProcessTree: false); }
                    await wait;
                }
                catch (Exception) { secondary.Add(new InvalidOperationException("The exact owned probe did not settle.")); }
            }
            if (stdout is not null)
                try { await stdout; } catch (Exception) { secondary.Add(new InvalidOperationException("The owned stdout drain did not settle.")); }
            if (stderr is not null)
                try { await stderr; } catch (Exception) { secondary.Add(new InvalidOperationException("The owned stderr drain did not settle.")); }
            if (inputWrite is not null)
                try { await inputWrite; } catch (Exception) { /* The original write failure is already retained; observe actual settlement. */ }
            CryptographicOperations.ZeroMemory(bytes);
            if (forced) secondary.Add(new InvalidOperationException("The probe required forced cleanup and cannot qualify as ordinary exit."));
        }
        TicketQualification.Throw(primary, secondary);
        return result ?? throw new InvalidOperationException("The owned closed process observation is absent.");
    }

    private static async Task<Drain> DrainAsync(Stream stream, bool retain)
    {
        var chunk = new byte[512];
        using var kept = new MemoryStream();
        var count = 0;
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(chunk);
                if (read == 0) break;
                if (retain && count + read <= 1024) kept.Write(chunk, 0, read);
                count = Math.Min(16385, count + read);
            }
            return new(retain ? kept.ToArray() : [], count, count > (retain ? 1024 : 16384));
        }
        finally { CryptographicOperations.ZeroMemory(chunk); }
    }

    private sealed record Drain(byte[] Bytes, int Count, bool Overflow)
    {
        public override string ToString() => nameof(Drain);
    }

    private sealed class ProbeObservation((int, long) identity, bool succeeded, bool refused, string? failureKind, bool preliminary,
        int readers, int creates, int renews, int revokes, bool output)
    {
        internal (int, long) ProcessIdentity { get; } = identity;
        internal bool Succeeded { get; } = succeeded;
        internal bool Refused { get; } = refused;
        internal string? FailureKind { get; } = failureKind;
        internal bool PreliminaryRetrieve { get; } = preliminary;
        internal int ReaderCalls { get; } = readers;
        internal int CreateCalls { get; } = creates;
        internal int RenewCalls { get; } = renews;
        internal int RevokeCalls { get; } = revokes;
        internal bool PrivateOutputCreated { get; } = output;
        internal string? LearnedKey { get; set; }
        internal bool Found { get; set; }
        internal bool Canonical { get; set; }
        internal string? Principal { get; set; }
        internal string? Organization { get; set; }
        internal string? Epoch { get; set; }
        internal DateTimeOffset? IssuedUtc { get; set; }
        internal DateTimeOffset? ExpiresUtc { get; set; }
        public override string ToString() => nameof(ProbeObservation);
    }
}
