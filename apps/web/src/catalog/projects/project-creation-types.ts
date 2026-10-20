import type { components } from "../../api/generated";
import { isCatalogId, type CatalogContext } from "../catalog-types";
import { trimCatalogName } from "../catalog-text";

export { trimCatalogName as trimProjectCreationName } from "../catalog-text";

export type CatalogProjectCreate =
  components["schemas"]["CatalogProjectCreateV1"];
export type CatalogProjectCreationResult =
  components["schemas"]["CatalogProjectCreationResultV1"];
export type CatalogProjectCreationReceipt =
  components["schemas"]["CatalogProjectCreationReceiptV1"];

export interface ProjectCreationResponse {
  readonly status: 200 | 201;
  readonly body: CatalogProjectCreationResult;
  readonly location: string;
}

export type ProjectCreationFieldError =
  | "required"
  | "invalidType"
  | "invalidUnicode"
  | "nul"
  | "scalarLimit"
  | "byteLimit";
export interface ProjectCreationInputErrors {
  readonly name: ProjectCreationFieldError | null;
  readonly description: ProjectCreationFieldError | null;
  readonly body: "bodyLimit" | null;
}
export type ProjectCreationInputValidation =
  | { readonly valid: false; readonly errors: ProjectCreationInputErrors }
  | {
      readonly valid: true;
      readonly acceptedName: string;
      readonly description: string;
      readonly body: string;
      readonly bodyByteCount: number;
    };

export type ProjectCreationErrorKind =
  | "invalidInput"
  | "identityChanged"
  | "featureDisabled"
  | "identityRejected"
  | "permissionDenied"
  | "invalidRequest"
  | "notFound"
  | "idempotencyConflict"
  | "bodyLimit"
  | "unsupportedMediaType"
  | "admissionBusy"
  | "persistenceFailure"
  | "internalFailure"
  | "timedOut"
  | "unavailable"
  | "incompatible";

/** mayHaveCommitted concerns this send; a later refusal cannot resolve earlier uncertainty. */
export class ProjectCreationError extends Error {
  readonly mayHaveCommitted: boolean;

  constructor(
    readonly kind: ProjectCreationErrorKind,
    readonly status: number | null = null,
  ) {
    super("The project creation request could not be completed.");
    this.name = "ProjectCreationError";
    this.mayHaveCommitted = [
      "persistenceFailure",
      "internalFailure",
      "timedOut",
      "unavailable",
      "incompatible",
    ].includes(kind);
  }
}

/** Validate raw form values before JSON.stringify can replace invalid surrogate sequences. */
export function validateProjectCreationInput(
  name: unknown,
  description: unknown,
): ProjectCreationInputValidation {
  let nameError: ProjectCreationFieldError | null = null;
  let descriptionError: ProjectCreationFieldError | null = null;
  let bodyError: "bodyLimit" | null = null;
  let acceptedName = "";
  if (typeof name !== "string")
    nameError = name == null ? "required" : "invalidType";
  else if (name.length > 65536) bodyError = "bodyLimit";
  else {
    nameError = unicode(name).error;
    if (nameError === null) {
      acceptedName = trimCatalogName(name);
      const accepted = unicode(acceptedName);
      nameError =
        accepted.scalars === 0
          ? "required"
          : accepted.scalars > 200
            ? "scalarLimit"
            : accepted.bytes > 800
              ? "byteLimit"
              : null;
    }
  }
  if (typeof description !== "string") descriptionError = "invalidType";
  else if (description.length > 8000) descriptionError = "scalarLimit";
  else {
    const facts = unicode(description);
    descriptionError =
      facts.error ??
      (facts.scalars > 4000
        ? "scalarLimit"
        : facts.bytes > 16000
          ? "byteLimit"
          : null);
  }
  if (nameError !== null || descriptionError !== null || bodyError !== null) {
    return Object.freeze({
      valid: false,
      errors: Object.freeze({
        name: nameError,
        description: descriptionError,
        body: bodyError,
      }),
    });
  }
  const body = JSON.stringify({ name, description });
  const bodyByteCount = new TextEncoder().encode(body).byteLength;
  if (bodyByteCount > 65536)
    return Object.freeze({
      valid: false,
      errors: Object.freeze({
        name: null,
        description: null,
        body: "bodyLimit",
      }),
    });
  return Object.freeze({
    valid: true,
    acceptedName,
    description: description as string,
    body,
    bodyByteCount,
  });
}

function unicode(value: string): {
  scalars: number;
  bytes: number;
  error: "invalidUnicode" | "nul" | null;
} {
  let scalars = 0,
    bytes = 0;
  for (let index = 0; index < value.length; ++index) {
    const code = value.charCodeAt(index);
    if (code === 0) return { scalars, bytes, error: "nul" };
    ++scalars;
    if (code >= 0xd800 && code <= 0xdbff) {
      const low = value.charCodeAt(++index);
      if (!(low >= 0xdc00 && low <= 0xdfff))
        return { scalars, bytes, error: "invalidUnicode" };
      bytes += 4;
    } else if (code >= 0xdc00 && code <= 0xdfff)
      return { scalars, bytes, error: "invalidUnicode" };
    else bytes += code <= 0x7f ? 1 : code <= 0x7ff ? 2 : 3;
  }
  return { scalars, bytes, error: null };
}

/** One immutable memory-only original request. JSON/diagnostic text never exports its values. */
export class ProjectCreationIntent {
  readonly #mode: CatalogContext["mode"];
  readonly #organizationId: string;
  readonly #actorPrincipalId: string;
  readonly #key: string;
  readonly #body: string;
  readonly #acceptedName: string;
  readonly #description: string;

  private constructor(
    mode: CatalogContext["mode"],
    organizationId: string,
    actorPrincipalId: string,
    key: string,
    accepted: Extract<ProjectCreationInputValidation, { valid: true }>,
  ) {
    this.#mode = mode;
    this.#organizationId = organizationId;
    this.#actorPrincipalId = actorPrincipalId;
    this.#key = key;
    this.#body = accepted.body;
    this.#acceptedName = accepted.acceptedName;
    this.#description = accepted.description;
    Object.freeze(this);
  }

  static create(
    context: CatalogContext,
    input: CatalogProjectCreate,
    key: string,
  ): ProjectCreationIntent {
    const organizationId = context?.allowedOrganizationId,
      actorPrincipalId = context?.actorPrincipalId;
    if (
      !isCatalogId(organizationId) ||
      (context.mode !== "development" && context.mode !== "session") ||
      !isCatalogId(actorPrincipalId) ||
      !isCatalogId(key)
    )
      throw new ProjectCreationError("invalidInput");
    const accepted = validateProjectCreationInput(
      input?.name,
      input?.description,
    );
    if (!accepted.valid) throw new ProjectCreationError("invalidInput");
    return new ProjectCreationIntent(
      context.mode,
      organizationId,
      actorPrincipalId,
      key,
      accepted,
    );
  }

  get organizationId(): string {
    return this.#organizationId;
  }
  get mode(): CatalogContext["mode"] {
    return this.#mode;
  }
  get actorPrincipalId(): string {
    return this.#actorPrincipalId;
  }
  get key(): string {
    return this.#key;
  }
  get body(): string {
    return this.#body;
  }
  get acceptedName(): string {
    return this.#acceptedName;
  }
  get description(): string {
    return this.#description;
  }
  toString(): string {
    return "ProjectCreationIntent";
  }
  toJSON(): string {
    return "ProjectCreationIntent";
  }
}
