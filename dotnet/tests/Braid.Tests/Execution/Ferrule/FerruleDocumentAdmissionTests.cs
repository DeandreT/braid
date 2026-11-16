using Braid.Application;
using Braid.MappingBackends;
using Braid.MappingBackends.Workers;

namespace Braid.Tests;

public sealed class FerruleDocumentAdmissionTests
{
    [Fact]
    public void JsonAndDocumentProfilesObserveTheSameLiveRunnerAdmissionWithoutDispatch()
    {
        var runner = new AdmissionRunner();
        var json = new FerrulePreviewBackend(runner);
        var document = new FerrulePreviewBackend(runner, PreviewExecutionProfile.Document);
        foreach (var admitted in new[] { false, true, false })
        {
            runner.CanExecute = admitted;
            Assert.Equal(admitted, json.CanExecute);
            Assert.Equal(admitted, document.CanExecute);
        }
    }

    private sealed class AdmissionRunner : IInspectionWorkerRunner, IExecutionAdmission
    {
        public string BuildIdentity => "sha256:" + new string('a', 64);
        public bool CanExecute { get; set; }
        public Task<WorkerProcessResult> RunAsync(ReadOnlyMemory<byte> requestFrame, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Reading admission must not dispatch a worker.");
    }
}
