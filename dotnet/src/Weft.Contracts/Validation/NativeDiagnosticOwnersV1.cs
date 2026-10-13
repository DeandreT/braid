using System.Text.Json.Serialization;

namespace Weft.Contracts.Validation;

/// <summary>Native identities are evidence within one artifact, not projected node IDs.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(GraphNodeNativeOwnerV1), "graphNode")]
[JsonDerivedType(typeof(UserFunctionNativeOwnerV1), "userFunction")]
[JsonDerivedType(typeof(EndpointNativeOwnerV1), "endpoint")]
[JsonDerivedType(typeof(SchemaNodeNativeOwnerV1), "schemaNode")]
[JsonDerivedType(typeof(ScopeNativeOwnerV1), "scope")]
[JsonDerivedType(typeof(FailureRuleNativeOwnerV1), "failureRule")]
public abstract record NativeDiagnosticOwnerV1;

public sealed record GraphNodeNativeOwnerV1(string? FunctionId, string NodeId) : NativeDiagnosticOwnerV1;
public sealed record UserFunctionNativeOwnerV1(string FunctionId) : NativeDiagnosticOwnerV1;
public sealed record EndpointNativeOwnerV1(NativeEndpointV1 Endpoint) : NativeDiagnosticOwnerV1;
public sealed record SchemaNodeNativeOwnerV1(NativeEndpointV1 Endpoint, IReadOnlyList<SchemaNativeOwnerStepV1> Path) : NativeDiagnosticOwnerV1;
public sealed record ScopeNativeOwnerV1(NativeEndpointV1 Target, IReadOnlyList<ScopeNativeOwnerStepV1> Path) : NativeDiagnosticOwnerV1;
public sealed record FailureRuleNativeOwnerV1(string Index) : NativeDiagnosticOwnerV1;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(SourceNativeEndpointV1), "source")]
[JsonDerivedType(typeof(TargetNativeEndpointV1), "target")]
[JsonDerivedType(typeof(NamedSourceNativeEndpointV1), "namedSource")]
[JsonDerivedType(typeof(NamedTargetNativeEndpointV1), "namedTarget")]
public abstract record NativeEndpointV1;

public sealed record SourceNativeEndpointV1 : NativeEndpointV1;
public sealed record TargetNativeEndpointV1 : NativeEndpointV1;
public sealed record NamedSourceNativeEndpointV1(string Index, string Name) : NativeEndpointV1;
public sealed record NamedTargetNativeEndpointV1(string Index, string Name) : NativeEndpointV1;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ChildSchemaNativeOwnerStepV1), "child")]
[JsonDerivedType(typeof(DynamicFieldSchemaNativeOwnerStepV1), "dynamicField")]
[JsonDerivedType(typeof(ContainsSchemaNativeOwnerStepV1), "contains")]
[JsonDerivedType(typeof(DependentSchemaNativeOwnerStepV1), "dependentSchema")]
public abstract record SchemaNativeOwnerStepV1;

public sealed record ChildSchemaNativeOwnerStepV1(string Index) : SchemaNativeOwnerStepV1;
public sealed record DynamicFieldSchemaNativeOwnerStepV1 : SchemaNativeOwnerStepV1;
public sealed record ContainsSchemaNativeOwnerStepV1(string Index) : SchemaNativeOwnerStepV1;
public sealed record DependentSchemaNativeOwnerStepV1(string Index) : SchemaNativeOwnerStepV1;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ChildScopeNativeOwnerStepV1), "child")]
[JsonDerivedType(typeof(DynamicChildScopeNativeOwnerStepV1), "dynamicChild")]
[JsonDerivedType(typeof(SegmentScopeNativeOwnerStepV1), "segment")]
public abstract record ScopeNativeOwnerStepV1;

public sealed record ChildScopeNativeOwnerStepV1(string Index) : ScopeNativeOwnerStepV1;
public sealed record DynamicChildScopeNativeOwnerStepV1(string Index) : ScopeNativeOwnerStepV1;
public sealed record SegmentScopeNativeOwnerStepV1(string Index) : ScopeNativeOwnerStepV1;
