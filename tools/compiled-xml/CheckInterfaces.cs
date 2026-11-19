using Ferrule.Generated;
using Ferrule.Runtime;
using Braid.Application;

// Only the small authored fixture emitted by the adjacent driver; never load a supplied assembly.
var source = File.ReadAllBytes("input.xml");
var primary = File.ReadAllBytes("primary.xml");
var copy = File.ReadAllBytes("copy.xml");
if (source.Length * 2 > 256 || primary.Length + copy.Length > 1024 || primary.SequenceEqual(copy))
    throw new InvalidOperationException("Invalid XML qualification fixture");
var clock = new FerruleExecutionContext("entry.native.json", "entry.native.json", "2026-11-02T12:00:00Z");
XmlDocumentSafety.ValidateSet([source, source], XmlDocumentRole.Input, maximumBytes: source.Length * 2);
var all = GeneratedMapping.ExecuteXmlBytesOutputsWithSources(source, [new("lookup", source)], clock);
if (!all.Primary.SequenceEqual(primary) || all.Extras.Count != 1 || all.Extras[0].Name != "copy" || !all.Extras[0].Document.SequenceEqual(copy))
    throw new InvalidOperationException("All-output XML differs from native bytes or identity");
ReadOnlyMemory<byte>[] documents = [all.Primary, all.Extras[0].Document];
var total = primary.Length + copy.Length;
XmlDocumentSafety.ValidateSet(documents, XmlDocumentRole.Output, maximumBytes: total);
try
{
    XmlDocumentSafety.ValidateSet(documents, XmlDocumentRole.Output, maximumBytes: total - 1);
    throw new InvalidOperationException("A byte over the combined output budget was accepted");
}
catch (XmlDocumentException error) when (error.Reason == XmlDocumentFailure.ByteLimit && error.Observed == total) { }
// The unused lookup is declared for All, but neither selected route needs it.
if (GeneratedMapping.ExecuteXmlBytesSelectedTargetWithHost(source, FerruleTargetSelection.Primary(), [], clock)
    is not SelectedXmlBytesTargetOutput.Primary selected || !selected.Document.SequenceEqual(primary))
    throw new InvalidOperationException("Selected primary XML differs from native bytes");
if (GeneratedMapping.ExecuteXmlBytesSelectedTargetWithHost(source, FerruleTargetSelection.Named("copy"), [], clock)
    is not SelectedXmlBytesTargetOutput.Named named || named.Output.Name != "copy" || !named.Output.Document.SequenceEqual(copy))
    throw new InvalidOperationException("Selected named XML differs from native bytes or identity");
Console.WriteLine("XML all / selected primary / selected named: exact native bytes");

CompiledXmlBytes.Document[] Invoke(bool every, string? name, int maxIn, int maxOut) => CompiledXmlBytes.Invoke(
    typeof(GeneratedMapping).Assembly, clock, every, name, new(null, source), every ? [new("lookup", source)] : [],
    ["copy"], maxIn, maxOut, default);
var bound = Invoke(true, null, 2 * source.Length, total);
if (bound.Length != 2 || bound[0].Name != null || !bound[0].Bytes.SequenceEqual(primary) ||
    bound[1].Name != "copy" || !bound[1].Bytes.SequenceEqual(copy)) throw new InvalidOperationException("Bound all outputs differ");
foreach (var (name, expected) in new (string?, byte[])[] { (null, primary), ("copy", copy) })
{
    var single = Invoke(false, name, source.Length, expected.Length).Single();
    if (single.Name != name || !single.Bytes.SequenceEqual(expected)) throw new InvalidOperationException("Bound selected output differs");
}
foreach (var (maxIn, maxOut) in new[] { (2 * source.Length - 1, total), (2 * source.Length, total - 1) })
{
    try { Invoke(true, null, maxIn, maxOut); throw new InvalidOperationException("Aggregate limit was not enforced"); }
    catch (XmlDocumentException error) when (error.Reason == XmlDocumentFailure.ByteLimit) { }
}
Console.WriteLine("Braid byte binding: three selections, exact limits, one-byte input/output refusals");

foreach (var selection in new (bool all, string? name)[] { (true, null), (false, null), (false, "copy") })
{
    void Run(byte[] input, CompiledXmlBytes.ProjectionLimits limits) => CompiledXmlBytes.Invoke(
        typeof(GeneratedMapping).Assembly, clock, selection.all, selection.name, new(null, input),
        selection.all ? [new("lookup", source)] : [], ["copy"], 256, 1024, default, limits);
    // Order has two scalar children. Field name bytes: 4+8, plus the string value 'A'.
    Run(source, new(3, 13, 7));
    foreach (var lower in new CompiledXmlBytes.ProjectionLimits[] { new(2, 13, 7), new(3, 12, 7), new(3, 13, 6) })
    {
        try { Run(source, lower); throw new InvalidOperationException("Projection limit not enforced"); }
        catch (CompiledXmlBytes.InputFailure error) when (error.ResourceLimit) { }
    }
    try { Run("<Order>"u8.ToArray(), new(3, 13, 7)); throw new InvalidOperationException("Incomplete XML accepted"); }
    catch (XmlDocumentException error) when (!error.IsResourceLimit) { }
}
Console.WriteLine("Bounded XML: all three projection budgets and malformed document remain distinct");
