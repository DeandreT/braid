using System.Collections.ObjectModel;
using System.Text;

namespace Braid.Domain;

/// <summary>The four scalar kinds supported by the pinned authoring compiler.</summary>
public enum CreationValueType { String = 1, Integer = 2, Float = 3, Boolean = 4 }

// Closed unions intentionally have no native JSON, expression text, path-to-file or extension-data slot.
public abstract record CreationFormatOptions
{
    private protected CreationFormatOptions() { }
}
public sealed record CreationJsonOptions : CreationFormatOptions;
public sealed record CreationXmlOptions : CreationFormatOptions;
public sealed record CreationX12Options(string InterchangeVersion) : CreationFormatOptions;
public sealed record CreationEdifactOptions : CreationFormatOptions;
public sealed record CreationCsvOptions(bool Header, bool Quote, bool Bom, bool PreserveEmptyStrings) : CreationFormatOptions;
public sealed record CreationTsvOptions(bool Header, bool Quote, bool Bom, bool PreserveEmptyStrings) : CreationFormatOptions;

/// <summary>Empty namespace is explicitly unqualified; absent metadata inherits. Only #text uses a null namespace.</summary>
public sealed record CreationXmlMetadata(string? Namespace, bool Attribute = false, bool Text = false,
    bool Optional = false, bool Nillable = false);

/// <summary>Immutable ordered schema. A null scalar type denotes a closed group.</summary>
public sealed class CreationSchemaNode
{
    public CreationSchemaNode(string name, CreationValueType? scalarType, IEnumerable<CreationSchemaNode>? children = null,
        bool repeating = false, bool nullable = false, IEnumerable<string>? required = null, CreationXmlMetadata? xml = null)
    {
        Name = name; ScalarType = scalarType; Repeating = repeating; Nullable = nullable; Xml = xml;
        Children = CreationPlanBounds.Copy(children ?? [], CreationPlanV2.MaximumSchemaNodes);
        Required = CreationPlanBounds.Copy(required ?? [], CreationPlanV2.MaximumSchemaNodes);
    }
    public string Name { get; }
    public CreationValueType? ScalarType { get; }
    public IReadOnlyList<CreationSchemaNode> Children { get; }
    public bool Repeating { get; }
    public bool Nullable { get; }
    public IReadOnlyList<string> Required { get; }
    public CreationXmlMetadata? Xml { get; }
}

public sealed record CreationBoundary(CreationSchemaNode Schema, CreationFormatOptions Options);

public abstract class CreationValue
{
    private protected CreationValue() { }
}

/// <summary>Absolute schema path from the primary source root, correlated to the active source iteration.</summary>
public sealed class CreationSourceValue : CreationValue
{
    public CreationSourceValue(IEnumerable<string> path) => Path = CreationPlanBounds.Copy(path, CreationPlanV2.MaximumDepth);
    public IReadOnlyList<string> Path { get; }
}

/// <summary>Typed scalar literals; numeric text must use invariant JSON syntax. Null means an explicit JSON null.</summary>
public sealed class CreationConstantValue : CreationValue
{
    public CreationConstantValue(CreationValueType? type, string? text = null) { Type = type; Text = text; }
    public CreationValueType? Type { get; }
    public string? Text { get; }
}

public sealed record CreationFieldBinding(string Target, CreationValue Value);

/// <summary>A target group constructor. Null iteration constructs one group; an empty path iterates source rows.</summary>
public sealed class CreationScope
{
    public CreationScope(string? target, IEnumerable<CreationFieldBinding> bindings, IEnumerable<CreationScope>? children = null,
        IEnumerable<string>? sourceIteration = null)
    {
        Target = target;
        Bindings = CreationPlanBounds.Copy(bindings, CreationPlanV2.MaximumBindings);
        Children = CreationPlanBounds.Copy(children ?? [], CreationPlanV2.MaximumSchemaNodes);
        SourceIteration = sourceIteration is null ? null : CreationPlanBounds.Copy(sourceIteration, CreationPlanV2.MaximumDepth);
    }
    public string? Target { get; }
    public IReadOnlyList<CreationFieldBinding> Bindings { get; }
    public IReadOnlyList<CreationScope> Children { get; }
    public IReadOnlyList<string>? SourceIteration { get; }
}

/// <summary>Executable intersection of the pinned compiler and authored-document preview contracts.</summary>
public sealed class CreationPlanV2
{
    public const int MaximumSchemaNodes = 512;
    public const int MaximumDepth = 12;
    public const int MaximumBindings = 512;
    public const int MaximumOperations = 1500;
    public CreationPlanV2(CreationBoundary source, CreationBoundary target, CreationScope scope)
    {
        Source = source; Target = target; Scope = scope;
        CreationPlanV2Validation.Validate(this);
    }
    public CreationBoundary Source { get; }
    public CreationBoundary Target { get; }
    public CreationScope Scope { get; }
}

internal static class CreationPlanBounds
{
    internal static ReadOnlyCollection<T> Copy<T>(IEnumerable<T> values, int maximum)
    {
        ArgumentNullException.ThrowIfNull(values);
        var items = values.Take(maximum + 1).ToArray();
        if (items.Length > maximum || items.Any(x => x is null)) throw new ArgumentException("Authoring collection exceeds its closed schema budget.");
        return Array.AsReadOnly(items);
    }

    internal static void Text(string text, int bytes, bool empty = false)
    {
        if (text is null || (!empty && text.Length == 0) || Encoding.UTF8.GetByteCount(text) > bytes || text.Any(char.IsControl))
            throw new ArgumentException("Invalid bounded authoring identifier.");
        // Reject unpaired surrogates rather than changing the literal identity during UTF-8 serialization.
        try { _ = new UTF8Encoding(false, true).GetByteCount(text); }
        catch (EncoderFallbackException) { throw new ArgumentException("Invalid Unicode authoring text."); }
    }
}

public sealed partial class MappingCreationPlan
{
    public MappingCreationPlan(CreationPlanV2 plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        TypedPlan = plan;
        Source = Array.Empty<CreationField>(); Target = Array.Empty<CreationField>(); Bindings = Array.Empty<CreationBinding>();
        bytes = CreationPlanV2Codec.Write(plan, compiler: false);
        compilerBytes = CreationPlanV2Codec.Write(plan, compiler: true);
        if (bytes.Length > MaximumBytes || compilerBytes.Length > MaximumBytes)
            throw new ArgumentException("Authoring plan exceeds its byte budget.");
        Digest = Sha256Digest.FromBytes(bytes); CompilerDigest = Sha256Digest.FromBytes(compilerBytes);
    }

    /// <summary>V2 structured plan; null for v1. The legacy Source/Target/Bindings lists are v1-only (empty for v2).</summary>
    public CreationPlanV2? TypedPlan { get; }
    public int Version => TypedPlan is null ? 1 : 2;
}
