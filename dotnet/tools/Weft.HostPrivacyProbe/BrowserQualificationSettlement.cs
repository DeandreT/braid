using System.Security.Cryptography;
using System.Text.Json;
using Weft.BrowserQualification;

namespace Weft.HostPrivacyProbe;

internal sealed partial class BrowserQualificationAdmission
{
    // Closed registration is a categorical cancellation ACK, never a grant and
    // never a socket-server failure caused solely by a legitimate seal race.
    // The existing Register/Allowed/Invoke consume/equality gates are unchanged.
    internal QualificationReply RegisterIfOpen(TransportAdmission item)
    {
        item.Validate();
        lock (sync)
        {
            if (closed) return Counts(QualificationReply.Ok() with { Status = 409 });
            Register(item); return Counts(QualificationReply.Ok() with { Status = 204 });
        }
    }
    // A page-canceled request whose continuation never reached the host leaves its exact
    // pending registration. Withdrawal only removes it; a used proof stays used.
    internal QualificationReply Withdraw(TransportAdmission item)
    {
        item.Validate();
        lock (sync)
        {
            if (pending.TryGetValue(item.Proof, out var held) && held == item) pending.Remove(item.Proof);
            return Counts(QualificationReply.Ok() with { Status = 204 });
        }
    }
    internal void WriteClosedObservation()
    {
        QualificationWire.Require(networkActive == 0 && closed && pending.Count == 0);
        var report = Counts(QualificationReply.Ok()) with { ProcessGeneration = input.Generation, ClosedObservation = true };
        string path = Path.Combine(input.Root, "closed-" + input.Generation + ".json"); PrivateQualificationFiles.RequireChild(input.Root, path);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(report, QualificationWire.Json);
        try
        {
            QualificationWire.Require(bytes.Length is > 0 and <= QualificationWire.Maximum);
            using var file = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            });
            file.Write(bytes);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
