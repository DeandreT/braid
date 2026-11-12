using System.Text.Json;
using Weft.Domain;

namespace Weft.Application;

/// <summary>Canonical v2 locator tuples, shared with future isolated PDF adapters. This is not a document parser.</summary>
public static class SpecEvidenceLocatorJson
{
    public static void Write(Utf8JsonWriter writer, SpecEvidenceLocator locator)
    {
        ArgumentNullException.ThrowIfNull(writer); ArgumentNullException.ThrowIfNull(locator);
        writer.WriteStartArray();
        switch (locator)
        {
            case SpecEvidenceLocator.ByteRange span:
                writer.WriteStringValue("byte-range"); writer.WriteNumberValue(span.Offset); writer.WriteNumberValue(span.Length);
                writer.WriteNumberValue(span.StartLine); writer.WriteNumberValue(span.EndLine); writer.WriteStringValue(span.JsonPointer);
                break;
            case SpecEvidenceLocator.WordParagraph word:
                writer.WriteStringValue("docx-paragraph"); writer.WriteStringValue(word.Part); writer.WriteNumberValue(word.Paragraph);
                if (word.Table is { } table)
                { writer.WriteNumberValue(table); writer.WriteNumberValue(word.Row!.Value); writer.WriteNumberValue(word.Cell!.Value); }
                else { writer.WriteNullValue(); writer.WriteNullValue(); writer.WriteNullValue(); }
                break;
            case SpecEvidenceLocator.SheetCell cell:
                writer.WriteStringValue("xlsx-cell"); writer.WriteStringValue(cell.Part); writer.WriteStringValue(cell.Sheet);
                writer.WriteStringValue(cell.Cell); writer.WriteStringValue(cell.ValueKind.ToString()); writer.WriteBooleanValue(cell.HasCachedValue);
                break;
            case SpecEvidenceLocator.PdfPage pdf:
                writer.WriteStringValue("pdf-page"); writer.WriteNumberValue(pdf.Page);
                break;
            default: throw new ArgumentException("Unknown evidence locator.", nameof(locator));
        }
        writer.WriteEndArray();
    }
}
