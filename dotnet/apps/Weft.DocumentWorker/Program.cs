using Weft.Application;

// No paths, URLs, layouts or execution modes are accepted. Host qualification is external to this executable.
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
try
{
    if (args.Length != 0) throw new SpecOfficeProtocolException();
    var input = await SpecOfficeProcessProtocol.ReadBoundedAsync(Console.OpenStandardInput(), SpecOfficeProcessProtocol.MaximumRequestBytes, deadline.Token);
    var request = SpecOfficeProcessProtocol.DecodeRequest(input);
    var result = SpecOfficeParser.Extract(request.Source, request.Limits, deadline.Token);
    var output = SpecOfficeProcessProtocol.EncodeResponse(request.Source, result);
    await Console.OpenStandardOutput().WriteAsync(output, deadline.Token);
    return 0;
}
catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
{
    // A fixed refusal also lets the sandbox host qualify the actual .NET entrypoint without sending private bytes.
    await Console.OpenStandardError().WriteAsync("WeftDocumentWorkerFailed:decode\n"u8.ToArray());
    return 1;
}
