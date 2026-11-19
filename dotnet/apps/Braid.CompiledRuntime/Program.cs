using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using Braid.Application;
using Braid.Application.Regression;
using Braid.Deployment;

if (!OperatingSystem.IsLinux()) return 1;
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
if (args is [XmlComparisonProtocol.ModeArgument]) return await XmlComparisonInvocation.RunAsync(timeout.Token);
if (args.Length != 0) return 1;
var stage = "request";
try
{
    using var input = new MemoryStream();
    var buffer = new byte[65536]; int count;
    var stream = Console.OpenStandardInput();
    while ((count = await stream.ReadAsync(buffer, timeout.Token)) != 0)
    {
        if (count > CompiledRuntimeProtocol.MaximumRequestBytes - input.Length) return 1;
        input.Write(buffer, 0, count);
    }
    stage = "decode";
    var invocation = CompiledRuntimeProtocol.DecodeRequest(input.ToArray());
    stage = "invocation";
    // Suppress generated-library diagnostics; only this host writes the closed framed result.
    Console.SetOut(TextWriter.Null); Console.SetError(TextWriter.Null);
    var result = CompiledInvocation.Run(invocation);
    stage = "response";
    await Console.OpenStandardOutput().WriteAsync(CompiledRuntimeProtocol.EncodeResponse(invocation, result.Status, result.Outputs), timeout.Token);
    return 0;
}
catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
{
    // Fixed stage only: no exception messages, type names, input or generated-code text.
    await Console.OpenStandardError().WriteAsync(Encoding.ASCII.GetBytes("BraidCompiledRuntimeFailed:" + stage + "\n"));
    return 1;
}

internal static class CompiledInvocation
{
    internal sealed record Result(string Status, RegressionOutputSet? Outputs = null);

    internal static Result Run(CompiledRuntimeProtocol.Invocation request)
    {
        // The pinned emitter retains its ordinary JSON API for all-JSON plans;
        // the document protocol still binds their explicit media types.
        var documents = BuildWorkerProtocol.IsDocumentProfile(request.Intent.Profile.Id) && request.Intent.Boundaries.Any(value => value.Format != "json");
        var csv = request.Intent.Profile.Id == BuildWorkerProtocol.CsvProfile;
        var xml = request.Intent.Profile.Id == BuildWorkerProtocol.XmlProfile;
        var native = BuildWorkerProtocol.NativeFormat(request.Intent.Profile.Id);
        if (native is not null && (request.Replay.NamedInputs.Count != 0 || request.Replay.Target.Kind == PreviewTargetKind.Named)) return new("unsupported");
        if (request.Intent.Profile.Id != BuildWorkerProtocol.Profile && !BuildWorkerProtocol.IsDocumentProfile(request.Intent.Profile.Id) || request.Intent.Profile.Runtime != "dotnet" ||
            request.Intent.Profile.RuntimeVersion != BuildWorkerProtocol.RuntimeVersion || request.Intent.Backend.EngineRevision != BuildWorkerProtocol.Engine ||
            request.Intent.Boundaries.Any(value => !BuildWorkerProtocol.AdmitsBoundary(request.Intent.Profile.Id, value.Format, value.CsvDialect))) return new("unsupported");
        if (csv && (!BuildWorkerProtocol.AdmitsCsvPlan(request.Intent.Boundaries) || request.Replay.NamedInputs.Count != 0 ||
            request.Replay.Target.Kind == PreviewTargetKind.Named)) return new("unsupported");
        if (request.Intent.Profile.Id == BuildWorkerProtocol.X12InterchangesProfile && !request.Intent.Boundaries.Any(value => value.Format == "x12")) return new("unsupported");
        if (documents)
        {
            foreach (var document in request.Replay.NamedInputs.Prepend(request.Replay.Primary))
            {
                var boundary = request.Intent.Boundaries.Single(value => value.Name == document.Name &&
                    value.Kind == (document.Name is null ? BuildBoundaryKind.PrimaryInput : BuildBoundaryKind.NamedInput));
                if (boundary.Format is "x12" or "edifact")
                {
                    var validation = EdiEnvelopeValidation.ValidateExecution(document.Bytes, BuildWorkerProtocol.MediaType(boundary.Format));
                    if (validation == EdiExecutionValidation.Invalid) return new("invalidInput");
                }
            }
        }
        var context = new AssemblyLoadContext("braid-compiled", isCollectible: true);
        var stage = "artifact";
        try
        {
            using var bytes = new MemoryStream(request.Artifact.Copy(), writable: false);
            var assembly = context.LoadFromStream(bytes);
            if (assembly.GetName().Name != "Ferrule.Generated") return new("invalidArtifact");
            Type Type(string name) => assembly.GetType(name, throwOnError: true)!;
            var mapping = Type("Ferrule.Generated.GeneratedMapping");
            var namedType = csv || xml || native is not null ? null : Type(documents ? "Ferrule.Generated.NamedDocumentBytesInput" : "Ferrule.Generated.NamedJsonBytesInput");
            var namedConstructor = namedType?.GetConstructor([typeof(string), typeof(byte[])]);
            if (!csv && !xml && native is null && namedConstructor is null) throw new MissingMethodException();
            var valueType = Type("Ferrule.Runtime.FerruleValue");
            var pairType = typeof(KeyValuePair<,>).MakeGenericType(typeof(string), valueType);
            var pairConstructor = pairType.GetConstructor([typeof(string), valueType]) ?? throw new MissingMethodException();
            var parameterType = Type("Ferrule.Runtime.FerruleRuntimeParameters");
            var parameterConstructor = parameterType.GetConstructor([typeof(IEnumerable<>).MakeGenericType(pairType)]) ?? throw new MissingMethodException();
            var executionType = Type("Ferrule.Runtime.FerruleExecutionContext");
            var executionConstructor = executionType.GetConstructor([typeof(string), typeof(string), typeof(string), parameterType]) ?? throw new MissingMethodException();
            var factories = new Dictionary<string, MethodInfo>(StringComparer.Ordinal)
            {
                ["FromString"] = valueType.GetMethod("FromString", BindingFlags.Public | BindingFlags.Static, [typeof(string)]) ?? throw new MissingMethodException(),
                ["FromBoolean"] = valueType.GetMethod("FromBoolean", BindingFlags.Public | BindingFlags.Static, [typeof(bool)]) ?? throw new MissingMethodException(),
                ["FromInt64"] = valueType.GetMethod("FromInt64", BindingFlags.Public | BindingFlags.Static, [typeof(long)]) ?? throw new MissingMethodException(),
                ["FromDouble"] = valueType.GetMethod("FromDouble", BindingFlags.Public | BindingFlags.Static, [typeof(double)]) ?? throw new MissingMethodException(),
            };
            MethodInfo? allMethod = null, selectedMethod = null, targetPrimary = null, targetNamed = null, csvMethod = null;
            if (csv)
                csvMethod = mapping.GetMethod("ExecuteCsvDocumentBytes", BindingFlags.Public | BindingFlags.Static, [typeof(byte[]), executionType]) ?? throw new MissingMethodException();
            else if (!xml && native is null)
            {
                var sourceListType = typeof(IReadOnlyList<>).MakeGenericType(namedType!);
                allMethod = mapping.GetMethod(documents ? "ExecuteDocumentBytesOutputsWithHost" : "ExecuteJsonBytesOutputsWithSources", BindingFlags.Public | BindingFlags.Static,
                    [typeof(byte[]), sourceListType, executionType]) ?? throw new MissingMethodException();
                var targetType = Type("Ferrule.Runtime.FerruleTargetSelection");
                targetPrimary = targetType.GetMethod("Primary", BindingFlags.Public | BindingFlags.Static, System.Type.EmptyTypes) ?? throw new MissingMethodException();
                targetNamed = targetType.GetMethod("Named", BindingFlags.Public | BindingFlags.Static, [typeof(string)]) ?? throw new MissingMethodException();
                selectedMethod = mapping.GetMethod(documents ? "ExecuteDocumentBytesSelectedTargetWithHost" : "ExecuteJsonBytesSelectedTargetWithHost", BindingFlags.Public | BindingFlags.Static,
                    documents ? [typeof(byte[]), targetType, sourceListType, executionType] :
                        [typeof(byte[]), targetType, sourceListType, executionType, Type("Ferrule.Runtime.IFerruleDynamicJsonSourceLoader")]) ?? throw new MissingMethodException();
            }
            var named = namedType is null ? null : Array.CreateInstance(namedType, request.Replay.NamedInputs.Count);
            for (var i = 0; !xml && i < request.Replay.NamedInputs.Count; i++)
            {
                var item = request.Replay.NamedInputs[i];
                named!.SetValue(namedConstructor!.Invoke([item.Name, item.CopyBytes()]), i);
            }
            stage = "parameter";
            var pairs = Array.CreateInstance(pairType, request.Replay.Parameters.Count);
            for (var i = 0; i < request.Replay.Parameters.Count; i++)
            {
                var item = request.Replay.Parameters[i];
                var (method, argument) = Value(item);
                pairs.SetValue(pairConstructor.Invoke([item.Name, factories[method].Invoke(null, [argument])!]), i);
            }
            var parameters = parameterConstructor.Invoke([pairs]);
            var execution = executionConstructor.Invoke(["entry.native.json", "entry.native.json", request.Replay.CurrentDateTime, parameters]);
            stage = "execution";
            var outputs = new List<RegressionOutput>();
            if (native is not null)
            {
                var invoke = Type("Braid.Generated.NativeDocument").GetMethod("Execute", BindingFlags.Public | BindingFlags.Static, [typeof(byte[]), typeof(bool), executionType]) ?? throw new MissingMethodException();
                var body = (byte[])invoke.Invoke(null, [request.Replay.Primary.CopyBytes(), request.Replay.Target.Kind == PreviewTargetKind.All, execution])!;
                if (body.Length > request.Replay.Limits.MaximumOutputBytes || body.Length > NativeDocumentFormat.MaximumBytes) return new("resourceLimit");
                outputs.Add(RegressionOutput.Capture(RegressionOutputKey.Primary, body, mediaType: native.MediaType,
                    documentSettings: request.Intent.Boundaries.Single(b => b.Kind == BuildBoundaryKind.PrimaryOutput).DocumentSettings));
            }
            else if (xml)
            {
                var declared = request.Intent.Boundaries.Where(b => b.Kind == BuildBoundaryKind.NamedOutput).Select(b => b.Name!).ToArray();
                var result = CompiledXmlBytes.Invoke(assembly, execution, request.Replay.Target.Kind == PreviewTargetKind.All, request.Replay.Target.Name,
                    new(null, request.Replay.Primary.CopyBytes()), request.Replay.NamedInputs.Select(i => new CompiledXmlBytes.Document(i.Name, i.CopyBytes())).ToArray(),
                    declared, request.Replay.Limits.MaximumInputBytes, request.Replay.Limits.MaximumOutputBytes, default);
                if (result.Length > request.Replay.Limits.MaximumArtifacts) return new("resourceLimit");
                foreach (var item in result)
                {
                    var boundary = request.Intent.Boundaries.Single(b => b.Kind == (item.Name is null ? BuildBoundaryKind.PrimaryOutput : BuildBoundaryKind.NamedOutput) && b.Name == item.Name);
                    outputs.Add(RegressionOutput.Capture(item.Name is null ? RegressionOutputKey.Primary : RegressionOutputKey.Named(item.Name), item.Bytes,
                        mediaType: XmlDocumentPolicy.MediaType, xmlMetadata: boundary.XmlMetadata));
                }
            }
            else if (csv)
                Add(null, csvMethod!.Invoke(null, [request.Replay.Primary.CopyBytes(), execution])!, null);
            else if (request.Replay.Target.Kind == PreviewTargetKind.All)
            {
                var result = allMethod!.Invoke(null, [request.Replay.Primary.CopyBytes(), named, execution])!;
                Add(null, Property(result, "Primary"), documents ? Property(result, "PrimaryFormat") : null);
                foreach (var extra in (IEnumerable)Property(result, "Extras"))
                    Add((string)Property(extra, "Name"), Property(extra, "Document"), documents ? Property(extra, "Format") : null);
            }
            else
            {
                var target = request.Replay.Target.Kind == PreviewTargetKind.Primary
                    ? targetPrimary!.Invoke(null, null)!
                    : targetNamed!.Invoke(null, [request.Replay.Target.Name])!;
                var result = selectedMethod!.Invoke(null, documents ? [request.Replay.Primary.CopyBytes(), target, named, execution] :
                    [request.Replay.Primary.CopyBytes(), target, named, execution, null])!;
                if (request.Replay.Target.Kind == PreviewTargetKind.Primary)
                    Add(null, Property(result, "Document"), documents ? Property(result, "Format") : null);
                else
                {
                    var output = Property(result, "Output");
                    Add((string)Property(output, "Name"), Property(output, "Document"), documents ? Property(output, "Format") : null);
                }
            }
            return new("succeeded", RegressionOutputSet.Capture(outputs));

            void Add(string? name, object value, object? format)
            {
                var body = (byte[])value;
                if (outputs.Count >= request.Replay.Limits.MaximumArtifacts || body.Length < 1 && !csv ||
                    body.Length > request.Replay.Limits.MaximumOutputBytes - outputs.Sum(item => item.ByteCount)) throw new OutputLimitException();
                var boundary = request.Intent.Boundaries.SingleOrDefault(value => value.Name == name &&
                    value.Kind == (name is null ? BuildBoundaryKind.PrimaryOutput : BuildBoundaryKind.NamedOutput));
                if (boundary is null) throw new InvalidOperationException();
                var media = csv ? BuildWorkerProtocol.MediaType(boundary.Format) : documents ? format?.ToString() switch
                {
                    "Json" => "application/json",
                    "X12" => "application/edi-x12",
                    "Edifact" => "application/edifact",
                    _ => throw new InvalidOperationException()
                } : PreviewPolicy.MediaType;
                if (media != BuildWorkerProtocol.MediaType(boundary.Format)) throw new InvalidOperationException();
                if (csv)
                {
                    var bounded = CsvDocumentBounds.Check(body, boundary.CsvDialect!, media);
                    if (bounded == CsvDocumentBoundResult.Limit) throw new OutputLimitException();
                    if (bounded != CsvDocumentBoundResult.Accepted) throw new InvalidOperationException();
                }
                else if (BuildWorkerProtocol.IsDocumentProfile(request.Intent.Profile.Id))
                {
                    // Syntax is bound into the approved generated assembly, never inferred from a filename.
                    char? segment = null;
                    if (media == "application/edi-x12")
                    {
                        var syntax = assembly.GetType("Braid.Generated.DocumentSyntax")?.GetMethod("OutputSegment");
                        if (syntax is not null) segment = (char)(int)syntax.Invoke(null, [name])!;
                        else if (DocumentFormatLimits.TryReadX12Segment(body, out var declared)) segment = declared;
                        else throw new UnsupportedOutputSyntaxException();
                    }
                    if (!DocumentFormatLimits.WithinLimits(body, media, segment)) throw new OutputLimitException();
                }
                outputs.Add(RegressionOutput.Capture(name is null ? RegressionOutputKey.Primary : RegressionOutputKey.Named(name), body, mediaType: media, csvDialect: boundary.CsvDialect));
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            var cause = error is TargetInvocationException { InnerException: { } inner } ? inner : error;
            // This refusal belongs specifically to the pinned reader's canonical-package check.
            // Other codec/mapping errors may describe output, not malformed input.
            if (native == NativeDocumentFormat.Xlsx && cause is InvalidDataException { Message: "Unsupported flat workbook package" }) return new("invalidInput");
            if (cause is OutputLimitException) return new("resourceLimit");
            if (xml && cause is CompiledXmlBytes.InputFailure failure) return new(failure.ResourceLimit ? "resourceLimit" : "invalidInput");
            if (xml && cause is XmlDocumentException document) return new(document.IsResourceLimit ? "resourceLimit" : "invalidInput");
            if (cause is UnsupportedOutputSyntaxException) return new("unsupported");
            if (csv && cause.GetType().FullName == "Ferrule.Runtime.FerruleCsvInputException")
            {
                var code = cause.GetType().GetProperty("Error")?.GetValue(cause)?.ToString();
                return new(code is "InputTooLarge" or "ValueTooLarge" or "TooManyRecords" or "TooManyColumns" or "NodeLimit" or "DecodedInputTooLarge"
                    ? "resourceLimit" : "invalidInput");
            }
            if (csv && cause.GetType().FullName == "Ferrule.Runtime.FerruleCsvException" &&
                cause.GetType().GetProperty("Error")?.GetValue(cause)?.ToString() == "OutputTooLarge") return new("resourceLimit");
            if (stage == "artifact") return new("invalidArtifact");
            if (stage == "parameter") return new("invalidParameter");
            // The generated JSON wrapper can report both input and output boundary failures.
            // Do not relabel an ambiguous runtime failure as an input-validation observation.
            return new("executionFailed");
        }
        finally { context.Unload(); }
    }

    private static object Property(object value, string name) => value.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)!.GetValue(value)!;
    private static (string Method, object Value) Value(PreviewParameter parameter) => parameter.Kind switch
    {
        PreviewParameterKind.String => ("FromString", parameter.Value),
        PreviewParameterKind.Boolean when parameter.Value is "true" or "false" => ("FromBoolean", parameter.Value == "true"),
        PreviewParameterKind.Integer when long.TryParse(parameter.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer) &&
            integer.ToString(CultureInfo.InvariantCulture) == parameter.Value => ("FromInt64", integer),
        PreviewParameterKind.Float => ("FromDouble", Float(parameter.Value)),
        _ => throw new FormatException(),
    };
    private static double Float(string text)
    {
        if (text.Length == 0 || text.Trim() != text) throw new FormatException();
        using var parsed = JsonDocument.Parse(text);
        if (parsed.RootElement.ValueKind != JsonValueKind.Number || !parsed.RootElement.TryGetDouble(out var value) || !double.IsFinite(value)) throw new FormatException();
        return value;
    }
    private sealed class OutputLimitException : Exception { }
    private sealed class UnsupportedOutputSyntaxException : Exception { }
}
