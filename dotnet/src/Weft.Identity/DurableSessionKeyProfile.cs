using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Weft.Identity;

/// <summary>
/// An admitted, certificate-backed existing key ring. The caller owns the certificates and
/// this profile's lifetime; ticket stores borrow it and must finish before it is disposed.
/// </summary>
public sealed partial class DurableSessionKeyProfile : IDisposable
{
    private readonly ServiceProvider services;
    private readonly SessionKeyDirectory directory;
    private readonly IDataProtector protector;
    private int disposed;

    private DurableSessionKeyProfile(Guid organizationId, ServiceProvider services, SessionKeyDirectory directory,
        IDataProtector protector)
    {
        OrganizationId = organizationId;
        this.services = services;
        this.directory = directory;
        this.protector = protector;
    }

    internal Guid OrganizationId { get; }

    public static DurableSessionKeyProfile Open(ServerSessionKeyProfileOptions options,
        X509Certificate2 currentCertificate, IReadOnlyCollection<X509Certificate2> overlapDecryptCertificates)
    {
        var startupErrors = new StartupErrorLatch();
        ServiceProvider? services = null;
        byte[]? protectedProbe = null;
        byte[]? unprotectedProbe = null;
        try
        {
            RequireOptions(options);
            var certificates = CopyCertificates(currentCertificate, overlapDecryptCertificates);
            var directory = SessionKeyDirectory.Admit(options.KeyDirectory);
            services = Compose(options, currentCertificate, certificates, startupErrors);
            var keys = services.GetRequiredService<IKeyManager>().GetAllKeys();
            startupErrors.ThrowIfError();
            var admitted = 0;
            foreach (var key in keys)
            {
                if (key.IsRevoked)
                    continue;
                // Force standard descriptor decryption for every retained nonrevoked key,
                // including expired/future keys, before ANY Protect can cause rotation.
                if (key.CreateEncryptor() is null)
                    throw ConfigurationFailure();
                admitted++;
            }
            startupErrors.ThrowIfError();
            if (admitted == 0)
                throw ConfigurationFailure();
            directory.Revalidate();
            var protector = services.GetRequiredService<IDataProtectionProvider>().CreateProtector(
                "Weft.ServerSessionTicket", "codec:1", "policy:1",
                "configured-organization:" + options.OrganizationId.ToString("D"));
            ReadOnlySpan<byte> probe = "Weft.ServerSessionTicket.Profile.v1"u8;
            protectedProbe = protector.Protect(probe.ToArray());
            unprotectedProbe = protector.Unprotect(protectedProbe);
            if (!CryptographicOperations.FixedTimeEquals(probe, unprotectedProbe))
                throw ConfigurationFailure();
            directory.Revalidate();
            startupErrors.ThrowIfError();
            startupErrors.Dispose();
            var result = new DurableSessionKeyProfile(options.OrganizationId, services, directory, protector);
            services = null;
            return result;
        }
        catch (Exception error) when (IsStartupConfigurationError(error, startupErrors))
        {
            throw ConfigurationFailure();
        }
        finally
        {
            startupErrors.Dispose();
            if (protectedProbe is not null)
                CryptographicOperations.ZeroMemory(protectedProbe);
            if (unprotectedProbe is not null)
                CryptographicOperations.ZeroMemory(unprotectedProbe);
            if (services is not null)
                DisposeComposition(services, ServerSessionIdentityFailureKind.ConfigurationFailure);
        }
    }

    public static DurableSessionKeyProfile BootstrapEmpty(ServerSessionKeyProfileOptions options,
        X509Certificate2 currentCertificate, IReadOnlyCollection<X509Certificate2> overlapDecryptCertificates,
        DateTimeOffset activationUtc, DateTimeOffset expirationUtc)
    {
        try
        {
            RequireOptions(options);
            if (!IsKeyInstant(activationUtc) || !IsKeyInstant(expirationUtc) || expirationUtc <= activationUtc)
                throw ConfigurationFailure();
            var certificates = CopyCertificates(currentCertificate, overlapDecryptCertificates);
            var directory = SessionKeyDirectory.Admit(options.KeyDirectory);
            if (!directory.IsEmpty())
                throw ConfigurationFailure();
            // The trusted owner coordinates exclusive bootstrap. There is no HTTP command,
            // missing-ring recovery, directory creation, or permission repair here.
            using (var startupErrors = new StartupErrorLatch())
            using (var temporary = Compose(options, currentCertificate, certificates, startupErrors))
            {
                if (!directory.IsEmpty())
                    throw ConfigurationFailure();
                if (temporary.GetRequiredService<IKeyManager>().CreateNewKey(activationUtc, expirationUtc) is null)
                    throw ConfigurationFailure();
                directory.Revalidate();
            }
            return Open(options, currentCertificate, certificates[1..]);
        }
        catch (Exception error) when (IsConfigurationError(error))
        {
            throw ConfigurationFailure();
        }
    }

    internal byte[] Protect(byte[] plaintext) => Transform(plaintext, unprotect: false);
    internal byte[] Unprotect(byte[] ciphertext) => Transform(ciphertext, unprotect: true);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
            DisposeComposition(services, ServerSessionIdentityFailureKind.Unavailable);
    }

    public override string ToString() => nameof(DurableSessionKeyProfile);

    private byte[] Transform(byte[] input, bool unprotect, IDataProtector? selectedProtector = null)
    {
        byte[]? output = null;
        try
        {
            if (Volatile.Read(ref disposed) != 0)
                throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.Unavailable);
            directory.Revalidate();
            var selected = selectedProtector ?? protector;
            output = unprotect ? selected.Unprotect(input) : selected.Protect(input);
            directory.Revalidate();
            var result = output;
            output = null;
            return result;
        }
        catch (CryptographicException)
        {
            throw ServerSessionIdentityErrors.Create(unprotect ? ServerSessionIdentityFailureKind.IntegrityFailure
                : ServerSessionIdentityFailureKind.Unavailable);
        }
        catch (Exception error) when (IsConfigurationError(error))
        {
            throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.Unavailable);
        }
        finally
        {
            if (output is not null)
                CryptographicOperations.ZeroMemory(output);
        }
    }

    private static ServiceProvider Compose(ServerSessionKeyProfileOptions options, X509Certificate2 current,
        X509Certificate2[] certificates, StartupErrorLatch startupErrors)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.ClearProviders()
            .SetMinimumLevel(LogLevel.Error).AddProvider(startupErrors));
        services.AddDataProtection().SetApplicationName(options.ApplicationName)
            .PersistKeysToFileSystem(new DirectoryInfo(options.KeyDirectory))
            .ProtectKeysWithCertificate(current)
            .UnprotectKeysWithAnyCertificate(certificates);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static X509Certificate2[] CopyCertificates(X509Certificate2 current,
        IReadOnlyCollection<X509Certificate2> overlap)
    {
        if (current is null || overlap is null || !current.HasPrivateKey)
            throw ConfigurationFailure();
        var copied = overlap.ToArray();
        if (copied.Any(certificate => certificate is null || !certificate.HasPrivateKey))
            throw ConfigurationFailure();
        return [current, .. copied];
    }

    private static void RequireOptions(ServerSessionKeyProfileOptions options)
    {
        if (options is null)
            throw ConfigurationFailure();
    }

    // Key-management configuration is not persisted session time. Do not impose the
    // session database's microsecond precision on standard Data Protection key dates.
    private static bool IsKeyInstant(DateTimeOffset instant) => instant.Offset == TimeSpan.Zero &&
        instant != DateTimeOffset.MinValue && instant != DateTimeOffset.MaxValue;

    private static void DisposeComposition(ServiceProvider composition, ServerSessionIdentityFailureKind kind)
    {
        try { composition.Dispose(); }
        catch (Exception error) when (IsConfigurationError(error))
        {
            throw ServerSessionIdentityErrors.Create(kind);
        }
    }

    private static bool IsStartupConfigurationError(Exception error, StartupErrorLatch startupErrors) =>
        IsConfigurationError(error) || (startupErrors.HasError && error is not OutOfMemoryException and
            not StackOverflowException and not AccessViolationException);

    private static bool IsConfigurationError(Exception error) => error is ServerSessionIdentityException or
        IOException or UnauthorizedAccessException or CryptographicException or ArgumentException or
        InvalidOperationException or NotSupportedException or FormatException or OverflowException or
        System.Xml.XmlException or System.Security.SecurityException or
        System.Runtime.InteropServices.ExternalException or TypeLoadException;

    private static ServerSessionIdentityException ConfigurationFailure() =>
        ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);

    // This composition owns no output providers. Observe only a closed startup state;
    // never render or retain log state, exceptions, event IDs, categories, or key data.
    private sealed class StartupErrorLatch : ILoggerProvider, ILogger
    {
        // 1: admitting, 2: failed, 0: disabled permanently for this composition.
        private int state = 1;

        internal bool HasError => Volatile.Read(ref state) == 2;

        internal void ThrowIfError()
        {
            if (HasError)
                throw ConfigurationFailure();
        }

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => Volatile.Read(ref state) != 0 &&
            (logLevel is LogLevel.Error or LogLevel.Critical);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel is LogLevel.Error or LogLevel.Critical)
                Interlocked.CompareExchange(ref this.state, 2, 1);
        }

        public void Dispose() => Interlocked.Exchange(ref state, 0);
    }
}
