using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.OpenApi;
using Weft.Application;
using Weft.Contracts.Catalog;
using Weft.Domain;

namespace Weft.Api.Catalog;

public static partial class CatalogEndpoints
{
    private static void DescribeInterchangeRead<T>(RouteHandlerBuilder route, string name, string summary)
    {
        Describe<T>(route, name, summary);
        route.AddOpenApiOperationTransformer((operation, _, _) =>
        {
            AddIngestionNoStoreHeaders(operation);
            return Task.CompletedTask;
        });
    }
    private static RouteHandlerBuilder DescribeInterchangeReview(RouteHandlerBuilder route, string name, bool import, bool append)
    {
        Describe<CatalogInterchangeReviewV1>(route, name, import ? "Convert an immutable MFD package for review without publishing a revision." :
            "Review a revision's MFD export and all conversion warnings before download.");
        route.Produces<CatalogErrorV1>(412).Produces<CatalogErrorV1>(413).Produces<CatalogErrorV1>(415)
            .WithDescription(import ? "Consumes canonical application/vnd.weft.mfd-package.v1 framing. At most 128 files, 4194304 bytes each, 16777216 aggregate content bytes, 256 UTF-8 bytes per path. This route alone permits the bounded package body; native upload and global limits are unchanged. Fresh upload authority precedes body consumption. No Idempotency-Key or conditional headers. Nothing is published before explicit confirmation." :
                "Empty body and no query, Idempotency-Key or conditional headers. Fresh scoped read authority is checked before and after private native storage and export. The returned review is original-actor scoped, immutable and expires after one hour.")
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                AddInterchangeRequestHeaders(operation);
                if (import)
                {
                    operation.RequestBody = new OpenApiRequestBody
                    {
                        Required = true,
                        Content = new Dictionary<string, OpenApiMediaType>
                        {
                            [CatalogInterchangeParser.MediaType] = new()
                            {
                                Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary", MaxLength = InterchangePackage.MaximumEncodedBytes }
                            }
                        }
                    };
                    if (append)
                        AddRevisionParameter(operation, "expectedHeadRevisionId", ParameterLocation.Query, true,
                            "Exact canonical committed head the reviewed append must still match at publication.", uuid: true);
                    else
                    {
                        AddRevisionParameter(operation, "name", ParameterLocation.Query, true, "Mapping name, at most 200 Unicode scalars.");
                        AddRevisionParameter(operation, "description", ParameterLocation.Query, false, "Description, at most 4000 Unicode scalars; default empty.");
                        AddRevisionParameter(operation, "backendId", ParameterLocation.Query, true, "Exact registered backend ID; at most 128 UTF-8 bytes.");
                    }
                    AddRevisionParameter(operation, "Content-Encoding", ParameterLocation.Header, false, "Absent or exactly identity; compression is not accepted.");
                }
                AddIngestionNoStoreHeaders(operation);
                return Task.CompletedTask;
            });
        return route;
    }

    private static void DescribeInterchangeConfirmation(RouteHandlerBuilder route, bool export)
    {
        if (export)
        {
            route.WithName("DownloadMfdExport").WithTags("Catalog")
                .WithSummary("Download the exact reviewed export after explicit warning acknowledgement.")
                .Produces<byte[]>(200, "application/xml").Produces<byte[]>(200, "application/zip");
            foreach (var status in new[] { 400, 401, 403, 404, 412, 413, 415, 500, 503 }) route.Produces<CatalogErrorV1>(status);
        }
        else DescribeIngestionMutation(route, "ConfirmMfdImport", "Publish only server-owned native bytes from the exact reviewed import.");
        route.Produces<CatalogErrorV1>(412).Produces<CatalogErrorV1>(413).Produces<CatalogErrorV1>(415)
            .WithDescription("Strict JSON object with exactly reviewDigest (canonical lowercase SHA-256) and acknowledgeWarnings (boolean), at most 1024 bytes, application/json with no media parameters or compression. Unknown, duplicate or incorrectly typed fields are refused. No query or conditional headers. The original actor's current authority is checked again. " +
                (export ? "No Idempotency-Key. Warnings must be acknowledged before bytes are returned; repeated downloads return the same reviewed package. Multiple-file packages download as a deterministic ZIP; a single design downloads as .mfd. No conversion is run on this route." :
                "Idempotency-Key must equal reviewId. Reviewed package, native bytes, complete warnings, engine identity and append head are immutable. Confirmation never accepts replacement native bytes or reruns conversion."))
            .AddOpenApiOperationTransformer(async (operation, context, token) =>
            {
                AddInterchangeRequestHeaders(operation);
                _ = await context.GetOrCreateSchemaAsync(typeof(CatalogInterchangeConfirmationV1), null, token);
                var schema = new OpenApiSchema
                {
                    Type = JsonSchemaType.Object,
                    AdditionalPropertiesAllowed = false,
                    Required = new HashSet<string> { "reviewDigest", "acknowledgeWarnings" },
                    Properties = new Dictionary<string, IOpenApiSchema>
                    {
                        ["reviewDigest"] = new OpenApiSchema { Type = JsonSchemaType.String, Pattern = "^[0-9a-f]{64}$", MinLength = 64, MaxLength = 64 },
                        ["acknowledgeWarnings"] = new OpenApiSchema { Type = JsonSchemaType.Boolean }
                    }
                };
                operation.RequestBody = new OpenApiRequestBody
                {
                    Required = true,
                    Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = new() { Schema = schema } }
                };
                if (!export) AddRevisionParameter(operation, "Idempotency-Key", ParameterLocation.Header, true, "Canonical reviewId UUID; one value.", uuid: true);
                if (export && operation.Responses?.TryGetValue("200", out var response) == true && response is OpenApiResponse success)
                {
                    success.Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["application/xml"] = new() { Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" } },
                        ["application/zip"] = new() { Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" } }
                    };
                    success.Headers ??= new Dictionary<string, IOpenApiHeader>();
                    success.Headers["X-Content-Sha256"] = new OpenApiHeader { Description = "SHA-256 of the exact download bytes.", Schema = new OpenApiSchema { Type = JsonSchemaType.String } };
                    success.Headers["Content-Disposition"] = new OpenApiHeader { Description = "Attachment named mapping.mfd or mapping.zip.", Schema = new OpenApiSchema { Type = JsonSchemaType.String } };
                }
                AddIngestionNoStoreHeaders(operation);
            });
    }

    private static void AddInterchangeRequestHeaders(OpenApiOperation operation)
    {
        AddRevisionParameter(operation, "Origin", ParameterLocation.Header, true, "Exact configured browser origin.");
        AddRevisionParameter(operation, CatalogRequestBoundary.RequestTokenHeader, ParameterLocation.Header, true,
            "Current cookie-bound request token obtained from catalog context; no state change is admitted without it.");
    }
}
