import {
  HttpClient,
  HttpErrorResponse,
  HttpHeaders,
} from "@angular/common/http";
import { Injectable, inject } from "@angular/core";
import { BrowserSessionState } from "../../auth/browser-session-state";
import {
  TimeoutError,
  catchError,
  map,
  of,
  throwError,
  timeout,
  type Observable,
} from "rxjs";
import {
  NativeRevisionAppendError,
  NativeRevisionAppendIntent,
  NativeRevisionAppendOperation,
  captureNativeRevisionAppendAdmission,
  nativeRevisionAppendReceiptPath,
  readNativeRevisionAppendResponse,
  type NativeRevisionAppendAdmission,
  type NativeRevisionAppendResponse,
} from "./native-revision-append-types";

@Injectable({ providedIn: "root" })
export class NativeRevisionAppendApi {
  private readonly http = inject(HttpClient);
  private readonly sessions = inject(BrowserSessionState);

  /** One deliberate original-byte send/resend; no current-head check or lookup by key. */
  append(
    admission: NativeRevisionAppendAdmission,
    intent: NativeRevisionAppendIntent,
    previous: NativeRevisionAppendOperation | null = null,
  ): Observable<NativeRevisionAppendResponse> {
    try {
      this.requireCurrent(admission);
      captureNativeRevisionAppendAdmission(
        admission,
        intent,
        previous ?? undefined,
      );
      const token = requestToken(admission);
      if (!(intent instanceof NativeRevisionAppendIntent)) throw invalidInput();
      return intent.withRequest((url, key, body) =>
        this.http
          .post<unknown>(url, body, {
            observe: "response",
            headers: new HttpHeaders({
              "Content-Type": "application/octet-stream",
              "Idempotency-Key": key,
              "X-XSRF-TOKEN": token,
            }),
          })
          .pipe(
            timeout(30000),
            map((response) => {
              if (!this.sessions.accepts(admission.context))
                throw new NativeRevisionAppendError("unavailable", null, true);
              return readNativeRevisionAppendResponse(
                response.status,
                response.body,
                response.headers.get("Location"),
                intent,
                previous,
              );
            }),
            catchError((error: unknown) =>
              this.sessions.accepts(admission.context)
                ? mutationFailure(error, intent, previous)
                : throwError(
                    () =>
                      new NativeRevisionAppendError("unavailable", null, true),
                  ),
            ),
          ),
      );
    } catch (error: unknown) {
      return throwError(() => localFailure(error));
    }
  }

  /** One exact own receipt GET, requiring current upload authority but claiming no work. */
  receipt(
    admission: NativeRevisionAppendAdmission,
    operation: NativeRevisionAppendOperation,
  ): Observable<NativeRevisionAppendOperation> {
    try {
      this.requireCurrent(admission);
      captureNativeRevisionAppendAdmission(admission, operation);
      const url = nativeRevisionAppendReceiptPath(operation);
      return this.http.get<unknown>(url, { observe: "response" }).pipe(
        timeout(8000),
        map((response) => {
          if (!this.sessions.accepts(admission.context))
            throw new NativeRevisionAppendError("unavailable");
          if (
            response.status !== 200 ||
            response.headers.get("Location") !== null
          )
            throw new NativeRevisionAppendError(
              "incompatible",
              response.status,
            );
          try {
            return NativeRevisionAppendOperation.fromResponse(
              operation,
              response.body,
            );
          } catch {
            throw new NativeRevisionAppendError(
              "incompatible",
              response.status,
            );
          }
        }),
        catchError((error: unknown) =>
          throwError(() => httpFailure(error, false)),
        ),
      );
    } catch (error: unknown) {
      return throwError(() => localFailure(error));
    }
  }

  /** One bodyless original-operation resume with fresh token, never a changed head/source/key. */
  resume(
    admission: NativeRevisionAppendAdmission,
    operation: NativeRevisionAppendOperation,
  ): Observable<NativeRevisionAppendResponse> {
    try {
      this.requireCurrent(admission);
      captureNativeRevisionAppendAdmission(admission, operation);
      const token = requestToken(admission),
        url = nativeRevisionAppendReceiptPath(operation) + "/resume";
      return this.http
        .post<unknown>(url, null, {
          observe: "response",
          headers: new HttpHeaders({ "X-XSRF-TOKEN": token }),
        })
        .pipe(
          timeout(30000),
          map((response) => {
            if (!this.sessions.accepts(admission.context))
              throw new NativeRevisionAppendError("unavailable", null, true);
            return readNativeRevisionAppendResponse(
              response.status,
              response.body,
              response.headers.get("Location"),
              operation,
            );
          }),
          catchError((error: unknown) =>
            this.sessions.accepts(admission.context)
              ? mutationFailure(error, operation)
              : throwError(
                  () =>
                    new NativeRevisionAppendError("unavailable", null, true),
                ),
          ),
        );
    } catch (error: unknown) {
      return throwError(() => localFailure(error));
    }
  }
  private requireCurrent(admission: NativeRevisionAppendAdmission): void {
    if (!admission?.context || !this.sessions.canDispatch(admission.context))
      throw new NativeRevisionAppendError("identityRejected");
  }
}

function requestToken(admission: NativeRevisionAppendAdmission): string {
  const token: unknown = admission.context.requestToken;
  if (
    typeof token !== "string" ||
    token.length === 0 ||
    token.length > 4096 ||
    /[\0\r\n]/.test(token)
  )
    throw new NativeRevisionAppendError("identityRejected");
  return token;
}
function mutationFailure(
  error: unknown,
  binding: NativeRevisionAppendIntent | NativeRevisionAppendOperation,
  previous: NativeRevisionAppendOperation | null = null,
): Observable<NativeRevisionAppendResponse> {
  if (
    error instanceof HttpErrorResponse &&
    error.status !== 0 &&
    object(error.error) &&
    Object.hasOwn(error.error, "outcome")
  ) {
    try {
      return of(
        readNativeRevisionAppendResponse(
          error.status,
          error.error,
          error.headers.get("Location"),
          binding,
          previous,
        ),
      );
    } catch {
      return throwError(
        () => new NativeRevisionAppendError("incompatible", error.status, true),
      );
    }
  }
  return throwError(() => httpFailure(error, true));
}
function httpFailure(
  error: unknown,
  writing: boolean,
): NativeRevisionAppendError {
  if (error instanceof NativeRevisionAppendError) return error;
  if (error instanceof TimeoutError)
    return new NativeRevisionAppendError("timedOut", null, writing);
  if (!(error instanceof HttpErrorResponse))
    return new NativeRevisionAppendError("unavailable", null, writing);
  const status = error.status;
  if (status === 0)
    return new NativeRevisionAppendError("unavailable", status, writing);
  if (
    !object(error.error) ||
    Object.keys(error.error).length !== 1 ||
    !Object.hasOwn(error.error, "code")
  )
    return new NativeRevisionAppendError("incompatible", status, writing);
  const code: unknown = error.error["code"];
  if (status === 400 && code === "invalidRequest")
    return new NativeRevisionAppendError("invalidRequest", status);
  if ((status === 401 || status === 403) && code === "identityRejected")
    return new NativeRevisionAppendError("identityRejected", status);
  if (status === 404 && (code === "notFound" || code === "unavailable"))
    return new NativeRevisionAppendError("notFound", status);
  if (writing && status === 413 && code === "bodyLimit")
    return new NativeRevisionAppendError("bodyLimit", status);
  if (writing && status === 415 && code === "unsupportedMediaType")
    return new NativeRevisionAppendError("unsupportedMediaType", status);
  if (writing && status === 503 && code === "ingestionAdmissionBusy")
    return new NativeRevisionAppendError("ingestionAdmissionBusy", status);
  if (status === 500 && code === "persistenceFailure")
    return new NativeRevisionAppendError("persistenceFailure", status, writing);
  if (status === 500 && code === "internalFailure")
    return new NativeRevisionAppendError("internalFailure", status, writing);
  return new NativeRevisionAppendError("incompatible", status, writing);
}
function localFailure(error: unknown): NativeRevisionAppendError {
  return error instanceof NativeRevisionAppendError ? error : invalidInput();
}
function invalidInput(): NativeRevisionAppendError {
  return new NativeRevisionAppendError("invalidInput");
}
function object(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}
