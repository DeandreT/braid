import { HttpClient, HttpErrorResponse } from "@angular/common/http";
import { Injectable, inject } from "@angular/core";
import {
  catchError,
  defer,
  map,
  throwError,
  timeout,
  type Observable,
} from "rxjs";
import type { components } from "../api/generated";
import { BrowserSessionState } from "../auth/browser-session-state";
import { isHelpSectionId } from "./help-sections";

export type HelpSectionList = components["schemas"]["HelpSectionListV1"];
export type HelpSectionSummary = components["schemas"]["HelpSectionSummaryV1"];
export type HelpSection = components["schemas"]["HelpSectionV1"];
export type HelpBlock = components["schemas"]["HelpBlockV1"];
export type HelpInline = components["schemas"]["HelpInlineV1"];

export type HelpErrorKind = "signedOut" | "unavailable" | "notFound" | "failed";

export class HelpError extends Error {
  constructor(readonly kind: HelpErrorKind) {
    super(kind);
  }
}

/** Reads the guide embedded in the server. Responses are checked against the closed contract. */
@Injectable({ providedIn: "root" })
export class HelpApi {
  private readonly http = inject(HttpClient);
  private readonly sessions = inject(BrowserSessionState);

  sections(): Observable<HelpSectionList> {
    return this.get("/api/v1/help/sections", isSectionList);
  }

  section(sectionId: string): Observable<HelpSection> {
    return defer(() => {
      if (!isHelpSectionId(sectionId)) throw new HelpError("notFound");
      return this.get(
        `/api/v1/help/sections/${sectionId}`,
        (value): value is HelpSection =>
          isSection(value) && value.id === sectionId,
      );
    });
  }

  private get<T>(
    url: string,
    guard: (value: unknown) => value is T,
  ): Observable<T> {
    return defer(() => {
      const generation = this.sessions.generation();
      const sessionMode = this.sessions.state().kind === "authenticated";
      return this.http.get<unknown>(url).pipe(
        timeout(8000),
        map((value) => {
          if (!guard(value)) throw new HelpError("failed");
          return value;
        }),
        catchError((error: unknown) => {
          if (
            sessionMode &&
            generation === this.sessions.generation() &&
            error instanceof HttpErrorResponse &&
            (error.status === 401 || error.status === 403)
          )
            this.sessions.rejectProtectedRequest(generation);
          return throwError(() => helpFailure(error));
        }),
      );
    });
  }
}

function helpFailure(error: unknown): HelpError {
  if (error instanceof HelpError) return error;
  if (error instanceof HttpErrorResponse) {
    if (error.status === 401 || error.status === 403)
      return new HelpError("signedOut");
    const code = (error.error as { code?: unknown } | null)?.code;
    if (error.status === 404 && code === "unavailable")
      return new HelpError("unavailable");
    if (error.status === 404 || error.status === 400)
      return new HelpError("notFound");
  }
  return new HelpError("failed");
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function hasExactKeys(value: Record<string, unknown>, keys: string[]): boolean {
  const actual = Object.keys(value);
  return (
    actual.length === keys.length && keys.every((key) => actual.includes(key))
  );
}

// These bounds equal the server's guide limits (UserGuideMarkdown and UserGuide), so every guide
// the server accepts passes them: 8192 characters per text, 256 items per list, row or block,
// 512 blocks, 128-character anchors and 64 sections.
function isText(value: unknown): value is string {
  return typeof value === "string" && value.length > 0 && value.length <= 8192;
}

function isSummary(value: unknown): value is HelpSectionSummary {
  return (
    isRecord(value) &&
    hasExactKeys(value, ["id", "title", "summary"]) &&
    isHelpSectionId(value["id"]) &&
    isText(value["title"]) &&
    isText(value["summary"])
  );
}

function isSectionList(value: unknown): value is HelpSectionList {
  if (!isRecord(value) || !hasExactKeys(value, ["sections"])) return false;
  const sections = value["sections"];
  return (
    Array.isArray(sections) &&
    sections.length > 0 &&
    sections.length <= 64 &&
    sections.every(isSummary) &&
    new Set(sections.map((section: HelpSectionSummary) => section.id)).size ===
      sections.length
  );
}

function isSection(value: unknown): value is HelpSection {
  if (
    !isRecord(value) ||
    !hasExactKeys(value, ["id", "title", "summary", "blocks"]) ||
    !isHelpSectionId(value["id"]) ||
    !isText(value["title"]) ||
    !isText(value["summary"])
  )
    return false;
  const blocks = value["blocks"];
  return (
    Array.isArray(blocks) &&
    blocks.length > 0 &&
    blocks.length <= 512 &&
    blocks.every(isBlock)
  );
}

function isBlock(value: unknown): value is HelpBlock {
  if (!isRecord(value)) return false;
  switch (value["kind"]) {
    case "heading":
      return (
        hasExactKeys(value, ["kind", "level", "anchor", "text"]) &&
        (value["level"] === 2 || value["level"] === 3) &&
        isAnchor(value["anchor"]) &&
        isText(value["text"])
      );
    case "paragraph":
      return (
        hasExactKeys(value, ["kind", "content"]) && isContent(value["content"])
      );
    case "list":
      return (
        hasExactKeys(value, ["kind", "ordered", "items"]) &&
        typeof value["ordered"] === "boolean" &&
        isList(value["items"], (item) => isCell(item))
      );
    case "table": {
      if (!hasExactKeys(value, ["kind", "header", "rows"])) return false;
      const header = value["header"];
      if (!isList(header, isCell)) return false;
      return isList(
        value["rows"],
        (row) =>
          isRecord(row) &&
          hasExactKeys(row, ["cells"]) &&
          isList(row["cells"], isCell) &&
          (row["cells"] as unknown[]).length === header.length,
      );
    }
    case "codeBlock":
      return hasExactKeys(value, ["kind", "text"]) && isText(value["text"]);
    default:
      return false;
  }
}

function isList(
  value: unknown,
  guard: (item: unknown) => boolean,
): value is unknown[] {
  return (
    Array.isArray(value) &&
    value.length > 0 &&
    value.length <= 256 &&
    value.every(guard)
  );
}

/** List items and table cells share the same shape. */
function isCell(value: unknown): boolean {
  return (
    isRecord(value) &&
    hasExactKeys(value, ["content"]) &&
    isContent(value["content"])
  );
}

function isContent(value: unknown): value is HelpInline[] {
  return isList(value, isInline);
}

function isInline(value: unknown): value is HelpInline {
  if (!isRecord(value)) return false;
  switch (value["kind"]) {
    case "text":
    case "strong":
    case "emphasis":
    case "code":
      return hasExactKeys(value, ["kind", "text"]) && isText(value["text"]);
    case "link":
      return (
        hasExactKeys(value, ["kind", "text", "sectionId", "anchor"]) &&
        isText(value["text"]) &&
        isHelpSectionId(value["sectionId"]) &&
        (value["anchor"] === null || isAnchor(value["anchor"]))
      );
    default:
      return false;
  }
}

function isAnchor(value: unknown): value is string {
  return (
    typeof value === "string" &&
    value.length <= 128 &&
    /^[a-z0-9]+(?:-[a-z0-9]+)*$/.test(value)
  );
}
