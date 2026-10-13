using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Weft.MappingBackends.Workers;

namespace Weft.Tests;

/// <summary>Process-seam tests qualify orchestration; they do not demonstrate container isolation.</summary>
public sealed class FerruleDockerRunnerTests
{
    private const string ImageId = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ContainerId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string DaemonMetadata = """
        {"OSType":"linux","CgroupVersion":"2","MemoryLimit":true,"SwapLimit":true,
         "CpuCfsPeriod":true,"CpuCfsQuota":true,"PidsLimit":true,
         "SecurityOptions":["name=seccomp,profile=builtin","name=cgroupns"]}
        """;
    private const string ImageMetadata = """
        {"Id":"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
         "Os":"linux","Config":{"Env":null,"Volumes":null,"Cmd":null}}
        """;
    private const string ContainerMetadata = """
        {"Image":"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","Mounts":[],
         "Config":{"User":"65532:65532","WorkingDir":"/","Entrypoint":["/ferrule-host"],"Cmd":null,
          "OpenStdin":true,"Tty":false,"Hostname":"worker","Env":["PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin","HOSTNAME=worker"]},
         "HostConfig":{"ReadonlyRootfs":true,"Privileged":false,"NetworkMode":"none","IpcMode":"none",
          "CgroupnsMode":"private","PidMode":"","UTSMode":"","NanoCpus":1000000000,
          "Memory":268435456,"MemorySwap":268435456,"PidsLimit":16,"CapDrop":["ALL"],"CapAdd":null,
          "SecurityOpt":["no-new-privileges=true"],"Binds":null,"VolumesFrom":null,"Devices":[],
          "DeviceRequests":null,"PortBindings":{},"RestartPolicy":{"Name":"no"},"LogConfig":{"Type":"none"},
          "Ulimits":[{"Name":"nofile","Soft":64,"Hard":64},{"Name":"cpu","Soft":10,"Hard":10},{"Name":"core","Soft":0,"Hard":0}]}}
        """;

    [Fact]
    public async Task PolicyIsFixedAndBytesPassWithoutTextConversion()
    {
        byte[] request = [0, 1, 255, 0, 13, 10];
        byte[] response = [0, 254, 128, 255, 1];
        var worker = new FakeProcess(response);
        var launcher = new FakeLauncher { Worker = worker };
        var runner = Runner(launcher);

        var result = await runner.RunAsync(request, CancellationToken.None);

        Assert.Equal(WorkerProcessFailure.None, result.Failure);
        Assert.Equal(response, result.Output);
        Assert.Equal(request, worker.Input.ToArray());
        Assert.True(worker.InputClosed);
        Assert.True(worker.InputOwnerClosed);
        Assert.Equal(["info", "image", "create", "inspect", "start", "rm"], launcher.Commands.Select(Operation));
        foreach (var command in launcher.Commands)
        {
            Assert.Equal("/usr/bin/docker", command.FileName);
            Assert.False(command.UseShellExecute);
            Assert.True(command.RedirectStandardInput && command.RedirectStandardOutput && command.RedirectStandardError);
            Assert.NotNull(command.StandardInputEncoding);
            Assert.Empty(command.StandardInputEncoding.GetPreamble());
            Assert.Empty(command.Arguments);
            Assert.Equal(["--config", command.WorkingDirectory, "--host", "unix:///var/run/docker.sock"], command.ArgumentList.Take(4));
            Assert.Equal(["DOCKER_CONFIG", "HOME", "LC_ALL"], command.Environment.Keys.Order(StringComparer.Ordinal));
            Assert.Equal(command.WorkingDirectory, command.Environment["HOME"]);
            Assert.Equal(command.WorkingDirectory, command.Environment["DOCKER_CONFIG"]);
            Assert.False(Directory.Exists(command.WorkingDirectory));
        }

        var create = launcher.Commands.Single(command => Operation(command) == "create").ArgumentList.Skip(4).ToArray();
        var name = create[3];
        Assert.Matches("^weft-inspect-[a-f0-9]{32}$", name);
        Assert.Equal(new[]
        {
            "container", "create", "--name", name, "--pull", "never", "--interactive",
            "--network", "none", "--read-only", "--cap-drop", "ALL", "--security-opt", "no-new-privileges=true",
            "--user", "65532:65532", "--cpus", "1", "--memory", "268435456", "--memory-swap", "268435456",
            "--pids-limit", "16", "--ulimit", "nofile=64:64", "--ulimit", "cpu=10:10", "--ulimit", "core=0:0",
            "--ipc", "none", "--cgroupns", "private", "--no-healthcheck", "--restart", "no", "--log-driver", "none",
            "--workdir", "/", "--entrypoint", "/ferrule-host", ImageId
        }, create);
        Assert.Equal(["container", "start", "--attach", "--interactive", name], launcher.Commands.Single(command => Operation(command) == "start").ArgumentList.Skip(4));
        Assert.Equal(["container", "rm", "--force", "--volumes", name], launcher.Commands.Last().ArgumentList.Skip(4));
    }

    [Theory]
    [InlineData("docker", ImageId)]
    [InlineData("/usr/bin/../bin/docker", ImageId)]
    [InlineData("/usr/bin/docker", "latest")]
    [InlineData("/usr/bin/docker", "repo@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("/usr/bin/docker", "sha256:zaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void OptionsRejectMutableImagesOrExecutableLookup(string executable, string image) =>
        Assert.Throws<ArgumentException>(() => new DockerInspectionWorkerOptions(executable, image));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10001)]
    public void OptionsCannotRelaxWallDeadline(int milliseconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new DockerInspectionWorkerOptions("/usr/bin/docker", ImageId, TimeSpan.FromMilliseconds(milliseconds)));

    [Fact]
    public void OptionsRejectRemoteDockerHosts() =>
        Assert.Throws<ArgumentException>(() => new DockerInspectionWorkerOptions("/usr/bin/docker", ImageId, dockerSocketPath: "tcp://example:2375"));

    [Fact]
    public async Task OversizedInputIsRejectedBeforeAnyProcessStarts()
    {
        var launcher = new FakeLauncher();

        await Assert.ThrowsAsync<ArgumentException>(() => Runner(launcher).RunAsync(new byte[DockerInspectionWorkerOptions.MaximumFrameBytes + 1], CancellationToken.None));

        Assert.Empty(launcher.Commands);
    }

    [Theory]
    [InlineData("OSType", "windows")]
    [InlineData("CgroupVersion", "1")]
    [InlineData("MemoryLimit", false)]
    [InlineData("SwapLimit", false)]
    [InlineData("CpuCfsQuota", false)]
    [InlineData("PidsLimit", false)]
    public async Task UnsupportedDaemonNeverCreatesAContainer(string property, object value)
    {
        var metadata = JsonNode.Parse(DaemonMetadata)!;
        metadata[property] = JsonValue.Create(value);
        var launcher = new FakeLauncher { Daemon = TextProcess(metadata.ToJsonString()) };

        var result = await Runner(launcher).RunAsync("request"u8.ToArray(), CancellationToken.None);

        Assert.Equal(WorkerProcessFailure.SandboxUnavailable, result.Failure);
        Assert.Single(launcher.Commands);
    }

    [Theory]
    [InlineData("Env", "[\"SECRET=value\"]")]
    [InlineData("Volumes", "{\"/data\":{}}")]
    [InlineData("Cmd", "[\"--unrestricted\"]")]
    public async Task ImageWithImplicitEnvironmentVolumeOrArgumentsCannotStart(string property, string value)
    {
        var metadata = JsonNode.Parse(ImageMetadata)!;
        metadata["Config"]![property] = JsonNode.Parse(value);
        var launcher = new FakeLauncher { Image = TextProcess(metadata.ToJsonString()) };

        var result = await Runner(launcher).RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None);

        Assert.Equal(WorkerProcessFailure.SandboxUnavailable, result.Failure);
        Assert.DoesNotContain(launcher.Commands, command => Operation(command) == "create");
    }

    [Fact]
    public async Task DockerInjectedImagePathAndOmittedOptionalFieldsAreSafe()
    {
        var metadata = JsonNode.Parse(ImageMetadata)!;
        var config = (JsonObject)metadata["Config"]!;
        config["Env"] = new JsonArray("PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin");
        config.Remove("Volumes");
        config.Remove("Cmd");
        var launcher = new FakeLauncher { Image = TextProcess(metadata.ToJsonString()) };

        var result = await Runner(launcher).RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None);

        Assert.Equal(WorkerProcessFailure.None, result.Failure);
        Assert.Contains(launcher.Commands, command => Operation(command) == "start");
    }

    [Theory]
    [InlineData("Env", "absent")]
    [InlineData("Env", "null")]
    [InlineData("Env", "[]")]
    [InlineData("Volumes", "absent")]
    [InlineData("Volumes", "null")]
    [InlineData("Volumes", "{}")]
    [InlineData("Cmd", "absent")]
    [InlineData("Cmd", "null")]
    [InlineData("Cmd", "[]")]
    public async Task ImageOptionalMetadataHasEquivalentSafeEmptyForms(string property, string representation)
    {
        var metadata = JsonNode.Parse(ImageMetadata)!;
        var config = (JsonObject)metadata["Config"]!;
        if (representation == "absent")
        {
            config.Remove(property);
        }
        else
        {
            config[property] = JsonNode.Parse(representation);
        }
        var launcher = new FakeLauncher { Image = TextProcess(metadata.ToJsonString()) };

        Assert.Equal(WorkerProcessFailure.None, (await Runner(launcher).RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None)).Failure);
    }

    [Theory]
    [InlineData("Env", "[\"PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin\",\"SECRET=value\"]")]
    [InlineData("Env", "[\"PATH=/custom\"]")]
    [InlineData("Env", "[\"HOSTNAME=image-controlled\"]")]
    [InlineData("Env", "{}")]
    [InlineData("Volumes", "[]")]
    [InlineData("Cmd", "{}")]
    public async Task ImageMetadataRejectsExtraEnvironmentAndIncorrectTypes(string property, string representation)
    {
        var metadata = JsonNode.Parse(ImageMetadata)!;
        metadata["Config"]![property] = JsonNode.Parse(representation);
        var launcher = new FakeLauncher { Image = TextProcess(metadata.ToJsonString()) };

        Assert.Equal(WorkerProcessFailure.SandboxUnavailable, (await Runner(launcher).RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None)).Failure);
        Assert.DoesNotContain(launcher.Commands, command => Operation(command) == "create");
    }

    [Theory]
    [InlineData("Config.Cmd")]
    [InlineData("HostConfig.CapAdd")]
    [InlineData("HostConfig.Binds")]
    [InlineData("HostConfig.VolumesFrom")]
    [InlineData("HostConfig.Devices")]
    [InlineData("HostConfig.DeviceRequests")]
    [InlineData("HostConfig.PortBindings")]
    public async Task ContainerOptionalEmptyCollectionsMayBeOmitted(string path)
    {
        var metadata = JsonNode.Parse(ContainerMetadata)!;
        var parts = path.Split('.');
        var parent = parts.Length == 1 ? (JsonObject)metadata : (JsonObject)metadata[parts[0]]!;
        parent.Remove(parts[^1]);
        var launcher = new FakeLauncher { Container = TextProcess(metadata.ToJsonString()) };

        Assert.Equal(WorkerProcessFailure.None, (await Runner(launcher).RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None)).Failure);
    }

    [Theory]
    [InlineData("Mounts")]
    [InlineData("Config.User")]
    [InlineData("Config.Entrypoint")]
    [InlineData("Config.OpenStdin")]
    [InlineData("HostConfig.ReadonlyRootfs")]
    [InlineData("HostConfig.NetworkMode")]
    [InlineData("HostConfig.Memory")]
    [InlineData("HostConfig.MemorySwap")]
    [InlineData("HostConfig.PidsLimit")]
    [InlineData("HostConfig.SecurityOpt")]
    public async Task RequiredIsolationFieldsCannotBeOmitted(string path)
    {
        var metadata = JsonNode.Parse(ContainerMetadata)!;
        var parts = path.Split('.');
        var parent = parts.Length == 1 ? (JsonObject)metadata : (JsonObject)metadata[parts[0]]!;
        parent.Remove(parts[^1]);
        var launcher = new FakeLauncher { Container = TextProcess(metadata.ToJsonString()) };

        Assert.Equal(WorkerProcessFailure.SandboxUnavailable, (await Runner(launcher).RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None)).Failure);
        Assert.DoesNotContain(launcher.Commands, command => Operation(command) == "start");
    }

    [Fact]
    public async Task StandardInputOwnerClosesBeforeLaterWriterDisposal()
    {
        var worker = new FakeProcess("result"u8.ToArray());
        var launcher = new FakeLauncher { Worker = worker };

        var result = await Runner(launcher).RunAsync("request"u8.ToArray(), CancellationToken.None);

        Assert.Equal(WorkerProcessFailure.None, result.Failure);
        Assert.True(worker.InputOwnerClosed);
        Assert.True(worker.InputClosed);
        Assert.Equal("request"u8.ToArray(), worker.Input.ToArray());
    }

    [Theory]
    [InlineData("Memory", "0")]
    [InlineData("MemorySwap", "-1")]
    [InlineData("PidsLimit", "0")]
    [InlineData("NetworkMode", "\"host\"")]
    [InlineData("ReadonlyRootfs", "false")]
    [InlineData("SecurityOpt", "[\"seccomp=unconfined\"]")]
    [InlineData("Binds", "[\"/secret:/secret\"]")]
    public async Task CreatedContainerMustMatchTheIsolationPolicy(string property, string value)
    {
        var metadata = JsonNode.Parse(ContainerMetadata)!;
        metadata["HostConfig"]![property] = JsonNode.Parse(value);
        var launcher = new FakeLauncher { Container = TextProcess(metadata.ToJsonString()) };

        var result = await Runner(launcher).RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None);

        Assert.Equal(WorkerProcessFailure.SandboxUnavailable, result.Failure);
        Assert.DoesNotContain(launcher.Commands, command => Operation(command) == "start");
        Assert.Equal("rm", Operation(launcher.Commands.Last()));
    }

    [Fact]
    public async Task MissingExecutableReturnsStartFailureAndDoesNotPoisonLaterAdmission()
    {
        var launcher = new FakeLauncher { ThrowOnOperation = "info" };
        var runner = Runner(launcher);

        Assert.Equal(WorkerProcessFailure.StartFailed, (await runner.RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None)).Failure);
        launcher.ThrowOnOperation = null;
        Assert.Equal(WorkerProcessFailure.None, (await runner.RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None)).Failure);
    }

    [Fact]
    public async Task NonzeroWorkerExitDiscardsItsOutputAndStillRemovesContainer()
    {
        var launcher = new FakeLauncher { Worker = new FakeProcess("partial"u8.ToArray(), exitCode: 137) };

        var result = await Runner(launcher).RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None);

        Assert.Equal(WorkerProcessFailure.WorkerFailed, result.Failure);
        Assert.Empty(result.Output);
        Assert.Equal("rm", Operation(launcher.Commands.Last()));
    }

    [Fact]
    public async Task TimeoutKillsCliAndIndependentlyRemovesItsNamedContainer()
    {
        var worker = FakeProcess.Hanging();
        var launcher = new FakeLauncher { Worker = worker };

        var result = await Runner(launcher, TimeSpan.FromMilliseconds(100)).RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None);

        Assert.Equal(WorkerProcessFailure.TimedOut, result.Failure);
        Assert.True(worker.Killed);
        Assert.Equal("rm", Operation(launcher.Commands.Last()));
    }

    [Fact]
    public async Task InterruptedCreateStillRemovesItsKnownNameAndAllowsAnotherAdmission()
    {
        var creation = FakeProcess.Hanging();
        var launcher = new FakeLauncher { Create = creation };
        var runner = Runner(launcher, TimeSpan.FromMilliseconds(100));

        var result = await runner.RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None);

        Assert.Equal(WorkerProcessFailure.TimedOut, result.Failure);
        Assert.True(creation.Killed);
        var create = launcher.Commands.Single(command => Operation(command) == "create");
        Assert.Equal(create.ArgumentList[7], launcher.Commands.Last().ArgumentList.Last());
        Assert.DoesNotContain(launcher.Commands, command => Operation(command) == "start");
        launcher.Create = null;
        Assert.Equal(WorkerProcessFailure.None, (await runner.RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None)).Failure);
    }

    [Fact]
    public async Task CallerCancellationWaitsForCreatingCliToSettleBeforeCleanupAndNextAdmission()
    {
        var creation = FakeProcess.GatedCreation();
        var launcher = new FakeLauncher { Create = creation };
        var runner = Runner(launcher);
        using var cancellation = new CancellationTokenSource();
        var first = runner.RunAsync("canceled-native-request"u8.ToArray(), cancellation.Token);
        await creation.ExitWaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var create = launcher.Commands.Single(command => Operation(command) == "create");
        var ownedName = create.ArgumentList[7];
        var commandCount = launcher.Commands.Count;

        cancellation.Cancel();
        var queuedRequest = "next-native-request"u8.ToArray();
        var queued = runner.RunAsync(queuedRequest, CancellationToken.None);

        try
        {
            // The captured create token makes this check independent of continuation scheduling.
            Assert.True(creation.FirstExitWaitToken.CanBeCanceled);
            Assert.False(creation.FirstExitWaitToken.IsCancellationRequested);
            Assert.False(creation.Killed);
            Assert.False(creation.Disposed);
            Assert.False(first.IsCompleted);
            Assert.False(queued.IsCompleted);
            Assert.Equal(commandCount, launcher.Commands.Count);
            Assert.DoesNotContain(launcher.Commands, command => Operation(command) is "inspect" or "start" or "rm");
            Assert.Empty(creation.Input.ToArray());
            Assert.Empty(launcher.Worker.Input.ToArray());
        }
        finally
        {
            // Already-running create owns this process reference; later admissions get a fresh CLI.
            launcher.Create = null;
            creation.Settle();
            await Task.WhenAll(first, queued).WaitAsync(TimeSpan.FromSeconds(1));
        }
        var canceled = await first.WaitAsync(TimeSpan.FromSeconds(1));
        var next = await queued.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(WorkerProcessFailure.Canceled, canceled.Failure);
        Assert.Empty(canceled.Output);
        Assert.False(creation.Killed);
        Assert.True(creation.Disposed);
        Assert.Equal(WorkerProcessFailure.None, next.Failure);
        Assert.Equal(queuedRequest, launcher.Worker.Input.ToArray());
        Assert.Equal(["info", "image", "create", "rm", "info", "image", "create", "inspect", "start", "rm"],
            launcher.Commands.Select(Operation));
        Assert.Equal(ownedName, launcher.Commands[3].ArgumentList.Last());
    }

    [Theory]
    [InlineData("")]
    [InlineData("another-container\n")]
    [InlineData(ContainerId + "\n")]
    public async Task UncertainCreateRequiresExactOwnedNameAcknowledgmentEvenWhenRemovalExitsZero(string removalOutput)
    {
        var creation = FakeProcess.Hanging();
        var launcher = new FakeLauncher { Create = creation, Remove = TextProcess(removalOutput) };
        var runner = Runner(launcher, TimeSpan.FromMilliseconds(100));

        var result = await runner.RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None);

        Assert.Equal(WorkerProcessFailure.CleanupFailed, result.Failure);
        Assert.True(creation.Killed);
        Assert.Empty(result.Output);
        Assert.DoesNotContain(launcher.Commands, command => Operation(command) is "inspect" or "start");
        Assert.Equal("rm", Operation(launcher.Commands.Last()));
        var commandCount = launcher.Commands.Count;
        Assert.Equal(WorkerProcessFailure.SandboxUnavailable,
            (await runner.RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None)).Failure);
        Assert.Equal(commandCount, launcher.Commands.Count);
    }

    [Fact]
    public async Task InterruptedCreateAndUnconfirmedAbsencePermanentlyBlockAdmission()
    {
        var launcher = new FakeLauncher { Create = FakeProcess.Hanging(), RemovalReportsMissing = true };
        var runner = Runner(launcher, TimeSpan.FromMilliseconds(100));

        Assert.Equal(WorkerProcessFailure.CleanupFailed, (await runner.RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None)).Failure);
        var commandCount = launcher.Commands.Count;
        Assert.Equal(WorkerProcessFailure.SandboxUnavailable, (await runner.RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None)).Failure);
        Assert.Equal(commandCount, launcher.Commands.Count);
    }

    [Fact]
    public async Task ConfirmedCreateAndAlreadyAbsentContainerAreSafeForIdempotentCleanup()
    {
        var launcher = new FakeLauncher { RemovalReportsMissing = true };

        Assert.Equal(WorkerProcessFailure.None, (await Runner(launcher).RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None)).Failure);
    }

    [Fact]
    public async Task ConfirmedCreateAndQuietSuccessfulRemovalAreSafeForIdempotentCleanup()
    {
        var launcher = new FakeLauncher { Remove = new FakeProcess([]) };

        Assert.Equal(WorkerProcessFailure.None,
            (await Runner(launcher).RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None)).Failure);
        Assert.Contains(launcher.Commands, command => Operation(command) == "start");
        Assert.Equal("rm", Operation(launcher.Commands.Last()));
    }

    [Fact]
    public async Task CancellationDuringBlockedInputDoesNotWaitForPipeCooperation()
    {
        var worker = FakeProcess.Hanging(blockInput: true);
        var launcher = new FakeLauncher { Worker = worker };
        using var cancellation = new CancellationTokenSource();
        var run = Runner(launcher).RunAsync("request"u8.ToArray(), cancellation.Token);
        await launcher.WorkerStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        cancellation.Cancel();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(WorkerProcessFailure.Canceled, result.Failure);
        Assert.True(worker.Killed);
        Assert.Equal("rm", Operation(launcher.Commands.Last()));
    }

    [Fact]
    public async Task CanceledQueueEntryDoesNotStartAnotherProcessAndAdmissionIsReleased()
    {
        var launcher = new FakeLauncher { Worker = FakeProcess.Hanging() };
        var runner = Runner(launcher);
        using var firstCancellation = new CancellationTokenSource();
        var first = runner.RunAsync(ReadOnlyMemory<byte>.Empty, firstCancellation.Token);
        await launcher.WorkerStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var commandCount = launcher.Commands.Count;
        using var queuedCancellation = new CancellationTokenSource();
        var queued = runner.RunAsync(ReadOnlyMemory<byte>.Empty, queuedCancellation.Token);
        queuedCancellation.Cancel();

        Assert.Equal(WorkerProcessFailure.Canceled, (await queued).Failure);
        Assert.Equal(commandCount, launcher.Commands.Count);
        firstCancellation.Cancel();
        Assert.Equal(WorkerProcessFailure.Canceled, (await first).Failure);
        launcher.Worker = new FakeProcess([]);
        Assert.Equal(WorkerProcessFailure.None, (await runner.RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None)).Failure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExcessOutputOrErrorTerminatesWorkerWithoutWaitingForExit(bool stderr)
    {
        var worker = FakeProcess.Hanging();
        if (stderr)
        {
            worker.Error = new MemoryStream(new byte[(64 * 1024) + 1]);
        }
        else
        {
            worker.Output = new MemoryStream(new byte[DockerInspectionWorkerOptions.MaximumFrameBytes + 1]);
        }

        var launcher = new FakeLauncher { Worker = worker };
        var result = await Runner(launcher).RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(WorkerProcessFailure.OutputLimitExceeded, result.Failure);
        Assert.True(worker.Killed);
        Assert.Empty(result.Output);
        Assert.Equal("rm", Operation(launcher.Commands.Last()));
    }

    [Fact]
    public async Task InvalidUtf8ControlOutputFailsClosed()
    {
        var launcher = new FakeLauncher { Daemon = new FakeProcess([255]) };

        var result = await Runner(launcher).RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None);

        Assert.Equal(WorkerProcessFailure.WorkerFailed, result.Failure);
        Assert.Single(launcher.Commands);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedKillOrReapPermanentlyBlocksNewAdmission(bool failedReap)
    {
        var worker = FakeProcess.Hanging();
        worker.FailKill = !failedReap;
        worker.FailReap = failedReap;
        var launcher = new FakeLauncher { Worker = worker };
        var runner = Runner(launcher, TimeSpan.FromMilliseconds(100));

        Assert.Equal(WorkerProcessFailure.CleanupFailed, (await runner.RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None)).Failure);
        var commandCount = launcher.Commands.Count;
        Assert.Equal(WorkerProcessFailure.SandboxUnavailable, (await runner.RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None)).Failure);
        Assert.Equal(commandCount, launcher.Commands.Count);
        Assert.Equal("rm", Operation(launcher.Commands.Last()));
    }

    [Fact]
    public async Task FailedRemovalDiscardsSuccessAndPermanentlyBlocksNewAdmission()
    {
        var launcher = new FakeLauncher { Remove = new FakeProcess([], exitCode: 1) };
        var runner = Runner(launcher);

        Assert.Equal(WorkerProcessFailure.CleanupFailed, (await runner.RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None)).Failure);
        var commandCount = launcher.Commands.Count;
        Assert.Equal(WorkerProcessFailure.SandboxUnavailable, (await runner.RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None)).Failure);
        Assert.Equal(commandCount, launcher.Commands.Count);
    }

    [Fact]
    public async Task HungRemovalHasIndependentBoundAndPermanentlyBlocksNewAdmission()
    {
        var removal = FakeProcess.Hanging();
        var launcher = new FakeLauncher { Remove = removal };
        var runner = Runner(launcher);

        var result = await runner.RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(4));

        Assert.Equal(WorkerProcessFailure.CleanupFailed, result.Failure);
        Assert.True(removal.Killed);
        Assert.Equal(WorkerProcessFailure.SandboxUnavailable, (await runner.RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None)).Failure);
    }

    private static DockerInspectionWorkerRunner Runner(FakeLauncher launcher, TimeSpan? timeout = null) =>
        new(new DockerInspectionWorkerOptions("/usr/bin/docker", ImageId, timeout), launcher);

    private static FakeProcess TextProcess(string text) => new(Encoding.UTF8.GetBytes(text));

    private static string Operation(ProcessStartInfo command) =>
        command.ArgumentList[4] == "container" ? command.ArgumentList[5] : command.ArgumentList[4];

    private sealed class FakeLauncher : IWorkerProcessLauncher
    {
        public List<ProcessStartInfo> Commands { get; } = [];
        public TaskCompletionSource WorkerStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FakeProcess? Daemon { get; init; }
        public FakeProcess? Image { get; init; }
        public FakeProcess? Container { get; init; }
        public FakeProcess? Create { get; set; }
        public FakeProcess Worker { get; set; } = new([]);
        public FakeProcess? Remove { get; init; }
        public bool RemovalReportsMissing { get; init; }
        public string? ThrowOnOperation { get; set; }

        public IWorkerProcess Start(ProcessStartInfo startInfo)
        {
            Commands.Add(startInfo);
            var operation = Operation(startInfo);
            if (operation == ThrowOnOperation)
            {
                throw new Win32Exception("Synthetic process launch failure.");
            }

            switch (operation)
            {
                case "info": return Daemon ?? TextProcess(DaemonMetadata);
                case "image": return Image ?? TextProcess(ImageMetadata);
                case "create": return Create ?? TextProcess(ContainerId + "\n");
                case "inspect": return Container ?? TextProcess(ContainerMetadata);
                case "start": WorkerStarted.TrySetResult(); return Worker;
                case "rm":
                    return RemovalReportsMissing
                        ? new FakeProcess([], exitCode: 1) { Error = new MemoryStream(Encoding.UTF8.GetBytes($"Error response from daemon: No such container: {startInfo.ArgumentList.Last()}\n")) }
                        : Remove ?? TextProcess(startInfo.ArgumentList.Last() + "\n");
                default: throw new InvalidOperationException("Unexpected Docker operation.");
            }
        }
    }

    private sealed class FakeProcess(byte[] output, int exitCode = 0) : IWorkerProcess
    {
        private readonly TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool hanging = false;
        public MemoryStream Input { get; } = new();
        public Stream Output { get; set; } = new MemoryStream(output);
        public Stream Error { get; set; } = new MemoryStream();
        public bool Killed { get; private set; }
        public bool Disposed { get; private set; }
        public TaskCompletionSource ExitWaitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken FirstExitWaitToken { get; private set; }
        public bool FailKill { get; set; }
        public bool FailReap { get; set; }
        public bool InputClosed { get; private set; }
        public bool InputOwnerClosed { get; private set; }
        public int ExitCode => exitCode;
        public Stream StandardInput => (inputWriter ??= new StreamWriter(blockedInput ?? new InputStream(Input, () => InputClosed = true), new UTF8Encoding(false))).BaseStream;
        public Stream StandardOutput => Output;
        public Stream StandardError => Error;
        private Stream? blockedInput;
        private StreamWriter? inputWriter;

        private FakeProcess(bool blockInput) : this([])
        {
            hanging = true;
            Output = new UncooperativeStream();
            Error = new UncooperativeStream();
            if (blockInput)
            {
                blockedInput = new UncooperativeStream();
            }
        }

        private FakeProcess(byte[] output, bool waitForRelease) : this(output)
        {
            hanging = waitForRelease;
        }

        public static FakeProcess Hanging(bool blockInput = false) => new(blockInput);
        public static FakeProcess GatedCreation() => new(Encoding.UTF8.GetBytes(ContainerId + "\n"), waitForRelease: true);
        public Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            if (!ExitWaitStarted.Task.IsCompleted)
            {
                FirstExitWaitToken = cancellationToken;
                ExitWaitStarted.TrySetResult();
            }
            return hanging ? exited.Task : Task.CompletedTask;
        }
        public void Settle() => exited.TrySetResult();

        public void CloseStandardInput()
        {
            if (!InputOwnerClosed)
            {
                InputOwnerClosed = true;
                inputWriter?.Dispose();
            }
        }

        public void Kill()
        {
            Killed = true;
            if (FailKill)
            {
                throw new Win32Exception("Synthetic kill failure.");
            }

            if (!FailReap)
            {
                exited.TrySetResult();
            }
        }

        public void Dispose()
        {
            Disposed = true;
            CloseStandardInput();
            Output.Dispose();
            Error.Dispose();
            blockedInput?.Dispose();
        }
    }

    private sealed class InputStream(MemoryStream captured, Action closed) : Stream
    {
        private bool disposed;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !disposed;
        public override long Length => captured.Length;
        public override long Position { get => captured.Position; set => throw new NotSupportedException(); }
        public override void Flush() => ObjectDisposedException.ThrowIf(disposed, this);
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => captured.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => captured.WriteAsync(buffer, cancellationToken);
        protected override void Dispose(bool disposing) { if (disposing && !disposed) { disposed = true; closed(); } base.Dispose(disposing); }
    }

    private sealed class UncooperativeStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(new TaskCompletionSource<int>().Task);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => new(new TaskCompletionSource().Task);
    }
}
