import type { components } from "../../api/generated";
import { trimCatalogName } from "../catalog-text";
import {
  isCatalogId,
  type CatalogContext,
  type CatalogMapping,
  type CatalogMappingScope,
} from "../catalog-types";

export type CatalogMappingMetadata =
  components["schemas"]["CatalogMappingMetadataV1"];
export type CatalogMappingMetadataEdit =
  components["schemas"]["CatalogMappingMetadataEditV1"];
export interface MappingMetadataAdmission {
  readonly context: CatalogContext;
  readonly mapping: CatalogMapping;
}
export interface MappingMetadataPatchResponse {
  readonly status: 200;
  readonly baseline: MappingMetadataBaseline;
  /** Derived from accepted fields and checked CAS version, not an extra wire field. */
  readonly changed: boolean;
}

export type MappingMetadataFieldError =
  | "required"
  | "invalidType"
  | "invalidUnicode"
  | "nul"
  | "scalarLimit"
  | "byteLimit";
export interface MappingMetadataInputErrors {
  readonly name: MappingMetadataFieldError | null;
  readonly description: MappingMetadataFieldError | null;
  readonly body: "bodyLimit" | null;
}
export type MappingMetadataInputValidation =
  | { readonly valid: false; readonly errors: MappingMetadataInputErrors }
  | {
      readonly valid: true;
      readonly acceptedName: string;
      readonly description: string;
      readonly body: string;
      readonly bodyByteCount: number;
    };

export type MappingMetadataErrorKind =
  | "invalidInput"
  | "identityChanged"
  | "identityRejected"
  | "featureDisabled"
  | "editUnavailable"
  | "notFound"
  | "invalidRequest"
  | "preconditionFailed"
  | "preconditionRequired"
  | "versionExhausted"
  | "bodyLimit"
  | "unsupportedMediaType"
  | "persistenceFailure"
  | "internalFailure"
  | "timedOut"
  | "unavailable"
  | "incompatible";

/** Only an ambiguous dispatched PATCH can set this flag; later refusals cannot resolve earlier uncertainty. */
export class MappingMetadataError extends Error {
  constructor(
    readonly kind: MappingMetadataErrorKind,
    readonly status: number | null = null,
    readonly mayHaveCommitted = false,
  ) {
    super("The mapping metadata request could not be completed.");
    this.name = "MappingMetadataError";
  }
}

/** Metadata-specific replacement validation, independent of project-creation requests. */
export function validateMappingMetadataInput(
  name: unknown,
  description: unknown,
): MappingMetadataInputValidation {
  let nameError: MappingMetadataFieldError | null = null;
  let descriptionError: MappingMetadataFieldError | null = null;
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
  if (nameError !== null || descriptionError !== null || bodyError !== null)
    return Object.freeze({
      valid: false,
      errors: Object.freeze({
        name: nameError,
        description: descriptionError,
        body: bodyError,
      }),
    });
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

/** Owns an actual response representation and its received validator; never a mapping-head snapshot. */
export class MappingMetadataBaseline {
  readonly #mode: CatalogContext["mode"];
  readonly #actorPrincipalId: string;
  readonly #metadata: CatalogMappingMetadata;
  readonly #etag: string;

  private constructor(
    mode: CatalogContext["mode"],
    actorPrincipalId: string,
    metadata: CatalogMappingMetadata,
    etag: string,
  ) {
    this.#mode = mode;
    this.#actorPrincipalId = actorPrincipalId;
    this.#metadata = metadata;
    this.#etag = etag;
    Object.freeze(this);
  }

  /** Transport capture point. The supplied tag is checked, then retained byte-for-byte. */
  static fromResponse(
    actorPrincipalId: string,
    value: unknown,
    receivedEtag: unknown,
    mode: CatalogContext["mode"] = "development",
  ): MappingMetadataBaseline {
    if (
      !isCatalogId(actorPrincipalId) ||
      (mode !== "development" && mode !== "session") ||
      !object(value) ||
      !keys(value, ["scope", "name", "description", "version"]) ||
      !object(value["scope"]) ||
      !keys(value["scope"], ["organizationId", "projectId", "mappingId"]) ||
      !isCatalogId(value["scope"]["organizationId"]) ||
      !isCatalogId(value["scope"]["projectId"]) ||
      !isCatalogId(value["scope"]["mappingId"]) ||
      !positive(value["version"])
    )
      throw new MappingMetadataError("incompatible");
    const accepted = validateMappingMetadataInput(
      value["name"],
      value["description"],
    );
    if (
      !accepted.valid ||
      accepted.acceptedName !== value["name"] ||
      typeof receivedEtag !== "string"
    )
      throw new MappingMetadataError("incompatible");
    const scope = Object.freeze({
      organizationId: value["scope"]["organizationId"],
      projectId: value["scope"]["projectId"],
      mappingId: value["scope"]["mappingId"],
    });
    const expectedTag = `"metadata-v1:${scope.organizationId}:${scope.projectId}:${scope.mappingId}:${value["version"]}"`;
    if (receivedEtag !== expectedTag)
      throw new MappingMetadataError("incompatible");
    const metadata: CatalogMappingMetadata = Object.freeze({
      scope,
      name: accepted.acceptedName,
      description: accepted.description,
      version: value["version"],
    });
    return new MappingMetadataBaseline(
      mode,
      actorPrincipalId,
      metadata,
      receivedEtag,
    );
  }

  get scope(): CatalogMappingScope {
    return this.#metadata.scope;
  }
  get mode(): CatalogContext["mode"] {
    return this.#mode;
  }
  get actorPrincipalId(): string {
    return this.#actorPrincipalId;
  }
  get metadata(): CatalogMappingMetadata {
    return this.#metadata;
  }
  get etag(): string {
    return this.#etag;
  }
  toString(): string {
    return "MappingMetadataBaseline";
  }
  toJSON(): string {
    return "MappingMetadataBaseline";
  }
}

/** One explicit CAS attempt, without tokens, replacement tags or automatic recovery. */
export class MappingMetadataAttempt {
  readonly #baseline: MappingMetadataBaseline;
  readonly #acceptedName: string;
  readonly #description: string;
  readonly #body: string;

  private constructor(
    baseline: MappingMetadataBaseline,
    accepted: Extract<MappingMetadataInputValidation, { valid: true }>,
  ) {
    this.#baseline = baseline;
    this.#acceptedName = accepted.acceptedName;
    this.#description = accepted.description;
    this.#body = accepted.body;
    Object.freeze(this);
  }

  static create(
    baseline: MappingMetadataBaseline,
    input: CatalogMappingMetadataEdit,
  ): MappingMetadataAttempt {
    if (!(baseline instanceof MappingMetadataBaseline))
      throw new MappingMetadataError("invalidInput");
    const accepted = validateMappingMetadataInput(
      input?.name,
      input?.description,
    );
    if (!accepted.valid) throw new MappingMetadataError("invalidInput");
    return new MappingMetadataAttempt(baseline, accepted);
  }

  get baseline(): MappingMetadataBaseline {
    return this.#baseline;
  }
  get acceptedName(): string {
    return this.#acceptedName;
  }
  get description(): string {
    return this.#description;
  }
  get body(): string {
    return this.#body;
  }
  toString(): string {
    return "MappingMetadataAttempt";
  }
  toJSON(): string {
    return "MappingMetadataAttempt";
  }
}

type JsonObject = Record<string, unknown>;
function object(value: unknown): value is JsonObject {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}
function keys(value: JsonObject, expected: readonly string[]): boolean {
  return (
    Object.keys(value).length === expected.length &&
    expected.every((key) => Object.hasOwn(value, key))
  );
}
function positive(value: unknown): value is string {
  return (
    typeof value === "string" &&
    /^[1-9][0-9]{0,18}$/.test(value) &&
    (value.length < 19 || value <= "9223372036854775807")
  );
}
