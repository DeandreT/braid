import {
  HttpClient,
  HttpErrorResponse,
  HttpHeaders,
} from "@angular/common/http";
import { Injectable, inject } from "@angular/core";
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
  NativeMappingUploadError,
  NativeMappingUploadIntent,
  NativeMappingUploadOperation,
  captureNativeMappingUploadAdmission,
  nativeMappingUploadReceiptPath,
  readNativeMappingUploadResponse,
  type NativeMappingUploadAdmission,
  type NativeMappingUploadResponse,
} from "./native-mapping-upload-types";

@Injectable({ providedIn: "root" })
export class NativeMappingUploadApi {
  private readonly http = inject(HttpClient);

  /** One explicit original-byte send or resend. No receipt is searched for or adopted by key. */
  create(
    admission: NativeMappingUploadAdmission,
    intent: NativeMappingUploadIntent,
    previous: NativeMappingUploadOperation | null = null,
  ): Observable<NativeMappingUploadResponse> {
    try {
      captureNativeMappingUploadAdmission(
        admission,
        intent,
        previous ?? undefined,
      );
      const token = requestToken(admission);
      if (!(intent instanceof NativeMappingUploadIntent))
        throw new NativeMappingUploadError("invalidInput");
      if (previous) captureNativeMappingUploadAdmission(admission, previous);
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
            map((response) =>
              readNativeMappingUploadResponse(
                response.status,
                response.body,
                response.headers.get("Location"),
                intent,
                previous,
              ),
            ),
            catchError((error: unknown) =>
              mutationFailure(error, intent, previous),
            ),
          ),
      );
    } catch (error: unknown) {
      return throwError(() => localFailure(error));
    }
  }

  /** Only a previously learned exact own operation is readable; this call never claims work. */
  receipt(
    admission: NativeMappingUploadAdmission,
    operation: NativeMappingUploadOperation,
  ): Observable<NativeMappingUploadOperation> {
    try {
      captureNativeMappingUploadAdmission(admission, operation);
      const url = nativeMappingUploadReceiptPath(operation);
      return this.http.get<unknown>(url, { observe: "response" }).pipe(
        timeout(8000),
        map((response) => {
          if (response.status !== 200)
            throw new NativeMappingUploadError("incompatible", response.status);
          try {
            return NativeMappingUploadOperation.fromResponse(
              operation,
              response.body,
            );
          } catch {
            throw new NativeMappingUploadError("incompatible", response.status);
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

  /** One source-free resume, with no query, native media, idempotency or conditional headers. */
  resume(
    admission: NativeMappingUploadAdmission,
    operation: NativeMappingUploadOperation,
  ): Observable<NativeMappingUploadResponse> {
    try {
      captureNativeMappingUploadAdmission(admission, operation);
      const token = requestToken(admission),
        url = nativeMappingUploadReceiptPath(operation) + "/resume";
      return this.http
        .post<unknown>(url, null, {
          observe: "response",
          headers: new HttpHeaders({ "X-XSRF-TOKEN": token }),
        })
        .pipe(
          timeout(30000),
          map((response) =>
            readNativeMappingUploadResponse(
              response.status,
              response.body,
              response.headers.get("Location"),
              operation,
            ),
          ),
          catchError((error: unknown) => mutationFailure(error, operation)),
        );
    } catch (error: unknown) {
      return throwError(() => localFailure(error));
    }
  }
}

function requestToken(admission: NativeMappingUploadAdmission): string {
  const token: unknown = admission.context.requestToken;
  if (
    typeof token !== "string" ||
    token.length === 0 ||
    token.length > 4096 ||
    /[\0\r\n]/.test(token)
  )
    throw new NativeMappingUploadError("identityRejected");
  return token;
}
function mutationFailure(
  error: unknown,
  binding: NativeMappingUploadIntent | NativeMappingUploadOperation,
  previous: NativeMappingUploadOperation | null = null,
): Observable<NativeMappingUploadResponse> {
  if (
    error instanceof HttpErrorResponse &&
    error.status !== 0 &&
    object(error.error) &&
    Object.hasOwn(error.error, "outcome")
  ) {
    try {
      return of(
        readNativeMappingUploadResponse(
          error.status,
          error.error,
          error.headers.get("Location"),
          binding,
          previous,
        ),
      );
    } catch {
      return throwError(
        () => new NativeMappingUploadError("incompatible", error.status, true),
      );
    }
  }
  return throwError(() => httpFailure(error, true));
}
function httpFailure(
  error: unknown,
  writing: boolean,
): NativeMappingUploadError {
  if (error instanceof NativeMappingUploadError) return error;
  if (error instanceof TimeoutError)
    return new NativeMappingUploadError("timedOut", null, writing);
  if (!(error instanceof HttpErrorResponse))
    return new NativeMappingUploadError("unavailable", null, writing);
  const status = error.status;
  if (status === 0)
    return new NativeMappingUploadError("unavailable", status, writing);
  if (
    !object(error.error) ||
    Object.keys(error.error).length !== 1 ||
    !Object.hasOwn(error.error, "code")
  )
    return new NativeMappingUploadError("incompatible", status, writing);
  const code: unknown = error.error["code"];
  if (status === 400 && code === "invalidRequest")
    return new NativeMappingUploadError("invalidRequest", status);
  if (status === 403 && code === "identityRejected")
    return new NativeMappingUploadError("identityRejected", status);
  if (status === 404 && (code === "notFound" || code === "unavailable"))
    return new NativeMappingUploadError("notFound", status);
  if (writing && status === 413 && code === "bodyLimit")
    return new NativeMappingUploadError("bodyLimit", status);
  if (writing && status === 415 && code === "unsupportedMediaType")
    return new NativeMappingUploadError("unsupportedMediaType", status);
  if (writing && status === 503 && code === "ingestionAdmissionBusy")
    return new NativeMappingUploadError("ingestionAdmissionBusy", status);
  if (status === 500 && code === "persistenceFailure")
    return new NativeMappingUploadError("persistenceFailure", status, writing);
  if (status === 500 && code === "internalFailure")
    return new NativeMappingUploadError("internalFailure", status, writing);
  return new NativeMappingUploadError("incompatible", status, writing);
}
function localFailure(error: unknown): NativeMappingUploadError {
  return error instanceof NativeMappingUploadError
    ? error
    : new NativeMappingUploadError("invalidInput");
}
function object(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}
