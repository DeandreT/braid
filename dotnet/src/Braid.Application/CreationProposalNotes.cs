using Braid.Domain;

namespace Braid.Application;

/// <summary>Canonical notes for both retention and exact replay, using the generation coverage policy.</summary>
public static class CreationProposalNotes
{
    public static CreationGenerationNotes Canonicalize(CreationSpecification specification, CreationGenerationNotes notes) =>
        CreationEvidenceCoverage.Include(specification, notes);
}
