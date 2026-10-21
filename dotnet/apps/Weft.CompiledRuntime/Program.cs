using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using Weft.Application;
using Weft.Application.Regression;
using Weft.Deployment;

if (!OperatingSystem.IsLinux()) return 1;
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
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
    await Console.OpenStandardError().WriteAsync(Encoding.ASCII.GetBytes("WeftCompiledRuntimeFailed:" + stage + "\n"));
    return 1;
}

internal static class CompiledInvocation
{
    internal sealed record Result(string Status, RegressionOutputSet? Outputs = null);

    internal static Result Run(CompiledRuntimeProtocol.Invocation request)
    {
        if (request.Intent.Profile.Id != BuildWorkerProtocol.Profile || request.Intent.Profile.Runtime != "dotnet" ||
            request.Intent.Profile.RuntimeVersion != BuildWorkerProtocol.RuntimeVersion || request.Intent.Backend.EngineRevision != BuildWorkerProtocol.Engine ||
            request.Intent.Boundaries.Any(value => value.Format != "json")) return new("unsupported");
        var context = new AssemblyLoadContext("weft-compiled", isCollectible: true);
        var stage = "artifact";
        try
        {
            using var bytes = new MemoryStream(request.Artifact.Copy(), writable: false);
            var assembly = context.LoadFromStream(bytes);
            if (assembly.GetName().Name != "Ferrule.Generated") return new("invalidArtifact");
            Type Type(string name) => assembly.GetType(name, throwOnError: true)!;
            var mapping = Type("Ferrule.Generated.GeneratedMapping");
            var namedType = Type("Ferrule.Generated.NamedJsonBytesInput");
            var namedConstructor = namedType.GetConstructor([typeof(string), typeof(byte[])]) ?? throw new MissingMethodException();
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
            var sourceListType = typeof(IReadOnlyList<>).MakeGenericType(namedType);
            var allMethod = mapping.GetMethod("ExecuteJsonBytesOutputsWithSources", BindingFlags.Public | BindingFlags.Static,
                [typeof(byte[]), sourceListType, executionType]) ?? throw new MissingMethodException();
            var targetType = Type("Ferrule.Runtime.FerruleTargetSelection");
            var targetPrimary = targetType.GetMethod("Primary", BindingFlags.Public | BindingFlags.Static, System.Type.EmptyTypes) ?? throw new MissingMethodException();
            var targetNamed = targetType.GetMethod("Named", BindingFlags.Public | BindingFlags.Static, [typeof(string)]) ?? throw new MissingMethodException();
            var selectedMethod = mapping.GetMethod("ExecuteJsonBytesSelectedTargetWithHost", BindingFlags.Public | BindingFlags.Static,
                [typeof(byte[]), targetType, sourceListType, executionType, Type("Ferrule.Runtime.IFerruleDynamicJsonSourceLoader")]) ?? throw new MissingMethodException();
            var named = Array.CreateInstance(namedType, request.Replay.NamedInputs.Count);
            for (var i = 0; i < request.Replay.NamedInputs.Count; i++)
            {
                var item = request.Replay.NamedInputs[i];
                named.SetValue(namedConstructor.Invoke([item.Name, item.CopyBytes()]), i);
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
            if (request.Replay.Target.Kind == PreviewTargetKind.All)
            {
                var result = allMethod.Invoke(null, [request.Replay.Primary.CopyBytes(), named, execution])!;
                Add(null, Property(result, "Primary"));
                foreach (var extra in (IEnumerable)Property(result, "Extras")) Add((string)Property(extra, "Name"), Property(extra, "Document"));
            }
            else
            {
                var target = request.Replay.Target.Kind == PreviewTargetKind.Primary
                    ? targetPrimary.Invoke(null, null)!
                    : targetNamed.Invoke(null, [request.Replay.Target.Name])!;
                var result = selectedMethod.Invoke(null, [request.Replay.Primary.CopyBytes(), target, named, execution, null])!;
                if (request.Replay.Target.Kind == PreviewTargetKind.Primary) Add(null, Property(result, "Document"));
                else
                {
                    var output = Property(result, "Output");
                    Add((string)Property(output, "Name"), Property(output, "Document"));
                }
            }
            return new("succeeded", RegressionOutputSet.Capture(outputs));

            void Add(string? name, object value)
            {
                var body = (byte[])value;
                if (outputs.Count >= request.Replay.Limits.MaximumArtifacts || body.Length < 1 ||
                    body.Length > request.Replay.Limits.MaximumOutputBytes - outputs.Sum(item => item.ByteCount)) throw new OutputLimitException();
                outputs.Add(RegressionOutput.Capture(name is null ? RegressionOutputKey.Primary : RegressionOutputKey.Named(name), body));
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            var cause = error is TargetInvocationException { InnerException: { } inner } ? inner : error;
            if (cause is OutputLimitException) return new("resourceLimit");
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
}
