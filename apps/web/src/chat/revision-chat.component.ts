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
import { RouterLink } from "@angular/router";
import { CatalogContextStore } from "../catalog/catalog-context";
import { CatalogApi } from "../catalog/catalog-api";
import {
  catalogOwner,
  type CatalogContext,
  type CatalogRevisionScope,
  type CatalogMapping,
  type CatalogMappingPage,
  type CatalogProject,
  type CatalogRevision,
} from "../catalog/catalog-types";
import { ChatApi } from "./chat-api";
import { ChatViewRecovery } from "./chat-view-recovery";
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
import { HelpLinkComponent } from "../help/help-link.component";
import {
  citationLink,
  conversationScope,
  isWorkspace,
  type AnyConversation,
  type ChatScope,
  type ChatFocus,
  type UiAvailability,
  type WorkspaceCitationEvent,
  type WorkspaceCitation,
} from "./workspace-chat-types";
import { UnsentWork } from "../workbench/unsent-work";
import { hasChatUnsentWork } from "./chat-unsent-work";

@Component({
  selector: "weft-revision-chat",
  imports: [HelpLinkComponent, RouterLink],
  templateUrl: "./revision-chat.component.html",
  styleUrl: "./revision-chat.component.css",
})
export class RevisionChatComponent {
  readonly terminal = terminal;
  readonly scope = input<CatalogRevisionScope | null>(null);
  /** A suggestion for a new conversation only; route changes never rebind a live conversation. */
  readonly suggestedFocus = input<CatalogRevisionScope | null>(null);
  readonly inspectionScope = input<CatalogRevisionScope | null>(null);
  readonly context = input.required<CatalogContext>();
  readonly reportsReady = input(false);
  readonly requestedConversation = input<string | null>(null);
  readonly requestedVersion = input(0);
  readonly selectionRejected = output<void>();
  readonly requestedLegacyScope = input<CatalogRevisionScope | null>(null);
  readonly projectionId = input<string | null>(null);
  readonly validationId = input<string | null>(null);
  readonly workspaceCitation = output<WorkspaceCitationEvent>();
  readonly selectedConversation = output<AnyConversation>();
  readonly isWorkspace = isWorkspace;
  readonly citationLink = (citation: WorkspaceCitation): string => {
    const link = citationLink(citation),
      current = this.current();
    return link.startsWith("/chat/resources?") &&
      current &&
      isWorkspace(current)
      ? `${link}&conversationId=${encodeURIComponent(current.conversationId)}`
      : link;
  };
  readonly citation = output<{
    conversation: ChatConversation;
    citation: ChatCitation;
  }>();
  private readonly api = inject(ChatApi);
  private readonly contexts = inject(CatalogContextStore);
  private readonly catalog = inject(CatalogApi);
  private readonly recovery = inject(ChatViewRecovery);
  private readonly element = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);
  readonly availability = signal<UiAvailability | null>(null);
  readonly conversations = signal<AnyConversation[]>([]);
  readonly current = signal<AnyConversation | null>(null);
  readonly canCreate = signal(false);
  private workspaceProfiles: UiAvailability | null = null;
  readonly newFocus = signal<ChatFocus | null>(null);
  readonly focusQuery = signal("");
  readonly focusMappings = signal<CatalogMapping[]>([]);
  readonly focusRevisions = signal<CatalogRevision[]>([]);
  readonly focusNotice = signal("");
  readonly unnamedFocus = computed(
    () =>
      !!this.newFocus() &&
      !this.focusMappings().some(
        (mapping) => mapping.scope.mappingId === this.newFocus()?.mappingId,
      ),
  );
  readonly unlistedRevision = computed(
    () =>
      !!this.newFocus()?.revisionId &&
      !this.focusRevisions().some(
        (revision) => revision.scope.revisionId === this.newFocus()?.revisionId,
      ),
  );
  private focusChosen = false;
  readonly moreFocus = signal(false);
  private focusScan: {
    query: string;
    projects: CatalogProject[];
    projectCursor: string | null;
    projectIndex: number;
    mappingCursor: string | null;
    started: boolean;
  } | null = null;
  private revisionCursor: string | null = null;
  readonly moreRevisions = signal(false);
  readonly workspaceCitations = computed(() =>
    this.events().filter(
      (e): e is WorkspaceCitationEvent => e.type === "workspace.citation",
    ),
  );
  readonly sharedData = computed(() =>
    this.events().filter((e) => e.type === "data.shared"),
  );
  readonly dataPolicy = computed(() =>
    this.availability()?.profiles.find((p) => p.profileId === this.profile())
      ?.payloadsAllowed === true
      ? "This profile permits sample data only with a current payload-read grant for the data's project. Each tool call rechecks both policies."
      : "Metadata only: sample payload tools are not offered to this profile.",
  );
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
  private readonly pendingState = signal<{
    conversationId: string;
    body: ChatCreate;
  } | null>(null);
  private get pending(): { conversationId: string; body: ChatCreate } | null {
    return this.pendingState();
  }
  private set pending(
    value: { conversationId: string; body: ChatCreate } | null,
  ) {
    this.pendingState.set(value);
  }
  private readonly pendingConversationState = signal<string | null>(null);
  private get pendingConversation(): string | null {
    return this.pendingConversationState();
  }
  private set pendingConversation(value: string | null) {
    this.pendingConversationState.set(value);
  }
  private pendingFocus: ChatFocus | null = null;
  private recoveredScope: ChatScope | null = null;
  private admission: {
    context: CatalogContext;
    scope: ChatScope;
  } | null = null;
  /**
   * A same-owner re-read replaces the context; only another owner, revision or set of workspace
   * features restarts chat.
   */
  private readonly owner = computed(() => {
    const scope = this.scope();
    const context = this.context();
    return JSON.stringify([catalogOwner(context), scope, context.features]);
  });
  private focusTarget: HTMLElement | null = null;
  private preferredFocus: string | null = null;
  private focusVersion = 0;

  constructor() {
    inject(UnsentWork).track(() => this.hasUnsent());
    effect(() => {
      const suggestion = this.suggestedFocus(),
        ready = this.availability();
      untracked(() => {
        if (
          ready &&
          suggestion &&
          !this.current() &&
          !this.focusChosen &&
          !this.hasUnsent()
        )
          this.newFocus.set({
            projectId: suggestion.projectId,
            mappingId: suggestion.mappingId,
            revisionId: suggestion.revisionId,
          });
      });
    });
    effect(() => {
      this.owner();
      this.contexts.identityVersion();
      untracked(() => {
        this.suspend();
        void this.load();
      });
    });
    effect(() => {
      const id = this.requestedConversation();
      this.requestedVersion();
      untracked(() => {
        if (!id || this.current()?.conversationId === id) return;
        if (this.availability()) void this.open(id);
        // Refresh temporarily removes availability while retaining the active
        // conversation. Reject route switches just as for other busy work; do
        // not leave its draft or recovery under a different conversation URL.
        else if (this.current()) this.selectionRejected.emit();
      });
    });
    // A failed or negative check is not a new owner: suspend the unsent question rather than discard it.
    const subscription = this.contexts.invalidated.subscribe(() =>
      this.suspend(),
    );
    const beforeUnload = (event: BeforeUnloadEvent) => {
      if (this.hasUnsent()) {
        event.preventDefault();
        event.returnValue = "";
      }
    };
    this.document.defaultView?.addEventListener("beforeunload", beforeUnload);
    inject(DestroyRef).onDestroy(() => {
      this.document.defaultView?.removeEventListener(
        "beforeunload",
        beforeUnload,
      );
      subscription.unsubscribe();
      this.suspend();
    });
  }

  private suspend(): void {
    const admission = this.admission,
      draft = this.draft(),
      pending = this.pending,
      pendingConversation = this.pendingConversation;
    if (admission && (draft || pending || pendingConversation))
      this.recovery.keep(admission.context, admission.scope, {
        conversationId: this.current()?.conversationId ?? null,
        draft,
        pending,
        pendingConversation,
        conversationScope: this.current()
          ? this.activeScope()
          : this.recoveredScope,
        pendingFocus: this.pendingFocus,
        focus: this.newFocus(),
      });
    this.clear();
  }

  /** Reopens the kept question's conversation after this panel's own fresh admission; nothing is sent again. */
  private async resume(): Promise<void> {
    const work = this.recovery.take(this.context(), this.viewScope());
    if (!work) return;
    this.draft.set(work.draft);
    this.pending = work.pending;
    this.pendingConversation = work.pendingConversation;
    this.pendingFocus = work.pendingFocus ?? null;
    this.recoveredScope = work.conversationScope ?? null;
    if (!this.scope() && work.focus) {
      const focus = work.focus;
      await this.perform(async () => {
        const mapping = await this.admitted((_c, signal) =>
          firstValueFrom(
            this.catalog
              .mapping({
                organizationId: this.context().allowedOrganizationId,
                projectId: focus.projectId,
                mappingId: focus.mappingId,
              })
              .pipe(takeUntil(fromEvent(signal, "abort"))),
          ),
        );
        const revision = focus.revisionId
          ? await this.admitted((_c, signal) =>
              firstValueFrom(
                this.catalog
                  .revision({
                    organizationId: this.context().allowedOrganizationId,
                    projectId: focus.projectId,
                    mappingId: focus.mappingId,
                    revisionId: focus.revisionId!,
                  })
                  .pipe(takeUntil(fromEvent(signal, "abort"))),
              ),
            )
          : null;
        this.focusMappings.set([mapping]);
        this.focusRevisions.set(revision ? [revision] : []);
        this.newFocus.set(focus);
      });
    }
    const conversation = work.pending?.conversationId ?? work.conversationId;
    if (conversation) await this.show(conversation);
  }

  private clear(): void {
    this.admission = null;
    ++this.generation;
    for (const c of this.operations) c.abort();
    this.operations.clear();
    this.streamController?.abort();
    this.streamController = null;
    this.availability.set(null);
    this.workspaceProfiles = null;
    this.canCreate.set(false);
    this.newFocus.set(null);
    this.focusChosen = false;
    this.focusMappings.set([]);
    this.focusRevisions.set([]);
    this.focusQuery.set("");
    this.focusNotice.set("");
    this.moreFocus.set(false);
    this.focusScan = null;
    this.revisionCursor = null;
    this.moreRevisions.set(false);
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
    this.pendingFocus = null;
    this.recoveredScope = null;
    this.lastSequence = 0;
    this.acceptedBytes = 0;
    this.focusTarget = null;
    this.preferredFocus = null;
    ++this.focusVersion;
  }

  private async admitted<T>(
    action: (context: CatalogContext, signal: AbortSignal) => Promise<T>,
  ): Promise<T> {
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
        !this.contexts.canDispatch(context)
      ) {
        this.recovery.discard();
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
    if (this.busy() || this.pending) return;
    this.availability.set(null);
    if (!this.context().features.chat) {
      this.problem.set("Chat is not configured for this workspace.");
      return;
    }
    await this.perform(async () => {
      const [availability, page] = await this.admitted((c, s) =>
        Promise.all([
          this.api
            .workspaceAvailability(this.viewScope(), c, s)
            .catch((error) => {
              if (
                error instanceof ChatError &&
                error.code === "profileUnavailable"
              )
                return null;
              throw error;
            }),
          this.api.organizationList(
            this.viewScope(),
            c,
            s,
            this.scope()?.revisionId,
          ),
        ]),
      );
      this.workspaceProfiles = availability;
      this.canCreate.set(!!availability?.profiles.length);
      this.availability.set(
        availability ?? {
          toolsEnabled: true,
          profiles: [],
          limits: { maxTextBytes: 32768, maxSelectors: 32, turnSeconds: 180 },
        },
      );
      this.profile.set(availability?.profiles[0]?.profileId ?? "");
      this.conversations.set(page.conversations);
      this.hasMore.set(page.hasMore);
      if (!availability?.profiles.length)
        this.problem.set("No selectable model profile is available.");
      this.admission = { context: this.context(), scope: this.viewScope() };
    });
    if (this.admission) {
      await this.resume();
      if (!this.current() && this.requestedConversation())
        await this.show(this.requestedConversation()!);
    }
  }
  async newConversation(): Promise<void> {
    if (this.busy() || this.pending || !this.canCreate()) return;
    if (!this.pendingConversation && !this.allowConversationSwitch()) return;
    await this.perform(async () => {
      if (!this.pendingConversation) {
        this.pendingConversation = crypto.randomUUID();
        const scope = this.scope();
        this.pendingFocus = scope
          ? {
              projectId: scope.projectId,
              mappingId: scope.mappingId,
              revisionId: scope.revisionId,
            }
          : this.newFocus();
      }
      const result = await this.admitted((c, s) =>
        this.api.createWorkspace(
          this.viewScope(),
          c,
          s,
          this.pendingConversation!,
          this.pendingFocus,
        ),
      );
      this.pendingConversation = null;
      this.pendingFocus = null;
      this.stopStream();
      this.current.set(result);
      this.availability.set(this.workspaceProfiles);
      // Creation finishes asynchronously. Keep an explicit, still-available profile
      // choice instead of replacing it with the default when the reply arrives.
      if (
        !this.workspaceProfiles?.profiles.some(
          (profile) => profile.profileId === this.profile(),
        )
      )
        this.profile.set(this.workspaceProfiles?.profiles[0]?.profileId ?? "");
      this.selectedConversation.emit(result);
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
    if (this.current()?.conversationId === id) return;
    if (this.busy() || this.pending || this.pendingConversation) {
      this.selectionRejected.emit();
      return;
    }
    if (!this.allowConversationSwitch()) {
      const current = this.current();
      if (current) this.selectedConversation.emit(current);
      return;
    }
    await this.show(id);
  }
  private allowConversationSwitch(): boolean {
    if (!this.hasUnsent()) return true;
    if (!(
      this.document.defaultView?.confirm(
        "Switch conversation? Discard the unsent question in this conversation? A previously sent request may already have committed.",
      ) ?? false
    ))
      return false;
    this.draft.set("");
    return true;
  }
  private async show(id: string): Promise<void> {
    const entry = this.conversations().find((c) => c.conversationId === id);
    const routeScope = entry
      ? conversationScope(entry, this.context().allowedOrganizationId)
      : (this.recoveredScope ??
        this.requestedLegacyScope() ?? {
          organizationId: this.context().allowedOrganizationId,
        });
    this.stopStream();
    this.current.set(null);
    this.turns.set([]);
    this.selected.set(null);
    this.events.set([]);
    await this.perform(async () => {
      const result = await this.admitted<{
        conversation: AnyConversation;
        turns: ChatTurn[];
        hasEarlierTurns: boolean;
      }>((c, s) =>
        "revisionId" in routeScope
          ? this.api.detail(routeScope, c, s, id)
          : this.api.workspaceDetail(routeScope, c, s, id),
      );
      const available = await this.admitted<UiAvailability>((c, s) =>
        "revisionId" in routeScope
          ? this.api.availability(routeScope, c, s)
          : this.api.workspaceAvailability(routeScope, c, s).catch((error) => {
              if (
                error instanceof ChatError &&
                error.code === "profileUnavailable"
              )
                return {
                  toolsEnabled: true,
                  profiles: [],
                  limits: {
                    maxTextBytes: 32768,
                    maxSelectors: 32,
                    turnSeconds: 180,
                  },
                };
              throw error;
            }),
      );
      this.availability.set(available);
      this.profile.set(available.profiles[0]?.profileId ?? "");
      this.current.set(result.conversation);
      this.selectedConversation.emit(result.conversation);
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
  hasPendingCreation(): boolean {
    return this.pendingConversation !== null;
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
            this.activeScope(),
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
            this.activeScope(),
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
          this.activeScope(),
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
    if (conversation && !("kind" in conversation))
      this.citation.emit({ conversation, citation: value });
  }
  legacyCitationLink(value: ChatCitation): string {
    const conversation = this.current();
    if (!conversation || isWorkspace(conversation) || !conversation.revision)
      return "";
    return citationLink({
      kind: "report",
      ...conversation.revision,
      reportDerivationId: value.reportDerivationId,
      target: value.target,
      subjectId: null,
      version: null,
      sectionId: null,
    });
  }
  viewScope(): ChatScope {
    return (
      this.scope() ?? { organizationId: this.context().allowedOrganizationId }
    );
  }
  private activeScope(): ChatScope {
    const current = this.current();
    return current
      ? conversationScope(current, this.context().allowedOrganizationId)
      : (this.recoveredScope ?? this.viewScope());
  }
  hasUnsent(): boolean {
    return hasChatUnsentWork({
      draft: this.draft(),
      pending: this.pending,
      pendingConversation: this.pendingConversation,
    });
  }
  openInChat(): Record<string, string> {
    const current = this.current();
    if (!current) return {};
    return {
      conversationId: current.conversationId,
      ...(!isWorkspace(current) && current.revision
        ? {
            projectId: current.revision.projectId,
            mappingId: current.revision.mappingId,
            revisionId: current.revision.revisionId,
          }
        : {}),
    };
  }
  canSelect(item: WorkspaceCitationEvent): boolean {
    const c = item.citation,
      scope = this.inspectionScope() ?? this.scope();
    return (
      c.kind === "report" &&
      !!scope &&
      c.organizationId === scope.organizationId &&
      c.projectId === scope.projectId &&
      c.mappingId === scope.mappingId &&
      c.revisionId === scope.revisionId &&
      c.reportDerivationId ===
        (c.target?.kind === "validationReport" ||
        c.target?.kind === "validationDiagnostic"
          ? this.validationId()
          : this.projectionId())
    );
  }
  async moreConversations(): Promise<void> {
    const after = this.conversations().at(-1)?.conversationId;
    if (!after || !this.hasMore() || this.busy()) return;
    await this.perform(async () => {
      const page = await this.admitted((c, s) =>
        this.api.organizationList(
          this.viewScope(),
          c,
          s,
          this.scope()?.revisionId,
          after,
        ),
      );
      this.conversations.update((list) => [
        ...list,
        ...page.conversations.filter(
          (c) => !list.some((old) => old.conversationId === c.conversationId),
        ),
      ]);
      this.hasMore.set(page.hasMore);
    });
  }
  async searchFocus(more = false): Promise<void> {
    if (this.busy() || this.scope()) return;
    if (!more || !this.focusScan) {
      this.focusScan = {
        query: this.focusQuery().trim().toLocaleLowerCase(),
        projects: [],
        projectCursor: null,
        projectIndex: 0,
        mappingCursor: null,
        started: false,
      };
      this.focusMappings.set([]);
    }
    // A failed later page must not advance beyond matches that were never presented.
    const scan = { ...this.focusScan };
    await this.perform(async () => {
      const found: CatalogMapping[] = [];
      let requests = 0,
        done = false;
      await this.admitted(async (context, signal) => {
        while (requests < 40 && found.length < 50) {
          if (scan.projectIndex >= scan.projects.length) {
            if (scan.started && !scan.projectCursor) {
              done = true;
              break;
            }
            const page = await firstValueFrom(
              this.catalog
                .projects(context.allowedOrganizationId, scan.projectCursor)
                .pipe(takeUntil(fromEvent(signal, "abort"))),
            );
            requests++;
            scan.started = true;
            scan.projects = page.projects;
            scan.projectIndex = 0;
            scan.projectCursor = page.nextCursor ?? null;
            if (!scan.projects.length) continue;
          }
          const project = scan.projects[scan.projectIndex]!;
          const mappings: CatalogMappingPage = await firstValueFrom(
            this.catalog
              .mappings(project.scope, scan.mappingCursor)
              .pipe(takeUntil(fromEvent(signal, "abort"))),
          );
          requests++;
          found.push(
            ...mappings.mappings.filter(
              (mapping) =>
                mapping.name.toLocaleLowerCase().includes(scan.query) ||
                mapping.description.toLocaleLowerCase().includes(scan.query),
            ),
          );
          scan.mappingCursor = mappings.nextCursor ?? null;
          if (!scan.mappingCursor) scan.projectIndex++;
        }
        if (
          scan.started &&
          scan.projectIndex >= scan.projects.length &&
          !scan.projectCursor
        )
          done = true;
      });
      this.focusMappings.update((list) => [
        ...list,
        ...found.filter(
          (mapping) =>
            !list.some(
              (old) => old.scope.mappingId === mapping.scope.mappingId,
            ),
        ),
      ]);
      this.focusScan = scan;
      this.moreFocus.set(!done);
      this.focusNotice.set(
        !done
          ? "More projects and mappings remain. Search more mappings to continue from this page."
          : this.focusMappings().length
            ? "Select a mapping below."
            : "No readable mapping matched.",
      );
    });
  }
  async chooseFocus(mappingId: string): Promise<void> {
    if (this.busy() || this.pendingConversation) return;
    this.focusChosen = true;
    const mapping = this.focusMappings().find(
      (m) => m.scope.mappingId === mappingId,
    );
    this.newFocus.set(
      mapping
        ? { projectId: mapping.scope.projectId, mappingId, revisionId: null }
        : null,
    );
    this.focusRevisions.set([]);
    this.revisionCursor = null;
    this.moreRevisions.set(false);
    if (mapping) await this.loadFocusRevisions();
  }
  chooseRevision(revisionId: string): void {
    const focus = this.newFocus();
    if (!focus || this.busy() || this.pendingConversation) return;
    this.focusChosen = true;
    this.newFocus.set({ ...focus, revisionId: revisionId || null });
  }
  async loadFocusRevisions(): Promise<void> {
    const focus = this.newFocus();
    if (!focus || this.busy()) return;
    await this.perform(async () => {
      const page = await this.admitted((_c, signal) =>
        firstValueFrom(
          this.catalog
            .revisions(
              {
                organizationId: this.context().allowedOrganizationId,
                projectId: focus.projectId,
                mappingId: focus.mappingId,
              },
              this.revisionCursor,
            )
            .pipe(takeUntil(fromEvent(signal, "abort"))),
        ),
      );
      this.focusRevisions.update((list) => [...list, ...page.revisions]);
      this.revisionCursor = page.nextCursor ?? null;
      this.moreRevisions.set(!!this.revisionCursor);
    });
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
