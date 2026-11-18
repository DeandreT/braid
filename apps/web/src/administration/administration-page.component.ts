import { OrganizationSelection } from "../catalog/organization-selection";
import { RouterLink } from "@angular/router";
import { DOCUMENT, DatePipe, JsonPipe } from "@angular/common";
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
import { finalize, firstValueFrom, type Subscription } from "rxjs";
import { CatalogContextStore } from "../catalog/catalog-context";
import { CatalogApi } from "../catalog/catalog-api";
import {
  catalogOwner,
  isCatalogId,
  type CatalogContext,
  type CatalogProject,
} from "../catalog/catalog-types";
import { UnsentWork } from "../workbench/unsent-work";
import { BrowserSessionState } from "../auth/browser-session-state";
import {
  AdministrationApi,
  AdministrationFailure,
  type AdministrationSnapshot,
  type OrganizationAdministration,
  type ProjectAdministration,
} from "./administration-api";
import { isInvitationPath } from "./invitation-path";

type AdministrationForm = "organization" | "project" | "invitation" | "grant";
const formKinds: AdministrationForm[] = [
  "organization",
  "project",
  "invitation",
  "grant",
];

@Component({
  selector: "braid-administration-page",
  imports: [RouterLink, FormsModule, DatePipe, JsonPipe],
  templateUrl: "./administration-page.component.html",
  styleUrl: "./administration-page.component.css",
  host: { "(input)": "draftChanged()", "(change)": "draftChanged()" },
})
export class AdministrationPageComponent {
  readonly selection = inject(OrganizationSelection);
  readonly contexts = inject(CatalogContextStore);
  private readonly api = inject(AdministrationApi);
  private readonly document = inject(DOCUMENT);
  private readonly lifetime = inject(DestroyRef);
  private readonly sessions = inject(BrowserSessionState);
  readonly organization =
    signal<AdministrationSnapshot<OrganizationAdministration> | null>(null);
  readonly project =
    signal<AdministrationSnapshot<ProjectAdministration> | null>(null);
  readonly busy = signal(false);
  readonly message = signal("");
  readonly invitationLink = signal<string | null>(null);
  readonly roles = ["viewer", "runner", "publisher", "administrator"];
  readonly invitationRoles = signal<{ projectId: string; role: string }[]>([]);
  readonly adminProjects = signal<CatalogProject[] | null>(null);
  readonly adminProjectsError = signal(false);
  private adminProjectsRead: Subscription | null = null;
  private readonly catalog = inject(CatalogApi);
  private owner: CatalogContext | null = null;
  private retainedOwner: string | null = null;
  private readonly admitted = signal<CatalogContext | null>(null);
  readonly presented = computed(
    () =>
      this.contexts.isCurrent(this.admitted()) &&
      this.contexts.canPresent(this.admitted()),
  );
  private loadedProjectId: string | null = null;
  private generation = 0;
  private readonly formChanges = signal(0);
  private readonly savedForms = signal<
    Partial<Record<AdministrationForm, string>>
  >({});
  private readonly mutationPending = signal(false);
  private readonly rowRoles = signal<ReadonlyMap<string, string>>(new Map());
  projectId = "";
  principalId = "";
  memberRole = "member";
  projectPrincipalId = "";
  projectRole = "viewer";
  tenantId = "";
  expectedObjectId = "";
  invitationProjectId = "";
  invitationRole = "viewer";
  machineTenantId = "";
  machineObjectId = "";
  targetId = "";

  constructor() {
    this.saveFormBaselines();
    inject(UnsentWork).track(() => {
      this.formChanges();
      const saved = this.savedForms();
      return (
        this.mutationPending() ||
        this.invitationLink() !== null ||
        this.rowRoles().size > 0 ||
        formKinds.some(
          (kind) =>
            this.hasFormWork(kind) && this.formSignature(kind) !== saved[kind],
        )
      );
    }, this.lifetime);
    this.contexts.invalidated
      .pipe(takeUntilDestroyed())
      .subscribe(() => this.suspend());
    effect(() => {
      const context = this.contexts.context();
      const ended =
        this.sessions.state().kind === "disabled" ||
        this.sessions.action().kind === "logoutAcknowledged";
      if (
        ended ||
        (context &&
          this.retainedOwner !== null &&
          catalogOwner(context) !== this.retainedOwner)
      ) {
        untracked(() => {
          this.owner = null;
          this.admitted.set(null);
          this.organization.set(null);
          this.project.set(null);
          this.cancelAdminProjects();
          this.loadedProjectId = null;
          this.clearDrafts();
          if (ended) this.retainedOwner = null;
        });
      }
    });
    this.lifetime.onDestroy(() => this.clear());
    void this.refresh();
  }

  draftChanged(): void {
    this.formChanges.update((value) => value + 1);
  }

  private rowKey(kind: "organization" | "project", principal: string): string {
    return `${kind}:${kind === "project" ? this.loadedProjectId : ""}:${principal}`;
  }

  rowRole(
    kind: "organization" | "project",
    principal: string,
    original: string,
  ): string {
    return this.rowRoles().get(this.rowKey(kind, principal)) ?? original;
  }

  changeRowRole(
    kind: "organization" | "project",
    principal: string,
    original: string,
    value: string,
  ): void {
    const roles = new Map(this.rowRoles());
    const key = this.rowKey(kind, principal);
    if (value === original) roles.delete(key);
    else roles.set(key, value);
    this.rowRoles.set(roles);
  }

  private submittedRow(
    kind: "organization" | "project",
    principal: string,
    role: string,
  ): { key: string; value: string } | undefined {
    const key = this.rowKey(kind, principal);
    return this.rowRoles().get(key) === role ? { key, value: role } : undefined;
  }

  private formSignature(kind: AdministrationForm): string {
    return JSON.stringify(
      kind === "organization"
        ? [this.principalId, this.memberRole]
        : kind === "project"
          ? [this.projectPrincipalId, this.projectRole]
          : kind === "grant"
            ? [this.targetId, this.machineTenantId, this.machineObjectId]
            : [
                this.tenantId,
                this.expectedObjectId,
                this.invitationProjectId,
                this.invitationRole,
                this.invitationRoles(),
              ],
    );
  }

  private hasFormWork(kind: AdministrationForm): boolean {
    if (kind === "organization")
      return this.principalId.length > 0 || this.memberRole !== "member";
    if (kind === "project")
      return (
        this.projectPrincipalId.length > 0 || this.projectRole !== "viewer"
      );
    if (kind === "grant")
      return [this.targetId, this.machineTenantId, this.machineObjectId].some(
        (value) => value.length > 0,
      );
    return (
      [this.tenantId, this.expectedObjectId, this.invitationProjectId].some(
        (value) => value.length > 0,
      ) ||
      this.invitationRole !== "viewer" ||
      this.invitationRoles().length > 0
    );
  }

  private saveFormBaselines(): void {
    this.savedForms.set(
      Object.fromEntries(
        formKinds.map((kind) => [kind, this.formSignature(kind)]),
      ),
    );
  }

  async refresh(): Promise<void> {
    if (this.busy()) return;
    const generation = ++this.generation;
    this.busy.set(true);
    this.message.set("");
    try {
      const context = await firstValueFrom(this.contexts.refresh());
      if (generation !== this.generation) return;
      const identity = catalogOwner(context);
      if (this.retainedOwner !== null && identity !== this.retainedOwner)
        this.clearDrafts();
      this.retainedOwner = identity;
      this.owner = context;
      this.admitted.set(context);
      if (!this.adminProjects()) {
        // A read started under the previous generation would be discarded.
        this.adminProjectsRead?.unsubscribe();
        this.loadAdminProjects();
      }
      await this.load(context, generation);
    } catch {
      if (generation === this.generation)
        this.message.set(
          "Administration is unavailable. Sign in and refresh your access.",
        );
    } finally {
      if (generation === this.generation) this.busy.set(false);
    }
  }
  /** Reads the first catalog page with each admitted access read until a list is shown. */
  loadAdminProjects(): void {
    const context = this.admitted();
    if (!context || !this.presented() || this.adminProjectsRead) return;
    const generation = this.generation;
    // Only the owner who asked may see the list; a later owner or session drops it.
    const current = () =>
      generation === this.generation &&
      this.contexts.isCurrent(context) &&
      this.contexts.canPresent(context);
    this.adminProjectsError.set(false);
    this.adminProjectsRead = this.catalog
      .projects(context.allowedOrganizationId)
      .pipe(
        finalize(() => (this.adminProjectsRead = null)),
        takeUntilDestroyed(this.lifetime),
      )
      .subscribe({
        next: (page) => {
          if (!current()) return;
          this.adminProjects.set(
            page.projects.filter(
              (item) => item.access.role === "administrator",
            ),
          );
        },
        error: () => {
          if (current()) this.adminProjectsError.set(true);
        },
      });
  }
  private cancelAdminProjects(): void {
    this.adminProjectsRead?.unsubscribe();
    this.adminProjectsRead = null;
    this.adminProjects.set(null);
    this.adminProjectsError.set(false);
  }
  async manageProject(id: string): Promise<void> {
    this.projectId = id;
    this.draftChanged();
    await this.selectProject();
  }
  async selectProject(): Promise<void> {
    if (!isCatalogId(this.projectId)) {
      this.message.set("Enter a canonical project ID from the catalog.");
      return;
    }
    await this.refresh();
  }
  organizationMember(id: string, active: boolean, role: string): void {
    if (!isCatalogId(id)) {
      this.message.set("Enter a principal ID.");
      return;
    }
    void this.mutate(
      null,
      `/members/${id}`,
      "PUT",
      { active, role },
      active && id === this.principalId && role === this.memberRole
        ? "organization"
        : undefined,
      this.submittedRow("organization", id, role),
    );
  }
  projectMember(id: string, active: boolean, role: string): void {
    if (!isCatalogId(id)) {
      this.message.set("Enter an admitted principal ID.");
      return;
    }
    if (this.loadedProjectId)
      void this.mutate(
        this.loadedProjectId,
        `/members/${id}`,
        "PUT",
        {
          active,
          role,
        },
        active && id === this.projectPrincipalId && role === this.projectRole
          ? "project"
          : undefined,
        this.submittedRow("project", id, role),
      );
  }
  payload(id: string, active: boolean): void {
    if (this.loadedProjectId)
      void this.mutate(
        this.loadedProjectId,
        `/members/${id}/payload-read`,
        "PUT",
        { active },
      );
  }
  disablePrincipal(id: string): void {
    void this.mutate(null, `/principals/${id}/disable`, "POST", null);
  }
  disableBinding(id: string): void {
    void this.mutate(null, `/bindings/${id}/disable`, "POST", null);
  }
  revokeInvitation(id: string): void {
    void this.mutate(null, `/invitations/${id}/revoke`, "POST", null);
  }
  revokeGrant(id: string): void {
    if (this.loadedProjectId)
      void this.mutate(
        this.loadedProjectId,
        `/invocation-grants/${id}/revoke`,
        "POST",
        null,
      );
  }
  addInvitationRole(): void {
    if (
      !isCatalogId(this.invitationProjectId) ||
      this.invitationRoles().some(
        (r) => r.projectId === this.invitationProjectId,
      ) ||
      this.invitationRoles().length >= 32
    ) {
      this.message.set("Enter a distinct project ID you administer.");
      return;
    }
    this.invitationRoles.update((roles) => [
      ...roles,
      { projectId: this.invitationProjectId, role: this.invitationRole },
    ]);
    this.invitationProjectId = "";
  }
  invite(): void {
    if (
      !isCatalogId(this.tenantId) ||
      (this.expectedObjectId && !isCatalogId(this.expectedObjectId))
    ) {
      this.message.set("Enter the fixed Entra tenant and optional object ID.");
      return;
    }
    void this.mutate(
      null,
      "/invitations",
      "POST",
      {
        invitationId: crypto.randomUUID(),
        tenantId: this.tenantId,
        expectedObjectId: this.expectedObjectId || null,
        projectRoles: this.invitationRoles(),
        expiresAt: new Date(Date.now() + 24 * 60 * 60 * 1000).toISOString(),
      },
      "invitation",
    );
  }
  grant(): void {
    if (
      ![this.targetId, this.machineTenantId, this.machineObjectId].every(
        isCatalogId,
      )
    ) {
      this.message.set("Enter the target, tenant and machine object IDs.");
      return;
    }
    if (this.loadedProjectId)
      void this.mutate(
        this.loadedProjectId,
        `/invocation-grants/${crypto.randomUUID()}`,
        "PUT",
        {
          targetId: this.targetId,
          tenantId: this.machineTenantId,
          objectId: this.machineObjectId,
        },
        "grant",
      );
  }

  private async mutate(
    project: string | null,
    suffix: string,
    method: "POST" | "PUT",
    body: unknown,
    draft?: AdministrationForm,
    row?: { key: string; value: string },
  ): Promise<void> {
    const owner = this.owner;
    const snapshot = project ? this.project() : this.organization();
    if (!owner || !snapshot || this.busy() || !this.contexts.canSend(owner))
      return;
    const generation = this.generation;
    const submittedForm = draft ? this.formSignature(draft) : null;
    this.busy.set(true);
    this.mutationPending.set(true);
    this.message.set("");
    try {
      const result = await firstValueFrom(
        this.api.write(owner, project, suffix, method, snapshot.etag, body),
      );
      if (
        generation !== this.generation ||
        !this.contexts.isCurrent(owner) ||
        !this.contexts.canPresent(owner)
      )
        return;
      if (draft && submittedForm !== null)
        this.savedForms.update((saved) => ({
          ...saved,
          [draft]: submittedForm,
        }));
      if (row && this.rowRoles().get(row.key) === row.value) {
        const roles = new Map(this.rowRoles());
        roles.delete(row.key);
        this.rowRoles.set(roles);
      }
      if (
        suffix === "/invitations" &&
        result &&
        typeof result === "object" &&
        "redemptionPath" in result &&
        isInvitationPath(result.redemptionPath, owner.allowedOrganizationId)
      )
        this.invitationLink.set(
          new URL(result.redemptionPath, this.document.location.origin).href,
        );
      this.message.set("Change saved.");
      await this.load(owner, generation);
    } catch (error) {
      if (generation !== this.generation) return;
      this.organization.set(null);
      this.project.set(null);
      this.message.set(
        error instanceof AdministrationFailure &&
          error.code === "lastAdministrator"
          ? "The last administrator cannot be removed, suspended, disabled or demoted. Add another administrator first."
          : error instanceof AdministrationFailure &&
              error.code === "preconditionFailed"
            ? "Administration changed since you opened it. Refresh and review the current state before editing."
            : "The change was not confirmed. It may already have committed. Refresh and review the state; don't resend it automatically.",
      );
    } finally {
      if (generation === this.generation) {
        this.busy.set(false);
        this.mutationPending.set(false);
      }
    }
  }
  private async load(owner: CatalogContext, generation: number): Promise<void> {
    const projectId = this.projectId;
    try {
      const organization = await firstValueFrom(this.api.organization(owner));
      if (
        generation === this.generation &&
        this.contexts.isCurrent(owner) &&
        this.contexts.canPresent(owner)
      )
        this.organization.set(organization);
    } catch {
      if (generation === this.generation) this.organization.set(null);
    }
    if (isCatalogId(projectId)) {
      try {
        const project = await firstValueFrom(
          this.api.project(owner, projectId),
        );
        if (
          generation === this.generation &&
          this.contexts.isCurrent(owner) &&
          this.contexts.canPresent(owner)
        ) {
          this.project.set(project);
          this.loadedProjectId = projectId;
        }
      } catch {
        if (generation === this.generation) {
          this.project.set(null);
          this.loadedProjectId = null;
          this.message.set(
            "This project is unavailable or you aren't its administrator.",
          );
        }
      }
    } else {
      this.project.set(null);
      this.loadedProjectId = null;
    }
  }
  private suspend(): void {
    ++this.generation;
    this.owner = null;
    this.admitted.set(null);
    this.loadedProjectId = null;
    this.organization.set(null);
    this.project.set(null);
    this.cancelAdminProjects();
    this.busy.set(false);
    this.mutationPending.set(false);
    this.message.set("");
  }

  private clearDrafts(): void {
    this.invitationLink.set(null);
    this.invitationRoles.set([]);
    this.rowRoles.set(new Map());
    this.projectId = "";
    this.principalId = "";
    this.projectPrincipalId = "";
    this.tenantId = "";
    this.expectedObjectId = "";
    this.invitationProjectId = "";
    this.memberRole = "member";
    this.projectRole = "viewer";
    this.invitationRole = "viewer";
    this.machineTenantId = "";
    this.machineObjectId = "";
    this.targetId = "";
    this.saveFormBaselines();
  }

  private clear(): void {
    this.suspend();
    this.clearDrafts();
    this.retainedOwner = null;
  }
}
