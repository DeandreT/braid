using System.Text.Json;
using Braid.Domain;

namespace Braid.Application;

/// <summary>Trusted coverage facts appended after model notes; cannot be omitted or downgraded by a model.</summary>
internal static class CreationEvidenceCoverage
{
    internal static CreationGenerationNotes Include(CreationSpecification specification, CreationGenerationNotes notes)
    {
        var required = specification.Extractions.Where(e => e.Outcome == SpecExtractionOutcome.Partial)
            .OrderBy(e => e.Identity.Source.SourceId.ToString("N"), StringComparer.Ordinal)
            .Select(e => new CreationAssumption(Identity(e.Identity),
                e.Coverage ?? throw new InvalidOperationException("Partial evidence has no admitted coverage contract."), true)).ToArray();
        // Preserve the exact legacy notes object and digest when every extraction is Complete.
        if (required.Length == 0) return notes;
        var ids = required.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var assumption in notes.Assumptions)
            if (ids.Contains(assumption.Id) && !required.Contains(assumption))
                throw new ArgumentException("A coverage assumption cannot be replaced or downgraded.");
        // Exact generated assumptions may be replayed, but always occupy the same
        // canonical suffix. Ordinary model assumptions retain their original order.
        return new(notes.Evidence, notes.Assumptions.Where(a => !ids.Contains(a.Id)).Concat(required), notes.Questions);
    }

    private static string Identity(SpecExtractionIdentity extraction)
    {
        // A one-extraction snapshot pins the entire identity (source version and
        // digest, family/version, profile, format, outcome and canonical evidence).
        var pin = new SpecBundleSnapshot(extraction.Source.Bundle, [extraction]).Digest.Value;
        return Sha256Digest.FromBytes(JsonSerializer.SerializeToUtf8Bytes(new[] { "braid.creation-coverage.v1", pin })).Value;
    }
}
