using Braid.Application;
using Braid.Domain;

namespace Braid.Tests;

public sealed class AiTokenAdmissionTests
{
    [Fact]
    public void A_token_cap_never_admits_an_unverified_estimate()
    {
        var request = Request();
        Assert.Equal(AiQuotaAdmission.TokenBoundUnavailable, AiTokenAdmission.Evaluate(request, 0, 0, true, null, 100, null));
        Assert.Equal(AiQuotaAdmission.Allowed, AiTokenAdmission.Evaluate(request, 0, 0, true, 2, null, null));
    }

    [Fact]
    public void The_entire_verified_turn_must_fit_alongside_spent_and_reserved_tokens()
    {
        var request = Request(); var bound = new AiTokenUpperBound(request, "test-bound", 1, 40);
        Assert.Equal(AiQuotaAdmission.Allowed, AiTokenAdmission.Evaluate(request, 1, 60, true, null, 100, bound));
        Assert.Equal(AiQuotaAdmission.TokenLimitReached, AiTokenAdmission.Evaluate(request, 1, 61, true, null, 100, bound));
        Assert.Equal(AiQuotaAdmission.TokenLimitReached, AiTokenAdmission.Evaluate(request, 1, 100, true, null, 100, bound));
    }

    [Fact]
    public void A_bound_cannot_be_reused_for_another_request()
    {
        var original = Request(); var bound = new AiTokenUpperBound(original, "test-bound", 1, 40);
        var changed = new AiTokenAdmissionRequest(original.Conversation, Guid.NewGuid(), original.EnvelopeFingerprint, original.ProfileFingerprint, original.Budget, original.DeadlineUtc);
        Assert.Equal(AiQuotaAdmission.TokenBoundMismatch, AiTokenAdmission.Evaluate(changed, 0, 0, true, null, 100, bound));
    }

    [Fact]
    public void Original_envelope_profile_and_conversation_are_part_of_the_bound()
    {
        var original = Request(); var bound = new AiTokenUpperBound(original, "test-bound", 1, 40);
        foreach (var changed in new[]
        {
            new AiTokenAdmissionRequest(original.Conversation, original.TurnId, Sha256Digest.FromBytes([3]), original.ProfileFingerprint, original.Budget, original.DeadlineUtc),
            new AiTokenAdmissionRequest(original.Conversation, original.TurnId, original.EnvelopeFingerprint, Sha256Digest.FromBytes([3]), original.Budget, original.DeadlineUtc),
            new AiTokenAdmissionRequest(Request().Conversation, original.TurnId, original.EnvelopeFingerprint, original.ProfileFingerprint, original.Budget, original.DeadlineUtc),
            new AiTokenAdmissionRequest(original.Conversation, original.TurnId, original.EnvelopeFingerprint, original.ProfileFingerprint, new(32, 65536, 4), original.DeadlineUtc),
            new AiTokenAdmissionRequest(original.Conversation, original.TurnId, original.EnvelopeFingerprint, original.ProfileFingerprint, original.Budget, original.DeadlineUtc.AddMinutes(1))
        }) Assert.Equal(AiQuotaAdmission.TokenBoundMismatch, AiTokenAdmission.Evaluate(changed, 0, 0, true, null, 100, bound));
    }

    [Fact]
    public void Uncertain_usage_keeps_the_reserved_budget_unavailable()
    {
        var request = Request(); var bound = new AiTokenUpperBound(request, "test-bound", 1, 21);
        Assert.Equal(AiQuotaAdmission.TokenLimitReached, AiTokenAdmission.Evaluate(request, 1, 80, true, null, 100, bound));
        Assert.Equal(AiQuotaAdmission.Allowed, AiTokenAdmission.Evaluate(request, 1, 79, true, null, 100, bound));
    }

    [Fact]
    public void Missing_historical_coverage_cannot_be_treated_as_unused_budget()
    {
        var request = Request(); var bound = new AiTokenUpperBound(request, "test-bound", 1, 1);
        Assert.Equal(AiQuotaAdmission.CoverageUnavailable, AiTokenAdmission.Evaluate(request, 0, 0, false, 10, 100, bound));
        Assert.Equal(AiQuotaAdmission.Allowed, AiTokenAdmission.Evaluate(request, 0, 0, false, null, null, null));
    }

    [Fact]
    public void Arithmetic_near_Int64_max_never_wraps_into_capacity()
    {
        var request = Request(); var bound = new AiTokenUpperBound(request, "test-bound", 1, 2);
        Assert.Equal(AiQuotaAdmission.TokenLimitReached, AiTokenAdmission.Evaluate(request, 0, long.MaxValue - 1, true, null, long.MaxValue, bound));
        Assert.Throws<ArgumentException>(() => AiTokenAdmission.Evaluate(request, -1, 0, true, null, 100, bound));
        Assert.Throws<ArgumentException>(() => AiTokenAdmission.Evaluate(request, 0, 0, true, null, 0, bound));
    }

    private static AiTokenAdmissionRequest Request() => new(new(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
        new(Guid.NewGuid()), Guid.NewGuid()), Guid.NewGuid(), Sha256Digest.FromBytes([1]), Sha256Digest.FromBytes([2]), new(16, 65536, 3),
        new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
}
