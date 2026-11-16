using System.Net;
using System.Runtime.Versioning;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Braid.Application;
using Braid.Identity;

namespace Braid.Tests;

/// <summary>
/// Source candidate only. Actual Kestrel TLS, public112 composition, real106
/// restricted ports and standard certificate-backed DP. No112 implementation,
/// internal handle/codec/provider bridge, product endpoint or browser activation.
/// </summary>
[SupportedOSPlatform("linux")]
[Collection(PostgresCollection.Name)]
public sealed class ServerSessionBrowserHttpsTests(PostgresFixture postgres)
{
    [PostgresFact]
    public Task SameOwnerFreshIssueRevokesBeforeNewStoreAndOldTokenFailsSpecificallyItsEpoch() =>
        BrowserHttpsFixture.RunAsync(postgres, async test =>
        {
            var authority = await test.Data.AuthorityAsync();
            var cookies = new BrowserCookies();
            await IssueAsync(test.First, cookies);
            var original = RequiredCreate(test.First);
            var originalFacts = await test.Data.StoredAsync(original.Key);
            var oldToken = await TokenAsync(test.First, cookies);
            var oldCookies = cookies.Copy();
            var oldObservation = await test.Second.SendAsync("/observe", oldCookies);
            BrowserHttpsFixture.Kind(oldObservation, ServerSessionRequestKind.Authenticated);
            BrowserHttpsFixture.Identity(oldObservation, test.Data.Principal, test.Data.Organization, original.LoginEpoch);
            await AcceptedUnsafeAsync(test.Second, cookies, oldToken);

            var sawCommittedTombstoneBeforeCreate = false;
            test.First.Writer.BeforeCreate = async (intent, cancellation) =>
            {
                sawCommittedTombstoneBeforeCreate = intent.Key != original.Key &&
                    await test.IndependentlyRevokedAsync(original.Key, cancellation);
            };
            var reissue = await test.First.SendAsync("/issue", cookies, method: "POST");
            Assert.True(reissue.Status == HttpStatusCode.NoContent && !reissue.HasLocation,
                "Fresh issuance did not acknowledge without redirect.");
            SessionAttributes(reissue);
            Assert.True(reissue.SetCookies.All(cookie => cookie.Name != BrowserHttpsFixture.AntiforgeryCookie),
                "Fresh issuance replaced the existing AF cookie.");
            cookies.Apply(reissue);
            var fresh = RequiredCreate(test.First);
            Assert.True(sawCommittedTombstoneBeforeCreate && test.First.Writer.RevokeAcknowledgementSequence > 0 &&
                test.First.Writer.RevokeAcknowledgementSequence < test.First.Writer.CreateDispatchSequence &&
                test.First.Writer.Creates == 2 && test.First.Writer.Revokes == 1 && test.First.Writer.Renews == 0 &&
                fresh.Key != original.Key && fresh.LoginEpoch != original.LoginEpoch &&
                string.Equals(cookies.Antiforgery, oldCookies.Antiforgery, StringComparison.Ordinal),
                "Fresh issuance did not retain actual revoke-ACK-before-Store and independent identity.");
            var tombstone = await test.Data.StoredAsync(original.Key);
            PostgresServerSessionTestSupport.SameImmutable(originalFacts, tombstone);
            Assert.True(tombstone.Version == 2 && tombstone.RevokedAtUtc is not null && await test.RowCountAsync() == 2,
                "Fresh issuance did not preserve the exact old tombstone and one new session.");
            foreach (var host in new[] { test.First, test.Second })
            {
                BrowserHttpsFixture.Kind(await host.SendAsync("/observe", oldCookies), ServerSessionRequestKind.AuthenticationRequired);
                var newObservation = await host.SendAsync("/observe", cookies);
                BrowserHttpsFixture.Kind(newObservation, ServerSessionRequestKind.Authenticated);
                BrowserHttpsFixture.Identity(newObservation, test.Data.Principal, test.Data.Organization, fresh.LoginEpoch);
                var rejected = await host.SendAsync("/admit", cookies, "POST", oldToken, body: "untouched"u8.ToArray());
                BrowserHttpsFixture.Kind(rejected, ServerSessionRequestKind.CsrfRejected);
                BrowserHttpsFixture.NoBodyOrDispatch(rejected);
                Assert.True(rejected.Status == HttpStatusCode.Forbidden,
                    "The genuine old token was not refused under the new session.");
            }
            var current = await test.Second.SendAsync("/observe", cookies);
            var oldName = oldObservation.Record.User!.FindFirst(ClaimTypes.NameIdentifier);
            var newName = current.Record.User!.FindFirst(ClaimTypes.NameIdentifier);
            Assert.True(oldName is not null && newName is not null && oldName.Type == newName.Type &&
                oldName.Value == newName.Value && oldName.Issuer == newName.Issuer,
                "The old/new framework ClaimUid basis did not remain the same canonical owner.");
            var ringBeforeReference = test.Files.RingSnapshot();
            await ServerSessionBrowserAntiforgeryReference.AcceptsSameCookieAndOwnerBeforeObservingOldEpochAsync(
                test, test.Second, cookies, oldToken, current.Record.User!, original.LoginEpoch);
            PostgresServerSessionTestSupport.SamePrivate(ringBeforeReference, test.Files.RingSnapshot());
            // Standard reference accepts this exact old token/cookie/user and
            // observes OLD AdditionalData. Actual Braid above rejects that pair.
            var tokenA = await TokenAsync(test.First, cookies);
            await AcceptedUnsafeAsync(test.Second, cookies, tokenA);
            var tokenB = await TokenAsync(test.Second, cookies);
            await AcceptedUnsafeAsync(test.First, cookies, tokenB);
            var reverse = new BrowserCookies();
            await IssueAsync(test.Second, reverse);
            var reverseObservation = await test.First.SendAsync("/observe", reverse);
            BrowserHttpsFixture.Kind(reverseObservation, ServerSessionRequestKind.Authenticated);
            BrowserHttpsFixture.Identity(reverseObservation, test.Data.Principal, test.Data.Organization, RequiredCreate(test.Second).LoginEpoch);
            PostgresServerSessionTestSupport.SamePrivate(authority, await test.Data.AuthorityAsync());
        });

    [PostgresFact]
    public Task SafeMethodsAndValidUnsafeMethodsAreExplicitWhileEveryRefusalLeavesBodyAndRenewalUntouched() =>
        BrowserHttpsFixture.RunAsync(postgres, async test =>
        {
            var due = await test.StartHostAsync(new FixedBrowserSchedulingClock(DateTimeOffset.MaxValue));
            var cookies = new BrowserCookies();
            await IssueAsync(due, cookies);
            var token = await TokenAsync(due, cookies);
            foreach (var method in new[] { "GET", "HEAD", "OPTIONS" })
            {
                var before = due.Writer.Renews;
                var result = await due.SendAsync("/admit", cookies, method);
                BrowserHttpsFixture.Kind(result, ServerSessionRequestKind.Authenticated);
                Assert.True(result.Status == HttpStatusCode.NoContent && result.Record.Dispatched == 1 && due.Writer.Renews == before + 1,
                    "An allowlisted safe method did not use its one explicit due-renewal admission.");
                Assert.True(result.SetCookies.Length == 0, "Admission unexpectedly refreshed a browser cookie.");
            }
            foreach (var method in new[] { "POST", "PUT", "PATCH", "DELETE" })
            {
                var beforeRows = await test.Data.RowsAsync();
                var beforeRenew = due.Writer.Renews;
                var refused = await due.SendAsync("/admit", cookies, method, body: "__RequestVerificationToken=ignored"u8.ToArray());
                BrowserHttpsFixture.Kind(refused, ServerSessionRequestKind.CsrfRejected);
                BrowserHttpsFixture.NoBodyOrDispatch(refused);
                Assert.True(due.Writer.Renews == beforeRenew, "An unsafe refusal reached renewal.");
                PostgresServerSessionTestSupport.SamePrivate(beforeRows, await test.Data.RowsAsync());
                var accepted = await due.SendAsync("/admit", cookies, method, token, body: "actual application body"u8.ToArray());
                BrowserHttpsFixture.Kind(accepted, ServerSessionRequestKind.Authenticated);
                Assert.True(accepted.Record.BodyReads > 0 && accepted.Record.Dispatched == 1 && due.Writer.Renews == beforeRenew + 1,
                    "Valid unsafe admission did not precede the application's one body dispatch.");
            }
            foreach (var method in new[] { "TRACE", "BRAID-CLOSED-METHOD" })
            {
                var before = due.Writer.Renews;
                var result = await due.SendAsync("/admit", cookies, method, token,
                    body: method == "TRACE" ? null : "untouched"u8.ToArray());
                BrowserHttpsFixture.Kind(result, ServerSessionRequestKind.CsrfRejected);
                BrowserHttpsFixture.NoBodyOrDispatch(result);
                Assert.True(due.Writer.Renews == before, "A nonallowlisted method reached renewal.");
            }
            // CONNECT is a PUBLIC-API-only assertion. No claim that Kestrel
            // accepts an invalid HTTP/1 origin-form CONNECT request is made.
            using var connectScope = due.Services.CreateScope();
            var connect = DirectContext(due, connectScope.ServiceProvider, cookies, "CONNECT", token);
            var observation = new BrowserRequestObservation();
            using var connectBody = new MemoryStream("untouched"u8.ToArray());
            connect.Request.Body = new CountingRequestBody(connectBody, observation);
            var renews = due.Writer.Renews;
            var admission = await due.Browser.AdmitRequestAsync(connect, CancellationToken.None);
            Assert.True(admission.Kind == ServerSessionRequestKind.CsrfRejected && observation.BodyReads == 0 && due.Writer.Renews == renews,
                "The public CONNECT refusal read its body or renewed a session.");
        });

    [PostgresFact]
    public Task UnsafeOriginHeaderAndCookieAmbiguitiesCannotFallbackToAFormOrRenewal() =>
        BrowserHttpsFixture.RunAsync(postgres, async test =>
        {
            var host = await test.StartHostAsync(new FixedBrowserSchedulingClock(DateTimeOffset.MaxValue));
            var cookies = new BrowserCookies();
            await IssueAsync(host, cookies);
            var token = await TokenAsync(host, cookies);
            var rows = await test.Data.RowsAsync();
            var parents = await test.Data.AuthorityAsync();
            string[][] origins = [[], ["null"], ["https://example.invalid"],
                [host.Origin.GetLeftPart(UriPartial.Authority), host.Origin.GetLeftPart(UriPartial.Authority)],
                [host.Origin.GetLeftPart(UriPartial.Authority) + ",https://example.invalid"]];
            foreach (var origin in origins)
                await DeniedUnsafeAsync(host, cookies, token, originValues: origin);
            string[][] tokens = [[], [""], [" "], ["not+canonical/"], [token, token], [token + "," + token]];
            foreach (var header in tokens)
                await DeniedUnsafeAsync(host, cookies, token, tokenValues: header);
            await DeniedUnsafeAsync(host, new BrowserCookies { Session = cookies.Session }, token);
            await DeniedUnsafeAsync(host, cookies, token, rawCookie: cookies.Header + "; " + BrowserHttpsFixture.AntiforgeryCookie + "=" + cookies.Antiforgery);
            await DeniedUnsafeAsync(host, cookies, token, rawCookie: cookies.Header + "; __host-braid.antiforgery=" + cookies.Antiforgery);
            Assert.True(host.Writer.Renews == 0 && host.Writer.Creates == 1 && host.Writer.Revokes == 0,
                "A rejected unsafe request dispatched a write or implicitly retried.");
            PostgresServerSessionTestSupport.SamePrivate(rows, await test.Data.RowsAsync());
            PostgresServerSessionTestSupport.SamePrivate(parents, await test.Data.AuthorityAsync());
        });

    [PostgresFact]
    public Task RawSessionCookieAliasesChunksDuplicatesAndBoundsFailClosedBeforeMutableCookieCollapse() =>
        BrowserHttpsFixture.RunAsync(postgres, async test =>
        {
            var cookies = new BrowserCookies();
            await IssueAsync(test.First, cookies);
            var rows = await test.Data.RowsAsync();
            var reads = test.First.Reader.Calls;
            var valid = cookies.Header;
            string[] invalid = [
                valid + "; " + valid,
                valid + "; __host-braid.session=" + cookies.Session,
                valid + "; __Host-Braid.SessionC1=chunk",
                BrowserHttpsFixture.SessionCookie + "=bad+encoding/",
                valid + "; " + string.Join("; ", Enumerable.Range(0, 128).Select(i => "ordinary" + i + "=x")),
                valid + "; ordinary=" + new string('x', 16384)
            ];
            foreach (var raw in invalid)
            {
                var result = await test.First.SendAsync("/observe", rawCookie: raw);
                BrowserHttpsFixture.Kind(result, ServerSessionRequestKind.AuthenticationRequired);
                BrowserHttpsFixture.NoBodyOrDispatch(result);
            }
            Assert.True(test.First.Reader.Calls == reads && test.First.Writer.Creates == 1 &&
                test.First.Writer.Revokes == 0 && test.First.Writer.Renews == 0,
                "A rejected raw cookie reached the restricted session ports.");
            PostgresServerSessionTestSupport.SamePrivate(rows, await test.Data.RowsAsync());
        });

    [PostgresFact]
    public Task CanonicalObservedIdentityIgnoresAmbientUserAndRechecksBindingPrincipalAndOrganization() =>
        BrowserHttpsFixture.RunAsync(postgres, async test =>
        {
            var cookies = new BrowserCookies();
            await IssueAsync(test.First, cookies);
            var intent = RequiredCreate(test.First);
            var rows = await test.Data.RowsAsync();
            test.Second.PoisonAmbientUser = true;
            var positive = await test.Second.SendAsync("/observe", cookies);
            BrowserHttpsFixture.Kind(positive, ServerSessionRequestKind.Authenticated);
            BrowserHttpsFixture.Identity(positive, test.Data.Principal, test.Data.Organization, intent.LoginEpoch);
            var foreign = await test.StartHostAsync(organization: test.Data.OtherOrganization);
            var other = await foreign.SendAsync("/observe", cookies);
            BrowserHttpsFixture.Kind(other, ServerSessionRequestKind.AuthenticationRequired);
            Assert.True(foreign.Reader.Calls == 0, "Another configured organization reached a foreign session row.");
            await test.Data.Case.ExecuteAsync("UPDATE braid.principals SET active=false WHERE id=$1", test.Data.Principal.Value);
            foreach (var host in new[] { test.First, test.Second })
                BrowserHttpsFixture.Kind(await host.SendAsync("/observe", cookies), ServerSessionRequestKind.AuthenticationRequired);
            PostgresServerSessionTestSupport.SamePrivate(rows, await test.Data.RowsAsync());
            var reads = test.Second.Reader.Calls;
            var revoked = await test.Second.SendAsync("/revoke", cookies, "POST");
            Assert.True(revoked.Status == HttpStatusCode.NoContent && test.Second.Reader.Calls == reads && test.Second.Writer.Revokes == 1,
                "Inactive direct revocation gained a live-reader prerequisite or lost its ACK.");
            DeletedCookies(revoked);
            await test.Data.Case.ExecuteAsync("UPDATE braid.principals SET active=true WHERE id=$1", test.Data.Principal.Value);
            BrowserHttpsFixture.Kind(await test.First.SendAsync("/observe", cookies), ServerSessionRequestKind.AuthenticationRequired);
            var fresh = new BrowserCookies();
            await IssueAsync(test.First, fresh);
            var disabled = await test.Data.IdentityOwner.DisableAsync(test.Data.Binding.Key, test.Data.Principal, test.Data.Binding.Version);
            Assert.True(disabled.Outcome == ExternalIdentityBindingOwnerOutcome.Disabled, "The actual binding disable was not acknowledged.");
            BrowserHttpsFixture.Kind(await test.Second.SendAsync("/observe", fresh), ServerSessionRequestKind.AuthenticationRequired);
            var before = await test.RowCountAsync();
            var deniedIssue = await test.Second.SendAsync("/issue", method: "POST");
            BrowserHttpsFixture.Failure(deniedIssue, ServerSessionIdentityFailureKind.Denied);
            Assert.True(deniedIssue.SetCookies.Length == 0 && await test.RowCountAsync() == before,
                "A disabled binding issued or adopted a session.");
        });

    [PostgresFact]
    public Task FreshAdmissionRejectsRealPrincipalInvalidationAfterTheCachedPositiveObservation() =>
        BrowserHttpsFixture.RunAsync(postgres, async test =>
        {
            var cookies = new BrowserCookies();
            await IssueAsync(test.First, cookies);
            var rows = await test.Data.RowsAsync();
            var authority = await test.Data.AuthorityAsync();
            var changed = 0;
            test.First.Reader.AfterObserve = async (_, _) =>
            {
                if (Interlocked.Increment(ref changed) == 1)
                    await test.Data.Case.ExecuteAsync("UPDATE braid.principals SET active=false WHERE id=$1", test.Data.Principal.Value);
            };
            var denied = await test.First.SendAsync("/admit", cookies);
            BrowserHttpsFixture.Kind(denied, ServerSessionRequestKind.AuthenticationRequired);
            BrowserHttpsFixture.NoBodyOrDispatch(denied);
            Assert.True(changed == 2 && test.First.Writer.Renews == 0 && test.First.Writer.Revokes == 0,
                "Fresh admission reused cached identity or wrote after actual parent invalidation.");
            PostgresServerSessionTestSupport.SamePrivate(rows, await test.Data.RowsAsync());
            await test.Data.Case.ExecuteAsync("UPDATE braid.principals SET active=true WHERE id=$1", test.Data.Principal.Value);
            PostgresServerSessionTestSupport.SamePrivate(authority, await test.Data.AuthorityAsync());
        });

    [PostgresFact]
    public Task AClosedCallerOwnedReaderPoolIsUnavailableWithoutMutationOrApplicationDispatch() =>
        BrowserHttpsFixture.RunAsync(postgres, async test =>
        {
            var cookies = new BrowserCookies();
            await IssueAsync(test.First, cookies);
            var rows = await test.Data.RowsAsync();
            await test.First.ReaderDatabase.DisposeAsync();
            var result = await test.First.SendAsync("/admit", cookies, "POST", body: "untouched"u8.ToArray());
            BrowserHttpsFixture.Kind(result, ServerSessionRequestKind.Unavailable);
            BrowserHttpsFixture.NoBodyOrDispatch(result);
            Assert.True(result.Status == HttpStatusCode.ServiceUnavailable && result.SetCookies.Length == 0 &&
                test.First.Writer.Creates == 1 && test.First.Writer.Renews == 0 && test.First.Writer.Revokes == 0,
                "Actual caller-owned pool unavailability became success, mutation or cookie rewrite.");
            PostgresServerSessionTestSupport.SamePrivate(rows, await test.Data.RowsAsync());
        });

    [PostgresFact]
    public Task DatabaseFinalFractionSurvivesProjectedFrameworkExpiryAndSkewedSchedulingThenDirectlyRevokesExpiredRow() =>
        BrowserHttpsFixture.RunAsync(postgres, async test =>
        {
            var host = await test.StartHostAsync(new FixedBrowserSchedulingClock(DateTimeOffset.MaxValue));
            using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            host.VerifiedDeadline = await test.FinalFractionDeadlineAsync(bound.Token);
            var cookies = new BrowserCookies();
            await IssueAsync(host, cookies, bound.Token);
            var intent = RequiredCreate(host);
            var facts = await test.Data.StoredAsync(intent.Key);
            var floor = new DateTimeOffset(facts.Lifetime.AbsoluteExpiresAtUtc.Ticks -
                facts.Lifetime.AbsoluteExpiresAtUtc.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
            DateTimeOffset observed;
            do
            {
                observed = await test.DatabaseNowAsync(bound.Token);
                if (observed < floor) await Task.Delay(5, bound.Token);
            } while (observed < floor);
            Assert.True(observed < facts.Lifetime.AbsoluteExpiresAtUtc, "The actual final-fraction window was missed before observation.");
            var reply = await host.SendAsync("/observe", cookies, cancellationToken: bound.Token);
            BrowserHttpsFixture.Kind(reply, ServerSessionRequestKind.Authenticated);
            BrowserHttpsFixture.Identity(reply, test.Data.Principal, test.Data.Organization, intent.LoginEpoch);
            var after = await test.DatabaseNowAsync(bound.Token);
            Assert.True(after >= floor && after < facts.Lifetime.AbsoluteExpiresAtUtc && host.Writer.Revokes == 0 && host.Writer.Renews == 0,
                "The final-fraction read was outside database liveness or mutated the row.");
            PostgresServerSessionTestSupport.SameFacts(facts, await test.Data.StoredAsync(intent.Key));
            do
            {
                observed = await test.DatabaseNowAsync(bound.Token);
                if (observed < facts.Lifetime.AbsoluteExpiresAtUtc) await Task.Delay(5, bound.Token);
            } while (observed < facts.Lifetime.AbsoluteExpiresAtUtc);
            BrowserHttpsFixture.Kind(await host.SendAsync("/observe", cookies, cancellationToken: bound.Token), ServerSessionRequestKind.AuthenticationRequired);
            var reads = host.Reader.Calls;
            var revoked = await host.SendAsync("/revoke", cookies, "POST", cancellationToken: bound.Token);
            Assert.True(revoked.Status == HttpStatusCode.NoContent && host.Reader.Calls == reads && host.Writer.Revokes == 1,
                "Expired direct revocation required live retrieval or failed to acknowledge.");
            DeletedCookies(revoked);
            var tombstone = await test.Data.StoredAsync(intent.Key);
            PostgresServerSessionTestSupport.SameImmutable(facts, tombstone);
            Assert.True(tombstone.Version == 2 && tombstone.RevokedAtUtc is not null,
                "The exact expired row lacks its one-way acknowledged tombstone.");
        });

    [PostgresFact]
    public Task ConcurrentDueRenewalsUseOneRealCasEachWithoutRetryOrCarrierRefresh() =>
        BrowserHttpsFixture.RunAsync(postgres, async test =>
        {
            var clock = new FixedBrowserSchedulingClock(DateTimeOffset.MaxValue);
            var first = await test.StartHostAsync(clock);
            var second = await test.StartHostAsync(clock);
            var cookies = new BrowserCookies();
            await IssueAsync(first, cookies);
            var facts = await test.Data.StoredAsync(RequiredCreate(first).Key);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var arrivals = 0;
            Task BeforeRenew(ServerSessionKey key, long version, CancellationToken cancellation)
            {
                Assert.True(key == facts.Key && version == 1, "Concurrent renewal did not retain its original exact CAS.");
                if (Interlocked.Increment(ref arrivals) == 2) both.TrySetResult();
                return gate.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellation);
            }
            first.Writer.BeforeRenew = BeforeRenew;
            second.Writer.BeforeRenew = BeforeRenew;
            var a = first.SendAsync("/admit", cookies);
            var b = second.SendAsync("/admit", cookies);
            try
            {
                await both.Task.WaitAsync(TimeSpan.FromSeconds(5));
                gate.TrySetResult();
                var results = await Task.WhenAll(a, b);
                foreach (var result in results)
                {
                    BrowserHttpsFixture.Kind(result, ServerSessionRequestKind.Authenticated);
                    Assert.True(result.Record.Dispatched == 1 && result.SetCookies.Length == 0,
                        "Concurrent read admission refreshed a cookie or failed to dispatch once.");
                }
                var outcomes = new[] { first.Writer.LastRenew?.Outcome, second.Writer.LastRenew?.Outcome };
                Assert.True(first.Writer.Renews == 1 && second.Writer.Renews == 1 &&
                    outcomes.Count(value => value == ServerSessionWriteOutcome.Renewed) == 1 &&
                    outcomes.Count(value => value == ServerSessionWriteOutcome.Conflict) == 1,
                    "Concurrent renewal retried or failed to retain one actual conflict.");
                var current = await test.Data.StoredAsync(facts.Key);
                PostgresServerSessionTestSupport.SameImmutable(facts, current);
                Assert.True(current.Version == 2 && current.Lifetime.IdleExpiresAtUtc > facts.Lifetime.IdleExpiresAtUtc &&
                    first.Reader.Calls + second.Reader.Calls is >= 6 and <= 7,
                    "The actual conflict did not use bounded separate fresh observations.");
            }
            finally { gate.TrySetResult(); await ObserveAsync(a); await ObserveAsync(b); }
        });

    [PostgresFact]
    public Task AcknowledgedConcurrentRevokeDeniesDueRenewalBeforeApplicationBody() =>
        BrowserHttpsFixture.RunAsync(postgres, async test =>
        {
            var due = await test.StartHostAsync(new FixedBrowserSchedulingClock(DateTimeOffset.MaxValue));
            var cookies = new BrowserCookies();
            await IssueAsync(due, cookies);
            var token = await TokenAsync(due, cookies);
            var key = RequiredCreate(due).Key;
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            due.Writer.BeforeRenew = (_, _, cancellation) =>
            { reached.TrySetResult(); return release.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellation); };
            var admission = due.SendAsync("/admit", cookies, "POST", token, body: "untouched"u8.ToArray());
            try
            {
                await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var revoke = await test.Second.SendAsync("/revoke", cookies, "POST");
                Assert.True(revoke.Status == HttpStatusCode.NoContent && await test.IndependentlyRevokedAsync(key, CancellationToken.None),
                    "The competing revocation was not independently acknowledged.");
                release.TrySetResult();
                var denied = await admission;
                BrowserHttpsFixture.Kind(denied, ServerSessionRequestKind.AuthenticationRequired);
                BrowserHttpsFixture.NoBodyOrDispatch(denied);
                Assert.True(due.Writer.Renews == 1 && due.Writer.LastRenew?.Outcome == ServerSessionWriteOutcome.Denied &&
                    denied.SetCookies.Length == 0, "Revocation denial retried renewal or rewrote the carrier.");
            }
            finally { release.TrySetResult(); await ObserveAsync(admission); }
        });

    [PostgresFact]
    public Task FreshCreateLostCommitAckDoesNotDeliverOrAdoptItsDurableSession() => LostAcknowledgementAsync("/issue", oldCookie: false);

    [PostgresFact]
    public Task DirectRevokeLostCommitAckDoesNotClaimCookieClearingSucceeded() => LostAcknowledgementAsync("/revoke", oldCookie: true);

    [PostgresFact]
    public Task OldRevokeLostCommitAckPreventsFreshIssueStoreDespiteDurableTombstone() => LostAcknowledgementAsync("/issue", oldCookie: true);

    private Task LostAcknowledgementAsync(string operationPath, bool oldCookie) =>
        BrowserHttpsFixture.RunAsync(postgres, async test =>
        {
            var cookies = new BrowserCookies();
            ServerSessionCreateIntent? original = null;
            if (oldCookie) { await IssueAsync(test.First, cookies); original = RequiredCreate(test.First); }
            var authority = await test.Data.AuthorityAsync();
            using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            ServerSessionCommitAckLossRelay? relay = null;
            BrowserHttpsHost? routed = null;
            Task<BrowserExchange>? operation = null;
            Exception? primary = null;
            var cleanup = new List<Exception>();
            try
            {
                relay = new(test.Data.Case.SessionWriterConnectionString);
                var connection = new NpgsqlConnectionStringBuilder(relay.RoutedConnectionString) { Timezone = "Etc/UTC" };
                routed = await test.StartHostAsync(writerConnectionString: connection.ConnectionString);
                operation = routed.SendAsync(operationPath, cookies, "POST", cancellationToken: caller.Token);
                await Task.WhenAny(operation, relay.CommitAcknowledgementWithheld).WaitAsync(TimeSpan.FromSeconds(10), caller.Token);
                Assert.True(relay.CommitAcknowledgementWithheld.IsCompletedSuccessfully && !operation.IsCompleted,
                    "The actual browser helper did not reach the real withheld COMMIT acknowledgement.");
                await relay.CommitAcknowledgementWithheld;
                Assert.True(relay.FrontendCommitSeen && relay.BackendCommitCompleteSeen && relay.BackendIdleReadySeen &&
                    relay.ParameterStatusFramesWithheld is > 0 and <= 16,
                    "The real protocol did not withhold bounded COMMIT/status/idle acknowledgement frames.");
                var intent = original ?? RequiredCreate(routed);
                var durable = await TicketQualification.DurableAsync(test.Data.Case, intent.Key, caller.Token);
                Assert.True(durable.Total == 1 && durable.Revoked == oldCookie && durable.Version == (oldCookie ? 2 : 1) &&
                    durable.Facts.BindingId == intent.BindingId && durable.Facts.BindingVersion == intent.BindingVersion &&
                    durable.Facts.LoginEpoch == intent.LoginEpoch && durable.Facts.CopyProtectedTicket().AsSpan().SequenceEqual(intent.CopyProtectedTicket()),
                    "The independent unpooled connection did not see the exact committed private transition.");
                Assert.True(!operation.IsCompleted && !caller.IsCancellationRequested,
                    "The helper settled before deliberate post-durability acknowledgement loss.");
                relay.DropAcknowledgement();
                var reply = await operation.WaitAsync(TimeSpan.FromSeconds(5), caller.Token);
                BrowserHttpsFixture.Failure(reply, ServerSessionIdentityFailureKind.Unavailable);
                Assert.True(reply.Status == HttpStatusCode.ServiceUnavailable && reply.SetCookies.Length == 0 &&
                    routed.Writer.Creates == (oldCookie ? 0 : 1) && routed.Writer.Revokes == (oldCookie ? 1 : 0) && routed.Writer.Renews == 0,
                    "Lost acknowledgement became a successful cookie operation, retry or subsequent fresh Store.");
                await relay.Completion.WaitAsync(TimeSpan.FromSeconds(5), caller.Token);
                Assert.True(relay.RequestedDropAccepted && !caller.IsCancellationRequested,
                    "The actual failure was replaced by budget cancellation or an unrelated relay deadline.");
                var reconciled = await TicketQualification.DurableAsync(test.Data.Case, intent.Key, caller.Token);
                PostgresServerSessionTestSupport.SameFacts(durable.Facts, reconciled.Facts);
                var current = await test.Data.Reader.ObserveAsync(intent.Key, caller.Token);
                Assert.True(oldCookie ? current is null : current is not null && current.PrincipalId == test.Data.Principal,
                    "Separate known-key reconciliation disagrees with actual committed state.");
                PostgresServerSessionTestSupport.SamePrivate(authority, await test.Data.AuthorityAsync());
                // This owner reconciliation is NOT a delivered cookie or helper ACK.
            }
            catch (Exception error) { primary = BrowserHttpsFixture.Safe(error, "The real HTTPS commit-loss qualification failed."); }
            finally
            {
                relay?.DropAcknowledgement();
                try { caller.Cancel(); } catch (Exception) { cleanup.Add(new InvalidOperationException("Owned ACK caller cancellation failed.")); }
                if (relay is not null) await BrowserHttpsFixture.CleanupAsync(relay.DisposeAsync, "Owned ACK relay did not settle.", cleanup);
                if (operation is not null) await ObserveAsync(operation);
                if (routed is not null) await BrowserHttpsFixture.CleanupAsync(routed.DisposeAsync, "Owned routed HTTPS host did not settle.", cleanup);
            }
            BrowserHttpsFixture.Throw(primary, cleanup);
        });

    [PostgresFact]
    public Task PublicCancellationPrecedencePreservesCallerOrRequestCauseWithoutRestrictedIo() =>
        BrowserHttpsFixture.RunAsync(postgres, async test =>
        {
            var cookies = new BrowserCookies();
            await IssueAsync(test.First, cookies);
            var rows = await test.Data.RowsAsync();
            var reads = test.First.Reader.Calls;
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            foreach (var requestCause in new[] { false, true })
            {
                for (var operation = 0; operation < 5; operation++)
                {
                    using var requestScope = test.First.Services.CreateScope();
                    var context = DirectContext(test.First, requestScope.ServiceProvider, cookies);
                    if (requestCause) context.RequestAborted = cancellation.Token;
                    var caller = requestCause ? CancellationToken.None : cancellation.Token;
                    Exception? error = null;
                    try
                    {
                        switch (operation)
                        {
                            case 0: await test.First.Browser.ObserveAsync(context, caller); break;
                            case 1: await test.First.Browser.AdmitRequestAsync(context, caller); break;
                            case 2: await test.First.Browser.IssueRequestTokenAsync(context, caller); break;
                            case 3: await test.First.Browser.RevokeLocalAsync(context, caller); break;
                            case 4:
                                await test.First.Browser.IssueFreshAsync(context, test.Data.Binding.BindingId,
                                test.Data.Binding.Version, test.Data.Principal, await test.Data.DeadlineAsync(), caller); break;
                        }
                    }
                    catch (Exception value) { error = value; }
                    Assert.True(error is OperationCanceledException canceled && canceled.CancellationToken == cancellation.Token,
                        "A canceled public helper lost its original caller or request cancellation cause.");
                }
            }
            Assert.True(test.First.Reader.Calls == reads && test.First.Writer.Creates == 1 &&
                test.First.Writer.Revokes == 0 && test.First.Writer.Renews == 0,
                "Pre-canceled public helpers reached restricted session I/O.");
            PostgresServerSessionTestSupport.SamePrivate(rows, await test.Data.RowsAsync());
        });

    [PostgresFact]
    public Task OnePrivateObservationOwnsTheRealReadWhileALaterCanceledWaiterCannotCancelItsOwner() =>
        BrowserHttpsFixture.RunAsync(postgres, async test =>
        {
            var cookies = new BrowserCookies();
            await IssueAsync(test.First, cookies);
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            test.First.Reader.AfterObserve = (_, cancellation) =>
            { reached.TrySetResult(); return release.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellation); };
            using var scope = test.First.Services.CreateScope();
            var context = DirectContext(test.First, scope.ServiceProvider, cookies);
            var reads = test.First.Reader.Calls;
            var owner = test.First.Browser.ObserveAsync(context, CancellationToken.None);
            using var waiterCancellation = new CancellationTokenSource();
            Task<ServerSessionRequestAdmission>? waiter = null;
            try
            {
                await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
                waiter = test.First.Browser.ObserveAsync(context, waiterCancellation.Token);
                waiterCancellation.Cancel();
                Exception? failure = null;
                try { await waiter; } catch (Exception error) { failure = error; }
                Assert.True(failure is OperationCanceledException canceled && canceled.CancellationToken == waiterCancellation.Token &&
                    !owner.IsCompleted && test.First.Reader.Calls == reads + 1,
                    "A later waiter canceled the observation owner or dispatched a second actual read.");
                release.TrySetResult();
                var admission = await owner;
                Assert.True(admission.Kind == ServerSessionRequestKind.Authenticated && admission.Identity?.PrincipalId == test.Data.Principal &&
                    test.First.Reader.Calls == reads + 1 && test.First.Writer.Renews == 0,
                    "The private owner observation did not retain one actual read and original identity.");
            }
            finally
            {
                release.TrySetResult();
                await ObserveAsync(owner);
                if (waiter is not null) await ObserveAsync(waiter);
            }
        });

    [PostgresFact]
    public Task HeadersStartedAfterRealCreateAckLeaveDurableMetadataButNeverClaimDeliveredSession() =>
        BrowserHttpsFixture.RunAsync(postgres, async test =>
        {
            test.First.StartHeadersAfterCreateAcknowledgement = true;
            var reply = await test.First.SendAsync("/issue", method: "POST");
            BrowserHttpsFixture.Failure(reply, ServerSessionIdentityFailureKind.Unavailable);
            var intent = RequiredCreate(test.First);
            var durable = await TicketQualification.DurableAsync(test.Data.Case, intent.Key, CancellationToken.None);
            Assert.True(durable.Total == 1 && durable.Version == 1 && !durable.Revoked &&
                durable.Facts.BindingId == intent.BindingId && durable.Facts.BindingVersion == intent.BindingVersion &&
                durable.Facts.LoginEpoch == intent.LoginEpoch && durable.Facts.CopyProtectedTicket().AsSpan().SequenceEqual(intent.CopyProtectedTicket()) &&
                reply.SetCookies.Length == 0 && test.First.Writer.Creates == 1 && test.First.Writer.Revokes == 0 && test.First.Writer.Renews == 0,
                "A real ACK followed by started headers became delivery success, compensation or retry.");
            // This is an owned response-feature timing fault after a genuine DB
            // ACK. It does not prove what a disconnected browser received.
        });

    [PostgresFact]
    public Task HeaderAndTransitionFailuresRemainClosedWithoutClaimingDatabaseAndHeadersAreAtomic() =>
        BrowserHttpsFixture.RunAsync(postgres, async test =>
        {
            var started = await test.First.SendAsync("/started-issue", method: "POST");
            BrowserHttpsFixture.Failure(started, ServerSessionIdentityFailureKind.Unavailable);
            Assert.True(test.First.Writer.Creates == 0 && started.SetCookies.Length == 0,
                "Prestarted headers reached trusted creation or appended a cookie.");
            var missing = await test.First.SendAsync("/token");
            BrowserHttpsFixture.Failure(missing, ServerSessionIdentityFailureKind.Denied);
            var twice = await test.First.SendAsync("/issue-twice", method: "POST");
            BrowserHttpsFixture.Failure(twice, ServerSessionIdentityFailureKind.UntrustedIssuance);
            Assert.True(test.First.Writer.Creates == 1 && test.First.Writer.Renews == 0 && twice.SetCookies.Count(
                cookie => cookie.Name == BrowserHttpsFixture.SessionCookie) == 1,
                "A reused issuance request performed a second Store or rewrote a first acknowledged cookie.");
            // The first cookie may already be delivered: no rollback/compensation
            // or joint DB+headers atomicity claim is made by this refusal.
            var cookies = new BrowserCookies();
            await IssueAsync(test.Second, cookies);
            var startedToken = await test.Second.SendAsync("/started-token", cookies);
            BrowserHttpsFixture.Failure(startedToken, ServerSessionIdentityFailureKind.Unavailable);
            Assert.True(startedToken.SetCookies.Length == 0, "Prestarted token headers appended an AF cookie.");
            var transition = await test.Second.SendAsync("/issue-then-token", cookies, "POST");
            BrowserHttpsFixture.Failure(transition, ServerSessionIdentityFailureKind.UntrustedIssuance);
            Assert.True(transition.SetCookies.All(cookie => cookie.Name != BrowserHttpsFixture.AntiforgeryCookie),
                "A transition request issued a stale-context AF token cookie.");
            var ordinary = await test.Second.SendAsync("/issue-then-admit", method: "POST");
            BrowserHttpsFixture.Kind(ordinary, ServerSessionRequestKind.Unavailable);
            Assert.True(ordinary.Record.Failure is null && ordinary.Record.Dispatched == 0 && test.Second.Writer.Renews == 0,
                "Ordinary admission threw or succeeded inside a transition request.");
            var observed = await test.Second.SendAsync("/issue-then-observe", method: "POST");
            BrowserHttpsFixture.Kind(observed, ServerSessionRequestKind.Unavailable);
            Assert.True(observed.Record.Failure is null && observed.Record.Dispatched == 0 && test.Second.Writer.Renews == 0,
                "Ordinary observation threw or succeeded inside a transition request.");
        });

    [PostgresFact]
    public Task NamedSchemesHaveNoDefaultsOrRedirectsAndHostAntiforgeryIsNotReplaced() =>
        BrowserHttpsFixture.RunAsync(postgres, async test =>
        {
            BrowserHttpsFixture.Kind(await test.First.SendAsync("/observe"), ServerSessionRequestKind.Anonymous);
            var anonymous = await test.First.SendAsync("/admit", method: "POST", body: "untouched"u8.ToArray());
            BrowserHttpsFixture.Kind(anonymous, ServerSessionRequestKind.Anonymous);
            BrowserHttpsFixture.NoBodyOrDispatch(anonymous);
            var schemeProvider = test.First.Services.GetRequiredService<IAuthenticationSchemeProvider>();
            Assert.True(await schemeProvider.GetDefaultAuthenticateSchemeAsync() is null &&
                await schemeProvider.GetDefaultChallengeSchemeAsync() is null &&
                await schemeProvider.GetDefaultForbidSchemeAsync() is null &&
                await schemeProvider.GetDefaultSignInSchemeAsync() is null &&
                await schemeProvider.GetDefaultSignOutSchemeAsync() is null,
                "Browser composition silently activated a host default scheme.");
            var schemes = (await schemeProvider.GetAllSchemesAsync()).ToArray();
            Assert.True(schemes.Length == 2 && schemes.Any(value => value.Name == ServerSessionSchemes.Session) &&
                schemes.Any(value => value.Name == ServerSessionSchemes.Issue) && schemes[0].HandlerType != schemes[1].HandlerType,
                "The named schemes do not own distinct handler types.");
            var challenge = await test.First.SendAsync("/challenge");
            var forbid = await test.First.SendAsync("/forbid");
            Assert.True(challenge.Status == HttpStatusCode.Unauthorized && forbid.Status == HttpStatusCode.Forbidden &&
                !challenge.HasLocation && !forbid.HasLocation && challenge.SetCookies.Length == 0 && forbid.SetCookies.Length == 0,
                "Named challenge/forbid redirected or emitted a cookie.");
            var services = new ServiceCollection();
            services.AddLogging(logging => logging.ClearProviders());
            services.AddAntiforgery(options => options.Cookie.Name = "Owned.Host.Antiforgery");
            services.AddBraidBrowserSessions(new ServerSessionBrowserOptions(test.First.Origin.GetLeftPart(UriPartial.Authority)),
                test.First.Profile, test.First.Reader, test.First.Writer, TimeProvider.System);
            foreach (var scheme in schemes)
                Assert.True(services.Any(value => value.ServiceType == scheme.HandlerType && value.Lifetime == ServiceLifetime.Transient),
                    "A named cookie handler was not registered with transient ownership.");
            using (var provider = services.BuildServiceProvider(validateScopes: true))
            {
                _ = provider.GetRequiredService<ServerSessionBrowser>();
                Assert.True(provider.GetRequiredService<IOptions<AntiforgeryOptions>>().Value.Cookie.Name == "Owned.Host.Antiforgery",
                    "The private browser composition replaced host-global AF options.");
            }
            var retained = new BrowserCookies();
            await IssueAsync(test.First, retained);
            BrowserHttpsFixture.Kind(await test.Second.SendAsync("/observe", retained), ServerSessionRequestKind.Authenticated);
            Exception? duplicate = null;
            try
            {
                services.AddBraidBrowserSessions(new ServerSessionBrowserOptions(test.First.Origin.GetLeftPart(UriPartial.Authority)),
                    test.First.Profile, test.First.Reader, test.First.Writer, TimeProvider.System);
            }
            catch (Exception error) { duplicate = error; }
            Assert.True(duplicate is ServerSessionIdentityException failure && failure.Kind == ServerSessionIdentityFailureKind.ConfigurationFailure &&
                failure.InnerException is null, "Conflicting composition registration did not fail categorically.");
            foreach (var invalid in new[] { "http://127.0.0.1:12345", "https://user@127.0.0.1:12345",
                "https://127.0.0.1:12345/path", "https://127.0.0.1:12345?query", "https://127.0.0.1:12345#fragment" })
                await ClosedFailureAsync(ServerSessionIdentityFailureKind.ConfigurationFailure, () =>
                {
                    var rejectedServices = new ServiceCollection();
                    rejectedServices.AddLogging(logging => logging.ClearProviders());
                    rejectedServices.AddBraidBrowserSessions(new ServerSessionBrowserOptions(invalid),
                        test.First.Profile, test.First.Reader, test.First.Writer, TimeProvider.System);
                    using var rejectedProvider = rejectedServices.BuildServiceProvider(validateScopes: true);
                    _ = rejectedProvider.GetRequiredService<ServerSessionBrowser>();
                    return Task.CompletedTask;
                });
        });

    [PostgresFact]
    public Task ApiAndSamePostgresNormalRestartUseFreshProfilesPoolsAndUnchangedOpaqueCookies() =>
        BrowserHttpsFixture.RunAsync(postgres, async test =>
        {
            var cookies = new BrowserCookies();
            await IssueAsync(test.First, cookies);
            var token = await TokenAsync(test.Second, cookies);
            var key = RequiredCreate(test.First).Key;
            var facts = await test.Data.StoredAsync(key);
            var firstOrigin = test.First.Origin;
            var secondOrigin = test.Second.Origin;
            await test.First.DisposeAsync();
            await test.Second.DisposeAsync();
            await test.Data.ReaderDatabase.DisposeAsync();
            await test.Data.WriterDatabase.DisposeAsync();
            await test.Data.IdentityOwner.DisposeAsync();
            await test.Data.Case.CloseOwnerPoolAsync();
            await postgres.RestartAsync();
            var first = await test.StartHostAsync(restartOrigin: firstOrigin);
            var second = await test.StartHostAsync(restartOrigin: secondOrigin);
            foreach (var host in new[] { first, second })
            {
                var observed = await host.SendAsync("/observe", cookies);
                BrowserHttpsFixture.Kind(observed, ServerSessionRequestKind.Authenticated);
                BrowserHttpsFixture.Identity(observed, test.Data.Principal, test.Data.Organization, facts.LoginEpoch);
                await AcceptedUnsafeAsync(host, cookies, token);
                Assert.True(host.Writer.Creates == 0 && host.Writer.Revokes == 0 && host.Writer.Renews == 0,
                    "Fresh HTTPS composition adopted, recreated or renewed an observed session.");
            }
            var durable = await TicketQualification.DurableAsync(test.Data.Case, key, CancellationToken.None);
            Assert.True(durable.Total == 1, "Same-PG restart changed the actual session row cardinality.");
            PostgresServerSessionTestSupport.SameFacts(facts, durable.Facts);
        });

    [PostgresFact]
    public Task ExplicitCertificateRotationRetainsOverlapAndFreshKeyLossCannotBootstrapOrRecoverIdentity() =>
        BrowserHttpsFixture.RunAsync(postgres, async test =>
        {
            var cookies = new BrowserCookies();
            await IssueAsync(test.First, cookies);
            var token = await TokenAsync(test.First, cookies);
            var original = await test.Data.StoredAsync(RequiredCreate(test.First).Key);
            var authority = await test.Data.AuthorityAsync();
            var firstOrigin = test.First.Origin;
            var secondOrigin = test.Second.Origin;
            await test.First.DisposeAsync();
            await test.Second.DisposeAsync();
            var next = test.CreateOwnedRotationCertificate();
            var beforeFiles = Directory.GetFiles(test.Files.Keys).ToHashSet(StringComparer.Ordinal);
            var services = new ServiceCollection();
            services.AddLogging(logging => logging.ClearProviders());
            services.AddDataProtection().SetApplicationName(test.Files.ApplicationName)
                .PersistKeysToFileSystem(new DirectoryInfo(test.Files.Keys)).ProtectKeysWithCertificate(next)
                .UnprotectKeysWithAnyCertificate(test.Files.Certificate, next).DisableAutomaticKeyGeneration();
            using (var owner = services.BuildServiceProvider())
                owner.GetRequiredService<IKeyManager>().CreateNewKey(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(90));
            var added = Directory.GetFiles(test.Files.Keys).Where(path => !beforeFiles.Contains(path)).ToArray();
            Assert.True(added.Length == 1, "Explicit owned key rotation did not add exactly one standard key.");
            foreach (var path in added) File.SetUnixFileMode(path, SessionTicketQualificationRoot.PrivateFile);
            await ClosedFailureAsync(ServerSessionIdentityFailureKind.ConfigurationFailure, () =>
            {
                using var refused = DurableSessionKeyProfile.Open(test.Files.Options(test.Data.Organization), next, []);
                return Task.CompletedTask;
            });
            var first = await test.StartHostAsync(currentCertificate: next, overlaps: [test.Files.Certificate], restartOrigin: firstOrigin);
            var second = await test.StartHostAsync(currentCertificate: next, overlaps: [test.Files.Certificate], restartOrigin: secondOrigin);
            foreach (var host in new[] { first, second })
            {
                BrowserHttpsFixture.Kind(await host.SendAsync("/observe", cookies), ServerSessionRequestKind.Authenticated);
                await AcceptedUnsafeAsync(host, cookies, token);
            }
            var newCookies = new BrowserCookies();
            await IssueAsync(first, newCookies);
            var newToken = await TokenAsync(second, newCookies);
            await AcceptedUnsafeAsync(first, newCookies, newToken);
            await first.DisposeAsync();
            await second.DisposeAsync();
            var beforeLoss = await test.Data.RowsAsync();
            var quarantine = Path.Combine(test.Files.Root, "owned-withheld-key-ring");
            Exception? primary = null;
            var cleanup = new List<Exception>();
            var moved = false;
            try
            {
                Directory.Move(test.Files.Keys, quarantine); moved = true;
                Directory.CreateDirectory(test.Files.Keys, SessionTicketQualificationRoot.PrivateDirectory);
                await ClosedFailureAsync(ServerSessionIdentityFailureKind.ConfigurationFailure, () =>
                {
                    using var refused = DurableSessionKeyProfile.Open(test.Files.Options(test.Data.Organization), next, [test.Files.Certificate]);
                    return Task.CompletedTask;
                });
                Assert.True(Directory.GetFiles(test.Files.Keys).Length == 0 && Directory.GetDirectories(test.Files.Keys).Length == 0,
                    "Fresh key loss implicitly bootstrapped a replacement key ring.");
                PostgresServerSessionTestSupport.SamePrivate(beforeLoss, await test.Data.RowsAsync());
            }
            catch (Exception error) { primary = BrowserHttpsFixture.Safe(error, "Fresh owned key-loss refusal failed."); }
            finally
            {
                if (moved)
                {
                    try { Directory.Delete(test.Files.Keys, recursive: false); Directory.Move(quarantine, test.Files.Keys); }
                    catch (Exception) { cleanup.Add(new InvalidOperationException("The retained owned key ring could not be restored.")); }
                }
            }
            BrowserHttpsFixture.Throw(primary, cleanup);
            Assert.True(await test.RowCountAsync() == 2, "The explicit post-rotation issue did not add exactly its independent session row.");
            PostgresServerSessionTestSupport.SameFacts(original, await test.Data.StoredAsync(original.Key));
            PostgresServerSessionTestSupport.SamePrivate(authority, await test.Data.AuthorityAsync());
        });

    private static async Task IssueAsync(BrowserHttpsHost host, BrowserCookies cookies, CancellationToken cancellationToken = default)
    {
        var result = await host.SendAsync("/issue", cookies, "POST", cancellationToken: cancellationToken);
        Assert.True(result.Status == HttpStatusCode.NoContent && result.Record.Failure is null && !result.Record.UnexpectedFailure && !result.HasLocation,
            "The trusted fixture issue did not acknowledge its actual cookie operation.");
        SessionAttributes(result);
        cookies.Apply(result);
        Assert.True(cookies.Session is { Length: > 0 and <= 2048 }, "The actual session cookie is absent or outside its bound.");
    }

    private static async Task<string> TokenAsync(BrowserHttpsHost host, BrowserCookies cookies)
    {
        var result = await host.SendAsync("/token", cookies);
        BrowserHttpsFixture.Kind(result, ServerSessionRequestKind.Authenticated);
        Assert.True(result.Status == HttpStatusCode.OK && result.Record.Failure is null && !result.Record.UnexpectedFailure &&
            result.PrivateBody.Length is > 0 and <= 4096 && !result.HasLocation,
            "The positive new request did not return its bounded private request token.");
        foreach (var cookie in result.SetCookies)
        {
            Assert.True(cookie.Name == BrowserHttpsFixture.AntiforgeryCookie && cookie.Secure && cookie.HttpOnly &&
                cookie.Path == "/" && !cookie.Domain.HasValue && cookie.SameSite == Microsoft.Net.Http.Headers.SameSiteMode.Strict &&
                cookie.Expires is null && cookie.MaxAge is null,
                "The AF cookie attributes or nonpersistent scope differ from the fixed contract.");
        }
        cookies.Apply(result);
        Assert.True(cookies.Antiforgery is { Length: > 0 and <= 4096 }, "The actual AF cookie is absent or outside its bound.");
        return result.PrivateBody;
    }

    private static async Task AcceptedUnsafeAsync(BrowserHttpsHost host, BrowserCookies cookies, string token)
    {
        var result = await host.SendAsync("/admit", cookies, "POST", token, body: "owned dispatch"u8.ToArray());
        BrowserHttpsFixture.Kind(result, ServerSessionRequestKind.Authenticated);
        Assert.True(result.Status == HttpStatusCode.NoContent && result.Record.Dispatched == 1 && result.Record.BodyReads > 0,
            "Actual valid unsafe admission did not precede exactly one application dispatch.");
    }

    private static async Task DeniedUnsafeAsync(BrowserHttpsHost host, BrowserCookies cookies, string token,
        string[]? originValues = null, string[]? tokenValues = null, string? rawCookie = null)
    {
        var result = await host.SendAsync("/admit", cookies, "POST", token, originValues, tokenValues, rawCookie,
            "__RequestVerificationToken=must-not-be-read"u8.ToArray());
        BrowserHttpsFixture.Kind(result, ServerSessionRequestKind.CsrfRejected);
        BrowserHttpsFixture.NoBodyOrDispatch(result);
        Assert.True(result.Status == HttpStatusCode.Forbidden && result.SetCookies.Length == 0,
            "The unsafe invalid-header request was not a cookie-free categorical refusal.");
    }

    private static void SessionAttributes(BrowserExchange result)
    {
        Assert.True(result.SetCookies.Length == 1, "A fresh issue did not emit exactly one session cookie.");
        var cookie = result.SetCookies[0];
        Assert.True(cookie.Name == BrowserHttpsFixture.SessionCookie && cookie.Secure && cookie.HttpOnly && cookie.Path == "/" &&
            !cookie.Domain.HasValue && cookie.SameSite == Microsoft.Net.Http.Headers.SameSiteMode.Lax &&
            cookie.Expires is null && cookie.MaxAge is null,
            "The session cookie attributes or nonpersistent scope differ from the fixed contract.");
    }

    private static void DeletedCookies(BrowserExchange result)
    {
        Assert.True(result.SetCookies.Length == 2 && result.SetCookies.Select(cookie => cookie.Name.ToString())
            .ToHashSet(StringComparer.Ordinal).SetEquals([BrowserHttpsFixture.SessionCookie, BrowserHttpsFixture.AntiforgeryCookie]) &&
            result.SetCookies.All(cookie => cookie.Value.Length == 0 && cookie.Secure && cookie.HttpOnly && cookie.Path == "/" &&
                !cookie.Domain.HasValue && cookie.Expires is DateTimeOffset expiry && expiry < DateTimeOffset.UtcNow),
            "An acknowledged local revoke did not clear exactly both fixed host cookies.");
    }

    private static ServerSessionCreateIntent RequiredCreate(BrowserHttpsHost host)
    {
        Assert.True(host.Writer.LastCreate is not null, "The genuine restricted creation intent is absent.");
        return host.Writer.LastCreate!;
    }

    private static DefaultHttpContext DirectContext(BrowserHttpsHost host, IServiceProvider requestServices,
        BrowserCookies cookies, string method = "GET", string? token = null)
    {
        var context = new DefaultHttpContext { RequestServices = requestServices };
        context.Request.Scheme = "https";
        context.Request.Host = new HostString(host.Origin.Host, host.Origin.Port);
        context.Request.Method = method;
        context.Request.Headers.Cookie = cookies.Header;
        context.Request.Headers.Origin = host.Origin.GetLeftPart(UriPartial.Authority);
        if (token is not null) context.Request.Headers[BrowserHttpsFixture.TokenHeader] = token;
        context.Response.Body = Stream.Null;
        return context;
    }

    private static async Task ClosedFailureAsync(ServerSessionIdentityFailureKind expected, Func<Task> call)
    {
        Exception? failure = null;
        try { await call(); } catch (Exception error) { failure = error; }
        Assert.True(failure is ServerSessionIdentityException closed && closed.Kind == expected && closed.InnerException is null,
            "A public operation did not retain its expected closed private-safe exception.");
    }

    private static async Task ObserveAsync(Task operation)
    { try { await operation; } catch (Exception) { /* Settle the exact owned operation; never substitute success. */ } }
}
