using Weft.MachineEndpoint;

MappingEndpointSettings settings;
try { settings = MappingEndpointSettings.Read(Environment.GetEnvironmentVariable); }
catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
{
    // Settings never echo values; invalid or incomplete configuration stops the endpoint.
    Console.Error.WriteLine("WeftMappingEndpointConfigurationFailed");
    return 1;
}
return await MappingEndpointHost.RunAsync(settings);
