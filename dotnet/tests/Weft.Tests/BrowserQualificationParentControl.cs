using Weft.BrowserQualification;

namespace Weft.Tests;

internal sealed partial class QualificationParentStopOwner
{
    private Task? controlConfirmation, controlClose;
    private QualificationSocketServer? controlServer;
    internal bool ControlFailed { get; private set; }
    internal Task ConfirmControlAsync(QualificationSocketServer? server)
    {
        lock (sync)
        {
            if (controlConfirmation is not null)
            {
                QualificationWire.Require(ReferenceEquals(controlServer, server)); return controlConfirmation;
            }
            controlServer = server; return controlConfirmation = ConfirmControlCoreAsync(server);
        }
    }
    private async Task ConfirmControlCoreAsync(QualificationSocketServer? server)
    {
        if (server is null) return; // no server was created; not a default receipt
        try { controlClose = server.CloseAsync(); await controlClose; }
        catch { ControlFailed = true; } // category retained; NOT settlement evidence
        ControlFailed |= server.Failed != 0;
        QualificationWire.Require(controlClose is { IsCompleted: true, IsCanceled: false } && server.ControlSettled);
    }
}
