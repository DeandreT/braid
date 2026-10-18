namespace Weft.Server;

/// <summary>Stops new work and joins both the pipeline and its final framework completion callback.</summary>
internal sealed class SessionRequestGate
{
    private readonly object sync = new();
    private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int active;
    private bool closed;

    internal void Use(IApplicationBuilder app) => app.Use(InvokeAsync);

    private async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        Lease lease;
        lock (sync)
        {
            if (closed) { SessionHostTransport.Reject(context, 503); return; }
            active++;
            lease = new(this);
        }
        try
        {
            // Registered FIRST: Kestrel's LIFO completion runs downstream112 cleanup before this witness.
            context.Response.OnCompleted(() => { Finish(lease, framework: true); return Task.CompletedTask; });
        }
        catch
        {
            // No downstream work was entered if registration failed.
            Finish(lease, framework: true);
            Finish(lease, framework: false);
            throw;
        }
        try { await next(context); }
        finally { Finish(lease, framework: false); }
    }

    internal Task CloseAndDrain()
    {
        lock (sync)
        {
            closed = true;
            if (active == 0) drained.TrySetResult();
            return drained.Task;
        }
    }

    private void Finish(Lease lease, bool framework)
    {
        lock (sync)
        {
            if (framework) lease.FrameworkCompleted = true;
            else lease.PipelineEnded = true;
            if (!lease.Released && lease.FrameworkCompleted && lease.PipelineEnded)
            {
                lease.Released = true;
                active--;
                if (closed && active == 0) drained.TrySetResult();
            }
        }
    }

    private sealed class Lease(SessionRequestGate owner)
    {
        internal SessionRequestGate Owner { get; } = owner;
        internal bool FrameworkCompleted;
        internal bool PipelineEnded;
        internal bool Released;
    }
}
