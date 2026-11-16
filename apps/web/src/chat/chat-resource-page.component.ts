import { Component, inject, signal } from "@angular/core";
import { ActivatedRoute, RouterLink } from "@angular/router";
import { takeUntilDestroyed } from "@angular/core/rxjs-interop";
import { firstValueFrom, from } from "rxjs";
import { CatalogApi } from "../catalog/catalog-api";
import { createCatalogRead } from "../catalog/catalog-state";
import { CatalogStatusComponent } from "../catalog/catalog-shell.component";
import {
  catalogRoutes,
  isCatalogId,
  type CatalogContext,
  type CatalogMappingScope,
  type CatalogRevisionScope,
} from "../catalog/catalog-types";
import { PreviewApi } from "../catalog/previews/preview-api";
import { RegressionApi } from "../catalog/regression/regression-api";
import { BuildApi } from "../catalog/builds/build-api";
import {
  workspaceCitation,
  type WorkspaceCitation,
} from "./workspace-chat-types";
import { ChatError } from "./chat-types";

type ResourceView = {
  title: string;
  rows: [string, string][];
  location: string;
  locationLabel: string;
};

/** Citation reads use existing typed, freshly authorized app clients. No model requests or payload display. */
@Component({
  selector: "braid-chat-resource-page",
  imports: [RouterLink, CatalogStatusComponent],
  template: `<section class="chat-resource-page">
    <p>Workspace reference</p>
    <h1>Resource details</h1>
    <p>
      This read-only view rechecks your current access. Opening a citation does
      not send anything to a model. Sample payloads are not displayed here.
    </p>
    @if (read.state(); as state) {
      @if (state.kind === "ready") {
        <h2>{{ state.data.title }}</h2>
        <dl>
          @for (row of state.data.rows; track row[0]) {
            <dt>{{ row[0] }}</dt>
            <dd>{{ row[1] }}</dd>
          }
        </dl>
        <a [routerLink]="state.data.location">{{ state.data.locationLabel }}</a>
      } @else {
        <braid-catalog-status [kind]="state.kind" (retry)="read.retry()" />
      }
    }
    <p>
      <a
        routerLink="/chat"
        [queryParams]="
          returnConversation() ? { conversationId: returnConversation() } : {}
        "
        >{{ returnConversation() ? "Return to conversation" : "Open chat" }}</a
      >
    </p>
  </section>`,
  styles: `
    :host {
      display: block;
    }
    .chat-resource-page {
      max-width: 64rem;
      margin: 0 auto;
      padding: 16px;
      color: var(--text);
    }
    .chat-resource-page h1 {
      font-size: 2rem;
    }
    dl {
      display: grid;
      grid-template-columns: minmax(8rem, 1fr) minmax(0, 3fr);
      gap: 0.75rem;
      padding: 12px 16px;
      background: var(--surface-muted-20);
      border-radius: 4px;
    }
    dt {
      font-weight: 600;
    }
    dd {
      margin: 0;
      overflow-wrap: anywhere;
    }
    a {
      color: var(--text-accent-4);
    }
    @media (max-width: 600px) {
      dl {
        grid-template-columns: 1fr;
        gap: 0.4rem;
      }
      dd {
        margin-bottom: 1rem;
      }
    }
  `,
})
export class ChatResourcePageComponent {
  readonly returnConversation = signal<string | null>(null);
  readonly read = createCatalogRead<ResourceView>();
  private readonly catalog = inject(CatalogApi);
  private readonly previews = inject(PreviewApi);
  private readonly regressions = inject(RegressionApi);
  private readonly builds = inject(BuildApi);
  constructor() {
    inject(ActivatedRoute)
      .queryParamMap.pipe(takeUntilDestroyed())
      .subscribe((query) => {
        this.returnConversation.set(null);
        this.read.select(null, (context) => {
          const keys = [
            "kind",
            "organizationId",
            "projectId",
            "mappingId",
            "revisionId",
            "subjectId",
            "version",
            "conversationId",
          ];
          const c: unknown = {
            kind: query.get("kind"),
            organizationId: query.get("organizationId"),
            projectId: query.get("projectId"),
            mappingId: query.get("mappingId"),
            revisionId: query.get("revisionId"),
            subjectId: query.get("subjectId"),
            version: query.get("version"),
            target: null,
            reportDerivationId: null,
            sectionId: null,
          };
          if (
            query.keys.some(
              (key) => !keys.includes(key) || query.getAll(key).length !== 1,
            ) ||
            (query.has("conversationId") &&
              !isCatalogId(query.get("conversationId"))) ||
            !workspaceCitation(c, context.allowedOrganizationId) ||
            ![
              "previewRun",
              "regressionCase",
              "regressionRun",
              "buildRun",
              "buildApproval",
              "buildEnvironment",
            ].includes(c.kind)
          )
            throw new ChatError("invalidRequest");
          this.returnConversation.set(query.get("conversationId"));
          return from(this.resource(context, c));
        });
      });
  }
  private async resource(
    context: CatalogContext,
    c: WorkspaceCitation,
  ): Promise<ResourceView> {
    const project = {
      organizationId: context.allowedOrganizationId,
      projectId: c.projectId!,
    };
    if (c.kind === "buildEnvironment") {
      const p = await firstValueFrom(this.catalog.project(project));
      const environment = await firstValueFrom(
        this.builds.environment(context, project, c.subjectId!, c.version!),
      );
      return {
        title: `Build environment · ${p.name}`,
        rows: [
          ["Environment", environment.reference.environmentId],
          ["Version", environment.reference.version],
          ["Status", environment.active ? "Active" : "Superseded"],
          [
            "Runtime",
            `${environment.profile.runtime} ${environment.profile.runtimeVersion}`,
          ],
          [
            "Profile",
            `${environment.profile.id} · ${environment.profile.version}`,
          ],
          ["Created", environment.createdAtUtc],
        ],
        location: catalogRoutes.project(project),
        locationLabel: "Open project",
      };
    }
    const mapping: CatalogMappingScope = {
      ...project,
      mappingId: c.mappingId!,
    };
    const m = await firstValueFrom(this.catalog.mapping(mapping));
    const result = (
      title: string,
      rows: [string, string][],
      revision?: CatalogRevisionScope,
    ): ResourceView => ({
      title: `${title} · ${m.name}`,
      rows,
      location: revision
        ? catalogRoutes.revision(revision)
        : catalogRoutes.mapping(mapping),
      locationLabel: revision ? "Open exact revision" : "Open mapping",
    });
    if (c.kind === "previewRun") {
      const revision: CatalogRevisionScope = {
        ...mapping,
        revisionId: c.revisionId!,
      };
      const run = await firstValueFrom(
        this.previews.read(revision, c.subjectId!),
      );
      return result(
        "Preview",
        [
          ["Run", run.runId],
          ["State", run.state],
          ["Submitted", run.createdAtUtc],
          ["Outputs", String(run.outputs.length)],
        ],
        revision,
      );
    }
    if (c.kind === "regressionCase") {
      const capabilities = await firstValueFrom(
        this.regressions.capabilities(context, mapping),
      );
      if (capabilities.readSamples) {
        const value = await firstValueFrom(
          this.regressions.case(context, mapping, c.subjectId!, c.version!),
        );
        // Retain only metadata from the existing authorized case route, not its replay documents.
        return result(
          `Regression case: ${value.definition.name}`,
          [
            ["Case", value.caseId],
            ["Version", value.version],
            ["Comparison policy", value.definition.comparisonPolicy],
          ],
          value.revision,
        );
      }
      let after: string | undefined;
      for (let page = 0; page < 20; page++) {
        const rows = await firstValueFrom(
          this.regressions.cases(context, mapping, after),
        );
        const value = rows.cases.find(
          (row) => row.caseId === c.subjectId && row.version === c.version,
        );
        if (value)
          return result(
            `Regression case: ${value.name}`,
            [
              ["Case", value.caseId],
              ["Version", value.version],
              ["Created", value.createdAtUtc],
              ["Data policy", "Metadata only; sample data was not requested."],
            ],
            value.revision,
          );
        if (!rows.nextCaseId) break;
        after = rows.nextCaseId;
      }
      throw new ChatError("notFound");
    }
    if (c.kind === "regressionRun") {
      const value = await firstValueFrom(
        this.regressions.run(context, mapping, c.subjectId!),
      );
      return result(
        "Regression run",
        [
          ["Run", value.run.runId],
          ["State", value.run.state],
          ["Case version", value.run.version],
          ["Execution", value.executionOutcome ?? "Not available"],
          ["Comparison", value.comparison?.outcome ?? "Not available"],
        ],
        value.run.revision,
      );
    }
    if (c.kind === "buildApproval") {
      const value = await firstValueFrom(
        this.builds.approval(context, mapping, c.subjectId!),
      );
      return result(
        "Build approval",
        [
          ["Approval", value.approvalId],
          ["Status", value.revoked ? "Revoked" : "Approved"],
          ["Build", value.buildId],
          [
            "Environment",
            `${value.environment.environmentId} · ${value.environment.version}`,
          ],
          ["Created", value.createdAtUtc],
        ],
        value.revision,
      );
    }
    const value = await firstValueFrom(
      this.builds.run(context, mapping, c.subjectId!),
    );
    return result(
      "Build",
      [
        ["Build", value.buildId],
        ["State", value.state],
        ["Outcome", value.outcome ?? "Not available"],
        [
          "Artifact",
          value.artifact
            ? `${value.artifact.format} · ${value.artifact.byteCount} bytes`
            : "Not available",
        ],
        ["Created", value.createdAtUtc],
      ],
      value.revision,
    );
  }
}
