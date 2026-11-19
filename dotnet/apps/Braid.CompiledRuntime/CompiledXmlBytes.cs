using System.Collections;
using System.Reflection;
using Braid.Application;

// Child-process binding only. This is not build or endpoint admission. The caller must have
// admitted the artifact, full declaration/metadata table, selected input set and context.
internal static class CompiledXmlBytes
{
    internal sealed record Document(string? Name, byte[] Bytes);

    internal static Document[] Invoke(Assembly assembly, object context, bool all, string? selected,
        Document primary, IReadOnlyList<Document> requiredNamed, IReadOnlyList<string> namedOutputs,
        int inputLimit, int outputLimit, CancellationToken token)
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
        var item = Type("Ferrule.Generated.NamedXmlBytesInput");
        var list = typeof(IReadOnlyList<>).MakeGenericType(item);
        if (context.GetType() != execution) throw Invalid();
        var inputs = Array.CreateInstance(item, requiredNamed.Count);
        var constructor = item.GetConstructor([typeof(string), typeof(byte[])]) ?? throw Invalid();
        for (int i = 0; i < requiredNamed.Count; i++)
            inputs.SetValue(constructor.Invoke([requiredNamed[i].Name ?? throw Invalid(), requiredNamed[i].Bytes]), i);
        object Call(string name, Type[] parameters, object?[] args) =>
            (mapping.GetMethod(name, BindingFlags.Public | BindingFlags.Static, parameters) ?? throw Invalid()).Invoke(null, args) ?? throw Invalid();
        static T Property<T>(object value, string name) => value.GetType().GetProperty(name)?.GetValue(value) is T result ? result : throw Invalid();
        static Document Named(object value, string expected) => Property<string>(value, "Name") == expected
            ? new(expected, Property<byte[]>(value, "Document")) : throw Invalid();

        Document[] outputs;
        token.ThrowIfCancellationRequested();
        if (all)
        {
            var result = Call("ExecuteXmlBytesOutputsWithSources", [typeof(byte[]), list, execution], [primary.Bytes, inputs, context]);
            if (result.GetType() != Type("Ferrule.Generated.XmlBytesExecutionOutputs")) throw Invalid();
            outputs = new Document[1 + namedOutputs.Count];
            outputs[0] = new(null, Property<byte[]>(result, "Primary"));
            var index = 0;
            foreach (var extra in Property<IEnumerable>(result, "Extras"))
            {
                if (index >= namedOutputs.Count || extra is null) throw Invalid();
                outputs[1 + index] = Named(extra, namedOutputs[index]);
                index++;
            }
            if (index != namedOutputs.Count) throw Invalid();
        }
        else
        {
            var selection = Type("Ferrule.Runtime.FerruleTargetSelection");
            var factory = selection.GetMethod(selected is null ? "Primary" : "Named", BindingFlags.Public | BindingFlags.Static,
                selected is null ? System.Type.EmptyTypes : [typeof(string)]) ?? throw Invalid();
            var result = Call("ExecuteXmlBytesSelectedTargetWithHost", [typeof(byte[]), selection, list, execution],
                [primary.Bytes, factory.Invoke(null, selected is null ? null : [selected]), inputs, context]);
            if (result.GetType() != Type("Ferrule.Generated.SelectedXmlBytesTargetOutput+" + (selected is null ? "Primary" : "Named"))) throw Invalid();
            outputs = [selected is null ? new(null, Property<byte[]>(result, "Document")) : Named(Property<object>(result, "Output"), selected)];
        }
        XmlDocumentSafety.ValidateSet(outputs.Select(d => (ReadOnlyMemory<byte>)d.Bytes).ToArray(), XmlDocumentRole.Output, token, outputLimit);
        return outputs;
    }

    private static InvalidOperationException Invalid() => new("Invalid generated XML interface");
}
