using Weft.Contracts.Validation;

namespace Weft.MappingBackends;

public static partial class FerruleReportNormalizer
{
    private sealed partial class Context
    {
        internal NativeDiagnosticOwnerV1? Owner(MappingDiagnosticOwner? owner) => owner switch
        {
            null => null,
            GraphNodeDiagnosticOwner graph => new GraphNodeNativeOwnerV1(OptionalText(graph.FunctionId), Text(graph.NodeId, false)),
            UserFunctionDiagnosticOwner function => new UserFunctionNativeOwnerV1(Text(function.FunctionId, false)),
            EndpointDiagnosticOwner endpoint => new EndpointNativeOwnerV1(Endpoint(endpoint.Endpoint)),
            SchemaNodeDiagnosticOwner schema => new SchemaNodeNativeOwnerV1(Endpoint(schema.Endpoint),
                Copy(schema.Path, SchemaStep, Example ? InspectionPolicy.MaximumResponseItems : Limits.ItemLimit)),
            ScopeDiagnosticOwner scope => new ScopeNativeOwnerV1(Endpoint(scope.Target),
                Copy(scope.Path, ScopeStep, Example ? InspectionPolicy.MaximumResponseItems : Limits.ItemLimit)),
            FailureRuleDiagnosticOwner failure => new FailureRuleNativeOwnerV1(Text(failure.Index, false)),
            _ => throw new Weft.Application.RevisionReportValidationException(Weft.Application.RevisionReportValidationFailure.Integrity)
        };
        private NativeEndpointV1 Endpoint(MappingEndpoint endpoint) => endpoint switch
        {
            SourceMappingEndpoint => new SourceNativeEndpointV1(),
            TargetMappingEndpoint => new TargetNativeEndpointV1(),
            NamedSourceMappingEndpoint source => new NamedSourceNativeEndpointV1(Text(source.Index, false), Text(source.Name)),
            NamedTargetMappingEndpoint target => new NamedTargetNativeEndpointV1(Text(target.Index, false), Text(target.Name)),
            _ => throw new Weft.Application.RevisionReportValidationException(Weft.Application.RevisionReportValidationFailure.Integrity)
        };
        private SchemaNativeOwnerStepV1 SchemaStep(SchemaOwnerStep step) => step switch
        {
            ChildSchemaOwnerStep child => new ChildSchemaNativeOwnerStepV1(Text(child.Index, false)),
            DynamicFieldSchemaOwnerStep => new DynamicFieldSchemaNativeOwnerStepV1(),
            ContainsSchemaOwnerStep contains => new ContainsSchemaNativeOwnerStepV1(Text(contains.Index, false)),
            DependentSchemaOwnerStep dependent => new DependentSchemaNativeOwnerStepV1(Text(dependent.Index, false)),
            _ => throw new Weft.Application.RevisionReportValidationException(Weft.Application.RevisionReportValidationFailure.Integrity)
        };
        private ScopeNativeOwnerStepV1 ScopeStep(ScopeOwnerStep step) => step switch
        {
            ChildScopeOwnerStep child => new ChildScopeNativeOwnerStepV1(Text(child.Index, false)),
            DynamicChildScopeOwnerStep dynamicStep => new DynamicChildScopeNativeOwnerStepV1(Text(dynamicStep.Index, false)),
            SegmentScopeOwnerStep segment => new SegmentScopeNativeOwnerStepV1(Text(segment.Index, false)),
            _ => throw new Weft.Application.RevisionReportValidationException(Weft.Application.RevisionReportValidationFailure.Integrity)
        };
    }
}
