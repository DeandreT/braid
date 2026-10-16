import type { components } from "../../api/generated";
import { trimCatalogName } from "../catalog-text";
import {
  isCatalogId,
  type CatalogContext,
  type CatalogMapping,
  type CatalogMappingScope,
  type CatalogProjectScope,
  type CatalogRevision,
  type CatalogRevisionScope,
} from "../catalog-types";
import {
  encodeNativeIngestionQueryValue,
  inspectNativeIngestionText,
  nativeIngestionFilenameError,
  nativeIngestionMaximumBytes,
  nativeIngestionWireTargetError,
  withNativeIngestionSource,
  type NativeIngestionSource,
} from "./native-ingestion-leaves";

export type NativeRevisionAppendResult =
  components["schemas"]["CatalogRevisionIngestionResultV1"];
export type NativeRevisionAppendReceipt =
  components["schemas"]["CatalogRevisionIngestionReceiptV1"];
export type NativeRevisionAppendOutcome =
  components["schemas"]["CatalogRevisionIngestionOutcomeV1"];
export type NativeRevisionAppendPhase =
  components["schemas"]["CatalogRevisionIngestionPhaseV1"];
export type NativeRevisionAppendFailure =
  components["schemas"]["CatalogRevisionIngestionFailureV1"];
export type NativeRevisionAppendRejection =
  components["schemas"]["CatalogRevisionIngestionRejectionV1"];
export type NativeRevisionAppendAdmissionClassification =
  components["schemas"]["CatalogAdmissionV1"];

export interface NativeRevisionAppendAdmission {
  readonly context: CatalogContext;
  readonly mapping: CatalogMapping;
}
export interface NativeRevisionAppendInput {
  readonly originalFilename: string;
}
export type NativeRevisionAppendFilenameError =
  | "required"
  | "invalidType"
  | "invalidUnicode"
  | "nul"
  | "unsafeFilename"
  | "byteLimit";
export interface NativeRevisionAppendInputErrors {
  readonly originalFilename: NativeRevisionAppendFilenameError | null;
  readonly request:
    "invalidScope" | "invalidHead" | "queryLimit" | "requestLineLimit" | null;
}
export type NativeRevisionAppendInputValidation =
  | {
      readonly valid: false;
      readonly errors: NativeRevisionAppendInputErrors;
    }
  | { readonly valid: true; readonly originalFilename: string };
export type NativeRevisionAppendErrorKind =
  | "invalidInput"
  | "identityChanged"
  | "identityRejected"
  | "featureDisabled"
  | "uploadUnavailable"
  | "unsupportedBackend"
  | "headReviewRequired"
  | "invalidRequest"
  | "notFound"
  | "bodyLimit"
  | "unsupportedMediaType"
  | "ingestionAdmissionBusy"
  | "persistenceFailure"
  | "internalFailure"
  | "timedOut"
  | "unavailable"
  | "incompatible";

/** Current-send facts only; earlier uncertainty belongs to the state controller. */
export class NativeRevisionAppendError extends Error {
  constructor(
    readonly kind: NativeRevisionAppendErrorKind,
    readonly status: number | null = null,
    readonly mayHaveCommitted = false,
  ) {
    super("The native revision append request could not be completed.");
    this.name = "NativeRevisionAppendError";
  }
}

type JsonObject = Record<string, unknown>;
type HeadFacts = {
  readonly scope: Readonly<CatalogRevisionScope>;
  readonly sequence: string;
  readonly admission: NativeRevisionAppendAdmissionClassification;
  readonly backendId: string;
};
type Expectation = NativeRevisionAppendOwner & {
  readonly origin: symbol;
  readonly reviewedHead: NativeRevisionAppendHead;
  readonly originalFilename: string;
  readonly digest: string;
  readonly byteCount: number;
};
type IntentFacts = {
  readonly expectation: Expectation;
  readonly body: Blob;
  readonly key: string;
  readonly url: string;
};
type OperationFacts = {
  readonly expectation: Expectation;
  readonly receipt: NativeRevisionAppendOwnedReceipt;
};
const heads = new WeakMap<NativeRevisionAppendHead, HeadFacts>();
const intents = new WeakMap<NativeRevisionAppendIntent, IntentFacts>();
const operations = new WeakMap<NativeRevisionAppendOperation, OperationFacts>();

/** Validate exact filename and once-encoded target without sending or allocating a key. */
export function validateNativeRevisionAppendInput(
  scope: CatalogMappingScope,
  reviewedHead: NativeRevisionAppendHead,
  input: NativeRevisionAppendInput,
): NativeRevisionAppendInputValidation {
  let originalFilename: NativeRevisionAppendFilenameError | null = null;
  let request: NativeRevisionAppendInputErrors["request"] = null;
  const head = heads.get(reviewedHead);
  if (!mappingScope(scope)) request = "invalidScope";
  else if (!head || !sameMapping(head.scope, scope)) request = "invalidHead";
  if (typeof input?.originalFilename !== "string")
    originalFilename = "invalidType";
  else originalFilename = nativeIngestionFilenameError(input.originalFilename);
  if (originalFilename === null && request === null && head) {
    request = nativeIngestionWireTargetError(
      appendPath(scope),
      encodedQuery(head.scope.revisionId, input.originalFilename),
    );
  }
  if (originalFilename !== null || request !== null)
    return Object.freeze({
      valid: false,
      errors: Object.freeze({ originalFilename, request }),
    });
  return Object.freeze({
    valid: true,
    originalFilename: input.originalFilename,
  });
}

/** Owned immutable read facts; head comparison itself establishes no upload authority. */
export class NativeRevisionAppendHead {
  private readonly headBrand = undefined;
  private constructor(facts: HeadFacts) {
    void this.headBrand;
    heads.set(this, ownHead(facts));
    Object.freeze(this);
  }
  static fromAdmission(
    admission: NativeRevisionAppendAdmission,
  ): NativeRevisionAppendHead {
    requireViewAdmission(admission);
    const facts = readMappingHead(admission.mapping);
    if (!facts) throw invalidInput();
    return new NativeRevisionAppendHead(facts);
  }
  get scope(): Readonly<CatalogRevisionScope> {
    return headFacts(this).scope;
  }
  get sequence(): string {
    return headFacts(this).sequence;
  }
  get admission(): NativeRevisionAppendAdmissionClassification {
    return headFacts(this).admission;
  }
  get backendId(): string {
    return headFacts(this).backendId;
  }
  matchesMapping(mapping: CatalogMapping): boolean {
    const expected = headFacts(this),
      current = readMappingHead(mapping);
    return current !== null && sameHead(expected, current);
  }
  toString(): "NativeRevisionAppendHead" {
    return "NativeRevisionAppendHead";
  }
  toJSON(): "NativeRevisionAppendHead" {
    return "NativeRevisionAppendHead";
  }
}

/** The original owner, source and equal-head wire request never change during recovery. */
export class NativeRevisionAppendIntent {
  private readonly intentBrand = undefined;
  private constructor(facts: IntentFacts) {
    void this.intentBrand;
    intents.set(this, facts);
    Object.freeze(this);
  }
  static create(
    admission: NativeRevisionAppendAdmission,
    reviewedHead: NativeRevisionAppendHead,
    input: NativeRevisionAppendInput,
    source: NativeIngestionSource,
    idempotencyKey: string,
  ): NativeRevisionAppendIntent {
    const owner = captureNativeRevisionAppendAdmission(admission);
    const accepted = validateNativeRevisionAppendInput(
      owner.scope,
      reviewedHead,
      input,
    );
    if (!accepted.valid || !isCatalogId(idempotencyKey)) throw invalidInput();
    if (!reviewedHead.matchesMapping(admission.mapping))
      throw new NativeRevisionAppendError("headReviewRequired");
    try {
      return withNativeIngestionSource(source, (material) => {
        if (
          !(material.body instanceof Blob) ||
          !Number.isSafeInteger(material.byteCount) ||
          material.byteCount < 1 ||
          material.byteCount > nativeIngestionMaximumBytes ||
          material.body.size !== material.byteCount ||
          typeof material.digest !== "string" ||
          !/^[0-9a-f]{64}$/.test(material.digest)
        )
          throw invalidInput();
        const expectation = ownExpectation({
          ...owner,
          origin: Symbol(),
          reviewedHead,
          originalFilename: accepted.originalFilename,
          digest: material.digest,
          byteCount: material.byteCount,
        });
        return new NativeRevisionAppendIntent(
          Object.freeze({
            expectation,
            body: material.body,
            key: idempotencyKey,
            url:
              appendPath(owner.scope) +
              encodedQuery(
                reviewedHead.scope.revisionId,
                accepted.originalFilename,
              ),
          }),
        );
      });
    } catch (error: unknown) {
      if (error instanceof NativeRevisionAppendError) throw error;
      throw invalidInput();
    }
  }
  get scope(): Readonly<CatalogMappingScope> {
    return intentFacts(this).expectation.scope;
  }
  get actorPrincipalId(): string {
    return intentFacts(this).expectation.actorPrincipalId;
  }
  get backendId(): "ferrule" {
    return intentFacts(this).expectation.backendId;
  }
  get reviewedHead(): NativeRevisionAppendHead {
    return intentFacts(this).expectation.reviewedHead;
  }
  get originalFilename(): string {
    return intentFacts(this).expectation.originalFilename;
  }
  get byteCount(): number {
    return intentFacts(this).expectation.byteCount;
  }
  /** @internal Only transport consumes these private arguments, synchronously and once. */
  withRequest<T>(send: (url: string, key: string, body: Blob) => T): T {
    const facts = intentFacts(this);
    return send(facts.url, facts.key, facts.body);
  }
  toString(): "NativeRevisionAppendIntent" {
    return "NativeRevisionAppendIntent";
  }
  toJSON(): "NativeRevisionAppendIntent" {
    return "NativeRevisionAppendIntent";
  }
}

export type NativeRevisionAppendOwnedReceipt = Readonly<
  Omit<
    NativeRevisionAppendReceipt,
    "project" | "futureMapping" | "futureRevision"
  > & {
    readonly project: Readonly<CatalogProjectScope>;
    readonly futureMapping: Readonly<CatalogMappingScope>;
    readonly futureRevision: Readonly<CatalogRevisionScope>;
  }
>;

/** Receipt plus primitive original expectations: no Intent/Source/Blob/key/query is retained. */
export class NativeRevisionAppendOperation {
  private readonly operationBrand = undefined;
  private constructor(
    expectation: Expectation,
    receipt: NativeRevisionAppendOwnedReceipt,
  ) {
    void this.operationBrand;
    operations.set(
      this,
      Object.freeze({ expectation: ownExpectation(expectation), receipt }),
    );
    Object.freeze(this);
  }
  /** @internal Learn only from a response bound to this original request or exact own operation. */
  static fromResponse(
    binding: NativeRevisionAppendIntent | NativeRevisionAppendOperation,
    value: unknown,
    previous: NativeRevisionAppendOperation | null = null,
  ): NativeRevisionAppendOperation {
    const expectation = bindingExpectation(binding);
    if (previous && bindingExpectation(previous).origin !== expectation.origin)
      throw incompatible();
    const boundOperation = operations.get(
      binding as NativeRevisionAppendOperation,
    );
    const receipt = readReceipt(
      value,
      expectation,
      boundOperation?.receipt ?? null,
    );
    // Both learned observations constrain the response; an earlier explicit previous cannot
    // weaken an already-terminal binding or bypass its nondecreasing attempt count.
    if (previous)
      readReceipt(receipt, expectation, operationFacts(previous).receipt);
    return new NativeRevisionAppendOperation(expectation, receipt);
  }
  get receipt(): NativeRevisionAppendOwnedReceipt {
    return operationFacts(this).receipt;
  }
  get scope(): Readonly<CatalogMappingScope> {
    return operationFacts(this).expectation.scope;
  }
  get actorPrincipalId(): string {
    return operationFacts(this).expectation.actorPrincipalId;
  }
  get backendId(): "ferrule" {
    return operationFacts(this).expectation.backendId;
  }
  get reviewedHead(): NativeRevisionAppendHead {
    return operationFacts(this).expectation.reviewedHead;
  }
  get committed(): boolean {
    return this.receipt.phase === "committed";
  }
  get terminal(): boolean {
    return terminal(this.receipt.phase);
  }
  get headConflict(): boolean {
    return this.receipt.phase === "headConflict";
  }
  get committedRevisionUrl(): string | null {
    return this.committed ? revisionPath(this.receipt.futureRevision) : null;
  }
  matchesRevision(revision: CatalogRevision): boolean {
    const { expectation, receipt } = operationFacts(this);
    return (
      this.committed &&
      !!revision &&
      revisionScope(revision.scope) &&
      sameRevision(revision.scope, receipt.futureRevision) &&
      revision.sequence === receipt.committedSequence &&
      followsReviewedHead(
        revision.sequence,
        expectation.reviewedHead.sequence,
      ) &&
      revision.admission === receipt.committedAdmission &&
      revision.creatorPrincipalId === expectation.actorPrincipalId &&
      revision.backendId === expectation.backendId &&
      revision.logicalDocument === "entry.native.json" &&
      revision.originalFilename === expectation.originalFilename &&
      revision.nativeByteCount === expectation.byteCount &&
      revision.nativeDigest?.algorithm === "sha256" &&
      revision.nativeDigest.value === expectation.digest
    );
  }
  toString(): "NativeRevisionAppendOperation" {
    return "NativeRevisionAppendOperation";
  }
  toJSON(): "NativeRevisionAppendOperation" {
    return "NativeRevisionAppendOperation";
  }
}

export interface NativeRevisionAppendResponse {
  readonly status: 200 | 202 | 400 | 404 | 409 | 413 | 422 | 503;
  readonly outcome: NativeRevisionAppendOutcome;
  readonly operation: NativeRevisionAppendOperation | null;
  readonly rejection: NativeRevisionAppendRejection;
  readonly failure: NativeRevisionAppendFailure;
  readonly committedRevisionUrl: string | null;
  readonly receiptLocation: string | null;
}
export interface NativeRevisionAppendOwner {
  readonly mode: "development";
  readonly actorPrincipalId: string;
  readonly scope: Readonly<CatalogMappingScope>;
  readonly backendId: "ferrule";
}

/** @internal Fresh upload/owner/target/backend admission; never compare today's head. */
export function captureNativeRevisionAppendAdmission(
  admission: NativeRevisionAppendAdmission,
  binding?: NativeRevisionAppendIntent | NativeRevisionAppendOperation,
  previous?: NativeRevisionAppendOperation,
): NativeRevisionAppendOwner {
  const current = requireViewAdmission(admission);
  if (binding) {
    const original = bindingExpectation(binding);
    if (
      original.mode !== current.mode ||
      original.actorPrincipalId !== current.actorPrincipalId ||
      !sameMapping(original.scope, current.scope)
    )
      throw new NativeRevisionAppendError("identityChanged");
    if (previous && bindingExpectation(previous).origin !== original.origin)
      throw invalidInput();
  } else if (previous) throw invalidInput();
  if (admission.context.features.uploadRevisions !== true)
    throw new NativeRevisionAppendError("featureDisabled");
  if (admission.mapping.access.actions.uploadRevisions !== true)
    throw new NativeRevisionAppendError("uploadUnavailable");
  if (current.backendId !== "ferrule")
    throw new NativeRevisionAppendError("unsupportedBackend");
  return Object.freeze({
    mode: current.mode,
    actorPrincipalId: current.actorPrincipalId,
    scope: ownMapping(current.scope),
    backendId: "ferrule",
  });
}

/** @internal Exact result status/nullability/header/receipt binding, including non2xx results. */
export function readNativeRevisionAppendResponse(
  status: number,
  value: unknown,
  location: string | null,
  binding: NativeRevisionAppendIntent | NativeRevisionAppendOperation,
  previous: NativeRevisionAppendOperation | null = null,
): NativeRevisionAppendResponse {
  try {
    if (
      !object(value) ||
      !keysWithin(
        value,
        ["outcome", "receipt", "rejection", "failure", "committedRevisionUrl"],
        ["outcome"],
      )
    )
      throw incompatible();
    const outcome = value["outcome"];
    if (!known(outcome, outcomes)) throw incompatible();
    const rawReceipt = optional(value, "receipt"),
      rejection = optional(value, "rejection"),
      failure = optional(value, "failure"),
      url = optional(value, "committedRevisionUrl");
    if (
      !nullableKnown(rejection, rejections) ||
      !nullableKnown(failure, failures) ||
      (url !== null && typeof url !== "string")
    )
      throw incompatible();
    // Validate the binding even for a no-receipt refusal; it cannot authorize adoption.
    const expectation = bindingExpectation(binding);
    if (previous && bindingExpectation(previous).origin !== expectation.origin)
      throw incompatible();
    const operation =
      rawReceipt === null
        ? null
        : NativeRevisionAppendOperation.fromResponse(
            binding,
            rawReceipt,
            previous,
          );
    const receipt = operation?.receipt ?? null,
      pending = receipt !== null && !terminal(receipt.phase);
    let expectedStatus: number;
    switch (outcome) {
      case "committedValid":
      case "committedDecodedInvalid":
        if (
          !operation?.committed ||
          receipt?.committedAdmission !==
            (outcome === "committedValid" ? "valid" : "decodedInvalid") ||
          url !== operation.committedRevisionUrl
        )
          throw incompatible();
        expectedStatus = 200;
        break;
      case "inProgress":
        if (!pending || receipt!.latestFailure !== null) throw incompatible();
        expectedStatus = 202;
        break;
      case "needsSource":
        if (!pending || receipt!.latestFailure !== "needsSource")
          throw incompatible();
        expectedStatus = 409;
        break;
      case "retryableFailure":
        if (
          !pending ||
          receipt!.latestFailure === null ||
          receipt!.latestFailure === "needsSource" ||
          receipt!.latestFailure === "evidenceLimit"
        )
          throw incompatible();
        expectedStatus = 503;
        break;
      case "headConflict":
        if (receipt?.phase !== "headConflict") throw incompatible();
        expectedStatus = 409;
        break;
      case "rejected":
        if (receipt === null) {
          if (
            rejection !== "emptyNative" &&
            rejection !== "nativeLimit" &&
            rejection !== "unregisteredBackend"
          )
            throw incompatible();
        } else if (
          receipt.phase !== "rejected" ||
          (rejection !== "nativeDecode" &&
            rejection !== "unsupportedArtifact" &&
            rejection !== "resourceLimit")
        )
          throw incompatible();
        expectedStatus =
          rejection === "emptyNative"
            ? 400
            : rejection === "nativeLimit"
              ? 413
              : 422;
        break;
      case "attemptExhausted":
        if (receipt?.phase !== "attemptExhausted") throw incompatible();
        expectedStatus = 409;
        break;
      case "idempotencyConflict":
      case "claimLost":
      case "notAuthorizedOrNotFound":
        if (receipt !== null) throw incompatible();
        expectedStatus = outcome === "notAuthorizedOrNotFound" ? 404 : 409;
        break;
      default:
        throw incompatible();
    }
    if (
      status !== expectedStatus ||
      failure !== (receipt?.latestFailure ?? null) ||
      (outcome !== "rejected" && rejection !== null) ||
      (failure === "evidenceLimit" && rejection !== "resourceLimit") ||
      (status !== 200 && url !== null)
    )
      throw incompatible();
    const receiptLocation =
      status === 202 ? nativeRevisionAppendReceiptPath(operation!) : null;
    if (location !== receiptLocation) throw incompatible();
    return Object.freeze({
      status: status as NativeRevisionAppendResponse["status"],
      outcome: outcome as NativeRevisionAppendOutcome,
      operation,
      rejection: rejection as NativeRevisionAppendRejection,
      failure: failure as NativeRevisionAppendFailure,
      committedRevisionUrl: url,
      receiptLocation,
    });
  } catch {
    throw new NativeRevisionAppendError("incompatible", status, true);
  }
}

/** @internal The learned own operation, never a pasted ID, supplies this URL. */
export function nativeRevisionAppendReceiptPath(
  operation: NativeRevisionAppendOperation,
): string {
  const receipt = operationFacts(operation).receipt;
  return (
    projectPath(receipt.project) + "/revision-ingestions/" + receipt.operationId
  );
}

const failures: readonly string[] = [
  "needsSource",
  "storageIntegrity",
  "storageIo",
  "backendUnavailable",
  "backendFailure",
  "timedOut",
  "canceled",
  "isolationFailure",
  "protocolFailure",
  "reservedProfileUnavailable",
  "evidenceLimit",
];
const rejections: readonly string[] = [
  "emptyNative",
  "nativeLimit",
  "unregisteredBackend",
  "nativeDecode",
  "unsupportedArtifact",
  "resourceLimit",
];
const outcomes: readonly string[] = [
  "committedValid",
  "committedDecodedInvalid",
  "inProgress",
  "needsSource",
  "retryableFailure",
  "rejected",
  "headConflict",
  "attemptExhausted",
  "idempotencyConflict",
  "claimLost",
  "notAuthorizedOrNotFound",
];
const phases: readonly string[] = [
  "reserved",
  "staged",
  "ready",
  "committed",
  "rejected",
  "headConflict",
  "attemptExhausted",
];
const maximumSequence = 9223372036854775807n;

function readReceipt(
  value: unknown,
  expected: Expectation,
  previous: NativeRevisionAppendOwnedReceipt | null,
): NativeRevisionAppendOwnedReceipt {
  if (
    !object(value) ||
    !keys(value, [
      "operationId",
      "project",
      "futureMapping",
      "futureRevision",
      "phase",
      "claimCount",
      "reservedAtUtc",
      "completedAtUtc",
      "originalFilename",
      "committedSequence",
      "committedAdmission",
      "latestFailure",
    ])
  )
    throw incompatible();
  const {
    operationId,
    project,
    futureMapping,
    futureRevision,
    phase,
    claimCount,
    reservedAtUtc,
    completedAtUtc,
    originalFilename,
    committedSequence,
    committedAdmission,
    latestFailure,
  } = value;
  if (
    !isCatalogId(operationId) ||
    !projectScope(project) ||
    !sameProject(project, expected.scope) ||
    !mappingScope(futureMapping) ||
    !sameMapping(futureMapping, expected.scope) ||
    !revisionScope(futureRevision) ||
    !sameMapping(futureRevision, expected.scope) ||
    !known(phase, phases) ||
    typeof claimCount !== "number" ||
    !Number.isInteger(claimCount) ||
    claimCount < 0 ||
    claimCount > 8 ||
    typeof originalFilename !== "string" ||
    originalFilename !== expected.originalFilename ||
    nativeIngestionFilenameError(originalFilename) !== null ||
    !nullableKnown(latestFailure, failures)
  )
    throw incompatible();
  const reserved = utcTime(reservedAtUtc),
    completed = completedAtUtc === null ? null : utcTime(completedAtUtc);
  if (
    reserved === null ||
    (completedAtUtc !== null && completed === null) ||
    terminal(phase) !== (completed !== null) ||
    (completed !== null && completed < reserved)
  )
    throw incompatible();
  const committed = phase === "committed";
  if (
    committed
      ? !followsReviewedHead(
          committedSequence,
          expected.reviewedHead.sequence,
        ) ||
        (committedAdmission !== "valid" &&
          committedAdmission !== "decodedInvalid")
      : committedSequence !== null || committedAdmission !== null
  )
    throw incompatible();
  if (
    (claimCount === 0 && (phase !== "reserved" || latestFailure !== null)) ||
    (phase === "attemptExhausted" && claimCount !== 8) ||
    ((committed || phase === "headConflict") && latestFailure !== null) ||
    (phase === "rejected" &&
      latestFailure !== null &&
      latestFailure !== "evidenceLimit") ||
    (latestFailure === "evidenceLimit" && phase !== "rejected")
  )
    throw incompatible();
  const receipt: NativeRevisionAppendOwnedReceipt = Object.freeze({
    operationId,
    project: ownProject(project),
    futureMapping: ownMapping(futureMapping),
    futureRevision: ownRevision(futureRevision),
    phase: phase as NativeRevisionAppendPhase,
    claimCount,
    reservedAtUtc: reservedAtUtc as string,
    completedAtUtc: completedAtUtc as string | null,
    originalFilename,
    committedSequence: committedSequence as string | null,
    committedAdmission:
      committedAdmission as NativeRevisionAppendReceipt["committedAdmission"],
    latestFailure: latestFailure as NativeRevisionAppendFailure,
  });
  if (
    previous &&
    (receipt.operationId !== previous.operationId ||
      !sameMapping(receipt.futureMapping, previous.futureMapping) ||
      !sameRevision(receipt.futureRevision, previous.futureRevision) ||
      reserved !== utcTime(previous.reservedAtUtc) ||
      claimCount < previous.claimCount ||
      (terminal(previous.phase) && !sameTerminal(receipt, previous)))
  )
    throw incompatible();
  return receipt;
}
function sameTerminal(
  left: NativeRevisionAppendOwnedReceipt,
  right: NativeRevisionAppendOwnedReceipt,
): boolean {
  return (
    left.phase === right.phase &&
    left.claimCount === right.claimCount &&
    utcTime(left.completedAtUtc) === utcTime(right.completedAtUtc) &&
    left.committedSequence === right.committedSequence &&
    left.committedAdmission === right.committedAdmission &&
    left.latestFailure === right.latestFailure
  );
}
function terminal(phase: string): boolean {
  return (
    phase === "committed" ||
    phase === "rejected" ||
    phase === "headConflict" ||
    phase === "attemptExhausted"
  );
}
function positiveSequence(value: unknown): value is string {
  return (
    typeof value === "string" &&
    value.length >= 1 &&
    value.length <= 19 &&
    /^[1-9][0-9]*$/.test(value) &&
    BigInt(value) <= maximumSequence
  );
}
function followsReviewedHead(
  value: unknown,
  reviewed: string,
): value is string {
  return (
    positiveSequence(value) &&
    positiveSequence(reviewed) &&
    BigInt(value) === BigInt(reviewed) + 1n
  );
}
function requireViewAdmission(admission: NativeRevisionAppendAdmission): {
  readonly mode: "development";
  readonly actorPrincipalId: string;
  readonly scope: Readonly<CatalogMappingScope>;
  readonly backendId: string;
} {
  const context = admission?.context,
    mapping = admission?.mapping;
  if (
    context?.mode !== "development" ||
    !isCatalogId(context.actorPrincipalId) ||
    !isCatalogId(context.allowedOrganizationId)
  )
    throw new NativeRevisionAppendError("identityRejected");
  if (!mappingScope(mapping?.scope)) throw invalidInput();
  if (
    context.allowedOrganizationId !== mapping.scope.organizationId ||
    context.features?.readCatalog !== true ||
    mapping.access?.actions?.view !== true
  )
    throw new NativeRevisionAppendError("notFound");
  if (!readMappingHead(mapping)) throw invalidInput();
  return Object.freeze({
    mode: "development",
    actorPrincipalId: context.actorPrincipalId,
    scope: ownMapping(mapping.scope),
    backendId: mapping.backendId,
  });
}
function readMappingHead(mapping: unknown): HeadFacts | null {
  if (
    !object(mapping) ||
    !mappingScope(mapping["scope"]) ||
    !object(mapping["head"])
  )
    return null;
  const head = mapping["head"],
    backendId = mapping["backendId"];
  if (
    !keys(head, ["scope", "sequence", "admission"]) ||
    !revisionScope(head["scope"]) ||
    !sameMapping(head["scope"], mapping["scope"]) ||
    !positiveSequence(head["sequence"]) ||
    (head["admission"] !== "valid" && head["admission"] !== "decodedInvalid") ||
    typeof backendId !== "string" ||
    backendId.length > 128 ||
    trimCatalogName(backendId).length === 0
  )
    return null;
  const backend = inspectNativeIngestionText(backendId);
  if (backend.error !== null || backend.bytes > 128) return null;
  return ownHead({
    scope: head["scope"],
    sequence: head["sequence"],
    admission: head["admission"],
    backendId,
  });
}
function sameHead(left: HeadFacts, right: HeadFacts): boolean {
  return (
    sameRevision(left.scope, right.scope) &&
    left.sequence === right.sequence &&
    left.admission === right.admission &&
    left.backendId === right.backendId
  );
}
function ownHead(value: HeadFacts): HeadFacts {
  return Object.freeze({
    scope: ownRevision(value.scope),
    sequence: value.sequence,
    admission: value.admission,
    backendId: value.backendId,
  });
}
function ownExpectation(value: Expectation): Expectation {
  // The shared head is an already owned frozen brand with primitive facts and no parent references.
  headFacts(value.reviewedHead);
  return Object.freeze({
    mode: value.mode,
    actorPrincipalId: value.actorPrincipalId,
    scope: ownMapping(value.scope),
    backendId: value.backendId,
    origin: value.origin,
    reviewedHead: value.reviewedHead,
    originalFilename: value.originalFilename,
    digest: value.digest,
    byteCount: value.byteCount,
  });
}
function projectPath(scope: Readonly<CatalogProjectScope>): string {
  return `/api/v1/organizations/${scope.organizationId}/projects/${scope.projectId}`;
}
function appendPath(scope: Readonly<CatalogMappingScope>): string {
  return projectPath(scope) + "/mappings/" + scope.mappingId + "/revisions";
}
function revisionPath(scope: Readonly<CatalogRevisionScope>): string {
  return appendPath(scope) + "/" + scope.revisionId;
}
function encodedQuery(
  expectedHeadRevisionId: string,
  originalFilename: string,
): string {
  return (
    "?head=equal&expectedHeadRevisionId=" +
    expectedHeadRevisionId +
    "&originalFilename=" +
    encodeNativeIngestionQueryValue(originalFilename)
  );
}
function ownProject(
  scope: Readonly<CatalogProjectScope>,
): Readonly<CatalogProjectScope> {
  return Object.freeze({
    organizationId: scope.organizationId,
    projectId: scope.projectId,
  });
}
function ownMapping(
  scope: Readonly<CatalogMappingScope>,
): Readonly<CatalogMappingScope> {
  return Object.freeze({
    organizationId: scope.organizationId,
    projectId: scope.projectId,
    mappingId: scope.mappingId,
  });
}
function ownRevision(
  scope: Readonly<CatalogRevisionScope>,
): Readonly<CatalogRevisionScope> {
  return Object.freeze({
    organizationId: scope.organizationId,
    projectId: scope.projectId,
    mappingId: scope.mappingId,
    revisionId: scope.revisionId,
  });
}
function projectScope(value: unknown): value is CatalogProjectScope {
  return (
    object(value) &&
    keys(value, ["organizationId", "projectId"]) &&
    isCatalogId(value["organizationId"]) &&
    isCatalogId(value["projectId"])
  );
}
function mappingScope(value: unknown): value is CatalogMappingScope {
  return (
    object(value) &&
    keys(value, ["organizationId", "projectId", "mappingId"]) &&
    isCatalogId(value["organizationId"]) &&
    isCatalogId(value["projectId"]) &&
    isCatalogId(value["mappingId"])
  );
}
function revisionScope(value: unknown): value is CatalogRevisionScope {
  return (
    object(value) &&
    keys(value, ["organizationId", "projectId", "mappingId", "revisionId"]) &&
    isCatalogId(value["organizationId"]) &&
    isCatalogId(value["projectId"]) &&
    isCatalogId(value["mappingId"]) &&
    isCatalogId(value["revisionId"])
  );
}
function sameProject(
  left: unknown,
  right: Readonly<CatalogProjectScope>,
): boolean {
  return (
    object(left) &&
    left["organizationId"] === right.organizationId &&
    left["projectId"] === right.projectId
  );
}
function sameMapping(
  left: unknown,
  right: Readonly<CatalogMappingScope>,
): boolean {
  return (
    sameProject(left, right) &&
    object(left) &&
    left["mappingId"] === right.mappingId
  );
}
function sameRevision(
  left: unknown,
  right: Readonly<CatalogRevisionScope>,
): boolean {
  return (
    sameMapping(left, right) &&
    object(left) &&
    left["revisionId"] === right.revisionId
  );
}
function object(value: unknown): value is JsonObject {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}
function keys(value: JsonObject, expected: readonly string[]): boolean {
  return (
    Object.keys(value).length === expected.length &&
    expected.every((key) => Object.hasOwn(value, key))
  );
}
function keysWithin(
  value: JsonObject,
  allowed: readonly string[],
  required: readonly string[],
): boolean {
  return (
    required.every((key) => Object.hasOwn(value, key)) &&
    Object.keys(value).every((key) => allowed.includes(key))
  );
}
function optional(value: JsonObject, key: string): unknown {
  return Object.hasOwn(value, key) ? value[key] : null;
}
function known(value: unknown, values: readonly string[]): value is string {
  return typeof value === "string" && values.includes(value);
}
function nullableKnown(
  value: unknown,
  values: readonly string[],
): value is string | null {
  return value === null || known(value, values);
}
function utcTime(value: unknown): string | null {
  if (typeof value !== "string" || value.length > 33) return null;
  const match =
    /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.(\d{1,7}))?(?:Z|\+00:00)$/.exec(
      value,
    );
  if (!match) return null;
  const year = Number(match[1]),
    month = Number(match[2]),
    day = Number(match[3]);
  const days = [
    31,
    year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0) ? 29 : 28,
    31,
    30,
    31,
    30,
    31,
    31,
    30,
    31,
    30,
    31,
  ];
  if (
    year < 1 ||
    month < 1 ||
    month > 12 ||
    day < 1 ||
    day > days[month - 1]! ||
    Number(match[4]) > 23 ||
    Number(match[5]) > 59 ||
    Number(match[6]) > 59
  )
    return null;
  return (
    match[1] +
    match[2] +
    match[3] +
    match[4] +
    match[5] +
    match[6] +
    (match[7] ?? "").padEnd(7, "0")
  );
}
function headFacts(head: NativeRevisionAppendHead): HeadFacts {
  const facts = heads.get(head);
  if (!facts) throw invalidInput();
  return facts;
}
function intentFacts(intent: NativeRevisionAppendIntent): IntentFacts {
  const facts = intents.get(intent);
  if (!facts) throw invalidInput();
  return facts;
}
function operationFacts(
  operation: NativeRevisionAppendOperation,
): OperationFacts {
  const facts = operations.get(operation);
  if (!facts) throw invalidInput();
  return facts;
}
function bindingExpectation(
  binding: NativeRevisionAppendIntent | NativeRevisionAppendOperation,
): Expectation {
  const intent = intents.get(binding as NativeRevisionAppendIntent);
  return intent
    ? intent.expectation
    : operationFacts(binding as NativeRevisionAppendOperation).expectation;
}
function invalidInput(): NativeRevisionAppendError {
  return new NativeRevisionAppendError("invalidInput");
}
function incompatible(): NativeRevisionAppendError {
  return new NativeRevisionAppendError("incompatible");
}
