using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Braid.Application;
using Braid.Domain;

namespace Braid.Identity;

public sealed partial class ServerSessionTicketStore
{
    public TrustedSessionIssuance BeginTrustedIssuance(HttpContext context, Guid bindingId,
        long bindingVersion, PrincipalId verifiedOwner, DateTimeOffset verifiedDeadlineUtc,
        CancellationToken cancellationToken)
    {
        if (context is null)
            RequireContext(context, cancellationToken);
        var features = RequestFeatures(context);
        IssuanceEntry entry;
        // The collection itself is the shared lock for first installation by different stores.
        lock (features)
        {
            var feature = features.Get<IssuanceFeature>();
            if (feature is null)
            {
                feature = new();
                features.Set(feature);
            }
            if (feature.Entries.ContainsKey(origin))
                throw Untrusted();
            entry = new(context, features, feature);
            feature.Entries.Add(origin, entry);
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.RequestAborted.ThrowIfCancellationRequested();
            if (context.Response.HasStarted || bindingId == Guid.Empty || bindingVersion < 1 ||
                verifiedOwner is null || verifiedDeadlineUtc.Offset != TimeSpan.Zero ||
                verifiedDeadlineUtc.Ticks % 10 != 0 || verifiedDeadlineUtc == DateTimeOffset.MinValue ||
                verifiedDeadlineUtc == DateTimeOffset.MaxValue)
                throw Untrusted();
            var handle = ServerSessionHandle.Create();
            var envelope = new ServerSessionEnvelope(new(codec.OrganizationId, handle.Digest), bindingId,
                bindingVersion, verifiedOwner, Guid.NewGuid(), verifiedDeadlineUtc);
            lock (features)
            {
                RequireOrigin(context, entry);
                if (entry.State != EntryState.Reserved)
                    throw Untrusted();
                entry.Handle = handle;
                entry.Envelope = envelope;
                entry.Token = cancellationToken;
            }
            return new(() => CopyIssue(entry), () => ClearIssuance(entry));
        }
        catch
        {
            ClearIssuance(entry);
            throw;
        }
    }

    private AuthenticationTicket CopyIssue(IssuanceEntry entry)
    {
        ServerSessionEnvelope envelope;
        lock (entry.Features)
        {
            RequireOrigin(entry.Context, entry);
            if (entry.State != EntryState.Reserved || entry.Envelope is null ||
                entry.Context.Response.HasStarted)
                throw Untrusted();
            entry.Token.ThrowIfCancellationRequested();
            entry.Context.RequestAborted.ThrowIfCancellationRequested();
            envelope = entry.Envelope;
        }
        return ServerSessionCanonicalTicket.CreateIssue(envelope.Key.OrganizationId, envelope.Owner, envelope.Epoch);
    }

    private (IssuanceEntry Entry, ServerSessionHandle Handle, ServerSessionEnvelope Envelope,
        CancellationToken Token) ConsumeIssuance(HttpContext context)
    {
        var features = RequestFeatures(context);
        lock (features)
        {
            var feature = features.Get<IssuanceFeature>();
            if (feature is null)
            {
                feature = new();
                features.Set(feature);
            }
            if (!feature.Entries.TryGetValue(origin, out var entry))
            {
                entry = new(context, features, feature) { State = EntryState.Refused };
                feature.Entries.Add(origin, entry);
                throw Untrusted();
            }
            RequireOrigin(context, entry);
            if (entry.State != EntryState.Reserved || entry.Handle is null || entry.Envelope is null ||
                context.Response.HasStarted)
            {
                entry.State = EntryState.Refused;
                entry.Handle = null;
                entry.Envelope = null;
                entry.Token = default;
                throw Untrusted();
            }
            entry.State = EntryState.Consumed;
            return (entry, entry.Handle, entry.Envelope, entry.Token);
        }
    }

    private void RequireOrigin(HttpContext context, IssuanceEntry entry)
    {
        if (!ReferenceEquals(context, entry.Context) || !ReferenceEquals(RequestFeatures(context), entry.Features) ||
            !ReferenceEquals(entry.Features.Get<IssuanceFeature>(), entry.Feature) ||
            !entry.Feature.Entries.TryGetValue(origin, out var current) || !ReferenceEquals(current, entry))
            throw Untrusted();
    }

    private static void ClearIssuance(IssuanceEntry entry)
    {
        lock (entry.Features)
        {
            entry.State = EntryState.Refused;
            entry.Handle = null;
            entry.Envelope = null;
            entry.Token = default;
        }
    }

    private static IFeatureCollection RequestFeatures(HttpContext context)
    {
        try { return context.Features; }
        catch (ObjectDisposedException) { throw Untrusted(); }
    }

    private enum EntryState { Reserved, Consumed, Refused }

    private sealed class IssuanceFeature
    {
        internal Dictionary<object, IssuanceEntry> Entries { get; } = new(ReferenceEqualityComparer.Instance);
    }

    private sealed class IssuanceEntry(HttpContext context, IFeatureCollection features, IssuanceFeature feature)
    {
        internal HttpContext Context { get; } = context;
        internal IFeatureCollection Features { get; } = features;
        internal IssuanceFeature Feature { get; } = feature;
        internal EntryState State { get; set; }
        internal ServerSessionHandle? Handle { get; set; }
        internal ServerSessionEnvelope? Envelope { get; set; }
        internal CancellationToken Token { get; set; }
    }

    private static ServerSessionIdentityException Untrusted() =>
        ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.UntrustedIssuance);
}
