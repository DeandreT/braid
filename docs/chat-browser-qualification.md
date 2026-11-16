# Combined browser qualification

**Historical browser-boundary qualification record.** The source identities,
correction, trace sequence and results below describe one retained run, not
current setup or proof of live-provider behavior. Use [chat proposal review](chat-proposal-review-ui.md)
for current approval/recovery behavior, [creation review](creation-review-ui.md)
for new mappings and [development](development.md) for checks. [ROADMAP](../ROADMAP.md)
owns remaining acceptance; provider and Azure gates are not activated by this record.

Production baseline: `85c483e0f473f407d1a1dc071daeef3126c84621`.
Combined merge: `e03f3fc9165c4f4930b122784c662d950768491d`, incorporating
reviewed chat test corrections `8c41a730b261cd6272854c4377b253d7e7b36c75`.
The subsequent correction in this commit changes only the workspace authority
test and this receipt. Production code is identical to the combined merge.

## Failure and correction

The first combined chat run returned 95 passed, one failed, and one optional
screenshot skipped. `workspace-chat.spec.ts:644` timed out after 30 seconds:
the Send question button detached during Playwright's click actionability check.

The retained red trace established this order (trace monotonic milliseconds):

- 149087: the broad receipt waiter consumed the first turn's mapping-writes read.
- 149210: the terminal-answer check started before the second POST at 149217.
- 149252: the test filled the third question, then changed the fixture actor.
- 149267–149305: second-turn receipt/proposal reads and authority checks were
  still completing; private chat was removed during the attempted third click.

The test now binds each successful POST to its returned conversation/turn IDs,
waits for the displayed question and terminal answer, verifies the exact retained
turn phase, and drains that turn's receipt and proposal refresh responses before
changing authority. Proposal refresh settlement accounts for the synthetic tool
completion, terminal event, and post-stream refresh. It retains the assertions
for rotated request tokens, revoked payload tools, absence of second-turn
disclosure, cleared private content, and no additional POST after owner change.
Timeouts remain unchanged.

## Results

Native Windows Chromium with Node 22.23.3, npm 12.1.0, and Chromium 1254:

- Corrected authority test: five repeats passed, zero failures/skips.
- Full chat after correction: 96 passed, zero failures, one optional screenshot
  skipped (97 selected). Inputs remained frozen throughout the run; only a
  formatting-only line wrap was applied afterward.
- Creation library on combined merge: 15 passed, zero failures/skips.
- Spec intake on combined merge: 60 passed, zero failures/skips.
- Creation review on combined merge: 28 passed, zero failures/skips.

The latter three selections remain applicable because the corrective change is
confined to the chat test. Full formatting and browser TypeScript checks passed.
These are synthetic browser-boundary fixtures, not live-provider acceptance.
Live provider and Azure acceptance remain deferred.

Receipts: `combined-e03f3fc9/{chat,creation-library,spec-intake,creation-review}.out`
and `workspace-authority-fix/{focused,full}.out`, with the failed trace and a
corrected focused trace retained alongside the qualification evidence.
