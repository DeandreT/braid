namespace Braid.WorkerProbeSupport;

// An independent test/probe oracle, not a source of production worker names.
internal static class InspectionWorkerName
{
    private const string Prefix = "braid-inspect-";

    internal static bool IsExact(string? name) =>
        name is not null && name.Length == Prefix.Length + 32 &&
        name.StartsWith(Prefix, StringComparison.Ordinal) &&
        !name.AsSpan(Prefix.Length).ContainsAnyExcept("0123456789abcdef");
}
