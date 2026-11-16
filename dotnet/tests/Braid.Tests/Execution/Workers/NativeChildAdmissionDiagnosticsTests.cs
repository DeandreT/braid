using System.Text.Json;
using Braid.MappingBackends.Workers;

namespace Braid.Tests;

public sealed class NativeChildAdmissionDiagnosticsTests
{
    [Theory]
    [InlineData(80, "marker-missing/arguments")]
    [InlineData(81, "marker-missing/platform")]
    [InlineData(82, "marker-missing/descriptor-inspection")]
    [InlineData(83, "marker-missing/inherited-descriptors")]
    [InlineData(84, "marker-missing/resource-limits")]
    [InlineData(85, "marker-missing/non-dumpable")]
    [InlineData(86, "marker-missing/no-new-privileges")]
    [InlineData(87, "marker-missing/filter-construction")]
    [InlineData(88, "marker-missing/filter-installation")]
    [InlineData(89, "marker-missing/marker-write")]
    [InlineData(90, "marker-missing/landlock-unavailable")]
    [InlineData(91, "marker-missing/path-grant")]
    [InlineData(92, "marker-missing/landlock-restriction")]
    [InlineData(93, "marker-missing/filesystem-confinement")]
    [InlineData(0, "marker-missing/unknown-startup")]
    [InlineData(1, "marker-missing/unknown-startup")]
    [InlineData(3, "marker-missing/unknown-startup")]
    [InlineData(79, "marker-missing/unknown-startup")]
    [InlineData(94, "marker-missing/unprivileged-origin")]
    [InlineData(95, "marker-missing/unknown-startup")]
    [InlineData(137, "marker-missing/unknown-startup")]
    [InlineData(255, "marker-missing/unknown-startup")]
    [InlineData(-1, "marker-missing/unknown-startup")]
    [InlineData(int.MinValue, "marker-missing/unknown-startup")]
    [InlineData(int.MaxValue, "marker-missing/unknown-startup")]
    public void Startup_codes_have_only_fixed_categories(int code, string category) =>
        Assert.Equal(category, ChildAdmissionDiagnostics.Category(ChildAdmissionDiagnostics.StartupExit(code)));

    [Theory]
    [MemberData(nameof(CompletedWorkerNonzeroExits))]
    public void Completed_worker_nonzero_exits_are_plain_worker_failures_and_discard_private_output(int code)
    {
        // Pure classification coverage of the branch called after ExchangeAsync has joined every operation.
        var output = "synthetic-private-native-output\0synthetic-private-child-value"u8.ToArray();
        var result = ProcessWorkerRunner.CompletedExit(output, code);
        Assert.Equal(WorkerProcessFailure.WorkerFailed, result.Failure);
        Assert.Equal(ChildAdmissionFailure.None, result.AdmissionFailure);
        Assert.Equal("none", ChildAdmissionDiagnostics.Category(result.AdmissionFailure));
        Assert.True(result.Output.Length == 0, "A completed worker failure exposed private output.");
        Assert.True(output.AsSpan().SequenceEqual("synthetic-private-native-output\0synthetic-private-child-value"u8),
            "Completed-exit classification mutated its input.");
        AssertPublicResultUnchanged(result);
    }

    public static IEnumerable<object[]> CompletedWorkerNonzeroExits() =>
        Enumerable.Range(80, 15).Concat([1, 3, 79, 95, 137, 255, -1, int.MinValue, int.MaxValue])
            .Select(code => new object[] { code });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Completed_worker_zero_exit_preserves_the_normal_public_output_and_record_copy(bool populated)
    {
        byte[] output = populated ? [0, 255, 65, 0] : [];
        var result = ProcessWorkerRunner.CompletedExit(output, 0);
        Assert.Equal(WorkerProcessFailure.None, result.Failure);
        Assert.Equal(ChildAdmissionFailure.None, result.AdmissionFailure);
        Assert.Equal("none", ChildAdmissionDiagnostics.Category(result.AdmissionFailure));
        Assert.Same(output, result.Output);
        var copy = result with { };
        Assert.Same(output, copy.Output);
        AssertPublicResultUnchanged(result);
    }

    [Fact]
    public void Undefined_categories_and_local_metadata_do_not_expand_the_public_result()
    {
        Assert.Equal("unknown", ChildAdmissionDiagnostics.Category((ChildAdmissionFailure)int.MaxValue));
        var refused = ChildAdmissionDiagnostics.Refused(ChildAdmissionFailure.InheritedDescriptors);
        Assert.Equal(WorkerProcessFailure.SandboxUnavailable, refused.Failure);
        Assert.Empty(refused.Output);
        AssertPublicResultUnchanged(refused);
        Assert.Equal(["Failure", "Output"], typeof(WorkerProcessResult).GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(Enum.GetValues<ChildAdmissionFailure>(), Categories.Select(item => item.Failure));
        foreach (var failure in Enum.GetValues<ChildAdmissionFailure>())
        {
            var category = ChildAdmissionDiagnostics.Category(failure);
            Assert.True(category.Length <= 48 && category.All(character => character is >= 'a' and <= 'z' or '-' or '/'),
                "Admission category escaped its fixed safe alphabet.");
        }
    }

    [Theory]
    [MemberData(nameof(AdmissionCategories))]
    public void Every_admission_branch_has_an_exact_closed_category_and_only_local_metadata(int value, string category)
    {
        var failure = (ChildAdmissionFailure)value;
        Assert.Equal(category, ChildAdmissionDiagnostics.Category(failure));
        var refused = ChildAdmissionDiagnostics.Refused(failure);
        Assert.Equal(failure, refused.AdmissionFailure);
        Assert.Equal(WorkerProcessFailure.SandboxUnavailable, refused.Failure);
        Assert.Empty(refused.Output);
        AssertPublicResultUnchanged(refused);
    }

    public static IEnumerable<object[]> AdmissionCategories() =>
        Categories.Select(item => new object[] { (int)item.Failure, item.Category });

    private static (ChildAdmissionFailure Failure, string Category)[] Categories =>
    [
        (ChildAdmissionFailure.None, "none"),
        (ChildAdmissionFailure.MarkerMissing, "marker-missing/unknown-startup"),
        (ChildAdmissionFailure.MarkerMismatch, "marker-mismatch"),
        (ChildAdmissionFailure.Arguments, "marker-missing/arguments"),
        (ChildAdmissionFailure.Platform, "marker-missing/platform"),
        (ChildAdmissionFailure.DescriptorInspection, "marker-missing/descriptor-inspection"),
        (ChildAdmissionFailure.InheritedDescriptors, "marker-missing/inherited-descriptors"),
        (ChildAdmissionFailure.ResourceLimits, "marker-missing/resource-limits"),
        (ChildAdmissionFailure.NonDumpable, "marker-missing/non-dumpable"),
        (ChildAdmissionFailure.NoNewPrivileges, "marker-missing/no-new-privileges"),
        (ChildAdmissionFailure.FilterConstruction, "marker-missing/filter-construction"),
        (ChildAdmissionFailure.FilterInstallation, "marker-missing/filter-installation"),
        (ChildAdmissionFailure.MarkerWrite, "marker-missing/marker-write"),
        (ChildAdmissionFailure.LandlockUnavailable, "marker-missing/landlock-unavailable"),
        (ChildAdmissionFailure.PathGrant, "marker-missing/path-grant"),
        (ChildAdmissionFailure.LandlockRestriction, "marker-missing/landlock-restriction"),
        (ChildAdmissionFailure.FilesystemConfinement, "marker-missing/filesystem-confinement"),
        (ChildAdmissionFailure.UnprivilegedOrigin, "marker-missing/unprivileged-origin"),
        (ChildAdmissionFailure.StatusRead, "procfs/status-read"),
        (ChildAdmissionFailure.IdentityVerification, "procfs/identity"),
        (ChildAdmissionFailure.NoNewPrivilegesVerification, "procfs/no-new-privileges"),
        (ChildAdmissionFailure.SeccompModeVerification, "procfs/seccomp-mode"),
        (ChildAdmissionFailure.FilterCountVerification, "procfs/filter-count"),
        (ChildAdmissionFailure.LimitsRead, "procfs/limits-read"),
        (ChildAdmissionFailure.LimitVerification, "procfs/limit-verification"),
        (ChildAdmissionFailure.EnvironmentReadable, "procfs/environment-readable"),
        (ChildAdmissionFailure.EnvironmentRead, "procfs/environment-read"),
        (ChildAdmissionFailure.RuntimeProbeRefusal, "runtime-probe/refusal-mismatch")
    ];

    [Fact]
    public void An_absent_procfs_process_is_a_closed_status_read_failure()
    {
        // No process can have this ID. No host environment or child data is read or rendered.
        var failure = ChildSandboxVerification.Verify(int.MinValue, 0, []);
        Assert.Equal(ChildAdmissionFailure.StatusRead, failure);
        Assert.Equal("procfs/status-read", ChildAdmissionDiagnostics.Category(failure));
        AssertPublicResultUnchanged(ChildAdmissionDiagnostics.Refused(failure));
    }

    [Theory]
    [MemberData(nameof(ProcfsReadFailures))]
    public void Procfs_read_failures_keep_the_exact_category_and_stop_later_reads(string stage, string exceptionKind, int expected, string category)
    {
        var reads = new List<string>();
        var exception = ProcfsException(exceptionKind);
        var failure = ChildSandboxVerification.Verify(SyntheticProcessId, 3, RequiredLimits,
            path =>
            {
                reads.Add(path);
                if (path == StatusPath && stage == "status" || path == LimitsPath && stage == "limits") throw exception;
                return ProcfsLines(path);
            },
            path =>
            {
                reads.Add(path);
                Assert.Equal(EnvironmentPath, path);
                throw exception;
            });
        Assert.Equal((ChildAdmissionFailure)expected, failure);
        string[] expectedReads = stage switch
        {
            "status" => [StatusPath],
            "limits" => [StatusPath, LimitsPath],
            _ => [StatusPath, LimitsPath, EnvironmentPath]
        };
        Assert.Equal(expectedReads, reads);
        AssertClosedProcfsResult(failure, category);
    }

    public static IEnumerable<object[]> ProcfsReadFailures()
    {
        foreach (var stage in new[] { "status", "limits", "environment" })
            foreach (var exception in new[] { "io", "unauthorized", "invalid-operation", "file-not-found" })
            {
                var (failure, category) = stage switch
                {
                    "status" => (ChildAdmissionFailure.StatusRead, "procfs/status-read"),
                    "limits" => (ChildAdmissionFailure.LimitsRead, "procfs/limits-read"),
                    _ when exception == "unauthorized" => (ChildAdmissionFailure.None, "none"),
                    _ => (ChildAdmissionFailure.EnvironmentRead, "procfs/environment-read")
                };
                yield return [stage, exception, (int)failure, category];
            }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_readable_environment_is_refused_even_when_empty_and_never_becomes_output(bool populated)
    {
        var reads = new List<string>();
        var failure = ChildSandboxVerification.Verify(SyntheticProcessId, 3, RequiredLimits,
            path => { reads.Add(path); return ProcfsLines(path); },
            path =>
            {
                reads.Add(path);
                Assert.Equal(EnvironmentPath, path);
                return populated ? "SYNTHETIC_PRIVATE_ENV=synthetic-private-child-value\0"u8.ToArray() : [];
            });
        Assert.Equal(ChildAdmissionFailure.EnvironmentReadable, failure);
        Assert.Equal([StatusPath, LimitsPath, EnvironmentPath], reads);
        AssertClosedProcfsResult(failure, "procfs/environment-readable");
    }

    [Theory]
    [MemberData(nameof(ProcfsVerificationGates))]
    public void Each_mandatory_procfs_control_gates_every_subsequent_read(string scenario, int expected, string category)
    {
        var reads = new List<string>();
        string[] status = scenario switch
        {
            "status-no-new-privileges" => ["NoNewPrivs:\t0", "Seccomp:\t2", "Seccomp_filters:\t3"],
            "status-seccomp-mode" => ["NoNewPrivs:\t1", "Seccomp:\t0", "Seccomp_filters:\t3"],
            "status-filters-missing" => ["NoNewPrivs:\t1", "Seccomp:\t2"],
            "status-filters-malformed" => ["NoNewPrivs:\t1", "Seccomp:\t2", "Seccomp_filters:\tsynthetic-private-operand"],
            "status-filters-wrong-count" => ["NoNewPrivs:\t1", "Seccomp:\t2", "Seccomp_filters:\t4"],
            _ => ValidStatus
        };
        string[] limits = scenario switch
        {
            "limits-missing" => [ValidLimits[0]],
            "limits-duplicate" => [ValidLimits[0], ValidLimits[0], ValidLimits[1]],
            "limits-soft" => ["Max cpu time  9  10 seconds", ValidLimits[1]],
            "limits-hard" => ["Max cpu time  10  11 seconds", ValidLimits[1]],
            "limits-malformed" => ["Max cpu time  synthetic-private-path-and-payload", ValidLimits[1]],
            _ => ValidLimits
        };
        var failure = ChildSandboxVerification.Verify(SyntheticProcessId, 3, RequiredLimits,
            path =>
            {
                reads.Add(path);
                return path switch
                {
                    StatusPath => status,
                    LimitsPath => limits,
                    _ => throw new InvalidOperationException("Unexpected synthetic procfs path.")
                };
            },
            path =>
            {
                reads.Add(path);
                throw new InvalidOperationException("A failed control must gate the synthetic environment reader.");
            });
        Assert.Equal((ChildAdmissionFailure)expected, failure);
        string[] expectedReads = scenario.StartsWith("status-", StringComparison.Ordinal) ? [StatusPath] : [StatusPath, LimitsPath];
        Assert.Equal(expectedReads, reads);
        AssertClosedProcfsResult(failure, category);
    }

    public static IEnumerable<object[]> ProcfsVerificationGates()
    {
        yield return ["status-no-new-privileges", (int)ChildAdmissionFailure.NoNewPrivilegesVerification, "procfs/no-new-privileges"];
        yield return ["status-seccomp-mode", (int)ChildAdmissionFailure.SeccompModeVerification, "procfs/seccomp-mode"];
        foreach (var scenario in new[] { "status-filters-missing", "status-filters-malformed", "status-filters-wrong-count" })
            yield return [scenario, (int)ChildAdmissionFailure.FilterCountVerification, "procfs/filter-count"];
        foreach (var scenario in new[] { "limits-missing", "limits-duplicate", "limits-soft", "limits-hard", "limits-malformed" })
            yield return [scenario, (int)ChildAdmissionFailure.LimitVerification, "procfs/limit-verification"];
    }

    [Theory]
    [InlineData("status")]
    [InlineData("limits")]
    [InlineData("environment")]
    public void Unexpected_procfs_exceptions_are_not_added_to_the_admission_catch_allowlist(string stage)
    {
        var exception = new ArgumentException("synthetic-private-exception-path-and-payload");
        var caught = Record.Exception(() => ChildSandboxVerification.Verify(SyntheticProcessId, 3, RequiredLimits,
            path =>
            {
                if (path == StatusPath && stage == "status" || path == LimitsPath && stage == "limits") throw exception;
                return ProcfsLines(path);
            },
            _ => throw exception));
        Assert.True(ReferenceEquals(exception, caught), "The procfs exception catch allowlist changed.");
    }

    private const int SyntheticProcessId = 424242;
    private const string StatusPath = "/proc/424242/status";
    private const string LimitsPath = "/proc/424242/limits";
    private const string EnvironmentPath = "/proc/424242/environ";
    private static string[] ValidStatus => ["NoNewPrivs:\t1", "Seccomp:\t2", "Seccomp_filters:\t3"];
    private static string[] ValidLimits => ["Max cpu time  10  10 seconds", "Max core file size  0  0 bytes"];
    private static (string Name, long Value)[] RequiredLimits => [("Max cpu time", 10), ("Max core file size", 0)];

    private static string[] ProcfsLines(string path) => path switch
    {
        StatusPath => ValidStatus,
        LimitsPath => ValidLimits,
        _ => throw new InvalidOperationException("Unexpected synthetic procfs path.")
    };

    private static Exception ProcfsException(string kind) => kind switch
    {
        "io" => new IOException("synthetic-private-exception-path-and-payload"),
        "unauthorized" => new UnauthorizedAccessException("synthetic-private-exception-path-and-payload"),
        "invalid-operation" => new InvalidOperationException("synthetic-private-exception-path-and-payload"),
        "file-not-found" => new FileNotFoundException("synthetic-private-exception-path-and-payload"),
        _ => throw new ArgumentException("Unknown synthetic procfs exception kind.", nameof(kind))
    };

    private static void AssertClosedProcfsResult(ChildAdmissionFailure failure, string category)
    {
        Assert.Equal(category, ChildAdmissionDiagnostics.Category(failure));
        var result = failure == ChildAdmissionFailure.None
            ? new WorkerProcessResult([], WorkerProcessFailure.None) : ChildAdmissionDiagnostics.Refused(failure);
        Assert.Equal(failure == ChildAdmissionFailure.None ? WorkerProcessFailure.None : WorkerProcessFailure.SandboxUnavailable, result.Failure);
        Assert.Equal(failure, result.AdmissionFailure);
        Assert.True(result.Output.Length == 0, "A procfs admission result exposed private data.");
        AssertPublicResultUnchanged(result);
    }

    internal static void AssertPublicResultUnchanged(WorkerProcessResult result)
    {
        var ordinary = new WorkerProcessResult(result.Output, result.Failure);
        Assert.Equal(ordinary, result);
        Assert.Equal(ordinary.GetHashCode(), result.GetHashCode());
        Assert.Equal(JsonSerializer.Serialize(ordinary), JsonSerializer.Serialize(result));
        Assert.Equal(ordinary.ToString(), result.ToString());
        Assert.Equal(ChildAdmissionFailure.None, ordinary.AdmissionFailure);
        // Record copying preserves public values, not the original object's local diagnostic attachment.
        var copy = result with { };
        Assert.Equal(ordinary, copy);
        Assert.Equal(ChildAdmissionFailure.None, copy.AdmissionFailure);
    }

    [Fact]
    public void Procfs_status_checks_keep_each_control_mandatory()
    {
        string[] valid = ["NoNewPrivs:\t1", "Seccomp:\t2", "Seccomp_filters:\t3"];
        Assert.Equal(ChildAdmissionFailure.None, ChildSandboxVerification.VerifyStatus(valid, 3));
        Assert.Equal(ChildAdmissionFailure.NoNewPrivilegesVerification, ChildSandboxVerification.VerifyStatus(valid.Skip(1), 3));
        Assert.Equal(ChildAdmissionFailure.SeccompModeVerification, ChildSandboxVerification.VerifyStatus([valid[0], valid[2]], 3));
        Assert.Equal(ChildAdmissionFailure.FilterCountVerification, ChildSandboxVerification.VerifyStatus(valid, 4));
        Assert.Equal(ChildAdmissionFailure.FilterCountVerification, ChildSandboxVerification.VerifyStatus([valid[0], valid[1], "Seccomp_filters:\tprivate-operand"], 3));
    }

    [Fact]
    public void Procfs_identity_requires_stable_non_root_capless_untraced_credentials()
    {
        string[] valid = ["Uid:\t1000\t1000\t1000\t1000", "Gid:\t1000\t1000\t1000\t1000", "TracerPid:\t0",
            "CapInh:\t0000000000000000", "CapPrm:\t0000000000000000", "CapEff:\t0000000000000000", "CapAmb:\t0000000000000000"];
        Assert.True(ChildSandboxVerification.UnprivilegedIdentity(valid));
        foreach (var (field, replacement) in new[]
        {
            ("Uid:", "Uid:\t0\t0\t0\t0"), ("Uid:", "Uid:\t1000\t0\t1000\t1000"), ("Gid:", "Gid:\t1000\t1000\t1000"),
            ("TracerPid:", "TracerPid:\t42"), ("CapEff:", "CapEff:\t0000000000000001"), ("CapAmb:", "CapAmb:\tprivate-operand")
        })
            Assert.False(ChildSandboxVerification.UnprivilegedIdentity(valid.Select(line => line.StartsWith(field, StringComparison.Ordinal) ? replacement : line)));
        Assert.False(ChildSandboxVerification.UnprivilegedIdentity(valid.Skip(1)));
        Assert.False(ChildSandboxVerification.UnprivilegedIdentity(valid.Append(valid[0])));
        Assert.Equal("procfs/identity", ChildAdmissionDiagnostics.Category(ChildAdmissionFailure.IdentityVerification));
    }

    [Fact]
    public void Procfs_limits_require_exact_soft_and_hard_values_without_rendering_the_input()
    {
        (string Name, long Value)[] required = [("Max cpu time", 10), ("Max core file size", 0)];
        string[] valid = ["Max cpu time  10  10 seconds", "Max core file size  0  0 bytes"];
        Assert.Equal(ChildAdmissionFailure.None, ChildSandboxVerification.VerifyLimits(valid, required));
        foreach (var invalid in new string[][]
        {
            [valid[0]],
            [valid[0], valid[0], valid[1]],
            ["Max cpu time  9  10 seconds", valid[1]],
            ["Max cpu time  10  11 seconds", valid[1]],
            ["Max cpu time  private-path-and-payload", valid[1]]
        })
            Assert.Equal(ChildAdmissionFailure.LimitVerification, ChildSandboxVerification.VerifyLimits(invalid, required));
        Assert.Equal("procfs/limit-verification", ChildAdmissionDiagnostics.Category(ChildAdmissionFailure.LimitVerification));
    }
}
