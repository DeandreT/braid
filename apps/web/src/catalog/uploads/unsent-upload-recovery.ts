import { Injectable } from "@angular/core";
import type {
  CatalogMappingScope,
  CatalogProjectScope,
} from "../catalog-types";
import { RetainedViewWork } from "../retained-view-work";
import type {
  NativeMappingUploadInput,
  NativeMappingUploadSource,
} from "./native-mapping-upload-types";
import type { NativeIngestionSource } from "./native-ingestion-leaves";
import type { NativeRevisionAppendHead } from "./native-revision-append-types";

interface UnsentFile<Source> {
  readonly source: Source | null;
  /** Retained only while an aborted capture needs to be restarted after fresh admission. */
  readonly file: File | null;
  readonly summary: {
    readonly name: string;
    readonly byteCount: number;
  } | null;
}

export interface MappingUploadDraft extends UnsentFile<NativeMappingUploadSource> {
  readonly input: NativeMappingUploadInput;
}

export interface RevisionAppendDraft extends UnsentFile<NativeIngestionSource> {
  readonly filename: string;
  readonly reviewed: NativeRevisionAppendHead | null;
  readonly reviewRequired: boolean;
}

/** Provided by the upload page. Sent requests remain in the existing tab-memory protocol. */
@Injectable()
export class MappingUploadDraftRecovery extends RetainedViewWork<
  MappingUploadDraft,
  CatalogProjectScope
> {}

/** Provided by the append page; never application-wide or persisted. */
@Injectable()
export class RevisionAppendDraftRecovery extends RetainedViewWork<
  RevisionAppendDraft,
  CatalogMappingScope
> {}
