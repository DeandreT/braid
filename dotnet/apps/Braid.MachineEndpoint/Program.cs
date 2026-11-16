using Microsoft.Extensions.Hosting;
using Braid.MachineEndpoint;

// Build-time OpenAPI generation runs this entry point inside its own tool and reads the document-only host.
if (MappingEndpointOpenApi.Generating)
{
    using var document = MappingEndpointOpenApi.BuildDocumentHost();
    await document.RunAsync();
    return 0;
}

// The only arguments the host accepts select its container health probe.
if (args.Length > 0) return await MappingEndpointProbe.RunAsync(args, Environment.GetEnvironmentVariable);

MappingEndpointSettings settings;
try { settings = MappingEndpointSettings.Read(Environment.GetEnvironmentVariable); }
catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
{
    // Settings never echo values; invalid or incomplete configuration stops the endpoint.
    Console.Error.WriteLine("BraidMappingEndpointConfigurationFailed");
    return 1;
}
return await MappingEndpointHost.RunAsync(settings);
