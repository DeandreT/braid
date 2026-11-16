using System.Text;
using System.Text.Json;

namespace Braid.Domain;

public enum CreationScalarType { Text = 1, Number = 2, Boolean = 3 }
public sealed record CreationField(string Name, CreationScalarType Type, bool Required);
public sealed record CreationBinding(string Source, string Target);

/// <summary>Closed versioned authoring language. V1 retains its original flat JSON canonical encoding.</summary>
public sealed partial class MappingCreationPlan
{
    public const int MaximumBytes = 65_536;
    private readonly byte[] bytes;
    private readonly byte[] compilerBytes;

    public MappingCreationPlan(IEnumerable<CreationField> source, IEnumerable<CreationField> target, IEnumerable<CreationBinding> bindings)
    {
        Source = Fields(source); Target = Fields(target);
        var selected = bindings.Take(257).ToArray();
        if (selected.Length is < 1 or > 256 || selected.Any(b => b is null ||
            !Source.Any(f => f.Name == b.Source) || !Target.Any(f => f.Name == b.Target)) ||
            selected.Select(b => b.Target).Distinct(StringComparer.Ordinal).Count() != selected.Length ||
            Target.Any(f => f.Required && !selected.Any(b => b.Target == f.Name)) ||
            selected.Any(b => Source.Single(f => f.Name == b.Source).Type != Target.Single(f => f.Name == b.Target).Type))
            throw new ArgumentException("Bindings must uniquely cover required targets with compatible declared fields.");
        Bindings = Array.AsReadOnly(selected);
        bytes = JsonSerializer.SerializeToUtf8Bytes(new { schema = "braid.creation-plan.v1", format = "json-record", source = Source, target = Target, bindings = Bindings });
        if (bytes.Length > MaximumBytes) throw new ArgumentException("Authoring plan exceeds its byte budget.");
        Digest = Sha256Digest.FromBytes(bytes);
        // Deterministic supported subset of the actual closed braid.authoring.v1 compiler grammar.
        object Boundary(string name, IReadOnlyList<CreationField> fields) => new
        {
            schema = new
            {
                name,
                shape = new
                {
                    kind = "group",
                    children = fields.Select(f => new
                    {
                        name = f.Name,
                        shape = new
                        {
                            kind = "scalar",
                            scalar = f.Type switch
                            {
                                CreationScalarType.Text => "string",
                                CreationScalarType.Number => "float",
                                CreationScalarType.Boolean => "bool",
                                _ => throw new ArgumentException("Unsupported scalar.")
                            }
                        }
                    })
                },
                required = fields.Where(f => f.Required).Select(f => f.Name)
            },
            options = new { kind = "json" }
        };
        compilerBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "braid.authoring.v1",
            source = Boundary("Input", Source),
            target = Boundary("Output", Target),
            edit = new
            {
                format = "ferrule.edit.v1",
                operations = Bindings.Select((b, index) => new
                {
                    op = "bind",
                    binding = new { scope = new { target = new { kind = "primary" }, children = Array.Empty<string>() }, field = b.Target },
                    // Each bind creates a node, even when two bindings read the same source field.
                    expression = new { kind = "source", id = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), path = new[] { b.Source } }
                })
            }
        });
        if (compilerBytes.Length > MaximumBytes) throw new ArgumentException("Compiled authoring plan exceeds its byte budget.");
        CompilerDigest = Sha256Digest.FromBytes(compilerBytes);
    }

    public IReadOnlyList<CreationField> Source { get; }
    public IReadOnlyList<CreationField> Target { get; }
    public IReadOnlyList<CreationBinding> Bindings { get; }
    public Sha256Digest Digest { get; }
    public Sha256Digest CompilerDigest { get; }
    public byte[] CopyBytes() => (byte[])bytes.Clone();
    public byte[] CopyCompilerPlanBytes() => (byte[])compilerBytes.Clone();

    /// <summary>Strict model-output admission. Unknown/duplicate fields, wrong versions and oversized documents fail closed.</summary>
    public static MappingCreationPlan Parse(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length is < 1 or > MaximumBytes) throw new ArgumentException("Authoring plan exceeds its byte budget.");
        using var document = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions { MaxDepth = 64 });
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("schema", out var version) &&
            version.ValueKind == JsonValueKind.String && version.GetString() == "braid.creation-plan.v2")
            return new MappingCreationPlan(CreationPlanV2Codec.Parse(root));
        Properties(root, "schema", "format", "source", "target", "bindings");
        if (root.GetProperty("schema").GetString() != "braid.creation-plan.v1" || root.GetProperty("format").GetString() != "json-record")
            throw new ArgumentException("Unsupported authoring schema.");
        CreationField[] ReadFields(string name) => BoundedArray(root.GetProperty(name)).Select(item =>
        {
            Properties(item, "Name", "Type", "Required");
            return new CreationField(item.GetProperty("Name").GetString()!, (CreationScalarType)item.GetProperty("Type").GetInt32(), item.GetProperty("Required").GetBoolean());
        }).ToArray();
        var bindings = BoundedArray(root.GetProperty("bindings")).Select(item =>
        {
            Properties(item, "Source", "Target");
            return new CreationBinding(item.GetProperty("Source").GetString()!, item.GetProperty("Target").GetString()!);
        }).ToArray();
        return new(ReadFields("source"), ReadFields("target"), bindings);
    }

    private static JsonElement.ArrayEnumerator BoundedArray(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() is < 1 or > 256)
            throw new ArgumentException("Authoring array exceeds its schema limit.");
        return element.EnumerateArray();
    }

    private static void Properties(JsonElement element, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new ArgumentException("Expected an authoring object.");
        var names = element.EnumerateObject().Select(p => p.Name).ToArray();
        if (names.Length != expected.Length || names.Distinct(StringComparer.Ordinal).Count() != names.Length || names.Except(expected, StringComparer.Ordinal).Any())
            throw new ArgumentException("Unknown, duplicate or missing authoring property.");
    }

    private static IReadOnlyList<CreationField> Fields(IEnumerable<CreationField> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var items = fields.Take(257).ToArray();
        if (items.Length is < 1 or > 256 || items.Any(f => f is null || !Enum.IsDefined(f.Type) ||
            string.IsNullOrEmpty(f.Name) || f.Name.Length > 128 || f.Name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_'))) ||
            items.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count() != items.Length)
            throw new ArgumentException("Fields require unique bounded identifiers and supported scalar types.");
        return Array.AsReadOnly(items);
    }
}

public sealed record CreationEvidenceReference(SpecSourceIdentity Source, int ChunkOrdinal);
public sealed record CreationAssumption(string Id, string Text, bool Critical);
public sealed record CreationQuestion(string Text, bool Critical);

/// <summary>Immutable bounded review facts. Evidence is attribution, never instructions or authority.</summary>
public sealed class CreationGenerationNotes
{
    public CreationGenerationNotes(IEnumerable<CreationEvidenceReference> evidence, IEnumerable<CreationAssumption> assumptions, IEnumerable<CreationQuestion> questions)
    {
        Evidence = Array.AsReadOnly(evidence.Take(257).ToArray());
        Assumptions = Array.AsReadOnly(assumptions.Take(65).ToArray());
        Questions = Array.AsReadOnly(questions.Take(65).ToArray());
        if (Evidence.Count is < 1 or > 256 || Assumptions.Count > 64 || Questions.Count > 64 ||
            Evidence.Any(e => e is null || e.Source is null || e.ChunkOrdinal < 0) || Questions.Any(q => q is null) ||
            Assumptions.Any(a => a is null || string.IsNullOrEmpty(a.Id) || a.Id.Length > 64 || a.Id.Any(c => !char.IsAsciiLetterOrDigit(c))) ||
            Assumptions.Select(a => a.Id).Distinct(StringComparer.Ordinal).Count() != Assumptions.Count)
            throw new ArgumentException("Generation notes exceed their schema limits.");
        foreach (var text in Assumptions.Select(a => a.Text).Concat(Questions.Select(q => q.Text)))
            if (string.IsNullOrWhiteSpace(text) || Encoding.UTF8.GetByteCount(text) > 2048 || text.Contains('\0'))
                throw new ArgumentException("Review text must be bounded and nonempty.");
        Digest = Sha256Digest.FromBytes(JsonSerializer.SerializeToUtf8Bytes(new { schema = "braid.creation-notes.v1", Evidence, Assumptions, Questions }));
    }

    public IReadOnlyList<CreationEvidenceReference> Evidence { get; }
    public IReadOnlyList<CreationAssumption> Assumptions { get; }
    public IReadOnlyList<CreationQuestion> Questions { get; }
    public Sha256Digest Digest { get; }
    public bool HasCriticalQuestions => Questions.Any(q => q.Critical);
}
