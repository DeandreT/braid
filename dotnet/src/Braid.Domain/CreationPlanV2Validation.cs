using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Braid.Domain;

internal static class CreationPlanV2Validation
{
    internal static void Validate(CreationPlanV2 plan)
    {
        ArgumentNullException.ThrowIfNull(plan.Source); ArgumentNullException.ThrowIfNull(plan.Target);
        ArgumentNullException.ThrowIfNull(plan.Scope);
        var nodes = 0;
        Boundary(plan.Source, ref nodes); Boundary(plan.Target, ref nodes);
        var bindings = 0; var operations = 0;
        Scope(plan.Scope, plan.Target.Schema, plan, null, 0, ref bindings, ref operations);
        if (bindings == 0) throw Invalid("At least one explicit binding is required.");
    }

    private static void Boundary(CreationBoundary boundary, ref int nodes)
    {
        ArgumentNullException.ThrowIfNull(boundary.Schema); ArgumentNullException.ThrowIfNull(boundary.Options);
        if (boundary.Options is not (CreationJsonOptions or CreationXmlOptions or CreationX12Options or CreationEdifactOptions or CreationCsvOptions or CreationTsvOptions))
            throw Invalid("Unsupported format options.");
        if (boundary.Schema.ScalarType is not null) throw Invalid("Boundary roots must be groups.");
        Schema(boundary.Schema, boundary.Options, 0, ref nodes);
        if (boundary.Options is CreationCsvOptions or CreationTsvOptions)
        {
            if (boundary.Schema.Repeating || boundary.Schema.Children.Count == 0 ||
                boundary.Schema.Children.Any(c => c.ScalarType is null || c.Repeating))
                throw Invalid("Tabular boundaries require a nonrepeating flat group of scalar columns.");
        }
        if (boundary.Options is CreationX12Options x12 && (x12.InterchangeVersion is null ||
            x12.InterchangeVersion.Length != 5 || !x12.InterchangeVersion.All(char.IsAsciiDigit)))
            throw Invalid("X12 interchange version requires five ASCII digits.");
        if (boundary.Options is CreationX12Options or CreationEdifactOptions)
        {
            var first = boundary.Schema.Children.FirstOrDefault();
            if (boundary.Schema.Repeating || first is null || first.ScalarType is not null ||
                (boundary.Options is CreationX12Options ? first.Name != "ISA" : first.Name is not ("UNB" or "UNH")))
                throw Invalid("EDI requires an explicit ordered envelope schema.");
        }
        if (boundary.Options is CreationXmlOptions && boundary.Schema.Repeating)
            throw Invalid("XML requires a singular document element.");
    }

    private static void Schema(CreationSchemaNode node, CreationFormatOptions options, int depth, ref int count)
    {
        if (++count > CreationPlanV2.MaximumSchemaNodes || depth > CreationPlanV2.MaximumDepth)
            throw Invalid("Schema exceeds node or depth budget.");
        CreationPlanBounds.Text(node.Name, 256);
        if (node.ScalarType is { } type && !Enum.IsDefined(type)) throw Invalid("Unsupported scalar type.");
        if (node.Repeating && node.Nullable) throw Invalid("Repeated nullable schemas are unsupported.");
        if (options is not CreationJsonOptions && (node.Nullable || node.Required.Count != 0))
            throw Invalid("Nullability and required child lists are JSON-only.");
        if (node.ScalarType is not null && (node.Children.Count != 0 || node.Required.Count != 0))
            throw Invalid("Scalars cannot declare children or required members.");
        if (node.Children.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count() != node.Children.Count ||
            node.Required.Distinct(StringComparer.Ordinal).Count() != node.Required.Count ||
            node.Required.Any(r => !node.Children.Any(c => c.Name == r)))
            throw Invalid("Sibling names and declared required members must be unique.");
        if (options is CreationXmlOptions && node.Xml?.Text != true && (!char.IsAsciiLetter(node.Name[0]) && node.Name[0] != '_' ||
            node.Name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-'))))
            throw Invalid("XML local names must be ASCII NCNames.");
        if (node.Xml is { } xml)
        {
            if (xml.Text)
            {
                if (node.Name != "#text" || xml.Namespace is not null) throw Invalid("XML text requires #text and no namespace.");
            }
            else CreationPlanBounds.Text(xml.Namespace!, 1024, empty: true);
            if (options is not CreationXmlOptions || xml.Nillable || (xml.Attribute && xml.Text) ||
                ((xml.Attribute || xml.Text) && (node.ScalarType is null || node.Repeating || xml.Optional || depth == 0)) ||
                (xml.Optional && (node.Repeating || depth == 0)))
                throw Invalid("Unsupported XML role, nil or optionality.");
            if (xml.Namespace is { Length: > 0 } && (xml.Namespace != xml.Namespace.Trim() ||
                xml.Namespace.Any(c => char.IsWhiteSpace(c) || c is '{' or '}' or '\ufffe' or '\uffff') ||
                xml.Namespace == "http://www.w3.org/2000/xmlns/" ||
                (!xml.Attribute && xml.Namespace == "http://www.w3.org/XML/1998/namespace") ||
                (xml.Attribute && xml.Namespace == "http://www.w3.org/2001/XMLSchema-instance" && node.Name is "nil" or "type")))
                throw Invalid("XML namespace must be an explicit bounded URI without whitespace.");
            if (xml.Attribute && node.Name == "xmlns") throw Invalid("Namespace declarations are not ordinary attributes.");
        }
        if (options is CreationXmlOptions && node.Children.Any(c => c.Xml?.Text == true) &&
            (node.Children.Count(c => c.Xml?.Text == true) != 1 || node.Children.Any(c => c.Xml is not { Attribute: true } and not { Text: true })))
            throw Invalid("XML text supports simple content plus attributes, not mixed content.");
        foreach (var child in node.Children) Schema(child, options, depth + 1, ref count);
    }

    private static void Scope(CreationScope scope, CreationSchemaNode target, CreationPlanV2 plan,
        IReadOnlyList<string>? iteration, int depth, ref int bindings, ref int operations)
    {
        if (depth > CreationPlanV2.MaximumDepth || (depth == 0 ? scope.Target is not null : scope.Target != target.Name))
            throw Invalid("Scope must address its declared target group.");
        if (target.ScalarType is not null) throw Invalid("Scopes construct groups only.");
        if (scope.SourceIteration is { } selected)
        {
            if (depth == 0 && plan.Target.Options is CreationXmlOptions)
                throw Invalid("XML requires one document root; iterate a repeating child group instead.");
            if (iteration is not null) throw Invalid("Only one source iteration is supported on a scope path.");
            var collection = Resolve(plan.Source.Schema, selected, out var repetitions);
            if (collection.ScalarType is not null || (selected.Count != 0 && !collection.Repeating) || repetitions > 1)
                throw Invalid("Source iteration requires a declared group collection.");
            if (depth != 0 && !target.Repeating) throw Invalid("Child source iteration requires a repeating target group.");
            iteration = selected; operations++;
        }
        if (scope.Bindings.Select(b => b.Target).Distinct(StringComparer.Ordinal).Count() != scope.Bindings.Count ||
            scope.Children.Select(c => c.Target).Distinct(StringComparer.Ordinal).Count() != scope.Children.Count)
            throw Invalid("A target child can be constructed only once.");
        foreach (var binding in scope.Bindings)
        {
            if (++bindings > CreationPlanV2.MaximumBindings) throw Invalid("Binding budget exceeded.");
            operations++;
            var field = target.Children.SingleOrDefault(c => c.Name == binding.Target);
            if (field is null || field.ScalarType is null || field.Repeating) throw Invalid("Binding target must be a declared singular scalar.");
            switch (binding.Value)
            {
                case CreationSourceValue source:
                    var input = Resolve(plan.Source.Schema, source.Path, out var repetitions);
                    if (input.ScalarType != field.ScalarType || input.Repeating || input.ScalarType is null ||
                        (input.Nullable && !field.Nullable) || repetitions > 1 ||
                        (repetitions != 0 && (iteration is null || !IterationOwns(plan.Source.Schema, iteration, source.Path))))
                        throw Invalid("Source binding requires compatible scalar type, nullability and collection context.");
                    if (iteration is { Count: > 0 } && !source.Path.Take(iteration.Count).SequenceEqual(iteration))
                    {
                        // Unpinned legacy Source reads search the current item before the root. There is no
                        // uniterated-root selector in the admitted Source vocabulary, so refuse schema paths
                        // that could read a different item field instead of the declared absolute root field.
                        var collection = Resolve(plan.Source.Schema, iteration, out _);
                        if (CanShadow(collection, source.Path))
                            throw Invalid("Absolute source path is shadowed by the active collection; bind it outside that iteration.");
                    }
                    break;
                case CreationConstantValue constant:
                    Constant(constant);
                    if (constant.Type is null ? !field.Nullable || plan.Target.Options is not CreationJsonOptions : constant.Type != field.ScalarType)
                        throw Invalid("Constant type must match its declared target.");
                    break;
                default: throw Invalid("Only source and typed constant expressions are authorable by the pinned compiler.");
            }
        }
        foreach (var child in scope.Children)
        {
            var field = target.Children.SingleOrDefault(c => c.Name == child.Target);
            if (field is null || field.ScalarType is not null) throw Invalid("Child scope must address a declared target group.");
            operations++;
            Scope(child, field, plan, iteration, depth + 1, ref bindings, ref operations);
        }
        // All structural constructors are explicit. JSON can omit nonrequired children; XML can omit optional
        // elements/attributes. EDI and tabular fields must be present, including unused positional slots.
        foreach (var field in target.Children)
        {
            var required = plan.Target.Options switch
            {
                CreationJsonOptions => target.Required.Contains(field.Name),
                CreationXmlOptions => field.Xml is not { Optional: true } and not { Attribute: true } && !field.Repeating,
                _ => true
            };
            if (required && !scope.Bindings.Any(b => b.Target == field.Name) && !scope.Children.Any(c => c.Target == field.Name))
                throw Invalid("Required target children need explicit bindings or scopes.");
        }
        if (operations > CreationPlanV2.MaximumOperations) throw Invalid("Compiler operation budget exceeded.");
    }

    private static bool IterationOwns(CreationSchemaNode root, IReadOnlyList<string> iteration, IReadOnlyList<string> path)
    {
        if (root.Repeating) return iteration.Count == 0;
        var node = root;
        for (var i = 0; i < path.Count; i++)
        {
            node = node.Children.Single(c => c.Name == path[i]);
            if (node.Repeating) return iteration.Count == i + 1 && iteration.SequenceEqual(path.Take(i + 1));
        }
        return false;
    }

    private static bool CanShadow(CreationSchemaNode collection, IReadOnlyList<string> path)
    {
        var node = collection;
        foreach (var component in path)
        {
            node = node.Children.SingleOrDefault(c => c.Name == component);
            if (node is null) return false;
            // Legacy scalar lookup returns Null immediately for an empty repeated value, even if a later
            // path component is absent. Refuse this possible shadow as well as an existing terminal node.
            if (node.Repeating) return true;
        }
        // Schema groups can materialize as scalar null/absence (including nullable JSON groups).
        // A declared terminal node is therefore a potential shadow regardless of schema kind or format.
        return true;
    }

    private static CreationSchemaNode Resolve(CreationSchemaNode root, IReadOnlyList<string> path, out int repetitions)
    {
        if (path.Count > CreationPlanV2.MaximumDepth) throw Invalid("Source path exceeds depth budget.");
        var node = root; repetitions = node.Repeating ? 1 : 0;
        foreach (var component in path)
        {
            CreationPlanBounds.Text(component, 256);
            node = node.Children.SingleOrDefault(c => c.Name == component) ?? throw Invalid("Source path is not declared.");
            if (node.Repeating) repetitions++;
        }
        return node;
    }

    private static void Constant(CreationConstantValue value)
    {
        if (value.Type is null)
        {
            if (value.Text is not null) throw Invalid("JSON null has no text.");
            return;
        }
        if (value.Text is null || Encoding.UTF8.GetByteCount(value.Text) > 4096)
            throw Invalid("Constant text exceeds its budget.");
        try { _ = new UTF8Encoding(false, true).GetByteCount(value.Text); }
        catch (EncoderFallbackException) { throw Invalid("Constant text must be valid Unicode."); }
        var valid = value.Type switch
        {
            CreationValueType.String => true,
            CreationValueType.Boolean => value.Text is "true" or "false",
            CreationValueType.Integer => long.TryParse(value.Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer) &&
                integer.ToString(CultureInfo.InvariantCulture) == value.Text,
            CreationValueType.Float => Float(value.Text),
            _ => false
        };
        if (!valid) throw Invalid("Invalid typed scalar literal.");
    }

    private static bool Float(string text)
    {
        try
        {
            using var json = JsonDocument.Parse(text);
            return text == text.Trim() && json.RootElement.ValueKind == JsonValueKind.Number &&
                json.RootElement.TryGetDouble(out var number) && double.IsFinite(number);
        }
        catch (JsonException) { return false; }
    }

    private static ArgumentException Invalid(string reason) => new(reason);
}
