using Braid.Application;

namespace Braid.Tests;

public sealed class AiUsageQueryTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Query_retains_explicit_scope_and_normalizes_only_submicrosecond_precision()
    {
        var org = Guid.NewGuid(); var project = Guid.NewGuid(); var principal = Guid.NewGuid();
        var query = new AiUsageQuery(org, Start.AddTicks(3), Start.AddHours(1).AddTicks(9),
            AiUsageGrouping.ModelProfile, project, principal, "bounded-model", 2, 100);
        Assert.Equal(org, query.OrganizationId); Assert.Equal(project, query.ProjectId); Assert.Equal(principal, query.PrincipalId);
        Assert.Equal("bounded-model", query.ProfileId); Assert.Equal(2, query.ProfileVersion);
        Assert.Equal(Start, query.Since); Assert.Equal(Start.AddHours(1), query.Until);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void Page_bounds_are_explicit(int limit) => Assert.Throws<ArgumentException>(() =>
        new AiUsageQuery(Guid.NewGuid(), Start, Start.AddHours(1), limit: limit));

    [Theory]
    [InlineData("")]
    [InlineData("model name")]
    [InlineData("../model")]
    [InlineData("MODEL")]
    [InlineData("-model")]
    public void Profile_filter_is_a_fixed_identifier(string profile) => Assert.Throws<ArgumentException>(() =>
        new AiUsageQuery(Guid.NewGuid(), Start, Start.AddHours(1), profileId: profile));

    [Fact]
    public void Empty_ids_unknown_grouping_or_version_without_profile_are_refused()
    {
        Assert.Throws<ArgumentException>(() => new AiUsageQuery(Guid.Empty, Start, Start.AddHours(1)));
        Assert.Throws<ArgumentException>(() => new AiUsageQuery(Guid.NewGuid(), Start, Start.AddHours(1), projectId: Guid.Empty));
        Assert.Throws<ArgumentException>(() => new AiUsageQuery(Guid.NewGuid(), Start, Start.AddHours(1), principalId: Guid.Empty));
        Assert.Throws<ArgumentException>(() => new AiUsageQuery(Guid.NewGuid(), Start, Start.AddHours(1), grouping: (AiUsageGrouping)100));
        Assert.Throws<ArgumentException>(() => new AiUsageQuery(Guid.NewGuid(), Start, Start.AddHours(1), profileVersion: 2));
        Assert.Throws<ArgumentException>(() => new AiUsageQuery(Guid.NewGuid(), Start, Start.AddHours(1), profileId: "model", profileVersion: 0));
    }

    [Fact]
    public void Invalid_or_non_UTC_windows_are_not_silently_widened()
    {
        Assert.Throws<ArgumentException>(() => new AiUsageQuery(Guid.NewGuid(), Start, Start));
        Assert.Throws<ArgumentException>(() => new AiUsageQuery(Guid.NewGuid(), Start.AddHours(1), Start));
        Assert.Throws<ArgumentException>(() => new AiUsageQuery(Guid.NewGuid(), Start.ToOffset(TimeSpan.FromHours(1)), Start.AddHours(1)));
        Assert.Throws<ArgumentException>(() => new AiUsageQuery(Guid.NewGuid(), Start, Start.AddTicks(1)));
    }

    [Fact]
    public void Cursor_keeps_the_exact_group_and_is_bound_to_filters_grouping_and_window()
    {
        var org = Guid.NewGuid(); var principal = Guid.NewGuid();
        var query = new AiUsageQuery(org, Start, Start.AddDays(1), AiUsageGrouping.ModelProfile,
            principalId: principal, profileId: "bounded-model");
        var cursor = query.Cursor("bounded-model:2");
        Assert.Equal("bounded-model:2", new AiUsageQuery(org, Start, Start.AddDays(1), AiUsageGrouping.ModelProfile,
            principalId: principal, profileId: "bounded-model", limit: 100, cursor: cursor).Position);
        Assert.Throws<ArgumentException>(() => new AiUsageQuery(org, Start, Start.AddDays(2), AiUsageGrouping.ModelProfile,
            principalId: principal, profileId: "bounded-model", cursor: cursor));
        Assert.Throws<ArgumentException>(() => new AiUsageQuery(org, Start, Start.AddDays(1), AiUsageGrouping.Principal,
            principalId: principal, profileId: "bounded-model", cursor: cursor));
        Assert.Throws<ArgumentException>(() => new AiUsageQuery(org, Start, Start.AddDays(1), AiUsageGrouping.ModelProfile,
            principalId: Guid.NewGuid(), profileId: "bounded-model", cursor: cursor));
    }

    [Theory]
    [InlineData("model:01")]
    [InlineData("model:0")]
    [InlineData("model:1:extra")]
    [InlineData("../model:1")]
    public void Invalid_group_keys_never_become_cursors(string key) => Assert.Throws<ArgumentException>(() =>
        new AiUsageQuery(Guid.NewGuid(), Start, Start.AddDays(1), AiUsageGrouping.ModelProfile).Cursor(key));
}
