using System.Collections;
using System.Reflection;
using Braid.Application;

// Child-process binding only. This is not build or endpoint admission. The caller must have
// admitted the artifact, full declaration/metadata table, selected input set and context.
internal static class CompiledXmlBytes
{
    internal sealed record Document(string? Name, byte[] Bytes);
    internal sealed record ProjectionLimits(int Nodes, long FieldBytes, long Work);
    internal sealed class InputFailure(bool resourceLimit) : Exception("Compiled XML input refused")
    {
        internal bool ResourceLimit { get; } = resourceLimit;
    }

    internal static Document[] Invoke(Assembly assembly, object context, bool all, string? selected,
        Document primary, IReadOnlyList<Document> requiredNamed, IReadOnlyList<string> namedOutputs,
        int inputLimit, int outputLimit, CancellationToken token, ProjectionLimits? projection = null)
    {
        if (primary.Name is not null || (all && selected is not null) || requiredNamed.Count >= XmlDocumentPolicy.MaximumInputs ||
            namedOutputs.Count >= XmlDocumentPolicy.MaximumOutputs || inputLimit is <= 0 or > XmlDocumentPolicy.MaximumBytes ||
            outputLimit is <= 0 or > XmlDocumentPolicy.MaximumBytes || (selected is not null && !namedOutputs.Contains(selected, StringComparer.Ordinal)))
            throw new InvalidOperationException("Invalid XML invocation");
        XmlDocumentSafety.ValidateSet(requiredNamed.Prepend(primary).Select(d => (ReadOnlyMemory<byte>)d.Bytes).ToArray(),
            XmlDocumentRole.Input, token, inputLimit);

        Type Type(string name) => assembly.GetType(name, throwOnError: true)!;
        var mapping = Type("Ferrule.Generated.GeneratedMapping");
        var execution = Type("Ferrule.Runtime.FerruleExecutionContext");
        projection ??= new(65536, 1048576, 1000000);
        var budgetType = Type("Ferrule.Runtime.FerruleXmlProjectionLimits");
        var budget = (budgetType.GetConstructor([typeof(int), typeof(long), typeof(long)]) ?? throw Invalid())
            .Invoke([projection.Nodes, projection.FieldBytes, projection.Work]);
        var item = Type("Ferrule.Generated.NamedXmlBytesInput");
        var list = typeof(IReadOnlyList<>).MakeGenericType(item);
        if (context.GetType() != execution) throw Invalid();
        var inputs = Array.CreateInstance(item, requiredNamed.Count);
        var constructor = item.GetConstructor([typeof(string), typeof(byte[])]) ?? throw Invalid();
        for (int i = 0; i < requiredNamed.Count; i++)
            inputs.SetValue(constructor.Invoke([requiredNamed[i].Name ?? throw Invalid(), requiredNamed[i].Bytes]), i);
        object Call(string name, Type[] parameters, object?[] args)
        {
            try { return (mapping.GetMethod(name, BindingFlags.Public | BindingFlags.Static, parameters) ?? throw Invalid()).Invoke(null, args) ?? throw Invalid(); }
            catch (TargetInvocationException e) when (e.InnerException is { } outer && outer.GetType() == Type("Ferrule.Runtime.FerruleXmlExecutionException") &&
                outer.InnerException is { } boundary && boundary.GetType() == Type("Ferrule.Runtime.FerruleXmlBoundaryException") &&
                ReferenceEquals(outer.GetType().GetProperty("Boundary")?.GetValue(outer), boundary) &&
                Equals(boundary.GetType().GetProperty("Kind")?.GetValue(boundary), Enum.Parse(Type("Ferrule.Runtime.FerruleXmlBoundaryErrorKind"), "Input")) &&
                outer.GetType().GetProperty("Output")?.GetValue(outer) is null && outer.GetType().GetProperty("Request")?.GetValue(outer) is null && outer.GetType().GetProperty("Input")?.GetValue(outer) is { } owner &&
                owner.GetType() == Type("Ferrule.Runtime.FerruleXmlInputSource") &&
                ValidOwner(owner))
            {
                var resource = boundary.InnerException?.GetType() == Type("Ferrule.Runtime.FerruleXmlStructuredResourceException") ||
                    boundary.InnerException is null && boundary.Message is
                    ("Input: XML boundary failed: XML input exceeds its materialized instance node limit" or
                     "Input: XML boundary failed: XML input exceeds its materialized field/string byte limit" or
                     "Input: XML boundary failed: XML input exceeds its projection work limit");
                throw new InputFailure(resource);
            }
        }
        bool ValidOwner(object owner)
        {
            var kind = Property<object>(owner, "Kind");
            var index = owner.GetType().GetProperty("Index")!.GetValue(owner);
            var name = owner.GetType().GetProperty("Name")!.GetValue(owner);
            var ownerKind = Type("Ferrule.Runtime.FerruleXmlInputSourceKind");
            return Equals(kind, Enum.Parse(ownerKind, "Primary")) ? index is null && name is null :
                Equals(kind, Enum.Parse(ownerKind, "Named")) && index is int ordinal && ordinal >= 0 &&
                name is string inputName && requiredNamed.Any(d => d.Name == inputName);
        }
        static T Property<T>(object value, string name) => value.GetType().GetProperty(name)?.GetValue(value) is T result ? result : throw Invalid();
        static Document Named(object value, string expected) => Property<string>(value, "Name") == expected
            ? new(expected, Property<byte[]>(value, "Document")) : throw Invalid();

        Document[] outputs;
        token.ThrowIfCancellationRequested();
        if (all)
        {
            var result = Call("ExecuteXmlBytesOutputsWithSources", [typeof(byte[]), list, execution, budgetType], [primary.Bytes, inputs, context, budget]);
            if (result.GetType() != Type("Ferrule.Generated.XmlBytesExecutionOutputs")) throw Invalid();
            outputs = new Document[1 + namedOutputs.Count];
            outputs[0] = new(null, Property<byte[]>(result, "Primary"));
            var remaining = namedOutputs.ToHashSet(StringComparer.Ordinal);
            var index = 0;
            foreach (var extra in Property<IEnumerable>(result, "Extras"))
            {
                if (index >= namedOutputs.Count || extra is null) throw Invalid();
                var name = Property<string>(extra, "Name");
                if (!remaining.Remove(name)) throw Invalid();
                outputs[1 + index] = Named(extra, name);
                index++;
            }
            if (index != namedOutputs.Count) throw Invalid();
        }
        else
        {
            var selection = Type("Ferrule.Runtime.FerruleTargetSelection");
            var factory = selection.GetMethod(selected is null ? "Primary" : "Named", BindingFlags.Public | BindingFlags.Static,
                selected is null ? System.Type.EmptyTypes : [typeof(string)]) ?? throw Invalid();
            var result = Call("ExecuteXmlBytesSelectedTargetWithHost", [typeof(byte[]), selection, list, execution, budgetType],
                [primary.Bytes, factory.Invoke(null, selected is null ? null : [selected]), inputs, context, budget]);
            if (result.GetType() != Type("Ferrule.Generated.SelectedXmlBytesTargetOutput+" + (selected is null ? "Primary" : "Named"))) throw Invalid();
            outputs = [selected is null ? new(null, Property<byte[]>(result, "Document")) : Named(Property<object>(result, "Output"), selected)];
        }
        try { XmlDocumentSafety.ValidateSet(outputs.Select(d => (ReadOnlyMemory<byte>)d.Bytes).ToArray(), XmlDocumentRole.Output, token, outputLimit); }
        catch (XmlDocumentException error) when (!error.IsResourceLimit) { throw Invalid(); }
        return outputs;
    }

    private static InvalidOperationException Invalid() => new("Invalid generated XML interface");
}
