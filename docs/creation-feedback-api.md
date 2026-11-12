# Creation capability and retained-proposal feedback

## Capability

`GET /api/v1/organizations/{organizationId}/projects/{projectId}/creation-capabilities`
returns `{"available":true}` or `{"available":false}` (`CreationCapabilitiesV1`).
The request requires session or bearer authentication, the authenticated organization,
and current project metadata access. Project authority is checked before and after
runtime admission. Responses use `Cache-Control: no-store`.

Availability comes from `CreationRuntime.AvailableAsync`. Missing or disabled runtime
ports return `available:false`. Capability reads require metadata access independently
of payload-sharing permission. Host admission must describe the qualified durable
runtime, including retained proposal reads after source retirement. The current
admission interface is `ICreationRuntimeAdmission.VerifyAsync(CancellationToken)`.

## Feedback tool

The provider-owned `read_specification` schema/parser admits this exclusive argument set:

```json
{
  "mode": "proposal",
  "projectId": "11111111-1111-4111-8111-111111111111",
  "proposalId": "22222222-2222-4222-8222-222222222222",
  "fingerprint": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
}
```

The ordinary workspace envelope contains `projectId`, `proposalId`, `fingerprint`,
`snapshotDigest`, `planDigest`, the complete JSON `plan`, `assumptions`, `questions`,
`evidence`, `bundleId`, retained `sources` identities, and `requiresNewApproval:true`.
Each source identity includes its ID/version, source digest, format, extractor
version, profile digest, extraction outcome, and evidence digest.

The handler uses the creator/content-authorized retained proposal reader. It checks
the exact project, creator, proposal ID, and fingerprint, then repeats the authorized
read and payload-grant check before releasing content. Payload-capable profile and
workspace payload-manifest policy apply through the existing chat boundary. Retained
reads remain usable when original sources have retired.

The complete response must fit the call's remaining byte and node budgets, capped
at 12,288 bytes per call. Every JSON data value counts toward the node budget.
An oversized response is refused whole with `resourceLimit`. Feedback is audited
as `Specification` (kind 5), using the proposal ID as subject and the full serialized
tool-response byte count. The existing orchestrator records disclosure before the
next provider round and rechecks payload authorization.

The existing proposal and approval remain immutable. A replacement requires fresh
source reads, generation of a new proposal, and its own explicit approval.

## UI handoff

The UI prepares an explicit, unsent question from `creationProjectId`,
`creationProposalId`, and `creationFingerprint`. The capability endpoint supplies
current server availability; the retained-feedback tool supplies content only when
the user sends the prepared question under the authorized payload policy.

## Qualification

`CreationFeedbackToolTests` exercises the public tool parser and a loopback HTTP/SSE
provider fixture with recording compiler/store ports. It covers retained approved
content after source retirement, identity and authorization refusals, whole-response
bounds, disclosure ordering, and revocation before the next provider request.
These are orchestration tests with fake backend ports; durable storage and live
provider qualification are separate host responsibilities.

`CreationCapabilitiesTests` covers generated OpenAPI and development-identity refusal
portably. Authenticated session/bearer cases use the existing Linux-only durable
session fixture and must run on Linux. The feedback tests exercise the provider
proposal-mode registry and grounding integrated from commit `374ea6a5` together
with this handler.
