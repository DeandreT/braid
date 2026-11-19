using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Braid.Application;
using Braid.Domain;
using Braid.MappingBackends.Ferrule;
using Braid.MappingBackends.Workers;

namespace Braid.MappingBackends;

/// <summary>Runs bounded previews through an explicitly selected restricted Ferrule admission profile.</summary>
public sealed class FerrulePreviewBackend : IMappingPreviewBackend, IExecutionAdmission
{
    public const string ProtocolVersion = "braid.ferrule-preview.v1";
    public const string AdapterBuild = "braid.ferrule-preview-adapter.v1";
    public const string DocumentProtocolVersion = "braid.ferrule-document-preview.v1";
    public const string DocumentAdapterBuild = "braid.ferrule-document-preview-adapter.v1";
    public const string CsvProtocolVersion = CsvDocumentPolicy.Profile;
    public const string CsvAdapterBuild = "braid.ferrule-csv-preview-adapter.v1";
    public const string XmlAdapterBuild = "braid.ferrule-xml-preview-adapter.v1";
    private readonly bool _xmlEnabled;
    private const string PrimaryOutput = "primary-output";
    private const string NamedOutputPrefix = "named-output:";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = CreateOptions();
    private readonly IInspectionWorkerRunner _runner;
    private readonly string _backendBuild;
    private readonly PreviewExecutionProfile _profile;
    private readonly IReadOnlyList<NativeDocumentFormat> _nativeFormats;
    private NativeDocumentFormat? NativeFormat => NativeDocumentFormat.FromExecution(_profile);
    private string SelectedProfile => NativeFormat?.Profile ?? _profile switch
    {
        PreviewExecutionProfile.CsvDocument => CsvDocumentPolicy.Profile,
        PreviewExecutionProfile.XmlDocument => XmlPreviewAdmission.Profile,
        PreviewExecutionProfile.Document => DocumentPreviewPolicy.Profile,
        _ => PreviewPolicy.Profile
    };
    private const string PortableProtocol = "braid.ferrule-portable-workbook-preview.v1";
    private string SelectedProtocol => NativeFormat == NativeDocumentFormat.Xlsx ? PortableProtocol : NativeFormat?.Profile ?? _profile switch
    {
        PreviewExecutionProfile.CsvDocument => CsvProtocolVersion,
        PreviewExecutionProfile.XmlDocument => XmlPreviewAdmission.Profile,
        PreviewExecutionProfile.Document => DocumentProtocolVersion,
        _ => ProtocolVersion
    };
    private string SelectedAdapter => NativeFormat?.AdapterBuild ?? _profile switch
    {
        PreviewExecutionProfile.CsvDocument => CsvAdapterBuild,
        PreviewExecutionProfile.XmlDocument => XmlAdapterBuild,
        PreviewExecutionProfile.Document => DocumentAdapterBuild,
        _ => AdapterBuild
    };

    public bool CanExecute => _runner is not IExecutionAdmission { CanExecute: false };

    public FerrulePreviewBackend(IInspectionWorkerRunner runner)
        : this(runner, PreviewExecutionProfile.LegacyJson)
    {
    }

    public FerrulePreviewBackend(IInspectionWorkerRunner runner, PreviewExecutionProfile profile, bool xmlEnabled = false,
        IReadOnlyList<NativeDocumentFormat>? nativeFormats = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        if (!Enum.IsDefined(profile))
            throw new ArgumentOutOfRangeException(nameof(profile));
        _profile = profile;
        _nativeFormats = nativeFormats?.ToArray() ?? [];
        _xmlEnabled = xmlEnabled;
        if (runner is DockerInspectionWorkerRunner docker && docker.Profile != DockerWorkerProfile.Preview)
            throw new ArgumentException("Preview requires a runner configured with the preview worker profile.", nameof(runner));
        var identity = runner.BuildIdentity;
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        if (StrictUtf8.GetByteCount(identity) > PreviewPolicy.MaximumTextBytes)
            throw new ArgumentException("The trusted worker build identity exceeds its bound.", nameof(runner));
        _runner = runner;
        _backendBuild = identity;
    }

    public async Task<MappingPreviewResult> PreviewAsync(MappingPreviewRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Profile is { } requested && requested != _profile)
            return await new FerrulePreviewBackend(_runner, requested, _xmlEnabled, _nativeFormats).PreviewAsync(request, cancellationToken).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(request.Artifact);
        ArgumentNullException.ThrowIfNull(request.Target);
        ArgumentNullException.ThrowIfNull(request.Primary);
        ArgumentNullException.ThrowIfNull(request.NamedInputs);
        ArgumentNullException.ThrowIfNull(request.Parameters);
        ArgumentNullException.ThrowIfNull(request.CurrentDateTime);
        ArgumentNullException.ThrowIfNull(request.Limits);
        if (cancellationToken.IsCancellationRequested)
            return LocalFailure(request.Artifact, MappingPreviewStatus.Canceled);
        if (Refuse(request) is { } refusal)
            return refusal;

        var requestId = Guid.NewGuid().ToString("D");
        if (FrameRequest(request, requestId) is not { } frame)
            return LocalFailure(request.Artifact, MappingPreviewStatus.InvalidRequest, "preview.request", "The preview request header exceeds its bound.");
        WorkerProcessResult process;
        try
        {
            process = await _runner.RunAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return LocalFailure(request.Artifact, MappingPreviewStatus.Canceled);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            return LocalFailure(request.Artifact, MappingPreviewStatus.BackendFailure);
        }

        if (process.Failure != WorkerProcessFailure.None)
            return LocalFailure(request.Artifact, ProcessStatus(process.Failure));
        try
        {
            return Decode(process.Output, request, requestId);
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or ProtocolException or NotSupportedException or ArgumentException or PreviewRunException or XmlDocumentException)
        {
            return LocalFailure(request.Artifact, MappingPreviewStatus.ProtocolFailure);
        }
    }

    private MappingPreviewResult? Refuse(MappingPreviewRequest request)
    {
        var artifact = request.Artifact;
        MappingPreviewResult Invalid(string message, string? subject = null) =>
            LocalFailure(artifact, MappingPreviewStatus.InvalidRequest, "preview.request", message, subject);

        if (artifact.ByteCount > InspectionPolicy.MaximumNativeBytes)
            return LocalFailure(artifact, MappingPreviewStatus.ResourceLimit);
        if (_profile == PreviewExecutionProfile.XmlDocument && !_xmlEnabled)
            return LocalFailure(artifact, MappingPreviewStatus.UnsupportedCapability, "preview.xmlDisabled", "XML previews are not enabled.");
        if (_profile != PreviewExecutionProfile.XmlDocument && request.NamedInputs.Prepend(request.Primary).Any(input => input?.XmlMetadata is not null))
            return Invalid("XML metadata requires its own admission profile.");
        if (NativeFormat is { } nativeFormat)
        {
            if (!_nativeFormats.Contains(nativeFormat)) return LocalFailure(artifact, MappingPreviewStatus.UnsupportedCapability, "preview.nativeDisabled", "This native document profile is not enabled.");
            if (request.NamedInputs.Count != 0 || request.Target.Kind == PreviewTargetKind.Named) return Invalid("Native documents require primary boundaries.");
            try
            {
                if (request.Primary.DocumentSettings != NativeDocumentNativeSettings.Read(artifact, nativeFormat).Input) return Invalid("Native input settings do not match the revision.");
                nativeFormat.CheckBytes(request.Primary.Bytes);
            }
            catch (PreviewRunException) { return Invalid("Invalid native document or saved settings."); }
        }
        else if (request.NamedInputs.Prepend(request.Primary).Any(input => input?.DocumentSettings is not null)) return Invalid("Native settings require a matching profile.");
        var limits = request.Limits;
        if (limits.MaximumInputBytes is < 1 or > PreviewPolicy.MaximumInputBytes
            || limits.MaximumOutputBytes is < 1 or > PreviewPolicy.MaximumOutputBytes
            || limits.MaximumArtifacts is < 1 or > PreviewPolicy.MaximumArtifacts
            || limits.MaximumTextBytes is < PreviewPolicy.MinimumTextBytes or > PreviewPolicy.MaximumTextBytes)
            return Invalid("The preview limits are outside the server ceilings.");
        if (request.Target.Kind == PreviewTargetKind.Named && !BoundedName(request.Target.Name))
            return Invalid("The selected target name is outside its bounds.");
        if (request.Primary.Name is not null)
            return Invalid("The primary input cannot carry a named-source identity.");
        if (_profile == PreviewExecutionProfile.CsvDocument &&
            (request.NamedInputs.Count != 0 || request.Target.Kind == PreviewTargetKind.Named))
            return LocalFailure(artifact, MappingPreviewStatus.UnsupportedCapability, "preview.csvPlan",
                "CSV preview supports only primary flat CSV-to-CSV plans, without named endpoints.");
        if (request.NamedInputs.Count + 1 > PreviewPolicy.MaximumInputDocuments)
            return Invalid("The request exceeds the input document count limit.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        long total = request.Primary.ByteCount;
        foreach (var input in request.NamedInputs)
        {
            if (input is null || !BoundedName(input.Name) || !names.Add(input.Name!))
                return Invalid("Named inputs must have distinct bounded names.", input?.Name);
            total += input.ByteCount;
        }
        if (!NativeDocumentPolicy.AllowsEmpty(_profile) && (request.Primary.ByteCount == 0 || request.NamedInputs.Any(input => input.ByteCount == 0)))
            return Invalid("Input documents cannot be empty.");
        if (total > limits.MaximumInputBytes)
            return LocalFailure(artifact, MappingPreviewStatus.ResourceLimit, "preview.inputLimit", "The input documents exceed the preview input limit.");
        if (request.Parameters.Count > PreviewPolicy.MaximumParameters)
            return Invalid("The request exceeds the runtime parameter count limit.");
        var parameterNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in request.Parameters)
        {
            if (parameter is null || !BoundedName(parameter.Name) || !parameterNames.Add(parameter.Name))
                return Invalid("Runtime parameters must have distinct bounded names.", parameter?.Name);
            if (!Enum.IsDefined(parameter.Kind) || parameter.Value is null || !Utf8Within(parameter.Value, PreviewPolicy.MaximumParameterTextBytes)
                || (parameter.Kind == PreviewParameterKind.Boolean && parameter.Value is not ("true" or "false")))
                return LocalFailure(artifact, MappingPreviewStatus.InvalidParameter, "preview.parameter", "A runtime parameter value is not admitted for its kind.", parameter.Name);
        }
        if (request.CurrentDateTime.Length is 0 or > PreviewPolicy.MaximumClockBytes || !request.CurrentDateTime.All(char.IsAscii))
            return Invalid("The execution clock is not an admitted dateTime.");
        if (_profile == PreviewExecutionProfile.XmlDocument)
        {
            try
            {
                var inputs = request.NamedInputs.Prepend(request.Primary).ToArray();
                if (inputs.Any(input => input.XmlMetadata is null)) return Invalid("XML inputs require saved metadata.");
                XmlPreviewAdmission.BindInputs(artifact, inputs);
                XmlPreviewAdmission.Outputs(artifact, request.Target);
            }
            catch (XmlDocumentException error)
            {
                return LocalFailure(artifact, error.IsResourceLimit ? MappingPreviewStatus.ResourceLimit : error.Reason == XmlDocumentFailure.InvalidNativeMetadata ? MappingPreviewStatus.UnsupportedCapability : MappingPreviewStatus.InvalidInput,
                    "preview.xml." + JsonNamingPolicy.CamelCase.ConvertName(error.Reason.ToString()), error.Message);
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                return Invalid("The saved XML boundaries are invalid.");
            }
        }
        return null;
    }

    private static bool BoundedName(string? name) =>
        !string.IsNullOrEmpty(name) && !name.Contains('\0', StringComparison.Ordinal) && Utf8Within(name, PreviewPolicy.MaximumNameBytes);

    private static bool Utf8Within(string text, int maximumBytes)
    {
        try
        {
            return StrictUtf8.GetByteCount(text) <= maximumBytes;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private byte[]? FrameRequest(MappingPreviewRequest request, string requestId)
    {
        var artifact = request.Artifact;
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("protocolVersion", SelectedProtocol);
            json.WriteString("requestId", requestId);
            json.WriteStartObject("revision");
            json.WriteString("organizationId", artifact.Scope.OrganizationId.ToString("D"));
            json.WriteString("projectId", artifact.Scope.ProjectId.ToString("D"));
            json.WriteString("mappingId", artifact.Scope.MappingId.ToString("D"));
            json.WriteString("revisionId", artifact.Scope.RevisionId.ToString("D"));
            json.WriteEndObject();
            json.WriteString("nativeDigest", artifact.Digest.Value);
            json.WriteString("documentId", InspectionPolicy.DocumentId);
            json.WriteStartObject("target");
            json.WriteString("kind", request.Target.Kind switch
            {
                PreviewTargetKind.All => "all",
                PreviewTargetKind.Primary => "primary",
                _ => "named"
            });
            if (request.Target.Kind == PreviewTargetKind.Named)
                json.WriteString("name", request.Target.Name);
            json.WriteEndObject();
            json.WriteString("currentDateTime", request.CurrentDateTime);
            json.WriteStartArray("parameters");
            foreach (var parameter in request.Parameters)
            {
                json.WriteStartObject();
                json.WriteString("name", parameter.Name);
                json.WriteStartObject("value");
                json.WriteString("kind", parameter.Kind switch
                {
                    PreviewParameterKind.String => "string",
                    PreviewParameterKind.Integer => "integer",
                    PreviewParameterKind.Float => "float",
                    _ => "boolean"
                });
                if (parameter.Kind == PreviewParameterKind.Boolean)
                    json.WriteBoolean("value", parameter.Value == "true");
                else
                    json.WriteString("value", parameter.Value);
                json.WriteEndObject();
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteStartArray("inputs");
            foreach (var input in request.NamedInputs.Prepend(request.Primary))
            {
                json.WriteStartObject();
                json.WriteString("role", input.Name is null ? "primary" : "named");
                if (input.Name is not null)
                    json.WriteString("name", input.Name);
                json.WriteNumber("byteLength", input.ByteCount);
                json.WriteString("sha256", input.Digest.Value);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteStartObject("limits");
            json.WriteNumber("maximumInputBytes", request.Limits.MaximumInputBytes);
            json.WriteNumber("maximumOutputBytes", request.Limits.MaximumOutputBytes);
            json.WriteNumber("maximumArtifacts", request.Limits.MaximumArtifacts);
            json.WriteNumber("maximumTextBytes", request.Limits.MaximumTextBytes);
            json.WriteEndObject();
            json.WriteEndObject();
        }

        if (buffer.Length > PreviewPolicy.MaximumHeaderBytes)
            return null;
        var header = buffer.ToArray();
        var native = artifact.CopyBytes();
        var length = 8L + header.Length + native.Length + request.Primary.ByteCount + 4
            + request.NamedInputs.Sum(input => 4L + input.ByteCount);
        var frame = new byte[length];
        var offset = 0;
        Append(frame, ref offset, header);
        Append(frame, ref offset, native);
        Append(frame, ref offset, request.Primary.Bytes);
        foreach (var input in request.NamedInputs)
            Append(frame, ref offset, input.Bytes);
        return frame;
    }

    private static void Append(byte[] frame, ref int offset, ReadOnlySpan<byte> body)
    {
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(offset), (uint)body.Length);
        body.CopyTo(frame.AsSpan(offset + 4));
        offset += 4 + body.Length;
    }

    private MappingPreviewResult Decode(byte[] output, MappingPreviewRequest request, string requestId)
    {
        Require(output is not null && output.Length >= 4);
        var length = BinaryPrimitives.ReadUInt32BigEndian(output);
        Require(length is > 0 and <= PreviewPolicy.MaximumResponseHeaderBytes && output.Length >= 4L + length);
        var json = StrictUtf8.GetString(output, 4, (int)length);
        var response = JsonSerializer.Deserialize<PreviewHostResponse>(json, JsonOptions) ?? throw new ProtocolException();
        var artifact = request.Artifact;
        var scope = artifact.Scope;
        Require(response.ProtocolVersion == SelectedProtocol && response.RequestId == requestId
            && response.Revision == new HostRevision(scope.OrganizationId.ToString("D"), scope.ProjectId.ToString("D"),
                scope.MappingId.ToString("D"), scope.RevisionId.ToString("D"))
            && response.NativeDigest == artifact.Digest.Value && response.DocumentId == InspectionPolicy.DocumentId);
        Require(response.EngineRevision == FerruleInspectionBackend.EngineRevision && response.Profile == (NativeFormat == NativeDocumentFormat.Xlsx ? PortableProtocol : SelectedProfile));
        Require((response.Result is null) != (response.Failure is null));
        var maximumText = request.Limits.MaximumTextBytes;
        var offset = 4 + (int)length;
        if (response.Failure is { } failure)
        {
            Require(offset == output.Length);
            var status = FailureStatus(failure.Code);
            Text(failure.Message, maximumText);
            if (failure.Subject is not null)
                Text(failure.Subject, maximumText);
            else
                Require(!failure.SubjectTruncated);
            return new MappingPreviewResult(status, Provenance(artifact, true), SelectedProfile, 0, [],
                new MappingPreviewFailure("ferrule." + failure.Code, failure.Message, failure.MessageTruncated, failure.Subject, failure.SubjectTruncated));
        }

        var result = response.Result!;
        Require(result.Artifacts is not null && result.Artifacts.Count is > 0 && result.Artifacts.Count <= request.Limits.MaximumArtifacts);
        Require(result.RecordsWritten <= long.MaxValue);
        var boundaries = result.Artifacts.Select(item => item?.BoundaryId).ToArray();
        Require(ExpectedBoundaries(request.Target, boundaries));
        if (_profile == PreviewExecutionProfile.CsvDocument || NativeFormat is not null)
            Require(boundaries.Length == 1 && boundaries[0] == PrimaryOutput);
        var artifacts = new List<PreviewOutputArtifact>(result.Artifacts.Count);
        long total = 0;
        foreach (var item in result.Artifacts)
        {
            var settings = item.FlexTextSettings is { } flex ? new NativeDocumentSettings(FlexTextDocumentPolicy.SettingsSchema, FlexTextSettingsJson.Read(flex).LayoutJson) :
                item.XlsxSettings is { } sheet ? new NativeDocumentSettings(XlsxDocumentPolicy.SettingsSchema, XlsxSettingsJson.Encode(XlsxSettingsJson.Read(sheet))) :
                item.DocumentSettings is { } saved ? NativeDocumentSettingsJson.Read(saved) : null;
            Require((item.FlexTextSettings is not null) == (NativeFormat == NativeDocumentFormat.FlexText) &&
                (item.XlsxSettings is not null) == (NativeFormat == NativeDocumentFormat.Xlsx) &&
                (item.DocumentSettings is not null) == (NativeFormat == NativeDocumentFormat.Protobuf || NativeFormat == NativeDocumentFormat.Xbrl));
            Require((_profile switch
            {
                _ when NativeFormat is { } format => item.CsvDialect is null && item.XmlMetadata is null && item.MediaType == format.MediaType && settings == NativeDocumentNativeSettings.Read(artifact, format).Output,
                PreviewExecutionProfile.CsvDocument => CsvDocumentPolicy.Admits(item.CsvDialect, item.MediaType),
                PreviewExecutionProfile.XmlDocument => item.CsvDialect is null && item.XmlMetadata is { } xml &&
                    XmlDocumentPolicy.Admits(XmlDocumentMetadataJson.Read(xml, XmlDocumentRole.Output), XmlDocumentRole.Output, item.MediaType),
                PreviewExecutionProfile.Document => item.CsvDialect is null && DocumentPreviewPolicy.AdmitsMediaType(item.MediaType),
                _ => item.CsvDialect is null && item.MediaType == PreviewPolicy.MediaType
            })
                && item.RecordsWritten <= long.MaxValue);
            Text(item.TargetName, maximumText);
            var digest = Sha256Digest.Parse(item.Sha256);
            Require(digest.Value == item.Sha256);
            total += item.ByteLength;
            Require(total <= request.Limits.MaximumOutputBytes && output.Length - offset >= 4);
            var frameLength = BinaryPrimitives.ReadUInt32BigEndian(output.AsSpan(offset));
            Require(frameLength == item.ByteLength && output.Length - offset - 4 >= frameLength);
            var bytes = output.AsSpan(offset + 4, (int)frameLength).ToArray();
            offset += 4 + (int)frameLength;
            if (_profile != PreviewExecutionProfile.XmlDocument && item.XmlMetadata is not null) throw new ProtocolException();
            NativeFormat?.CheckBytes(bytes);
            var verified = new PreviewOutputArtifact(item.BoundaryId, item.TargetName, item.TargetNameTruncated, item.MediaType, (long)item.RecordsWritten, bytes, item.CsvDialect,
                item.XmlMetadata is { } value ? XmlDocumentMetadataJson.Read(value, XmlDocumentRole.Output) : null, settings);
            Require(verified.Digest == digest);
            artifacts.Add(verified);
        }
        Require(offset == output.Length);
        if (_profile == PreviewExecutionProfile.XmlDocument) XmlPreviewAdmission.VerifyOutputs(request, artifacts);
        if (request.Target.Kind == PreviewTargetKind.Named)
            Require(artifacts[0].TargetNameTruncated || artifacts[0].TargetName == request.Target.Name);
        return new MappingPreviewResult(MappingPreviewStatus.Succeeded, Provenance(artifact, true), SelectedProfile,
            (long)result.RecordsWritten, artifacts, null);
    }

    /// <summary>Primary first, then distinct named outputs in declaration order.</summary>
    private static bool ExpectedBoundaries(PreviewTargetSelection target, string?[] boundaries)
    {
        if (boundaries.Any(boundary => boundary is null))
            return false;
        var named = new List<ulong>();
        var primary = 0;
        foreach (var boundary in boundaries)
        {
            if (boundary == PrimaryOutput)
            {
                if (named.Count > 0 || primary++ > 0)
                    return false;
            }
            else if (boundary!.StartsWith(NamedOutputPrefix, StringComparison.Ordinal)
                && ulong.TryParse(boundary.AsSpan(NamedOutputPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                && index.ToString(CultureInfo.InvariantCulture) == boundary[NamedOutputPrefix.Length..]
                && (named.Count == 0 || named[^1] < index))
            {
                named.Add(index);
            }
            else
            {
                return false;
            }
        }
        return target.Kind switch
        {
            PreviewTargetKind.All => primary == 1,
            PreviewTargetKind.Primary => primary == 1 && named.Count == 0,
            _ => primary == 0 && named.Count == 1
        };
    }

    private static void Text(string? text, int maximumBytes) => Require(text is not null && Utf8Within(text, maximumBytes));

    private static MappingPreviewStatus FailureStatus(string code) => code switch
    {
        "invalidRequest" or "unknownTarget" or "unexpectedInput" or "invalidClock" => MappingPreviewStatus.InvalidRequest,
        "invalidMapping" => MappingPreviewStatus.InvalidMapping,
        "nativeDecodeFailed" or "unsupportedNativeVersion" => MappingPreviewStatus.UnsupportedArtifact,
        "unsupportedCapability" => MappingPreviewStatus.UnsupportedCapability,
        "missingInput" => MappingPreviewStatus.MissingInput,
        "invalidInput" => MappingPreviewStatus.InvalidInput,
        "unknownParameter" or "invalidParameter" or "missingParameter" => MappingPreviewStatus.InvalidParameter,
        "executionFailed" => MappingPreviewStatus.ExecutionFailed,
        "inputLimitExceeded" or "resultLimitExceeded" => MappingPreviewStatus.ResourceLimit,
        "invalidFrame" or "invalidHeader" or "unsupportedProtocolVersion" or "invalidContext" or "invalidLimits" or "digestMismatch"
            => MappingPreviewStatus.ProtocolFailure,
        _ => throw new ProtocolException()
    };

    private static MappingPreviewStatus ProcessStatus(WorkerProcessFailure failure) => failure switch
    {
        WorkerProcessFailure.Canceled => MappingPreviewStatus.Canceled,
        WorkerProcessFailure.TimedOut => MappingPreviewStatus.TimedOut,
        WorkerProcessFailure.StartFailed or WorkerProcessFailure.SandboxUnavailable => MappingPreviewStatus.BackendUnavailable,
        WorkerProcessFailure.CleanupFailed => MappingPreviewStatus.IsolationFailure,
        WorkerProcessFailure.OutputLimitExceeded => MappingPreviewStatus.ResourceLimit,
        _ => MappingPreviewStatus.BackendFailure
    };

    private PreviewProvenance Provenance(NativeArtifact artifact, bool confirmed) =>
        new(artifact.Scope, artifact.Digest, "ferrule", FerruleInspectionBackend.EngineRevision, _backendBuild, SelectedAdapter, confirmed);

    private MappingPreviewResult LocalFailure(NativeArtifact artifact, MappingPreviewStatus status, string? code = null, string? message = null, string? subject = null)
    {
        var (defaultCode, defaultMessage) = status switch
        {
            MappingPreviewStatus.ResourceLimit => ("preview.limit", "Preview exceeded the server resource policy."),
            MappingPreviewStatus.Canceled => ("preview.canceled", "Preview was canceled."),
            MappingPreviewStatus.TimedOut => ("preview.timeout", "Preview exceeded its deadline."),
            MappingPreviewStatus.ProtocolFailure => ("preview.protocol", "The preview worker returned an invalid bounded response."),
            MappingPreviewStatus.IsolationFailure => ("preview.cleanup", "Worker cleanup could not be confirmed; further worker admission is blocked."),
            MappingPreviewStatus.BackendUnavailable => ("preview.unavailable", "The configured isolated preview worker is unavailable."),
            _ => ("preview.failure", "The isolated preview worker failed.")
        };
        return new MappingPreviewResult(status, Provenance(artifact, false), SelectedProfile, 0, [],
            new MappingPreviewFailure(code ?? defaultCode, message ?? defaultMessage, false, subject, false));
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            NumberHandling = JsonNumberHandling.Strict,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true,
            RespectNullableAnnotations = true,
            AllowDuplicateProperties = false,
            MaxDepth = 8
        };
        return options;
    }

    private static void Require([DoesNotReturnIf(false)] bool condition)
    {
        if (!condition) throw new ProtocolException();
    }

    private sealed class ProtocolException : Exception { }
}
