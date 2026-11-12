using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Weft.Application;
using Weft.Domain;
using Weft.MappingBackends.Workers;

namespace Weft.Tests;

public sealed class SpecDocumentWorkerTests
{
    private static readonly SpecSourceIdentity Identity = new(new(new ProjectScope(Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid()), Guid.NewGuid(), 1);
    private static SpecCapturedSource Source(SpecSourceFormat format = SpecSourceFormat.Docx) => new(Identity, format,
        SpecOfficeExtractionTests.Zip(format == SpecSourceFormat.Docx ? SpecOfficeExtractionTests.Word() : SpecOfficeExtractionTests.Excel()));

    // Portable wire qualification only: this helper is test code, never a production launcher/capability.
    private sealed class PortableWorker : ISpecOfficeExtractionWorker
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Reaped { get; private set; }
        internal bool Joined { get; private set; }
        internal int Launches { get; private set; }
        internal Action? AfterResponse { get; init; }
        internal Func<byte[], byte[]>? MutateResponse { get; init; }
        internal Func<byte[], byte[]>? MutateRequest { get; init; }
        internal int Cap { get; init; } = SpecOfficeProcessProtocol.MaximumResponseBytes;
        internal bool WithholdInput { get; init; }
        internal TimeSpan WallTime { get; init; } = TimeSpan.FromSeconds(20);
        internal SpecOfficeExtractionResult? Last { get; private set; }

        public async Task<SpecOfficeExtractionResult> ExtractAsync(SpecCapturedSource source, SpecOfficeExtractionLimits limits, CancellationToken token)
        {
            var request = SpecOfficeProcessProtocol.EncodeRequest(source, limits);
            var result = await Exchange(MutateRequest?.Invoke(request) ?? request, token);
            if (result.Exit != 0) throw new SpecOfficeWorkerUnavailableException();
            var decoded = SpecOfficeProcessProtocol.DecodeResponse(MutateResponse?.Invoke(result.Output) ?? result.Output, source, limits);
            Last = decoded; AfterResponse?.Invoke(); return decoded;
        }
        internal async Task<(int Exit, byte[] Output, byte[] Error)> Exchange(byte[] frame, CancellationToken token = default)
        {
            var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
            Assert.True(host is not null && Path.IsPathFullyQualified(host), "Pinned DOTNET_HOST_PATH is required for actual child tests.");
            var start = new ProcessStartInfo(host!) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "DocumentWorker", "Weft.DocumentWorker.dll"));
            using var child = Process.Start(start)!;
            Launches++; Started.TrySetResult();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(WallTime);
            var output = SpecOfficeProcessProtocol.ReadBoundedAsync(child.StandardOutput.BaseStream, Cap, deadline.Token);
            var error = SpecOfficeProcessProtocol.ReadBoundedAsync(child.StandardError.BaseStream, 4096, deadline.Token);
            var writer = Task.Run(async () =>
            {
                if (WithholdInput) await Task.Delay(Timeout.InfiniteTimeSpan, deadline.Token);
                await child.StandardInput.BaseStream.WriteAsync(frame, deadline.Token);
                child.StandardInput.Close();
            }, CancellationToken.None);
            var exit = child.WaitForExitAsync(deadline.Token);
            Task[] tasks = [output, error, writer, exit];
            try
            {
                var pending = tasks.ToList();
                while (pending.Count != 0)
                { var done = await Task.WhenAny(pending).WaitAsync(deadline.Token); pending.Remove(done); await done; }
                return (child.ExitCode, await output, await error);
            }
            finally
            {
                deadline.Cancel();
                if (!child.HasExited) child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Reaped = child.HasExited;
                try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (Exception) when (tasks.All(t => t.IsCompleted)) { }
                Joined = tasks.All(t => t.IsCompleted);
            }
        }
    }

    [Theory]
    [InlineData(SpecSourceFormat.Docx, "café 東京")]
    [InlineData(SpecSourceFormat.Xlsx, "東京")]
    public async Task Actual_office_child_returns_canonical_source_bound_structural_evidence(SpecSourceFormat format, string text)
    {
        var source = Source(format); var limits = new SpecOfficeExtractionLimits(); var worker = new PortableWorker();
        var result = await worker.ExtractAsync(source, limits, default);
        Assert.Equal(SpecExtractionOutcome.Partial, result.Outcome); Assert.Equal(text, Assert.Single(result.Chunks).Text);
        Assert.Equal(source.Identity, result.Identity.Source); Assert.Equal(source.Digest, result.Identity.SourceDigest);
        Assert.Equal(limits.Digest, result.Identity.ProfileDigest);
        Assert.Equal(SpecOfficeParser.Extract(source, limits).CopyEvidenceBytes(), result.CopyEvidenceBytes());
        Assert.True(worker.Reaped && worker.Joined);
    }

    [Fact]
    public async Task Actual_child_refuses_corrupt_source_hash_bad_limits_and_truncated_request_with_fixed_diagnostics()
    {
        var valid = SpecOfficeProcessProtocol.EncodeRequest(Source(), new());
        var hash = (byte[])valid.Clone(); hash[52] ^= 1;
        var limits = (byte[])valid.Clone(); Array.Clear(limits, 16, 4);
        foreach (var frame in new[] { Array.Empty<byte>(), hash, limits, valid[..^1], valid.Concat(new byte[] { 0 }).ToArray() })
        {
            var worker = new PortableWorker(); var result = await worker.Exchange(frame);
            Assert.Equal(1, result.Exit); Assert.Empty(result.Output);
            Assert.Equal("WeftDocumentWorkerFailed:decode\n", Encoding.UTF8.GetString(result.Error));
            Assert.True(worker.Reaped && worker.Joined);
        }
    }

    [Fact]
    public async Task Actual_child_applies_configured_extraction_limits_and_refusals_remain_atomic()
    {
        var result = await new PortableWorker().ExtractAsync(Source(), new(maximumEntries: 1), default);
        Assert.Equal(SpecExtractionOutcome.LimitExceeded, result.Outcome); Assert.Empty(result.Chunks);
        Assert.Equal("zip-entries", result.Code);
        var malformed = new SpecCapturedSource(Identity, SpecSourceFormat.Docx, "not a zip"u8);
        result = await new PortableWorker().ExtractAsync(malformed, new(), default);
        Assert.Equal(SpecExtractionOutcome.Malformed, result.Outcome); Assert.Empty(result.Chunks);
    }

    [Fact]
    public async Task Portable_wire_cancellation_and_deadline_reap_actual_child_and_join_io()
    {
        foreach (var caller in new[] { true, false })
        {
            var worker = new PortableWorker { WithholdInput = true, WallTime = caller ? TimeSpan.FromSeconds(20) : TimeSpan.FromMilliseconds(300) };
            using var cancel = new CancellationTokenSource();
            var run = worker.ExtractAsync(Source(), new(), cancel.Token);
            await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (caller) cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            Assert.True(worker.Reaped && worker.Joined); Assert.Null(worker.Last);
        }
    }

    [Fact]
    public async Task Actual_child_stdout_is_capped_before_a_result_can_be_admitted()
    {
        var worker = new PortableWorker { Cap = 64 };
        await Assert.ThrowsAsync<SpecOfficeProtocolException>(() => worker.ExtractAsync(Source(), new(), default));
        Assert.True(worker.Reaped && worker.Joined); Assert.Null(worker.Last);
    }

    [Theory]
    [InlineData("sourceSha256", "bad")]
    [InlineData("sourceFormat", "Xlsx")]
    [InlineData("profileSha256", "bad")]
    [InlineData("format", "other")]
    [InlineData("outcome", "Complete")]
    [InlineData("evidenceSha256", "bad")]
    public async Task Parent_refuses_mutated_actual_child_identity_and_outcomes(string field, string value)
    {
        var worker = new PortableWorker
        {
            MutateResponse = bytes =>
            { var json = JsonNode.Parse(bytes)!; json[field] = value; return Encoding.UTF8.GetBytes(json.ToJsonString()); }
        };
        await Assert.ThrowsAsync<SpecOfficeProtocolException>(() => worker.ExtractAsync(Source(), new(), default));
        Assert.True(worker.Reaped && worker.Joined);
    }

    [Fact]
    public void Decoder_requires_closed_fields_and_exact_canonical_evidence_bytes()
    {
        var source = Source(); var limits = new SpecOfficeExtractionLimits();
        var valid = SpecOfficeProcessProtocol.EncodeResponse(source, SpecOfficeParser.Extract(source, limits));
        var json = JsonNode.Parse(valid)!; json["extra"] = true;
        Assert.Throws<SpecOfficeProtocolException>(() => SpecOfficeProcessProtocol.DecodeResponse(Encoding.UTF8.GetBytes(json.ToJsonString()), source, limits));
        var duplicate = Encoding.UTF8.GetString(valid).Insert(1, "\"format\":\"weft.office-worker-response.v1\",");
        Assert.Throws<SpecOfficeProtocolException>(() => SpecOfficeProcessProtocol.DecodeResponse(Encoding.UTF8.GetBytes(duplicate), source, limits));
        var spaced = Encoding.UTF8.GetString(valid).Replace("[\"weft.spec-evidence.v2\"", "[ \"weft.spec-evidence.v2\"", StringComparison.Ordinal);
        Assert.Throws<SpecOfficeProtocolException>(() => SpecOfficeProcessProtocol.DecodeResponse(Encoding.UTF8.GetBytes(spaced), source, limits));
        json = JsonNode.Parse(valid)!; json["sourceByteCount"] = source.ByteCount + 1;
        Assert.Throws<SpecOfficeProtocolException>(() => SpecOfficeProcessProtocol.DecodeResponse(Encoding.UTF8.GetBytes(json.ToJsonString()), source, limits));
    }

    [Fact]
    public async Task Windows_production_composition_is_explicitly_unavailable_without_starting_a_child()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var host = await QualifiedOfficeProcessWorker.CreateAsync("not-a-path", "bad", "bad", "bad", "bad", "bad");
        Assert.False(host.Available); Assert.Equal("document-worker-platform-unsupported", host.Refusal);
        await Assert.ThrowsAsync<SpecOfficeWorkerUnavailableException>(() => host.ExtractAsync(Source(), new(), default));
    }

    [Fact]
    public void Decoder_rejects_canonical_but_invalid_coordinates_and_limit_claims()
    {
        var source = Source(); var limits = new SpecOfficeExtractionLimits();
        var forged = new SpecOfficeExtractionResult(source, limits, SpecExtractionOutcome.Partial, "office-body-text-only",
            [new(0, "text", new SpecEvidenceLocator.WordParagraph("word/document.xml", int.MaxValue))]);
        Assert.Throws<SpecOfficeProtocolException>(() => SpecOfficeProcessProtocol.DecodeResponse(SpecOfficeProcessProtocol.EncodeResponse(source, forged), source, limits));
        limits = new(maximumSourceBytes: 1);
        forged = new(source, limits, SpecExtractionOutcome.Partial, "office-body-text-only", []);
        Assert.Throws<SpecOfficeProtocolException>(() => SpecOfficeProcessProtocol.DecodeResponse(SpecOfficeProcessProtocol.EncodeResponse(source, forged), source, limits));
        source = Source(SpecSourceFormat.Xlsx); limits = new();
        forged = new(source, limits, SpecExtractionOutcome.Partial, "office-stored-cells-only",
            [new(0, "1", new SpecEvidenceLocator.SheetCell("xl/sheet.xml", "Sheet", "A1", SpecCellValueKind.Number, false)),
             new(1, "text", new SpecEvidenceLocator.SheetCell("xl/sheet.xml", "Sheet", "A1", SpecCellValueKind.String, false))]);
        Assert.Throws<SpecOfficeProtocolException>(() => SpecOfficeProcessProtocol.DecodeResponse(SpecOfficeProcessProtocol.EncodeResponse(source, forged), source, limits));
    }

    [Theory]
    [InlineData(SpecCellValueKind.Blank, "unexpected", false, null, false)]
    [InlineData(SpecCellValueKind.Blank, "", true, "1", false)]
    [InlineData(SpecCellValueKind.InlineString, "", false, "1", false)]
    [InlineData(SpecCellValueKind.InlineString, "cached", true, "1", false)]
    [InlineData(SpecCellValueKind.Blank, "", false, null, true)]
    [InlineData(SpecCellValueKind.Blank, "", false, "1", true)]
    [InlineData(SpecCellValueKind.InlineString, "", false, null, true)]
    public void Canonical_correctly_hashed_cell_kinds_must_match_extractor_invariants(
        SpecCellValueKind kind, string text, bool cached, string? formula, bool admitted)
    {
        var source = Source(SpecSourceFormat.Xlsx); var limits = new SpecOfficeExtractionLimits();
        var result = new SpecOfficeExtractionResult(source, limits, SpecExtractionOutcome.Partial, "office-stored-cells-only",
            [new(0, text, new SpecEvidenceLocator.SheetCell("xl/worksheets/sheet1.xml", "Sheet", "A1", kind, cached), formula)]);
        var response = SpecOfficeProcessProtocol.EncodeResponse(source, result);
        using var json = System.Text.Json.JsonDocument.Parse(response);
        var evidence = Encoding.UTF8.GetBytes(json.RootElement.GetProperty("evidence").GetRawText());
        Assert.Equal(result.CopyEvidenceBytes(), evidence);
        Assert.Equal(Sha256Digest.FromBytes(evidence).Value, json.RootElement.GetProperty("evidenceSha256").GetString());
        Assert.Equal(source.Digest.Value, json.RootElement.GetProperty("sourceSha256").GetString());
        Assert.Equal(limits.Digest.Value, json.RootElement.GetProperty("profileSha256").GetString());
        if (admitted)
            Assert.Equal(evidence, SpecOfficeProcessProtocol.DecodeResponse(response, source, limits).CopyEvidenceBytes());
        else
            Assert.Throws<SpecOfficeProtocolException>(() => SpecOfficeProcessProtocol.DecodeResponse(response, source, limits));
    }

    [Fact]
    public async Task Canceled_child_and_wrong_original_owner_cannot_create_extraction_reservations()
    {
        var state = new Publications(); var service = new SpecBundleService(state, state); var source = Source(); var original = Guid.NewGuid();
        await service.UploadAsync(Identity, source.Format, original, Guid.NewGuid(), 1, new MemoryStream(source.CopyBytes()));
        var worker = new PortableWorker { WithholdInput = true };
        await Assert.ThrowsAsync<SpecStorageException>(() => service.ExtractOfficeAndPublishAsync(new(Identity.Bundle, Guid.NewGuid(), 1), source.Format,
            original, Guid.NewGuid(), Guid.NewGuid(), 2, worker, new()));
        Assert.Equal(0, worker.Launches);
        using var cancel = new CancellationTokenSource();
        var pending = service.ExtractOfficeAndPublishAsync(Identity, source.Format, original, Guid.NewGuid(), Guid.NewGuid(), 2, worker, new(), cancel.Token);
        await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Single(state.Reservations); Assert.Null(state.Extraction); Assert.True(worker.Reaped && worker.Joined);
    }

    [Fact]
    public async Task Document_refusal_is_published_atomically_and_retained_in_snapshot()
    {
        var state = new Publications(); var service = new SpecBundleService(state, state); var original = Guid.NewGuid(); var extraction = Guid.NewGuid();
        await service.UploadAsync(Identity, SpecSourceFormat.Docx, original, Guid.NewGuid(), 1, new MemoryStream("malformed zip"u8.ToArray()));
        var result = await service.ExtractOfficeAndPublishAsync(Identity, SpecSourceFormat.Docx, original, extraction, Guid.NewGuid(), 2, new PortableWorker(), new());
        Assert.Equal(SpecExtractionOutcome.Malformed, result.Extraction.Outcome);
        Assert.Equal(result.Extraction, Assert.Single(new SpecBundleSnapshot(Identity.Bundle, [result.Extraction]).Extractions));
        Assert.Equal(result.Extraction.EvidenceDigest, Sha256Digest.FromBytes(state.Bytes[extraction]));
        Assert.Equal(2, JsonNode.Parse(state.Bytes[extraction])!.AsArray().Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Process_exit_or_output_cap_failure_never_becomes_published_document_failure(bool nonzeroExit)
    {
        var state = new Publications(); var service = new SpecBundleService(state, state); var source = Source(); var original = Guid.NewGuid();
        await service.UploadAsync(Identity, source.Format, original, Guid.NewGuid(), 1, new MemoryStream(source.CopyBytes()));
        var worker = nonzeroExit ? new PortableWorker { MutateRequest = bytes => { bytes[52] ^= 1; return bytes; } } : new PortableWorker { Cap = 64 };
        var failure = await Record.ExceptionAsync(() => service.ExtractOfficeAndPublishAsync(Identity, source.Format, original, Guid.NewGuid(), Guid.NewGuid(), 2, worker, new()));
        Assert.True(failure is SpecOfficeWorkerUnavailableException or SpecOfficeProtocolException);
        Assert.Single(state.Reservations); Assert.Null(state.Extraction); Assert.True(worker.Reaped && worker.Joined);
    }

    [Fact]
    public async Task V2_publication_replays_exact_bytes_keys_and_snapshot_after_a_lost_acknowledgement()
    {
        var state = new Publications(); var service = new SpecBundleService(state, state); var source = Source();
        var original = Guid.NewGuid(); var extraction = Guid.NewGuid(); var key = Guid.NewGuid();
        await service.UploadAsync(Identity, source.Format, original, Guid.NewGuid(), 1, new MemoryStream(source.CopyBytes()));
        state.LoseExtractionAck = true; var worker = new PortableWorker();
        await Assert.ThrowsAsync<SpecStorageException>(() => service.ExtractOfficeAndPublishAsync(Identity, source.Format, original, extraction, key, 2, worker, new()));
        var before = new SpecBundleSnapshot(Identity.Bundle, [state.Extraction!]);
        var exact = state.Bytes[extraction];
        var replay = await service.ExtractOfficeAndPublishAsync(Identity, source.Format, original, extraction, key, 2, worker, new());
        Assert.Equal(before.Digest, new SpecBundleSnapshot(Identity.Bundle, [replay.Extraction]).Digest);
        Assert.Equal(exact, state.Bytes[extraction]); Assert.Equal(worker.Last!.CopyEvidenceBytes(), exact);
        Assert.Equal(Sha256Digest.FromBytes(exact), replay.Publication.Claim.Reference.Digest);
        Assert.Equal(key, replay.Publication.Claim.AttemptId); Assert.Equal(SpecExtractionOutcome.Partial, replay.Extraction.Outcome);
        Assert.Equal(2, state.Reservations.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Source_or_worker_identity_failure_never_reserves_extraction(bool corruptSource)
    {
        var state = new Publications(); var service = new SpecBundleService(state, state); var source = Source(); var original = Guid.NewGuid();
        await service.UploadAsync(Identity, source.Format, original, Guid.NewGuid(), 1, new MemoryStream(source.CopyBytes()));
        if (corruptSource) state.Bytes[original][0] ^= 1;
        var worker = new PortableWorker { MutateResponse = bytes => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace(source.Digest.Value, new string('a', 64), StringComparison.Ordinal)) };
        var exception = await Record.ExceptionAsync(() => service.ExtractOfficeAndPublishAsync(Identity, source.Format, original, Guid.NewGuid(), Guid.NewGuid(), 2, worker, new()));
        Assert.NotNull(exception); Assert.True(exception is SpecStorageException or SpecOfficeProtocolException);
        Assert.Single(state.Reservations); Assert.Null(state.Extraction);
        Assert.Equal(corruptSource ? 0 : 1, worker.Launches);
    }

    [Fact]
    public async Task Current_authority_is_rechecked_after_worker_and_unavailable_worker_never_publishes_evidence()
    {
        var state = new Publications(); var service = new SpecBundleService(state, state); var source = Source(); var original = Guid.NewGuid();
        await service.UploadAsync(Identity, source.Format, original, Guid.NewGuid(), 1, new MemoryStream(source.CopyBytes()));
        await Assert.ThrowsAsync<SpecOfficeWorkerUnavailableException>(() => service.ExtractOfficeAndPublishAsync(Identity, source.Format, original,
            Guid.NewGuid(), Guid.NewGuid(), 2, new UnavailableSpecOfficeExtractionWorker(), new()));
        var worker = new PortableWorker { AfterResponse = () => state.Deny = true };
        await Assert.ThrowsAsync<SpecStorageException>(() => service.ExtractOfficeAndPublishAsync(Identity, source.Format, original, Guid.NewGuid(), Guid.NewGuid(), 2, worker, new()));
        Assert.Single(state.Reservations); Assert.Null(state.Extraction);
    }

    // Controlled actor-bound storage/lifecycle contract, not a production backend qualification.
    private sealed class Publications : ISpecBundleLifecycle, ISpecObjectStore
    {
        internal Dictionary<Guid, SpecObjectReservation> Reservations { get; } = [];
        internal Dictionary<Guid, byte[]> Bytes { get; } = [];
        internal SpecExtractionIdentity? Extraction { get; private set; }
        internal bool LoseExtractionAck { get; set; }
        internal bool Deny { get; set; }
        private long version = 1;
        public Task<SpecBundleStatus> AuthorizeAsync(SpecBundleScope scope, bool write, CancellationToken token)
        { token.ThrowIfCancellationRequested(); if (Deny) throw new SpecStorageException(SpecStorageError.NotAdmitted); return Task.FromResult(new SpecBundleStatus(scope, version, false)); }
        public Task<SpecBundleStatus> CreateAsync(SpecBundleScope scope, Guid key, CancellationToken token) => AuthorizeAsync(scope, true, token);
        public async Task<SpecObjectReservation> ReserveAsync(SpecObjectReference reference, SpecSourceFormat format, Guid attempt, long expected, SpecExtractionIdentity? extraction, CancellationToken token)
        {
            await AuthorizeAsync(reference.Id.Source.Bundle, true, token);
            if (Reservations.TryGetValue(reference.Id.ObjectId, out var old))
            { Assert.Equal(old.Claim.AttemptId, attempt); Assert.Equal(old.Claim.Reference, reference); return old; }
            if (expected != version) throw new SpecStorageException(SpecStorageError.Conflict);
            var reservation = new SpecObjectReservation(new(Guid.NewGuid(), attempt, reference), ++version);
            Reservations.Add(reference.Id.ObjectId, reservation); if (extraction is not null) Extraction = extraction;
            return reservation;
        }
        public async Task<SpecObjectReference> FindAsync(SpecBundleScope scope, Guid id, CancellationToken token)
        { await AuthorizeAsync(scope, false, token); return Reservations[id].Claim.Reference; }
        public async Task<SpecPublishResult> PublishAsync(SpecBundleScope scope, SpecPublicationClaim claim, Stream input, CancellationToken token)
        {
            await AuthorizeAsync(scope, true, token);
            var bytes = await SpecObjectBytes.ReadVerifiedAsync(claim.Reference, input, token);
            var exists = Bytes.ContainsKey(claim.Reference.Id.ObjectId); Bytes[claim.Reference.Id.ObjectId] = bytes;
            if (LoseExtractionAck && claim.Reference.Id.Kind == SpecObjectKind.Extraction)
            { LoseExtractionAck = false; throw new SpecStorageException(SpecStorageError.Io); }
            return exists ? SpecPublishResult.AlreadyPresent : SpecPublishResult.Created;
        }
        public async Task<byte[]> ReadAsync(SpecBundleScope scope, SpecObjectReference reference, CancellationToken token)
        { await AuthorizeAsync(scope, false, token); return (byte[])Bytes[reference.Id.ObjectId].Clone(); }
        public Task<SpecBundleStatus> TombstoneAsync(SpecBundleScope scope, SpecSourceIdentity? source, long expected, Guid key, CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<SpecRetirementClaim>> RetirementsAsync(SpecBundleScope scope, CancellationToken token) => throw new NotSupportedException();
        public Task<SpecRetireResult> RetireAsync(SpecBundleScope scope, SpecRetirementClaim claim, CancellationToken token = default) => throw new NotSupportedException();
    }
}
