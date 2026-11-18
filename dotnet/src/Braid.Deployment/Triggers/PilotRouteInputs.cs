using System.Text.Json;
using System.Text.RegularExpressions;
using Braid.Application;
using Braid.Domain;

namespace Braid.Deployment.Triggers;

/// <summary>Pilot schedule attachment. Persist the exact JSON bytes inside the immutable, size-bounded configuration version, never a mutable path.</summary>
public static partial class PilotRouteInputs
{
    private const string Prefix = "braid.pilot.json-reference.v1:";
    [GeneratedRegex(@"\A[a-zA-Z][a-zA-Z0-9_-]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    public static PreviewParameter Attach(string name, byte[] bytes)
    {
        if (!NamePattern().IsMatch(name) || bytes.Length is 0 or > 3072) throw new ArgumentException("Invalid pilot reference.");
        try { using var document = JsonDocument.Parse(bytes); }
        catch (JsonException) { throw new ArgumentException("Reference must be a JSON document."); }
        return new(Prefix + name, PreviewParameterKind.String, Convert.ToBase64String(bytes));
    }

    internal static MachineInvocationRequest Request(TriggerConfiguration config, ReadOnlySpan<byte> primary)
    {
        List<PreviewInputDocument> references = [];
        List<PreviewParameter> parameters = [];
        List<MachineInputMedia> media = [new(null, config.InputMediaType)];
        foreach (var p in config.Parameters)
        {
            if (config.Kind != TriggerKind.Schedule || !p.Name.StartsWith(Prefix, StringComparison.Ordinal)) { parameters.Add(p); continue; }
            var name = p.Name[Prefix.Length..];
            byte[] bytes;
            try { bytes = Convert.FromBase64String(p.Value); }
            catch (FormatException) { throw new ArgumentException("Invalid pinned reference bytes."); }
            if (Attach(name, bytes) != p) throw new ArgumentException("Invalid pinned reference encoding.");
            references.Add(new(name, bytes)); media.Add(new(name, "application/json"));
        }
        return new(new(null, primary), references, parameters, media);
    }
}
