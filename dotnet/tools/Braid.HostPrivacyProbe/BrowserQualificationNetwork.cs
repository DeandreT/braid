using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Braid.BrowserQualification;

namespace Braid.HostPrivacyProbe;

internal sealed partial class BrowserQualificationAdmission
{
    private TaskCompletionSource networkIdle = CompletedIdle();
    private int networkActive, networkRequests, networkPreflights, networkAborts;
    private bool networkArmed, networkUsed;
    private static TaskCompletionSource CompletedIdle()
    { var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); result.SetResult(); return result; }
    // Qualification lifetime only; no logging/inspection/echo of the proof. Remains
    // outside strip gate so refused/aborted ORIGINAL framework callbacks are owned too.
    internal async Task NetworkLifetimeAsync(HttpContext context, RequestDelegate next)
    {
        lock (sync)
        {
            if (networkActive++ == 0) networkIdle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            networkRequests++; if (context.Request.Method == "OPTIONS") networkPreflights++;
        }
        bool pipeline = false, framework = false, released = false;
        void Finish(bool complete)
        {
            lock (sync)
            {
                if (complete) framework = true; else pipeline = true;
                if (framework && pipeline && !released) { released = true; if (--networkActive == 0) networkIdle.TrySetResult(); }
            }
        }
        try { context.Response.OnCompleted(() => { Finish(true); return Task.CompletedTask; }); }
        catch { Finish(true); Finish(false); throw; }
        try { await next(context); } finally { Finish(false); }
    }
    internal Task NetworkBarrierAsync() { lock (sync) return networkIdle.Task; }
    internal void NetworkBegin()
    {
        lock (sync)
        {
            QualificationWire.Require(!closed && input.NetworkCase is not null && QualificationNetwork.Cases.Contains(input.NetworkCase) && !networkArmed && !networkUsed && networkActive == 0 && pending.Count == 0);
            networkArmed = true;
        }
    }
    internal void NetworkRevoke()
    { lock (sync) { QualificationWire.Require(input.NetworkCase == "detach" && networkArmed && !closed); pending.Clear(); } }
    private void NetworkAbort() { lock (sync) networkAborts++; }
    private void NetworkResponse(HttpContext context, TransportAdmission admitted)
    {
        lock (sync)
        {
            if (!networkArmed || networkUsed || !QualificationNetwork.ResponseCases.Contains(input.NetworkCase ?? "") || admitted.RawTarget != "/sign-in" || admitted.Method != "GET" || admitted.Destination != "document") return;
            networkUsed = true;
        }
        // Registered AFTER the outer gate but BEFORE next: mutate only this actual
        // harmless selected response, before the outer final-header gate inspects it.
        context.Response.OnStarting(() =>
        {
            QualificationWire.Require(context.Response.StatusCode == 200);
            if (input.NetworkCase != "refresh") context.Response.StatusCode = 303;
            var headers = context.Response.Headers;
            switch (input.NetworkCase)
            {
                case "location-host": headers.Location = "https://braid-inert.invalid/"; break;
                case "location-ip": headers.Location = "https://192.0.2.1/"; break;
                case "location-port": headers.Location = "https://127.0.0.1:" + (new Uri(input.Origin).Port == 44444 ? 44445 : 44444) + "/"; break;
                case "location-http": headers.Location = "http://192.0.2.1/"; break;
                case "location-file": headers.Location = "file:///braid-inert"; break;
                case "location-document": headers.Location = "/api/auth/session"; break;
                case "location-duplicate": headers.Location = new StringValues(["/", "/sign-in"]); break;
                case "refresh": headers["Refresh"] = "0;url=/sign-in"; break;
                default: throw new QualificationFailure();
            }
            return Task.CompletedTask;
        });
    }
    private QualificationReply NetworkCounts(QualificationReply reply) => reply with
    {
        NetworkRequests = networkRequests,
        NetworkPreflights = networkPreflights,
        NetworkAborts = networkAborts,
        NetworkActive = networkActive,
        ProofPending = pending.Count,
        ProofUsed = used.Count,
        NetworkArmed = networkArmed,
        AdmissionSealed = closed
    };
}
