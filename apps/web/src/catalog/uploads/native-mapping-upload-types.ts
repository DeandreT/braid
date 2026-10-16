import type { components } from "../../api/generated";
import {
  NativeIngestionSource,
  NativeIngestionSourceError,
  encodeNativeIngestionQueryValue as encode,
  inspectNativeIngestionText as unicode,
  nativeIngestionFilenameError as filenameError,
  nativeIngestionWireTargetError,
  withNativeIngestionSource,
} from "./native-ingestion-leaves";
import { trimCatalogName } from "../catalog-text";
import {
  isCatalogId,
  type CatalogContext,
  type CatalogProject,
  type CatalogProjectScope,
  type CatalogRevision,
} from "../catalog-types";

export type NativeMappingUploadResult =
  components["schemas"]["CatalogRevisionIngestionResultV1"];
export type NativeMappingUploadReceipt =
  components["schemas"]["CatalogRevisionIngestionReceiptV1"];
export type NativeMappingUploadOutcome =
  components["schemas"]["CatalogRevisionIngestionOutcomeV1"];
export type NativeMappingUploadPhase =
  components["schemas"]["CatalogRevisionIngestionPhaseV1"];
export type NativeMappingUploadFailure =
  components["schemas"]["CatalogRevisionIngestionFailureV1"];
export type NativeMappingUploadRejection =
  components["schemas"]["CatalogRevisionIngestionRejectionV1"];

export interface NativeMappingUploadAdmission {
  readonly context: CatalogContext;
  readonly project: CatalogProject;
}
export interface NativeMappingUploadInput {
  readonly name: string;
  readonly description: string;
  readonly originalFilename: string;
}
export type NativeMappingUploadFieldError =
  | "required"
  | "invalidType"
  | "invalidUnicode"
  | "nul"
  | "scalarLimit"
  | "byteLimit"
  | "unsafeFilename";
export interface NativeMappingUploadInputErrors {
  readonly name: NativeMappingUploadFieldError | null;
  readonly description: NativeMappingUploadFieldError | null;
  readonly originalFilename: NativeMappingUploadFieldError | null;
  readonly request: "queryLimit" | "requestLineLimit" | "invalidScope" | null;
}
export type NativeMappingUploadInputValidation =
  | { readonly valid: false; readonly errors: NativeMappingUploadInputErrors }
  | {
      readonly valid: true;
      readonly acceptedName: string;
      readonly description: string;
      readonly originalFilename: string;
    };
export type NativeMappingUploadErrorKind =
  | "invalidInput"
  | "emptyNative"
  | "nativeLimit"
  | "fileReadUnavailable"
  | "hashUnavailable"
  | "canceled"
  | "identityChanged"
  | "identityRejected"
  | "featureDisabled"
  | "uploadUnavailable"
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

/** Current-send categories only; the state independently retains earlier uncertainty. */
export class NativeMappingUploadError extends Error {
  constructor(
    readonly kind: NativeMappingUploadErrorKind,
    readonly status: number | null = null,
    readonly mayHaveCommitted = false,
  ) {
    super("The native mapping upload request could not be completed.");
    this.name = "NativeMappingUploadError";
  }
}

type JsonObject = Record<string, unknown>;
type SourceFacts = {
  readonly body: Blob;
  readonly digest: string;
  readonly byteCount: number;
};
type Owner = {
  readonly mode: "development";
  readonly actorPrincipalId: string;
  readonly scope: CatalogProjectScope;
};
type Expectation = Owner & {
  readonly origin: symbol;
  readonly originalFilename: string;
  readonly digest: string;
  readonly byteCount: number;
};
type IntentFacts = {
  readonly expectation: Expectation;
  readonly body: Blob;
  readonly key: string;
  readonly url: string;
  readonly acceptedName: string;
  readonly description: string;
};
type OperationFacts = {
  readonly expectation: Expectation;
  readonly receipt: NativeMappingUploadReceipt;
};
const sources = new WeakMap<NativeMappingUploadSource, SourceFacts>();
const intents = new WeakMap<NativeMappingUploadIntent, IntentFacts>();
const operations = new WeakMap<NativeMappingUploadOperation, OperationFacts>();

/** Validate accepted text and the exact encoded target, without retaining the raw name. */
export function validateNativeMappingUploadInput(
  scope: CatalogProjectScope,
  input: NativeMappingUploadInput,
): NativeMappingUploadInputValidation {
  let name: NativeMappingUploadFieldError | null = null;
  let description: NativeMappingUploadFieldError | null = null;
  let originalFilename: NativeMappingUploadFieldError | null = null;
  let request: NativeMappingUploadInputErrors["request"] = null;
  let acceptedName = "";
  if (!projectScope(scope)) request = "invalidScope";
  if (typeof input?.name !== "string")
    name = input?.name == null ? "required" : "invalidType";
  else {
    name = unicode(input.name).error;
    if (name === null) {
      acceptedName = trimCatalogName(input.name);
      const accepted = unicode(acceptedName);
      name =
        accepted.scalars === 0
          ? "required"
          : accepted.scalars > 200
            ? "scalarLimit"
            : accepted.bytes > 800
              ? "byteLimit"
              : null;
    }
  }
  if (typeof input?.description !== "string") description = "invalidType";
  else if (input.description.length > 8000) description = "scalarLimit";
  else {
    const facts = unicode(input.description);
    description =
      facts.error ??
      (facts.scalars > 4000
        ? "scalarLimit"
        : facts.bytes > 16000
          ? "byteLimit"
          : null);
  }
  if (typeof input?.originalFilename !== "string")
    originalFilename = "invalidType";
  else originalFilename = filenameError(input.originalFilename);
  if (
    name === null &&
    description === null &&
    originalFilename === null &&
    request === null
  ) {
    const query = encodedQuery(
      acceptedName,
      input.description,
      input.originalFilename,
    );
    if (query.length > 65536) request = "queryLimit";
    else request = nativeIngestionWireTargetError(createPath(scope), query);
  }
  if (
    name !== null ||
    description !== null ||
    originalFilename !== null ||
    request !== null
  )
    return Object.freeze({
      valid: false,
      errors: Object.freeze({ name, description, originalFilename, request }),
    });
  return Object.freeze({
    valid: true,
    acceptedName,
    description: input.description,
    originalFilename: input.originalFilename,
  });
}

/** One unchanged byte snapshot; neither this object nor its diagnostics expose the content/digest. */
export class NativeMappingUploadSource {
  private constructor(facts: SourceFacts) {
    sources.set(this, facts);
    Object.freeze(this);
  }

  static async capture(
    file: Blob,
    signal?: AbortSignal,
  ): Promise<NativeMappingUploadSource> {
    try {
      const source = await NativeIngestionSource.capture(file, signal);
      return withNativeIngestionSource(
        source,
        (facts) => new NativeMappingUploadSource(facts),
      );
    } catch (error: unknown) {
      if (error instanceof NativeIngestionSourceError)
        throw new NativeMappingUploadError(error.kind);
      throw error;
    }
  }
  get byteCount(): number {
    return sourceFacts(this).byteCount;
  }
  toString(): string {
    return "NativeMappingUploadSource";
  }
  toJSON(): string {
    return "NativeMappingUploadSource";
  }
}

/** The original wire request remains private and cannot acquire a different owner on recovery. */
export class NativeMappingUploadIntent {
  private constructor(facts: IntentFacts) {
    intents.set(this, facts);
    Object.freeze(this);
  }

  static create(
    admission: NativeMappingUploadAdmission,
    input: NativeMappingUploadInput,
    source: NativeMappingUploadSource,
    idempotencyKey: string,
  ): NativeMappingUploadIntent {
    const owner = captureNativeMappingUploadAdmission(admission);
    const accepted = validateNativeMappingUploadInput(owner.scope, input);
    if (!accepted.valid || !isCatalogId(idempotencyKey))
      throw new NativeMappingUploadError("invalidInput");
    const material = sourceFacts(source);
    const expectation = ownExpectation({
      ...owner,
      origin: Symbol(),
      originalFilename: accepted.originalFilename,
      digest: material.digest,
      byteCount: material.byteCount,
    });
    return new NativeMappingUploadIntent(
      Object.freeze({
        expectation,
        body: material.body,
        key: idempotencyKey,
        url:
          createPath(owner.scope) +
          encodedQuery(
            accepted.acceptedName,
            accepted.description,
            accepted.originalFilename,
          ),
        acceptedName: accepted.acceptedName,
        description: accepted.description,
      }),
    );
  }
  get scope(): CatalogProjectScope {
    return intentFacts(this).expectation.scope;
  }
  get actorPrincipalId(): string {
    return intentFacts(this).expectation.actorPrincipalId;
  }
  get acceptedName(): string {
    return intentFacts(this).acceptedName;
  }
  get description(): string {
    return intentFacts(this).description;
  }
  get originalFilename(): string {
    return intentFacts(this).expectation.originalFilename;
  }
  get byteCount(): number {
    return intentFacts(this).expectation.byteCount;
  }
  /** @internal Transport callback only. Never publish its arguments in view state or diagnostics. */
  withRequest<T>(send: (url: string, key: string, body: Blob) => T): T {
    const facts = intentFacts(this);
    return send(facts.url, facts.key, facts.body);
  }
  toString(): string {
    return "NativeMappingUploadIntent";
  }
  toJSON(): string {
    return "NativeMappingUploadIntent";
  }
}

/** Safe receipt plus primitive expectations only: no source, intent, Blob, key or query is retained. */
export class NativeMappingUploadOperation {
  private constructor(
    expectation: Expectation,
    receipt: NativeMappingUploadReceipt,
  ) {
    operations.set(
      this,
      Object.freeze({ expectation: ownExpectation(expectation), receipt }),
    );
    Object.freeze(this);
  }
  /** @internal Only an actual response bound to the original request/learned operation can learn facts. */
  static fromResponse(
    binding: NativeMappingUploadIntent | NativeMappingUploadOperation,
    value: unknown,
    previous: NativeMappingUploadOperation | null = null,
  ): NativeMappingUploadOperation {
    const expectation = bindingExpectation(binding);
    if (previous && bindingExpectation(previous).origin !== expectation.origin)
      throw incompatible();
    const old =
      previous ??
      (binding instanceof NativeMappingUploadOperation ? binding : null);
    const receipt = readReceipt(value, expectation, old?.receipt ?? null);
    return new NativeMappingUploadOperation(expectation, receipt);
  }
  get receipt(): NativeMappingUploadReceipt {
    return operationFacts(this).receipt;
  }
  get scope(): CatalogProjectScope {
    return operationFacts(this).expectation.scope;
  }
  get actorPrincipalId(): string {
    return operationFacts(this).expectation.actorPrincipalId;
  }
  get committed(): boolean {
    return this.receipt.phase === "committed";
  }
  get terminal(): boolean {
    return terminal(this.receipt.phase);
  }
  get committedRevisionUrl(): string | null {
    return this.committed ? revisionPath(this.receipt) : null;
  }
  matchesRevision(revision: CatalogRevision): boolean {
    const { expectation, receipt } = operationFacts(this);
    return (
      this.committed &&
      !!revision &&
      object(revision.scope) &&
      sameProject(revision.scope, receipt.futureRevision) &&
      revision.scope.mappingId === receipt.futureRevision.mappingId &&
      revision.scope.revisionId === receipt.futureRevision.revisionId &&
      revision.sequence === "1" &&
      revision.admission === receipt.committedAdmission &&
      revision.creatorPrincipalId === expectation.actorPrincipalId &&
      revision.backendId === "ferrule" &&
      revision.logicalDocument === "entry.native.json" &&
      revision.originalFilename === expectation.originalFilename &&
      revision.nativeByteCount === expectation.byteCount &&
      revision.nativeDigest?.algorithm === "sha256" &&
      revision.nativeDigest.value === expectation.digest
    );
  }
  toString(): string {
    return "NativeMappingUploadOperation";
  }
  toJSON(): string {
    return "NativeMappingUploadOperation";
  }
}

export interface NativeMappingUploadResponse {
  readonly status: 200 | 202 | 400 | 404 | 409 | 413 | 422 | 503;
  readonly outcome: NativeMappingUploadOutcome;
  readonly operation: NativeMappingUploadOperation | null;
  readonly rejection: NativeMappingUploadRejection;
  readonly failure: NativeMappingUploadFailure;
  readonly committedRevisionUrl: string | null;
  readonly receiptLocation: string | null;
}

/** @internal Callers must provide a fresh admitted context/project for every operation. */
export function captureNativeMappingUploadAdmission(
  admission: NativeMappingUploadAdmission,
  binding?: NativeMappingUploadIntent | NativeMappingUploadOperation,
  previous?: NativeMappingUploadOperation,
): Owner {
  const context = admission?.context,
    project = admission?.project;
  if (
    context?.mode !== "development" ||
    !isCatalogId(context.actorPrincipalId) ||
    !isCatalogId(context.allowedOrganizationId)
  )
    throw new NativeMappingUploadError("identityRejected");
  if (!projectScope(project?.scope))
    throw new NativeMappingUploadError("invalidInput");
  const scope = ownProject(project.scope);
  if (
    scope.organizationId !== context.allowedOrganizationId ||
    context.features?.readCatalog !== true ||
    project.access?.actions?.view !== true
  )
    throw new NativeMappingUploadError("notFound");
  if (binding) {
    const original = bindingExpectation(binding);
    if (
      original.mode !== context.mode ||
      original.actorPrincipalId !== context.actorPrincipalId ||
      !sameProject(original.scope, scope)
    )
      throw new NativeMappingUploadError("identityChanged");
    if (previous && bindingExpectation(previous).origin !== original.origin)
      throw new NativeMappingUploadError("invalidInput");
  }
  if (context.features.createMappings !== true)
    throw new NativeMappingUploadError("featureDisabled");
  if (project.access.actions.createMappings !== true)
    throw new NativeMappingUploadError("uploadUnavailable");
  return Object.freeze({
    mode: "development",
    actorPrincipalId: context.actorPrincipalId,
    scope,
  });
}

/** @internal Mutation result guard, including result-shaped non2xx responses. */
export function readNativeMappingUploadResponse(
  status: number,
  value: unknown,
  location: string | null,
  binding: NativeMappingUploadIntent | NativeMappingUploadOperation,
  previous: NativeMappingUploadOperation | null = null,
): NativeMappingUploadResponse {
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
    if (!known(outcome, outcomes) || outcome === "headConflict")
      throw incompatible();
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
    const operation =
      rawReceipt === null
        ? null
        : NativeMappingUploadOperation.fromResponse(
            binding,
            rawReceipt,
            previous,
          );
    const receipt = operation?.receipt ?? null;
    const pending = receipt !== null && !terminal(receipt.phase);
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
      status === 202 ? nativeMappingUploadReceiptPath(operation!) : null;
    if (status === 202 && location !== receiptLocation) throw incompatible();
    return Object.freeze({
      status: status as NativeMappingUploadResponse["status"],
      outcome: outcome as NativeMappingUploadOutcome,
      operation,
      rejection: rejection as NativeMappingUploadRejection,
      failure: failure as NativeMappingUploadFailure,
      committedRevisionUrl: url,
      receiptLocation,
    });
  } catch {
    throw new NativeMappingUploadError("incompatible", status, true);
  }
}

/** @internal Scope comes only from an admitted own operation, never a pasted operation ID. */
export function nativeMappingUploadReceiptPath(
  operation: NativeMappingUploadOperation,
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

function readReceipt(
  value: unknown,
  expected: Expectation,
  previous: NativeMappingUploadReceipt | null,
): NativeMappingUploadReceipt {
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
    !object(futureMapping) ||
    !keys(futureMapping, ["organizationId", "projectId", "mappingId"]) ||
    !sameProject(futureMapping, project) ||
    !isCatalogId(futureMapping["mappingId"]) ||
    !object(futureRevision) ||
    !keys(futureRevision, [
      "organizationId",
      "projectId",
      "mappingId",
      "revisionId",
    ]) ||
    !sameProject(futureRevision, project) ||
    futureRevision["mappingId"] !== futureMapping["mappingId"] ||
    !isCatalogId(futureRevision["revisionId"]) ||
    !known(phase, phases) ||
    phase === "headConflict" ||
    typeof claimCount !== "number" ||
    !Number.isInteger(claimCount) ||
    claimCount < 0 ||
    claimCount > 8 ||
    typeof originalFilename !== "string" ||
    originalFilename !== expected.originalFilename ||
    filenameError(originalFilename) !== null ||
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
      ? committedSequence !== "1" ||
        (committedAdmission !== "valid" &&
          committedAdmission !== "decodedInvalid")
      : committedSequence !== null || committedAdmission !== null
  )
    throw incompatible();
  if (
    (claimCount === 0 && (phase !== "reserved" || latestFailure !== null)) ||
    (phase === "attemptExhausted" && claimCount !== 8) ||
    (committed && latestFailure !== null) ||
    (phase === "rejected" &&
      latestFailure !== null &&
      latestFailure !== "evidenceLimit") ||
    (latestFailure === "evidenceLimit" && phase !== "rejected")
  )
    throw incompatible();
  const receipt: NativeMappingUploadReceipt = Object.freeze({
    operationId,
    project: ownProject(project),
    futureMapping: Object.freeze({
      ...ownProject(project),
      mappingId: futureMapping["mappingId"],
    }),
    futureRevision: Object.freeze({
      ...ownProject(project),
      mappingId: futureMapping["mappingId"],
      revisionId: futureRevision["revisionId"],
    }),
    phase: phase as NativeMappingUploadPhase,
    claimCount,
    reservedAtUtc: reservedAtUtc as string,
    completedAtUtc: completedAtUtc as string | null,
    originalFilename,
    committedSequence: committedSequence as string | null,
    committedAdmission:
      committedAdmission as NativeMappingUploadReceipt["committedAdmission"],
    latestFailure: latestFailure as NativeMappingUploadFailure,
  });
  if (
    previous &&
    (operationId !== previous.operationId ||
      receipt.futureMapping.mappingId !== previous.futureMapping.mappingId ||
      receipt.futureRevision.revisionId !==
        previous.futureRevision.revisionId ||
      reserved !== utcTime(previous.reservedAtUtc) ||
      claimCount < previous.claimCount ||
      (terminal(previous.phase) && !sameTerminal(receipt, previous)))
  )
    throw incompatible();
  return receipt;
}
function sameTerminal(
  left: NativeMappingUploadReceipt,
  right: NativeMappingUploadReceipt,
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
function terminal(phase: NativeMappingUploadPhase | string): boolean {
  return (
    phase === "committed" ||
    phase === "rejected" ||
    phase === "headConflict" ||
    phase === "attemptExhausted"
  );
}
function revisionPath(receipt: NativeMappingUploadReceipt): string {
  return (
    createPath(receipt.project) +
    "/" +
    receipt.futureRevision.mappingId +
    "/revisions/" +
    receipt.futureRevision.revisionId
  );
}
function projectPath(scope: CatalogProjectScope): string {
  return `/api/v1/organizations/${scope.organizationId}/projects/${scope.projectId}`;
}
function createPath(scope: CatalogProjectScope): string {
  return projectPath(scope) + "/mappings";
}
function encodedQuery(
  name: string,
  description: string,
  filename: string,
): string {
  return (
    "?name=" +
    encode(name) +
    "&backendId=ferrule&description=" +
    encode(description) +
    "&originalFilename=" +
    encode(filename)
  );
}
function utcTime(value: unknown): string | null {
  if (typeof value !== "string") return null;
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
function ownProject(scope: CatalogProjectScope): CatalogProjectScope {
  return Object.freeze({
    organizationId: scope.organizationId,
    projectId: scope.projectId,
  });
}
function ownExpectation(value: Expectation): Expectation {
  return Object.freeze({
    mode: value.mode,
    actorPrincipalId: value.actorPrincipalId,
    scope: ownProject(value.scope),
    origin: value.origin,
    originalFilename: value.originalFilename,
    digest: value.digest,
    byteCount: value.byteCount,
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
function sameProject(left: unknown, right: CatalogProjectScope): boolean {
  return (
    object(left) &&
    left["organizationId"] === right.organizationId &&
    left["projectId"] === right.projectId
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
function sourceFacts(source: NativeMappingUploadSource): SourceFacts {
  const facts = sources.get(source);
  if (!facts) throw new NativeMappingUploadError("invalidInput");
  return facts;
}
function intentFacts(intent: NativeMappingUploadIntent): IntentFacts {
  const facts = intents.get(intent);
  if (!facts) throw new NativeMappingUploadError("invalidInput");
  return facts;
}
function operationFacts(
  operation: NativeMappingUploadOperation,
): OperationFacts {
  const facts = operations.get(operation);
  if (!facts) throw new NativeMappingUploadError("invalidInput");
  return facts;
}
function bindingExpectation(
  binding: NativeMappingUploadIntent | NativeMappingUploadOperation,
): Expectation {
  return binding instanceof NativeMappingUploadIntent
    ? intentFacts(binding).expectation
    : operationFacts(binding).expectation;
}
function incompatible(): NativeMappingUploadError {
  return new NativeMappingUploadError("incompatible");
}
