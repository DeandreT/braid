import { DOCUMENT } from "@angular/common";
import {
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from "@angular/core";
import { firstValueFrom, fromEvent, takeUntil } from "rxjs";
import { CatalogContextStore } from "../catalog/catalog-context";
import { CatalogApi } from "../catalog/catalog-api";
import type {
  CatalogContext,
  CatalogRevisionScope,
} from "../catalog/catalog-types";
import { ChatApi } from "./chat-api";
import {
  ChatError,
  reconcileTurn,
  terminal,
  type ChatAvailability,
  type ChatCitation,
  type ChatConversation,
  type ChatCreate,
  type ChatEvent,
  type ChatTurn,
} from "./chat-types";

@Component({
  selector: "weft-revision-chat",
  templateUrl: "./revision-chat.component.html",
  styleUrl: "./revision-chat.component.css",
})
export class RevisionChatComponent {
  readonly terminal = terminal;
  readonly scope = input.required<CatalogRevisionScope>();
  readonly context = input.required<CatalogContext>();
  readonly reportsReady = input(false);
  readonly citation = output<{
    conversation: ChatConversation;
    citation: ChatCitation;
  }>();
  private readonly api = inject(ChatApi);
  private readonly contexts = inject(CatalogContextStore);
  private readonly catalog = inject(CatalogApi);
  private readonly element = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);
  readonly availability = signal<ChatAvailability | null>(null);
  readonly conversations = signal<ChatConversation[]>([]);
  readonly current = signal<ChatConversation | null>(null);
  readonly turns = signal<ChatTurn[]>([]);
  readonly selected = signal<ChatTurn | null>(null);
  readonly events = signal<ChatEvent[]>([]);
  readonly draft = signal("");
  readonly profile = signal("");
  readonly busy = signal(false);
  readonly streaming = signal(false);
  readonly disconnected = signal(false);
  readonly hasMore = signal(false);
  readonly hasEarlier = signal(false);
  readonly problem = signal("");
  readonly answer = computed(() =>
    this.events()
      .filter(
        (e): e is Extract<ChatEvent, { type: "text.delta" }> =>
          e.type === "text.delta",
      )
      .map((e) => e.text)
      .join(""),
  );
  readonly citations = computed(() =>
    this.events().filter((e): e is ChatCitation => e.type === "citation"),
  );
  readonly tools = computed(() =>
    this.events().filter(
      (
        e,
      ): e is Extract<ChatEvent, { type: "tool.started" | "tool.completed" }> =>
        e.type === "tool.started" || e.type === "tool.completed",
    ),
  );
  readonly usage = computed(() => {
    const rounds = new Map<string, Extract<ChatEvent, { type: "usage" }>>();
    for (const e of this.events())
      if (e.type === "usage") rounds.set(e.roundId, e);
    return [...rounds.values()];
  });
  readonly status = computed(() => {
    const last = this.events().at(-1);
    if (last?.type === "turn.failed")
      return last.reason === "interrupted"
        ? "Answer interrupted. It will not be sent again automatically."
        : `Answer failed: ${last.reason}${last.providerFailure ? ` (${last.providerFailure})` : ""}.`;
    if (last?.type === "turn.cancelled") return "Answer cancelled.";
    if (last?.type === "turn.completed") return "Answer complete.";
    if (this.disconnected())
      return "Connection interrupted. Reconnect to read the remaining answer; this does not send another question.";
    if (this.streaming()) return "Reading answer…";
    return "";
  });
  readonly canSend = computed(
    () =>
      !!this.current() &&
      !!this.availability()?.profiles.length &&
      !!this.profile() &&
      !this.busy() &&
      !this.turns().some((t) => t.phase === "active") &&
      !this.pending &&
      this.draft().trim().length > 0,
  );
  private generation = 0;
  private operations = new Set<AbortController>();
  private streamController: AbortController | null = null;
  private lastSequence = 0;
  private acceptedBytes = 0;
  private pending: { conversationId: string; body: ChatCreate } | null = null;
  private pendingConversation: string | null = null;
  private focusTarget: HTMLElement | null = null;
  private preferredFocus: string | null = null;
  private focusVersion = 0;

  constructor() {
    effect(() => {
      this.scope();
      this.context();
      this.contexts.identityVersion();
      untracked(() => {
        this.clear();
        if (this.reportsReady()) void this.load();
      });
    });
    effect(() => {
      const ready = this.reportsReady();
      untracked(() => {
        if (ready && !this.availability() && !this.busy()) void this.load();
      });
    });
    const subscription = this.contexts.invalidated.subscribe(() =>
      this.clear(),
    );
    inject(DestroyRef).onDestroy(() => {
      subscription.unsubscribe();
      this.clear();
    });
  }

  private clear(): void {
    ++this.generation;
    for (const c of this.operations) c.abort();
    this.operations.clear();
    this.streamController?.abort();
    this.streamController = null;
    this.availability.set(null);
    this.conversations.set([]);
    this.current.set(null);
    this.turns.set([]);
    this.selected.set(null);
    this.events.set([]);
    this.draft.set("");
    this.profile.set("");
    this.problem.set("");
    this.busy.set(false);
    this.streaming.set(false);
    this.disconnected.set(false);
    this.hasMore.set(false);
    this.hasEarlier.set(false);
    this.pending = null;
    this.pendingConversation = null;
    this.lastSequence = 0;
    this.acceptedBytes = 0;
    this.focusTarget = null;
    this.preferredFocus = null;
    ++this.focusVersion;
  }

  private async admitted<T>(
    action: (context: CatalogContext, signal: AbortSignal) => Promise<T>,
  ): Promise<T> {
    if (!this.reportsReady()) throw new ChatError("reportsPending");
    this.captureFocus();
    const generation = this.generation,
      controller = new AbortController();
    this.operations.add(controller);
    try {
      const context = await firstValueFrom(
        this.catalog
          .context()
          .pipe(takeUntil(fromEvent(controller.signal, "abort"))),
      );
      const expected = this.context();
      if (generation !== this.generation || controller.signal.aborted)
        throw new ChatError("stale");
      if (
        context.actorPrincipalId !== expected.actorPrincipalId ||
        context.allowedOrganizationId !== expected.allowedOrganizationId ||
        context.mode !== expected.mode ||
        (context.mode === "session" ? context.identityContext : null) !==
          (expected.mode === "session" ? expected.identityContext : null) ||
        !this.contexts.canPresent(context)
      ) {
        this.clear();
        throw new ChatError("stale");
      }
      if (!context.features.chat) throw new ChatError("unavailable");
      this.restoreFocus();
      const value = await action(context, controller.signal);
      if (generation !== this.generation || controller.signal.aborted)
        throw new ChatError("stale");
      return value;
    } finally {
      this.operations.delete(controller);
      this.restoreFocus();
    }
  }

  async load(): Promise<void> {
    if (this.busy() || !this.reportsReady()) return;
    this.availability.set(null);
    if (!this.context().features.chat) {
      this.problem.set("Chat is not configured for this workspace.");
      return;
    }
    await this.perform(async () => {
      const [availability, page] = await this.admitted((c, s) =>
        Promise.all([
          this.api.availability(this.scope(), c, s),
          this.api.list(this.scope(), c, s),
        ]),
      );
      this.availability.set(availability);
      this.profile.set(availability.profiles[0]?.profileId ?? "");
      this.conversations.set(page.conversations);
      this.hasMore.set(page.hasMore);
      if (!availability.profiles.length)
        this.problem.set("No selectable model profile is available.");
    });
  }
  async newConversation(): Promise<void> {
    if (this.busy() || this.pending) return;
    await this.perform(async () => {
      this.pendingConversation ??= crypto.randomUUID();
      const result = await this.admitted((c, s) =>
        this.api.create(this.scope(), c, s, this.pendingConversation!),
      );
      this.pendingConversation = null;
      this.stopStream();
      this.current.set(result);
      this.turns.set([]);
      this.selected.set(null);
      this.events.set([]);
      this.hasEarlier.set(false);
      this.conversations.update((list) =>
        [
          result,
          ...list.filter((v) => v.conversationId !== result.conversationId),
        ].slice(0, 50),
      );
    }, "[data-chat-composer]");
  }
  async open(id: string): Promise<void> {
    if (this.busy() || this.pending) return;
    this.stopStream();
    this.current.set(null);
    this.turns.set([]);
    this.selected.set(null);
    this.events.set([]);
    await this.perform(async () => {
      const result = await this.admitted((c, s) =>
        this.api.detail(this.scope(), c, s, id),
      );
      this.current.set(result.conversation);
      this.turns.set(result.turns);
      this.hasEarlier.set(result.hasEarlierTurns);
      const selected = result.turns.at(-1);
      if (selected) {
        this.selected.set(selected);
        this.lastSequence = 0;
        this.acceptedBytes = 0;
      }
    });
    if (this.selected()) await this.reconnect();
  }
  async send(): Promise<void> {
    if (!this.canSend()) return;
    const profile = this.availability()?.profiles.find(
      (p) => p.profileId === this.profile(),
    );
    if (!profile || !this.current()) return;
    if (
      new TextEncoder().encode(this.draft()).byteLength >
        (this.availability()?.limits.maxTextBytes ?? 0) ||
      this.draft().includes("\0")
    ) {
      this.problem.set(
        "The question exceeds the text limit or contains a NUL character.",
      );
      return;
    }
    this.pending = {
      conversationId: this.current()!.conversationId,
      body: {
        turnId: crypto.randomUUID(),
        text: this.draft(),
        profileId: profile.profileId,
        profileVersion: profile.version,
        selectors: [],
      },
    };
    await this.retryQuestion();
  }
  hasPending(): boolean {
    return this.pending !== null;
  }
  async retryQuestion(): Promise<void> {
    if (!this.pending || this.busy()) return;
    let accepted = false;
    await this.perform(async () => {
      const pending = this.pending!;
      let result: ChatTurn;
      try {
        result = await this.admitted((c, s) =>
          this.api.send(
            this.scope(),
            c,
            s,
            pending.conversationId,
            pending.body,
          ),
        );
      } catch (error) {
        if (
          error instanceof ChatError &&
          [
            "invalidRequest",
            "bodyLimit",
            "profileUnavailable",
            "turnNotStarted",
            "turnActive",
          ].includes(error.code)
        )
          this.pending = null;
        throw error;
      }
      this.pending = null;
      this.draft.set("");
      this.stopStream();
      this.events.set([]);
      this.lastSequence = 0;
      this.acceptedBytes = 0;
      this.turns.update((turns) => [
        ...turns.filter((t) => t.turnId !== result.turnId),
        result,
      ]);
      this.selected.set(result);
      accepted = true;
    }, "[data-chat-composer]");
    if (accepted) await this.reconnect();
  }
  async selectTurn(turn: ChatTurn): Promise<void> {
    if (this.busy()) return;
    this.stopStream();
    this.selected.set(turn);
    this.events.set([]);
    this.lastSequence = 0;
    this.acceptedBytes = 0;
    await this.reconnect();
  }
  private stopStream(): void {
    this.streamController?.abort();
    this.streamController = null;
    this.streaming.set(false);
    this.disconnected.set(false);
  }
  async reconnect(): Promise<void> {
    const conversation = this.current(),
      selected = this.selected();
    if (
      !conversation ||
      !selected ||
      this.streaming() ||
      this.busy() ||
      this.events().some(terminal)
    )
      return;
    const generation = this.generation,
      controller = new AbortController();
    this.streamController = controller;
    this.streaming.set(true);
    this.disconnected.set(false);
    this.problem.set("");
    try {
      await this.admitted(async (context, signal) => {
        const stop = () => controller.abort();
        signal.addEventListener("abort", stop, { once: true });
        try {
          await this.api.stream(
            this.scope(),
            context,
            controller.signal,
            conversation.conversationId,
            selected.turnId,
            this.lastSequence,
            (value) => {
              if (
                controller.signal.aborted ||
                generation !== this.generation ||
                this.selected()?.turnId !== selected.turnId
              )
                return;
              this.acceptedBytes += new TextEncoder().encode(
                JSON.stringify(value),
              ).byteLength;
              if (this.acceptedBytes > 4 * 1024 * 1024)
                throw new ChatError("bodyLimit");
              this.lastSequence = value.sequence;
              this.events.update((events) => [...events, value]);
              if (terminal(value)) {
                const phase =
                  value.type === "turn.completed"
                    ? "completed"
                    : value.type === "turn.cancelled"
                      ? "cancelled"
                      : "failed";
                this.turns.update((turns) =>
                  turns.map((t) =>
                    t.turnId === selected.turnId ? { ...t, phase } : t,
                  ),
                );
              }
            },
          );
        } finally {
          signal.removeEventListener("abort", stop);
        }
      });
    } catch (error) {
      if (generation === this.generation && !controller.signal.aborted)
        this.showError(error);
    } finally {
      if (this.streamController === controller) {
        this.streamController = null;
        this.streaming.set(false);
        this.disconnected.set(!this.events().some(terminal));
      }
    }
  }
  async cancel(): Promise<void> {
    const conversation = this.current(),
      selected = this.selected();
    if (!conversation || !selected || this.busy()) return;
    await this.perform(async () => {
      const result = await this.admitted((c, s) =>
        this.api.cancel(
          this.scope(),
          c,
          s,
          conversation.conversationId,
          selected.turnId,
        ),
      );
      this.turns.update((turns) =>
        turns.map((t) =>
          t.turnId === result.turnId ? reconcileTurn(t, result) : t,
        ),
      );
    }, "[data-chat-composer]");
    if (!this.streaming()) await this.reconnect();
  }
  follow(value: ChatCitation): void {
    const conversation = this.current();
    if (conversation) this.citation.emit({ conversation, citation: value });
  }
  private async perform(
    work: () => Promise<void>,
    preferredFocus?: string,
  ): Promise<void> {
    if (this.busy()) return;
    const generation = this.generation;
    this.busy.set(true);
    this.problem.set("");
    let succeeded = false;
    try {
      await work();
      succeeded = true;
    } catch (error) {
      if (generation === this.generation) this.showError(error);
    } finally {
      if (generation === this.generation) {
        this.busy.set(false);
        this.restoreFocus(
          this.pending
            ? "[data-chat-recover]"
            : succeeded
              ? preferredFocus
              : undefined,
        );
      }
    }
  }

  private captureFocus(): void {
    const active = this.document.activeElement;
    if (
      active instanceof HTMLElement &&
      this.element.nativeElement.contains(active)
    ) {
      this.focusTarget = active;
      this.preferredFocus = null;
      ++this.focusVersion;
    }
  }

  private restoreFocus(preferred?: string): void {
    if (preferred) this.preferredFocus = preferred;
    const generation = this.generation;
    const version = this.focusVersion;
    afterNextRender(
      () => {
        if (
          generation !== this.generation ||
          version !== this.focusVersion ||
          !this.contexts.canPresent(this.context())
        )
          return;
        const active = this.document.activeElement;
        if (
          active !== this.document.body &&
          active !== this.document.documentElement &&
          active !== this.focusTarget
        ) {
          this.preferredFocus = null;
          return;
        }
        const target = this.preferredFocus
          ? this.element.nativeElement.querySelector<HTMLElement>(
              this.preferredFocus,
            )
          : this.focusTarget;
        if (
          target?.isConnected &&
          this.element.nativeElement.contains(target) &&
          !target.matches(":disabled")
        ) {
          target.focus();
          this.focusTarget = target;
          this.preferredFocus = null;
        }
      },
      { injector: this.injector },
    );
  }
  private showError(error: unknown): void {
    const code = error instanceof ChatError ? error.code : "unavailable";
    if (code === "stale") return;
    if (code === "identityRejected") {
      this.clear();
      this.problem.set(
        "The session was rejected. Sign in again and refresh the catalog.",
      );
      return;
    }
    const messages: Record<string, string> = {
      reportsPending: "Wait for the report reads to finish before using chat.",
      unavailable: "Chat is unavailable. Refresh chat to check again.",
      reportsUnavailable:
        "Generate the saved explanation and validation before starting a conversation.",
      profileUnavailable:
        "This model profile is no longer available. Refresh chat before choosing another profile.",
      turnActive:
        "This conversation already has an active answer. Reopen it to read that answer.",
      idempotencyConflict:
        "The original question conflicts with the saved request. It has not been replaced.",
      turnNotStarted:
        "The question did not start. Its text is retained; send it again only when you are ready.",
      incompatible:
        "The chat response could not be verified. No unverified content was accepted.",
      notFound: "This conversation is unavailable or access has changed.",
      bodyLimit: "The chat response exceeded the display limit.",
    };
    this.problem.set(
      messages[code] ??
        "The chat request failed. No new question will be sent automatically.",
    );
  }
}
