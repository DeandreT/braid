using System.Text;
using System.Text.Json.Nodes;
using Braid.MappingBackends.Workers;

namespace Braid.Tests;

/// <summary>Deterministic process-seam ownership cases; not actual Docker qualification.</summary>
public sealed partial class FerruleDockerRunnerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactNameCreateConflictCannotRemoveAnUnownedVictim(bool previousOperationLabel)
    {
        var victimStillPresent = true;
        var launcher = new FakeLauncher
        {
            Create = new FakeProcess([], exitCode: 125),
            OwnershipInspection = (target, name, owner) =>
            {
                Assert.Equal(name, target);
                var node = JsonNode.Parse(OwnedMetadata(name, OtherOwner(owner), OtherContainerId))!;
                if (!previousOperationLabel) node["Config"]!["Labels"] = null;
                return TextProcess(node.ToJsonString());
            },
            RemoveObserved = _ => victimStillPresent = false
        };
        var runner = Runner(launcher);

        Assert.Equal(WorkerProcessFailure.CleanupFailed, (await runner.RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None)).Failure);
        Assert.True(victimStillPresent);
        Assert.Equal(["info", "image", "create", "ownership-inspect"], launcher.Commands.Select(Operation));
        var count = launcher.Commands.Count;
        Assert.Equal(WorkerProcessFailure.SandboxUnavailable, (await runner.RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None)).Failure);
        Assert.Equal(count, launcher.Commands.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("truncated-id\n")]
    [InlineData(ContainerId + "\nextra\n")]
    public async Task LostOrMalformedCreateAcknowledgementUsesNameOnlyForReadOnlyOwnershipDiscovery(string acknowledgement)
    {
        var launcher = new FakeLauncher { Create = TextProcess(acknowledgement) };
        var result = await Runner(launcher).RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None);

        Assert.Equal(WorkerProcessFailure.WorkerFailed, result.Failure);
        Assert.Empty(result.Output);
        Assert.Equal(["info", "image", "create", "ownership-inspect", "rm"], launcher.Commands.Select(Operation));
        Assert.Equal(launcher.CreatedNames[0], launcher.Commands[3].ArgumentList.Last());
        Assert.Equal(ContainerId, launcher.Commands[4].ArgumentList.Last());
        Assert.DoesNotContain(launcher.Commands, command => Operation(command) is "inspect" or "start");
    }

    [Fact]
    public async Task LostAcknowledgementAndExactNameAbsenceNeverPretendTheDaemonCreateSettled()
    {
        var launcher = new FakeLauncher
        {
            Create = TextProcess(""),
            OwnershipInspection = (target, _, _) => MissingProcess(target)
        };
        var runner = Runner(launcher);

        Assert.Equal(WorkerProcessFailure.CleanupFailed, (await runner.RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None)).Failure);
        Assert.DoesNotContain(launcher.Commands, command => Operation(command) == "rm");
        var count = launcher.Commands.Count;
        Assert.Equal(WorkerProcessFailure.SandboxUnavailable, (await runner.RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None)).Failure);
        Assert.Equal(count, launcher.Commands.Count);
    }

    [Theory]
    [InlineData("Id", "missing")]
    [InlineData("Id", "short")]
    [InlineData("Id", "nonhex")]
    [InlineData("Id", "other")]
    [InlineData("Name", "missing")]
    [InlineData("Name", "other")]
    [InlineData("Name", "number")]
    [InlineData("Config", "missing")]
    [InlineData("Labels", "missing")]
    [InlineData("Labels", "array")]
    [InlineData("Owner", "missing")]
    [InlineData("Owner", "other")]
    [InlineData("Owner", "number")]
    public async Task CleanupOwnershipRequiresExactCurrentNameLabelAndAcknowledgedFullId(string field, string change)
    {
        var launcher = new FakeLauncher
        {
            OwnershipInspection = (_, name, owner) =>
            {
                var node = JsonNode.Parse(OwnedMetadata(name, owner))!;
                var parent = field switch
                {
                    "Labels" => (JsonObject)node["Config"]!,
                    "Owner" => (JsonObject)node["Config"]!["Labels"]!,
                    _ => (JsonObject)node
                };
                var property = field == "Owner" ? OperationLabel : field;
                if (change == "missing") parent.Remove(property);
                else parent[property] = change switch
                {
                    "short" => JsonValue.Create("bbbb"),
                    "nonhex" => JsonValue.Create(new string('z', 64)),
                    "array" => new JsonArray(),
                    "number" => JsonValue.Create(1),
                    _ => JsonValue.Create(field == "Id" ? OtherContainerId : field == "Owner" ? OtherOwner(owner) : "another-operation")
                };
                return TextProcess(node.ToJsonString());
            }
        };
        var runner = Runner(launcher);

        Assert.Equal(WorkerProcessFailure.CleanupFailed, (await runner.RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None)).Failure);
        Assert.Contains(launcher.Commands, command => Operation(command) == "start");
        Assert.DoesNotContain(launcher.Commands, command => Operation(command) == "rm");
        var count = launcher.Commands.Count;
        Assert.Equal(WorkerProcessFailure.SandboxUnavailable, (await runner.RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None)).Failure);
        Assert.Equal(count, launcher.Commands.Count);
    }

    [Theory]
    [InlineData("Id")]
    [InlineData("Name")]
    [InlineData("Config")]
    [InlineData("Labels")]
    [InlineData("Owner")]
    public async Task DuplicateIdentityFieldsCannotAuthorizeRemoval(string duplicate)
    {
        var launcher = new FakeLauncher
        {
            OwnershipInspection = (_, name, owner) =>
            {
                var label = $"\"{OperationLabel}\":\"{owner}\"";
                var labels = "{\"Labels\":{" + label + "}}";
                var config = duplicate switch
                {
                    "Labels" => "{\"Labels\":{" + label + "},\"Labels\":{" + label + "}}",
                    "Owner" => "{\"Labels\":{" + label + "," + label + "}}",
                    _ => labels
                };
                var id = $"\"Id\":\"{ContainerId}\"";
                var named = $"\"Name\":\"/{name}\"";
                var configured = "\"Config\":" + config;
                var json = "{" + id + "," + named + "," + configured;
                json += duplicate switch
                {
                    "Id" => "," + id,
                    "Name" => "," + named,
                    "Config" => "," + configured,
                    _ => ""
                };
                return TextProcess(json + "}");
            }
        };

        Assert.Equal(WorkerProcessFailure.CleanupFailed, (await Runner(launcher).RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None)).Failure);
        Assert.DoesNotContain(launcher.Commands, command => Operation(command) == "rm");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnershipControlOutputRemainsStrictUtf8AndBounded(bool oversized)
    {
        var launcher = new FakeLauncher
        {
            OwnershipInspection = (_, _, _) => new FakeProcess(oversized ? new byte[(64 * 1024) + 1] : [255])
        };
        var runner = Runner(launcher);

        Assert.Equal(WorkerProcessFailure.CleanupFailed, (await runner.RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None)).Failure);
        Assert.DoesNotContain(launcher.Commands, command => Operation(command) == "rm");
        Assert.Equal(WorkerProcessFailure.SandboxUnavailable, (await runner.RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None)).Failure);
    }

    [Fact]
    public async Task ReassignedNameAfterOwnershipReadCannotRetargetTheExactIdRemoval()
    {
        var liveIds = new HashSet<string>(StringComparer.Ordinal) { ContainerId, OtherContainerId };
        var nameNowResolvesTo = ContainerId;
        var launcher = new FakeLauncher
        {
            OwnershipInspection = (target, name, owner) =>
            {
                Assert.Equal(ContainerId, target);
                var snapshot = OwnedMetadata(name, owner);
                // Model rename/reassignment immediately AFTER the read-only response.
                nameNowResolvesTo = OtherContainerId;
                return TextProcess(snapshot);
            },
            RemoveObserved = target => liveIds.Remove(target.Length == 64 ? target : nameNowResolvesTo)
        };

        Assert.Equal(WorkerProcessFailure.None, (await Runner(launcher).RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None)).Failure);
        Assert.DoesNotContain(ContainerId, liveIds);
        Assert.Contains(OtherContainerId, liveIds);
        Assert.Equal(ContainerId, launcher.Commands.Last().ArgumentList.Last());
    }

    [Fact]
    public async Task ConfirmedIdAbsenceDoesNotInspectOrRemoveItsReusedName()
    {
        var replacementStillPresent = true;
        var launcher = new FakeLauncher
        {
            OwnershipInspection = (target, _, _) =>
            {
                Assert.Equal(ContainerId, target);
                return MissingProcess(target);
            },
            RemoveObserved = _ => replacementStillPresent = false
        };

        Assert.Equal(WorkerProcessFailure.None, (await Runner(launcher).RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None)).Failure);
        Assert.True(replacementStillPresent);
        Assert.DoesNotContain(launcher.Commands, command => Operation(command) == "rm");
    }

    [Fact]
    public async Task PolicyInspectionCannotStartAContainerWithAnotherOperationsOwnership()
    {
        var launcher = new FakeLauncher
        {
            PolicyInspection = (_, name, owner) => TextProcess(OwnedMetadata(name, OtherOwner(owner))),
            OwnershipInspection = (_, name, owner) => TextProcess(OwnedMetadata(name, OtherOwner(owner)))
        };

        Assert.Equal(WorkerProcessFailure.CleanupFailed, (await Runner(launcher).RunAsync("native"u8.ToArray(), CancellationToken.None)).Failure);
        Assert.DoesNotContain(launcher.Commands, command => Operation(command) is "start" or "rm");
        Assert.Empty(launcher.Worker.Input.ToArray());
    }

    [Fact]
    public async Task EachAdmissionUsesAFreshMarkerIndependentOfTheContainerName()
    {
        var launcher = new FakeLauncher();
        var runner = Runner(launcher);
        for (var index = 0; index < 2; index++)
        {
            launcher.Worker = new FakeProcess([]);
            Assert.Equal(WorkerProcessFailure.None, (await runner.RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None)).Failure);
        }

        Assert.Equal(2, launcher.CreatedOwners.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, launcher.CreatedNames.Distinct(StringComparer.Ordinal).Count());
        foreach (var owner in launcher.CreatedOwners)
        {
            Assert.Matches("^[a-f0-9]{32}$", owner);
            Assert.DoesNotContain("braid-inspect-" + owner, launcher.CreatedNames);
        }
    }

    [Fact]
    public async Task FailedCreateLaunchHasNoDaemonOwnershipToDiscoverAndDoesNotPoisonAdmission()
    {
        var launcher = new FakeLauncher { ThrowOnOperation = "create" };
        var runner = Runner(launcher);
        Assert.Equal(WorkerProcessFailure.StartFailed, (await runner.RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None)).Failure);
        Assert.DoesNotContain(launcher.Commands, command => Operation(command) is "ownership-inspect" or "rm");
        launcher.ThrowOnOperation = null;
        Assert.Equal(WorkerProcessFailure.None, (await runner.RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None)).Failure);
    }

    [Fact]
    public async Task HungOwnershipReadConsumesTheExistingCleanupBudgetAndNeverStartsRemoval()
    {
        var inspection = FakeProcess.Hanging();
        var launcher = new FakeLauncher { OwnershipInspection = (_, _, _) => inspection };
        var runner = Runner(launcher);

        Assert.Equal(WorkerProcessFailure.CleanupFailed,
            (await runner.RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(4))).Failure);
        Assert.True(inspection.Killed);
        Assert.DoesNotContain(launcher.Commands, command => Operation(command) == "rm");
        Assert.Equal(WorkerProcessFailure.SandboxUnavailable, (await runner.RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None)).Failure);
    }

    [Theory]
    [InlineData("Id")]
    [InlineData("Name")]
    [InlineData("Owner")]
    [InlineData("duplicate")]
    public void TestTeardownOracleAlsoRefusesUnownedOrAmbiguousContainerMetadata(string mismatch)
    {
        var name = "braid-inspect-" + new string('0', 32);
        var owner = new string('1', 32);
        var metadata = OwnedMetadata(name, owner);
        var node = JsonNode.Parse(metadata)!;
        if (mismatch == "Owner") node["Config"]!["Labels"]![OperationLabel] = OtherOwner(owner);
        else if (mismatch == "Id") node["Id"] = OtherContainerId;
        else if (mismatch == "Name") node["Name"] = "/another-name";
        var json = mismatch == "duplicate" ? "{\"Id\":\"" + ContainerId + "\"," + metadata[1..] : node.ToJsonString();

        Assert.ThrowsAny<Exception>(() => DockerWorkerOwnershipTestSupport.VerifiedId(json, name, owner, ContainerId));
    }

    [Theory]
    [InlineData("Name")]
    [InlineData("Label")]
    [InlineData("Id")]
    public async Task EscapedUnpairedUnicodeOwnershipMetadataFailsClosedAndDeletesPrivateConfiguration(string field)
    {
        string? privateDirectory = null;
        FakeLauncher? launcher = null;
        launcher = new FakeLauncher
        {
            Worker = TextProcess("must-be-discarded"),
            OwnershipInspection = (_, name, owner) =>
            {
                privateDirectory = launcher!.Commands[^1].WorkingDirectory;
                Assert.True(Directory.Exists(privateDirectory));
                // These are literal ASCII JSON escape bytes, not a .NET surrogate
                // encoded through replacement fallback or a JSON serializer.
                var id = field == "Id" ? "\\uD800" : ContainerId;
                var inspectedName = field == "Name" ? "\\uD800" : "/" + name;
                var inspectedOwner = field == "Label" ? "\\uD800" : owner;
                return TextProcess("{\"Id\":\"" + id + "\",\"Name\":\"" + inspectedName
                    + "\",\"Config\":{\"Labels\":{\"" + OperationLabel + "\":\"" + inspectedOwner + "\"}}}");
            }
        };
        var runner = Runner(launcher);

        var result = await runner.RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None);

        Assert.Equal(WorkerProcessFailure.CleanupFailed, result.Failure);
        Assert.Empty(result.Output);
        Assert.Equal(["info", "image", "create", "inspect", "start", "ownership-inspect"], launcher.Commands.Select(Operation));
        Assert.DoesNotContain(launcher.Commands, command => Operation(command) == "rm");
        Assert.NotNull(privateDirectory);
        Assert.False(Directory.Exists(privateDirectory));
        Assert.All(launcher.Commands, command => Assert.Equal(privateDirectory, command.WorkingDirectory));
        var count = launcher.Commands.Count;
        Assert.Equal(WorkerProcessFailure.SandboxUnavailable,
            (await runner.RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None)).Failure);
        Assert.Equal(count, launcher.Commands.Count);
    }

    private static string OtherOwner(string owner) => (owner[0] == '0' ? "1" : "0") + owner[1..];

    private static FakeProcess MissingProcess(string target) => new([], exitCode: 1)
    {
        Error = new MemoryStream(Encoding.UTF8.GetBytes($"Error: No such container: {target}\n"))
    };
}
