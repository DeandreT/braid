import { DeleteItemComponent } from "../catalog/deletion/delete-item.component";
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
  viewChild,
} from "@angular/core";
import { firstValueFrom, fromEvent, map, switchMap, takeUntil } from "rxjs";
import { BrowserSessionState } from "../auth/browser-session-state";
import { RouterLink } from "@angular/router";
import { CatalogContextStore } from "../catalog/catalog-context";
import { OrganizationSelection } from "../catalog/organization-selection";
import { CatalogApi } from "../catalog/catalog-api";
import {
  catalogOwner,
  catalogRoutes,
  type CatalogContext,
  type CatalogMappingScope,
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
  citationName,
  conversationScope,
  isWorkspace,
  type AnyConversation,
  type ChatScope,
  type ChatFocus,
  type MappingWriteEvent,
  type MappingWriteReceipt,
  type UiAvailability,
  type WorkspaceCitationEvent,
  type WorkspaceCitation,
} from "./workspace-chat-types";
import {
  comparisonPath,
  comparisonQuery,
  currentSide,
} from "../catalog/comparison/revision-comparison-selection";
import { RegressionStatusComponent } from "../catalog/automation/regression-status.component";
import { DraftApi } from "../edit/draft-api";
import { UnsentWork } from "../workbench/unsent-work";
import { hasChatUnsentWork } from "./chat-unsent-work";
import { mappingUpdateQuestion } from "./mapping-update-question";
import {
  sameProposal,
  type MappingProposal,
  type MappingProposalOperations,
} from "./mapping-proposal-types";

interface ProposalReviewState {
  busy?: boolean;
  details?: MappingProposalOperations;
  reviewed?: boolean;
  uncertain?: boolean;
  message?: string;
}

@Component({
  selector: "braid-revision-chat",
  imports: [DeleteItemComponent, HelpLinkComponent, RouterLink, RegressionStatusComponent],
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
  readonly citationName = citationName;
  readonly citationLink = (citation: WorkspaceCitation): string => {
    const link = this.selection.link(citationLink(citation)),
      current = this.current();
    return /^(?:\/organizations\/[^/]+)?\/chat\/resources\?/.test(link) &&
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
  readonly selection = inject(OrganizationSelection);
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
  readonly focusBusy = signal(false);
  private focusController: AbortController | null = null;
  private focusTimer: ReturnType<typeof setTimeout> | undefined;
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
  readonly policySummary = computed(() =>
    this.availability()?.profiles.find((p) => p.profileId === this.profile())
      ?.payloadsAllowed
      ? "Sample data needs a current payload-read grant."
      : "Metadata only. Sample data isn't shared with this model.",
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
  private readonly problemCode = signal("");
  readonly pendingProblem = computed(() =>
    this.problemCode() === "unavailable"
      ? "The request outcome is unknown."
      : this.problem(),
  );
  readonly problemAtComposer = signal(false);
  readonly menu = signal<
    "history" | "more" | "scope" | "focus" | "notes" | null
  >(null);
  readonly historyQuery = signal("");
  readonly title = computed(
    () => this.current()?.title || this.turns()[0]?.text || "New chat",
  );
  readonly historyGroups = computed(() => {
    this.menu();
    const query = this.historyQuery().trim().toLocaleLowerCase();
    const today = new Date().toDateString();
    return ["Today", "Earlier"]
      .map((label) => ({
        label,
        conversations: this.conversations().filter(
          (conversation) =>
            (conversation.title || conversation.createdAtUtc)
              .toLocaleLowerCase()
              .includes(query) &&
            (new Date(
              conversation.lastTurnAtUtc ?? conversation.createdAtUtc,
            ).toDateString() ===
              today) ===
              (label === "Today"),
        ),
      }))
      .filter((group) => group.conversations.length);
  });
  readonly starters = [
    "Which fields does this mapping leave empty?",
    "How can I investigate a failing test?",
    "What changed between versions?",
  ];
  readonly draftBytes = computed(
    () => new TextEncoder().encode(this.draft()).byteLength,
  );
  readonly nearLimit = computed(
    () =>
      this.draftBytes() >=
      (this.availability()?.limits.maxTextBytes ?? 32768) * 0.8,
  );
  readonly pendingText = computed(() => this.pendingState()?.body.text ?? "");
  readonly canStop = computed(
    () =>
      !!this.selected() &&
      this.selectedPhase() === "active" &&
      !this.events().some(terminal),
  );
  private readonly savedEvents = signal<Record<string, ChatEvent[]>>({});
  private transcriptController: AbortController | null = null;
  private readonly answerProblems = signal<Record<string, string>>({});
  readonly expandedTools = signal<ReadonlySet<string>>(new Set());
  readonly expandedDetails = signal<ReadonlySet<string>>(new Set());
  readonly copiedTurn = signal("");
  readonly copyProblem = signal("");
  readonly dismissedTips = signal<ReadonlySet<string>>(new Set());
  private readonly scroller = viewChild<ElementRef<HTMLElement>>("scroller");
  private followBottom = true;
  private menuTrigger: HTMLElement | null = null;
  readonly conversationFocus = computed(() => {
    const conversation = this.current();
    if (conversation && !isWorkspace(conversation))
      return {
        label: "Pinned version",
        detail: `Legacy conversation: pinned to its original revision ${conversation.revision?.revisionId ?? ""}. Citations never change the panel's version.`,
      };
    const focus =
      conversation && isWorkspace(conversation) ? conversation.focus : null;
    return this.describeFocus(focus);
  });

  private selectedPhase(): ChatTurn["phase"] | undefined {
    return this.turns().find((turn) => turn.turnId === this.selected()?.turnId)
      ?.phase;
  }

  private describeFocus(focus: ChatFocus | null): {
    label: string;
    detail: string;
  } {
    if (!focus)
      return {
        label: "Whole workspace",
        detail:
          "No mapping focus. Tools can use any project you are authorized to read; editing requires current edit permission.",
      };
    const name =
      this.focusMappings().find(
        (mapping) => mapping.scope.mappingId === focus.mappingId,
      )?.name ?? "Mapping";
    return focus.revisionId
      ? {
          label: `${name} · pinned version`,
          detail: `Pinned version: ${focus.revisionId}. Mapping ${focus.mappingId}. Focus stays fixed for this conversation and never limits authorized tools or changes the panel's version.`,
        }
      : {
          label: `${name} · follows newest version`,
          detail: `Follows newest version: mapping ${focus.mappingId}. Its head is resolved when each new turn starts. The recorded turn focus stays fixed. Focus never limits authorized tools or changes the panel's version.`,
        };
  }

  nextFocusLabel(): string {
    return this.describeFocus(this.newFocus()).label;
  }

  toggleMenu(
    menu: NonNullable<ReturnType<typeof this.menu>>,
    trigger: HTMLElement,
  ): void {
    if (this.menu() === menu) {
      this.closeMenu(true);
      return;
    }
    if (
      menu === "history" &&
      (!this.availability() || this.busy() || this.hasPending())
    )
      return;
    this.menuTrigger = trigger;
    this.menu.set(menu);
    if (menu === "focus" && !this.focusScan && !this.focusBusy())
      void this.searchFocus();
    afterNextRender(
      () => {
        this.element.nativeElement
          .querySelector<HTMLElement>(
            `#chat-${menu === "notes" ? "composer-notes" : `${menu}-menu`} :is(input, button, a, select)`,
          )
          ?.focus();
      },
      { injector: this.injector },
    );
  }

  closeMenu(restore = true): void {
    this.menu.set(null);
    if (restore && this.menuTrigger?.isConnected) this.menuTrigger.focus();
    this.menuTrigger = null;
  }

  allowTip(id: string): void {
    this.dismissedTips.update((previous) => {
      const next = new Set(previous);
      next.delete(id);
      return next;
    });
  }

  private dismissVisibleTips(): boolean {
    const visible = [
      ...this.element.nativeElement.querySelectorAll<HTMLElement>(
        "[data-chat-tip]",
      ),
    ]
      .filter(
        (trigger) =>
          trigger.querySelector(".chat-tip")?.getClientRects().length,
      )
      .map((trigger) => trigger.dataset["chatTip"]!);
    if (!visible.length) return false;
    this.dismissedTips.update((previous) => new Set([...previous, ...visible]));
    return true;
  }

  async chooseConversation(id: string): Promise<void> {
    this.closeMenu();
    await this.open(id);
    this.restoreFocus(".chat-title");
  }

  async refresh(): Promise<void> {
    this.closeMenu();
    await this.load();
  }

  async useStarter(text: string): Promise<void> {
    if (!this.current()) await this.newConversation();
    if (!this.current() || this.hasPending()) return;
    this.draft.set(text);
    this.restoreFocus("[data-chat-composer]");
  }

  /** Explicitly confirmed feedback is workspace-only and may never replace newer work. */
  async prepareWorkspaceQuestion(
    text: string,
    isCurrent: () => boolean,
  ): Promise<boolean> {
    if (
      !isCurrent() ||
      this.busy() ||
      this.hasPending() ||
      this.pendingConversation
    )
      return false;
    const owner = this.owner(),
      context = this.context(),
      draft = this.draft();
    let conversation = this.current();
    if (!conversation || !isWorkspace(conversation)) {
      const requested = this.requestedConversation();
      let adopted = false;
      await this.newConversation(
        draft,
        () =>
          (adopted =
            isCurrent() &&
            owner === this.owner() &&
            this.contexts.isCurrent(context) &&
            this.requestedConversation() === requested &&
            this.draft() === draft),
      );
      if (!adopted) return false;
      conversation = this.current();
    }
    if (
      !isCurrent() ||
      owner !== this.owner() ||
      !this.contexts.isCurrent(context) ||
      !this.contexts.canSend(this.context()) ||
      !conversation ||
      !isWorkspace(conversation) ||
      this.current() !== conversation ||
      this.hasPending() ||
      this.busy() ||
      this.draft() !== draft
    )
      return false;
    this.draft.set(text);
    this.restoreFocus("[data-chat-composer]");
    return true;
  }

  editDraft(event: Event): void {
    const textarea = event.target as HTMLTextAreaElement;
    this.draft.set(textarea.value);
    this.growComposer(textarea);
  }

  private growComposer(textarea: HTMLTextAreaElement): void {
    textarea.style.height = "auto";
    textarea.style.height = `${Math.min(160, textarea.scrollHeight)}px`;
  }

  followScroll(): void {
    const element = this.scroller()?.nativeElement;
    if (element)
      this.followBottom =
        element.scrollHeight - element.scrollTop - element.clientHeight < 48;
  }

  private rememberSelected(): void {
    const id = this.selected()?.turnId;
    if (id)
      this.savedEvents.update((saved) => ({ ...saved, [id]: this.events() }));
  }

  turnView(turn: ChatTurn) {
    const selected = this.selected()?.turnId === turn.turnId;
    const events = selected ? this.events() : this.savedEvents()[turn.turnId];
    const tools = new Map<
      string,
      { key: string; name: string; completed: boolean }
    >();
    const usage = new Map<string, Extract<ChatEvent, { type: "usage" }>>();
    for (const event of events ?? []) {
      if (event.type === "tool.started" || event.type === "tool.completed") {
        const key = `${event.roundId}:${event.ordinal}`;
        tools.set(key, {
          key,
          name: event.name,
          completed: event.type === "tool.completed",
        });
      }
      if (event.type === "usage") usage.set(event.roundId, event);
    }
    const last = events?.at(-1);
    const interrupted = selected
      ? this.disconnected()
      : !!events && !events.some(terminal);
    const status = selected
      ? this.status()
      : interrupted
        ? "Connection interrupted. Reconnect to read the remaining answer; this does not send another question."
        : last?.type === "turn.failed"
          ? last.reason === "interrupted"
            ? "Answer interrupted. It will not be sent again automatically."
            : `Answer failed: ${last.reason}${last.providerFailure ? ` (${last.providerFailure})` : ""}.`
          : last?.type === "turn.cancelled"
            ? "Answer cancelled."
            : last?.type === "turn.completed"
              ? "Answer complete."
              : "";
    return {
      loaded: !!events,
      selected,
      status,
      interrupted,
      checking:
        selected &&
        this.streaming() &&
        [...tools.values()].some((tool) => !tool.completed),
      tone: interrupted
        ? "uncertain"
        : last?.type === "turn.failed"
          ? "failure"
          : last?.type === "turn.cancelled"
            ? "attention"
            : last?.type === "turn.completed"
              ? "complete"
              : "running",
      answer: (events ?? [])
        .filter((e) => e.type === "text.delta")
        .map((e) => e.text)
        .join(""),
      tools: [...tools.values()],
      usage: [...usage.values()],
      citations: (events ?? []).filter(
        (e): e is ChatCitation => e.type === "citation",
      ),
      workspaceCitations: (events ?? []).filter(
        (e): e is WorkspaceCitationEvent => e.type === "workspace.citation",
      ),
      sharedData: (events ?? []).some((e) => e.type === "data.shared"),
      writes: this.mergeWrites(
        (events ?? []).filter(
          (e): e is MappingWriteEvent => e.type === "mapping.write",
        ),
        this.saveReceipts()[turn.turnId] ?? [],
      ).filter(
        (write) =>
          !(this.proposals()[turn.turnId] ?? []).some(
            (p) =>
              p.status === "saved" &&
              p.revisionId === write.revisionId &&
              p.baseRevision.organizationId ===
                write.baseRevision.organizationId &&
              p.baseRevision.projectId === write.baseRevision.projectId &&
              p.baseRevision.mappingId === write.baseRevision.mappingId,
          ),
      ),
      saveReceipts: (this.saveReceipts()[turn.turnId] ?? []).filter(
        (r) => r.status !== "saved",
      ),
      saveProblem: this.saveProblems()[turn.turnId] ?? "",
      problem: this.answerProblems()[turn.turnId] ?? "",
    };
  }

  private readonly drafts = inject(DraftApi);
  readonly saveReceipts = signal<Record<string, MappingWriteReceipt[]>>({});
  readonly saveProblems = signal<Record<string, string>>({});
  readonly readingSaves = signal<ReadonlySet<string>>(new Set());
  private readonly saveFlights = new Map<string, Promise<void>>();
  private saveEpoch = 0;
  private resetSaveReads(): void {
    this.proposalEpoch++;
    this.proposalFlights.clear();
    this.proposalVersions.clear();
    this.proposals.set({});
    this.proposalReviews.set({});
    this.proposalProblems.set({});
    this.readingProposals.set(new Set());
    ++this.saveEpoch;
    this.saveFlights.clear();
    this.readingSaves.set(new Set());
  }
  private mergeWrites(
    events: MappingWriteEvent[],
    receipts: MappingWriteReceipt[],
  ): MappingWriteEvent[] {
    const writes = [...events];
    for (const receipt of receipts)
      if (
        receipt.write &&
        !writes.some((w) => w.revisionId === receipt.write!.revisionId)
      )
        writes.push(receipt.write);
    return writes;
  }
  async refreshSaves(turn: ChatTurn): Promise<void> {
    void this.refreshProposals(turn);
    const conversation = this.current(),
      scope = this.activeScope(),
      generation = this.generation,
      epoch = this.saveEpoch;
    if (!conversation || "revisionId" in scope) return;
    const stale = () =>
      generation !== this.generation ||
      epoch !== this.saveEpoch ||
      this.current()?.conversationId !== conversation.conversationId;
    const key = `${epoch}:${conversation.conversationId}:${turn.turnId}`;
    const previous = this.saveFlights.get(key) ?? Promise.resolve();
    // Serialize a turn's reads without dropping a later refresh. Other turns read independently.
    const flight = previous.then(async () => {
      if (stale()) return;
      const version = this.proposalVersions.get(turn.turnId) ?? 0;
      try {
        const receipts = await this.admitted((context, signal) =>
          this.api.mappingWrites(
            scope,
            context,
            signal,
            conversation.conversationId,
            turn.turnId,
            false,
          ),
        );
        if (
          stale() ||
          version !== (this.proposalVersions.get(turn.turnId) ?? 0)
        )
          return;
        this.saveReceipts.update((all) => ({
          ...all,
          [turn.turnId]: receipts,
        }));
        this.saveProblems.update((all) => ({ ...all, [turn.turnId]: "" }));
      } catch {
        if (
          !stale() &&
          version === (this.proposalVersions.get(turn.turnId) ?? 0)
        )
          this.saveProblems.update((all) => ({
            ...all,
            [turn.turnId]:
              "Couldn't check durable save status. Refresh before repeating an edit.",
          }));
      }
    });
    this.saveFlights.set(key, flight);
    this.readingSaves.update((all) => new Set([...all, turn.turnId]));
    try {
      await flight;
    } finally {
      if (!stale() && this.saveFlights.get(key) === flight) {
        this.saveFlights.delete(key);
        this.readingSaves.update((all) => {
          const next = new Set(all);
          next.delete(turn.turnId);
          return next;
        });
      }
    }
  }
  readonly proposals = signal<Record<string, MappingProposal[]>>({});
  readonly proposalReviews = signal<Record<string, ProposalReviewState>>({});
  readonly proposalProblems = signal<Record<string, string>>({});
  readonly readingProposals = signal<ReadonlySet<string>>(new Set());
  private proposalEpoch = 0;
  private readonly proposalFlights = new Map<string, Promise<void>>();
  private readonly proposalVersions = new Map<string, number>();
  proposalKey(turn: ChatTurn, proposal: MappingProposal): string {
    return `${turn.turnId}:${proposal.proposalId}`;
  }
  private proposalGuard(
    turn: ChatTurn,
    proposal?: MappingProposal,
  ): (() => boolean) | null {
    const current = this.current(),
      epoch = this.proposalEpoch,
      generation = this.generation,
      owner = catalogOwner(this.context());
    if (
      !current ||
      !isWorkspace(current) ||
      !this.turns().some((t) => t.turnId === turn.turnId)
    )
      return null;
    return () =>
      epoch !== this.proposalEpoch ||
      generation !== this.generation ||
      owner !== catalogOwner(this.context()) ||
      this.current()?.conversationId !== current.conversationId ||
      (proposal !== undefined &&
        !this.proposals()[turn.turnId]?.some((p) => sameProposal(p, proposal)));
  }
  private setProposalReview(
    key: string,
    change: Partial<ProposalReviewState>,
  ): void {
    this.proposalReviews.update((all) => ({
      ...all,
      [key]: { ...all[key], ...change },
    }));
  }
  async refreshProposals(turn: ChatTurn): Promise<void> {
    const stale = this.proposalGuard(turn),
      current = this.current(),
      scope = this.activeScope();
    if (
      !stale ||
      !current ||
      "revisionId" in scope ||
      this.proposals()[turn.turnId]?.some(
        (p) => this.proposalReviews()[this.proposalKey(turn, p)]?.busy,
      )
    )
      return;
    const epoch = this.proposalEpoch,
      key = `${epoch}:${turn.turnId}`;
    const previous = this.proposalFlights.get(key) ?? Promise.resolve();
    const flight = previous.then(async () => {
      if (stale()) return;
      const version = this.proposalVersions.get(turn.turnId) ?? 0;
      try {
        const result = await this.admitted((c, s) =>
          this.api.proposals(scope, c, s, current.conversationId, turn.turnId),
        );
        if (
          stale() ||
          version !== (this.proposalVersions.get(turn.turnId) ?? 0)
        )
          return;
        this.proposalReviews.update((all) => {
          const next = { ...all };
          for (const old of this.proposals()[turn.turnId] ?? []) {
            const id = this.proposalKey(turn, old),
              replacement = result.find((p) => sameProposal(old, p));
            if (!replacement) delete next[id];
            else next[id] = { ...next[id], uncertain: false, message: "" };
          }
          return next;
        });
        this.proposals.update((all) => ({ ...all, [turn.turnId]: result }));
        this.proposalProblems.update((all) => ({ ...all, [turn.turnId]: "" }));
      } catch {
        if (
          stale() ||
          version !== (this.proposalVersions.get(turn.turnId) ?? 0)
        )
          return;
        this.proposalProblems.update((all) => ({
          ...all,
          [turn.turnId]:
            "Couldn't read proposal status. Refresh before approving or retrying.",
        }));
        for (const p of this.proposals()[turn.turnId] ?? [])
          this.setProposalReview(this.proposalKey(turn, p), {
            details: undefined,
            reviewed: false,
            uncertain: true,
          });
      }
    });
    this.proposalFlights.set(key, flight);
    this.readingProposals.update((all) => new Set([...all, turn.turnId]));
    await flight;
    if (!stale() && this.proposalFlights.get(key) === flight) {
      this.proposalFlights.delete(key);
      this.readingProposals.update((all) => {
        const next = new Set(all);
        next.delete(turn.turnId);
        return next;
      });
    }
  }
  async readProposalOperations(
    turn: ChatTurn,
    proposal: MappingProposal,
  ): Promise<void> {
    const stale = this.proposalGuard(turn, proposal),
      current = this.current(),
      scope = this.activeScope(),
      key = this.proposalKey(turn, proposal);
    if (!stale || !current || this.proposalReviews()[key]?.busy) return;
    this.setProposalReview(key, { busy: true, reviewed: false, message: "" });
    try {
      const details = await this.admitted((c, s) =>
        this.api.proposalDetails(
          scope,
          c,
          s,
          current.conversationId,
          turn.turnId,
          proposal,
        ),
      );
      if (!stale()) this.setProposalReview(key, { details });
    } catch {
      if (!stale())
        this.setProposalReview(key, {
          details: undefined,
          message:
            "Couldn't verify the complete operations. Refresh the proposal and try again.",
        });
    } finally {
      if (!stale()) this.setProposalReview(key, { busy: false });
    }
  }
  markProposalReviewed(
    turn: ChatTurn,
    proposal: MappingProposal,
    event: Event,
  ): void {
    const key = this.proposalKey(turn, proposal);
    if (
      this.proposalReviews()[key]?.details?.fingerprint === proposal.fingerprint
    )
      this.setProposalReview(key, {
        reviewed: (event.target as HTMLInputElement).checked,
      });
  }
  canApproveProposal(turn: ChatTurn, proposal: MappingProposal): boolean {
    const review = this.proposalReviews()[this.proposalKey(turn, proposal)];
    return (
      ["awaitingApproval", "approved", "pending", "notSaved"].includes(
        proposal.status,
      ) &&
      !this.proposalProblems()[turn.turnId] &&
      !this.readingProposals().has(turn.turnId) &&
      !review?.busy &&
      !review?.uncertain &&
      review?.reviewed === true &&
      review.details?.fingerprint === proposal.fingerprint
    );
  }
  async actOnProposal(
    turn: ChatTurn,
    proposal: MappingProposal,
    action: "approve" | "recover",
  ): Promise<void> {
    const stale = this.proposalGuard(turn, proposal),
      current = this.current(),
      scope = this.activeScope(),
      key = this.proposalKey(turn, proposal);
    if (
      !stale ||
      !current ||
      this.proposalReviews()[key]?.busy ||
      this.proposalReviews()[key]?.uncertain ||
      this.proposalProblems()[turn.turnId] ||
      (action === "approve"
        ? !this.canApproveProposal(turn, proposal)
        : proposal.status !== "pending")
    )
      return;
    this.proposalVersions.set(
      turn.turnId,
      (this.proposalVersions.get(turn.turnId) ?? 0) + 1,
    );
    this.setProposalReview(key, { busy: true, message: "" });
    let confirmed = false;
    try {
      const result = await this.admitted((c, s) =>
        this.api.actOnProposal(
          scope,
          c,
          s,
          current.conversationId,
          turn.turnId,
          proposal,
          action,
        ),
      );
      if (stale()) return;
      confirmed = true;
      this.proposals.update((all) => ({
        ...all,
        [turn.turnId]: all[turn.turnId]!.map((p) =>
          p.proposalId === proposal.proposalId ? result : p,
        ),
      }));
      this.setProposalReview(key, {
        reviewed: false,
        uncertain: false,
        message:
          result.status === "pending"
            ? "Still pending. Recovery never supplies missing source. Review and explicitly approve this same proposal again if it needs source."
            : "",
      });
    } catch {
      if (!stale())
        this.setProposalReview(key, {
          uncertain: true,
          reviewed: false,
          message:
            "The request outcome is unknown. No saved version is confirmed. Refresh status before retrying this exact proposal.",
        });
    } finally {
      if (!stale()) {
        this.proposalVersions.set(
          turn.turnId,
          (this.proposalVersions.get(turn.turnId) ?? 0) + 1,
        );
        this.setProposalReview(key, { busy: false });
        if (confirmed) await this.refreshSaves(turn);
      }
    }
  }
  proposalVersionLink(proposal: MappingProposal): string {
    return catalogRoutes.revision({
      ...proposal.baseRevision,
      revisionId: proposal.revisionId!,
    });
  }
  prepareProposalQuestion(turn: ChatTurn, proposal: MappingProposal): void {
    if (
      !this.canUpdate() ||
      this.hasPending() ||
      proposal.status !== "outOfDate"
    )
      return;
    const question = `Prepare a new proposal for mapping ${proposal.baseRevision.mappingId} after reading its current head. The previous proposal ${proposal.proposalId} (fingerprint ${proposal.fingerprint}) is out of date. Do not save or approve anything; I will review the new exact proposal separately. Original request: ${JSON.stringify(turn.text)}`;
    if (
      new TextEncoder().encode(question).length >
      (this.availability()?.limits.maxTextBytes ?? 0)
    ) {
      this.setProposalReview(this.proposalKey(turn, proposal), {
        message:
          "Write a new question naming this mapping and the intended change; the original request exceeds the question limit.",
      });
      return;
    }
    this.draft.set(question);
  }

  /** Per receipt: the Undo request key, retained until its outcome is known so a retry never saves twice. */
  readonly writeStates = signal<
    Record<string, { key: string; busy: boolean; message: string }>
  >({});
  writeKey(turn: ChatTurn, write: MappingWriteEvent): string {
    return `${turn.turnId}:${write.revisionId ?? write.sequence}`;
  }
  writeMapping(write: MappingWriteEvent): CatalogMappingScope {
    const { organizationId, projectId, mappingId } = write.baseRevision;
    return { organizationId, projectId, mappingId };
  }
  savedLink(write: MappingWriteEvent): string {
    return catalogRoutes.revision({
      ...write.baseRevision,
      revisionId: write.revisionId!,
    });
  }
  comparisonLink(write: MappingWriteEvent): string {
    return comparisonPath(this.writeMapping(write));
  }
  comparisonParams(write: MappingWriteEvent) {
    return comparisonQuery(
      currentSide(write.baseRevision.revisionId),
      currentSide(write.revisionId!),
    );
  }
  async undoWrite(turn: ChatTurn, write: MappingWriteEvent): Promise<void> {
    const id = this.writeKey(turn, write);
    const state = this.writeStates()[id];
    if (state?.busy || write.outcome !== "saved") return;
    const key = state?.key ?? crypto.randomUUID();
    const set = (busy: boolean, message: string, keep = true) =>
      this.writeStates.update((all) => {
        const next = { ...all };
        if (keep) next[id] = { key, busy, message };
        else next[id] = { key: crypto.randomUUID(), busy, message };
        return next;
      });
    set(true, "Saving the previous content as a new version…");
    try {
      const result = await this.drafts.restore(
        this.context(),
        this.writeMapping(write),
        write.baseRevision.revisionId,
        write.revisionId!,
        key,
      );
      const phase = result.receipt?.phase;
      if (phase === "committed")
        set(
          false,
          `Undone: version ${result.receipt?.committedSequence ?? "?"} has the content of version ${write.baseSequence}.`,
        );
      else if (phase === "headConflict")
        set(
          false,
          "Not undone: a newer version was saved after this one. Nothing changed.",
          false,
        );
      else
        set(
          false,
          "The undo outcome isn't known yet. Undo again to check the same request.",
        );
    } catch {
      set(
        false,
        "Undo couldn't be confirmed. Undo again to retry the same request.",
      );
    }
  }
  /** A new question, sent only by this click, asking for the same edit on the newer version. */
  readonly canUpdate = computed(
    () =>
      !!this.current() &&
      !this.busy() &&
      !this.turns().some((t) => t.phase === "active") &&
      !this.draft().trim(),
  );
  async updateWrite(turn: ChatTurn, write: MappingWriteEvent): Promise<void> {
    if (!this.canUpdate() || this.hasPending() || write.headSequence === null)
      return;
    const question = mappingUpdateQuestion(
      turn,
      write,
      this.availability()?.limits.maxTextBytes ?? 0,
    );
    if (question === null) {
      this.writeStates.update((states) => ({
        ...states,
        [this.writeKey(turn, write)]: {
          key: "",
          busy: false,
          message:
            "The complete intended edit can't be included in one update question. Send a new question specifying the mapping and intended change; nothing was sent or saved.",
        },
      }));
      return;
    }
    this.draft.set(question);
    await this.send();
  }
  readonly transcript = computed(() =>
    this.turns().map((turn) => ({ turn, ...this.turnView(turn) })),
  );

  toggleAnswer(set: typeof this.expandedTools, id: string): void {
    set.update((previous) => {
      const next = new Set(previous);
      if (!next.delete(id)) next.add(id);
      return next;
    });
  }

  chipRole(kind: string): string {
    return kind === "field"
      ? "source"
      : ["rule", "parameter", "control"].includes(kind)
        ? "rule"
        : ["previewOutput", "regressionRunOutput"].includes(kind)
          ? "output"
          : "evidence";
  }

  async copyAnswer(turn: ChatTurn): Promise<void> {
    this.copyProblem.set("");
    try {
      const clipboard = this.document.defaultView?.navigator.clipboard;
      if (!clipboard) throw new Error("Clipboard unavailable");
      await clipboard.writeText(this.turnView(turn).answer);
      this.copiedTurn.set(turn.turnId);
    } catch {
      this.copyProblem.set("Couldn't copy. Select the answer text to copy it.");
    }
  }
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
  private readonly sessions = inject(BrowserSessionState);
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
      this.draft();
      afterNextRender(
        () => {
          const textarea =
            this.element.nativeElement.querySelector<HTMLTextAreaElement>(
              "[data-chat-composer]",
            );
          if (textarea) this.growComposer(textarea);
        },
        { injector: this.injector },
      );
    });
    effect(() => {
      this.events();
      this.turns();
      this.pendingState();
      afterNextRender(
        () => {
          const element = this.scroller()?.nativeElement;
          if (element && this.followBottom)
            element.scrollTop = element.scrollHeight;
        },
        { injector: this.injector },
      );
    });
    const key = (event: KeyboardEvent) => {
      if (event.key !== "Escape" || this.document.querySelector("dialog[open]"))
        return;
      // Escape elsewhere on the page belongs to that control, not to chat menus or tips.
      // An open chat menu still closes when focus fell back to the page body.
      const host = this.element.nativeElement;
      const active = this.document.activeElement;
      const inside =
        (event.target instanceof Node && host.contains(event.target)) ||
        host.contains(active);
      const unfocused =
        !active ||
        active === this.document.body ||
        active === this.document.documentElement;
      if (!inside && !(this.menu() && unfocused)) return;
      if (this.menu()) {
        event.preventDefault();
        event.stopPropagation();
        this.closeMenu();
        this.dismissVisibleTips();
      } else if (this.dismissVisibleTips()) {
        event.preventDefault();
        event.stopPropagation();
      }
    };
    const outside = (event: PointerEvent) => {
      if (
        this.menu() &&
        event.target instanceof Node &&
        !this.element.nativeElement
          .querySelector(
            `#chat-${this.menu() === "notes" ? "composer-notes" : `${this.menu()}-menu`}`,
          )
          ?.contains(event.target) &&
        !this.menuTrigger?.contains(event.target)
      )
        this.closeMenu(false);
    };
    this.document.addEventListener("keydown", key, true);
    this.document.addEventListener("pointerdown", outside);
    const leaveMenu = (event: FocusEvent) => {
      const menu = this.menu();
      if (!menu || !(event.relatedTarget instanceof Node)) return;
      const popup = this.element.nativeElement.querySelector(
        `#chat-${menu === "notes" ? "composer-notes" : `${menu}-menu`}`,
      );
      if (
        popup?.contains(event.target as Node) &&
        !popup.contains(event.relatedTarget) &&
        !this.menuTrigger?.contains(event.relatedTarget)
      )
        this.closeMenu(false);
    };
    this.element.nativeElement.addEventListener("focusout", leaveMenu);
    inject(DestroyRef).onDestroy(() => {
      this.document.removeEventListener("keydown", key, true);
      this.document.removeEventListener("pointerdown", outside);
      this.element.nativeElement.removeEventListener("focusout", leaveMenu);
    });
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
    this.transcriptController?.abort();
    this.transcriptController = null;
    this.menu.set(null);
    this.menuTrigger = null;
    this.historyQuery.set("");
    this.copyProblem.set("");
    this.copiedTurn.set("");
    this.dismissedTips.set(new Set());
    this.problemAtComposer.set(false);
    this.followBottom = true;
    this.savedEvents.set({});
    this.saveReceipts.set({});
    this.saveProblems.set({});
    this.resetSaveReads();
    this.answerProblems.set({});
    this.expandedTools.set(new Set());
    this.expandedDetails.set(new Set());
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
    clearTimeout(this.focusTimer);
    this.focusController?.abort();
    this.focusController = null;
    this.focusBusy.set(false);
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
      // The page organization's qualified context, with its admission incarnation and token.
      const context = await firstValueFrom(
        this.catalog
          .organizationContext(this.context().allowedOrganizationId)
          .pipe(
            map((read) => read.context),
            // Another routine check can start while catalog context is in flight.
            // Wait for it; temporary dispatch suspension is not an owner change.
            switchMap((context) =>
              this.sessions.whenSettled().pipe(map(() => context)),
            ),
            takeUntil(fromEvent(controller.signal, "abort")),
          ),
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
  async newConversation(
    confirmedDraft?: string,
    isCurrent: () => boolean = () => true,
  ): Promise<void> {
    if (this.busy() || this.pending || !this.canCreate()) return;
    if (
      !this.pendingConversation &&
      !(confirmedDraft !== undefined && this.draft() === confirmedDraft) &&
      !this.allowConversationSwitch()
    )
      return;
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
      if (!isCurrent()) return;
      this.stopStream();
      this.followBottom = true;
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
      this.savedEvents.set({});
      this.saveReceipts.set({});
      this.saveProblems.set({});
      this.resetSaveReads();
      this.answerProblems.set({});
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
    this.followBottom = true;
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
    this.savedEvents.set({});
    this.saveReceipts.set({});
    this.saveProblems.set({});
    this.resetSaveReads();
    this.answerProblems.set({});
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
    if (this.selected()) {
      void this.readEarlierAnswers();
      await this.reconnect();
    }
  }

  /** Replays retained terminal answers only. It never creates or retries a turn. */
  private async readEarlierAnswers(): Promise<void> {
    const conversation = this.current();
    if (!conversation) return;
    const generation = this.generation;
    const scope = this.activeScope();
    const controller = new AbortController();
    this.transcriptController = controller;
    const turns = this.turns();
    const skipped = (turn: ChatTurn) =>
      turn.turnId === this.selected()?.turnId || turn.phase === "active";
    if (turns.every(skipped)) return;
    const stale = () =>
      controller.signal.aborted ||
      generation !== this.generation ||
      this.current()?.conversationId !== conversation.conversationId;
    const unreadable = (turnId: string) =>
      this.answerProblems.update((problems) => ({
        ...problems,
        [turnId]:
          "Couldn't read this saved answer. Select the question to reconnect.",
      }));
    try {
      // One admission covers the replay; every stream request still checks dispatch and
      // session generation itself, so an owner change mid-loop refuses the next read.
      await this.admitted(async (context, signal) => {
        for (const turn of turns) {
          if (skipped(turn)) continue;
          const events: ChatEvent[] = [];
          try {
            let bytes = 0;
            await this.api.stream(
              scope,
              context,
              AbortSignal.any([signal, controller.signal]),
              conversation.conversationId,
              turn.turnId,
              0,
              (event) => {
                bytes += new TextEncoder().encode(
                  JSON.stringify(event),
                ).byteLength;
                if (bytes > 4 * 1024 * 1024) throw new ChatError("bodyLimit");
                events.push(event);
              },
            );
            if (stale()) return;
          } catch {
            if (stale()) return;
            unreadable(turn.turnId);
          }
          this.savedEvents.update((saved) => ({
            ...saved,
            [turn.turnId]: events,
          }));
          await this.refreshSaves(turn);
        }
      });
    } catch {
      if (stale()) return;
      // Admission itself failed: no remaining saved answer was read.
      for (const turn of turns)
        if (!skipped(turn) && !(turn.turnId in this.savedEvents()))
          unreadable(turn.turnId);
    }
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
      this.problemAtComposer.set(true);
      this.problem.set(
        "The question exceeds the text limit or contains a NUL character.",
      );
      return;
    }
    this.followBottom = true;
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
    if (
      !this.pending ||
      this.busy() ||
      !this.contexts.canPresent(this.context()) ||
      !this.context().features.chat
    )
      return;
    const conversationId = this.pending.conversationId;
    const needsConversation = !this.current();
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
            "quotaTurnLimitReached",
            "quotaTokenLimitReached",
            "quotaTokenBoundUnavailable",
            "quotaCoverageUnavailable",
          ].includes(error.code)
        )
          this.pending = null;
        throw error;
      }
      this.pending = null;
      this.draft.set("");
      this.followBottom = true;
      this.rememberSelected();
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
    if (accepted) {
      if (needsConversation) await this.show(conversationId);
      else await this.reconnect();
    }
  }
  async selectTurn(turn: ChatTurn): Promise<void> {
    if (this.busy() || this.pending) return;
    this.rememberSelected();
    this.stopStream();
    this.selected.set(turn);
    this.events.set([]);
    this.lastSequence = 0;
    this.acceptedBytes = 0;
    this.answerProblems.update((problems) => {
      const next = { ...problems };
      delete next[turn.turnId];
      return next;
    });
    await this.reconnect();
  }
  async reconnectTurn(turn: ChatTurn): Promise<void> {
    if (this.busy() || this.pending) return;
    if (this.selected()?.turnId !== turn.turnId) {
      this.rememberSelected();
      this.stopStream();
      this.selected.set(turn);
      const events = this.savedEvents()[turn.turnId] ?? [];
      this.events.set(events);
      this.lastSequence = events.at(-1)?.sequence ?? 0;
      this.acceptedBytes = events.reduce(
        (bytes, event) =>
          bytes + new TextEncoder().encode(JSON.stringify(event)).byteLength,
        0,
      );
      this.answerProblems.update((problems) => {
        const next = { ...problems };
        delete next[turn.turnId];
        return next;
      });
    }
    await this.reconnect();
  }
  /** A confirmed owner deletion: drop the conversation locally without another read of its content. */
  conversationDeleted(id: string): void {
    if (this.current()?.conversationId !== id) return;
    this.stopStream();
    this.current.set(null);
    this.selected.set(null);
    this.events.set([]);
    this.hasEarlier.set(false);
    this.conversations.update((list) =>
      list.filter((v) => v.conversationId !== id),
    );
  }

  private stopStream(): void {
    this.transcriptController?.abort();
    this.transcriptController = null;
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
    this.problemAtComposer.set(false);
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
              if (value.type === "tool.completed" || terminal(value))
                void this.refreshProposals(selected);
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
      if (generation === this.generation && !controller.signal.aborted)
        await this.refreshSaves(selected);
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
  typeFocus(value: string): void {
    this.focusQuery.set(value);
    clearTimeout(this.focusTimer);
    this.focusTimer = setTimeout(() => void this.searchFocus(), 250);
  }
  /** A new query cancels the scan in flight; more continues the scan already shown. */
  async searchFocus(more = false): Promise<void> {
    if (this.scope()) return;
    clearTimeout(this.focusTimer);
    const query = this.focusQuery().trim().toLocaleLowerCase();
    if (more && this.focusBusy()) return;
    more &&= this.focusScan?.query === query;
    this.focusController?.abort();
    const controller = (this.focusController = new AbortController()),
      generation = this.generation;
    // A failed later page must not advance beyond matches that were never presented.
    const scan: NonNullable<RevisionChatComponent["focusScan"]> =
      more && this.focusScan
        ? { ...this.focusScan }
        : {
            query,
            projects: [],
            projectCursor: null,
            projectIndex: 0,
            mappingCursor: null,
            started: false,
          };
    this.focusBusy.set(true);
    this.focusNotice.set("Finding mappings…");
    try {
      const found: CatalogMapping[] = [];
      let requests = 0,
        done = false;
      await this.admitted(async (context, admittedSignal) => {
        const signal = AbortSignal.any([admittedSignal, controller.signal]);
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
      if (controller.signal.aborted || generation !== this.generation) return;
      this.focusMappings.update((old) => {
        // A new query keeps only the chosen mapping so its label stays named.
        const list = more
          ? old
          : old.filter((m) => m.scope.mappingId === this.newFocus()?.mappingId);
        return [
          ...list,
          ...found.filter(
            (mapping) =>
              !list.some((m) => m.scope.mappingId === mapping.scope.mappingId),
          ),
        ];
      });
      this.focusScan = scan;
      this.moreFocus.set(!done);
      this.focusNotice.set(
        !done
          ? "More projects and mappings remain. Show more mappings to continue from this page."
          : this.focusMappings().length
            ? "Select a mapping below."
            : "No readable mapping matched.",
      );
    } catch (error) {
      if (!controller.signal.aborted && generation === this.generation) {
        this.focusNotice.set("");
        this.showError(error);
      }
    } finally {
      if (this.focusController === controller) {
        this.focusController = null;
        this.focusBusy.set(false);
      }
    }
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
    this.problemAtComposer.set(false);
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
    this.problemCode.set(code);
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
        "This version's explanation and validation aren't saved yet. Opening the version generates them; refresh chat once they're saved.",
      profileUnavailable:
        "This model profile is no longer available. Refresh chat before choosing another profile.",
      turnActive:
        "This conversation already has an active answer. Reopen it to read that answer.",
      idempotencyConflict:
        "The original question conflicts with the saved request. It has not been replaced.",
      turnNotStarted:
        "The question did not start. Its text is retained; send it again only when you are ready.",
      quotaTurnLimitReached:
        "The AI turn limit has been reached. Your question is retained and has not started.",
      quotaTokenLimitReached:
        "The remaining token budget cannot cover this entire turn. Your question is retained and has not started.",
      quotaTokenBoundUnavailable:
        "This model profile cannot guarantee the configured hard token cap. Your question is retained and has not started.",
      quotaCoverageUnavailable:
        "Incomplete usage or unsettled reservations prevent quota admission. Your question is retained and has not started.",
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
