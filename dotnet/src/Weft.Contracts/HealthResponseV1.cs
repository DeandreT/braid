namespace Weft.Contracts;

public sealed record HealthResponseV1(string Status, string Service, string Version, string ApiVersion);
