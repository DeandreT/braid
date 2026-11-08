using System.Runtime.CompilerServices;

namespace Weft.MappingBackends.Workers;

// Local qualification metadata only. Never added to worker frames or service responses.
internal enum ChildAdmissionFailure
{
    None,
    MarkerMissing,
    MarkerMismatch,
    Arguments,
    Platform,
    DescriptorInspection,
    InheritedDescriptors,
    ResourceLimits,
    NonDumpable,
    NoNewPrivileges,
    FilterConstruction,
    FilterInstallation,
    MarkerWrite,
    LandlockUnavailable,
    PathGrant,
    LandlockRestriction,
    FilesystemConfinement,
    UnprivilegedOrigin,
    StatusRead,
    IdentityVerification,
    NoNewPrivilegesVerification,
    SeccompModeVerification,
    FilterCountVerification,
    LimitsRead,
    LimitVerification,
    EnvironmentReadable,
    EnvironmentRead,
    RuntimeProbeRefusal
}

internal static class ChildAdmissionDiagnostics
{
    // Diagnostic metadata must not become part of this public record's equality, cloning or wire shape.
    private static readonly ConditionalWeakTable<WorkerProcessResult, Metadata> Failures = new();
    private sealed record Metadata(ChildAdmissionFailure Failure);

    internal static ChildAdmissionFailure GetFailure(WorkerProcessResult result) =>
        Failures.TryGetValue(result, out var metadata) ? metadata.Failure : ChildAdmissionFailure.None;

    // The two installed native programs use only these fixed startup exit codes.
    // Unknown codes (including signals) are never formatted as numbers or native text.
    internal static ChildAdmissionFailure StartupExit(int code) => code switch
    {
        80 => ChildAdmissionFailure.Arguments,
        81 => ChildAdmissionFailure.Platform,
        82 => ChildAdmissionFailure.DescriptorInspection,
        83 => ChildAdmissionFailure.InheritedDescriptors,
        84 => ChildAdmissionFailure.ResourceLimits,
        85 => ChildAdmissionFailure.NonDumpable,
        86 => ChildAdmissionFailure.NoNewPrivileges,
        87 => ChildAdmissionFailure.FilterConstruction,
        88 => ChildAdmissionFailure.FilterInstallation,
        89 => ChildAdmissionFailure.MarkerWrite,
        90 => ChildAdmissionFailure.LandlockUnavailable,
        91 => ChildAdmissionFailure.PathGrant,
        92 => ChildAdmissionFailure.LandlockRestriction,
        93 => ChildAdmissionFailure.FilesystemConfinement,
        94 => ChildAdmissionFailure.UnprivilegedOrigin,
        _ => ChildAdmissionFailure.MarkerMissing
    };

    internal static WorkerProcessResult Refused(ChildAdmissionFailure failure)
    {
        var result = new WorkerProcessResult([], WorkerProcessFailure.SandboxUnavailable);
        Failures.Add(result, new(failure));
        return result;
    }

    // Explicit strings, rather than Enum.ToString(), keep undefined/future values closed too.
    internal static string Category(ChildAdmissionFailure failure) => failure switch
    {
        ChildAdmissionFailure.None => "none",
        ChildAdmissionFailure.MarkerMissing => "marker-missing/unknown-startup",
        ChildAdmissionFailure.MarkerMismatch => "marker-mismatch",
        ChildAdmissionFailure.Arguments => "marker-missing/arguments",
        ChildAdmissionFailure.Platform => "marker-missing/platform",
        ChildAdmissionFailure.DescriptorInspection => "marker-missing/descriptor-inspection",
        ChildAdmissionFailure.InheritedDescriptors => "marker-missing/inherited-descriptors",
        ChildAdmissionFailure.ResourceLimits => "marker-missing/resource-limits",
        ChildAdmissionFailure.NonDumpable => "marker-missing/non-dumpable",
        ChildAdmissionFailure.NoNewPrivileges => "marker-missing/no-new-privileges",
        ChildAdmissionFailure.FilterConstruction => "marker-missing/filter-construction",
        ChildAdmissionFailure.FilterInstallation => "marker-missing/filter-installation",
        ChildAdmissionFailure.MarkerWrite => "marker-missing/marker-write",
        ChildAdmissionFailure.LandlockUnavailable => "marker-missing/landlock-unavailable",
        ChildAdmissionFailure.PathGrant => "marker-missing/path-grant",
        ChildAdmissionFailure.LandlockRestriction => "marker-missing/landlock-restriction",
        ChildAdmissionFailure.FilesystemConfinement => "marker-missing/filesystem-confinement",
        ChildAdmissionFailure.UnprivilegedOrigin => "marker-missing/unprivileged-origin",
        ChildAdmissionFailure.StatusRead => "procfs/status-read",
        ChildAdmissionFailure.IdentityVerification => "procfs/identity",
        ChildAdmissionFailure.NoNewPrivilegesVerification => "procfs/no-new-privileges",
        ChildAdmissionFailure.SeccompModeVerification => "procfs/seccomp-mode",
        ChildAdmissionFailure.FilterCountVerification => "procfs/filter-count",
        ChildAdmissionFailure.LimitsRead => "procfs/limits-read",
        ChildAdmissionFailure.LimitVerification => "procfs/limit-verification",
        ChildAdmissionFailure.EnvironmentReadable => "procfs/environment-readable",
        ChildAdmissionFailure.EnvironmentRead => "procfs/environment-read",
        ChildAdmissionFailure.RuntimeProbeRefusal => "runtime-probe/refusal-mismatch",
        _ => "unknown"
    };
}
