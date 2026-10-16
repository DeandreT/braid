using System.Text;

namespace Weft.Tests;

public sealed class ExternalIdentityOwnerToolInputTests
{
    private const string InputOnlyConnection = "Host=127.0.0.1;Port=1;Database=weft_input_only;Username=weft_input_only;Password=weft-input-only-marker;Pooling=false;Timeout=1;CommandTimeout=1;Enlist=false";
    private const string Marker = "weft-private-stdin-marker";

    [Fact]
    public async Task ActualBinaryStdinRejectsInvalidUtf8EscapedSurrogatesAndNonexactJsonObjectsCategorically()
    {
        var documents = new[]
        {
            "", "null", "[]",
            """{"provider":"provider","authority":"authority"}""",
            """{"provider":"provider","authority":"authority","subject":null}""",
            """{"provider":"provider","authority":"authority","subject":1}""",
            """{"provider":"provider","authority":"authority","subject":[]}""",
            """{"provider":"provider","authority":"authority","subject":"value","extra":"private"}""",
            """{"provider":"provider","authority":"authority","subject":"first","subject":"second"}""",
            """{"provider":"provider","authority":"authority","subject":"first","\u0073ubject":"second"}""",
            """{"provider":"provider","authority":"authority","subject":"\uD800"}""",
            """{"provider":"provider","authority":"authority","subject":"\uDC00"}""",
            """{"provider":"provider","authority":"authority","subject":"\uD800x"}""",
            """{"provider":"provider","authority":"authority","subject":"\uD800\u0041"}""",
            """{"provider":"provider","authority":"authority","subject":"\u0000"}""",
            """{"provider":"provider","authority":"authority","subject":"\u0085"}""",
            """{"provider":"Provider","authority":"authority","subject":"value"}""",
            """{"provider":"provider","authority":"authority","subject":"value",}""",
            """{"provider":"provider","authority":"authority","subject":"value"}{}""",
            """{"provider":"provider","authority":"authority","subject":"value"}true""",
            """{/*comment*/"provider":"provider","authority":"authority","subject":"value"}""",
            """{"provider":{"nested":{"too":"deep"}},"authority":"authority","subject":"value"}"""
        };
        var principal = Guid.NewGuid().ToString("D");
        foreach (var document in documents)
        {
            var result = await ExternalIdentityOwnerToolTestSupport.RunAsync(InputOnlyConnection,
                ["bind-external-identity", principal], Encoding.UTF8.GetBytes(document), [Marker, document]);
            ExternalIdentityOwnerToolTestSupport.Category(result, 2, "error", "invalidRequest");
        }
        var prefix = Encoding.UTF8.GetBytes("{\"provider\":\"provider\",\"authority\":\"authority\",\"subject\":\"");
        var suffix = Encoding.UTF8.GetBytes("\"}");
        foreach (var invalidUtf8 in new byte[][] { [0xc0, 0xaf], [0xed, 0xa0, 0x80], [0xff], [0xf0, 0x9f] })
        {
            var bytes = prefix.Concat(invalidUtf8).Concat(suffix).ToArray();
            var result = await ExternalIdentityOwnerToolTestSupport.RunAsync(InputOnlyConnection,
                ["bind-external-identity", principal], bytes, [Marker]);
            ExternalIdentityOwnerToolTestSupport.Category(result, 2, "error", "invalidRequest");
        }
    }

    [Fact]
    public async Task ActualBinaryStdinRefusesOneByteBeyondRawDocumentLimit()
    {
        var document = Encoding.UTF8.GetBytes("{\"provider\":\"provider\",\"authority\":\"authority\",\"subject\":\"" + Marker + "\"}");
        var bytes = document.Concat(Enumerable.Repeat((byte)' ', 8193 - document.Length)).ToArray();
        Assert.Equal(8193, bytes.Length);
        foreach (var command in new[]
        {
            new[] { "bind-external-identity", Guid.NewGuid().ToString("D") },
            new[] { "disable-external-identity", Guid.NewGuid().ToString("D"), "1" }
        })
        {
            var result = await ExternalIdentityOwnerToolTestSupport.RunAsync(InputOnlyConnection, command, bytes, [Marker]);
            ExternalIdentityOwnerToolTestSupport.Category(result, 2, "error", "invalidRequest");
        }
    }

    [Fact]
    public async Task RecognizedOwnerCommandsRequireExactArityCanonicalIdsAndPositiveCanonicalInt64Version()
    {
        var principal = Guid.NewGuid().ToString("D");
        var organization = Guid.NewGuid().ToString("D");
        List<string[]> malformed =
        [
            ["ensure-principal"], ["ensure-principal", principal, "extra"],
            ["ensure-organization-admission", organization],
            ["ensure-organization-admission", organization, principal, "extra"],
            ["bind-external-identity"], ["bind-external-identity", principal, "extra"],
            ["disable-external-identity", principal], ["disable-external-identity", principal, "1", "extra"]
        ];
        foreach (var invalid in new[]
        {
            "", Guid.Empty.ToString("D"), " " + principal, principal + " ",
            Guid.Parse(principal).ToString("N"), Guid.Parse(principal).ToString("B"),
            "ABCDEF01-2345-6789-ABCD-ABCDEF012345", Marker
        })
        {
            malformed.Add(["ensure-principal", invalid]);
            malformed.Add(["ensure-organization-admission", invalid, principal]);
            malformed.Add(["ensure-organization-admission", organization, invalid]);
            malformed.Add(["bind-external-identity", invalid]);
            malformed.Add(["disable-external-identity", invalid, "1"]);
        }
        foreach (var version in new[] { "", "0", "-1", "+1", "01", " 1", "1 ", "1.0", "9223372036854775808", Marker })
            malformed.Add(["disable-external-identity", principal, version]);
        foreach (var arguments in malformed)
        {
            var result = await ExternalIdentityOwnerToolTestSupport.RunAsync(InputOnlyConnection, arguments, privateValues: [Marker]);
            ExternalIdentityOwnerToolTestSupport.Category(result, 2, "error", "invalidRequest");
        }
    }

    [Fact]
    public async Task MissingOwnerConfigurationIsCategoricalAndLegacyDevelopmentEnvironmentIsNotImplicitInput()
    {
        var principal = Guid.NewGuid().ToString("D");
        var development = new Dictionary<string, string>
        {
            ["WEFT_DEV_PRINCIPAL_ID"] = principal,
            ["WEFT_DEV_ORGANIZATION_ID"] = Guid.NewGuid().ToString("D"),
            ["WEFT_DEV_ORGANIZATION_NAME"] = Marker
        };
        foreach (var configuration in new string?[] { null, "", " \t" })
        {
            var result = await ExternalIdentityOwnerToolTestSupport.RunAsync(configuration,
                ["ensure-principal", principal], privateValues: [Marker], environment: development);
            ExternalIdentityOwnerToolTestSupport.Category(result, 2, "error", "configurationInvalid");
        }
        var refused = await ExternalIdentityOwnerToolTestSupport.RunAsync(InputOnlyConnection,
            ["ensure-principal"], privateValues: [Marker], environment: development);
        ExternalIdentityOwnerToolTestSupport.Category(refused, 2, "error", "invalidRequest");
    }
}
