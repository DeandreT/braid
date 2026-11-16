using System.Diagnostics.CodeAnalysis;
using System.Text;
using Braid.Contracts.Projections;
using Braid.Domain;

namespace Braid.Api.Fixtures;

/// <summary>Self-authored explanation fixtures; this is not a mapping execution engine.</summary>
public static class ProjectionFixtures
{
    public static IReadOnlyList<string> Ids { get; } = Array.AsReadOnly<string>(["direct", "rich", "partial", "unavailable"]);

    public static bool TryGet(string id, [NotNullWhen(true)] out ProjectionDocumentV1? document, int maximumNodes = 256, int maximumDiagnostics = 32)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumNodes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumDiagnostics);
        if (!Ids.Contains(id, StringComparer.Ordinal))
        {
            document = null;
            return false;
        }

        var artifact = GetNativeArtifact(id);
        document = SyntheticProjectionBuilder.Build(id, artifact);
        var nodeCount = document.Boundaries.Count + document.Fields.Count + document.Parameters.Count
            + document.Rules.Count + document.Controls.Count + document.Relationships.Count;
        if (nodeCount > maximumNodes)
            document = SyntheticProjectionBuilder.Limited(document, "projection.nodeLimit", "The explanation exceeds the configured node limit.");
        else if (document.Diagnostics.Count > maximumDiagnostics)
            document = SyntheticProjectionBuilder.Limited(document, "projection.diagnosticLimit", "The explanation exceeds the configured diagnostic limit.");

        return true;
    }

    public static NativeArtifact GetNativeArtifact(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        var number = id switch
        {
            "direct" => "1",
            "rich" => "2",
            "partial" => "3",
            "unavailable" => "4",
            _ => throw new ArgumentException("Unknown synthetic projection fixture.", nameof(id))
        };
        var scope = new RevisionScope(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Guid.Parse("33333333-3333-3333-3333-33333333333" + number),
            Guid.Parse("44444444-4444-4444-4444-44444444444" + number));
        return NativeArtifact.Admit(scope, Encoding.UTF8.GetBytes(NativeSource(id)), 16 * 1024);
    }

    private static string NativeSource(string id) => id switch
    {
        "direct" => """
            {
              "format": "braid.synthetic.v1", "fixture": "direct",
              "boundaries": [{"name":"source"}, {"name":"target"}],
              "inputNodes": {"root":{}, "customer.name":{"id":9007199254740993}, "customer":{"name":{}}},
              "outputNodes": {"root":{}, "display.name":{}, "a~b/c":{}},
              "rules": [{"from":"customer.name","to":"display.name"}, {"from":["customer","name"],"to":["display","name"]}],
              "unknownMetadata": {"preserveWhitespace":true, "numericBits":"3ff0000000000001"}
            }
            """,
        "rich" => """
            {
              "format": "braid.synthetic.v1", "fixture": "rich",
              "boundaries": [{"name":"orders"}, {"name":"customers"}, {"name":"rates"}, {"name":"invoices"}, {"name":"audit"}, {"name":"rejected"}],
              "orders": {"root":{}, "customerId":{"id":12,"function":"orders"}, "amount":{}, "currency":{}},
              "customers": {"root":{}, "id":{"id":12,"function":"customers","namespace":"urn:braid:identity"}, "tier":{"namespace":"urn:braid:customers"}},
              "rates": {"root":{}, "value":{"index":"0"}},
              "invoices": {"root":{}, "customerId":{}, "total":{}, "tier":{}, "flag":{}},
              "audit": {"root":{}, "sequence":{"nativeId":9007199254740993,"function":"audit"}, "negativeZero":{"bits":"8000000000000000"}, "region":{}},
              "rejected": {"root":{}, "reason":{}},
              "parameters": [{"name":"threshold","decimal":"0.1000000000000000000000000001"}, {"name":"region","default":"west"}],
              "rules": [{"name":"customerId"}, {"name":"total"}, {"name":"tier"}, {"name":"flag"}, {"name":"sequence","integer":"9007199254740993"}, {"name":"negativeZero","bits":"8000000000000000"}, {"name":"region"}, {"name":"reason"}],
              "controls": [{"kind":"iteration"}, {"kind":"filter"}, {"kind":"join"}, {"kind":"grouping"}, {"kind":"sorting"}, {"kind":"window"}, {"kind":"failure"}, {"kind":"ordering"}],
              "unknownMetadata": {"keep": "  exact native metadata  "}
            }
            """,
        "partial" => """
            {
              "format": "braid.synthetic.v1", "fixture": "partial",
              "boundaries": [{"name":"source"}, {"name":"target"}],
              "inputNodes": {"root":{}, "customer.name":{"id":9007199254740993}, "customer":{"name":{}}},
              "outputNodes": {"root":{}, "display.name":{}, "a~b/c":{}},
              "rules": [{"from":"customer.name","to":"display.name","unknown":{"kind":"futureTransform","token":"retain-me"}}, {"from":["customer","name"],"to":["display","name"]}]
            }
            """,
        "unavailable" => """
            {
              "format": "braid.synthetic.v1", "fixture": "unavailable",
              "opaquePayload": {"kind":"futureEnvelope","nativeId":9007199254740993, "retain":"original bytes"}
            }
            """,
        _ => throw new ArgumentException("Unknown synthetic projection fixture.", nameof(id))
    };
}
