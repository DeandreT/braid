// PURE REQUIREMENT MODEL ONLY. No browser/CDP transport invokes this policy.
// Passing its source oracles cannot establish interception or network coverage.

const requireRule: (condition: unknown) => asserts condition = (condition) => {
  if (!condition) throw new Error("Owned redirect policy rejected.");
};
export type RedirectScope = Readonly<{
  context: object;
  target: object;
  frame: object;
}>;
export type RedirectRequest = Readonly<{
  scope: RedirectScope;
  url: string;
  method: string;
  bodyBytes: number;
  authorization: boolean;
  contentType: boolean;
  appActionHeaders: boolean;
  cookie: boolean;
  referer: boolean;
  resource: string;
  redirectedFrom: string | null;
}>;
export type RedirectHeader = Readonly<{ name: string; value: string }>;
type Phase =
  | "native"
  | "issuer-response"
  | "callback"
  | "callback-response"
  | "return"
  | "done";

// This models only the finite native -> actual issuer 303 -> exact callback ->
// actual callback 303 '/' -> return chain. A future source-proved transport must
// additionally own ALL requests/targets/contexts, not just these edge predicates.
export class RedirectChainPolicy {
  private phase: Phase = "native";
  private callback: string | null = null;
  private callbackConsumed = false;
  private returnConsumed = false;
  constructor(
    private readonly origin: string,
    private readonly nativeUrl: string,
    private readonly scope: RedirectScope,
  ) {
    const app = new URL(origin);
    requireRule(
      app.protocol === "https:" &&
        app.hostname === "127.0.0.1" &&
        app.port !== "" &&
        app.origin === origin &&
        app.pathname === "/" &&
        app.search === "" &&
        app.hash === "" &&
        app.username === "" &&
        app.password === "",
    );
    const native = new URL(nativeUrl);
    requireRule(
      nativeUrl.length > 0 &&
        nativeUrl.length <= 16384 &&
        nativeUrl.startsWith("https://login.microsoftonline.com/") &&
        native.origin === "https://login.microsoftonline.com" &&
        native.username === "" &&
        native.password === "" &&
        native.hash === "" &&
        /^\/[0-9a-f-]{36}\/oauth2\/v2\.0\/authorize$/.test(native.pathname),
    );
  }
  private request(request: RedirectRequest): void {
    requireRule(
      request.scope.context === this.scope.context &&
        request.scope.target === this.scope.target &&
        request.scope.frame === this.scope.frame &&
        request.resource === "Document" &&
        request.method === "GET" &&
        request.bodyBytes === 0 &&
        request.authorization === false &&
        request.contentType === false &&
        request.appActionHeaders === false,
    );
  }
  private location(headers: readonly RedirectHeader[]): string {
    requireRule(headers.length > 0 && headers.length <= 128);
    requireRule(
      headers.every(
        (header) =>
          header.name.length > 0 &&
          header.name.length <= 256 &&
          header.value.length <= 16384 &&
          !/[\r\n\0]/.test(header.name + header.value) &&
          header.name.toLowerCase() !== "refresh",
      ),
    );
    const values = headers.filter(
      (header) => header.name.toLowerCase() === "location",
    );
    requireRule(values.length === 1 && values[0]!.value.length > 0);
    return values[0]!.value;
  }
  native(request: RedirectRequest): void {
    this.request(request);
    requireRule(
      this.phase === "native" &&
        request.url === this.nativeUrl &&
        request.redirectedFrom === null &&
        !request.cookie &&
        !request.referer,
    );
    this.phase = "issuer-response";
  }
  issuer303(status: number, headers: readonly RedirectHeader[]): string {
    requireRule(this.phase === "issuer-response" && status === 303);
    const value = this.location(headers),
      callback = new URL(value);
    requireRule(
      value.length <= 16384 &&
        callback.href === value &&
        callback.origin === this.origin &&
        callback.pathname === "/api/auth/callback/entra" &&
        callback.username === "" &&
        callback.password === "" &&
        callback.hash === "" &&
        [...callback.searchParams.keys()].length === 2 &&
        callback.searchParams.getAll("code").length === 1 &&
        callback.searchParams.getAll("state").length === 1 &&
        callback.searchParams.get("code")!.length > 0 &&
        callback.searchParams.get("state")!.length > 0,
    );
    this.callback = value;
    this.phase = "callback";
    return value;
  }
  callbackRequest(request: RedirectRequest): void {
    this.request(request);
    requireRule(
      this.phase === "callback" &&
        !this.callbackConsumed &&
        request.url === this.callback &&
        request.redirectedFrom === this.nativeUrl &&
        !request.referer,
    );
    // Synchronous consumption must precede any future transport continuation.
    this.callbackConsumed = true;
    this.phase = "callback-response";
    // Cookie VALUES/attributes are deliberately not fabricated or validated here.
    // Only actual browser TLS/cookie enforcement and the real framework establish them.
  }
  callback303(status: number, headers: readonly RedirectHeader[]): string {
    requireRule(
      this.phase === "callback-response" &&
        this.callbackConsumed &&
        status === 303 &&
        this.location(headers) === "/",
    );
    this.phase = "return";
    return this.origin + "/";
  }
  returnRequest(request: RedirectRequest): void {
    this.request(request);
    requireRule(
      this.phase === "return" &&
        !this.returnConsumed &&
        request.url === this.origin + "/" &&
        request.redirectedFrom === this.callback &&
        !request.referer,
    );
    this.returnConsumed = true;
    this.phase = "done";
  }
  facts(): Readonly<{
    callbackConsumed: boolean;
    returnConsumed: boolean;
    complete: boolean;
  }> {
    return {
      callbackConsumed: this.callbackConsumed,
      returnConsumed: this.returnConsumed,
      complete: this.phase === "done",
    };
  }
}
