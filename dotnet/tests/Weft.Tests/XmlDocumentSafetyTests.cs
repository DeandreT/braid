using System.Text;
using Weft.Application;

namespace Weft.Tests;

public sealed class XmlDocumentSafetyTests
{
    [Theory]
    [InlineData("<r/>")]
    [InlineData("\ufeff<r/>")]
    [InlineData("<?xml version='1.0'?><r/>")]
    [InlineData("<?xml version='1.0' encoding='uTf-8'?><r/>")]
    [InlineData("<r>&amp;&lt;&gt;&apos;&quot;&#65;&#x1F680;</r>")]
    [InlineData("<!-- <!DOCTYPE r SYSTEM 'file:///never-open'> --><r><![CDATA[<!DOCTYPE r>]]></r>")]
    [InlineData("<?xml-stylesheet href='https://never-fetch.invalid/style.xsl'?><r xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance' xsi:noNamespaceSchemaLocation='file:///never-open.xsd'/>")]
    public void Safe_xml_is_scanned_without_fetching_resources(string xml)
    {
        var bytes = Encoding.UTF8.GetBytes(xml);
        var result = XmlDocumentSafety.Validate(bytes);
        Assert.Equal(bytes.Length, result.OriginalBytes);
        Assert.True(result.PhysicalNodes >= 2);
    }

    [Theory]
    [InlineData("<!DOCTYPE r><r/>")]
    [InlineData("<!DOCTYPE r [<!ENTITY s 'secret'>]><r>&s;</r>")]
    [InlineData("<!DOCTYPE r SYSTEM 'file:///private-secret.xml'><r/>")]
    [InlineData("<!DOCTYPE r PUBLIC 'sensitive-id' 'https://private.invalid/dtd'><r/>")]
    [InlineData("<!DOCTYPE r [<!ENTITY % p SYSTEM 'file:///private-secret.xml'>%p;]><r/>")]
    [InlineData("<r>&unknown;</r>")]
    [InlineData("<r>&#0;</r>")]
    [InlineData("<r>&#xD800;</r>")]
    [InlineData("<r>&#x110000;</r>")]
    [InlineData("<r/><s/>")]
    [InlineData("<r a='1' a='2'/>")]
    [InlineData("<p:r/>")]
    [InlineData("<r>\0</r>")]
    [InlineData("\ufeff\ufeff<r/>")]
    public void Unsafe_or_malformed_xml_has_a_fixed_sanitized_refusal(string xml)
    {
        var error = Assert.Throws<XmlDocumentException>(() => XmlDocumentSafety.Validate(Encoding.UTF8.GetBytes(xml)));
        Assert.Equal(XmlDocumentFailure.InvalidXml, error.Reason);
        Assert.False(error.IsResourceLimit);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("private", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("<!DOCTYPE", error.ToString(), StringComparison.Ordinal);
        if (error.Location is { } location)
        {
            Assert.True(location.Line > 0);
            Assert.True(location.Column > 0);
        }
    }

    [Theory]
    [InlineData("<?xml version='1.1'?><r/>")]
    [InlineData("<?xml version='1.0' encoding='UTF-16'?><r/>")]
    [InlineData("<?xml version='1.0' encoding='ISO-8859-1'?><r/>")]
    [InlineData("<?xml version='1.0' encoding='utf8'?><r/>")]
    public void Declaration_never_selects_a_different_decoder(string xml)
    {
        var error = Assert.Throws<XmlDocumentException>(() => XmlDocumentSafety.Validate(Encoding.UTF8.GetBytes(xml)));
        Assert.Contains(error.Reason, new[] { XmlDocumentFailure.InvalidXml, XmlDocumentFailure.InvalidDeclaration });
    }

    [Fact]
    public void Invalid_utf8_and_utf16_bytes_are_not_transcoded()
    {
        byte[][] documents = [[0xc0, 0xaf], [0xed, 0xa0, 0x80], [0xf4, 0x90, 0x80, 0x80], [0xe2, 0x82],
            [0xff, 0xfe, 0x3c, 0x00], Encoding.Unicode.GetBytes("<r/>")];
        foreach (var bytes in documents)
        {
            var error = Assert.Throws<XmlDocumentException>(() => XmlDocumentSafety.Validate(bytes));
            Assert.Contains(error.Reason, new[] { XmlDocumentFailure.InvalidUtf8, XmlDocumentFailure.InvalidXml });
            Assert.Null(error.InnerException);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("\ufeff")]
    [InlineData(" \r\n\t")]
    [InlineData("<!-- only a comment -->")]
    public void A_document_requires_a_root(string xml)
    {
        var error = Assert.Throws<XmlDocumentException>(() => XmlDocumentSafety.Validate(Encoding.UTF8.GetBytes(xml)));
        Assert.Contains(error.Reason, new[] { XmlDocumentFailure.DocumentRequired, XmlDocumentFailure.InvalidXml });
    }

    [Fact]
    public void Node_definition_combines_adjacent_text_and_cdata_and_counts_comments_and_pis()
    {
        var xml = "<?xml version='1.0'?>\n<!--p--><?p v?><r>a<![CDATA[b]]>c<!--i-->d<?i v?>e<e/>f</r><!--z-->\n";
        var result = XmlDocumentSafety.Validate(Encoding.UTF8.GetBytes(xml));
        Assert.Equal(12, result.PhysicalNodes);
        Assert.Equal(2, result.MaximumDepth);
        Assert.Equal(0, result.MaximumAttributes);
    }

    [Fact]
    public void Physical_depth_including_unknown_children_is_bounded()
    {
        static byte[] Nested(int count) => Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("<unknown>", count)) +
            string.Concat(Enumerable.Repeat("</unknown>", count)));
        Assert.Equal(64, XmlDocumentSafety.Validate(Nested(64)).MaximumDepth);
        var error = Assert.Throws<XmlDocumentException>(() => XmlDocumentSafety.Validate(Nested(65)));
        Assert.Equal(XmlDocumentFailure.DepthLimit, error.Reason);
        Assert.Equal(65L, error.Observed);
        Assert.Equal(64L, error.Limit);
        Assert.True(error.IsResourceLimit);
    }

    [Fact]
    public void Physical_attributes_include_namespace_declarations()
    {
        static byte[] Attributes(int count) => Encoding.UTF8.GetBytes("<r" +
            string.Concat(Enumerable.Range(0, count).Select(index => $" xmlns:p{index}='urn:{index}'")) + "/>");
        Assert.Equal(256, XmlDocumentSafety.Validate(Attributes(256)).MaximumAttributes);
        var error = Assert.Throws<XmlDocumentException>(() => XmlDocumentSafety.Validate(Attributes(257)));
        Assert.Equal(XmlDocumentFailure.AttributeLimit, error.Reason);
        Assert.Equal(257L, error.Observed);
    }

    [Fact]
    public void Physical_node_boundary_includes_the_document_and_unknown_elements()
    {
        static byte[] Nodes(int count) => Encoding.UTF8.GetBytes("<r>" +
            string.Concat(Enumerable.Repeat("<ignored/>", count - 2)) + "</r>");
        // Use a short element name so the independent 8 MiB byte budget does not win first.
        var atLimit = Encoding.UTF8.GetBytes("<r>" + string.Concat(Enumerable.Repeat("<x/>", XmlDocumentPolicy.MaximumNodes - 2)) + "</r>");
        Assert.Equal(XmlDocumentPolicy.MaximumNodes, XmlDocumentSafety.Validate(atLimit).PhysicalNodes);
        var over = Encoding.UTF8.GetBytes("<r>" + string.Concat(Enumerable.Repeat("<x/>", XmlDocumentPolicy.MaximumNodes - 1)) + "</r>");
        Assert.Equal(XmlDocumentFailure.NodeLimit,
            Assert.Throws<XmlDocumentException>(() => XmlDocumentSafety.Validate(over)).Reason);
        Assert.Equal(3, XmlDocumentSafety.Validate(Nodes(3)).PhysicalNodes);
    }

    [Fact]
    public void Original_bytes_and_complete_set_have_independent_smaller_caller_bounds()
    {
        byte[] single = Encoding.UTF8.GetBytes("<r/>");
        Assert.Equal(4, XmlDocumentSafety.Validate(single, maximumBytes: 4).OriginalBytes);
        Assert.Equal(XmlDocumentFailure.ByteLimit,
            Assert.Throws<XmlDocumentException>(() => XmlDocumentSafety.Validate(single, maximumBytes: 3)).Reason);
        ReadOnlyMemory<byte>[] documents = [single, single];
        Assert.Equal(2, XmlDocumentSafety.ValidateSet(documents, XmlDocumentRole.Input, maximumBytes: 8).Count);
        var error = Assert.Throws<XmlDocumentException>(() => XmlDocumentSafety.ValidateSet(documents, XmlDocumentRole.Input, maximumBytes: 7));
        Assert.Equal(XmlDocumentFailure.ByteLimit, error.Reason);
        Assert.Equal(8L, error.Observed);
        Assert.Null(error.Statistics); // Aggregate refusal occurs before any member is scanned.
        Assert.Throws<ArgumentOutOfRangeException>(() => XmlDocumentSafety.Validate(single, maximumBytes: XmlDocumentPolicy.MaximumBytes + 1));
    }

    [Fact]
    public void Full_byte_boundary_and_large_chunked_values_are_scanned()
    {
        var atLimit = Encoding.UTF8.GetBytes("<r>" + new string('x', XmlDocumentPolicy.MaximumBytes - 7) + "</r>");
        Assert.Equal(XmlDocumentPolicy.MaximumBytes, XmlDocumentSafety.Validate(atLimit).OriginalBytes);
        var over = new byte[atLimit.Length + 1];
        Assert.Equal(XmlDocumentFailure.ByteLimit,
            Assert.Throws<XmlDocumentException>(() => XmlDocumentSafety.Validate(over)).Reason);
        var comment = Encoding.UTF8.GetBytes("<!--" + new string('x', 20_000) + "--><r a='" + new string('y', 20_000) + "'/>");
        Assert.Equal(3, XmlDocumentSafety.Validate(comment).PhysicalNodes);
        Assert.Equal(3, XmlDocumentSafety.Validate(Encoding.UTF8.GetBytes("<r>" + new string('\u00e9', 4095) + "\U0001f680</r>")).PhysicalNodes);
    }

    [Fact]
    public void Output_count_is_not_limited_to_the_input_count_and_late_invalid_output_never_returns_a_set()
    {
        ReadOnlyMemory<byte>[] documents = Enumerable.Repeat<ReadOnlyMemory<byte>>(Encoding.UTF8.GetBytes("<r/>"), 64).ToArray();
        Assert.Equal(64, XmlDocumentSafety.ValidateSet(documents, XmlDocumentRole.Output).Count);
        Assert.Equal(XmlDocumentFailure.DocumentCountLimit,
            Assert.Throws<XmlDocumentException>(() => XmlDocumentSafety.ValidateSet(documents, XmlDocumentRole.Input)).Reason);
        documents[^1] = Encoding.UTF8.GetBytes("<broken>");
        Assert.Throws<XmlDocumentException>(() => XmlDocumentSafety.ValidateSet(documents, XmlDocumentRole.Output));
    }

    [Fact]
    public void Cancellation_is_not_reclassified_as_invalid_xml()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => XmlDocumentSafety.Validate(Encoding.UTF8.GetBytes("<r/>"), cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => XmlDocumentSafety.ValidateSet(
            new ReadOnlyMemory<byte>[] { Encoding.UTF8.GetBytes("<r/>") }, XmlDocumentRole.Input, cancellation.Token));
    }
}
