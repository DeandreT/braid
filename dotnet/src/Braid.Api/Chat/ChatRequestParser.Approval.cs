using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Braid.Domain;

namespace Braid.Api.Chat;

internal static partial class ChatRequestParser
{
    internal static Sha256Digest Fingerprint(string? text)
    {
        try
        {
            var digest = Sha256Digest.Parse(text ?? "");
            return digest.Value == text ? digest : throw ChatRequestException.Invalid();
        }
        catch (ArgumentException) { throw ChatRequestException.Invalid(); }
    }

    internal static async Task<Sha256Digest> ReadApprovalAsync(HttpRequest request, CancellationToken ct)
    {
        using var document = await ReadJsonAsync(request, 256, ct);
        string? fingerprint = null;
        try
        {
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name != "expectedFingerprint" || fingerprint is not null || property.Value.ValueKind != JsonValueKind.String)
                    throw ChatRequestException.Invalid();
                fingerprint = property.Value.GetString();
            }
        }
        catch (InvalidOperationException) { throw ChatRequestException.Invalid(); }
        return Fingerprint(fingerprint);
    }
}
