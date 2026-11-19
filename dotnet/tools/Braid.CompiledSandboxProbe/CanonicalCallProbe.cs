using System.Reflection;
using System.Security.Cryptography.Xml;
using System.Xml;

// Only the test assembly contains these rendezvous nodes. No worker protocol or XML
// document can select them. The marker is emitted by a getter called during traversal.
internal static class CanonicalCallProbe
{
    internal static int Run(string phase)
    {
        var transform = new XmlDsigC14NWithCommentsTransform { Resolver = null };
        var source = new XmlDocument { XmlResolver = null };
        source.LoadXml("<root><first/></root>");
        if (phase == "canonical-load")
        {
            var node = new LoadNode(source);
            source.DocumentElement!.AppendChild(node);
            node.Armed = true;
            transform.LoadInput(source);
        }
        else
        {
            transform.LoadInput(source);
            // This package traverses a private copy, not the original DOM. Install
            // the witness in that copy; a change to the pinned layout fails the test.
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var canonical = typeof(XmlDsigC14NTransform).GetField("_cXml", flags)!.GetValue(transform)!;
            var copy = (XmlDocument)canonical.GetType().GetField("_c14nDoc", flags)!.GetValue(canonical)!;
            var node = new OutputNode(copy);
            copy.DocumentElement!.AppendChild(node);
            node.Armed = true;
            using var stream = (Stream)transform.GetOutput(typeof(Stream));
        }
        return 1; // A missed callback is not proof.
    }

    private static void Park(byte marker)
    {
        using var output = Console.OpenStandardOutput();
        output.WriteByte(marker);
        output.Flush();
        using var forever = new ManualResetEvent(false);
        forever.WaitOne();
    }

    private sealed class LoadNode(XmlDocument doc) : XmlElement("", "load", "", doc)
    {
        internal bool Armed;
        public override string LocalName { get { if (Armed) Park(76); return base.LocalName; } }
    }

    private sealed class OutputNode(XmlDocument doc) : XmlElement("", "output", "", doc)
    {
        internal bool Armed;
        public override XmlNodeList ChildNodes { get { if (Armed) Park(79); return base.ChildNodes; } }
    }
}
