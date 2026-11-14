import { HttpErrorResponse } from "@angular/common/http";
import { DOCUMENT } from "@angular/common";
import {
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from "@angular/core";
import { takeUntilDestroyed } from "@angular/core/rxjs-interop";
import { FormsModule } from "@angular/forms";
import { ActivatedRoute, RouterLink } from "@angular/router";
import {
  type Observable,
  EMPTY,
  finalize,
  forkJoin,
  of,
  switchMap,
  takeUntil,
} from "rxjs";
import { CatalogContextStore } from "../catalog-context";
import { UnsentWork } from "../../workbench/unsent-work";
import {
  catalogOwner,
  isCatalogId,
  type CatalogContext,
  type CatalogMappingScope,
} from "../catalog-types";
import {
  OperationsApi,
  type OperationBody,
  type OperationsPage,
  type OriginalOperation,
} from "../operations/operations-api";
import {
  SandboxApi,
  type SandboxMessage,
  type SandboxSend,
  type SandboxState,
  type SandboxTarget,
} from "./sandbox-api";

type Release = Extract<OperationsPage["items"][number], { kind: "release" }>;
interface Pending {
  kind: "deploy" | "send" | "operation";
  id: string;
  owner: string;
  run: (context: CatalogContext) => Observable<unknown>;
}

/** Text when the bytes are UTF-8, otherwise a byte count. */
export function decodeContent(base64: string | null | undefined): string {
  if (!base64) return "";
  const bytes = Uint8Array.from(atob(base64), (c) => c.charCodeAt(0));
  try {
    return new TextDecoder("utf-8", { fatal: true }).decode(bytes);
  } catch {
    return `(${bytes.length} bytes of binary content)`;
  }
}

export function encodeContent(bytes: Uint8Array): string {
  let text = "";
  for (let index = 0; index < bytes.length; index += 0x8000)
    text += String.fromCharCode(...bytes.subarray(index, index + 0x8000));
  return btoa(text);
}

@Component({
  selector: "weft-sandbox-page",
  imports: [FormsModule, RouterLink],
  templateUrl: "./sandbox-page.component.html",
  styleUrls: ["../catalog.css", "./sandbox-page.component.css"],
})
export class SandboxPageComponent {
  readonly context = inject(CatalogContextStore);
  private readonly api = inject(SandboxApi);
  private readonly operations = inject(OperationsApi);
  private readonly route = inject(ActivatedRoute);
  private readonly destroy = inject(DestroyRef);
  private readonly document = inject(DOCUMENT);
  private generation = 0;
  private retainedOwner: string | null = null;
  private readonly admitted = signal<CatalogContext | null>(null);
  readonly presented = computed(
    () =>
      this.context.isCurrent(this.admitted()) &&
      this.context.canPresent(this.admitted()),
  );
  readonly state = signal<SandboxState | null>(null);
  readonly releases = signal<Release[]>([]);
  readonly history = signal<SandboxMessage[]>([]);
  readonly detail = signal<SandboxMessage | null>(null);
  readonly comparison = signal<[SandboxMessage, SandboxMessage] | null>(null);
  readonly busy = signal(false);
  readonly message = signal("");
  readonly unavailable = signal(false);
  private readonly retainedPending = signal<Pending | null>(null);
  readonly pending = computed(() => {
    const context = this.context.context();
    const pending = this.retainedPending();
    return context &&
      this.context.canPresent(context) &&
      pending?.owner === catalogOwner(context)
      ? pending
      : null;
  });
  readonly decode = decodeContent;
  target = "";
  build = "";
  source = "case";
  sample = "";
  primary = "";
  private uploadedPrimary: { text: string; bytes: Uint8Array } | null = null;
  named: { name: string; text: string }[] = [];
  rollbackSource = "";
  readonly compare = signal<string[]>([]);
  private scope: CatalogMappingScope | null = null;

  constructor() {
    inject(UnsentWork).track(
      () => this.retainedPending() !== null,
      this.destroy,
    );
    this.context.invalidated.pipe(takeUntilDestroyed()).subscribe(() => {
      this.generation++;
      this.reset();
    });
    effect(() => {
      const context = this.context.context();
      if (context) untracked(() => this.bindOwner(context));
    });
    this.route.paramMap.pipe(takeUntilDestroyed()).subscribe((params) => {
      const organizationId = params.get("organizationId") ?? "";
      const projectId = params.get("projectId") ?? "";
      const mappingId = params.get("mappingId") ?? "";
      this.scope = [organizationId, projectId, mappingId].every(isCatalogId)
        ? { organizationId, projectId, mappingId }
        : null;
      this.generation++;
      this.reset();
      this.clearDrafts();
      this.load();
    });
  }

  current(): SandboxTarget | null {
    return (
      this.state()?.targets.find((value) => value.targetId === this.target) ??
      null
    );
  }

  selectTarget(): void {
    this.detail.set(null);
    this.comparison.set(null);
    this.compare.set([]);
    this.rollbackSource = "";
    this.load();
  }

  load(keepMessage = false): void {
    if (!keepMessage) this.message.set("");
    this.run(
      (context, scope) => {
        const generation = this.generation;
        return this.api.state(context, scope).pipe(
          switchMap((state) => {
            if (
              generation !== this.generation ||
              !this.context.isCurrent(context)
            )
              return EMPTY;
            this.state.set(state);
            this.unavailable.set(false);
            if (!state.targets.some((value) => value.targetId === this.target))
              this.target = state.targets[0]?.targetId ?? "";
            if (!state.builds.some((value) => value.buildId === this.build))
              this.build = state.builds[0]?.buildId ?? "";
            if (!state.samples.some((value) => value.reference === this.sample))
              this.sample = state.samples[0]?.reference ?? "";
            if (!this.target) return of(null);
            return forkJoin([
              this.api.messages(context, scope, this.target),
              this.operations.read(
                context,
                scope,
                this.target,
                "releases",
                0,
                new URLSearchParams(),
              ),
            ]);
          }),
        );
      },
      (value) => {
        if (!value) {
          this.history.set([]);
          this.releases.set([]);
          return;
        }
        const [page, releases] = value as [
          { items: SandboxMessage[] },
          OperationsPage,
        ];
        this.history.set(page.items);
        this.releases.set(
          releases.items.filter(
            (item): item is Release =>
              item.kind === "release" && item.phase === "active",
          ),
        );
      },
      (error) => {
        if (error instanceof HttpErrorResponse && error.status === 404) {
          this.unavailable.set(true);
          return "";
        }
        return "The sandbox is unavailable or your access could not be confirmed.";
      },
    );
  }

  deploy(): void {
    if (!isCatalogId(this.build)) return;
    const build = this.build;
    this.start("deploy", (context, scope, id) =>
      this.api.deploy(context, scope, id, build),
    );
  }

  sendSample(): void {
    let body: SandboxSend;
    if (this.source === "inline") {
      if (!this.primary && !this.uploadedPrimary) {
        this.message.set("Enter or upload a primary input first.");
        return;
      }
      const text = (value: string) =>
        encodeContent(new TextEncoder().encode(value));
      body = {
        source: "inline",
        reference: null,
        replayOf: null,
        parameters: [],
        inputs: [
          {
            name: null,
            mediaType: null,
            sha256: null,
            content:
              this.uploadedPrimary?.text === this.primary
                ? encodeContent(this.uploadedPrimary.bytes)
                : text(this.primary),
          },
          ...this.named
            .filter((value) => value.name)
            .map((value) => ({
              name: value.name,
              mediaType: null,
              sha256: null,
              content: text(value.text),
            })),
        ],
      };
    } else {
      if (!this.sample) {
        this.message.set("Choose a saved case first.");
        return;
      }
      body = {
        source: "case",
        reference: this.sample,
        inputs: null,
        parameters: null,
        replayOf: null,
      };
    }
    this.sendBody(body);
  }

  replay(original: SandboxMessage): void {
    this.sendBody({
      source: "replay",
      replayOf: original.messageId,
      reference: null,
      inputs: null,
      parameters: null,
    });
  }

  operate(action: "rollback" | "disable" | "enable"): void {
    const target = this.current();
    const source = this.releases().find(
      (value) => value.release_id === this.rollbackSource,
    );
    if (!target || (action === "rollback" && !source)) return;
    if (!(
      this.document.defaultView?.confirm(
        `${action === "rollback" ? "Roll back" : action === "disable" ? "Disable" : "Enable"} the sandbox target? This changes the local release pointer.`,
      ) ?? false
    ))
      return;
    const body: OperationBody = {
      action,
      environmentId: target.environmentId,
      environmentVersion: target.environmentVersion,
      qualificationId: action === "rollback" ? source!.qualification_id : null,
      sourceReleaseId: action === "rollback" ? source!.release_id : null,
    };
    this.start("operation", (context, scope, id) => {
      const original: OriginalOperation = {
        id,
        target: target.targetId,
        version: target.version,
        owner: catalogOwner(context),
        body: JSON.stringify(body),
      };
      return this.operations.apply(context, scope, original);
    });
  }

  open(message: SandboxMessage): void {
    this.run(
      (context, scope) =>
        this.api.message(context, scope, this.target, message.messageId),
      (value) => this.detail.set(value as SandboxMessage),
    );
  }

  toggleCompare(id: string): void {
    this.compare.update((ids) =>
      ids.includes(id)
        ? ids.filter((value) => value !== id)
        : ids.length < 2
          ? [...ids, id]
          : ids,
    );
  }

  runComparison(): void {
    const [left, right] = this.compare();
    if (!left || !right) return;
    this.run(
      (context, scope) =>
        forkJoin([
          this.api.message(context, scope, this.target, left),
          this.api.message(context, scope, this.target, right),
        ]),
      (value) => this.comparison.set(value as [SandboxMessage, SandboxMessage]),
    );
  }

  /** Every output name of both messages, primary first, with whether the bytes match. */
  differences(pair: [SandboxMessage, SandboxMessage]) {
    const names = new Set(
      [...(pair[0].outputs ?? []), ...(pair[1].outputs ?? [])].map(
        (value) => value.name ?? "",
      ),
    );
    return [...names].sort().map((name) => {
      const find = (message: SandboxMessage) =>
        message.outputs?.find((value) => (value.name ?? "") === name);
      const [left, right] = [find(pair[0]), find(pair[1])];
      return {
        name: name || "Primary output",
        same: !!left && !!right && left.sha256 === right.sha256,
        left: decodeContent(left?.content),
        right: decodeContent(right?.content),
      };
    });
  }

  async upload(event: Event): Promise<void> {
    const generation = this.generation;
    const owner = this.admitted();
    if (!owner || !this.presented()) return;
    const file = (event.target as HTMLInputElement).files?.[0];
    if (!file) return;
    if (file.size > 1024 * 1024) {
      this.message.set("Inputs are limited to 1 MiB.");
      return;
    }
    const bytes = new Uint8Array(await file.arrayBuffer());
    if (
      generation !== this.generation ||
      !this.context.isCurrent(owner) ||
      !this.context.canPresent(owner)
    )
      return;
    this.primary = new TextDecoder().decode(bytes);
    this.uploadedPrimary = { text: this.primary, bytes };
  }

  addNamed(): void {
    this.named = [...this.named, { name: "", text: "" }];
  }

  recover(): void {
    const pending = this.pending();
    if (pending) this.dispatch(pending);
  }

  requestLeave() {
    return of(
      this.retainedPending() === null && !this.primary
        ? true
        : (this.document.defaultView?.confirm(
            "Leave the sandbox? Unsent input and this tab's recovery request will be lost. A sent request may already have committed.",
          ) ?? false),
    );
  }

  private sendBody(body: SandboxSend): void {
    const target = this.target;
    if (!isCatalogId(target)) return;
    this.start("send", (context, scope, id) =>
      this.api.send(context, scope, target, id, body),
    );
  }

  // A mutation keeps its original ID until a receipt arrives, so a retry replays instead of repeating it.
  private start(
    kind: Pending["kind"],
    request: (
      context: CatalogContext,
      scope: CatalogMappingScope,
      id: string,
    ) => Observable<unknown>,
  ): void {
    const scope = this.scope;
    const owner = this.admitted();
    if (
      !scope ||
      !owner ||
      !this.presented() ||
      this.busy() ||
      this.retainedPending()
    )
      return;
    const id = crypto.randomUUID();
    this.dispatch({
      kind,
      id,
      owner: catalogOwner(owner),
      run: (context) => request(context, scope, id),
    });
  }

  private dispatch(pending: Pending): void {
    if (this.busy()) return;
    this.retainedPending.set(pending);
    this.message.set("");
    this.run(
      (context) => {
        if (catalogOwner(context) !== pending.owner)
          throw new Error("Different workspace owner");
        return pending.run(context);
      },
      (value) => {
        this.retainedPending.set(null);
        const result = value as {
          outcome?: string;
          status?: string;
          messageId?: string;
          target?: SandboxTarget | null;
        };
        if (
          pending.kind === "deploy" &&
          result.target &&
          ["activated", "replayed"].includes(result.outcome ?? "")
        ) {
          this.target = result.target.targetId;
          this.detail.set(null);
          this.comparison.set(null);
          this.compare.set([]);
          this.rollbackSource = "";
        }
        if (pending.kind === "send") {
          this.detail.set(value as SandboxMessage);
          if (this.source === "inline") {
            this.primary = "";
            this.uploadedPrimary = null;
          }
        }
        this.message.set(
          pending.kind === "send"
            ? `Message ${result.status ?? "sent"}${result.status === "failed" ? ` (${(value as SandboxMessage).outcome})` : ""}.`
            : pending.kind === "deploy"
              ? result.outcome === "activated" || result.outcome === "replayed"
                ? "Deployed to the local sandbox."
                : `Deployment ${result.outcome}.`
              : "Operation recorded.",
        );
        // After this request settles and releases the busy state.
        const generation = this.generation;
        queueMicrotask(() => {
          if (generation === this.generation && this.presented())
            this.load(true);
        });
      },
      (error) => {
        if (
          error instanceof HttpErrorResponse &&
          [400, 404, 409, 412, 413, 415, 428].includes(error.status)
        ) {
          this.retainedPending.set(null);
          return error.status === 412
            ? "The target changed. Refresh and review before trying again."
            : "The request was refused.";
        }
        return "The result is uncertain. Retry the original request; do not start a new one.";
      },
    );
  }

  private run<T>(
    request: (
      context: CatalogContext,
      scope: CatalogMappingScope,
    ) => Observable<T>,
    next: (value: T) => void,
    failure: (error: unknown) => string = () =>
      "The sandbox is unavailable or your access could not be confirmed.",
  ): void {
    const scope = this.scope;
    if (!scope || this.busy()) return;
    const owner = this.retainedOwner;
    const generation = ++this.generation;
    this.busy.set(true);
    this.context
      .refresh()
      .pipe(
        switchMap((context) => {
          if (generation !== this.generation) return EMPTY;
          this.bindOwner(context);
          // A selection/action captured under A must never be dispatched as B.
          if (
            generation !== this.generation ||
            (owner !== null && owner !== catalogOwner(context))
          )
            return EMPTY;
          this.admitted.set(context);
          return request(context, scope);
        }),
        finalize(() => {
          if (generation === this.generation) this.busy.set(false);
        }),
        takeUntil(this.context.invalidated),
        takeUntilDestroyed(this.destroy),
      )
      .subscribe({
        next: (value) => {
          if (generation === this.generation && this.presented()) next(value);
        },
        error: (error) => {
          if (
            generation === this.generation &&
            (!this.admitted() || this.presented())
          )
            this.message.set(failure(error));
        },
      });
  }

  private bindOwner(context: CatalogContext): void {
    const owner = catalogOwner(context);
    if (this.retainedOwner !== null && this.retainedOwner !== owner) {
      ++this.generation;
      this.reset();
      this.clearDrafts();
    }
    this.retainedOwner = owner;
  }

  private clearDrafts(): void {
    this.retainedPending.set(null);
    this.primary = "";
    this.uploadedPrimary = null;
    this.named = [];
    this.source = "case";
    this.target = "";
    this.build = "";
    this.sample = "";
    this.rollbackSource = "";
  }

  private reset(): void {
    this.state.set(null);
    this.history.set([]);
    this.releases.set([]);
    this.detail.set(null);
    this.comparison.set(null);
    this.compare.set([]);
    this.message.set("");
    this.unavailable.set(false);
    this.busy.set(false);
    this.admitted.set(null);
  }
}
