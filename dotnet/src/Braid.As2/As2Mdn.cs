using System.Formats.Asn1;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Text;

namespace Braid.As2;

public static class As2Mdn
{
    // A delayed receipt must be checked against the stored dispatch expectation, never a new binding.
    public static bool Verify(As2Attempt attempt, string from, string to, string? contentType, ReadOnlyMemory<byte> body)
    {
        if (from != attempt.To || to != attempt.From || contentType is null || body.Length > As2Connector.MaxReceiptBytes) return false;
        try
        {
            var outer = Type(contentType, "multipart/signed");
            Require(Equal(Param(outer, "protocol"), "application/pkcs7-signature") && Equal(Param(outer, "micalg"), "sha-256"));
            // Latin1 is a reversible octet view. Do not unfold, trim or reserialize the signed entity.
            var (report, signature) = Parts(Encoding.Latin1.GetString(body.Span), Param(outer, "boundary"));
            var (sigHeaders, sigBody) = Entity(signature);
            Type(Get(sigHeaders, "Content-Type"), "application/pkcs7-signature");
            Require(Equal(Get(sigHeaders, "Content-Transfer-Encoding"), "base64"));
            var der = Convert.FromBase64String(sigBody);
            RequireDetachedData(der);
            var cms = new SignedCms(new ContentInfo(Encoding.Latin1.GetBytes(report)), detached: true);
            cms.Decode(der);
            Require(cms.SignerInfos.Count == 1);
            var signer = cms.SignerInfos[0];
            Require(signer.Certificate?.GetCertHashString(HashAlgorithmName.SHA256) == attempt.ReceiptCertificateSha256 &&
                signer.SignerIdentifier.Type != SubjectIdentifierType.NoSignature && signer.DigestAlgorithm.Value == As2Connector.Sha256 &&
                signer.SignatureAlgorithm.Value is "1.2.840.113549.1.1.1" or "1.2.840.113549.1.1.11");
            signer.CheckSignature(verifySignatureOnly: true); // Trust comes from the pinned certificate, not the machine trust store.

            var (reportHeaders, reportBody) = Entity(report);
            var reportType = Type(Get(reportHeaders, "Content-Type"), "multipart/report");
            Require(Equal(Param(reportType, "report-type"), "disposition-notification"));
            var (human, machine) = Parts(reportBody, Param(reportType, "boundary"));
            Type(Get(Entity(human).Headers, "Content-Type"), "text/plain");
            var (mdnHeaders, fields) = Entity(machine);
            Type(Get(mdnHeaders, "Content-Type"), "message/disposition-notification");
            var mdn = Headers(fields.TrimEnd('\r', '\n'));
            var mic = Get(mdn, "Received-content-MIC").Split(',', StringSplitOptions.TrimEntries);
            var disposition = Get(mdn, "Disposition").Split(';', StringSplitOptions.TrimEntries);
            var recipient = Get(mdn, "Final-Recipient").Split(';', StringSplitOptions.TrimEntries);
            return recipient.Length == 2 && Equal(recipient[0], "rfc822") && recipient[1] == attempt.To &&
                disposition.Length == 2 && Equal(disposition[0], "automatic-action/MDN-sent-automatically") &&
                Equal(disposition[1], "processed") && Get(mdn, "Original-Message-ID") == attempt.MessageId &&
                mic.Length == 2 && mic[0] == attempt.Mic && Equal(mic[1], "sha-256");
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or AsnContentException)
        {
            return false;
        }
    }

    // Ensure that CheckSignature can only authenticate the supplied report, not an embedded message.
    private static void RequireDetachedData(byte[] der)
    {
        var reader = new AsnReader(der, AsnEncodingRules.DER);
        var content = reader.ReadSequence();
        reader.ThrowIfNotEmpty();
        Require(content.ReadObjectIdentifier() == "1.2.840.113549.1.7.2");
        var signed = content.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadSequence();
        signed.ReadInteger();
        signed.ReadSetOf();
        var encapsulated = signed.ReadSequence();
        Require(encapsulated.ReadObjectIdentifier() == "1.2.840.113549.1.7.1" && !encapsulated.HasData);
    }

    // Strict, bounded two-part profile. Only CRLF-delimited whole boundary lines are delimiters.
    private static (string First, string Second) Parts(string text, string boundary)
    {
        Require(boundary.Length is > 0 and <= 70 && boundary.All(c => char.IsAsciiLetterOrDigit(c) || "'()+_,-./:=?".Contains(c)));
        var marker = "\r\n--" + boundary;
        var framed = "\r\n" + text;
        var pieces = framed.Split(marker + "\r\n", StringSplitOptions.None);
        Require(pieces.Length == 3 && !pieces[0].Contains(marker + "--", StringComparison.Ordinal) && !pieces[1].Contains(marker + "--", StringComparison.Ordinal));
        var last = pieces[2].Split(marker + "--", StringSplitOptions.None);
        Require(last.Length == 2 && (last[1].Length == 0 || last[1].StartsWith("\r\n", StringComparison.Ordinal)));
        return (pieces[1], last[0]);
    }

    private static (Dictionary<string, string> Headers, string Body) Entity(string part)
    {
        var split = part.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        Require(split is > 0 and <= 8192);
        return (Headers(part[..split]), part[(split + 4)..]);
    }

    private static Dictionary<string, string> Headers(string text)
    {
        Require(text.Length <= 8192);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? name = null;
        foreach (var line in text.Split("\r\n"))
        {
            Require(line.Length > 0 && line.All(c => c == '\t' || c is >= ' ' and <= '~'));
            if (line[0] is ' ' or '\t')
            {
                Require(name is not null);
                headers[name!] += " " + line.Trim();
                continue;
            }
            var colon = line.IndexOf(':');
            Require(colon > 0);
            name = line[..colon];
            Require(name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') && headers.TryAdd(name, line[(colon + 1)..].Trim()));
        }
        return headers;
    }

    private static MediaTypeHeaderValue Type(string value, string expected)
    {
        var type = MediaTypeHeaderValue.Parse(value);
        Require(Equal(type.MediaType, expected));
        return type;
    }

    private static string Param(MediaTypeHeaderValue type, string name)
    {
        var matches = type.Parameters.Where(p => Equal(p.Name, name)).ToArray();
        Require(matches.Length == 1 && matches[0].Value is not null);
        return matches[0].Value!.Trim('"');
    }

    private static string Get(Dictionary<string, string> fields, string name) => fields.TryGetValue(name, out var value) ? value : throw new FormatException();
    private static bool Equal(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static void Require(bool condition) { if (!condition) throw new FormatException(); }
}
