using System.Text.Json.Nodes;
using Microsoft.OpenApi;

namespace Weft.Api.Auth;

// Static supported contract, independent of runtime opt-in. Handlers own verb refusal.
internal static class AuthenticationOpenApi
{
    internal static void Transform(OpenApiDocument document)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>();
        var schemas = document.Components.Schemas;
        var token = new OpenApiSchema
        {
            Type = JsonSchemaType.String,
            MinLength = 1,
            MaxLength = 4096,
            Pattern = "^[A-Za-z0-9_-]+$"
        };
        var fingerprint = new OpenApiSchema
        {
            Type = JsonSchemaType.String,
            MinLength = 46,
            MaxLength = 46,
            Pattern = "^v1\\.[A-Za-z0-9_-]{43}$"
        };
        var empty = new OpenApiSchema { Type = JsonSchemaType.Null };
        schemas["AuthSessionV1"] = new OpenApiSchema
        {
            OneOf = [Session("authenticated", token, fingerprint),
                Session("anonymous", empty, empty), Session("authenticationRequired", empty, empty)]
        };
        schemas["AuthModeStatusV1"] = Object(new()
        {
            ["version"] = Version(),
            ["mode"] = Text("development", "disabled")
        });
        schemas["AuthStatusV1"] = new OpenApiSchema
        {
            OneOf = [new OpenApiSchemaReference("AuthSessionV1", document),
                new OpenApiSchemaReference("AuthModeStatusV1", document)]
        };
        schemas["AuthLoginV1"] = Object(new()
        {
            ["version"] = Version(),
            ["authorizationUrl"] = new OpenApiSchema
            { Type = JsonSchemaType.String, Format = "uri", MaxLength = 16384 }
        });
        schemas["AuthForgetLocalV1"] = Object(new()
        { ["version"] = Version(), ["outcome"] = Text("revocationUnconfirmed") });
        schemas["AuthErrorV1"] = Object(new()
        {
            ["version"] = Version(),
            ["code"] = Text("invalidRequest", "authenticationRequired",
                "requestRejected", "forgetNotAvailable", "unavailable")
        });
        document.Paths ??= new OpenApiPaths();
        Add("/api/auth/session", HttpMethod.Get, "ReadBrowserAuthentication", "AuthStatusV1", "X-Weft-Session");
        Add("/api/auth/login", HttpMethod.Post, "BeginBrowserLogin", "AuthLoginV1", "X-Weft-Login");
        Add("/api/auth/logout", HttpMethod.Post, "LogoutBrowserSession", null, "X-Weft-Logout");
        Add("/api/auth/forget-local", HttpMethod.Post, "ForgetDeniedLocalSession", "AuthForgetLocalV1", "X-Weft-Forget-Local");
        Add("/api/auth/callback/entra", HttpMethod.Get, "CompleteEntraLogin", null, null);
        if (schemas.TryGetValue("CatalogContextV1", out var contextSchema) &&
            contextSchema is OpenApiSchema context && context.Properties is { } original)
        {
            var developmentProperties = new Dictionary<string, IOpenApiSchema>(original)
            { ["mode"] = Text("development") };
            developmentProperties.Remove("identityContext");
            var sessionProperties = new Dictionary<string, IOpenApiSchema>(original)
            { ["mode"] = Text("session"), ["identityContext"] = fingerprint };
            schemas["CatalogContextV1"] = new OpenApiSchema
            { OneOf = [Object(developmentProperties), Object(sessionProperties)] };
        }

        OpenApiSchema Session(string state, IOpenApiSchema requestToken, IOpenApiSchema identityContext) => Object(new()
        {
            ["version"] = Version(),
            ["mode"] = Text("session"),
            ["state"] = Text(state),
            ["authActionRequestToken"] = token,
            ["requestToken"] = requestToken,
            ["identityContext"] = identityContext
        });

        void Add(string path, HttpMethod method, string id, string? schema, string? actionHeader)
        {
            var callback = path.EndsWith("/callback/entra", StringComparison.Ordinal);
            var operation = new OpenApiOperation
            {
                OperationId = id,
                Summary = callback ? "Complete the fixed Entra code flow." : "Explicit browser authentication protocol.",
                Description = "Supported contract; presence does not activate a runtime route or authentication mode. " +
                    "Bodyless, bounded, no-store/no-ETag/no-referrer. No bearer/ambient-claim identity or request-selected return target. " +
                    (callback ? "Exactly code,state,session_state,client_info,iss,error,error_description,error_uri; no duplicates, token fields or code+error. " +
                        "Success only303Location'/' after acknowledged fresh issuance; failure is fixed readable401/503 HTML, never provider text."
                        : "One exact action header. Unsafe actions require exact Origin and separate standard auth-action AF; positive sessions additionally require112 epoch AF. " +
                            "Session fields/fingerprint are non-authority and memory-only. Forget success means revocation-unconfirmed only."),
                Parameters = [],
                Responses = new OpenApiResponses(),
                RequestBody = null
            };
            if (actionHeader is not null) Header(actionHeader, true, Text("1"));
            if (method == HttpMethod.Post)
            {
                Header("Origin", true, new OpenApiSchema { Type = JsonSchemaType.String });
                Header("X-Weft-Auth-Token", true, token);
                if (path != "/api/auth/forget-local") Header("X-XSRF-TOKEN", false, token);
            }
            var success = callback ? "303" : path == "/api/auth/logout" ? "204" : "200";
            operation.Responses[success] = Response(schema, "Completed only after the fixed protocol acknowledges success.");
            foreach (var failure in callback ? new[] { "401", "405", "503" } : new[] { "400", "401", "403", "405", "409", "503" })
            {
                var response = Response("AuthErrorV1", "Closed categorical failure; no success or automatic retry.");
                if (callback && failure != "405")
                {
                    response.Description = "Fixed categorical readable HTML; no dynamic request/provider details.";
                    response.Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["text/html"] = new()
                        { Schema = new OpenApiSchema { Type = JsonSchemaType.String, MaxLength = 1024 } }
                    };
                }
                operation.Responses[failure] = response;
            }
            if (callback && operation.Responses["303"] is OpenApiResponse redirect)
                redirect.Headers!["Location"] = new OpenApiHeader { Schema = Text("/") };
            var item = new OpenApiPathItem();
            item.AddOperation(method, operation);
            document.Paths[path] = item;

            void Header(string name, bool required, IOpenApiSchema value) => operation.Parameters!.Add(new OpenApiParameter
            { Name = name, In = ParameterLocation.Header, Required = required, Schema = value });
        }
        OpenApiResponse Response(string? name, string description) => new()
        {
            Description = description,
            Content = name is null ? null : new Dictionary<string, OpenApiMediaType>
            { ["application/json"] = new() { Schema = new OpenApiSchemaReference(name, document) } },
            Headers = new Dictionary<string, IOpenApiHeader>
            {
                ["Cache-Control"] = new OpenApiHeader { Schema = Text("no-store") },
                ["Referrer-Policy"] = new OpenApiHeader { Schema = Text("no-referrer") }
            }
        };
    }

    private static OpenApiSchema Object(Dictionary<string, IOpenApiSchema> properties) => new()
    {
        Type = JsonSchemaType.Object,
        Properties = properties,
        AdditionalPropertiesAllowed = false,
        Required = properties.Keys.ToHashSet(StringComparer.Ordinal)
    };
    private static OpenApiSchema Version() => new()
    { Type = JsonSchemaType.Integer, Enum = [JsonValue.Create(1)!] };
    private static OpenApiSchema Text(params string[] values) => new()
    { Type = JsonSchemaType.String, Enum = values.Select(value => (JsonNode)JsonValue.Create(value)!).ToList() };
}
