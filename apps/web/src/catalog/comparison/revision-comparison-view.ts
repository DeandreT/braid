import {
  pathText,
  type NodeReference,
  type ProjectionView,
} from "../../workbench/projection-view";
import {
  comparisonEntities,
  type ComparisonAspect,
  type ComparisonCandidate,
  type ComparisonEntity,
  type ComparisonFacet,
  type ComparisonItem,
  type ComparisonMatch,
  type ComparisonReason,
  type ComparisonSideName,
  type RevisionComparison,
} from "./revision-comparison-types";

export interface ComparisonCandidateView {
  readonly side: ComparisonSideName;
  readonly label: string;
  /** A node of the displayed saved explanation for that side, or null when none can be shown. */
  readonly reference: NodeReference | null;
}

export interface ComparisonFacetView {
  readonly aspect: ComparisonAspect;
  readonly label: string;
  readonly state: "changed" | "changedUncertain" | "uncertain" | "unchanged";
  readonly text: string;
  readonly reasons: readonly string[];
}

export interface ComparisonItemView {
  readonly id: string;
  readonly entity: ComparisonEntity;
  readonly match: ComparisonMatch;
  readonly title: string;
  readonly left: readonly ComparisonCandidateView[];
  readonly right: readonly ComparisonCandidateView[];
  readonly leftOmitted: number;
  readonly rightOmitted: number;
  readonly facets: readonly ComparisonFacetView[];
  readonly changed: boolean;
  /** Every observed difference is in labels or summaries, not behavior aspects. */
  readonly presentationOnly: boolean;
  readonly uncertain: boolean;
  /** Matched with every compared aspect observed equal; never inferred for partial evidence. */
  readonly unchanged: boolean;
}

export interface ComparisonGroupView {
  readonly entity: ComparisonEntity;
  readonly id: string;
  readonly title: string;
  readonly items: readonly ComparisonItemView[];
  readonly changed: number;
  readonly uncertain: number;
  readonly unchanged: number;
  readonly onlyLeft: number;
  readonly onlyRight: number;
  readonly ambiguous: number;
}

const entityTitles: Record<ComparisonEntity, string> = {
  boundary: "Inputs and outputs",
  field: "Fields",
  parameter: "Parameters",
  rule: "Rules",
  control: "Controls",
  relationship: "Relationships",
  validation: "Validation",
};
const entityNames: Record<ComparisonEntity, string> = {
  boundary: "input or output",
  field: "field",
  parameter: "parameter",
  rule: "rule",
  control: "control",
  relationship: "relationship",
  validation: "validation",
};
const aspectLabels: Record<ComparisonAspect, string> = {
  shape: "Structure",
  dataType: "Data type",
  cardinality: "Repetition",
  requirement: "Requirement",
  nullability: "Nullability",
  value: "Value sources",
  condition: "Conditions",
  default: "Defaults",
  details: "Rule details",
  control: "Control behavior",
  relationship: "Relationship",
  presentation: "Labels and summaries (presentation only)",
  validationValidity: "Validity",
  validationCounts: "Notice counts",
};
const reasonLabels: Record<ComparisonReason, string> = {
  incompleteCoverage:
    "An explanation is incomplete, so absence on that side is not confirmed.",
  unknownFact: "A saved explanation does not know this fact.",
  unsupportedDetail: "A construct is not supported by the explanation.",
  incompatibleProfile:
    "The explanations come from different engine or adapter profiles, so expression operations are not compared.",
  ambiguousMatch: "More than one counterpart is possible; none was chosen.",
  unresolvedReference:
    "A referenced item could not be paired across revisions.",
  missingValidation: "One side has no saved validation in this comparison.",
  truncatedValidation: "Validation notices or their text were shortened.",
  candidatesOmitted: "Some possible counterparts were omitted.",
  itemsOmitted: "Some comparison items were omitted.",
};
const relationshipKinds: Record<string, string> = {
  dependsOn: "depends on",
  produces: "produces",
  governedBy: "is governed by",
  boundaryMember: "belongs to",
};
const boundaryRoles: Record<string, string> = {
  primaryInput: "primary input",
  namedInput: "named input",
  primaryOutput: "primary output",
  namedOutput: "named output",
};

export function matchLabel(match: ComparisonMatch): string {
  return {
    matched: "Matched",
    onlyLeft: "Only in left",
    onlyRight: "Only in right",
    ambiguous: "Ambiguous",
  }[match];
}

export function reasonLabel(reason: ComparisonReason): string {
  return reasonLabels[reason];
}

export function entityTitle(entity: ComparisonEntity): string {
  return entityTitles[entity];
}

/** Report-local node IDs are only resolved inside the exact saved explanation they came from. */
export function comparisonGroups(
  comparison: RevisionComparison,
  left: ProjectionView,
  right: ProjectionView,
): ComparisonGroupView[] {
  const items = comparison.items.map((item, index) =>
    itemView(item, index, left, right),
  );
  return comparisonEntities.flatMap((entity) => {
    const grouped = items.filter((item) => item.entity === entity);
    if (grouped.length === 0) return [];
    return [
      {
        entity,
        id: `comparison-group-${entity}`,
        title: entityTitles[entity],
        items: grouped,
        changed: grouped.filter((item) => item.changed).length,
        uncertain: grouped.filter((item) => item.uncertain).length,
        unchanged: grouped.filter((item) => item.unchanged).length,
        onlyLeft: grouped.filter((item) => item.match === "onlyLeft").length,
        onlyRight: grouped.filter((item) => item.match === "onlyRight").length,
        ambiguous: grouped.filter((item) => item.match === "ambiguous").length,
      },
    ];
  });
}

function itemView(
  item: ComparisonItem,
  index: number,
  leftView: ProjectionView,
  rightView: ProjectionView,
): ComparisonItemView {
  const left = item.left.map((candidate) =>
    candidateView(candidate, "left", leftView),
  );
  const right = item.right.map((candidate) =>
    candidateView(candidate, "right", rightView),
  );
  const facets = item.facets.map((facet) => facetView(facet, item));
  const changed = item.facets.some((facet) => facet.hasObservedDifference);
  const uncertain = item.facets.some((facet) => facet.hasUncertainty);
  return {
    id: `comparison-item-${index}`,
    entity: item.entity,
    match: item.match,
    title: title(item, left, right),
    left,
    right,
    leftOmitted: item.leftCandidateCount - item.left.length,
    rightOmitted: item.rightCandidateCount - item.right.length,
    facets,
    changed,
    presentationOnly:
      changed &&
      item.match === "matched" &&
      item.facets.every(
        (facet) =>
          !facet.hasObservedDifference || facet.aspect === "presentation",
      ),
    uncertain,
    unchanged: item.match === "matched" && !changed && !uncertain,
  };
}

function title(
  item: ComparisonItem,
  left: readonly ComparisonCandidateView[],
  right: readonly ComparisonCandidateView[],
): string {
  if (item.entity === "validation") return "Native validation";
  const name = entityNames[item.entity];
  const l = left[0]?.label ?? `Omitted ${name}`,
    r = right[0]?.label ?? `Omitted ${name}`;
  switch (item.match) {
    case "matched":
      return l === r ? l : `${l} → ${r}`;
    case "onlyLeft":
      return l;
    case "onlyRight":
      return r;
    default: {
      const total = item.leftCandidateCount + item.rightCandidateCount;
      return `Ambiguous ${name}: ${total} possible ${total === 1 ? "match" : "matches"}`;
    }
  }
}

function candidateView(
  candidate: ComparisonCandidate,
  side: ComparisonSideName,
  view: ProjectionView,
): ComparisonCandidateView {
  if (candidate.relationshipId !== null) {
    const relationship = view.document.relationships.find(
      (value) => value.id === candidate.relationshipId,
    );
    return relationship
      ? {
          side,
          label: `${nodeLabel(view, relationship.from)} ${relationshipKinds[relationship.kind] ?? relationship.kind} ${nodeLabel(view, relationship.to)}`,
          reference: available(view, relationship.from)
            ? relationship.from
            : null,
        }
      : {
          side,
          label: "Relationship not in the displayed report",
          reference: null,
        };
  }
  const node = candidate.node!;
  return available(view, node)
    ? { side, label: nodeLabel(view, node), reference: node }
    : { side, label: "Item not in the displayed report", reference: null };
}

function available(view: ProjectionView, node: NodeReference): boolean {
  switch (node.kind) {
    case "boundary":
      return view.boundaries.has(node.boundaryId);
    case "field":
      return view.fields.has(node.fieldId);
    case "parameter":
      return view.parameters.has(node.parameterId);
    case "rule":
      return view.rules.has(node.ruleId);
    case "control":
      return view.controls.has(node.controlId);
    default:
      return false;
  }
}

function nodeLabel(view: ProjectionView, node: NodeReference): string {
  switch (node.kind) {
    case "boundary": {
      const boundary = view.boundaries.get(node.boundaryId);
      return boundary
        ? `${boundary.name} (${boundaryRoles[boundary.role] ?? boundary.role})`
        : "Boundary unavailable";
    }
    case "rule": {
      const rule = view.rules.get(node.ruleId);
      const field = rule ? view.fields.get(rule.outputFieldId) : undefined;
      if (!rule) return "Rule unavailable";
      return field
        ? `Rule for ${view.fieldLabel(field)}: ${rule.summary}`
        : `Rule: ${rule.summary}`;
    }
    case "field": {
      const field = view.fields.get(node.fieldId);
      return field
        ? `${view.boundaries.get(field.boundaryId)?.name ?? "Unknown boundary"} ${pathText(field.path)}`
        : "Field unavailable";
    }
    default:
      return view.label(node);
  }
}

function facetView(
  facet: ComparisonFacet,
  item: ComparisonItem,
): ComparisonFacetView {
  const state = facet.hasObservedDifference
    ? facet.hasUncertainty
      ? "changedUncertain"
      : "changed"
    : facet.hasUncertainty
      ? "uncertain"
      : "unchanged";
  let text = {
    changed: "Changed",
    changedUncertain: "Changed; some evidence is uncertain",
    uncertain: "Uncertain",
    unchanged: "No observed difference",
  }[state];
  // One-sided structural items describe absence; validation keeps its own facets.
  if (item.entity !== "validation") {
    if (item.match === "onlyLeft" || item.match === "onlyRight")
      text = facet.hasObservedDifference
        ? "Absent from the other revision"
        : "Absence not confirmed";
    else if (item.match === "ambiguous") text = "Not paired";
  }
  return {
    aspect: facet.aspect,
    label: aspectLabels[facet.aspect],
    state,
    text,
    reasons: facet.reasons.map((reason) => reasonLabels[reason]),
  };
}
