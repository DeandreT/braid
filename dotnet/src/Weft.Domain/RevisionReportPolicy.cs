using System.Text.Json;

namespace Weft.Domain;

/// <summary>Immutable effective limits. Every reduction is part of report identity.</summary>
public sealed record RevisionReportPolicy
{
    public const int MaximumNativeByteLimit = 4194304;
    public const int MaximumWorkerResponseByteLimit = 8388608;
    public const int MaximumItemLimit = 8192;
    public const int MaximumResponseItemLimit = 32768;
    public const int MaximumJsonDepthLimit = 64;
    public const int MaximumDetailDepthLimit = 16;
    public const int MaximumDiagnosticLimit = 128;
    public const int MaximumValidationIssueLimit = 32768;
    public const int MaximumTextByteLimit = 2048;
    public const int MaximumHeaderByteLimit = 16384;
    public const int MaximumStoredObjectByteLimit = 16777216;
    public const int MaximumResponseByteLimit = 16777216;

    public RevisionReportPolicy(int nativeByteLimit = MaximumNativeByteLimit,
        int workerResponseByteLimit = MaximumWorkerResponseByteLimit, int itemLimit = MaximumItemLimit,
        int responseItemLimit = MaximumResponseItemLimit, int jsonDepthLimit = MaximumJsonDepthLimit,
        int detailDepthLimit = MaximumDetailDepthLimit, int diagnosticLimit = MaximumDiagnosticLimit,
        int validationIssueLimit = MaximumValidationIssueLimit, int textByteLimit = MaximumTextByteLimit,
        int headerByteLimit = MaximumHeaderByteLimit, int storedObjectByteLimit = MaximumStoredObjectByteLimit,
        int responseByteLimit = MaximumResponseByteLimit)
    {
        NativeByteLimit = Limit(nativeByteLimit, MaximumNativeByteLimit, nameof(nativeByteLimit));
        WorkerResponseByteLimit = Limit(workerResponseByteLimit, MaximumWorkerResponseByteLimit, nameof(workerResponseByteLimit));
        ItemLimit = Limit(itemLimit, MaximumItemLimit, nameof(itemLimit));
        ResponseItemLimit = Limit(responseItemLimit, MaximumResponseItemLimit, nameof(responseItemLimit));
        JsonDepthLimit = Limit(jsonDepthLimit, MaximumJsonDepthLimit, nameof(jsonDepthLimit));
        DetailDepthLimit = Limit(detailDepthLimit, MaximumDetailDepthLimit, nameof(detailDepthLimit));
        DiagnosticLimit = Limit(diagnosticLimit, MaximumDiagnosticLimit, nameof(diagnosticLimit));
        ValidationIssueLimit = Limit(validationIssueLimit, MaximumValidationIssueLimit, nameof(validationIssueLimit));
        TextByteLimit = Limit(textByteLimit, MaximumTextByteLimit, nameof(textByteLimit));
        HeaderByteLimit = Limit(headerByteLimit, MaximumHeaderByteLimit, nameof(headerByteLimit));
        StoredObjectByteLimit = Limit(storedObjectByteLimit, MaximumStoredObjectByteLimit, nameof(storedObjectByteLimit));
        ResponseByteLimit = Limit(responseByteLimit, MaximumResponseByteLimit, nameof(responseByteLimit));
    }

    public int NativeByteLimit { get; }
    public int WorkerResponseByteLimit { get; }
    public int ItemLimit { get; }
    public int ResponseItemLimit { get; }
    public int JsonDepthLimit { get; }
    public int DetailDepthLimit { get; }
    public int DiagnosticLimit { get; }
    public int ValidationIssueLimit { get; }
    public int TextByteLimit { get; }
    public int HeaderByteLimit { get; }
    public int StoredObjectByteLimit { get; }
    public int ResponseByteLimit { get; }
    public string SchemaVersion => "weft.revision-report-policy.v1";
    public Sha256Digest Fingerprint => Sha256Digest.FromBytes(CopyCanonicalUtf8());

    public byte[] CopyCanonicalUtf8()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
            WriteCanonical(writer);
        return buffer.ToArray();
    }

    internal void WriteCanonical(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("schemaVersion", SchemaVersion);
        writer.WriteNumber("nativeByteLimit", NativeByteLimit);
        writer.WriteNumber("workerResponseByteLimit", WorkerResponseByteLimit);
        writer.WriteNumber("itemLimit", ItemLimit);
        writer.WriteNumber("responseItemLimit", ResponseItemLimit);
        writer.WriteNumber("jsonDepthLimit", JsonDepthLimit);
        writer.WriteNumber("detailDepthLimit", DetailDepthLimit);
        writer.WriteNumber("diagnosticLimit", DiagnosticLimit);
        writer.WriteNumber("validationIssueLimit", ValidationIssueLimit);
        writer.WriteNumber("textByteLimit", TextByteLimit);
        writer.WriteNumber("headerByteLimit", HeaderByteLimit);
        writer.WriteNumber("storedObjectByteLimit", StoredObjectByteLimit);
        writer.WriteNumber("responseByteLimit", ResponseByteLimit);
        writer.WriteEndObject();
    }

    private static int Limit(int value, int maximum, string parameterName)
    {
        if (value < 1 || value > maximum)
            throw new ArgumentOutOfRangeException(parameterName, value, "A report limit must be positive and within its hard ceiling.");
        return value;
    }
}
