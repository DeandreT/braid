namespace Braid.Tests.AuditTestSupport;

internal static class AuditFixtureCleanup
{
    // A failed cleanliness assertion must not skip disposal of the issuer or stub.
    internal static async Task RunAsync(params Func<Task>[] steps)
    {
        List<Exception> errors = [];
        foreach (var step in steps)
        {
            try { await step(); }
            catch (Exception error) { errors.Add(error); }
        }
        if (errors.Count != 0) throw new AggregateException(errors);
    }
}
