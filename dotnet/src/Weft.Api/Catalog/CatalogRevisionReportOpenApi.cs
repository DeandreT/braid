using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.OpenApi;
using Weft.Contracts.Catalog;
using Weft.Domain;

namespace Weft.Api.Catalog;

public static partial class CatalogEndpoints
{
    private static void DescribeRevisionReport<T>(RouteHandlerBuilder route, string name,
        RevisionReportOperation reportOperation, bool retained, bool ensure)
    {
        var operationName = reportOperation == RevisionReportOperation.Projection ? "projection" : "validation";
        Describe<T>(route, name, ensure
            ? $"Explicitly ensure one scoped persisted {operationName} report."
            : retained ? $"Read one exact retained successful {operationName} report."
                : $"Read the current selected {operationName} report for one immutable revision.");
        route.Produces<CatalogErrorV1>(StatusCodes.Status415UnsupportedMediaType)
            .Produces<CatalogErrorV1>(StatusCodes.Status429TooManyRequests)
            .Produces<CatalogErrorV1>(StatusCodes.Status503ServiceUnavailable);
        if (ensure)
            route.Produces<T>(StatusCodes.Status202Accepted)
                .Produces<CatalogErrorV1>(StatusCodes.Status409Conflict);
        route.WithDescription("Requires fresh project ReadMetadata authority for the exact actor and full revision ancestry; all four project roles qualify. " +
            "Enabled features are independent of organization project-creation capability and upload authority. " +
            "All route and query UUIDs are canonical nonzero lowercase D strings. Unknown, repeated and escaped-duplicate query names are rejected; " +
            "no actor, digest, backend profile, policy or private object inputs are accepted. " +
            "No body or Content-Type is supported. Content-Encoding is absent or one identity token, Transfer-Encoding is absent, " +
            "and Content-Length is zero or the server proves no body. Unknown/chunked/nonempty framing is rejected before body consumption. " +
            "Conditional headers and Idempotency-Key are unsupported. " +
            (ensure
                ? "A cookie-bound framework request token and exact configured Origin are required. Optional repairDerivationId selects only the exact current successful result; " +
                    "there is no implicit repair or projection-link selector. Completed or cached observations return 200; actual pending or retry-delayed observations return 202. " +
                    "ClaimLost, SelectionChanged and GenerationExhausted are distinct 409 errors. "
                : "Reads never derive, claim, repair, provision storage or start a worker. " +
                    (retained ? "The derivationId selects an immutable successful result, never a pending or failed attempt. "
                        : "Current selects the server's full report key for this revision, independently of the mapping head. ") +
                    (reportOperation == RevisionReportOperation.Validation
                        ? "Optional projectionDerivationId links findings only through that exact verified projection; unavailable selection is opaque 404. "
                        : "No query is accepted. ")) +
            "Lifecycle, backend status and read refusal are separate. False validation is a usable report. " +
            "Missing, corrupt, unsupported or over-budget selected payloads retain honest available metadata with a categorical read refusal and null report. " +
            "The complete wrapper including linked findings is bounded before response bytes and followed by fresh authority/source validation. " +
            "Local work/read admission is nonwaiting 429; report dependency availability is closed 503. " +
            "A ResourceLimit wrapper that cannot fit its policy uses a fixed categorical 503 error of at most 1024 bytes. " +
            "All responses are no-store and carry no ETag.")
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                // Description-only selectors and headers never cause runtime
                // JSON/form binding ahead of feature and authority admission.
                operation.RequestBody = null;
                if (ensure)
                {
                    AddReportParameter(operation, "repairDerivationId", ParameterLocation.Query, false,
                        "Exact selected successful derivation to repair explicitly; no current-result substitution.", uuid: true);
                    AddReportParameter(operation, "Origin", ParameterLocation.Header, true,
                        "One exact configured canonical browser origin.");
                    AddReportParameter(operation, CatalogRequestBoundary.RequestTokenHeader, ParameterLocation.Header, true,
                        "Framework cookie-bound token issued by an admitted catalog context.", maximumLength: 4096);
                }
                else if (reportOperation == RevisionReportOperation.Validation)
                    AddReportParameter(operation, "projectionDerivationId", ParameterLocation.Query, false,
                        "Exact verified projection result for optional diagnostic linking.", uuid: true);
                AddReportParameter(operation, "Content-Encoding", ParameterLocation.Header, false,
                    "Absent or a single identity token; no other coding or request body is accepted.");
                if (operation.Responses is not null)
                    foreach (var response in operation.Responses.Values.OfType<OpenApiResponse>())
                    {
                        response.Headers ??= new Dictionary<string, IOpenApiHeader>();
                        response.Headers["Cache-Control"] = new OpenApiHeader
                        {
                            Description = "Always no-store.",
                            Schema = ReportLiteral("no-store")
                        };
                    }
                if (ensure && operation.Responses?.TryGetValue("202", out var pending) == true && pending is OpenApiResponse accepted)
                {
                    accepted.Headers ??= new Dictionary<string, IOpenApiHeader>();
                    accepted.Headers["Location"] = new OpenApiHeader
                    {
                        Description = "Canonical full-scoped current report GET URL; future reads independently recheck authority.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String }
                    };
                    accepted.Headers["Retry-After"] = new OpenApiHeader
                    {
                        Description = "Optional positive integer seconds from the actual database-observed next-attempt time.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String, Pattern = "^[1-9][0-9]*$" }
                    };
                }
                return Task.CompletedTask;
            });
    }

    internal static void TransformRevisionReportSchemas(OpenApiDocument document)
    {
        if (document.Components?.Schemas is not { } schemas)
            return;
        // Operations are generated before all shared components have settled.
        // Apply exact response vocabulary only in the final document transform.
        TransformWrapper(nameof(CatalogProjectionReportV1), "projection", "ProjectionReportV1");
        TransformWrapper(nameof(CatalogValidationReportV1), "validation", "ValidationReportV1");
        if (schemas.TryGetValue(nameof(CatalogRevisionReportResultV1), out var resultSchema) && resultSchema is OpenApiSchema result)
        {
            RequireReportProperties(result, ["derivationId", "generation", "startedAtUtc", "finishedAtUtc"]);
            DescribeReportIdentity(result);
        }
        if (schemas.TryGetValue(nameof(CatalogRevisionReportAttemptV1), out var attemptSchema) && attemptSchema is OpenApiSchema attempt)
        {
            RequireReportProperties(attempt, ["derivationId", "generation", "startedAtUtc", "finishedAtUtc", "status", "failure"]);
            DescribeReportIdentity(attempt);
            if (attempt.Properties is { } properties)
            {
                properties["finishedAtUtc"] = NullableReportString("date-time");
                properties["status"] = NullableReportReference("OperationStatusV1", document);
                properties["failure"] = NullableReportReference(nameof(CatalogRevisionReportFailureV1), document);
            }
        }
        TransformEnum(nameof(CatalogRevisionReportStateV1), ["notDerived", "pending", "available", "failed"]);
        TransformEnum(nameof(CatalogRevisionReportReadOutcomeV1),
            ["observed", "objectUnavailable", "storageIntegrity", "unsupportedFormat", "resourceLimit"]);
        TransformEnum(nameof(CatalogRevisionReportFailureV1),
            ["objectUnavailable", "storageIntegrity", "storageIo", "backendUnavailable", "backendFailure", "timedOut",
                "canceled", "isolationFailure", "protocolFailure", "profileUnavailable", "resourceLimit", "unsupportedArtifact"]);
        if (schemas.ContainsKey(nameof(CatalogProjectionReportV1)) || schemas.ContainsKey(nameof(CatalogValidationReportV1)))
        {
            // A catalog-only document first encounters status through nullable
            // report metadata. Keep null on those properties, not the shared enum.
            TransformEnum("OperationStatusV1",
                ["succeeded", "invalidMapping", "unsupportedArtifact", "resourceLimit", "protocolFailure",
                    "backendUnavailable", "backendFailure", "timedOut", "canceled", "isolationFailure"]);
        }

        void TransformWrapper(string name, string operation, string reportName)
        {
            if (!schemas.TryGetValue(name, out var wrapperSchema) || wrapperSchema is not OpenApiSchema wrapper)
                return;
            RequireReportProperties(wrapper, ["schemaVersion", "revision", "operation", "state", "readOutcome", "status",
                "result", "currentAttempt", "nextAttemptAtUtc", "failure", "report"]);
            if (wrapper.Properties is not { } properties)
                return;
            properties["schemaVersion"] = ReportLiteral("weft.catalog-revision-report.v1");
            properties["operation"] = ReportLiteral(operation);
            properties["status"] = NullableReportReference("OperationStatusV1", document);
            properties["result"] = NullableReportReference(nameof(CatalogRevisionReportResultV1), document);
            properties["currentAttempt"] = NullableReportReference(nameof(CatalogRevisionReportAttemptV1), document);
            properties["nextAttemptAtUtc"] = NullableReportString("date-time");
            properties["failure"] = NullableReportReference(nameof(CatalogRevisionReportFailureV1), document);
            properties["report"] = NullableReportReference(reportName, document);
            if (operation == "validation")
            {
                wrapper.Required!.Add("projectionDerivationId");
                properties["projectionDerivationId"] = NullableReportString("uuid");
            }
        }

        void TransformEnum(string name, string[] values)
        {
            if (!schemas.TryGetValue(name, out var enumSchema) || enumSchema is not OpenApiSchema enumeration)
                return;
            // Null belongs to the containing nullable property, not the shared
            // closed enum. Otherwise null would match both oneOf branches.
            enumeration.Type = JsonSchemaType.String;
            enumeration.Enum = values.Select(value => (JsonNode)JsonValue.Create(value)!).ToList();
        }
    }

    private static OpenApiSchema NullableReportReference(string name, OpenApiDocument document) => new()
    {
        OneOf = [new OpenApiSchema { Type = JsonSchemaType.Null }, new OpenApiSchemaReference(name, document)]
    };

    private static OpenApiSchema NullableReportString(string format) => new()
    {
        Type = JsonSchemaType.String | JsonSchemaType.Null,
        Format = format
    };

    private static void RequireReportProperties(OpenApiSchema schema, string[] names)
    {
        schema.AdditionalPropertiesAllowed = false;
        schema.Required ??= new HashSet<string>();
        foreach (var name in names)
            schema.Required.Add(name);
    }

    private static void DescribeReportIdentity(OpenApiSchema schema)
    {
        if (schema.Properties is not { } properties)
            return;
        properties["derivationId"] = new OpenApiSchema
        {
            Type = JsonSchemaType.String,
            Format = "uuid",
            Description = "Canonical nonzero lowercase D-format UUID. A failed or pending attempt ID is not a retained success."
        };
        properties["generation"] = new OpenApiSchema
        {
            Type = JsonSchemaType.String,
            Pattern = "^[1-9][0-9]{0,18}$",
            Description = "Exact canonical positive decimal generation in 1..9223372036854775807; never a JSON number."
        };
    }

    private static OpenApiSchema ReportLiteral(string value) => new()
    {
        Type = JsonSchemaType.String,
        Enum = [JsonValue.Create(value)!],
        ReadOnly = true
    };

    private static void AddReportParameter(OpenApiOperation operation, string name, ParameterLocation location,
        bool required, string description, bool uuid = false, int? maximumLength = null)
    {
        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = name,
            In = location,
            Required = required,
            Description = description,
            Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = uuid ? "uuid" : null, MaxLength = maximumLength }
        });
    }
}
