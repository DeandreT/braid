using System.Text.Json.Serialization;

namespace Braid.Contracts.Editing;

/// <summary>Ferrule's closed ferrule.edit.v1 intent vocabulary. All identities and scalar values are exact text.</summary>
public sealed record EditScriptV1(string Format, IReadOnlyList<EditOperationV1> Operations);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "op")]
[JsonDerivedType(typeof(BindEditV1), "bind")]
[JsonDerivedType(typeof(RetargetBindingEditV1), "retarget_binding")]
[JsonDerivedType(typeof(RemoveBindingEditV1), "remove_binding")]
[JsonDerivedType(typeof(ConnectEditV1), "connect")]
[JsonDerivedType(typeof(DisconnectEditV1), "disconnect")]
[JsonDerivedType(typeof(EditConstantV1), "edit_constant")]
[JsonDerivedType(typeof(EditCallV1), "edit_call")]
[JsonDerivedType(typeof(EditValueMapV1), "edit_value_map")]
[JsonDerivedType(typeof(RemoveNodesEditV1), "remove_nodes")]
[JsonDerivedType(typeof(AddScopeEditV1), "add_scope")]
[JsonDerivedType(typeof(RemoveScopeEditV1), "remove_scope")]
[JsonDerivedType(typeof(SetIterationEditV1), "set_iteration")]
[JsonDerivedType(typeof(SetFilterEditV1), "set_filter")]
[JsonDerivedType(typeof(SetSortEditV1), "set_sort")]
[JsonDerivedType(typeof(SetGroupingEditV1), "set_grouping")]
[JsonDerivedType(typeof(AddFailureRuleEditV1), "add_failure_rule")]
[JsonDerivedType(typeof(RemoveFailureRuleEditV1), "remove_failure_rule")]
[JsonDerivedType(typeof(MoveFailureRuleEditV1), "move_failure_rule")]
[JsonDerivedType(typeof(EditFailureRuleV1), "edit_failure_rule")]
public abstract record EditOperationV1;

public sealed record BindEditV1(EditBindingV1 Binding, EditExpressionV1 Expression) : EditOperationV1;
public sealed record RetargetBindingEditV1(EditBindingV1 Binding, EditBindingV1 To, string Node) : EditOperationV1;
public sealed record RemoveBindingEditV1(EditBindingV1 Binding) : EditOperationV1;
public sealed record ConnectEditV1(string Node, string Input, EditExpressionV1 Expression) : EditOperationV1;
public sealed record DisconnectEditV1(string Node, string Input, string? Replacement = null) : EditOperationV1;
public sealed record EditConstantV1(string Node, EditScalarV1 Value) : EditOperationV1;
public sealed record EditCallV1(string Node, string Function, IReadOnlyList<string> Args) : EditOperationV1;
public sealed record EditValueMapV1(string Node, EditValueMapDefinitionV1 Value) : EditOperationV1;
public sealed record RemoveNodesEditV1(EditScopeV1 Scope, IReadOnlyList<string> Nodes, IReadOnlyList<string> Replacements) : EditOperationV1;
public sealed record AddScopeEditV1(EditScopeV1 Parent, string Field) : EditOperationV1;
public sealed record RemoveScopeEditV1(EditScopeV1 Scope) : EditOperationV1;
public sealed record SetIterationEditV1(EditScopeV1 Scope, EditSourceCollectionV1? Source = null) : EditOperationV1;
public sealed record SetFilterEditV1(EditScopeV1 Scope, string? Node = null) : EditOperationV1;
public sealed record SetSortEditV1(EditScopeV1 Scope, IReadOnlyList<EditSortKeyV1> Keys) : EditOperationV1;
public sealed record SetGroupingEditV1(EditScopeV1 Scope, EditGroupingV1 Grouping) : EditOperationV1;
public sealed record AddFailureRuleEditV1(EditFailureRuleDefinitionV1 Rule) : EditOperationV1;
public sealed record RemoveFailureRuleEditV1(string Index) : EditOperationV1;
public sealed record MoveFailureRuleEditV1(string From, string To) : EditOperationV1;
public sealed record EditFailureRuleV1(string Index, EditFailureRuleDefinitionV1 Rule) : EditOperationV1;

public sealed record EditScopeV1(EditTargetV1 Target, IReadOnlyList<string> Children);
public sealed record EditBindingV1(EditScopeV1 Scope, string Field);
public sealed record EditSortKeyV1(string Node, bool Descending);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(PrimaryEditTargetV1), "primary")]
[JsonDerivedType(typeof(NamedEditTargetV1), "named")]
public abstract record EditTargetV1;
public sealed record PrimaryEditTargetV1 : EditTargetV1;
public sealed record NamedEditTargetV1(string Name) : EditTargetV1;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(LegacyEditCollectionV1), "legacy")]
[JsonDerivedType(typeof(PrimaryEditCollectionV1), "primary")]
[JsonDerivedType(typeof(NamedEditCollectionV1), "named")]
public abstract record EditSourceCollectionV1;
public sealed record LegacyEditCollectionV1(IReadOnlyList<string> Path) : EditSourceCollectionV1;
public sealed record PrimaryEditCollectionV1(IReadOnlyList<string> Path) : EditSourceCollectionV1;
public sealed record NamedEditCollectionV1(string Name, IReadOnlyList<string> Path) : EditSourceCollectionV1;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(PrimaryEditSourceV1), "primary")]
[JsonDerivedType(typeof(NamedEditSourceV1), "named")]
public abstract record EditSourceOwnerV1;
public sealed record PrimaryEditSourceV1 : EditSourceOwnerV1;
public sealed record NamedEditSourceV1(string Name) : EditSourceOwnerV1;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ExistingEditExpressionV1), "existing")]
[JsonDerivedType(typeof(SourceEditExpressionV1), "source")]
[JsonDerivedType(typeof(OwnedSourceEditExpressionV1), "owned_source")]
[JsonDerivedType(typeof(ConstantEditExpressionV1), "constant")]
[JsonDerivedType(typeof(CallEditExpressionV1), "call")]
public abstract record EditExpressionV1;
public sealed record ExistingEditExpressionV1(string Node) : EditExpressionV1;
public sealed record SourceEditExpressionV1(string Id, IReadOnlyList<string> Path, IReadOnlyList<string>? Frame = null) : EditExpressionV1;
public sealed record OwnedSourceEditExpressionV1(string Id, EditSourceOwnerV1 Owner, IReadOnlyList<string> Path, IReadOnlyList<string>? Frame = null) : EditExpressionV1;
public sealed record ConstantEditExpressionV1(string Id, EditScalarV1 Value) : EditExpressionV1;
public sealed record CallEditExpressionV1(string Id, string Function, IReadOnlyList<string> Args) : EditExpressionV1;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(NullEditScalarV1), "null")]
[JsonDerivedType(typeof(JsonNullEditScalarV1), "json_null")]
[JsonDerivedType(typeof(XmlNilEditScalarV1), "xml_nil")]
[JsonDerivedType(typeof(BoolEditScalarV1), "bool")]
[JsonDerivedType(typeof(IntEditScalarV1), "int")]
[JsonDerivedType(typeof(FloatEditScalarV1), "float")]
[JsonDerivedType(typeof(StringEditScalarV1), "string")]
public abstract record EditScalarV1;
public sealed record NullEditScalarV1 : EditScalarV1;
public sealed record JsonNullEditScalarV1 : EditScalarV1;
public sealed record XmlNilEditScalarV1 : EditScalarV1;
public sealed record BoolEditScalarV1(string Text) : EditScalarV1;
public sealed record IntEditScalarV1(string Text) : EditScalarV1;
public sealed record FloatEditScalarV1(string Text) : EditScalarV1;
public sealed record StringEditScalarV1(string Text) : EditScalarV1;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(StringEditScalarTypeV1), "string")]
[JsonDerivedType(typeof(IntEditScalarTypeV1), "int")]
[JsonDerivedType(typeof(FloatEditScalarTypeV1), "float")]
[JsonDerivedType(typeof(BoolEditScalarTypeV1), "bool")]
public abstract record EditScalarTypeV1;
public sealed record StringEditScalarTypeV1 : EditScalarTypeV1;
public sealed record IntEditScalarTypeV1 : EditScalarTypeV1;
public sealed record FloatEditScalarTypeV1 : EditScalarTypeV1;
public sealed record BoolEditScalarTypeV1 : EditScalarTypeV1;
public sealed record EditValueMapDefinitionV1(
    IReadOnlyList<EditValueMapEntryV1> Table,
    [property: JsonPropertyName("input_type")] EditScalarTypeV1? InputType = null, EditScalarV1? Default = null);
public sealed record EditValueMapEntryV1(EditScalarV1 From, EditScalarV1 To);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(NoEditGroupingV1), "none")]
[JsonDerivedType(typeof(ByEditGroupingV1), "by")]
[JsonDerivedType(typeof(AdjacentEditGroupingV1), "adjacent_by")]
[JsonDerivedType(typeof(StartingEditGroupingV1), "starting_with")]
[JsonDerivedType(typeof(EndingEditGroupingV1), "ending_with")]
[JsonDerivedType(typeof(BlocksEditGroupingV1), "into_blocks")]
public abstract record EditGroupingV1;
public sealed record NoEditGroupingV1 : EditGroupingV1;
public sealed record ByEditGroupingV1(string Node) : EditGroupingV1;
public sealed record AdjacentEditGroupingV1(string Node) : EditGroupingV1;
public sealed record StartingEditGroupingV1(string Node) : EditGroupingV1;
public sealed record EndingEditGroupingV1(string Node) : EditGroupingV1;
public sealed record BlocksEditGroupingV1(string Node) : EditGroupingV1;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(AllEditFailuresV1), "all")]
[JsonDerivedType(typeof(TrueEditFailuresV1), "when_true")]
[JsonDerivedType(typeof(FalseEditFailuresV1), "when_false")]
public abstract record EditFailureSelectionV1;
public sealed record AllEditFailuresV1 : EditFailureSelectionV1;
public sealed record TrueEditFailuresV1(EditExpressionV1 Expression) : EditFailureSelectionV1;
public sealed record FalseEditFailuresV1(EditExpressionV1 Expression) : EditFailureSelectionV1;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(GenerateEditSequenceV1), "generate")]
[JsonDerivedType(typeof(TokenizeEditSequenceV1), "tokenize")]
[JsonDerivedType(typeof(TokenizeLengthEditSequenceV1), "tokenize_by_length")]
[JsonDerivedType(typeof(TokenizeRegexEditSequenceV1), "tokenize_regex")]
public abstract record EditGeneratedSequenceV1;
public sealed record GenerateEditSequenceV1(EditExpressionV1 To, string Item, EditExpressionV1? From = null) : EditGeneratedSequenceV1;
public sealed record TokenizeEditSequenceV1(EditExpressionV1 Input, EditExpressionV1 Delimiter, string Item) : EditGeneratedSequenceV1;
public sealed record TokenizeLengthEditSequenceV1(EditExpressionV1 Input, EditExpressionV1 Length, string Item) : EditGeneratedSequenceV1;
public sealed record TokenizeRegexEditSequenceV1(EditExpressionV1 Input, EditExpressionV1 Pattern, string Item, EditExpressionV1? Flags = null) : EditGeneratedSequenceV1;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(SourceEditFailureIterationV1), "source")]
[JsonDerivedType(typeof(GeneratedEditFailureIterationV1), "generated")]
public abstract record EditFailureIterationV1;
public sealed record SourceEditFailureIterationV1(EditSourceCollectionV1 Collection) : EditFailureIterationV1;
public sealed record GeneratedEditFailureIterationV1(EditGeneratedSequenceV1 Sequence) : EditFailureIterationV1;
public sealed record EditFailureRuleDefinitionV1(EditFailureIterationV1 Iteration, EditFailureSelectionV1 Selection, EditExpressionV1? Message = null);
