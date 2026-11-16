namespace Braid.Tests;

public sealed class ProjectPayloadOwnerToolInputTests
{
    private const string Marker = "braid-private-payload-tool-input";

    [Fact]
    public async Task RecognizedPayloadCommandRejectsNonexactArityAndNoncanonicalFullTupleBeforeConfiguration()
    {
        var organization = Guid.NewGuid().ToString("D");
        var project = Guid.NewGuid().ToString("D");
        var principal = Guid.NewGuid().ToString("D");
        var valid = new[] { "set-project-payload-read", organization, project, principal, "0", "true" };
        List<string[]> malformed = [];
        for (var length = 1; length < valid.Length; length++)
            malformed.Add(valid[..length]);
        malformed.Add([.. valid, Marker]);
        foreach (var invalid in new[]
        {
            "", Guid.Empty.ToString("D"), " " + principal, principal + " ",
            Guid.Parse(principal).ToString("N"), Guid.Parse(principal).ToString("B"),
            "ABCDEF01-2345-6789-ABCD-ABCDEF012345", Marker
        })
        {
            for (var index = 1; index <= 3; index++)
            {
                var arguments = valid.ToArray();
                arguments[index] = invalid;
                malformed.Add(arguments);
            }
        }
        foreach (var arguments in malformed)
        {
            // Missing configuration makes invalidRequest evidence that argument refusal precedes any database work.
            var result = await ExternalIdentityOwnerToolTestSupport.RunAsync(null, arguments, privateValues: [Marker]);
            ExternalIdentityOwnerToolTestSupport.Category(result, 2, "error", "invalidRequest");
        }
    }

    [Fact]
    public async Task RecognizedPayloadCommandRejectsNoncanonicalNonnegativeInt64VersionsAndNonliteralBooleans()
    {
        var prefix = new[]
        {
            "set-project-payload-read", Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D")
        };
        foreach (var version in new[]
        {
            "", "-1", "-0", "+0", "+1", "00", "01", " 0", "0 ", "1.0", "1e0",
            "9223372036854775808", "18446744073709551615", Marker
        })
        {
            var result = await ExternalIdentityOwnerToolTestSupport.RunAsync(null,
                [.. prefix, version, "true"], privateValues: [Marker]);
            ExternalIdentityOwnerToolTestSupport.Category(result, 2, "error", "invalidRequest");
        }
        foreach (var active in new[] { "", "True", "False", "TRUE", "1", "0", " true", "false ", Marker })
        {
            var result = await ExternalIdentityOwnerToolTestSupport.RunAsync(null,
                [.. prefix, "0", active], privateValues: [Marker]);
            ExternalIdentityOwnerToolTestSupport.Category(result, 2, "error", "invalidRequest");
        }
    }

    [Fact]
    public async Task CanonicalVersionBoundariesRequireEnvironmentOnlyMigrationConfigurationWithoutDevelopmentFallback()
    {
        var organization = Guid.NewGuid().ToString("D");
        var project = Guid.NewGuid().ToString("D");
        var principal = Guid.NewGuid().ToString("D");
        var development = new Dictionary<string, string>
        {
            ["BRAID_DEV_ORGANIZATION_ID"] = organization,
            ["BRAID_DEV_PROJECT_ID"] = project,
            ["BRAID_DEV_PRINCIPAL_ID"] = principal,
            ["BRAID_DEV_ORGANIZATION_NAME"] = Marker,
            ["BRAID_DEV_PROJECT_ROLE"] = "administrator"
        };
        foreach (var configuration in new string?[] { null, "", " \t" })
        {
            foreach (var version in new[] { "0", "9223372036854775807" })
            {
                foreach (var active in new[] { "true", "false" })
                {
                    var result = await ExternalIdentityOwnerToolTestSupport.RunAsync(configuration,
                        ["set-project-payload-read", organization, project, principal, version, active],
                        privateValues: [Marker], environment: development);
                    ExternalIdentityOwnerToolTestSupport.Category(result, 2, "error", "configurationInvalid");
                }
            }
        }
    }
}
