# braid workspace layout

The workspace shares a shell across the catalog, revision workbench and creation
review. See [Creation review](creation-review-ui.md) for saving and recovery, and
[catalog browser](catalog-browser.md) for catalog workflows.

## Shared shell and catalog

Desktop navigation shows names beside the existing icons. From 680–1023px, a
44px icon rail preserves room for side-by-side chat; links retain accessible
names, native title tooltips and 44px targets. Below 680px, the existing menu
keeps the same destinations reachable without page-level horizontal scrolling.
Organization context, search, notifications, administration, help, chat and account
controls retain their existing visibility and interaction rules.

The project inventory groups readable project titles, descriptions, update dates
and actual access roles. Creation, upload, refresh and pagination still depend on
the existing server-provided capabilities and current access.

## Revision workbench

The mapping name and version provide context above the workflow tabs. The eight
workflows remain Data mapping, Try sample, Versions, Compare, Tests, Prepare,
In use and Change history. Inspector, Issues, Incoming data, Result, Diff and
Activity remain investigation views; Rule, Data flow and Details remain nested
Inspector views.

The refresh keeps real selection, keyboard navigation, investigation links,
resizing, density choices, filters, parameters, output downloads, test cases,
build preparation, release operations and explicit recovery. Native validity,
test outcomes, declared data flow and observed execution are different facts.
Unavailable controls and unknown release status remain visibly unavailable or
unknown, rather than becoming successful placeholder content.

## Exact creation review

Review requirements and evidence, test a sample, then save the exact proposal.
Desktop presents the approval area beside the requirements/sample column;
smaller layouts keep the same sequence in one column. Critical assumptions and
questions remain explicit. Technical identities and the exact plan are available
through keyboard-operable disclosures.

Each critical assumption and the separate exact-plan review confirmation retain
their existing approval conditions. Samples do not approve a mapping. Feedback
carries the proposal identity, while samples and output remain on the review
page. Uncertain saves still require checking the original save status rather
than guessing that another save is safe.

## Tests

Layout checks use the built Angular application, authored API fixtures and the
focused `apps/web/tests/ux-layout.spec.ts` regression selection.
