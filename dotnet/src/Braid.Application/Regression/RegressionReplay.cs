using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Braid.Application.Regression;

/// <summary>Exact inputs and fixed evaluation clock; never used as an authorization, lease or retention clock.</summary>
public sealed class RegressionReplay
{
    public RegressionReplay(PreviewTargetSelection target, string currentDateTime, PreviewLimits limits,
        PreviewInputDocument primary, IReadOnlyList<PreviewInputDocument> namedInputs,
        IReadOnlyList<PreviewParameter> parameters, CancellationToken cancellationToken = default,
        PreviewExecutionProfile? profile = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(namedInputs);
        ArgumentNullException.ThrowIfNull(parameters);
        var selectedProfile = profile ?? primary.DocumentSettings?.Format.Execution ?? (primary.XmlMetadata is not null ? PreviewExecutionProfile.XmlDocument :
            primary.CsvDialect is null ? PreviewExecutionProfile.LegacyJson : PreviewExecutionProfile.CsvDocument);
        var native = NativeDocumentFormat.FromExecution(selectedProfile);
        if (primary.DocumentSettings?.Format != native || namedInputs.Any(i => i?.DocumentSettings is not null) || native is not null && (namedInputs.Count != 0 || target.Kind == PreviewTargetKind.Named)) throw new RegressionCaseException(RegressionCaseFailure.InvalidInput);
        native?.CheckBytes(primary.Bytes);
        // XML inputs are all explicit and role-valid; no other profile may carry XML metadata.
        if (namedInputs.Prepend(primary).Any(input => input is not null && (input.XmlMetadata is null) == (selectedProfile == PreviewExecutionProfile.XmlDocument)))
            throw new RegressionCaseException(RegressionCaseFailure.InvalidInput);
        if (!Enum.IsDefined(selectedProfile) || primary.Name is not null || !NativeDocumentPolicy.AllowsEmpty(selectedProfile) && primary.ByteCount == 0 ||
            !Enum.IsDefined(target.Kind)) throw new RegressionCaseException(RegressionCaseFailure.InvalidInput);
        if (selectedProfile == PreviewExecutionProfile.CsvDocument && (namedInputs.Count != 0 || target.Kind == PreviewTargetKind.Named))
            throw new RegressionCaseException(RegressionCaseFailure.InvalidInput);
        if (selectedProfile != PreviewExecutionProfile.CsvDocument && (primary.CsvDialect is not null || namedInputs.Any(input => input?.CsvDialect is not null)))
            throw new RegressionCaseException(RegressionCaseFailure.InvalidInput);
        if (target.Kind == PreviewTargetKind.Named) RegressionCaseValues.Name(target.Name!);
        if (limits.MaximumInputBytes is < 1 or > PreviewPolicy.MaximumInputBytes ||
            limits.MaximumOutputBytes is < 1 or > PreviewPolicy.MaximumOutputBytes ||
            limits.MaximumArtifacts is < 1 or > PreviewPolicy.MaximumArtifacts ||
            limits.MaximumTextBytes is < PreviewPolicy.MinimumTextBytes or > PreviewPolicy.MaximumTextBytes)
            throw new RegressionCaseException(RegressionCaseFailure.InvalidInput);
        RegressionCaseValues.Clock(currentDateTime);
        var inputCount = namedInputs.Count;
        var parameterCount = parameters.Count;
        if (inputCount is < 0 or >= PreviewPolicy.MaximumInputDocuments ||
            parameterCount is < 0 or > PreviewPolicy.MaximumParameters)
            throw new RegressionCaseException(RegressionCaseFailure.ResourceLimit);
        var ownedInputs = new PreviewInputDocument[inputCount];
        var names = new HashSet<string>(StringComparer.Ordinal);
        var inputBytes = primary.ByteCount;
        if (inputBytes > limits.MaximumInputBytes) throw new RegressionCaseException(RegressionCaseFailure.ResourceLimit);
        for (var index = 0; index < inputCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var input = namedInputs[index];
            if (input is null || input.Name is null || input.ByteCount == 0)
                throw new RegressionCaseException(RegressionCaseFailure.InvalidInput);
            RegressionCaseValues.Name(input.Name);
            if (!names.Add(input.Name)) throw new RegressionCaseException(RegressionCaseFailure.InvalidInput);
            if (input.ByteCount > limits.MaximumInputBytes - inputBytes)
                throw new RegressionCaseException(RegressionCaseFailure.ResourceLimit);
            inputBytes += input.ByteCount;
            ownedInputs[index] = input;
        }
        Array.Sort(ownedInputs, (left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
        var ownedParameters = new PreviewParameter[parameterCount];
        names.Clear();
        for (var index = 0; index < parameterCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parameter = parameters[index];
            if (parameter is null || !Enum.IsDefined(parameter.Kind))
                throw new RegressionCaseException(RegressionCaseFailure.InvalidInput);
            RegressionCaseValues.Name(parameter.Name);
            if (!names.Add(parameter.Name)) throw new RegressionCaseException(RegressionCaseFailure.InvalidInput);
            // Preserve typed lexical values, even ones the runtime will refuse. No coercion or normalization.
            RegressionCaseValues.Text(parameter.Value, PreviewPolicy.MaximumParameterTextBytes, allowEmpty: true, allowControls: true);
            ownedParameters[index] = parameter;
        }
        Array.Sort(ownedParameters, (left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
        Target = target;
        CurrentDateTime = currentDateTime;
        Limits = limits;
        Primary = primary;
        NamedInputs = Array.AsReadOnly(ownedInputs);
        Parameters = Array.AsReadOnly(ownedParameters);
        InputByteCount = inputBytes;
        Profile = selectedProfile;
        cancellationToken.ThrowIfCancellationRequested();
    }

    public PreviewTargetSelection Target { get; }
    public string CurrentDateTime { get; }
    public PreviewLimits Limits { get; }
    public PreviewInputDocument Primary { get; }
    public IReadOnlyList<PreviewInputDocument> NamedInputs { get; }
    public IReadOnlyList<PreviewParameter> Parameters { get; }
    public int InputByteCount { get; }
    public PreviewExecutionProfile Profile { get; }
    public override string ToString() => nameof(RegressionReplay);
}

internal static partial class RegressionCaseValues
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static string Text(string value, int maximumBytes, bool allowEmpty = false, bool allowControls = false)
    {
        if (value is null || value.Length > maximumBytes || !allowEmpty && string.IsNullOrWhiteSpace(value) ||
            !allowControls && value.Any(char.IsControl)) throw new RegressionCaseException(RegressionCaseFailure.InvalidInput);
        try
        {
            if (Utf8.GetByteCount(value) > maximumBytes) throw new RegressionCaseException(RegressionCaseFailure.InvalidInput);
        }
        catch (EncoderFallbackException) { throw new RegressionCaseException(RegressionCaseFailure.InvalidInput); }
        return value;
    }

    internal static string Name(string value) => Text(value, PreviewPolicy.MaximumNameBytes);

    internal static void Clock(string value)
    {
        Text(value, PreviewPolicy.MaximumClockBytes);
        var match = ClockPattern().Match(value);
        // Validate calendar/offset without rewriting the saved fractional precision or offset spelling.
        var zone = match.Groups[2].Value == "Z" ? "+00:00" : match.Groups[2].Value;
        if (!match.Success || !DateTimeOffset.TryParseExact(match.Groups[1].Value + zone,
            "yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new RegressionCaseException(RegressionCaseFailure.InvalidInput);
    }

    [GeneratedRegex(@"\A([0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2})(?:\.[0-9]{1,9})?(Z|[+-][0-9]{2}:[0-9]{2})\z", RegexOptions.CultureInvariant)]
    private static partial Regex ClockPattern();
}
