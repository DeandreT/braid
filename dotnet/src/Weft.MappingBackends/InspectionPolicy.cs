namespace Weft.MappingBackends;

/// <summary>Server-owned admission limits; mapping inputs cannot increase these ceilings.</summary>
public static class InspectionPolicy
{
    public const int MaximumHeaderBytes = 16 * 1024;
    public const int MaximumNativeBytes = 4 * 1024 * 1024;
    public const int MaximumResponseBytes = 8 * 1024 * 1024;
    public const int MaximumDiagnostics = 128;
    public const int MaximumValidationIssues = 32768;
    public const int MaximumItems = 8192;
    public const int MaximumResponseItems = 4 * MaximumItems;
    public const int MaximumTextBytes = 2048;
    public const int MaximumJsonDepth = 64;
    public const int MaximumProjectionDetailDepth = 16;
    public const string DocumentId = "entry.native.json";
}
