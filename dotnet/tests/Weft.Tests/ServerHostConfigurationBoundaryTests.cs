using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Npgsql;
using Weft.Api.Auth;
using Weft.Server;

namespace Weft.Tests;

[SupportedOSPlatform("linux")]
public sealed class ServerHostConfigurationBoundaryTests
{
    [Fact]
    public void ScalarModeSelectionNeverInfersSessionAndRetainsLegacyDisabledAndDevelopment()
    {
        var values = new Dictionary<string, string?>();
        ServerHostCheck.That(ServerHostOptions.Read(Read).Mode == AuthenticationHttpMode.Disabled,
            "Absent host mode no longer defaults to disabled.");
        values["WEFT_CATALOG_ENABLED"] = "true";
        values["WEFT_DEV_BROWSER_ORIGIN"] = "http://127.0.0.1:4100";
        ServerHostCheck.That(ServerHostOptions.Read(Read).Mode == AuthenticationHttpMode.Development,
            "Legacy enabled catalog inferred Session instead of Development.");
        values["WEFT_CATALOG_AUTH_MODE"] = "session";
        bool rejected = false; try { _ = ServerHostOptions.Read(Read); } catch (Exception) { rejected = true; }
        ServerHostCheck.That(rejected, "Incomplete Session selected a fallback configuration.");
        string? Read(string name) => values.GetValueOrDefault(name);
    }

    [Theory]
    [InlineData("true ")]
    [InlineData("True")]
    [InlineData("1")]
    [InlineData("")]
    public void ScalarBooleansAreExact(string value)
    {
        bool rejected = false;
        try { _ = ServerHostOptions.Flag(name => name == "owned" ? value : null, "owned"); }
        catch (Exception) { rejected = true; }
        ServerHostCheck.That(rejected, "Host accepted a noncanonical enablement scalar.");
    }

    [Theory]
    [InlineData("WEFT_METADATA_EDITS_ENABLED", "True")]
    [InlineData("WEFT_REVISION_INGESTION_ENABLED", "true ")]
    [InlineData("WEFT_PROJECT_CREATION_ENABLED", "1")]
    [InlineData("WEFT_REVISION_REPORTS_ENABLED", "")]
    [InlineData("WEFT_REVISION_REPORT_DERIVATION_ENABLED", "TRUE")]
    [InlineData("WEFT_REVISION_REPORT_DERIVATION_ENABLED", "true")]
    [InlineData("WEFT_NATIVE_BYTE_LIMIT", "01")]
    [InlineData("WEFT_DERIVED_READ_BYTE_LIMIT", "0")]
    [InlineData("WEFT_CHAT_ENABLED", "True")]
    [InlineData("WEFT_CHAT_ENABLED", "")]
    public void SessionFeatureScalarsAreRejectedBeforeAnySensitiveDeliveryIsRequested(string name, string value)
    {
        var values = SessionScalars();
        if (name == "WEFT_NATIVE_BYTE_LIMIT")
        {
            values["WEFT_REVISION_INGESTION_ENABLED"] = "true";
            foreach (var pair in InspectionWorkerScalars()) values[pair.Key] = pair.Value;
        }
        if (name == "WEFT_DERIVED_READ_BYTE_LIMIT") values["WEFT_REVISION_REPORTS_ENABLED"] = "true";
        int privateReads = 0;
        _ = ServerHostOptions.Read(Read); // Adjacent valid scalar control; no sensitive delivery exists.
        values[name] = value;
        bool rejected = false;
        try { _ = ServerHostOptions.Read(Read); } catch (Exception) { rejected = true; }
        ServerHostCheck.That(rejected && privateReads == 0,
            "An invalid selected feature scalar reached sensitive delivery or escaped scalar admission.");
        string? Read(string key)
        {
            if (key.EndsWith("_FILE", StringComparison.Ordinal)) privateReads++;
            return values.GetValueOrDefault(key);
        }
    }

    [Fact]
    public void SessionFeatureOptionsRetainOneParsedSnapshotAndDormantLimitsStayDormant()
    {
        var values = SessionScalars();
        values["WEFT_NATIVE_BYTE_LIMIT"] = "invalid-dormant";
        values["WEFT_DERIVED_READ_BYTE_LIMIT"] = "invalid-dormant";
        var dormant = ServerHostOptions.Read(values.GetValueOrDefault);
        ServerHostCheck.That(!dormant.Ingestion.Enabled && !dormant.Reports.Enabled
            && dormant.NativeByteLimit is null && dormant.DerivedReadByteLimit is null,
            "A dormant business limit became an active Session resource dependency.");
        values["WEFT_INSPECTION_WORKER_URL"] = "invalid-dormant";
        ServerHostCheck.That(ServerHostOptions.Read(values.GetValueOrDefault).InspectionWorker is null,
            "Dormant hosted inspection settings became a Session dependency.");
        values["WEFT_METADATA_EDITS_ENABLED"] = "true"; values["WEFT_REVISION_INGESTION_ENABLED"] = "true";
        values["WEFT_PROJECT_CREATION_ENABLED"] = "true"; values["WEFT_REVISION_REPORTS_ENABLED"] = "true";
        values["WEFT_REVISION_REPORT_DERIVATION_ENABLED"] = "true";
        values["WEFT_NATIVE_BYTE_LIMIT"] = "1024"; values["WEFT_DERIVED_READ_BYTE_LIMIT"] = "2048";
        foreach (var pair in InspectionWorkerScalars()) values[pair.Key] = pair.Value;
        var admitted = ServerHostOptions.Read(values.GetValueOrDefault);
        values["WEFT_METADATA_EDITS_ENABLED"] = "invalid-after-admission";
        values["WEFT_NATIVE_BYTE_LIMIT"] = "invalid-after-admission";
        values["WEFT_INSPECTION_WORKER_URL"] = "invalid-after-admission";
        ServerHostCheck.That(admitted.Metadata.Enabled && admitted.Ingestion.Enabled && admitted.Creation.Enabled
            && admitted.Reports.Enabled && admitted.Reports.DerivationEnabled
            && admitted.NativeByteLimit == 1024 && admitted.DerivedReadByteLimit == 2048
            && admitted.InspectionWorker?.Endpoint == new Uri("https://10.0.0.4:8443"),
            "Session lost its admitted feature-scalar snapshot.");
    }

    [Theory]
    [InlineData("WEFT_REVISION_INGESTION_ENABLED", "WEFT_INSPECTION_WORKER_URL", null)]
    [InlineData("WEFT_REVISION_INGESTION_ENABLED", "WEFT_INSPECTION_WORKER_URL", "http://10.0.0.4:8443")]
    [InlineData("WEFT_REVISION_INGESTION_ENABLED", "WEFT_INSPECTION_WORKER_URL", "https://10.0.0.4:8443/")]
    [InlineData("WEFT_REVISION_INGESTION_ENABLED", "WEFT_INSPECTION_WORKER_URL", "https://user@10.0.0.4:8443")]
    [InlineData("WEFT_REVISION_INGESTION_ENABLED", "WEFT_INSPECTION_WORKER_CERTIFICATE_SHA256", null)]
    [InlineData("WEFT_REVISION_INGESTION_ENABLED", "WEFT_INSPECTION_WORKER_CERTIFICATE_SHA256", "ABCDEF")]
    [InlineData("WEFT_REVISION_INGESTION_ENABLED", "WEFT_FERRULE_WORKER_IMAGE", null)]
    [InlineData("WEFT_REVISION_INGESTION_ENABLED", "WEFT_FERRULE_WORKER_IMAGE", "weft-ferrule:latest")]
    [InlineData("WEFT_REVISION_REPORT_DERIVATION_ENABLED", "WEFT_INSPECTION_WORKER_URL", null)]
    public void SessionNativeWorkRequiresTheHostedWorkerBeforeAnyPrivateDelivery(string feature, string name, string? value)
    {
        var values = SessionScalars();
        values[feature] = "true";
        if (feature == "WEFT_REVISION_REPORT_DERIVATION_ENABLED") values["WEFT_REVISION_REPORTS_ENABLED"] = "true";
        foreach (var pair in InspectionWorkerScalars()) values[pair.Key] = pair.Value;
        int privateReads = 0;
        _ = ServerHostOptions.Read(Read);
        values[name] = value;
        bool rejected = false;
        try { _ = ServerHostOptions.Read(Read); } catch (Exception) { rejected = true; }
        ServerHostCheck.That(rejected && privateReads == 0,
            "Session native work admitted an incomplete hosted worker or read a delivery first.");
        string? Read(string key)
        {
            if (key.EndsWith("_FILE", StringComparison.Ordinal)) privateReads++;
            return values.GetValueOrDefault(key);
        }
    }

    [Fact]
    public void SessionNativeWorkSelectsTheFerruleServiceOnlyExplicitlyAndUnambiguously()
    {
        var values = SessionScalars();
        values["WEFT_REVISION_INGESTION_ENABLED"] = "true";
        foreach (var pair in FerruleServiceScalars()) values[pair.Key] = pair.Value;
        int privateReads = 0;
        var admitted = ServerHostOptions.Read(Read);
        ServerHostCheck.That(admitted.FerruleService?.Endpoint == new Uri("https://10.0.0.5:8443") && admitted.InspectionWorker is null
            && admitted.FerruleService.BuildIdentity == "sha256:" + new string('b', 64) && privateReads == 0,
            "Session did not select the Ferrule service from its explicit scalars.");

        foreach (var (name, value) in new (string, string?)[]
        {
            ("WEFT_FERRULE_RUNNER", "docker"), ("WEFT_FERRULE_RUNNER", "Service"), ("WEFT_FERRULE_RUNNER", "azure"),
            ("WEFT_FERRULE_SERVICE_URL", null), ("WEFT_FERRULE_SERVICE_URL", "http://10.0.0.5:8443"),
            ("WEFT_FERRULE_SERVICE_CERTIFICATE_SHA256", "ABCDEF"), ("WEFT_FERRULE_WORKER_IMAGE", null),
            // A second runner's settings beside the selected one are ambiguous.
            ("WEFT_INSPECTION_WORKER_URL", "https://10.0.0.4:8443")
        })
        {
            var copy = new Dictionary<string, string?>(values) { [name] = value };
            bool rejected = false;
            try { _ = ServerHostOptions.Read(key => Count(copy, key)); } catch (Exception) { rejected = true; }
            ServerHostCheck.That(rejected && privateReads == 0, "Session admitted an invalid or ambiguous Ferrule service selection: " + name + ".");
        }

        // The hosted worker remains the default, and service scalars beside it are ambiguous.
        var worker = SessionScalars();
        worker["WEFT_REVISION_INGESTION_ENABLED"] = "true";
        foreach (var pair in InspectionWorkerScalars()) worker[pair.Key] = pair.Value;
        ServerHostCheck.That(ServerHostOptions.Read(worker.GetValueOrDefault).InspectionWorker is not null, "The hosted worker stopped being the default.");
        worker["WEFT_FERRULE_SERVICE_URL"] = "https://10.0.0.5:8443";
        bool ambiguous = false;
        try { _ = ServerHostOptions.Read(worker.GetValueOrDefault); } catch (Exception) { ambiguous = true; }
        ServerHostCheck.That(ambiguous, "Session admitted Ferrule service scalars beside the hosted worker.");

        // A dormant selection never becomes a dependency.
        var dormant = SessionScalars();
        dormant["WEFT_FERRULE_RUNNER"] = "invalid-dormant";
        ServerHostCheck.That(ServerHostOptions.Read(dormant.GetValueOrDefault).FerruleService is null, "A dormant runner selection was admitted.");

        string? Read(string key) => Count(values, key);
        string? Count(Dictionary<string, string?> source, string key)
        {
            if (key.EndsWith("_FILE", StringComparison.Ordinal)) privateReads++;
            return source.GetValueOrDefault(key);
        }
    }

    [Fact]
    public void SessionNativeWorkSelectsDynamicSessionsOnlyExplicitlyAndUnambiguously()
    {
        var values = SessionScalars();
        values["WEFT_REVISION_REPORTS_ENABLED"] = "true";
        values["WEFT_REVISION_REPORT_DERIVATION_ENABLED"] = "true";
        values["WEFT_FERRULE_RUNNER"] = "azureContainerAppsSessions";
        values["WEFT_FERRULE_SESSIONS_POOL_ENDPOINT"] = "https://weft-ferrule.env.westus2.azurecontainerapps.io";
        values["WEFT_FERRULE_WORKER_IMAGE"] = "sha256:" + new string('b', 64);
        var admitted = ServerHostOptions.Read(values.GetValueOrDefault);
        ServerHostCheck.That(admitted.FerruleSessions?.PoolManagementEndpoint == new Uri("https://weft-ferrule.env.westus2.azurecontainerapps.io")
            && admitted.FerruleService is null && admitted.InspectionWorker is null && admitted.FerruleSessions.ManagedIdentityClientId is null,
            "Session did not select dynamic sessions from its explicit scalars.");
        foreach (var (name, value) in new (string, string?)[]
        {
            ("WEFT_FERRULE_SESSIONS_POOL_ENDPOINT", null), ("WEFT_FERRULE_SESSIONS_POOL_ENDPOINT", "https://10.0.0.6"),
            ("WEFT_FERRULE_SESSIONS_MANAGED_IDENTITY_CLIENT_ID", "not-a-guid"),
            ("WEFT_FERRULE_SERVICE_URL", "https://10.0.0.5:8443"), ("WEFT_INSPECTION_WORKER_URL", "https://10.0.0.4:8443")
        })
        {
            var copy = new Dictionary<string, string?>(values) { [name] = value };
            bool rejected = false;
            try { _ = ServerHostOptions.Read(copy.GetValueOrDefault); } catch (Exception) { rejected = true; }
            ServerHostCheck.That(rejected, "Session admitted an invalid or ambiguous dynamic-sessions selection: " + name + ".");
        }
    }

    private static Dictionary<string, string?> FerruleServiceScalars() => new(StringComparer.Ordinal)
    {
        ["WEFT_FERRULE_RUNNER"] = "service",
        ["WEFT_FERRULE_SERVICE_URL"] = "https://10.0.0.5:8443",
        ["WEFT_FERRULE_SERVICE_CERTIFICATE_SHA256"] = new string('a', 64),
        ["WEFT_FERRULE_WORKER_IMAGE"] = "sha256:" + new string('b', 64)
    };

    private static Dictionary<string, string?> InspectionWorkerScalars() => new(StringComparer.Ordinal)
    {
        ["WEFT_INSPECTION_WORKER_URL"] = "https://10.0.0.4:8443",
        ["WEFT_INSPECTION_WORKER_CERTIFICATE_SHA256"] = new string('a', 64),
        ["WEFT_FERRULE_WORKER_IMAGE"] = "sha256:" + new string('b', 64)
    };

    [Theory]
    [InlineData("missing-user")]
    [InlineData("empty-user")]
    [InlineData("missing-password")]
    [InlineData("empty-password")]
    [InlineData("passfile")]
    [InlineData("empty-passfile")]
    [InlineData("client-certificate")]
    [InlineData("client-key")]
    [InlineData("client-key-password")]
    [InlineData("gss-encryption")]
    [InlineData("gss-authentication")]
    [InlineData("trust-authentication")]
    [InlineData("empty-authentication")]
    [InlineData("malformed")]
    public void DeliveredConnectionsRequireExplicitPasswordAuthenticationAndRefuseAlternates(string kind)
    {
        using var files = new ServerHostFiles(); var connection = PasswordConnection();
        switch (kind)
        {
            case "missing-user": connection.Remove("Username"); break;
            case "empty-user": connection.Username = ""; break;
            case "missing-password": connection.Remove("Password"); break;
            case "empty-password": connection.Password = ""; break;
            case "passfile": connection.Passfile = "/owned-unopened-passfile"; break;
            case "empty-passfile": connection.Passfile = ""; break;
            case "client-certificate": connection.SslCertificate = "/owned-unopened-client.pem"; break;
            case "client-key": connection.SslKey = "/owned-unopened-client.key"; break;
            case "client-key-password": connection.SslPassword = "owned-unused-key-password"; break;
            case "gss-encryption": connection.GssEncryptionMode = GssEncryptionMode.Require; break;
            case "gss-authentication": connection.RequireAuth = "GSS"; break;
            case "trust-authentication": connection.RequireAuth = "None"; break;
            case "empty-authentication": connection.RequireAuth = ""; break;
        }
        string delivered = kind == "malformed" ? "owned-private-unsupported-key=owned-private-value" : connection.ConnectionString;
        string path = files.Text("rejected-connection.txt", delivered + "\n");
        bool rejected = false;
        try { _ = SessionHostResources.ReadConnection(key => key == "delivery" ? path : null, "delivery"); }
        catch (InvalidOperationException error)
        {
            rejected = error.InnerException is null && error.Message == "The server host profile is invalid.";
        }
        ServerHostCheck.That(rejected, "A delivered connection admitted an implicit or alternate credential mechanism.");
    }

    [Theory]
    [InlineData("Passfile")]
    [InlineData("pAsSfIlE")]
    [InlineData("SSL Certificate")]
    [InlineData("SslCertificate")]
    [InlineData("SSL Key")]
    [InlineData("SslKey")]
    [InlineData("SSL Password")]
    [InlineData("SslPassword")]
    [InlineData("Require Auth")]
    [InlineData("RequireAuth")]
    [InlineData("GSS Encryption Mode")]
    [InlineData("GssEncryptionMode")]
    public void EmptyDeliveredCredentialSelectorsAreRejectedBeforeNormalization(string keyword)
    {
        using var files = new ServerHostFiles(); var connection = PasswordConnection(); int index = 0;
        foreach (string empty in new[] { "", "\"\"", "''", " \t ", "\" \t \"", "' \t '" })
        {
            // Write actual raw fields: serializing an empty builder value emits an unquoted empty.
            string path = files.Text("empty-selector-" + index++ + ".txt", connection.ConnectionString + ";" + keyword + "=" + empty + "\n");
            bool rejected = false;
            try { _ = SessionHostResources.ReadConnection(key => key == "delivery" ? path : null, "delivery"); }
            catch (InvalidOperationException error)
            {
                rejected = error.InnerException is null && error.Message == "The server host profile is invalid.";
            }
            ServerHostCheck.That(rejected, "An empty delivered credential selector disappeared during parsing.");
        }
    }

    [Theory]
    [InlineData("Passfile=owned-unopened-passfile;Passfile=")]
    [InlineData("SSL Certificate=owned-unopened-client.pem;SslCertificate=")]
    [InlineData("SSL Key=owned-unopened-client.key;SslKey=")]
    [InlineData("SSL Password=owned-unused-key-password;SslPassword=")]
    [InlineData("Require Auth=;RequireAuth=ScramSHA256")]
    [InlineData("RequireAuth=\"\";Require Auth=Password")]
    [InlineData("GSS Encryption Mode=;GssEncryptionMode=Disable")]
    [InlineData("GssEncryptionMode=\"\";GSS Encryption Mode=Disable")]
    public void DeliveredCredentialSelectorAdmissionSurvivesLaterFields(string fields)
    {
        using var files = new ServerHostFiles(); var connection = PasswordConnection();
        string path = files.Text("replaced-selectors.txt", connection.ConnectionString + ";" + fields + "\n");
        bool rejected = false;
        try { _ = SessionHostResources.ReadConnection(key => key == "delivery" ? path : null, "delivery"); }
        catch (InvalidOperationException error)
        {
            rejected = error.InnerException is null && error.Message == "The server host profile is invalid.";
        }
        ServerHostCheck.That(rejected, "Later fields erased a supplied forbidden or empty authentication selector.");
    }

    [Theory]
    [InlineData("PGUSER")]
    [InlineData("PGPASSWORD")]
    [InlineData("PGPASSFILE")]
    [InlineData("PGSSLCERT")]
    [InlineData("PGSSLKEY")]
    [InlineData("PGREQUIREAUTH")]
    [InlineData("PGGSSENCMODE")]
    public void AmbientCredentialSourcesAreRefusedBeforeOpeningTheDelivery(string name)
    {
        int deliveryReads = 0; bool rejected = false;
        try { _ = SessionHostResources.ReadConnection(Read, "delivery"); }
        catch (InvalidOperationException error) { rejected = error.InnerException is null; }
        ServerHostCheck.That(rejected && deliveryReads == 0,
            "An alternate ambient credential source reached private delivery or pool configuration.");
        string? Read(string key)
        {
            if (key == "delivery") { deliveryReads++; return "/owned-unopened-delivery"; }
            return key == name ? "owned-unused-ambient-value" : null;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitDeliveredCredentialsRetainOrdinalValuesAndPasswordOnlyAuthentication(bool restrictedScram)
    {
        using var files = new ServerHostFiles(); var expected = PasswordConnection();
        expected.Username = "owned restricted role"; expected.Password = "  \u00e9\u0301;=interior\r\nsecret  ";
        expected.SslMode = SslMode.VerifyFull; expected.RootCertificate = "/owned-explicit-trust-root.pem";
        if (restrictedScram) expected.RequireAuth = "ScramSHA256";
        string path = files.Text("accepted-connection.txt", expected.ConnectionString + "\n");
        var actual = new NpgsqlConnectionStringBuilder(SessionHostResources.ReadConnection(key => key == "delivery" ? path : null, "delivery"));
        ServerHostCheck.That(actual.Username == expected.Username && actual.Password == expected.Password
            && actual.SslMode == expected.SslMode && actual.RootCertificate == expected.RootCertificate
            && actual.GssEncryptionMode == GssEncryptionMode.Disable
            && actual.RequireAuth == (restrictedScram ? "ScramSHA256" : "Password,MD5,ScramSHA256"),
            "Explicit restricted credentials were changed or ambient authentication remained selectable.");
    }

    [Fact]
    public void AbsentAlternateSelectorsRetainCredentialsAndRestrictedDefaults()
    {
        using var files = new ServerHostFiles(); var expected = PasswordConnection();
        string path = files.Text("absent-selectors.txt", expected.ConnectionString + "\n"); int deliveryReads = 0;
        var actual = new NpgsqlConnectionStringBuilder(SessionHostResources.ReadConnection(Read, "delivery"));
        ServerHostCheck.That(deliveryReads == 1 && actual.Username == expected.Username && actual.Password == expected.Password
            && actual.Passfile is null && actual.SslCertificate is null && actual.SslKey is null && actual.SslPassword is null
            && actual.GssEncryptionMode == GssEncryptionMode.Disable && actual.RequireAuth == "Password,MD5,ScramSHA256",
            "Genuine selector absence was rejected, changed credentials or lost restricted defaults.");
        string? Read(string key)
        {
            if (key != "delivery") return null;
            deliveryReads++;
            return path;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QuotedPasswordSelectorLookalikesRemainOrdinalCredentialText(bool singleQuoted)
    {
        using var files = new ServerHostFiles(); var expected = PasswordConnection();
        expected.Username = "owned restricted role";
        // The double quote selects single-quoted serialization; the apostrophe selects double quotes.
        expected.Password = "  \u00e9\u0301;Passfile=;Require Auth=;SSL Key=\r\nowned  " + (singleQuoted ? "\"" : "'");
        string path = files.Text("quoted-selector-password.txt", expected.ConnectionString + "\n");
        var actual = new NpgsqlConnectionStringBuilder(SessionHostResources.ReadConnection(key => key == "delivery" ? path : null, "delivery"));
        ServerHostCheck.That(string.Equals(actual.Username, expected.Username, StringComparison.Ordinal)
            && string.Equals(actual.Password, expected.Password, StringComparison.Ordinal)
            && actual.Passfile is null && actual.SslKey is null
            && actual.GssEncryptionMode == GssEncryptionMode.Disable && actual.RequireAuth == "Password,MD5,ScramSHA256",
            "Quoted credential contents were treated as selectors or changed during admission.");
    }

    [Fact]
    public void TlsConnectionRefusesAnImplicitHomeClientIdentityWithoutReadingIt()
    {
        using var files = new ServerHostFiles();
        string directory = Path.Combine(files.Root, ".postgresql"); Directory.CreateDirectory(directory, ServerHostFiles.PrivateDirectory);
        // Deliberately invalid and unread: only the existence of Npgsql's implicit client identity matters.
        File.WriteAllText(Path.Combine(directory, "postgresql.crt"), "not a delivered client certificate");
        var connection = PasswordConnection(); connection.SslMode = SslMode.VerifyFull;
        string path = files.Text("tls-connection.txt", connection.ConnectionString); bool rejected = false;
        try { _ = SessionHostResources.ReadConnection(key => key == "delivery" ? path : key == "HOME" ? files.Root : null, "delivery"); }
        catch (InvalidOperationException error) { rejected = error.InnerException is null; }
        ServerHostCheck.That(rejected, "TLS attempted an undeclared default PostgreSQL client identity.");
    }

    private static NpgsqlConnectionStringBuilder PasswordConnection() => new()
    {
        Host = "127.0.0.1",
        Database = "owned_host_case",
        Username = "owned-restricted-user",
        Password = "owned-delivered-password",
        SslMode = SslMode.Disable
    };

    private static Dictionary<string, string?> SessionScalars() => new(StringComparer.Ordinal)
    {
        ["WEFT_CATALOG_ENABLED"] = "true",
        ["WEFT_CATALOG_AUTH_MODE"] = "session",
        ["WEFT_PUBLIC_ORIGIN"] = "https://127.0.0.1:58413",
        ["WEFT_LISTEN_ADDR"] = "127.0.0.1:58413",
        ["WEFT_HOST_TRANSPORT"] = "directHttps",
        ["WEFT_SESSION_ORGANIZATION_ID"] = "97b79dcc-d078-4994-9a06-f75147842811",
        ["WEFT_ENTRA_TENANT_ID"] = "e6492421-de0b-4626-87a1-773d7f77e0aa",
        ["WEFT_ENTRA_CLIENT_ID"] = "39ceba91-5e4d-4ead-9be2-ddbf843be1c6",
        ["WEFT_SESSION_APPLICATION_NAME"] = "Owned scalar admission",
        ["WEFT_SESSION_KEY_DIRECTORY"] = "/owned-unopened-key-ring"
    };

    [Fact]
    public void DeliveredTextPreservesOrdinalUtf8AndRemovesAtMostOneTerminalLf()
    {
        using var files = new ServerHostFiles();
        string exact = " \u00e9\u0301\r\nopaque-interior\n\n";
        string path = files.Text("exact-utf8.txt", exact);
        ServerHostCheck.That(string.Equals(HostPrivateFiles.ReadText(path, 16_384), exact, StringComparison.Ordinal)
            && string.Equals(HostPrivateFiles.ReadText(path, 16_384, removeTerminalLf: true), exact[..^1], StringComparison.Ordinal),
            "Host secret delivery trimmed or normalized private UTF8.");
        string maximum = new('\u00e9', 8192);
        ServerHostCheck.That(string.Equals(HostPrivateFiles.ReadText(files.Text("maximum-utf8.txt", maximum), 16_384), maximum, StringComparison.Ordinal),
            "Host delivery rejected the exact byte bound or changed Unicode.");
    }

    [Fact]
    public void DeliveredTextRejectsInvalidEncodingNullBomAndOverboundBytesCategorically()
    {
        using var files = new ServerHostFiles();
        byte[][] values = [[0xef, 0xbb, 0xbf, 65], [65, 0, 66], [0xc3, 0x28], Encoding.UTF8.GetBytes(new string('x', 16_385))];
        int index = 0;
        foreach (byte[] value in values)
        {
            string path = files.Bytes("invalid-" + index++ + ".txt", value); bool rejected = false;
            try { _ = HostPrivateFiles.ReadText(path, 16_384, removeTerminalLf: true); }
            catch (InvalidOperationException error) { rejected = error.InnerException is null && !error.Message.Contains(files.Root, StringComparison.Ordinal); }
            ServerHostCheck.That(rejected, "Invalid delivered private text did not retain a categorical operand-free failure.");
        }
    }

    [Fact]
    public void DeliveredFilesRefuseSymlinkHardlinkAndLoosePermissionsWithoutRepair()
    {
        using var files = new ServerHostFiles(); string source = files.Text("source.txt", "opaque");
        string symbolic = Path.Combine(files.Root, "symbolic.txt"); File.CreateSymbolicLink(symbolic, source);
        string hard = Path.Combine(files.Root, "hard.txt");
        ServerHostCheck.That(Link(source, hard) == 0, "The owned hardlink negative-control fixture could not be prepared.");
        string loose = files.Text("loose.txt", "opaque"); File.SetUnixFileMode(loose, ServerHostFiles.PrivateFile | UnixFileMode.GroupRead);
        foreach (string path in new[] { symbolic, hard, loose })
        {
            bool rejected = false; try { _ = HostPrivateFiles.ReadText(path, 16_384); } catch (Exception) { rejected = true; }
            ServerHostCheck.That(rejected, "Host accepted an indirect or nonprivate delivered file.");
        }
        ServerHostCheck.That(File.GetUnixFileMode(loose) == (ServerHostFiles.PrivateFile | UnixFileMode.GroupRead)
            && new FileInfo(symbolic).LinkTarget is not null, "A refused host delivery repaired caller-owned filesystem state.");
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(string existing, string created);
}
