using Weft.Application;
using Weft.Application.MappingTools;
using Weft.Domain;

namespace Weft.Tests;

/// <summary>Cancellation/fault interleavings at the read port.</summary>
public sealed class MappingToolCancellationPrecedenceTests
{
    private static readonly ChatCitationTarget[] ReportTarget = [new(ChatCitationKind.ProjectionReport)];

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Execute_preserves_original_caller_token_when_the_read_cancels_then_faults(int fault)
    {
        var fixture = new MappingToolFixture();
        var service = fixture.Service();
        using var caller = new CancellationTokenSource();
        fixture.OnRead = dependencyToken =>
        {
            caller.Cancel();
            throw Fault(fault, dependencyToken);
        };
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.InvokeAsync(service, "get_mapping_summary", "{}", cancellationToken: caller.Token));
        Assert.Equal(caller.Token, error.CancellationToken);
        Assert.DoesNotContain(MappingToolFixture.PrivateText, error.Message, StringComparison.Ordinal);
        fixture.OnRead = null;
        Assert.Equal(MappingToolOutcome.Available, (await fixture.InvokeAsync(service, "get_mapping_summary", "{}")).Outcome);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Citations_preserve_original_caller_token_when_the_read_cancels_then_faults(int fault)
    {
        var fixture = new MappingToolFixture();
        var service = fixture.Service();
        using var caller = new CancellationTokenSource();
        fixture.OnRead = dependencyToken =>
        {
            caller.Cancel();
            throw Fault(fault, dependencyToken);
        };
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ValidateCitationsAsync(MappingToolFixture.Actor, fixture.Conversation, ReportTarget, caller.Token));
        Assert.Equal(caller.Token, error.CancellationToken);
        Assert.DoesNotContain(MappingToolFixture.PrivateText, error.Message, StringComparison.Ordinal);
        fixture.OnRead = null;
        Assert.Equal(MappingCitationOutcome.Valid,
            (await service.ValidateCitationsAsync(MappingToolFixture.Actor, fixture.Conversation, ReportTarget)).Outcome);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    public async Task A_fault_without_caller_cancellation_stays_categorical(bool citations, int fault)
    {
        var fixture = new MappingToolFixture();
        var service = fixture.Service();
        fixture.OnRead = dependencyToken => throw Fault(fault, dependencyToken);
        if (citations)
        {
            var result = await service.ValidateCitationsAsync(MappingToolFixture.Actor, fixture.Conversation, ReportTarget);
            Assert.Equal(fault == 3 ? MappingCitationOutcome.ResourceLimit : MappingCitationOutcome.DependencyFailure, result.Outcome);
            Assert.Empty(result.Citations);
            Assert.DoesNotContain(MappingToolFixture.PrivateText, result.ToString(), StringComparison.Ordinal);
        }
        else
        {
            var result = await fixture.InvokeAsync(service, "get_mapping_summary", "{}");
            Assert.Equal(fault == 3 ? MappingToolOutcome.ResourceLimit : MappingToolOutcome.DependencyFailure, result.Outcome);
            Assert.Null(result.Data);
            Assert.Empty(result.Citations);
            Assert.DoesNotContain(MappingToolFixture.PrivateText, result.Result.Json, StringComparison.Ordinal);
        }
    }

    private static Exception Fault(int fault, CancellationToken dependencyToken) => fault switch
    {
        0 => new InvalidOperationException(MappingToolFixture.PrivateText),
        1 => new OperationCanceledException(MappingToolFixture.PrivateText, null, dependencyToken),
        2 => new RevisionReportServiceException(RevisionReportFailureCategory.BackendUnavailable),
        3 => new RevisionReportValidationException(RevisionReportValidationFailure.ResourceLimit),
        _ => throw new ArgumentOutOfRangeException(nameof(fault))
    };
}
