using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Weft.Cli.Http;

namespace Weft.Tests.Cli;

/// <summary>
/// The CLI's operation table against the committed <c>contracts/weft-v1.json</c>: the same method and path for each
/// operation ID, only declared parameters with every required one supplied, the declared request and response media
/// types, and request, response and error contracts whose JSON shapes match the documented schemas.
/// </summary>
public sealed class CliContractDriftTests
{
    private static readonly CliOpenApi Spec = CliOpenApi.Load();

    public static TheoryData<string> Operations()
    {
        var data = new TheoryData<string>();
        foreach (var operation in ApiOperations.All)
            data.Add(operation.OperationId);
        return data;
    }

    [Fact]
    public void EveryDocumentedOperationIsCalledOrDeliberatelyUnused()
    {
        var called = ApiOperations.All.Select(operation => operation.OperationId).ToArray();
        Assert.Equal(called.Length, called.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(called, ApiOperations.NotUsed.ContainsKey);
        var documented = Spec.Operations.Select(operation => operation.Id).ToHashSet(StringComparer.Ordinal);
        Assert.All(called.Concat(ApiOperations.NotUsed.Keys), id => Assert.Contains(id, documented));
        Assert.All(documented, id => Assert.True(called.Contains(id) || ApiOperations.NotUsed.ContainsKey(id), $"{id} is neither called nor listed as unused"));
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public void TheOperationMatchesItsSpecification(string operationId)
    {
        var operation = ApiOperations.All.Single(item => item.OperationId == operationId);
        var documented = Spec.Operation(operationId);
        Assert.Equal(documented.Method, operation.Method);
        Assert.Equal(documented.Path, operation.Path);
        var parameters = documented.Parameters.ToArray();
        foreach (var name in operation.Query)
            Assert.True(parameters.Any(parameter => parameter.In == "query" && parameter.Name == name), $"{operationId} sends undeclared query {name}");
        foreach (var name in operation.Headers)
            Assert.True(parameters.Any(parameter => parameter.In == "header" && parameter.Name == name), $"{operationId} sends undeclared header {name}");
        foreach (var (location, name, required) in parameters.Where(parameter => parameter.Required))
            Assert.True(location switch
            {
                "query" => operation.Query.Contains(name),
                "header" => operation.Headers.Contains(name) || ApiOperations.CookieSessionHeaders.Contains(name),
                "path" => operation.Path.Contains("{" + name + "}", StringComparison.Ordinal),
                _ => false
            }, $"{operationId} omits the required {location} parameter {name}");
        foreach (var name in PathNames(operation.Path))
            Assert.True(parameters.Any(parameter => parameter.In == "path" && parameter.Name == name), $"{operationId} path {name} is undeclared");

        Assert.Equal(documented.RequestMediaTypes, operation.RequestMediaType is null ? [] : [operation.RequestMediaType]);
        if (operation.RequestBody is { } body)
        {
            Assert.Equal("#/components/schemas/" + body.Name, documented.RequestSchema(operation.RequestMediaType!)!["$ref"]?.GetValue<string>());
            CompareShape(body, documented.RequestSchema(operation.RequestMediaType!)!, $"{operationId} request");
        }

        foreach (var (status, type) in operation.Success)
        {
            var response = documented.Response(status);
            Assert.True(response is not null, $"{operationId} does not document status {status}");
            if (type is null)
                continue;
            var content = response!["content"]?[operation.ResponseMediaType];
            Assert.True(content is not null, $"{operationId} {status} is not documented as {operation.ResponseMediaType}");
            Assert.Equal("#/components/schemas/" + type.Name, content!["schema"]?["$ref"]?.GetValue<string>());
            CompareShape(type, content["schema"]!, $"{operationId} {status}");
        }

        // Business operations admit delegated bearer tokens; only public health probes omit credentials.
        var security = documented.Node["security"]?.AsArray() ?? Spec.Document["security"]?.AsArray();
        if (operation.RequiresAuthentication)
            Assert.True(security is not null && security.Any(requirement => requirement!.AsObject().Count == 1 &&
                requirement["entraBearer"]?.AsArray().Select(scope => scope!.GetValue<string>()).SequenceEqual(["api://{sessionClientId}/{scope}"]) == true),
                $"{operationId} does not admit the entraBearer scheme alone");
        else
            Assert.True(security is null || security.Count == 0 || security.Any(requirement => requirement!.AsObject().Count == 0),
                $"{operationId} is not declared as a public probe");

        var errorSchema = "#/components/schemas/" + operation.ErrorBody.Name;
        var errors = documented.Statuses.Where(status => status >= 400).Select(status => documented.Response(status)!)
            .Select(response => response["content"]?["application/json"]?["schema"]).Where(schema => schema is not null).ToArray();
        if (operation.RequiresAuthentication) Assert.NotEmpty(errors);
        Assert.All(errors, schema => Assert.True(schema!.ToJsonString().Contains(errorSchema, StringComparison.Ordinal) ||
            schema.ToJsonString().Contains("#/components/schemas/CatalogRevisionIngestionResultV1", StringComparison.Ordinal),
            $"{operationId} documents an error body other than {operation.ErrorBody.Name}"));
        CompareShape(operation.ErrorBody, Spec.Schema(errorSchema), $"{operationId} error");
    }

    [Fact]
    public void TheBearerSchemeOffersTheCliFlowsAtTheFixedMicrosoftEndpoints()
    {
        var scheme = Spec.Document["components"]!["securitySchemes"]!["entraBearer"]!;
        Assert.Equal("oauth2", scheme["type"]!.GetValue<string>());
        var code = scheme["flows"]!["authorizationCode"]!;
        Assert.Equal("https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/authorize", code["authorizationUrl"]!.GetValue<string>());
        Assert.Equal("https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token", code["tokenUrl"]!.GetValue<string>());
        var device = scheme["flows"]!["x-deviceAuthorization"]!;
        Assert.Equal("https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/devicecode", device["deviceAuthorizationUrl"]!.GetValue<string>());
        Assert.Equal("https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token", device["tokenUrl"]!.GetValue<string>());
        Assert.NotNull(code["scopes"]!["api://{sessionClientId}/{scope}"]);
    }

    [Fact]
    public void ARequestCanOnlySetItsDeclaredParameters()
    {
        var request = new ApiRequest(ApiOperations.GetCatalogProjects);
        Assert.Throws<InvalidOperationException>(() => request.Query("afterCaseId", "x"));
        Assert.Throws<InvalidOperationException>(() => request.Header("Idempotency-Key", "x"));
        Assert.Throws<InvalidOperationException>(() => request.Path("mappingId", "x"));
        Assert.Throws<InvalidOperationException>(() => request.Body(() => new System.Net.Http.ByteArrayContent([])));
        Assert.Throws<InvalidOperationException>(request.Target);
        request.Path("organizationId", "a b/c").Query("limit", "5").Query("cursor", null);
        Assert.Equal("/api/v1/organizations/a%20b%2Fc/projects?limit=5", request.Target());
    }

    private static IEnumerable<string> PathNames(string path) =>
        System.Text.RegularExpressions.Regex.Matches(path, "{([A-Za-z]+)}").Select(match => match.Groups[1].Value);

    /// <summary>The C# contract's JSON properties against the documented schema, recursively through referenced components.</summary>
    private static void CompareShape(Type type, JsonNode schema, string at)
    {
        var seen = new HashSet<(Type, string)>();
        Compare(type, schema, at, seen, null);
    }

    private static void Compare(Type type, JsonNode schemaNode, string at, HashSet<(Type, string)> seen, string? discriminator)
    {
        var schema = Resolve(schemaNode, out var reference);
        if (reference is not null && !seen.Add((type, reference)))
            return;
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || type == typeof(DateTimeOffset) ||
            type == typeof(DateTime) || type == typeof(Guid) || type == typeof(object))
            return;
        var info = WeftApiClient.Json.GetTypeInfo(type);
        if (info.Kind == JsonTypeInfoKind.Enumerable)
        {
            var element = type.IsArray ? type.GetElementType()! : type.GetGenericArguments().Single();
            Assert.True(schema["items"] is not null, $"{at}: documented as {schema["type"]}, sent as an array");
            Compare(element, schema["items"]!, at + "[]", seen, null);
            return;
        }
        if (info.Kind != JsonTypeInfoKind.Object)
            return;
        if (info.PolymorphismOptions is { } polymorphism)
        {
            var alternatives = (schema["anyOf"] ?? schema["oneOf"])!.AsArray().Select(alternative => Resolve(alternative!, out _)).ToArray();
            foreach (var derived in polymorphism.DerivedTypes)
            {
                var name = (string)derived.TypeDiscriminator!;
                var alternative = alternatives.SingleOrDefault(item =>
                    item["properties"]?[polymorphism.TypeDiscriminatorPropertyName]?["enum"]?[0]?.GetValue<string>() == name);
                Assert.True(alternative is not null, $"{at}: the derived contract {name} is undocumented");
                Compare(derived.DerivedType, alternative!, at + "<" + name + ">", seen, polymorphism.TypeDiscriminatorPropertyName);
            }
            Assert.Equal(polymorphism.DerivedTypes.Count, alternatives.Length);
            return;
        }
        var documented = Properties(schema);
        var actual = info.Properties.ToDictionary(property => property.Name, StringComparer.Ordinal);
        var expected = documented.Keys.Where(name => name != discriminator).ToHashSet(StringComparer.Ordinal);
        Assert.True(expected.SetEquals(actual.Keys), $"{at} ({type.Name}): documented [{string.Join(", ", expected.Order())}], " +
            $"contract [{string.Join(", ", actual.Keys.Order())}]");
        foreach (var (name, property) in actual)
            Compare(property.PropertyType, documented[name], at + "." + name, seen, null);
    }

    private static Dictionary<string, JsonNode> Properties(JsonObject schema)
    {
        var result = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var (name, value) in schema["properties"]?.AsObject() ?? [])
            result[name] = value!;
        foreach (var keyword in new[] { "oneOf", "anyOf" })
            foreach (var alternative in schema[keyword]?.AsArray() ?? [])
                foreach (var (name, value) in Properties(Resolve(alternative!, out _)))
                    result.TryAdd(name, value);
        return result;
    }

    /// <summary>Follows a reference and unwraps a nullable <c>oneOf</c> of null and one schema.</summary>
    private static JsonObject Resolve(JsonNode node, out string? reference)
    {
        reference = null;
        var schema = node.AsObject();
        while (true)
        {
            if (schema["$ref"] is { } target)
            {
                reference = target.GetValue<string>();
                schema = Spec.Schema(reference);
                continue;
            }
            if (schema["oneOf"] is JsonArray choices && choices.Count == 2 &&
                choices.SingleOrDefault(choice => choice!["type"] is JsonValue kind && kind.GetValue<string>() == "null") is { } empty)
            {
                schema = choices.Single(choice => !ReferenceEquals(choice, empty))!.AsObject();
                continue;
            }
            return schema;
        }
    }
}
