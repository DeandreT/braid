using Weft.Application;
using Weft.Domain;
using static Weft.Tests.MaintenanceObservationContractFacts;

namespace Weft.Tests;

/// <summary>Pure flat-witness fixtures. A successful match here is not actual SQL or filesystem proof.</summary>
public sealed class PrivatePublicationObservationContractTests
{
    [Fact]
    public void RequestsOwnInputWithoutMergingDifferentOrdinalsOrReorderingThem()
    {
        var slots = new List<PrivateStorageSlotObservation> { Final(3), Final(1) };
        var request = new PublicationObservationRequest(Project, slots);
        slots.Clear();
        Assert.Equal(new[] { 3, 1 }, request.Slots.Select(slot => slot.Ordinal));
        Assert.Equal(request.Slots[0].ActualIdentity, request.Slots[1].ActualIdentity);
        Assert.Throws<NotSupportedException>(() => ((IList<PrivateStorageSlotObservation>)request.Slots).Clear());
        Assert.Throws<ArgumentException>(() => new PublicationObservationRequest(Project, [Final(), Final()]));
        var foreign = new RevisionScope(Id(99), Id(2), Id(3), Id(4));
        Assert.Throws<ArgumentException>(() => new PublicationObservationRequest(Project, [Final(objectId: Object(scope: foreign))]));
    }

    [Theory]
    [InlineData(PublicationObservationOutcome.NoMatch, 0)]
    [InlineData(PublicationObservationOutcome.ExactlyOne, 1)]
    [InlineData(PublicationObservationOutcome.Ambiguous, 2)]
    public void ClosedOutcomesRequireExactlyZeroOneOrTwoDistinctWitnesses(PublicationObservationOutcome outcome, int count)
    {
        var input = Enumerable.Range(0, count).Select(index => Witness(publication: 50 + index, temp: 60 + index)).ToList();
        var result = new PublicationSlotObservation(0, outcome, input);
        input.Clear();
        Assert.Equal(count, result.Witnesses.Count);
        Assert.Throws<NotSupportedException>(() => ((IList<ObservedPublicationWitness>)result.Witnesses).Clear());
        if (count != 0) Assert.Throws<ArgumentException>(() => new PublicationSlotObservation(0, outcome, []));
        if (count == 0) Assert.Throws<ArgumentException>(() => new PublicationSlotObservation(0, outcome, [Witness()]));
    }

    [Fact]
    public void AmbiguityCannotBeManufacturedByDuplicatingEitherIdentity()
    {
        var witness = Witness();
        Assert.Throws<ArgumentException>(() => new PublicationSlotObservation(0, PublicationObservationOutcome.Ambiguous, [witness, witness]));
        Assert.Throws<ArgumentException>(() => new PublicationSlotObservation(0, PublicationObservationOutcome.Ambiguous,
            [witness, Witness(publication: 51)]));
        Assert.Throws<ArgumentException>(() => new PublicationSlotObservation(0, PublicationObservationOutcome.Ambiguous,
            [witness, Witness(temp: 61)]));
        Assert.Throws<ArgumentException>(() => new PublicationSlotObservation(0, PublicationObservationOutcome.Ambiguous,
            [Witness(), Witness(51, 61), Witness(52, 62)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PublicationSlotObservation(0, (PublicationObservationOutcome)99, []));
    }

    [Fact]
    public void BatchHasExactlyOneOrderedResultPerOriginalSlotAndOwnsResults()
    {
        var request = new PublicationObservationRequest(Project, [Final(3), Final(1)]);
        var results = new List<PublicationSlotObservation>
        {
            new(3, PublicationObservationOutcome.NoMatch, []),
            new(1, PublicationObservationOutcome.ExactlyOne, [Witness()])
        };
        var batch = new PrivatePublicationObservationBatch(request, At, results);
        results.Clear();
        Assert.Equal(new[] { 3, 1 }, batch.Results.Select(result => result.Ordinal));
        Assert.Same(request, batch.Request);
        Assert.Equal(At, batch.ObservedAtUtc);
        Assert.Throws<NotSupportedException>(() => ((IList<PublicationSlotObservation>)batch.Results).Clear());
        Assert.Throws<ArgumentException>(() => new PrivatePublicationObservationBatch(request, At, []));
        Assert.Throws<ArgumentException>(() => new PrivatePublicationObservationBatch(request, At, batch.Results.Reverse()));
        Assert.Throws<ArgumentException>(() => new PrivatePublicationObservationBatch(request, At,
            [batch.Results[0], batch.Results[0]]));
        Assert.Empty(new PrivatePublicationObservationBatch(new(Project, []), At, []).Results);
    }

    [Fact]
    public void FinalWitnessRequiresExactObjectAndTemporaryWitnessRequiresExactTempWithoutInventedObject()
    {
        var finalRequest = new PublicationObservationRequest(Project, [Final()]);
        Assert.Throws<ArgumentException>(() => new PrivatePublicationObservationBatch(finalRequest, At,
            [new(0, PublicationObservationOutcome.ExactlyOne, [Witness(objectId: Object(99))])]));
        var temporary = PrivateStorageSlotObservation.Temporary(0, Scope, PrivateObjectClass.NativeArtifact, Id(60), Identity());
        var request = new PublicationObservationRequest(Project, [temporary]);
        var matches = new PublicationSlotObservation(0, PublicationObservationOutcome.Ambiguous,
            [Witness(objectId: Object(5)), Witness(51, objectId: Object(6))]);
        Assert.Equal(PublicationObservationOutcome.Ambiguous, Assert.Single(new PrivatePublicationObservationBatch(request, At, [matches]).Results).Outcome);
        Assert.Null(temporary.ObjectId);
        Assert.Throws<ArgumentException>(() => new PrivatePublicationObservationBatch(request, At,
            [new(0, PublicationObservationOutcome.ExactlyOne, [Witness(temp: 99)])]));
    }

    [Fact]
    public void EveryBindingComponentIsComparedBeforeAnyPositiveAttribution()
    {
        PrivatePublicationFileIdentity[] changes =
        [
            Identity(root: Id(99)), Identity(control: Id(99)), Identity(boot: Id(99)),
            Identity(namespaceDevice: 99), Identity(namespaceInode: 99), Identity(user: 99),
            Identity(file: new(99, 7, 8, 9)), Identity(parent: new(99, 7, 8, 9)),
            Identity(file: new(42, 99, 8, 9), parent: new(41, 99, 8, 9)),
            Identity(file: new(42, 7, 99, 9), parent: new(41, 7, 99, 9)),
            Identity(file: new(42, 7, 8, 99), parent: new(41, 7, 8, 99))
        ];
        var request = new PublicationObservationRequest(Project, [Final()]);
        Assert.All(changes, identity => Assert.Throws<ArgumentException>(() => new PrivatePublicationObservationBatch(request, At,
            [new(0, PublicationObservationOutcome.ExactlyOne, [Witness(identity: identity)])])));
        RevisionScope[] foreign =
        [
            new(Id(99), Id(2), Id(3), Id(4)), new(Id(1), Id(99), Id(3), Id(4)),
            new(Id(1), Id(2), Id(99), Id(4)), new(Id(1), Id(2), Id(3), Id(99))
        ];
        Assert.All(foreign, scope => Assert.Throws<ArgumentException>(() => new PrivatePublicationObservationBatch(request, At,
            [new(0, PublicationObservationOutcome.ExactlyOne, [Witness(objectId: Object(scope: scope))])])));
        Assert.Throws<ArgumentException>(() => new PrivatePublicationObservationBatch(request, At,
            [new(0, PublicationObservationOutcome.ExactlyOne, [Witness(objectId: Object(objectClass: PrivateObjectClass.DerivedArtifact))])]));
    }

    [Fact]
    public void AmbiguousWitnessOrderIsFinalTempOrTemporaryObjectThenTemp()
    {
        var first = Witness();
        var second = Witness(51, 61);
        var final = new PublicationObservationRequest(Project, [Final()]);
        var ordered = new PublicationSlotObservation(0, PublicationObservationOutcome.Ambiguous, [first, second]);
        Assert.Same(ordered, Assert.Single(new PrivatePublicationObservationBatch(final, At, [ordered]).Results));
        Assert.Throws<ArgumentException>(() => new PrivatePublicationObservationBatch(final, At,
            [new(0, PublicationObservationOutcome.Ambiguous, [second, first])]));
        var temp = new PublicationObservationRequest(Project,
            [PrivateStorageSlotObservation.Temporary(0, Scope, PrivateObjectClass.NativeArtifact, Id(60), Identity())]);
        Assert.Throws<ArgumentException>(() => new PrivatePublicationObservationBatch(temp, At,
            [new(0, PublicationObservationOutcome.Ambiguous, [Witness(51, objectId: Object(6)), Witness(objectId: Object(5))])]));
    }

    [Fact]
    public void HundredSlotsAndTwoHundredWitnessesAreOwnedAndExtraSlotRefuses()
    {
        var slots = Enumerable.Range(0, 100).Select(index => Final(index)).ToArray();
        var request = new PublicationObservationRequest(Project, slots);
        var results = slots.Select(slot => new PublicationSlotObservation(slot.Ordinal,
            PublicationObservationOutcome.Ambiguous, [Witness(), Witness(51, 61)])).ToArray();
        var batch = new PrivatePublicationObservationBatch(request, At, results);
        Assert.Equal(100, batch.Results.Count);
        Assert.Equal(200, batch.Results.Sum(result => result.Witnesses.Count));
        Assert.Throws<ArgumentException>(() => new PublicationObservationRequest(Project, slots.Concat([Final()])));
        Assert.Throws<ArgumentException>(() => new PrivatePublicationObservationBatch(request, At, results.Concat([results[0]])));
    }

    [Fact]
    public void FlatWitnessRefusesContradictoryOriginWithoutRichHistoricalClaim()
    {
        var key = new PrivatePublicationKey(Id(10), Object(), Id(60));
        Assert.Throws<ArgumentException>(() => new ObservedPublicationWitness(Guid.Empty, key, Identity(), ObjectCleanupOrigin.ForIngestion(Scope, Id(70))));
        Assert.Throws<ArgumentException>(() => new ObservedPublicationWitness(Id(50), key, Identity(root: Id(99)), ObjectCleanupOrigin.ForIngestion(Scope, Id(70))));
        Assert.Throws<ArgumentException>(() => new ObservedPublicationWitness(Id(50), key, Identity(), ObjectCleanupOrigin.ForReport(Scope, Id(71), Id(72), 1)));
        Assert.Throws<ArgumentException>(() => new ObservedPublicationWitness(Id(50), key, Identity(),
            ObjectCleanupOrigin.ForIngestion(new(Id(1), Id(2), Id(3), Id(99)), Id(70))));
    }

    [Fact]
    public void CancellationPrecedesEnumerationAcrossEveryBoundedPublicationSnapshot()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var request = new PublicationObservationRequest(Project, []);
        Assert.Throws<OperationCanceledException>(() => new PublicationObservationRequest(Project, [], cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => new PublicationSlotObservation(0, PublicationObservationOutcome.NoMatch, [], cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => new PrivatePublicationObservationBatch(request, At, [], cancellation.Token));
    }
}
