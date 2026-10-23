using Weft.Storage.FileSystem;

namespace Weft.Worker;

/// <summary>
/// Each worker database credential comes either from its plain setting or from the protected private file named by
/// <c>NAME_FILE</c> (for example a <c>WEFT_DELIVER_FILE_*</c> delivery), never both. A file is read only when its
/// component asks for the credential, so a disabled component reads none. Every other setting passes through.
/// </summary>
internal static class WorkerCredentialSettings
{
    internal const string FileSuffix = "_FILE";
    private const int MaximumBytes = 16_384;

    internal static readonly string[] Connections =
    [
        "WEFT_PREVIEW_WRITER_CONNECTION_STRING",
        "WEFT_REGRESSION_RUN_WRITER_CONNECTION_STRING",
        "WEFT_REGRESSION_CASE_READER_CONNECTION_STRING",
        "WEFT_REGRESSION_ACCESS_CONNECTION_STRING",
        "WEFT_REGRESSION_REPORT_READER_CONNECTION_STRING",
        "WEFT_BUILD_DISPATCHER_CONNECTION_STRING",
        "WEFT_BUILD_WRITER_CONNECTION_STRING",
        "WEFT_BUILD_ACCESS_CONNECTION_STRING",
        "WEFT_BUILD_REPORT_READER_CONNECTION_STRING",
        "WEFT_BUILD_REGRESSION_RUN_READER_CONNECTION_STRING",
        "WEFT_BUILD_CASE_READER_CONNECTION_STRING"
    ];

    internal static Func<string, string?> Resolve(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return name =>
        {
            if (Array.IndexOf(Connections, name) < 0 || environment(name + FileSuffix) is not { } file) return environment(name);
            if (environment(name) is not null)
                throw new InvalidOperationException($"The worker requires {name} or {name}{FileSuffix}, not both.");
            // Admitted like the worker's certificate files: an owned regular single-link 0600 file under protected parents.
            return ProtectedHostFiles.ReadText(file, MaximumBytes, removeTerminalLf: true);
        };
    }
}
