using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresExternalIdentityBindingTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task LiteralComponentsAndUtf8BoundaryKeysRemainDistinctThroughActualRestrictedReads()
    {
        await using var test = await PostgresExternalIdentityTestSupport.CreateAsync(fixture);
        var principal = await test.PrincipalAsync();
        var keys = new[]
        {
            new ExternalIdentityKey("provider", "Tenant", "Subject"),
            new ExternalIdentityKey("provider", "tenant", "Subject"),
            new ExternalIdentityKey("provider", "Tenant", "subject"),
            new ExternalIdentityKey("provider", " Tenant ", "Subject"),
            new ExternalIdentityKey("provider", "Tenant", " Subject "),
            new ExternalIdentityKey("provider", "Tenant", "\u00e9"),
            new ExternalIdentityKey("provider", "Tenant", "e\u0301"),
            new ExternalIdentityKey("provider", "a|b", "c"),
            new ExternalIdentityKey("provider", "a", "b|c"),
            new ExternalIdentityKey("provider-other", "Tenant", "Subject"),
            new ExternalIdentityKey(new string('a', 64), new string('\u00e9', 512), string.Concat(Enumerable.Repeat("\U0001f600", 128)))
        };
        var ids = new HashSet<Guid>();
        foreach (var key in keys)
        {
            var binding = await test.BindAsync(key, principal);
            Assert.True(ids.Add(binding.BindingId));
            Assert.Equal(1, binding.Version);
            Assert.True(binding.Enabled);
            Assert.Null(binding.DisabledAtUtc);
            Assert.Equal(TimeSpan.Zero, binding.CreatedAtUtc.Offset);
            PostgresExternalIdentityTestSupport.SameBinding(binding,
                PostgresExternalIdentityTestSupport.Resolved(await test.Reader.ResolveAsync(key)));
            PostgresExternalIdentityTestSupport.SameBinding(binding,
                PostgresExternalIdentityTestSupport.Resolved(await test.Reader.ResolveBindingAsync(binding.BindingId)));
        }
        Assert.Equal(keys.Length, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.external_identity_bindings"));
        Assert.Equal(new long[] { 0, 1, 0, 0, 0, 0, keys.Length }, await test.CountsAsync());
    }

    [PostgresFact]
    public async Task ReusedReaderObservesInactiveAndDisabledBindingsWithoutLeakingNegativeBindingFacts()
    {
        await using var test = await PostgresExternalIdentityTestSupport.CreateAsync(fixture);
        var principal = await test.PrincipalAsync();
        var key = PostgresExternalIdentityTestSupport.Key();
        var binding = await test.BindAsync(key, principal);
        var before = await test.BindingsAsync();
        PostgresExternalIdentityTestSupport.Resolved(await test.Reader.ResolveAsync(key));
        PostgresExternalIdentityTestSupport.Hidden(ExternalIdentityResolutionOutcome.Unknown,
            await test.Reader.ResolveAsync(PostgresExternalIdentityTestSupport.Key("unbound")));
        PostgresExternalIdentityTestSupport.Hidden(ExternalIdentityResolutionOutcome.Unknown,
            await test.Reader.ResolveBindingAsync(Guid.NewGuid()));
        await test.Case.ExecuteAsync("UPDATE weft.principals SET active=false WHERE id=$1", principal.Value);
        PostgresExternalIdentityTestSupport.Hidden(ExternalIdentityResolutionOutcome.PrincipalInactive, await test.Reader.ResolveAsync(key));
        PostgresExternalIdentityTestSupport.Hidden(ExternalIdentityResolutionOutcome.PrincipalInactive, await test.Reader.ResolveBindingAsync(binding.BindingId));
        Assert.True(string.Equals(before, await test.BindingsAsync(), StringComparison.Ordinal), "The private binding snapshot changed.");
        // Revocation remains possible while the principal is inactive. Disabled wins
        // the closed negative classification, without returning the owner's record.
        var disabled = await test.Owner.DisableAsync(key, principal, binding.Version);
        Assert.Equal(ExternalIdentityBindingOwnerOutcome.Disabled, disabled.Outcome);
        PostgresExternalIdentityTestSupport.Hidden(ExternalIdentityResolutionOutcome.Disabled, await test.Reader.ResolveAsync(key));
        PostgresExternalIdentityTestSupport.Hidden(ExternalIdentityResolutionOutcome.Disabled, await test.Reader.ResolveBindingAsync(binding.BindingId));
    }

    [PostgresFact]
    public async Task BindingRequiresExplicitActivePrincipalAndDoesNotPrepareAnyGrantOrAdmission()
    {
        await using var test = await PostgresExternalIdentityTestSupport.CreateAsync(fixture);
        var key = PostgresExternalIdentityTestSupport.Key();
        var missing = new PrincipalId(Guid.NewGuid());
        var initial = await test.CountsAsync();
        var denied = await test.Owner.BindAsync(key, missing);
        Assert.Equal(ExternalIdentityBindingOwnerOutcome.PrincipalMissing, denied.Outcome);
        Assert.Null(denied.Binding);
        Assert.Equal(initial, await test.CountsAsync());
        var inactive = await test.PrincipalAsync(active: false);
        denied = await test.Owner.BindAsync(key, inactive);
        Assert.Equal(ExternalIdentityBindingOwnerOutcome.PrincipalInactive, denied.Outcome);
        Assert.Null(denied.Binding);
        Assert.Equal(new long[] { 0, 1, 0, 0, 0, 0, 0 }, await test.CountsAsync());
        var active = await test.PrincipalAsync();
        var binding = await test.BindAsync(key, active);
        var retained = await test.BindingsAsync();
        var replay = await test.Owner.BindAsync(key, active);
        Assert.Equal(ExternalIdentityBindingOwnerOutcome.Existing, replay.Outcome);
        PostgresExternalIdentityTestSupport.SameBinding(binding, Assert.IsType<ExternalIdentityBinding>(replay.Binding));
        denied = await test.Owner.BindAsync(key, inactive);
        Assert.Equal(ExternalIdentityBindingOwnerOutcome.BindingConflict, denied.Outcome);
        Assert.Null(denied.Binding);
        Assert.True(string.Equals(retained, await test.BindingsAsync(), StringComparison.Ordinal), "The private binding snapshot changed.");
        Assert.Equal(new long[] { 0, 2, 0, 0, 0, 0, 1 }, await test.CountsAsync());
    }

    [PostgresFact]
    public async Task OwnerPreparationCreatesOnlyExplicitRowsAndRefusesMissingOrInactiveDependencies()
    {
        await using var test = await PostgresExternalIdentityTestSupport.CreateAsync(fixture);
        var principal = new PrincipalId(Guid.NewGuid());
        var organization = Guid.NewGuid();
        Assert.Equal(ExternalIdentityPrincipalPreparationOutcome.Created, await test.Owner.EnsurePrincipalAsync(principal));
        var createdAt = await test.Case.ScalarAsync<string>("SELECT created_at::text FROM weft.principals WHERE id=$1", principal.Value);
        Assert.Equal(ExternalIdentityPrincipalPreparationOutcome.Existing, await test.Owner.EnsurePrincipalAsync(principal));
        Assert.Equal(createdAt, await test.Case.ScalarAsync<string>("SELECT created_at::text FROM weft.principals WHERE id=$1", principal.Value));
        Assert.Equal(ExternalIdentityOrganizationAdmissionPreparationOutcome.OrganizationMissing,
            await test.Owner.EnsureOrganizationAdmissionAsync(organization, principal));
        Assert.Equal(new long[] { 0, 1, 0, 0, 0, 0, 0 }, await test.CountsAsync());
        await test.Case.ExecuteAsync("INSERT INTO weft.organizations(id,name) VALUES ($1,'Explicit existing organization')", organization);
        Assert.Equal(ExternalIdentityOrganizationAdmissionPreparationOutcome.PrincipalMissing,
            await test.Owner.EnsureOrganizationAdmissionAsync(organization, new PrincipalId(Guid.NewGuid())));
        Assert.Equal(ExternalIdentityOrganizationAdmissionPreparationOutcome.Created,
            await test.Owner.EnsureOrganizationAdmissionAsync(organization, principal));
        var membership = await test.Case.ScalarAsync<string>("SELECT to_jsonb(m)::text FROM weft.organization_memberships m WHERE organization_id=$1 AND principal_id=$2", organization, principal.Value);
        Assert.Equal(ExternalIdentityOrganizationAdmissionPreparationOutcome.Existing,
            await test.Owner.EnsureOrganizationAdmissionAsync(organization, principal));
        Assert.True(string.Equals(membership, await test.Case.ScalarAsync<string>("SELECT to_jsonb(m)::text FROM weft.organization_memberships m WHERE organization_id=$1 AND principal_id=$2", organization, principal.Value), StringComparison.Ordinal), "The private admission snapshot changed.");
        await test.Case.ExecuteAsync("UPDATE weft.organization_memberships SET active=false WHERE organization_id=$1 AND principal_id=$2", organization, principal.Value);
        Assert.Equal(ExternalIdentityOrganizationAdmissionPreparationOutcome.AdmissionInactive,
            await test.Owner.EnsureOrganizationAdmissionAsync(organization, principal));
        Assert.False(await test.Case.ScalarAsync<bool>("SELECT active FROM weft.organization_memberships WHERE organization_id=$1 AND principal_id=$2", organization, principal.Value));
        await test.Case.ExecuteAsync("UPDATE weft.principals SET active=false WHERE id=$1", principal.Value);
        Assert.Equal(ExternalIdentityPrincipalPreparationOutcome.Inactive, await test.Owner.EnsurePrincipalAsync(principal));
        Assert.Equal(ExternalIdentityOrganizationAdmissionPreparationOutcome.PrincipalInactive,
            await test.Owner.EnsureOrganizationAdmissionAsync(organization, principal));
        Assert.False(await test.Case.ScalarAsync<bool>("SELECT active FROM weft.principals WHERE id=$1", principal.Value));
        Assert.Equal(new long[] { 1, 1, 1, 0, 0, 0, 0 }, await test.CountsAsync());
    }

    [PostgresFact]
    public async Task DisableRequiresExactOwnerAndCurrentVersionAndNeverReactivatesOnReplay()
    {
        await using var test = await PostgresExternalIdentityTestSupport.CreateAsync(fixture);
        var principal = await test.PrincipalAsync();
        var other = await test.PrincipalAsync();
        var key = PostgresExternalIdentityTestSupport.Key();
        var original = await test.BindAsync(key, principal);
        var before = await test.BindingsAsync();
        var unknown = await test.Owner.DisableAsync(PostgresExternalIdentityTestSupport.Key("unknown"), principal, 1);
        Assert.Equal(ExternalIdentityBindingOwnerOutcome.UnknownBinding, unknown.Outcome);
        Assert.Null(unknown.Binding);
        var refusal = await test.Owner.DisableAsync(key, other, 1);
        Assert.Equal(ExternalIdentityBindingOwnerOutcome.OwnerConflict, refusal.Outcome);
        Assert.Null(refusal.Binding);
        refusal = await test.Owner.DisableAsync(key, principal, 2);
        Assert.Equal(ExternalIdentityBindingOwnerOutcome.VersionConflict, refusal.Outcome);
        Assert.Null(refusal.Binding);
        Assert.True(string.Equals(before, await test.BindingsAsync(), StringComparison.Ordinal), "The private binding snapshot changed.");
        var result = await test.Owner.DisableAsync(key, principal, 1);
        Assert.Equal(ExternalIdentityBindingOwnerOutcome.Disabled, result.Outcome);
        var disabled = Assert.IsType<ExternalIdentityBinding>(result.Binding);
        Assert.Equal(original.BindingId, disabled.BindingId);
        Assert.True(original.Key == disabled.Key);
        Assert.Equal(original.PrincipalId, disabled.PrincipalId);
        Assert.Equal(original.CreatedAtUtc, disabled.CreatedAtUtc);
        Assert.Equal(2, disabled.Version);
        Assert.False(disabled.Enabled);
        Assert.NotNull(disabled.DisabledAtUtc);
        Assert.True(disabled.DisabledAtUtc >= original.CreatedAtUtc);
        var retained = await test.BindingsAsync();
        refusal = await test.Owner.DisableAsync(key, principal, 1);
        Assert.Equal(ExternalIdentityBindingOwnerOutcome.VersionConflict, refusal.Outcome);
        Assert.Null(refusal.Binding);
        result = await test.Owner.DisableAsync(key, principal, 2);
        Assert.Equal(ExternalIdentityBindingOwnerOutcome.AlreadyDisabled, result.Outcome);
        PostgresExternalIdentityTestSupport.SameBinding(disabled, Assert.IsType<ExternalIdentityBinding>(result.Binding));
        result = await test.Owner.BindAsync(key, principal);
        Assert.Equal(ExternalIdentityBindingOwnerOutcome.Existing, result.Outcome);
        PostgresExternalIdentityTestSupport.SameBinding(disabled, Assert.IsType<ExternalIdentityBinding>(result.Binding));
        Assert.True(string.Equals(retained, await test.BindingsAsync(), StringComparison.Ordinal), "The private binding snapshot changed.");
    }

    [PostgresFact]
    public async Task ConcurrentDistinctOwnerBindsHaveOneDurableWinnerAndNeverReassignTheExactKey()
    {
        await using var test = await PostgresExternalIdentityTestSupport.CreateAsync(fixture);
        var first = await test.PrincipalAsync();
        var second = await test.PrincipalAsync();
        var key = PostgresExternalIdentityTestSupport.Key();
        await using var firstOwner = new ExternalIdentityBindingProvisioner(test.Case.MigrationConnectionString);
        await using var secondOwner = new ExternalIdentityBindingProvisioner(test.Case.MigrationConnectionString);
        // Independent pools dispatch without awaiting either result. Scheduling is
        // not asserted; the durable one-winner/refusal facts are the oracle.
        var firstCall = firstOwner.BindAsync(key, first);
        var secondCall = secondOwner.BindAsync(key, second);
        var results = await Task.WhenAll(firstCall, secondCall);
        var winner = Assert.Single(results, result => result.Outcome == ExternalIdentityBindingOwnerOutcome.Created);
        var loser = Assert.Single(results, result => result.Outcome == ExternalIdentityBindingOwnerOutcome.BindingConflict);
        Assert.Null(loser.Binding);
        var binding = Assert.IsType<ExternalIdentityBinding>(winner.Binding);
        Assert.Equal(1, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.external_identity_bindings"));
        PostgresExternalIdentityTestSupport.SameBinding(binding,
            PostgresExternalIdentityTestSupport.Resolved(await test.Reader.ResolveAsync(key)));
        var retained = await test.BindingsAsync();
        var replay = await test.Owner.BindAsync(key, binding.PrincipalId);
        Assert.Equal(ExternalIdentityBindingOwnerOutcome.Existing, replay.Outcome);
        PostgresExternalIdentityTestSupport.SameBinding(binding, Assert.IsType<ExternalIdentityBinding>(replay.Binding));
        Assert.True(string.Equals(retained, await test.BindingsAsync(), StringComparison.Ordinal), "The private binding snapshot changed.");
    }

    [PostgresFact]
    public async Task ConcurrentSameVersionDisablesProduceOneIncrementAndOneStaleVersionRefusal()
    {
        await using var test = await PostgresExternalIdentityTestSupport.CreateAsync(fixture);
        var principal = await test.PrincipalAsync();
        var key = PostgresExternalIdentityTestSupport.Key();
        var original = await test.BindAsync(key, principal);
        await using var otherOwner = new ExternalIdentityBindingProvisioner(test.Case.MigrationConnectionString);
        var first = test.Owner.DisableAsync(key, principal, original.Version);
        var second = otherOwner.DisableAsync(key, principal, original.Version);
        var results = await Task.WhenAll(first, second);
        var changed = Assert.Single(results, result => result.Outcome == ExternalIdentityBindingOwnerOutcome.Disabled);
        Assert.Null(Assert.Single(results, result => result.Outcome == ExternalIdentityBindingOwnerOutcome.VersionConflict).Binding);
        var disabled = Assert.IsType<ExternalIdentityBinding>(changed.Binding);
        Assert.Equal(original.BindingId, disabled.BindingId);
        Assert.Equal(original.CreatedAtUtc, disabled.CreatedAtUtc);
        Assert.Equal(2, disabled.Version);
        Assert.False(disabled.Enabled);
        var retained = await test.BindingsAsync();
        var replay = await test.Owner.DisableAsync(key, principal, disabled.Version);
        Assert.Equal(ExternalIdentityBindingOwnerOutcome.AlreadyDisabled, replay.Outcome);
        PostgresExternalIdentityTestSupport.SameBinding(disabled, Assert.IsType<ExternalIdentityBinding>(replay.Binding));
        Assert.True(string.Equals(retained, await test.BindingsAsync(), StringComparison.Ordinal), "The private binding snapshot changed.");
    }
}
