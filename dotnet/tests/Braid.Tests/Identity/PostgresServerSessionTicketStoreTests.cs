using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Npgsql;
using Braid.Application;
using Braid.Domain;
using Braid.Identity;
using Braid.Persistence.Postgres;
using Xunit.Sdk;

namespace Braid.Tests;

/// <summary>Actual restricted PG calls and certificate-backed DP; no cookie handler, HTTP or provider login.</summary>
[SupportedOSPlatform("linux")]
[Collection(PostgresCollection.Name)]
public sealed class PostgresServerSessionTicketStoreTests(PostgresFixture fixture)
{
    [PostgresFact]
    public Task RealProtectedCreationAndFreshRetrievalKeepOnlyCanonicalIdentityAndDatabaseTimes() =>
        TicketQualification.RunAsync(fixture, async test =>
        {
            var parents = await test.Data.AuthorityAsync();
            var key = await test.IssueAsync();
            var intent = test.Writer.LastCreate!;
            var current = await test.Data.Reader.ObserveAsync(intent.Key);
            Assert.True(current is not null && current.PrincipalId == test.Data.Principal,
                "Real creation did not preserve the original private owner.");
            var first = await test.Adapter.RetrieveAsync(key, new DefaultHttpContext(), CancellationToken.None);
            var second = await test.Adapter.RetrieveAsync(key, new DefaultHttpContext(), CancellationToken.None);
            TicketQualification.Canonical(first, current!);
            TicketQualification.Canonical(second, current!);
            Assert.True(!ReferenceEquals(first, second) && !ReferenceEquals(first!.Principal, second!.Principal),
                "Fresh retrieval reused a mutable identity graph.");
            Assert.True(test.Writer.CreateCalls == 1 && test.Writer.RenewCalls == 0 && test.Reader.Calls == 2,
                "Retrieval did not remain a fresh read without renewal.");
            var before = await test.Data.RowsAsync();
            using var foreign = DurableSessionKeyProfile.Open(test.Files.Options(test.Data.OtherOrganization),
                test.Files.Certificate, []);
            ITicketStore other = new ServerSessionTicketStore(foreign, test.Data.Reader, test.Data.Writer);
            Assert.True(await other.RetrieveAsync(key, new DefaultHttpContext(), CancellationToken.None) is null,
                "Another configured organization acquired private identity.");
            PostgresServerSessionTestSupport.SamePrivate(before, await test.Data.RowsAsync());
            PostgresServerSessionTestSupport.SamePrivate(parents, await test.Data.AuthorityAsync());
            test.Files.AssertEncryptedRing();
        });

    [PostgresFact]
    public Task FreshBindingAndPrincipalRefusalsNeitherCacheIdentityNorSeedAuthority() =>
        TicketQualification.RunAsync(fixture, async test =>
        {
            var first = await test.IssueAsync();
            var firstIntent = test.Writer.LastCreate!;
            await test.Data.Case.ExecuteAsync("UPDATE braid.principals SET active=false WHERE id=$1", test.Data.Principal.Value);
            Assert.True(await test.Adapter.RetrieveAsync(first, new DefaultHttpContext(), CancellationToken.None) is null,
                "A repeated retrieval retained an inactive principal.");
            var reads = test.Reader.Calls;
            await test.Adapter.RemoveAsync(first, new DefaultHttpContext(), CancellationToken.None);
            Assert.True(test.Reader.Calls == reads && test.Writer.RevokeCalls == 1,
                "Revocation introduced a live-reader prerequisite or a second write.");
            await test.Data.Case.ExecuteAsync("UPDATE braid.principals SET active=true WHERE id=$1", test.Data.Principal.Value);
            Assert.True(await test.Adapter.RetrieveAsync(first, new DefaultHttpContext(), CancellationToken.None) is null,
                "Principal restoration revived an acknowledged tombstone.");
            var tombstone = await test.Data.StoredAsync(firstIntent.Key);
            Assert.True(tombstone.Version == 2 && tombstone.RevokedAtUtc is not null,
                "The exact original session lacks its real tombstone.");
            var live = await test.IssueAsync();
            var before = await test.Data.RowsAsync();
            var disabled = await test.Data.IdentityOwner.DisableAsync(test.Data.Binding.Key,
                test.Data.Principal, test.Data.Binding.Version);
            Assert.True(disabled.Outcome == ExternalIdentityBindingOwnerOutcome.Disabled,
                "The owned binding was not actually disabled.");
            Assert.True(await test.Adapter.RetrieveAsync(live, new DefaultHttpContext(), CancellationToken.None) is null,
                "Fresh retrieval retained a disabled binding version.");
            await TicketQualification.FailureAsync(ServerSessionIdentityFailureKind.Denied, () => test.IssueAsync());
            Assert.True(test.Writer.CreateCalls == 3, "Denied creation retried or skipped its real write admission.");
            PostgresServerSessionTestSupport.SamePrivate(before, await test.Data.RowsAsync());
        });

    [PostgresFact]
    public Task InitialStaleBindingAndWrongExpectedOwnerCannotProduceAnAcknowledgedIdentity() =>
        TicketQualification.RunAsync(fixture, async test =>
        {
            var before = await test.Data.RowsAsync();
            var parents = await test.Data.AuthorityAsync();
            await TicketQualification.FailureAsync(ServerSessionIdentityFailureKind.Denied,
                () => test.IssueAsync(bindingVersion: test.Data.Binding.Version + 1));
            // The port uses the same fixed InvalidOperationException category
            // for this integrity refusal and unavailable I/O. Do not parse it.
            await TicketQualification.FailureAsync(ServerSessionIdentityFailureKind.Unavailable,
                () => test.IssueAsync(owner: new PrincipalId(Guid.NewGuid())));
            Assert.True(test.Writer.CreateCalls == 2, "An initial refusal retried or adopted an existing handle.");
            PostgresServerSessionTestSupport.SamePrivate(before, await test.Data.RowsAsync());
            PostgresServerSessionTestSupport.SamePrivate(parents, await test.Data.AuthorityAsync());
        });

    [PostgresFact]
    public Task AlreadyCanceledContextualCallsLeaveActualRowsAndRestrictedPortsUntouched() =>
        TicketQualification.RunAsync(fixture, async test =>
        {
            var key = await test.IssueAsync();
            var ticket = await test.Adapter.RetrieveAsync(key, new DefaultHttpContext(), CancellationToken.None);
            Assert.True(ticket is not null, "The cancellation setup lacks its real canonical ticket.");
            var before = await test.Data.RowsAsync();
            var reads = test.Reader.Calls;
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            var context = new DefaultHttpContext();
            using var scope = test.Store.BeginTrustedIssuance(context, test.Data.Binding.BindingId,
                test.Data.Binding.Version, test.Data.Principal, await test.Data.DeadlineAsync(), CancellationToken.None);
            Func<Task>[] calls = [
                () => test.Adapter.StoreAsync(scope.CopySignInTicket(), context, canceled.Token),
                () => test.Adapter.RetrieveAsync(key, new DefaultHttpContext(), canceled.Token),
                () => test.Adapter.RenewAsync(key, ticket!, new DefaultHttpContext(), canceled.Token),
                () => test.Adapter.RemoveAsync(key, new DefaultHttpContext(), canceled.Token)];
            foreach (var call in calls)
            {
                Exception? failure = null;
                try { await call(); } catch (Exception error) { failure = error; }
                Assert.True(failure is OperationCanceledException operation && operation.CancellationToken == canceled.Token,
                    "A canceled actual-port composition did not preserve the original caller token.");
            }
            Assert.True(test.Reader.Calls == reads && test.Writer.CreateCalls == 1 && test.Writer.RenewCalls == 0 && test.Writer.RevokeCalls == 0,
                "An already canceled call reached a real restricted store port.");
            PostgresServerSessionTestSupport.SamePrivate(before, await test.Data.RowsAsync());
        });

    [PostgresFact]
    public Task ConcurrentRenewalsUseOneActualCasEachAndSeparatelyObserveTheWinningIdentity() =>
        TicketQualification.RunAsync(fixture, async test =>
        {
            var key = await test.IssueAsync();
            var original = await test.Data.StoredAsync(test.Writer.LastCreate!.Key);
            var ticket = await test.Adapter.RetrieveAsync(key, new DefaultHttpContext(), CancellationToken.None);
            Assert.True(ticket is not null, "The real renewal setup has no canonical ticket.");
            var parents = await test.Data.AuthorityAsync();
            test.Reader.ArmPair();
            await Task.WhenAll(
                test.Adapter.RenewAsync(key, ticket!, new DefaultHttpContext(), CancellationToken.None),
                test.Adapter.RenewAsync(key, ticket!, new DefaultHttpContext(), CancellationToken.None));
            var after = await test.Data.StoredAsync(original.Key);
            PostgresServerSessionTestSupport.SameImmutable(original, after);
            Assert.True(after.Version == original.Version + 1 &&
                after.Lifetime.IdleExpiresAtUtc > original.Lifetime.IdleExpiresAtUtc,
                "Concurrent renewal did not retain one actual forward transition.");
            Assert.True(test.Writer.RenewCalls == 2 &&
                test.Writer.RenewOutcomes.Count(v => v == ServerSessionWriteOutcome.Renewed) == 1 &&
                test.Writer.RenewOutcomes.Count(v => v == ServerSessionWriteOutcome.Conflict) == 1,
                "A stale renewal retried instead of separately observing the winning state.");
            Assert.True(test.Reader.PairedReads == 2 && test.Reader.Calls == 4,
                "Renewal did not use two first observations and one distinct conflict observation.");
            PostgresServerSessionTestSupport.SamePrivate(parents, await test.Data.AuthorityAsync());
            await test.Adapter.RemoveAsync(key, new DefaultHttpContext(), CancellationToken.None);
            var revoked = await test.Data.StoredAsync(original.Key);
            var writes = test.Writer.RenewCalls;
            await TicketQualification.FailureAsync(ServerSessionIdentityFailureKind.Denied, () =>
                test.Adapter.RenewAsync(key, ticket!, new DefaultHttpContext(), CancellationToken.None));
            Assert.True(test.Writer.RenewCalls == writes && revoked.RevokedAtUtc is not null,
                "A revoked observation dispatched renewal or revived the row.");
        });

    [PostgresFact]
    public Task ClippedNoopAndExpiredHistoricalMetadataPreserveBlobsWithoutResurrection() =>
        TicketQualification.RunAsync(fixture, async test =>
        {
            var key = await test.IssueAsync(deadline: await test.Data.DeadlineAsync(5));
            var facts = await test.Data.StoredAsync(test.Writer.LastCreate!.Key);
            var ticket = await test.Adapter.RetrieveAsync(key, new DefaultHttpContext(), CancellationToken.None);
            Assert.True(ticket is not null, "The real clipped setup has no canonical ticket.");
            await test.Adapter.RenewAsync(key, ticket!, new DefaultHttpContext(), CancellationToken.None);
            PostgresServerSessionTestSupport.SameFacts(facts, await test.Data.StoredAsync(facts.Key));
            Assert.True(test.Writer.RenewCalls == 1 && test.Writer.RenewOutcomes.Single() == ServerSessionWriteOutcome.Unchanged,
                "The clipped no-op changed version or repeated its write.");

            // Owner-authored past metadata, under ALL original guards. This is not
            // a claim that CreateOnly ran thirty minutes ago or wall time elapsed.
            var historical = ServerSessionHandle.Create();
            var historicalKey = new ServerSessionKey(test.Data.Organization, historical.Digest);
            var deadline = await test.Data.DeadlineAsync();
            var envelope = new ServerSessionEnvelope(historicalKey, test.Data.Binding.BindingId,
                test.Data.Binding.Version, test.Data.Principal, Guid.NewGuid(), deadline);
            var protectedBytes = new ServerSessionTicketCodec(test.Profile).Protect(envelope, CancellationToken.None);
            try
            {
                await using var owner = PostgresServerSessionTestSupport.Unpooled(test.Data.Case.MigrationConnectionString);
                await owner.OpenAsync();
                await using var insert = PostgresServerSessionTestSupport.HistoricalInsert(owner, null, historicalKey,
                    test.Data.Binding, envelope.Epoch, protectedBytes, ageMinutes: 30, absoluteMinutes: 60);
                Assert.True(await insert.ExecuteNonQueryAsync() == 1, "The guarded historical fixture was not inserted.");
            }
            finally { CryptographicOperations.ZeroMemory(protectedBytes); }
            var before = await test.Data.StoredAsync(historicalKey);
            Assert.True(await test.Adapter.RetrieveAsync(historical.ExportKey(), new DefaultHttpContext(), CancellationToken.None) is null,
                "Expired metadata was accepted as live identity.");
            var reads = test.Reader.Calls;
            await test.Adapter.RemoveAsync(historical.ExportKey(), new DefaultHttpContext(), CancellationToken.None);
            Assert.True(test.Reader.Calls == reads, "Expired revocation incorrectly required a live read.");
            var after = await test.Data.StoredAsync(historicalKey);
            PostgresServerSessionTestSupport.SameImmutable(before, after);
            Assert.True(after.Version == 2 && after.RevokedAtUtc is not null,
                "The expired exact row did not retain its real one-way revocation.");
        });

    [PostgresFact]
    public Task ExplicitOpaqueNegativeRowsNeverTurnMalformedOrForeignProtectedEnvelopesIntoIdentity() =>
        TicketQualification.RunAsync(fixture, async test =>
        {
            var parents = await test.Data.AuthorityAsync();
            for (var variant = 0; variant < 3; variant++)
            {
                var handle = ServerSessionHandle.Create();
                var key = new ServerSessionKey(test.Data.Organization, handle.Digest);
                var deadline = await test.Data.DeadlineAsync();
                byte[] bytes;
                Guid epoch;
                if (variant == 0) { bytes = [1, 2, 3]; epoch = Guid.NewGuid(); }
                else
                {
                    var org = variant == 1 ? test.Data.Organization : test.Data.OtherOrganization;
                    using var profile = DurableSessionKeyProfile.Open(test.Files.Options(org), test.Files.Certificate, []);
                    var envelope = new ServerSessionEnvelope(new(org, handle.Digest), test.Data.Binding.BindingId,
                        test.Data.Binding.Version, variant == 1 ? new PrincipalId(Guid.NewGuid()) : test.Data.Principal,
                        Guid.NewGuid(), deadline);
                    epoch = envelope.Epoch;
                    bytes = new ServerSessionTicketCodec(profile).Protect(envelope, CancellationToken.None);
                }
                try
                {
                    // Deliberately opaque negative input: genuine writer ACK is
                    // persistence only, never a fabricated usable ticket result.
                    var intent = new ServerSessionCreateIntent(key, test.Data.Binding.BindingId, test.Data.Binding.Version,
                        test.Data.Principal, epoch, deadline, bytes);
                    var stored = await test.Data.Writer.CreateOnlyAsync(intent);
                    Assert.True(stored.Outcome == ServerSessionWriteOutcome.Created,
                        "The explicit opaque-negative row was not genuinely committed.");
                }
                finally { CryptographicOperations.ZeroMemory(bytes); }
                var before = await test.Data.RowsAsync();
                await TicketQualification.FailureAsync(ServerSessionIdentityFailureKind.IntegrityFailure, () =>
                    test.Adapter.RetrieveAsync(handle.ExportKey(), new DefaultHttpContext(), CancellationToken.None));
                PostgresServerSessionTestSupport.SamePrivate(before, await test.Data.RowsAsync());
            }
            Assert.True(test.Writer.CreateCalls == 0 && test.Writer.RenewCalls == 0 && test.Writer.RevokeCalls == 0,
                "A refused protected read performed an adapter mutation.");
            PostgresServerSessionTestSupport.SamePrivate(parents, await test.Data.AuthorityAsync());
        });

    [PostgresFact]
    public Task RealProtectedCreateCommitLossFailsWithoutIssuingAHandleOrRetrying() => LostAckAsync(revoke: false);

    [PostgresFact]
    public Task RealProtectedRevokeCommitLossFailsDespiteItsSeparatelyObservedTombstone() => LostAckAsync(revoke: true);

    private Task LostAckAsync(bool revoke) => TicketQualification.RunAsync(fixture, async test =>
    {
        var learned = revoke ? await test.IssueAsync() : null;
        var before = revoke ? await test.Data.StoredAsync(test.Writer.LastCreate!.Key) : null;
        var parents = await test.Data.AuthorityAsync();
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        ServerSessionCommitAckLossRelay? relay = null;
        PostgresServerSessionDatabase? routedDatabase = null;
        var context = new DefaultHttpContext();
        TrustedSessionIssuance? issuance = null;
        Task? operation = null;
        Exception? primary = null;
        var cleanup = new List<Exception>();
        try
        {
            relay = new(test.Data.Case.SessionWriterConnectionString);
            var routedString = new NpgsqlConnectionStringBuilder(relay.RoutedConnectionString) { Timezone = "Etc/UTC" };
            routedDatabase = new(routedString.ConnectionString, ServerSessionDatabaseProfile.Writer);
            var writer = new TicketCountingWriter(new PostgresServerSessionWriter(routedDatabase));
            ITicketStore adapter = new ServerSessionTicketStore(test.Profile, test.Data.Reader, writer);
            if (revoke) operation = adapter.RemoveAsync(learned!, context, caller.Token);
            else
            {
                issuance = ((ServerSessionTicketStore)adapter).BeginTrustedIssuance(context,
                    test.Data.Binding.BindingId, test.Data.Binding.Version, test.Data.Principal,
                    await test.Data.DeadlineAsync(), caller.Token);
                operation = adapter.StoreAsync(issuance.CopySignInTicket(), context, caller.Token);
            }
            var originalOperation = operation ?? throw new InvalidOperationException("The exact protected operation is absent.");
            await Task.WhenAny(originalOperation, relay.CommitAcknowledgementWithheld).WaitAsync(TimeSpan.FromSeconds(10), caller.Token);
            Assert.True(relay.CommitAcknowledgementWithheld.IsCompletedSuccessfully && !originalOperation.IsCompleted,
                "The real protected operation did not reach the withheld commit gate.");
            await relay.CommitAcknowledgementWithheld;
            Assert.True(relay.FrontendCommitSeen && relay.BackendCommitCompleteSeen && relay.BackendIdleReadySeen
                && relay.ParameterStatusFramesWithheld is > 0 and <= 16,
                "Actual bounded COMMIT/status/idle frames were not all withheld.");
            var key = revoke ? before!.Key : writer.LastCreate!.Key;
            var durable = await TicketQualification.DurableAsync(test.Data.Case, key, caller.Token);
            Assert.True(durable.Total == 1 && durable.Version == (revoke ? 2 : 1) && durable.Revoked == revoke,
                "An independent unpooled owner did not see the exact durable transition.");
            var intent = revoke ? test.Writer.LastCreate! : writer.LastCreate!;
            Assert.True(durable.Facts.BindingId == intent.BindingId && durable.Facts.BindingVersion == intent.BindingVersion
                && durable.Facts.LoginEpoch == intent.LoginEpoch && durable.Facts.CopyProtectedTicket().AsSpan()
                    .SequenceEqual(intent.CopyProtectedTicket()), "The durable protected row differs from its private intent.");
            Assert.True(durable.Facts.Lifetime == ServerSessionPolicy.InitialLifetime(durable.Facts.Lifetime.IssuedAtUtc,
                intent.VerifiedLoginDeadlineUtc), "The durable protected row changed its database-selected lifetime.");
            if (before is not null) PostgresServerSessionTestSupport.SameImmutable(before, durable.Facts);
            Assert.True(!originalOperation.IsCompleted && !caller.IsCancellationRequested,
                "The operation settled before deliberate acknowledgement loss.");
            relay.DropAcknowledgement();
            await TicketQualification.FailureAsync(ServerSessionIdentityFailureKind.Unavailable, () => originalOperation);
            await relay.Completion.WaitAsync(TimeSpan.FromSeconds(5), caller.Token);
            Assert.True(relay.RequestedDropAccepted && !caller.IsCancellationRequested &&
                writer.CreateCalls == (revoke ? 0 : 1) && writer.RevokeCalls == (revoke ? 1 : 0),
                "Acknowledgement loss was replaced by cancellation or automatic retry.");
            var after = await TicketQualification.DurableAsync(test.Data.Case, key, caller.Token);
            PostgresServerSessionTestSupport.SameFacts(durable.Facts, after.Facts);
            var observed = await test.Data.Reader.ObserveAsync(key, caller.Token);
            Assert.True(revoke ? observed is null : observed is not null && observed.PrincipalId == test.Data.Principal,
                "Separate known-key reconciliation did not match the real retained state.");
            PostgresServerSessionTestSupport.SamePrivate(parents, await test.Data.AuthorityAsync());
            // No Store result is exported or adopted after failed creation.
            // This owner metadata observation does not retroactively acknowledge it.
        }
        catch (Exception error) { primary = TicketQualification.Safe(error, "Protected commit-loss qualification failed."); }
        finally
        {
            relay?.DropAcknowledgement();
            try { caller.Cancel(); } catch (Exception) { cleanup.Add(new InvalidOperationException("Owned cancellation failed.")); }
            if (relay is not null)
                try { await relay.DisposeAsync(); } catch (Exception) { cleanup.Add(new InvalidOperationException("Owned relay did not settle.")); }
            if (operation is not null)
                try { await operation; } catch (Exception) { /* Observe the exact call before pool/case disposal. */ }
            try { issuance?.Dispose(); } catch (Exception) { cleanup.Add(new InvalidOperationException("Owned issuance disposal failed.")); }
            if (routedDatabase is not null)
                try { await routedDatabase.DisposeAsync(); } catch (Exception) { cleanup.Add(new InvalidOperationException("Owned routed pool disposal failed.")); }
        }
        TicketQualification.Throw(primary, cleanup);
    });
}

[SupportedOSPlatform("linux")]
internal sealed class TicketQualification
{
    internal PostgresServerSessionTestSupport Data { get; }
    internal SessionTicketQualificationRoot Files { get; }
    internal DurableSessionKeyProfile Profile { get; }
    internal TicketCountingReader Reader { get; }
    internal TicketCountingWriter Writer { get; }
    internal ServerSessionTicketStore Store { get; }
    internal ITicketStore Adapter => Store;

    private TicketQualification(PostgresServerSessionTestSupport data, SessionTicketQualificationRoot files,
        DurableSessionKeyProfile profile)
    {
        Data = data; Files = files; Profile = profile;
        Reader = new(data.Reader); Writer = new(data.Writer);
        Store = new(profile, Reader, Writer);
    }

    internal static async Task RunAsync(PostgresFixture fixture, Func<TicketQualification, Task> body)
    {
        PostgresServerSessionTestSupport? data = null;
        SessionTicketQualificationRoot? files = null;
        DurableSessionKeyProfile? profile = null;
        Exception? primary = null;
        var secondary = new List<Exception>();
        try
        {
            data = await PostgresServerSessionTestSupport.CreateAsync(fixture);
            files = new();
            using (DurableSessionKeyProfile.BootstrapEmpty(files.Options(data.Organization), files.Certificate, [],
                DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(90))) { }
            profile = DurableSessionKeyProfile.Open(files.Options(data.Organization), files.Certificate, []);
            await body(new(data, files, profile));
        }
        catch (Exception error) { primary = Safe(error, "Owned PG and protected-ticket qualification failed."); }
        finally
        {
            try { profile?.Dispose(); } catch (Exception) { secondary.Add(new InvalidOperationException("Owned profile disposal failed.")); }
            if (data is not null)
                try { await data.DisposeAsync(); } catch (Exception) { secondary.Add(new InvalidOperationException("Owned PostgreSQL case disposal failed.")); }
            try { files?.Dispose(); } catch (Exception) { secondary.Add(new InvalidOperationException("Owned private key fixture disposal failed.")); }
        }
        Throw(primary, secondary);
    }

    internal async Task<string> IssueAsync(DateTimeOffset? deadline = null, long? bindingVersion = null, PrincipalId? owner = null)
    {
        var context = new DefaultHttpContext();
        using var issuance = Store.BeginTrustedIssuance(context, Data.Binding.BindingId,
            bindingVersion ?? Data.Binding.Version, owner ?? Data.Principal,
            deadline ?? await Data.DeadlineAsync(), CancellationToken.None);
        return await Adapter.StoreAsync(issuance.CopySignInTicket(), context, CancellationToken.None);
    }

    internal static void Canonical(AuthenticationTicket? ticket, CurrentServerSession current)
    {
        Assert.True(ticket is not null, "Fresh protected retrieval did not return a canonical ticket.");
        var identities = ticket!.Principal.Identities.ToArray();
        var claims = ticket.Principal.Claims.ToArray();
        Assert.True(identities.Length == 1 && claims.Length == 3 &&
            ticket.AuthenticationScheme == ServerSessionSchemes.Session &&
            identities[0].AuthenticationType == ServerSessionSchemes.Session &&
            identities[0].Actor is null && identities[0].BootstrapContext is null &&
            claims.All(c => c.Issuer == ServerSessionSchemes.Session && c.OriginalIssuer == ServerSessionSchemes.Session && c.Properties.Count == 0) &&
            claims.Count(c => c.Type == ClaimTypes.NameIdentifier && c.Value == current.PrincipalId.Value.ToString("D")) == 1 &&
            claims.Count(c => c.Type == "braid:organization_id" && c.Value == current.Facts.Key.OrganizationId.ToString("D")) == 1 &&
            claims.Count(c => c.Type == "braid:login_epoch" && c.Value == current.Facts.LoginEpoch.ToString("D")) == 1,
            "A protected retrieval retained foreign or extra identity facts.");
        Assert.True(ticket.Properties.IssuedUtc == ServerSessionCanonicalTicket.ToFrameworkTime(current.Facts.Lifetime.IssuedAtUtc) &&
            ticket.Properties.ExpiresUtc == ServerSessionCanonicalTicket.ToFrameworkTime(current.Facts.Lifetime.AbsoluteExpiresAtUtc) &&
            !ticket.Properties.IsPersistent && ticket.Properties.AllowRefresh == false && ticket.Properties.Parameters.Count == 0,
            "Canonical framework properties do not project the actual database times.");
    }

    internal static async Task FailureAsync(ServerSessionIdentityFailureKind kind, Func<Task> action)
    {
        Exception? error = null;
        try { await action(); } catch (Exception value) { error = value; }
        Assert.True(error is ServerSessionIdentityException identity && identity.Kind == kind && identity.InnerException is null,
            "The operation did not retain its exact categorical private-safe failure.");
    }

    internal static Exception Safe(Exception error, string message) => error is XunitException ? error : new InvalidOperationException(message);
    internal static void Throw(Exception? primary, List<Exception> secondary)
    {
        if (primary is not null)
        {
            if (secondary.Count != 0) throw new AggregateException("Qualification and owned cleanup failed.", new[] { primary }.Concat(secondary));
            ExceptionDispatchInfo.Capture(primary).Throw();
        }
        if (secondary.Count != 0) throw new AggregateException("Owned qualification cleanup failed.", secondary);
    }

    internal static async Task<DurableSessionSnapshot> DurableAsync(PostgresCase test, ServerSessionKey key, CancellationToken token)
    {
        await using var owner = PostgresServerSessionTestSupport.Unpooled(test.MigrationConnectionString);
        await owner.OpenAsync(token);
        await using var command = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM ONLY braid.server_sessions),binding_id,binding_version,login_epoch,
                issued_at,idle_expires_at,absolute_expires_at,version,revoked_at,policy_version,codec_version,protected_ticket
            FROM ONLY braid.server_sessions WHERE organization_id=$1 AND handle_digest=$2
            """, owner) { CommandTimeout = 3 };
        command.Parameters.AddWithValue(key.OrganizationId);
        command.Parameters.AddWithValue(Convert.FromHexString(key.HandleDigest.Value));
        await using var row = await command.ExecuteReaderAsync(token);
        Assert.True(await row.ReadAsync(token), "Independent exact durable metadata is absent.");
        var total = row.GetInt64(0);
        var facts = new ServerSessionFacts(key, row.GetGuid(1), row.GetInt64(2), row.GetGuid(3),
            new(row.GetFieldValue<DateTimeOffset>(4), row.GetFieldValue<DateTimeOffset>(5), row.GetFieldValue<DateTimeOffset>(6)),
            row.GetInt64(7), row.GetFieldValue<byte[]>(11), row.IsDBNull(8) ? null : row.GetFieldValue<DateTimeOffset>(8),
            row.GetInt32(9), row.GetInt32(10));
        Assert.True(!await row.ReadAsync(token), "Independent exact durable metadata is duplicated.");
        return new(total, facts);
    }
}

internal sealed record DurableSessionSnapshot(long Total, ServerSessionFacts Facts)
{
    internal long Version => Facts.Version;
    internal bool Revoked => Facts.RevokedAtUtc is not null;
    public override string ToString() => nameof(DurableSessionSnapshot);
}

internal sealed class TicketCountingWriter(IServerSessionWriter actual) : IServerSessionWriter
{
    private int createCalls, renewCalls, revokeCalls;
    internal int CreateCalls => Volatile.Read(ref createCalls);
    internal int RenewCalls => Volatile.Read(ref renewCalls);
    internal int RevokeCalls => Volatile.Read(ref revokeCalls);
    internal ServerSessionCreateIntent? LastCreate { get; private set; }
    internal ConcurrentQueue<ServerSessionWriteOutcome> RenewOutcomes { get; } = new();
    public Task<ServerSessionWriteResult> CreateOnlyAsync(ServerSessionCreateIntent intent, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref createCalls); LastCreate = intent;
        return actual.CreateOnlyAsync(intent, cancellationToken);
    }
    public async Task<ServerSessionWriteResult> RenewAsync(ServerSessionKey key, long expectedVersion, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref renewCalls);
        var result = await actual.RenewAsync(key, expectedVersion, cancellationToken);
        RenewOutcomes.Enqueue(result.Outcome); return result;
    }
    public Task<ServerSessionWriteResult> RevokeAsync(ServerSessionKey key, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref revokeCalls); return actual.RevokeAsync(key, cancellationToken);
    }
}

internal sealed class TicketCountingReader(IServerSessionReader actual) : IServerSessionReader
{
    private int calls, paired, armed;
    private readonly TaskCompletionSource pair = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int Calls => Volatile.Read(ref calls);
    internal int PairedReads => Volatile.Read(ref paired);
    internal void ArmPair() => Volatile.Write(ref armed, 1);
    public async Task<CurrentServerSession?> ObserveAsync(ServerSessionKey key, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref calls);
        var current = await actual.ObserveAsync(key, cancellationToken);
        // The real reader's transaction has settled before either caller waits here.
        if (Volatile.Read(ref armed) == 1)
        {
            if (Interlocked.Increment(ref paired) == 2) { Volatile.Write(ref armed, 0); pair.TrySetResult(); }
            await pair.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }
        return current;
    }
}

[SupportedOSPlatform("linux")]
internal sealed class SessionTicketQualificationRoot : IDisposable
{
    internal string Root { get; }
    internal string Keys { get; }
    internal string ApplicationName => "Braid owned durable session qualification";
    internal X509Certificate2 Certificate { get; }
    internal string CertificatePath { get; }
    internal string Password { get; }
    internal const UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    internal const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    internal SessionTicketQualificationRoot()
    {
        Root = Path.Combine(Path.GetTempPath(), "braid-ticket-qualification-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root, PrivateDirectory);
        Keys = Path.Combine(Root, "keys");
        X509Certificate2? certificate = null;
        try
        {
            Directory.CreateDirectory(Keys, PrivateDirectory);
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=Braid owned qualification", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(100));
            var password = RandomNumberGenerator.GetBytes(24);
            try { Password = Convert.ToBase64String(password); }
            finally { CryptographicOperations.ZeroMemory(password); }
            CertificatePath = Path.Combine(Root, "current.pfx");
            var pfx = certificate.Export(X509ContentType.Pfx, Password);
            try { WritePrivate(CertificatePath, pfx); }
            finally { CryptographicOperations.ZeroMemory(pfx); }
            Certificate = certificate;
        }
        catch (Exception)
        {
            var failures = new List<Exception> { new InvalidOperationException("Owned private key fixture construction failed.") };
            try { certificate?.Dispose(); } catch (Exception) { failures.Add(new InvalidOperationException("Owned certificate settlement failed.")); }
            try { Directory.Delete(Root, recursive: true); } catch (Exception) { failures.Add(new InvalidOperationException("Owned partial key root settlement failed.")); }
            throw new AggregateException("Private key fixture construction and settlement did not complete.", failures);
        }
    }

    internal ServerSessionKeyProfileOptions Options(Guid organization, string? app = null, string? directory = null) =>
        new(organization, app ?? ApplicationName, directory ?? Keys);

    internal static void WritePrivate(string path, ReadOnlySpan<byte> bytes)
    {
        using var output = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = PrivateFile
        });
        output.Write(bytes);
    }

    internal string RingSnapshot() => JsonSerializer.Serialize(Directory.GetFiles(Keys).Order(StringComparer.Ordinal)
        .Select(p => new { Name = Path.GetFileName(p), Mode = (int)File.GetUnixFileMode(p), Digest = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p))) }));

    internal void AssertEncryptedRing()
    {
        Assert.True(File.GetUnixFileMode(Keys) == PrivateDirectory && new DirectoryInfo(Keys).LinkTarget is null,
            "The owned durable key directory is not private and direct.");
        var files = Directory.GetFiles(Keys);
        Assert.True(files.Length > 0 && Directory.GetDirectories(Keys).Length == 0,
            "The owned key repository has an unexpected child shape.");
        foreach (var file in files)
        {
            Assert.True(new FileInfo(file).LinkTarget is null,
                "A durable key file became an indirect path.");
            var document = XDocument.Load(file);
            Assert.True(document.Descendants().Any(v => v.Name.LocalName == "encryptedSecret"),
                "A persisted key is not protected by the standard certificate XML wrapper.");
            // Exact framework-created file modes are measured in RingSnapshot,
            // not assumed to have a particular numeric value before execution.
        }
    }

    public void Dispose()
    {
        Certificate.Dispose();
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}
