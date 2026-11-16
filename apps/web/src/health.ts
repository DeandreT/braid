import { HttpClient } from "@angular/common/http";
import { Injectable, inject } from "@angular/core";
import { catchError, map, of, timeout } from "rxjs";

export type HealthState = { kind: "checking" | "connected" | "unavailable" };

@Injectable({ providedIn: "root" })
export class WorkspaceConnection {
  private readonly http = inject(HttpClient);

  check() {
    return this.http.get<unknown>("/api/v1/health").pipe(
      timeout(5000),
      map((body): HealthState => {
        if (
          typeof body === "object" &&
          body !== null &&
          "status" in body &&
          body.status === "ok" &&
          "service" in body &&
          body.service === "braid-api" &&
          "apiVersion" in body &&
          body.apiVersion === "v1"
        )
          return { kind: "connected" };
        return { kind: "unavailable" };
      }),
      catchError(() => of<HealthState>({ kind: "unavailable" })),
    );
  }
}
