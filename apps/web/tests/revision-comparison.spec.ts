import { expect, test, type Locator, type Page } from "@playwright/test";
import {
  ComparisonFixture,
  P,
  REV,
  V,
  comparison,
  comparisonQuery,
  facet,
  item,
  node,
  notDerived,
  pairedItems,
  projection,
} from "./revision-comparison.fixture";

const pageErrors = new WeakMap<Page, string[]>();
const fixtures = new WeakMap<Page, ComparisonFixture>();
test.beforeEach(async ({ page }) => {
  const errors: string[] = [];
  pageErrors.set(page, errors);
  page.on("pageerror", (error) => errors.push(error.message));
  const fixture = new ComparisonFixture(page);
  fixtures.set(page, fixture);
  await fixture.install();
});
test.afterEach(({ page }) => {
  const fixture = fixtures.get(page)!;
  expect(pageErrors.get(page)).toEqual([]);
  expect(fixture.unexpected).toEqual([]);
  // Comparison never derives, repairs or starts report work.
  expect(fixture.posts).toEqual([]);
});

const results = (page: Page) =>
  page.getByRole("region", { name: "Comparison", exact: true });
const side = (page: Page, name: "Left revision" | "Right revision") =>
  page.getByRole("region", { name, exact: true });
const group = (page: Page, title: string) =>
  results(page).getByRole("region", { name: title, exact: true });
const itemNamed = (scope: Locator, title: string) =>
  scope.getByRole("listitem", { name: title, exact: true });

async function openPaired(page: Page, fixture: ComparisonFixture) {
  await page.goto(fixture.comparisonPage(`left=${REV[0]}&right=${REV[2]}`));
  await expect(page.getByRole("heading", { level: 1 })).toHaveText(
    "Compare revisions",
  );
  await expect(results(page).locator(".comparison-headline")).toBeVisible();
}

test("the mapping and revision pages open a comparison that pairs the chosen revision", async ({
  page,
}) => {
  const fixture = fixtures.get(page)!;
  await page.goto(`${fixture.comparisonPage().replace(/\/comparisons$/, "")}`);
  await page.getByRole("link", { name: "Compare revisions" }).click();
  await expect(page).toHaveURL(new RegExp(`/comparisons\\?right=${REV[2]}$`));
  await expect(side(page, "Left revision")).toContainText(
    "No revision selected",
  );
  await expect(side(page, "Right revision")).toContainText("Revision 3");
  await expect(results(page)).toContainText(
    "The comparison starts when both revisions have a readable saved explanation.",
  );
  expect(fixture.comparisons).toEqual([]);

  await page
    .getByRole("link", { name: "Use revision 1 as left", exact: true })
    .click();
  await expect(page).toHaveURL(
    new RegExp(`/comparisons\\?left=${REV[0]}&right=${REV[2]}$`),
  );
  await expect(results(page).locator(".comparison-headline")).toHaveText(
    "Observed differences in 5 of 10 items shown.",
  );
  // Exact retained derivations, linked validation, in the closed parameter order.
  expect(fixture.comparisons).toEqual([comparisonQuery(0, 2)]);
  await expect(side(page, "Left revision")).toContainText(P[0]);
  await expect(side(page, "Left revision")).toContainText(V[0]);
  await expect(side(page, "Left revision")).toContainText(
    "Linked to this explanation",
  );

  await page.goto(fixture.revisionPage(1));
  await page
    .getByRole("link", { name: "Compare with another revision" })
    .click();
  await expect(page).toHaveURL(new RegExp(`/comparisons\\?right=${REV[1]}$`));
  await expect(side(page, "Right revision")).toContainText("Revision 2");
});

test("entry points stay hidden when report reading is unavailable", async ({
  page,
}) => {
  const fixture = fixtures.get(page)!;
  fixture.mapping.access.actions.readReports = false;
  await page.goto(fixture.comparisonPage().replace(/\/comparisons$/, ""));
  await expect(page.getByRole("heading", { level: 1 })).toHaveText(
    "Synthetic mapping",
  );
  await expect(
    page.getByRole("link", { name: "Compare revisions" }),
  ).toHaveCount(0);
  await page.goto(fixture.comparisonPage(`left=${REV[0]}&right=${REV[2]}`));
  await expect(side(page, "Left revision")).toContainText(
    "Saved report reading is unavailable for this mapping.",
  );
  expect(fixture.comparisons).toEqual([]);
});

test("differences are grouped by entity with facets, unchanged items and both-side citations", async ({
  page,
}) => {
  const fixture = fixtures.get(page)!;
  await openPaired(page, fixture);
  const fields = group(page, "Fields");
  await expect(
    itemNamed(fields, 'Shipment ["qty"]').getByText("Data type"),
  ).toBeVisible();
  await expect(itemNamed(fields, 'Shipment ["qty"]')).toContainText("Changed");
  await expect(itemNamed(fields, 'Shipment ["legacy"]')).toContainText(
    "Only in left",
  );
  await expect(itemNamed(fields, 'Shipment ["legacy"]')).toContainText(
    "absent from revision 3's complete explanation",
  );
  await expect(itemNamed(fields, 'Shipment ["carrier"]')).toContainText(
    "Only in right",
  );
  await expect(
    itemNamed(group(page, "Parameters"), "Parameter: currency"),
  ).toBeVisible();
  const validity = itemNamed(group(page, "Validation"), "Native validation");
  await expect(validity).toContainText("Valid · 0 notices, 0 returned");
  await expect(validity).toContainText("Issues found · 1 notices, 1 returned");
  await expect(fields.locator(".comparison-meta").first()).toHaveText(
    "4 items · 3 changed · 1 unchanged · 1 only in left · 1 only in right",
  );
  await expect(
    group(page, "Rules").locator(".comparison-meta").first(),
  ).toHaveText("1 item · 1 unchanged");

  // Unchanged matched items are hidden by default and counted, never dropped.
  await expect(results(page)).toContainText(
    "5 matched items with no observed difference are hidden.",
  );
  await expect(itemNamed(fields, 'Shipment ["id"]')).toHaveCount(0);
  const toggle = page.getByRole("button", { name: "Show unchanged items" });
  await toggle.click();
  await expect(toggle).toHaveAttribute("aria-pressed", "true");
  await expect(itemNamed(fields, 'Shipment ["id"]')).toContainText(
    "No observed difference",
  );
  await expect(
    itemNamed(
      group(page, "Rules"),
      'Rule for Shipment ["id"]: Copy the order ID',
    ),
  ).toBeVisible();

  const evidence = page.getByRole("region", { name: "Cited evidence" });
  await expect(evidence).toContainText(REV[0]);
  await expect(evidence).toContainText(REV[2]);
  for (const id of [P[0], V[0], P[2], V[2]])
    await expect(evidence).toContainText(id);
  await expect(
    evidence.getByRole("link", {
      name: "Open revision 1 with these saved reports",
    }),
  ).toHaveAttribute(
    "href",
    `${fixture.revisionPage(0)}?projectionDerivationId=${P[0]}&validationDerivationId=${V[0]}`,
  );
  const exact = evidence.getByRole("link", {
    name: "Link to this exact comparison",
  });
  await expect(exact).toHaveAttribute(
    "href",
    fixture.comparisonPage(
      `left=${REV[0]}&leftProjection=${P[0]}&leftValidation=${V[0]}&right=${REV[2]}&rightProjection=${P[2]}&rightValidation=${V[2]}`,
    ),
  );
  // A pinned exact link reads the retained reports and cites the same derivations.
  await exact.click();
  await expect(side(page, "Left revision")).toContainText(
    "Pinned saved reports",
  );
  await expect.poll(() => fixture.comparisons.length).toBe(2);
  expect(fixture.comparisons[1]).toBe(comparisonQuery(0, 2));
  await expect(results(page).locator(".comparison-headline")).toBeVisible();

  // Each side's node opens that side's own saved explanation in the mapping viewer.
  await itemNamed(fields, 'Shipment ["legacy"]')
    .getByRole("button", { name: "Show in left explanation" })
    .click();
  const details = page.getByRole("region", { name: "Mapping details" });
  await expect(details).toBeFocused();
  await expect(details.getByRole("heading", { level: 3 })).toHaveText(
    'Shipment ["legacy"]',
  );
  await expect(
    page.getByRole("button", { name: "Left explanation · revision 1" }),
  ).toHaveAttribute("aria-pressed", "true");
  await itemNamed(fields, 'Shipment ["carrier"]')
    .getByRole("button", { name: "Show in right explanation" })
    .click();
  await expect(
    page.getByRole("region", { name: "Mapping details" }),
  ).toBeFocused();
  await expect(
    page
      .getByRole("region", { name: "Mapping details" })
      .getByRole("heading", { level: 3 }),
  ).toHaveText('Shipment ["carrier"]');
  await expect(
    page.getByRole("button", { name: "Right explanation · revision 3" }),
  ).toHaveAttribute("aria-pressed", "true");
});

test("ambiguous items show bounded candidates from both sides without choosing a pairing", async ({
  page,
}) => {
  const fixture = fixtures.get(page)!;
  fixture.compare = () => ({
    json: comparison({
      items: [
        item(
          "field",
          "ambiguous",
          [node("field", "r1-field-id"), node("field", "r1-field-qty")],
          [node("field", "r3-field-id")],
          [
            facet("shape", false, true, [
              "ambiguousMatch",
              "candidatesOmitted",
            ]),
          ],
          { left: 4 },
        ),
        item(
          "parameter",
          "ambiguous",
          [],
          [node("parameter", "r3-param-currency")],
          [
            facet("shape", false, true, [
              "ambiguousMatch",
              "unresolvedReference",
            ]),
          ],
        ),
      ],
    }),
  });
  await openPaired(page, fixture);
  const ambiguous = itemNamed(
    group(page, "Fields"),
    "Ambiguous field: 5 possible matches",
  );
  await expect(ambiguous).toContainText("Ambiguous");
  await expect(ambiguous).toContainText("No pairing was chosen.");
  await expect(ambiguous).toContainText('Shipment ["id"]');
  await expect(ambiguous).toContainText('Shipment ["qty"]');
  await expect(ambiguous).toContainText(
    "2 more possible counterparts omitted.",
  );
  await expect(ambiguous).toContainText("Not paired");
  await expect(ambiguous).toContainText(
    "More than one counterpart is possible; none was chosen.",
  );
  await expect(ambiguous).toContainText(
    "Some possible counterparts were omitted.",
  );
  await expect(
    ambiguous.getByRole("button", { name: "Show in left explanation" }),
  ).toHaveCount(2);
  await expect(
    itemNamed(
      group(page, "Parameters"),
      "Ambiguous parameter: 1 possible match",
    ),
  ).toContainText("A referenced item could not be paired across revisions.");
  // Candidate truncation is partial evidence even with no observed difference.
  await expect(results(page).locator(".comparison-headline")).toHaveText(
    "No differences were observed in the available evidence, but the evidence is incomplete. This is not a no-changes result.",
  );
  await expect(results(page)).toContainText(
    "Some possible counterparts were omitted from ambiguous items, so this result is partial.",
  );
  await expect(results(page)).not.toContainText("Showing 2 of 2 items");
});

test("partial, truncated and uncertain evidence is bannered and never reported as no changes", async ({
  page,
}) => {
  const fixture = fixtures.get(page)!;
  const unchanged = pairedItems().filter(
    (value) =>
      value.match === "matched" &&
      value.entity !== "validation" &&
      value.facets.every((entry) => !entry.hasObservedDifference),
  );
  unchanged[0] = {
    ...unchanged[0],
    facets: [facet("shape", false, true, ["unknownFact"])],
  };
  fixture.compare = () => ({
    json: comparison({
      items: unchanged,
      total: unchanged.length + 3,
      rightValidation: false,
      rightCoverage: {
        status: "partial",
        gaps: [
          {
            code: "syntheticGap",
            message: "A synthetic construct is not explained",
            nativeLocator: {
              kind: "jsonPointer",
              documentId: "entry.native.json",
              pointer: "/gap",
            },
            projectionLocator: null,
          },
        ],
      },
    }),
  });
  fixture.validations[2] = notDerived(fixture.validations[2]);
  await openPaired(page, fixture);
  expect(fixture.comparisons).toEqual([
    comparisonQuery(0, 2, { rightValidation: false }),
  ]);
  const view = results(page);
  await expect(view.locator(".comparison-headline")).toHaveText(
    "No differences were observed in the available evidence, but the evidence is incomplete. This is not a no-changes result.",
  );
  await expect(view).not.toContainText(
    "No differences were observed between these saved explanations.",
  );
  const banners = view.getByRole("list", { name: "Evidence limits" });
  await expect(banners).toContainText("The right explanation is partial.");
  await expect(banners).toContainText(
    `Showing ${unchanged.length} of ${unchanged.length + 3} items. The remaining items were omitted, so this result is partial.`,
  );
  await expect(banners).toContainText("Some facts could not be compared.");
  await expect(banners).toContainText(
    "Validation is available for only one side",
  );
  await banners.getByText("1 right explanation gap").click();
  await expect(banners).toContainText("A synthetic construct is not explained");
  await expect(side(page, "Right revision")).toContainText(
    "No saved validation exists for this revision.",
  );
  await expect(
    side(page, "Right revision").getByRole("link", {
      name: "Open revision 3 report controls",
    }),
  ).toHaveAttribute("href", fixture.revisionPage(2));
  // An uncertain matched item is shown even though nothing differs.
  await expect(
    itemNamed(group(page, "Inputs and outputs"), "Orders (primary input)"),
  ).toContainText("A saved explanation does not know this fact.");
});

test("complete equal evidence reads as no observed differences, never as equal execution", async ({
  page,
}) => {
  const fixture = fixtures.get(page)!;
  fixture.compare = () => ({
    json: comparison({
      items: [
        item(
          "field",
          "matched",
          [node("field", "r1-field-id")],
          [node("field", "r3-field-id")],
          [facet("shape"), facet("dataType")],
        ),
      ],
      leftValidation: false,
      rightValidation: false,
    }),
  });
  fixture.validations[0] = notDerived(fixture.validations[0]);
  fixture.validations[2] = notDerived(fixture.validations[2]);
  await openPaired(page, fixture);
  expect(fixture.comparisons).toEqual([
    comparisonQuery(0, 2, { leftValidation: false, rightValidation: false }),
  ]);
  await expect(results(page).locator(".comparison-headline")).toHaveText(
    "No differences were observed between these saved explanations.",
  );
  await expect(results(page)).toContainText(
    "Validation was not compared: neither side has a saved validation in this comparison.",
  );
  await expect(page.locator(".catalog-intro")).toContainText(
    "Matching explanations are not evidence that two revisions run the same way.",
  );
  await expect(group(page, "Fields")).toContainText(
    "Every item in this group is unchanged.",
  );
});

test("presentation-only changes and one-sided validation are not described as behavior or absence", async ({
  page,
}) => {
  const fixture = fixtures.get(page)!;
  fixture.validations[2] = notDerived(fixture.validations[2]);
  fixture.compare = () => ({
    json: comparison({
      rightValidation: false,
      items: [
        item(
          "rule",
          "matched",
          [node("rule", "r1-rule-id")],
          [node("rule", "r3-rule-id")],
          [facet("value"), facet("details"), facet("presentation", true)],
        ),
        item(
          "validation",
          "onlyLeft",
          [],
          [],
          [
            facet("validationValidity", false, true, ["missingValidation"]),
            facet("validationCounts", false, true, ["missingValidation"]),
          ],
        ),
      ],
    }),
  });
  await openPaired(page, fixture);
  const rule = itemNamed(
    group(page, "Rules"),
    'Rule for Shipment ["id"]: Copy the order ID',
  );
  await expect(rule).toContainText(
    "Only labels or summaries differ among the compared aspects.",
  );
  await expect(
    rule.getByRole("listitem").filter({ hasText: "presentation only" }),
  ).toContainText("Changed");
  await expect(
    rule.getByRole("listitem").filter({ hasText: "Value sources" }),
  ).toContainText("No observed difference");
  const validity = itemNamed(group(page, "Validation"), "Native validation");
  await expect(validity).toContainText(/Right validation\s*Not compared/);
  await expect(validity).toContainText(
    "One side has no saved validation in this comparison.",
  );
  await expect(validity).toContainText("Uncertain");
  await expect(validity).not.toContainText("Absence not confirmed");
  await expect(validity).not.toContainText("absent from");
});

test("a side without a readable saved explanation links to its report controls and is never derived", async ({
  page,
}) => {
  const fixture = fixtures.get(page)!;
  fixture.projections[0] = notDerived(fixture.projections[0]);
  fixture.projections[2] = {
    ...fixture.projections[2],
    readOutcome: "objectUnavailable",
    report: null,
  };
  await page.goto(fixture.comparisonPage(`left=${REV[0]}&right=${REV[2]}`));
  await expect(side(page, "Left revision")).toContainText(
    "No saved explanation exists for this revision. Comparison reads saved reports only and never generates one.",
  );
  await expect(
    side(page, "Left revision").getByRole("link", {
      name: "Open revision 1 report controls",
    }),
  ).toHaveAttribute("href", fixture.revisionPage(0));
  await expect(side(page, "Right revision")).toContainText(
    "A saved explanation exists, but its payload could not be read (saved object is unavailable).",
  );
  await expect(results(page)).toContainText(
    "The comparison starts when both revisions have a readable saved explanation.",
  );
  expect(fixture.comparisons).toEqual([]);

  // A pinned saved report that is no longer available offers the current reports instead.
  fixture.projections[0] = projection(0);
  await page.goto(
    fixture.comparisonPage(
      `left=${REV[0]}&leftProjection=${P[1]}&right=${REV[2]}`,
    ),
  );
  await expect(side(page, "Left revision")).toContainText(
    "The selected saved explanation is unavailable or you no longer have access.",
  );
  await side(page, "Left revision")
    .getByRole("link", { name: "Use current reports for this side" })
    .click();
  await expect(page).toHaveURL(
    new RegExp(`/comparisons\\?left=${REV[0]}&right=${REV[2]}$`),
  );
  await expect(side(page, "Left revision")).toContainText(P[0]);
});

test("comparison refusals, missing reports and unavailable storage recover explicitly", async ({
  page,
}) => {
  const fixture = fixtures.get(page)!;
  const replies = [
    { status: 404, json: { code: "notFound" } },
    { status: 503, json: { code: "revisionReportUnavailable" } },
    { json: comparison({}) },
  ];
  fixture.compare = () => replies.shift()!;
  await page.goto(fixture.comparisonPage(`left=${REV[0]}&right=${REV[2]}`));
  const alert = results(page).getByRole("alert");
  await expect(alert).toContainText(
    "A compared revision or saved report is unavailable, or you no longer have access.",
  );
  await expect(
    alert.getByRole("button", { name: "Try comparison again" }),
  ).toHaveCount(0);
  await alert.getByRole("button", { name: "Refresh catalog" }).click();
  await expect(alert).toContainText("Saved report storage is unavailable.");
  await alert.getByRole("button", { name: "Try comparison again" }).click();
  await expect(results(page).locator(".comparison-headline")).toHaveText(
    "Observed differences in 5 of 10 items shown.",
  );
  expect(fixture.comparisons).toHaveLength(3);

  fixture.compare = () => ({
    status: 503,
    json: { code: "revisionReportResourceLimit" },
  });
  await page.getByRole("button", { name: "Refresh catalog" }).first().click();
  await expect(results(page).getByRole("alert")).toContainText(
    "These explanations are too large to compare within the service limits.",
  );
  await expect(
    results(page).getByRole("button", { name: "Try comparison again" }),
  ).toHaveCount(0);

  fixture.compare = () => ({
    status: 429,
    json: { code: "revisionReportAdmissionBusy" },
  });
  await page.getByRole("button", { name: "Refresh catalog" }).first().click();
  await expect(results(page).getByRole("alert")).toContainText(
    "The service is busy.",
  );

  fixture.compare = () => ({
    status: 403,
    json: { code: "identityRejected" },
  });
  await page.getByRole("button", { name: "Refresh catalog" }).first().click();
  await expect(results(page).getByRole("alert")).toContainText(
    "The comparison request was not accepted.",
  );

  // A response citing another derivation is refused whole.
  fixture.compare = () => {
    const wrong = comparison({});
    wrong.right.projection.derivationId = P[1];
    return { json: wrong };
  };
  await page.getByRole("button", { name: "Refresh catalog" }).first().click();
  await expect(results(page).getByRole("alert")).toContainText(
    "could not be verified for these saved reports. Nothing is shown from that response.",
  );
  await expect(results(page).locator(".comparison-groups")).toHaveCount(0);
});

test("missing revisions and invalid links are shown without a comparison", async ({
  page,
}) => {
  const fixture = fixtures.get(page)!;
  fixture.missingRevisions.add(REV[0]);
  await page.goto(fixture.comparisonPage(`left=${REV[0]}&right=${REV[2]}`));
  await expect(side(page, "Left revision")).toContainText(
    "The selected revision is not part of this mapping or is no longer available.",
  );
  await expect(side(page, "Right revision")).toContainText(P[2]);
  expect(fixture.comparisons).toEqual([]);

  for (const query of [
    `left=${REV[0]}&leftRevision=${REV[2]}`,
    `left=${REV[0]}&left=${REV[1]}`,
    `leftProjection=${P[0]}&right=${REV[2]}`,
    "left=ABCDEF00-0000-4000-8000-000000000001",
    "right=00000000-0000-0000-0000-000000000000",
    `right=${REV[2]}&cursor=%2B`,
  ]) {
    await page.goto(fixture.comparisonPage(query));
    await expect(page.getByRole("alert")).toContainText("Invalid link");
  }
  expect(fixture.comparisons).toEqual([]);
});

test("a superseded comparison reply is ignored after the sides change", async ({
  page,
}) => {
  const fixture = fixtures.get(page)!;
  const held = fixture.holdComparison({ json: comparison({}) });
  await page.goto(fixture.comparisonPage(`left=${REV[0]}&right=${REV[2]}`));
  await expect(results(page)).toContainText("Comparing saved explanations…");
  await expect.poll(held.entered).toBe(true);
  fixture.compare = () => ({
    json: comparison({ left: 2, right: 0, items: [] }),
  });
  await page.getByRole("link", { name: "Swap left and right" }).click();
  await expect(page).toHaveURL(
    new RegExp(`/comparisons\\?left=${REV[2]}&right=${REV[0]}$`),
  );
  await expect(results(page).locator(".comparison-headline")).toHaveText(
    "No differences were observed between these saved explanations.",
  );
  held.release();
  await expect.poll(held.cancelled).toBe(true);
  await expect(results(page).locator(".comparison-headline")).toHaveText(
    "No differences were observed between these saved explanations.",
  );
  expect(fixture.comparisons).toEqual([
    comparisonQuery(0, 2),
    comparisonQuery(2, 0),
  ]);
});

test("keyboard users move between groups, items and each side's explanation", async ({
  page,
}) => {
  const fixture = fixtures.get(page)!;
  await openPaired(page, fixture);
  const jump = page.getByRole("button", { name: "Fields (4)" });
  await jump.focus();
  await page.keyboard.press("Enter");
  const heading = page.getByRole("heading", { name: "Fields", exact: true });
  await expect(heading).toBeFocused();
  const fields = group(page, "Fields");
  await page.keyboard.press("ArrowDown");
  await expect(itemNamed(fields, 'Shipment ["qty"]')).toBeFocused();
  await page.keyboard.press("ArrowDown");
  await expect(itemNamed(fields, 'Shipment ["legacy"]')).toBeFocused();
  await page.keyboard.press("ArrowUp");
  await expect(itemNamed(fields, 'Shipment ["qty"]')).toBeFocused();
  await page.keyboard.press("End");
  await expect(
    itemNamed(group(page, "Validation"), "Native validation"),
  ).toBeFocused();
  await page.keyboard.press("Home");
  await expect(itemNamed(fields, 'Shipment ["qty"]')).toBeFocused();

  // Tab reaches the item's own controls; Enter opens the viewer and Close returns focus.
  await page.keyboard.press("Tab");
  const show = itemNamed(fields, 'Shipment ["qty"]').getByRole("button", {
    name: "Show in left explanation",
  });
  await expect(show).toBeFocused();
  await page.keyboard.press("Enter");
  const details = page.getByRole("region", { name: "Mapping details" });
  await expect(details).toBeFocused();
  await details.getByRole("button", { name: "Close details" }).press("Enter");
  await expect(show).toBeFocused();

  // Revision choices are ordinary links.
  const choose = page.getByRole("link", { name: "Use revision 2 as left" });
  await choose.focus();
  await page.keyboard.press("Enter");
  await expect(page).toHaveURL(
    new RegExp(`/comparisons\\?left=${REV[1]}&right=${REV[2]}$`),
  );
});

test("a narrow viewport stacks both sides without horizontal scrolling", async ({
  page,
}) => {
  const fixture = fixtures.get(page)!;
  await page.setViewportSize({ width: 390, height: 844 });
  await openPaired(page, fixture);
  await page.getByRole("button", { name: "Show unchanged items" }).click();
  await page
    .getByRole("button", { name: "Right explanation · revision 3" })
    .click();
  await expect(
    page.getByRole("heading", { name: "Saved explanations" }),
  ).toBeFocused();
  const left = await side(page, "Left revision").boundingBox();
  const right = await side(page, "Right revision").boundingBox();
  expect(left && right).toBeTruthy();
  expect(right!.y).toBeGreaterThan(left!.y + left!.height - 1);
  expect(Math.abs(right!.x - left!.x)).toBeLessThan(2);
  const overflow = await page.evaluate(
    () =>
      document.documentElement.scrollWidth -
      document.documentElement.clientWidth,
  );
  expect(overflow).toBeLessThanOrEqual(0);
});
