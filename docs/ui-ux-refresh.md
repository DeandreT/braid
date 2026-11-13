# braid workspace layout

The approved workspace refresh keeps the existing routes, application behavior
and permission checks. It changes how the catalog, revision workbench and exact
creation review are presented; it does not replace them with the static design
prototype.

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

## Verification boundaries

Static before/after design captures document the approved direction, not
application behavior. Qualification of the implemented layout uses the built
Angular application, authored API fixtures, existing behavioral suites and the
focused `apps/web/tests/ux-layout.spec.ts` regression selection. These checks are
separate from real-provider, native execution and deployed-environment acceptance.

The refreshed layout is implemented in Angular. Integrated-07 passed 667 fixtures
with four approved optional captures skipped, all 88 UX executions, 17 production
smoke cases and the separate four-case published-shell editor selection.
Twelve documentation captures were regenerated with provenance. Results,
preserved failed attempts and bounded visual-evidence limits are recorded in the
[integration qualification record](v1-integration.md#ui-implementation-qualification).
Final formatting, contracts, browser TypeScript, documentation-image and diff
checks passed. Frozen inputs and the unrelated served demo were preserved, and
owned validation processes settled. Live-provider and Azure acceptance remain
user-deferred.
