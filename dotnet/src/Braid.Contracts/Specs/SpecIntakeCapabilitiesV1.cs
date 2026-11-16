namespace Braid.Contracts.Specs;

public sealed record SpecIntakeFormatV1(string Format, int MaximumSourceBytes, int ExtractorVersion, string EvidenceFormat);
public sealed record SpecIntakeCapabilitiesV1(int Version, int MaximumTextRequestBytes, IReadOnlyList<SpecIntakeFormatV1> Formats);
