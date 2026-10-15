// Actual creation-only browser fixture. No native upload, storage root or worker.
import { runBrowserFixture } from "../browser-fixture/runtime.mjs";

await runBrowserFixture({
  kind: "project-creation",
  entrypoint: import.meta.url,
  fixtures: [],
  create(fx) {
    const {
      Failure,
      check,
      sha,
      equivalent,
      guid,
      cfg,
      fixtureId,
      ids,
      provision,
      tool,
      startHost,
      stopHost,
      settleWorkers,
      createProject,
      request,
      rows,
      snapshot,
      state,
      save,
      captureCreatedFact,
      changeCreatedMembership,
      normalPgRestart,
    } = fx;
    const profiles = ["creator", "projectAdministrator"];
    const actorFor = (profile) => {
      check(profiles.includes(profile), "invalidControl");
      return profile === "creator" ? ids.viewer : ids.publisher;
    };
    const captured = new Map();
    let seedProject = null,
      creationBaseline = null,
      previousCheckpoint = null,
      capability = true;
    const counts = (current) => ({
      projects: current.projects.length,
      projectMemberships: current.project_memberships.length,
      creationFacts: current.project_creations.length,
      creatorProjects: current.projects.filter(
        (row) => row.creator_principal_id === ids.viewer,
      ).length,
      workersCreated: fx.workerCreates(),
    });
    function observe(kind, value) {
      state.observations.push({ kind, ...value });
      save();
    }
    function noNative(current) {
      for (const table of [
        "mappings",
        "mapping_revisions",
        "revision_ingestions",
        "revision_ingestion_attempts",
        "revision_derivations",
        "revision_derivation_attempts",
        "private_object_publications",
        "private_object_retirements",
        "private_object_cleanup_attempts",
      ])
        check(current[table].length === 0, "productFailure");
      check(fx.workerCreates() === 0, "productFailure");
      for (const [projectId, fact] of captured)
        check(
          current.project_creations.some(
            (row) => row.project_id === projectId && equivalent(row, fact),
          ),
          "productFailure",
        );
    }
    // Only this one exact genuine creation may extend the trusted private state.
    // A checkpoint may observe it before capture, but never advances the baseline.
    function admittedCreation(current, projectId = null) {
      check(creationBaseline !== null);
      const old = creationBaseline.rows,
        now = current.rows;
      const added = now.projects.filter(
        (project) =>
          !old.projects.some(
            (row) =>
              row.organization_id === project.organization_id &&
              row.id === project.id,
          ),
      );
      check(added.length === 1, "productFailure");
      const project = added[0];
      guid(project.id);
      check(
        project.organization_id === ids.organization &&
          project.creator_principal_id === ids.viewer &&
          (projectId === null || project.id === projectId),
        "productFailure",
      );
      const mine = (row) =>
        row.organization_id === ids.organization &&
        row.project_id === project.id;
      const members = now.project_memberships.filter(mine),
        facts = now.project_creations.filter(mine);
      check(members.length === 1 && facts.length === 1, "productFailure");
      const member = members[0],
        fact = facts[0];
      guid(fact.idempotency_key);
      check(
        member.principal_id === ids.viewer &&
          member.role === "administrator" &&
          member.active === true &&
          String(member.version) === "1" &&
          fact.original_actor_id === ids.viewer &&
          project.name === fact.name &&
          project.description === fact.description &&
          String(project.version) === "1" &&
          project.created_at === project.updated_at &&
          project.created_at === fact.created_at &&
          String(fact.initial_project_version) === "1" &&
          fact.initial_membership_role === "administrator" &&
          fact.initial_membership_active === true &&
          String(fact.initial_membership_version) === "1" &&
          fact.fingerprint_version === 1 &&
          /^[a-f0-9]{64}$/.test(fact.fingerprint),
        "productFailure",
      );
      const remainder = {
        ...current,
        rows: {
          ...now,
          projects: now.projects.filter((row) => row !== project),
          project_memberships: now.project_memberships.filter(
            (row) => row !== member,
          ),
          project_creations: now.project_creations.filter(
            (row) => row !== fact,
          ),
        },
      };
      check(equivalent(remainder, creationBaseline), "productFailure");
      return { project, member, fact };
    }
    function trusted(current, allowOneUncapturedCreation = false) {
      check(creationBaseline !== null);
      noNative(current.rows);
      if (equivalent(current, creationBaseline)) return false;
      check(allowOneUncapturedCreation, "productFailure");
      admittedCreation(current);
      return true;
    }
    async function open(profile) {
      return await startHost("read", {
        profile,
        actor: actorFor(profile),
        creationOnly: true,
      });
    }
    async function setup() {
      for (const actor of [ids.administrator, ids.viewer, ids.publisher])
        provision(actor, ids.organization);
      for (const [actor, enabled] of [
        [ids.administrator, true],
        [ids.viewer, true],
        [ids.publisher, false],
      ])
        tool([
          "set-project-creation-capability",
          ids.organization,
          actor,
          String(enabled),
        ]);
      const setup = await startHost("mutation", {
        profile: "creator",
        actor: ids.administrator,
        creationOnly: true,
      });
      seedProject = await createProject(
        setup,
        "Creation fixture administrator project",
      );
      provision(
        ids.publisher,
        ids.organization,
        seedProject.projectId,
        "administrator",
      );
      await stopHost("mutation");
      await settleWorkers(false);
      const current = rows();
      noNative(current);
      check(
        current.projects.length === 1 &&
          current.project_creations.length === 1 &&
          current.project_memberships.length === 2 &&
          current.project_memberships.every(
            (row) => row.principal_id !== ids.viewer,
          ) &&
          current.projects.every(
            (row) => row.creator_principal_id !== ids.viewer,
          ),
      );
      for (const [actor, enabled] of [
        [ids.administrator, true],
        [ids.viewer, true],
        [ids.publisher, false],
      ]) {
        const caps = current.organization_project_creation_capabilities.filter(
          (row) =>
            row.organization_id === ids.organization &&
            row.principal_id === actor,
        );
        check(caps.length === 1 && caps[0].can_create_projects === enabled);
      }
      const host = await open("creator"),
        context = await request(host, "/api/v1/catalog/context");
      check(
        context.status === 200 &&
          context.body.features.createProjects === true &&
          context.body.organizationActions.createProjects === true,
      );
      creationBaseline = snapshot();
      check(equivalent(creationBaseline.rows, current));
      observe("creation-only-setup", {
        creatorInitiallyHasNoProjectMembership: true,
        projectAdministratorHasNoOrganizationCapability: true,
        initialCounts: counts(creationBaseline.rows),
        nativeWorkersForbidden: true,
        privateObjectRootAbsent: true,
        grantsSha256: sha(Buffer.from(JSON.stringify(creationBaseline.grants))),
      });
      return {
        schemaVersion: "weft.project-creation-browser-fixture.v1",
        fixtureId,
        source: { commit: cfg.source.commit, tree: cfg.source.tree },
        readOrigin: cfg.readOrigin,
        initialReadProfile: "creator",
        organizationId: ids.organization,
        profiles: Object.fromEntries(
          profiles.map((profile) => [
            profile,
            {
              actorPrincipalId: actorFor(profile),
              allowedOrganizationId: ids.organization,
            },
          ]),
        ),
        seedProject: { scope: seedProject },
      };
    }
    async function checkpoint() {
      await settleWorkers(false);
      const current = snapshot(),
        uncapturedCreation = trusted(current, true);
      const result = counts(current.rows);
      const unchanged =
        previousCheckpoint !== null &&
        equivalent(previousCheckpoint.counts, result);
      if (unchanged)
        check(
          equivalent(previousCheckpoint.snapshot, current),
          "productFailure",
        );
      previousCheckpoint = { counts: result, snapshot: current };
      observe("creation-checkpoint", {
        counts: result,
        fullSnapshotUnchangedSinceEqualCounts: unchanged,
        trustedBaselineMatched: !uncapturedCreation,
        singleExactUncapturedCreationObserved: uncapturedCreation,
        rowsSha256: sha(Buffer.from(JSON.stringify(current.rows))),
        credentialsSha256: sha(
          Buffer.from(JSON.stringify(current.credentials)),
        ),
        journalSha256: sha(Buffer.from(JSON.stringify(current.journal))),
        grantsSha256: sha(Buffer.from(JSON.stringify(current.grants))),
        privateObjectRootAbsent: current.physical.length === 0,
      });
      return result;
    }
    async function capture(projectId) {
      guid(projectId);
      check(!captured.has(projectId) && creationBaseline !== null);
      await settleWorkers(false);
      const before = snapshot(),
        observed = counts(before.rows);
      noNative(before.rows);
      const delta = admittedCreation(before, projectId),
        baselineCounts = counts(creationBaseline.rows);
      check(
        observed.projects === baselineCounts.projects + 1 &&
          observed.projectMemberships ===
            baselineCounts.projectMemberships + 1 &&
          observed.creationFacts === baselineCounts.creationFacts + 1 &&
          observed.creatorProjects === baselineCounts.creatorProjects + 1 &&
          observed.workersCreated === 0,
      );
      const project = captureCreatedFact(projectId);
      check(equivalent(before, snapshot()), "productFailure");
      captured.set(projectId, delta.fact);
      creationBaseline = before;
      previousCheckpoint = null;
      observe("actual-created-project-captured", {
        projectId,
        initialFactsMatch: true,
        counts: observed,
        entirePreviousSnapshotPreserved: true,
        grantsSha256: sha(Buffer.from(JSON.stringify(before.grants))),
      });
      return {
        outcome: "captured",
        project,
        counts: observed,
        initialFactsMatch: true,
      };
    }
    function capabilityChange(enabled) {
      check(enabled !== capability && !fx.mutationHost);
      const old = snapshot();
      trusted(old);
      const mine = (row) =>
        row.organization_id === ids.organization &&
        row.principal_id === ids.viewer;
      const oldCaps =
        old.rows.organization_project_creation_capabilities.filter(mine);
      check(
        oldCaps.length === 1 && oldCaps[0].can_create_projects === capability,
      );
      const expectedRow = { ...oldCaps[0], can_create_projects: enabled };
      tool([
        "set-project-creation-capability",
        ids.organization,
        ids.viewer,
        String(enabled),
      ]);
      const after = snapshot(),
        actual =
          after.rows.organization_project_creation_capabilities.filter(mine);
      check(
        actual.length === 1 && equivalent(actual[0], expectedRow),
        "productFailure",
      );
      // Remove only the exact affected row on each side: PostgreSQL's row-text
      // ordering may move it when a value changes. Every other row retains order.
      check(
        equivalent(
          {
            ...creationBaseline,
            rows: {
              ...creationBaseline.rows,
              organization_project_creation_capabilities:
                creationBaseline.rows.organization_project_creation_capabilities.filter(
                  (row) => !mine(row),
                ),
            },
          },
          {
            ...after,
            rows: {
              ...after.rows,
              organization_project_creation_capabilities:
                after.rows.organization_project_creation_capabilities.filter(
                  (row) => !mine(row),
                ),
            },
          },
        ),
        "productFailure",
      );
      creationBaseline = after;
      capability = enabled;
      previousCheckpoint = null;
      noNative(after.rows);
      observe("creator-capability-changed", {
        enabled,
        allOtherRowsCredentialsGrantsAndJournalPreserved: true,
      });
    }
    async function membershipChange(projectId, mode) {
      guid(projectId);
      check(captured.has(projectId));
      await settleWorkers(false);
      const old = snapshot();
      trusted(old);
      const mine = (row) =>
        row.organization_id === ids.organization &&
        row.project_id === projectId &&
        row.principal_id === ids.viewer;
      const oldMembers = creationBaseline.rows.project_memberships.filter(mine);
      check(
        oldMembers.length === 1 &&
          oldMembers[0].active === true &&
          Number.isSafeInteger(oldMembers[0].version),
      );
      if (mode === "viewer")
        check(
          oldMembers[0].role === "administrator" && oldMembers[0].version === 1,
        );
      const expectedRow =
        mode === "deleted"
          ? null
          : {
              ...oldMembers[0],
              role: "viewer",
              version: oldMembers[0].version + 1,
            };
      const project = changeCreatedMembership(projectId, mode),
        after = snapshot(),
        actual = after.rows.project_memberships.filter(mine);
      check(
        mode === "deleted"
          ? actual.length === 0
          : actual.length === 1 && equivalent(actual[0], expectedRow),
        "productFailure",
      );
      check(
        equivalent(
          {
            ...creationBaseline,
            rows: {
              ...creationBaseline.rows,
              project_memberships:
                creationBaseline.rows.project_memberships.filter(
                  (row) => !mine(row),
                ),
            },
          },
          {
            ...after,
            rows: {
              ...after.rows,
              project_memberships: after.rows.project_memberships.filter(
                (row) => !mine(row),
              ),
            },
          },
        ),
        "productFailure",
      );
      creationBaseline = after;
      noNative(after.rows);
      previousCheckpoint = null;
      return { outcome: "changed", project, membership: mode };
    }
    const shapes = {
      "api-stop": [],
      "api-start": ["profile"],
      "restart-profile": ["profile"],
      checkpoint: [],
      "capture-created": ["projectId"],
      "revoke-capability": [],
      "restore-capability": [],
      "membership-viewer": ["projectId"],
      "membership-delete": ["projectId"],
      "pg-normal-restart": [],
      shutdown: [],
    };
    function validateControl(bytes) {
      check(bytes.length > 0 && bytes.length <= 1024, "invalidControl");
      let item;
      try {
        item = JSON.parse(
          new TextDecoder("utf-8", { fatal: true }).decode(bytes),
        );
      } catch {
        throw new Failure("invalidControl");
      }
      check(
        item &&
          typeof item === "object" &&
          !Array.isArray(item) &&
          Object.getPrototypeOf(item) === Object.prototype,
        "invalidControl",
      );
      check(
        Object.hasOwn(shapes, item.action) &&
          equivalent(
            Object.keys(item).sort(),
            [
              "requestNonce",
              "fixtureId",
              "action",
              ...shapes[item.action],
            ].sort(),
          ),
        "invalidControl",
      );
      guid(item.requestNonce);
      check(item.fixtureId === fixtureId, "invalidControl");
      if (Object.hasOwn(item, "profile"))
        check(profiles.includes(item.profile), "invalidControl");
      if (Object.hasOwn(item, "projectId")) guid(item.projectId);
      return item;
    }
    return {
      profiles,
      setup,
      validateControl,
      beforeCleanup() {
        if (creationBaseline !== null) trusted(snapshot(), true);
      },
      maximum(action) {
        return {
          "api-stop": 15000,
          "api-start": 45000,
          "restart-profile": 60000,
          checkpoint: 15000,
          "capture-created": 15000,
          "revoke-capability": 10000,
          "restore-capability": 10000,
          "membership-viewer": 10000,
          "membership-delete": 10000,
          "pg-normal-restart": 90000,
        }[action];
      },
      async dispatch(item) {
        switch (item.action) {
          case "api-stop":
            return {
              outcome: (await stopHost("read")) ? "stopped" : "alreadyStopped",
            };
          case "api-start":
          case "restart-profile": {
            if (item.action === "restart-profile") await stopHost("read");
            const host = await open(item.profile);
            return {
              outcome: "ready",
              profile: item.profile,
              contextStatus: host.contextStatus,
            };
          }
          case "checkpoint":
            return { outcome: "observed", counts: await checkpoint() };
          case "capture-created":
            return await capture(item.projectId);
          case "revoke-capability":
            capabilityChange(false);
            return { outcome: "revoked" };
          case "restore-capability":
            capabilityChange(true);
            return { outcome: "restored" };
          case "membership-viewer":
            return await membershipChange(item.projectId, "viewer");
          case "membership-delete":
            return await membershipChange(item.projectId, "deleted");
          case "pg-normal-restart": {
            check(!fx.mutationHost);
            const profile = fx.readHost?.profile;
            await stopHost("read");
            await settleWorkers(false);
            await normalPgRestart();
            if (profile) await open(profile);
            return { outcome: "restarted" };
          }
          default:
            throw new Failure("invalidControl");
        }
      },
    };
  },
});
