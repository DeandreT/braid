// Real regression API, immutable cases and the ordinary Weft.Worker process. No scripted execution output.
import { runBrowserFixture } from "../browser-fixture/runtime.mjs";
import { setTimeout as pause } from "node:timers/promises";

await runBrowserFixture({
  kind: "regression",
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
      fixtures,
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
    const profiles = ["administrator", "publisher", "runner", "viewer"];
    let project, revision, original;
    let profile = "publisher";
    const captures = new Map();
    const caseVersions = new Map();
    const cases = new Map();
    function preserveCases(current) {
      for (const row of current.rows.regression_cases) {
        const previous = cases.get(row.case_id);
        if (previous) {
          const { head_version: before, ...old } = previous;
          const { head_version: after, ...next } = row;
          check(
            equivalent(old, next) && BigInt(after) >= BigInt(before),
            "productFailure",
          );
        }
        cases.set(row.case_id, row);
      }
      for (const row of current.rows.regression_case_versions) {
        const key = row.case_id + ":" + row.version;
        if (caseVersions.has(key))
          check(equivalent(row, caseVersions.get(key)), "productFailure");
        caseVersions.set(key, row);
      }
      check(
        current.rows.regression_cases.length === cases.size &&
          current.rows.regression_case_versions.length === caseVersions.size,
        "productFailure",
      );
    }
    function nativeStable() {
      const current = snapshot();
      for (const table of fx.tables.filter(
        (table) => !table.startsWith("regression_"),
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
      return current;
    }
    async function start(profileName) {
      profile = profileName;
      await startHost("read", { profile, regression: true });
      await startHost("mutation", {
        profile: "administrator",
        regressionWorker: true,
      });
    }
    async function stop() {
      await stopHost("mutation");
      await stopHost("read");
      await settleWorkers(true);
    }
    async function capture(sample, runId) {
      check(
        ["passed", "failed", "notEvaluated"].includes(sample) &&
          !captures.has(runId),
      );
      guid(runId);
      const path = fx.mappingPath(revision) + "/regression-runs/" + runId;
      const end = Date.now() + fx.budget(60000);
      let value;
      while (true) {
        const response = await request(fx.readHost, path);
        check(
          response.status === 200 && response.body.run.runId === runId,
          "productFailure",
        );
        value = response.body;
        if (value.run.state === "completed") break;
        check(
          ["queued", "running"].includes(value.run.state) && Date.now() < end,
          "productFailure",
        );
        await pause(100);
      }
      check(
        value.executionOutcome === "completed" &&
          value.comparison?.outcome === sample &&
          equivalent(value.run.revision, revision),
        "productFailure",
      );
      const row = rows().regression_runs.filter(
        (row) =>
          row.run_id === runId && row.organization_id === ids.organization,
      );
      check(
        row.length === 1 &&
          row[0].state === "completed" &&
          row[0].attempt === 1 &&
          row[0].submitter_id === ids.publisher,
        "productFailure",
      );
      if (sample === "passed")
        check(
          value.previewStatus === "succeeded" &&
            value.comparison.totalDifferences === 0 &&
            value.outputs.length === 1,
          "productFailure",
        );
      if (sample === "failed")
        check(
          value.previewStatus === "succeeded" &&
            value.comparison.totalDifferences > 0,
          "productFailure",
        );
      if (sample === "notEvaluated")
        check(
          value.previewStatus !== "succeeded" &&
            value.comparison.notEvaluatedReason === "executionUnsuccessful",
          "productFailure",
        );
      captures.set(runId, { sample, row: row[0] });
      preserveCases(nativeStable());
      state.observations.push({
        kind: "actual-regression-run",
        runId,
        comparison: sample,
        attempt: 1,
        currentAuthorityAndNativePreserved: true,
      });
      save();
      return { outcome: "captured", runId, comparison: sample };
    }
    function checkpoint() {
      const current = nativeStable();
      preserveCases(current);
      for (const [id, captured] of captures) {
        const row = current.rows.regression_runs.filter(
          (row) => row.run_id === id,
        );
        check(
          row.length === 1 && equivalent(row[0], captured.row),
          "productFailure",
        );
      }
      return {
        outcome: "observed",
        caseVersions: current.rows.regression_case_versions.length,
        runs: current.rows.regression_runs.length,
        capturedRuns: captures.size,
        workerCreates: fx.workerCreates(),
      };
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
          });
          project = await createProject(
            host,
            "Actual regression browser fixture",
          );
          revision = (await upload(host, project, fixtures[0])).scope;
          for (const name of ["publisher", "runner", "viewer"])
            provision(ids[name], ids.organization, project.projectId, name);
        } finally {
          await stopHost("mutation");
          await settleWorkers(true);
        }
        captureRegressionPublisher(project.projectId);
        await start("publisher");
        original = snapshot();
        return {
          schemaVersion: "weft.regression-browser-fixture.v1",
          fixtureId,
          source: { commit: cfg.source.commit, tree: cfg.source.tree },
          readOrigin: cfg.readOrigin,
          initialReadProfile: "publisher",
          organizationId: ids.organization,
          revision,
          profiles: Object.fromEntries(
            profiles.map((name) => [
              name,
              {
                actorPrincipalId: ids[name],
                allowedOrganizationId: ids.organization,
                primaryRole: name,
              },
            ]),
          ),
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
        if (value.action === "capture-run") fields.push("sample", "runId");
        else if (value.action === "restart-profile") fields.push("profile");
        else
          check(
            [
              "checkpoint",
              "revoke-publisher",
              "restore-publisher",
              "restart",
              "shutdown",
            ].includes(value.action),
            "invalidControl",
          );
        check(
          Object.keys(value).sort().join("|") === fields.sort().join("|"),
          "invalidControl",
        );
        if (value.action === "capture-run") {
          guid(value.runId);
          check(
            ["passed", "failed", "notEvaluated"].includes(value.sample),
            "invalidControl",
          );
        }
        if (value.action === "restart-profile")
          check(profiles.includes(value.profile), "invalidControl");
        return value;
      },
      maximum(action) {
        return action === "capture-run"
          ? 70000
          : action === "restart"
            ? 120000
            : 60000;
      },
      async dispatch(item) {
        if (item.action === "capture-run")
          return await capture(item.sample, item.runId);
        if (item.action === "checkpoint") return checkpoint();
        if (item.action === "restart") {
          checkpoint();
          await stop();
          await normalPgRestart();
          await start(profile);
          checkpoint();
          return { outcome: "restarted" };
        }
        if (item.action === "restart-profile") {
          checkpoint();
          await stop();
          await start(item.profile);
          checkpoint();
          return { outcome: "ready", profile: item.profile };
        }
        if (["revoke-publisher", "restore-publisher"].includes(item.action)) {
          checkpoint();
          await stopHost("mutation");
          changeRegressionPublisherRole(
            item.action === "revoke-publisher" ? "viewer" : "publisher",
          );
          // The fixed owner control independently proves that only the captured membership changed.
          original.rows.project_memberships = rows().project_memberships;
          await startHost("mutation", {
            profile: "administrator",
            regressionWorker: true,
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
