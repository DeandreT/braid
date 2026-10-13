import { HttpClient, HttpErrorResponse } from "@angular/common/http";
import { Injectable, inject } from "@angular/core";
import { catchError, map, of, timeout } from "rxjs";
import type { components } from "../api/generated";

export type ExampleDescriptor =
  components["schemas"]["NativeExampleDescriptorV1"];
export type ExampleResult = components["schemas"]["NativeExampleResultV1"];
export type OperationStatus = components["schemas"]["OperationStatusV1"];
export type OperationProvenance =
  components["schemas"]["OperationProvenanceV1"];
export type ValidationReport = components["schemas"]["ValidationReportV1"];
export type ManifestState =
  | { kind: "loading" }
  | { kind: "loaded"; examples: ExampleDescriptor[] }
  | { kind: "unavailable" };
export type ExampleState =
  | { kind: "idle" }
  | { kind: "loading" }
  | { kind: "ready"; result: ExampleResult }
  | { kind: "missing" }
  | { kind: "unavailable" };

@Injectable({ providedIn: "root" })
export class MappingExamples {
  private readonly http = inject(HttpClient);

  list() {
    return this.http.get<ExampleDescriptor[]>("/api/v1/examples/mappings").pipe(
      timeout(8000),
      map((examples): ManifestState => {
        if (
          !Array.isArray(examples) ||
          examples.some(
            (example) =>
              typeof example?.id !== "string" ||
              typeof example?.title !== "string",
          )
        )
          throw new Error("Invalid example list.");
        return { kind: "loaded", examples };
      }),
      catchError(() => of<ManifestState>({ kind: "unavailable" })),
    );
  }

  read(exampleId: string) {
    return this.http
      .get<ExampleResult>(
        `/api/v1/examples/mappings/${encodeURIComponent(exampleId)}`,
      )
      .pipe(
        timeout(25000),
        map((result): ExampleState => {
          if (
            result?.example?.id !== exampleId ||
            !result.projection ||
            !result.validation
          )
            throw new Error("Invalid example response.");
          return { kind: "ready", result };
        }),
        catchError((error: unknown) =>
          of<ExampleState>({
            kind:
              error instanceof HttpErrorResponse && error.status === 404
                ? "missing"
                : "unavailable",
          }),
        ),
      );
  }
}

export function operationMessage(status: OperationStatus): string {
  return {
    succeeded: "Completed",
    invalidMapping: "The native mapping is invalid",
    unsupportedArtifact: "This mapping format is unsupported",
    resourceLimit: "This mapping exceeds the explanation limits",
    protocolFailure: "The mapping service returned an incompatible response",
    backendUnavailable: "The mapping engine is unavailable",
    backendFailure: "The mapping engine could not finish",
    timedOut: "The mapping operation timed out",
    canceled: "The mapping operation was canceled",
    isolationFailure: "The mapping worker could not be isolated safely",
  }[status];
}

export function validationMessage(report: ValidationReport): string {
  if (report.status === "succeeded" || report.status === "invalidMapping") {
    if (report.isValid === true) return "Native mapping is valid";
    if (report.isValid === false) return "Native mapping has validation issues";
  }
  if (report.status !== "succeeded") return operationMessage(report.status);
  return "Validation result is unknown";
}
