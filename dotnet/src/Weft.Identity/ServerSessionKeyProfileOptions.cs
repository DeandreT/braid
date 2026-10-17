using System.Text;
using System.Text.Json.Serialization;

namespace Weft.Identity;

/// <summary>Explicit durable realm configuration; no directory or application fallback.</summary>
public sealed class ServerSessionKeyProfileOptions
{
    public ServerSessionKeyProfileOptions(Guid organizationId, string applicationName, string keyDirectory)
    {
        try
        {
            if (organizationId == Guid.Empty || string.IsNullOrEmpty(applicationName) ||
                string.IsNullOrEmpty(keyDirectory))
                throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);

            // Strict UTF-8 rejects unpaired surrogates rather than replacing configuration text.
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            _ = utf8.GetByteCount(keyDirectory);
            if (utf8.GetByteCount(applicationName) > 128 || applicationName.Any(char.IsControl) ||
                !Path.IsPathFullyQualified(keyDirectory) ||
                (keyDirectory.Length > 1 && Path.EndsInDirectorySeparator(keyDirectory)) ||
                !string.Equals(Path.GetFullPath(keyDirectory), keyDirectory, StringComparison.Ordinal))
                throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);

            OrganizationId = organizationId;
            ApplicationName = applicationName;
            KeyDirectory = keyDirectory;
        }
        catch (ServerSessionIdentityException) { throw; }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException)
        {
            throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);
        }
    }

    [JsonIgnore]
    public Guid OrganizationId { get; }
    [JsonIgnore]
    public string ApplicationName { get; }
    [JsonIgnore]
    public string KeyDirectory { get; }
    public override string ToString() => nameof(ServerSessionKeyProfileOptions);
}
