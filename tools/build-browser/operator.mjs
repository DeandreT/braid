// Real HTTP approvals/queues and ordinary compiler dispatch; no substituted mapping or compiler results.
import { runBrowserFixture } from "../browser-fixture/runtime.mjs";
import { randomUUID } from "node:crypto";
import { setTimeout as pause } from "node:timers/promises";

await runBrowserFixture({
  kind: "build",
  entrypoint: import.meta.url,
  fixtures: [
    [
      "engines/ferrule-host/tests/fixtures/valid-basic.json",
      "4220877674cba63c004226d487f5a80042369aab30350a964dc5fff5df48dc26",
      2191,
    ],
  ],
  create(fx) {
    const {
      check,
      guid,
      cfg,
      ids,
      fixtureId,
      provision,
      tool,
      initializeStorage,
      startHost,
      stopHost,
      settleWorkers,
      createProject,
      upload,
      request,
      rows,
      snapshot,
      equivalent,
      state,
      save,
      normalPgRestart,
      captureRegressionPublisher,
      changeRegressionPublisherRole,
    } = fx;
    const profiles = ["administrator", "publisher", "viewer"];
    let project, revision, original, evidenceId, caseId;
    let profile = "administrator";
    const captured = new Map(),
      approvals = new Map(),
      environments = new Map();
    const root = () => fx.mappingPath(revision);
    async function start(name) {
      profile = name;
      await startHost("read", { profile, regression: true, builds: true });
      await startHost("mutation", {
        profile: "administrator",
        regressionWorker: true,
        buildWorker: true,
      });
    }
    async function stop() {
      await stopHost("mutation");
      await stopHost("read");
      await settleWorkers(true);
    }
    function preserve(current) {
      for (const table of fx.tables.filter(
        (name) => !name.startsWith("build_"),
      ))
        check(
          equivalent(current.rows[table], original.rows[table]),
          "productFailure",
        );
      check(
        equivalent(current.physical, original.physical) &&
          equivalent(current.journal, original.journal) &&
          equivalent(current.credentials, original.credentials),
        "productFailure",
      );
      for (const value of current.rows.build_environments) {
        const key = value.environment_id + ":" + value.version,
          previous = environments.get(key);
        if (previous) {
          const { active: wasActive, ...before } = previous,
            { active, ...after } = value;
          check(
            equivalent(before, after) && (!active || wasActive),
            "productFailure",
          );
        }
        environments.set(key, value);
      }
      for (const value of current.rows.build_approvals) {
        const previous = approvals.get(value.approval_id);
        if (previous) {
          const { revoked: wasRevoked, ...before } = previous,
            { revoked, ...after } = value;
          check(
            equivalent(before, after) && (!wasRevoked || revoked),
            "productFailure",
          );
        }
        approvals.set(value.approval_id, value);
      }
      check(
        environments.size === current.rows.build_environments.length &&
          approvals.size === current.rows.build_approvals.length,
        "productFailure",
      );
      for (const [id, previous] of captured) {
        const run = current.rows.build_runs.filter(
          (value) => value.build_id === id,
        );
        const publication = current.rows.build_publications.filter(
          (value) => value.build_id === id,
        );
        check(
          run.length === 1 &&
            equivalent(run[0], previous.run) &&
            equivalent(publication, previous.publication),
          "productFailure",
        );
      }
    }
    function checkpoint() {
      const current = snapshot();
      preserve(current);
      state.observations.push({
        kind: "build-conservation",
        nativeReportsEvidenceAndCapturedArtifactsPreserved: true,
        capturedBuilds: captured.size,
      });
      save();
      return {
        outcome: "observed",
        capturedBuilds: captured.size,
        approvals: approvals.size,
        environmentVersions: environments.size,
      };
    }
    async function capture(buildId, sample) {
      guid(buildId);
      check(!captured.has(buildId));
      const end = Date.now() + fx.budget(120000);
      let response;
      while (true) {
        response = await request(
          fx.readHost,
          root() + "/build-runs/" + buildId,
        );
        check(
          response.status === 200 &&
            response.body.buildId === buildId &&
            equivalent(response.body.revision, revision),
          "productFailure",
        );
        if (response.body.state === sample) break;
        check(
          ["queued", "running"].includes(response.body.state) &&
            Date.now() < end,
          "productFailure",
        );
        await pause(150);
      }
      const current = snapshot();
      preserve(current);
      const run = current.rows.build_runs.filter(
        (value) => value.build_id === buildId,
      );
      const publication = current.rows.build_publications.filter(
        (value) => value.build_id === buildId,
      );
      const approval = current.rows.build_approvals.filter(
        (value) => value.build_id === buildId,
      );
      check(
        run.length === 1 &&
          approval.length === 1 &&
          run[0].actor_id === ids.publisher &&
          approval[0].actor_id === ids.publisher &&
          run[0].approval_id === approval[0].approval_id &&
          run[0].state === sample &&
          run[0].intent_digest === approval[0].intent_digest,
        "productFailure",
      );
      const intentBytes = Buffer.from(
          run[0].intent_body.replace(/^\\x/, ""),
          "hex",
        ),
        intent = JSON.parse(intentBytes.toString("utf8"));
      check(
        fx.sha(intentBytes) === run[0].intent_digest &&
          intent.buildId === buildId &&
          intent.approvalId === approval[0].approval_id &&
          intent.organizationId === revision.organizationId &&
          intent.projectId === revision.projectId &&
          intent.mappingId === revision.mappingId &&
          intent.revisionId === revision.revisionId &&
          intent.nativeDigest ===
            current.rows.mapping_revisions[0].native_digest &&
          intent.engineBuild === cfg.workerImage &&
          intent.regressionEvidenceId === evidenceId &&
          intent.boundaries.length === 2 &&
          intent.boundaries.every((value) => value.format === "json"),
        "productFailure",
      );
      if (sample === "succeeded") {
        check(
          run[0].attempt === 1 &&
            publication.length === 1 &&
            publication[0].state === 2 &&
            publication[0].artifact_body_sha256 === run[0].artifact_digest &&
            publication[0].artifact_digest === run[0].artifact_digest &&
            publication[0].artifact_body_bytes === run[0].artifact_bytes &&
            run[0].artifact_bytes > 1024 &&
            response.body.artifact.sha256 === run[0].artifact_digest,
          "productFailure",
        );
        check(
          fx.jsonSql(
            "SELECT to_json(get_byte(artifact_body,0)=77 AND get_byte(artifact_body,1)=90) FROM weft.build_publications WHERE organization_id='" +
              guid(revision.organizationId) +
              "' AND build_id='" +
              buildId +
              "'",
          ) === true,
          "productFailure",
        );
      } else
        check(
          sample === "canceled" &&
            run[0].attempt === 0 &&
            publication.length === 0 &&
            response.body.artifact === null,
          "productFailure",
        );
      captured.set(buildId, { run: run[0], publication });
      state.observations.push({
        kind: "actual-browser-build",
        buildId,
        sample,
        attempt: run[0].attempt,
        exactIntentSourceEvidenceAndPrivateArtifactVerified: true,
      });
      save();
      return {
        outcome: "captured",
        buildId,
        state: sample,
        artifactDigest: run[0].artifact_digest,
      };
    }
    async function prepareEvidence() {
      const capability = await request(
        fx.readHost,
        root() + "/regression-cases/capabilities",
      );
      check(
        capability.status === 200 && capability.body.publishCases,
        "productFailure",
      );
      caseId = randomUUID();
      evidenceId = randomUUID();
      const body = Buffer.from(
        JSON.stringify({
          name: "Actual build evidence",
          revisionId: revision.revisionId,
          runtime: capability.body.runtime,
          limits: capability.body.limits,
          comparisonPolicy: capability.body.comparisonPolicy,
          currentDateTime: "2026-10-20T12:00:00Z",
          target: { kind: "all", name: null },
          primaryBase64Url: Buffer.from(
            '{"code":"A1","a.b":"dot","nested":{"b":"x"}}',
          ).toString("base64url"),
          namedInputs: [],
          parameters: [],
          expected: [
            {
              name: null,
              bytesBase64Url: Buffer.from(
                '{\n  "code": "A1",\n  "dotted": "dot",\n  "nestedOutput": {\n    "b": "x"\n  }\n}\n',
              ).toString("base64url"),
            },
          ],
        }),
      );
      check(
        (
          await request(
            fx.readHost,
            root() + "/regression-cases/" + caseId + "/versions/1",
            {
              method: "PUT",
              body,
              media: "application/vnd.weft.regression-case.v1+json",
              unsafe: true,
            },
          )
        ).status === 201,
        "productFailure",
      );
      check(
        (
          await request(
            fx.readHost,
            root() + "/regression-runs/" + evidenceId,
            {
              method: "PUT",
              body: Buffer.from(JSON.stringify({ caseId, version: "1" })),
              media: "application/vnd.weft.regression-run.v1+json",
              unsafe: true,
            },
          )
        ).status === 201,
        "productFailure",
      );
      const end = Date.now() + fx.budget(60000);
      while (true) {
        const result = await request(
          fx.readHost,
          root() + "/regression-runs/" + evidenceId,
        );
        check(result.status === 200, "productFailure");
        if (result.body.run.state === "completed") {
          check(
            result.body.run.attempt === 1 &&
              result.body.previewStatus === "succeeded" &&
              result.body.comparison?.outcome === "passed",
            "productFailure",
          );
          break;
        }
        check(
          ["queued", "running"].includes(result.body.run.state) &&
            Date.now() < end,
          "productFailure",
        );
        await pause(150);
      }
    }
    return {
      profiles,
      async setup() {
        for (const name of profiles) provision(ids[name], ids.organization);
        tool([
          "set-project-creation-capability",
          ids.organization,
          ids.administrator,
          "true",
        ]);
        initializeStorage();
        try {
          const host = await startHost("mutation", {
            profile: "administrator",
            writer: true,
            reportMode: "full",
          });
          project = await createProject(
            host,
            "Actual approved build browser fixture",
          );
          const value = JSON.parse(fx.fixtures[0].bytes.toString("utf8"));
          value.source_options = { json_document: true };
          value.target_options = { json_document: true };
          const bytes = Buffer.from(JSON.stringify(value));
          revision = (
            await upload(host, project, { bytes, sha256: fx.sha(bytes) })
          ).scope;
          const projection = await request(
            host,
            root() +
              "/revisions/" +
              revision.revisionId +
              "/reports/projection/ensure",
            { method: "POST", unsafe: true },
          );
          check(
            projection.status === 200 &&
              projection.body.state === "available" &&
              projection.body.report?.document?.coverage?.status === "complete",
            "productFailure",
          );
          for (const name of ["publisher", "viewer"])
            provision(ids[name], ids.organization, project.projectId, name);
        } finally {
          await stopHost("mutation");
          await settleWorkers(true);
        }
        await start("publisher");
        await prepareEvidence();
        await stop();
        captureRegressionPublisher(project.projectId);
        await start("administrator");
        original = snapshot();
        return {
          schemaVersion: "weft.build-browser-fixture.v1",
          fixtureId,
          source: { commit: cfg.source.commit, tree: cfg.source.tree },
          readOrigin: cfg.readOrigin,
          initialReadProfile: "administrator",
          organizationId: ids.organization,
          revision,
          evidenceId,
          caseId,
        };
      },
      validateControl(bytes) {
        check(bytes.length <= 1024, "invalidControl");
        let value;
        try {
          value = JSON.parse(bytes);
        } catch {
          throw new fx.Failure("invalidControl");
        }
        check(
          value &&
            typeof value === "object" &&
            !Array.isArray(value) &&
            value.fixtureId === fixtureId,
          "invalidControl",
        );
        guid(value.requestNonce);
        const fields = ["fixtureId", "requestNonce", "action"];
        if (value.action === "capture-build") {
          fields.push("buildId", "sample");
          guid(value.buildId);
          check(
            ["succeeded", "canceled"].includes(value.sample),
            "invalidControl",
          );
        } else if (value.action === "restart-profile") {
          fields.push("profile");
          check(profiles.includes(value.profile), "invalidControl");
        } else
          check(
            [
              "checkpoint",
              "restart",
              "pause-worker",
              "resume-worker",
              "revoke-publisher",
              "restore-publisher",
              "shutdown",
            ].includes(value.action),
            "invalidControl",
          );
        check(
          Object.keys(value).sort().join("|") === fields.sort().join("|"),
          "invalidControl",
        );
        return value;
      },
      maximum(action) {
        return ["restart", "restart-profile", "pause-worker"].includes(action)
          ? 360000
          : action === "capture-build"
            ? 130000
            : 60000;
      },
      async dispatch(item) {
        if (item.action === "capture-build")
          return await capture(item.buildId, item.sample);
        if (item.action === "checkpoint") return checkpoint();
        if (item.action === "pause-worker") {
          checkpoint();
          await stopHost("mutation");
          await settleWorkers(true);
          return { outcome: "paused" };
        }
        if (item.action === "resume-worker") {
          check(fx.mutationHost === null, "invalidControl");
          await startHost("mutation", {
            profile: "administrator",
            regressionWorker: true,
            buildWorker: true,
          });
          return { outcome: "ready" };
        }
        if (["restart", "restart-profile"].includes(item.action)) {
          checkpoint();
          await stop();
          if (item.action === "restart") await normalPgRestart();
          await start(item.action === "restart" ? profile : item.profile);
          checkpoint();
          return { outcome: "restarted", profile };
        }
        if (["revoke-publisher", "restore-publisher"].includes(item.action)) {
          checkpoint();
          await stopHost("mutation");
          changeRegressionPublisherRole(
            item.action === "revoke-publisher" ? "viewer" : "publisher",
          );
          original.rows.project_memberships = rows().project_memberships;
          await startHost("mutation", {
            profile: "administrator",
            regressionWorker: true,
            buildWorker: true,
          });
          checkpoint();
          return {
            outcome:
              item.action === "revoke-publisher" ? "revoked" : "restored",
          };
        }
        throw new fx.Failure("invalidControl");
      },
      async beforeCleanup() {
        if (original) checkpoint();
      },
    };
  },
});
