using Braid.Contracts.Validation;

namespace Braid.Application;

internal static partial class RevisionReportSnapshots
{
    private sealed partial class SnapshotCopy
    {
        internal ValidationDiagnosticV1 ValidationDiagnostic(ValidationDiagnosticV1 value)
        {
            Required(value);
            return new(Text(value.Code), Known(value.Severity), Text(value.Message), value.MessageTruncated,
                value.NativeLocator is { } locator ? Locator(locator) : null,
                value.Owner is { } owner ? Owner(owner) : null, OptionalText(value.Location), value.LocationTruncated,
                value.ProjectionLocator is { } reference ? Reference(reference) : null);
        }

        private NativeDiagnosticOwnerV1 Owner(NativeDiagnosticOwnerV1 value) => Required(value) switch
        {
            GraphNodeNativeOwnerV1 item => new GraphNodeNativeOwnerV1(OptionalText(item.FunctionId), Text(item.NodeId)),
            UserFunctionNativeOwnerV1 item => new UserFunctionNativeOwnerV1(Text(item.FunctionId)),
            EndpointNativeOwnerV1 item => new EndpointNativeOwnerV1(Endpoint(item.Endpoint)),
            SchemaNodeNativeOwnerV1 item => new SchemaNodeNativeOwnerV1(Endpoint(item.Endpoint), List(item.Path, SchemaStep)),
            ScopeNativeOwnerV1 item => new ScopeNativeOwnerV1(Endpoint(item.Target), List(item.Path, ScopeStep)),
            FailureRuleNativeOwnerV1 item => new FailureRuleNativeOwnerV1(Text(item.Index)),
            _ => throw Integrity()
        };

        private NativeEndpointV1 Endpoint(NativeEndpointV1 value) => Required(value) switch
        {
            SourceNativeEndpointV1 => new SourceNativeEndpointV1(),
            TargetNativeEndpointV1 => new TargetNativeEndpointV1(),
            NamedSourceNativeEndpointV1 item => new NamedSourceNativeEndpointV1(Text(item.Index), Text(item.Name)),
            NamedTargetNativeEndpointV1 item => new NamedTargetNativeEndpointV1(Text(item.Index), Text(item.Name)),
            _ => throw Integrity()
        };

        private SchemaNativeOwnerStepV1 SchemaStep(SchemaNativeOwnerStepV1 value) => Required(value) switch
        {
            ChildSchemaNativeOwnerStepV1 item => new ChildSchemaNativeOwnerStepV1(Text(item.Index)),
            DynamicFieldSchemaNativeOwnerStepV1 => new DynamicFieldSchemaNativeOwnerStepV1(),
            ContainsSchemaNativeOwnerStepV1 item => new ContainsSchemaNativeOwnerStepV1(Text(item.Index)),
            DependentSchemaNativeOwnerStepV1 item => new DependentSchemaNativeOwnerStepV1(Text(item.Index)),
            _ => throw Integrity()
        };

        private ScopeNativeOwnerStepV1 ScopeStep(ScopeNativeOwnerStepV1 value) => Required(value) switch
        {
            ChildScopeNativeOwnerStepV1 item => new ChildScopeNativeOwnerStepV1(Text(item.Index)),
            DynamicChildScopeNativeOwnerStepV1 item => new DynamicChildScopeNativeOwnerStepV1(Text(item.Index)),
            SegmentScopeNativeOwnerStepV1 item => new SegmentScopeNativeOwnerStepV1(Text(item.Index)),
            _ => throw Integrity()
        };
    }
}
