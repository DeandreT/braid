import type { components } from "../src/api/generated";
import {
  controlledData,
  type ControlledReports,
  type Data,
} from "./revision-reports-controlled.fixture";

type Schema = components["schemas"];
export type Projection = Schema["ProjectionDocumentV1"];

export function installDisplay(reports: ControlledReports, data: Data): void {
  // The shared boundary helper builds its linked-location case from diagnostics[0].
  // Register metadata with that template, then install this display's independent validation.
  reports.add({ ...data, validation: controlledData().validation });
  reports.set("GET", data.reportPath("validation"), { json: data.validation });
  if (data.validation.result)
    reports.set(
      "GET",
      data.reportPath("validation", data.validation.result.derivationId),
      { json: data.validation },
    );
  if (data.validation.result && data.projection.result) {
    const linked = structuredClone(data.validation);
    linked.projectionDerivationId = data.projection.result.derivationId;
    reports.set(
      "GET",
      data.reportPath("validation", data.validation.result.derivationId),
      { json: linked },
      `projectionDerivationId=${data.projection.result.derivationId}`,
    );
  }
}

// Display cases are controlled catalog reports, not claims of native-worker qualification.
// Keep the revision, digest and provenance consistent with the catalog metadata.
export function displayData(rich = false) {
  const data = controlledData();
  const document = data.projection.report!.document!;
  const template = document.fields[0];
  document.coverage = { status: "complete", gaps: [] };
  document.fields = [];
  document.rules = [];
  document.boundaries[0].name = "target";
  document.boundaries.push({
    ...document.boundaries[0],
    id: "source",
    name: rich ? "orders" : "source",
    role: "primaryInput",
  });
  const field = (
    id: string,
    boundaryId: string,
    path: Schema["PathSegment"][],
  ) => {
    const value = { ...template, id, name: id, boundaryId, path };
    document.fields.push(value);
    return value;
  };
  const source = field(
    "source-value",
    "source",
    rich
      ? [{ kind: "anyItem" }, { kind: "field", name: "amount" }]
      : [{ kind: "field", name: "customer.name" }],
  );
  const output = field(
    "output-value",
    "controlled-output",
    rich
      ? [{ kind: "anyItem" }, { kind: "field", name: "total" }]
      : [{ kind: "field", name: "display.name" }],
  );
  const rule: Schema["ProjectionRule"] = {
    id: "display-rule",
    outputFieldId: output.id,
    summary: "Populate displayed value",
    nativeLocator: template.nativeLocator,
    dependencies: [{ kind: "field", fieldId: source.id, role: "value" }],
    details: [
      { kind: "literal", value: { kind: "string", value: "display value" } },
    ],
  };
  document.rules.push(rule);
  const nested = field("nested-output", "controlled-output", [
    { kind: "field", name: "display" },
    { kind: "field", name: "name" },
  ]);
  document.rules.push({ ...rule, id: "nested-rule", outputFieldId: nested.id });
  data.validation.status = "succeeded";
  data.validation.report = {
    ...data.validation.report!,
    status: "succeeded",
    isValid: true,
    totalIssueCount: 0,
    emittedIssueCount: 0,
    truncated: false,
    diagnostics: [],
  };
  if (rich) {
    document.boundaries.push({
      ...document.boundaries[0],
      id: "customers",
      name: "customers",
      role: "namedInput",
    });
    const tier = field("tier", "customers", [
      { kind: "field", name: "customer", namespaceUri: "urn:weft:customers" },
      { kind: "field", name: "tier", namespaceUri: "urn:weft:customers" },
    ]);
    const identity = field("customer-id", "customers", [
      { kind: "field", name: "customer", namespaceUri: "urn:weft:customers" },
      { kind: "attribute", name: "id", namespaceUri: "urn:weft:identity" },
    ]);
    document.controls.push({
      id: "customer-join",
      kind: "join",
      summary: "Join customer identity",
      appliesTo: [{ kind: "field", fieldId: output.id }],
      dependencies: [
        { kind: "field", fieldId: identity.id, role: "condition" },
      ],
      details: [
        {
          kind: "reference",
          reference: { kind: "field", fieldId: identity.id },
        },
      ],
      nativeLocator: template.nativeLocator,
    });
    document.boundaries.push({
      ...document.boundaries[0],
      id: "rates",
      name: "rates",
      role: "namedInput",
    });
    const rate = field("rate", "rates", [
      { kind: "field", name: "rates" },
      { kind: "index", index: "0" },
      { kind: "field", name: "value" },
    ]);
    rule.dependencies.push(
      { kind: "field", fieldId: tier.id, role: "condition" },
      { kind: "field", fieldId: rate.id, role: "value" },
      {
        kind: "constant",
        value: {
          kind: "exactNumber",
          flavor: "decimal",
          value: "0.1000000000000000000000000001",
        },
      },
    );
    document.capabilities = [
      {
        operation: "preview",
        status: "notQualified",
        profile: "display-only",
        boundaryIds: [],
        conditions: [],
      },
    ];
  }
  return data;
}
