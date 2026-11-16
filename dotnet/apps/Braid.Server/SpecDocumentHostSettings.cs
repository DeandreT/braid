using Braid.Application;
using Braid.Domain;

namespace Braid.Server;

internal sealed record SpecDocumentHostSettings(string OfficeAssembly, string OfficeBuild, string OfficeRuntime,
    string OfficeLauncher, string DotnetRoot, string OfficeWorkRoot, string PdfExecutable, string PdfWorkRoot,
    SpecOfficeExtractionLimits OfficeLimits, SpecPdfExtractionProfile PdfProfile)
{
    internal static SpecDocumentHostSettings? Read(Func<string, string?> read)
    {
        var mode = read("BRAID_SPEC_DOCUMENT_WORKERS");
        if (mode is null or "disabled") return null;
        if (mode != "optional") throw ServerHostOptions.Failure();
        string Required(string suffix) => ServerHostOptions.Required(read, "BRAID_SPEC_DOCUMENT_" + suffix);
        Sha256Digest Digest(string suffix)
        {
            var value = Required(suffix);
            var digest = Sha256Digest.Parse(value);
            if (value != digest.Value) throw ServerHostOptions.Failure();
            return digest;
        }
        var limits = new SpecOfficeExtractionLimits();
        if (Digest("OFFICE_PROFILE_SHA256") != limits.Digest) throw ServerHostOptions.Failure();
        var pdf = new SpecPdfExtractionProfile(Digest("PDF_BUILD_SHA256"));
        if (Digest("PDF_PROFILE_SHA256") != pdf.Digest) throw ServerHostOptions.Failure();
        return new(UnixPath(Required("OFFICE_ASSEMBLY")), "sha256:" + Digest("OFFICE_BUILD_SHA256").Value,
            "sha256:" + Digest("OFFICE_RUNTIME_SHA256").Value, UnixPath(Required("OFFICE_LAUNCHER")),
            UnixPath(Required("DOTNET_ROOT")), UnixPath(Required("OFFICE_WORK_ROOT")),
            UnixPath(Required("PDF_EXECUTABLE")), UnixPath(Required("PDF_WORK_ROOT")), limits, pdf);
    }

    // Platform-independent syntax validation lets Windows reject malformed settings without touching configured paths.
    internal static string UnixPath(string value)
    {
        if (!value.StartsWith('/') || value == "/" || value.EndsWith('/') || value.Contains('\\') ||
            value.Any(char.IsControl) || value.Split('/').Skip(1).Any(p => p is "" or "." or ".."))
            throw ServerHostOptions.Failure();
        return value;
    }
}
