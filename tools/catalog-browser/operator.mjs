// Catalog policy and data scenario. Resource/process mechanics are shared, never imported from an executable.
import { runBrowserFixture } from "../browser-fixture/runtime.mjs";

await runBrowserFixture({
  kind: "catalog",
  entrypoint: import.meta.url,
  fixtures: [
    [
      "engines/ferrule-host/tests/fixtures/valid-basic.json",
      "4220877674cba63c004226d487f5a80042369aab30350a964dc5fff5df48dc26",
      2191,
    ],
    [
      "dotnet/tests/Weft.Tests/Fixtures/validation-cycle.json",
      "54514537939302ceda435e4403da8051935f55c9e684dc2f9dadfb05145f52c2",
      526,
    ],
  ],
  create(fx) {
    const {
      Failure,
      check,
      sha,
      same,
      equivalent,
      guid,
      cfg,
      fixtureId,
      ids,
      fixtures,
      provision,
      tool,
      initializeStorage,
      startHost,
      stopHost,
      settleWorkers,
      createProject,
      upload,
      projectPath,
      mappingPath,
      request,
      page,
      rows,
      tables,
      snapshot,
      physical,
      jsonSql,
      captureMembership,
      verifyInputBodies,
      state,
      budget,
      revoke,
      normalPgRestart,
    } = fx;
    const profiles = [
      "administrator",
      "viewer",
      "runner",
      "publisher",
      "noProjects",
      "readerUnavailable",
    ];
    let appended = false,
      manifest = null,
      primaryProjectUrl = null,
      primaryMappingUrl = null,
      originalRevisionUrl = null;
    async function setup(migrated) {
      for (const profile of [
        "administrator",
        "viewer",
        "runner",
        "publisher",
        "noProjects",
      ])
        provision(ids[profile], ids.organization);
      provision(ids.foreignAdministrator, ids.foreignOrganization);
      for (const [organization, actor] of [
        [ids.organization, ids.administrator],
        [ids.foreignOrganization, ids.foreignAdministrator],
      ])
        tool(["set-project-creation-capability", organization, actor, "true"]);
      initializeStorage();
      const setup = await startHost("mutation", {
        actor: ids.administrator,
        writer: true,
      });
      const projects = [];
      for (let index = 0; index < 12; index++)
        projects.push(await createProject(setup, "Browser project " + index));
      const primary = projects[0],
        empty = projects[1],
        denied = projects[11];
      primaryProjectUrl = projectPath(primary);
      const mappingRecords = [];
      for (let index = 0; index < 11; index++)
        mappingRecords.push(
          await upload(setup, primary, fixtures[index % 2], null, index),
        );
      const original = mappingRecords[0];
      primaryMappingUrl = mappingPath(original.scope);
      originalRevisionUrl = original.revisionUrl;
      for (let index = 0; index < 10; index++)
        await upload(
          setup,
          primary,
          fixtures[index % 2],
          original.scope,
          index + 11,
        );
      const originalAgain = await request(setup, originalRevisionUrl);
      check(
        originalAgain.status === 200 &&
          equivalent(originalAgain.body, original.metadata),
      );
      const history = await page(
        setup,
        primaryMappingUrl + "/revisions",
        "revisions",
      );
      check(
        history.length === 11 &&
          history.every(
            (revision, index) => revision.sequence === String(11 - index),
          ) &&
          equivalent(history[10], original.metadata) &&
          history[0].nativeDigest.value !== history[10].nativeDigest.value &&
          history[0].admission !== history[10].admission,
      );
      for (const project of projects.slice(0, 11))
        provision(ids.viewer, ids.organization, project.projectId, "viewer");
      for (const role of ["runner", "publisher"])
        provision(ids[role], ids.organization, primary.projectId, role);
      await stopHost("mutation");
      await settleWorkers(true);
      const foreignHost = await startHost("mutation", {
        actor: ids.foreignAdministrator,
        organization: ids.foreignOrganization,
        writer: true,
      });
      const foreign = await createProject(
        foreignHost,
        "Foreign browser project",
      );
      await stopHost("mutation");
      await settleWorkers(true);
      captureMembership(primary);
      const viewer = await startHost("read", { profile: "viewer" });
      const visible = await page(
        viewer,
        "/api/v1/organizations/" + ids.organization + "/projects",
        "projects",
      );
      const expectedProjectIds = projects
        .slice(0, 11)
        .map((project) => project.projectId)
        .sort();
      check(
        visible.length === 11 &&
          equivalent(
            visible.map((project) => project.scope.projectId),
            expectedProjectIds,
          ) &&
          visible.every(
            (project) =>
              project.scope.organizationId === ids.organization &&
              project.access.role === "viewer",
          ),
      );
      const mappings = await page(
        viewer,
        primaryProjectUrl + "/mappings",
        "mappings",
      );
      check(
        mappings.length === 11 &&
          equivalent(
            mappings.map((mapping) => mapping.scope.mappingId),
            mappingRecords.map((record) => record.scope.mappingId).sort(),
          ),
      );
      for (const path of [projectPath(denied), projectPath(foreign)])
        check((await request(viewer, path)).status === 404);
      const emptyPage = await page(
        viewer,
        projectPath(empty) + "/mappings",
        "mappings",
      );
      check(emptyPage.length === 0);
      const finalRows = rows();
      check(
        finalRows.projects.length === 13 &&
          finalRows.mappings.length === 11 &&
          finalRows.mapping_revisions.length === 21 &&
          finalRows.revision_derivations.length === 0 &&
          finalRows.revision_derivation_attempts.length === 0 &&
          finalRows.private_object_publications.length === 21 &&
          finalRows.private_object_retirements.length === 0 &&
          finalRows.private_object_cleanup_attempts.length === 0,
      );
      for (const revision of finalRows.mapping_revisions) {
        const publications = finalRows.private_object_publications.filter(
          (row) =>
            row.organization_id === revision.organization_id &&
            row.project_id === revision.project_id &&
            row.mapping_id === revision.mapping_id &&
            row.revision_id === revision.id &&
            row.object_class === "native" &&
            row.object_id === revision.native_object_id,
        );
        check(
          publications.length === 1 &&
            publications[0].owner_kind === "native_ingestion" &&
            publications[0].bound_at !== null &&
            /^[0-9a-f]{64}$/.test(publications[0].bound_record_digest),
          "productFailure",
        );
      }
      manifest = {
        schemaVersion: "weft.catalog-browser-fixture.v1",
        fixtureId,
        source: { commit: cfg.source.commit, tree: cfg.source.tree },
        readOrigin: cfg.readOrigin,
        initialReadProfile: "viewer",
        organizationId: ids.organization,
        profiles: Object.fromEntries(
          profiles.map((profile) => [
            profile,
            {
              actorPrincipalId:
                ids[profile === "readerUnavailable" ? "viewer" : profile],
              allowedOrganizationId: ids.organization,
              expectedContextStatus:
                profile === "readerUnavailable" ? 500 : 200,
              primaryRole:
                profile === "noProjects" || profile === "readerUnavailable"
                  ? null
                  : profile,
            },
          ]),
        ),
        projects: {
          primary,
          empty,
          deniedToViewer: denied,
          foreign,
          viewerVisibleUuidOrder: visible.map((project) => project.scope),
        },
        mappings: {
          primary: {
            organizationId: original.scope.organizationId,
            projectId: original.scope.projectId,
            mappingId: original.scope.mappingId,
          },
          uuidOrder: mappings.map((mapping) => mapping.scope),
        },
        history: {
          original: original.metadata,
          initialHead: history[0],
          sequenceDescending: history,
        },
      };
      return manifest;
    }
    async function appendPrimary() {
      check(
        !appended && !fx.orgRevoked && !fx.projectRevoked && !fx.mutationHost,
      );
      const originalBytes = Buffer.from(
          JSON.stringify(manifest.history.original),
        ),
        old = snapshot();
      let result,
        first = null;
      try {
        const host = await startHost("mutation", {
          actor: ids.publisher,
          writer: true,
        });
        const current = await request(host, primaryMappingUrl);
        check(
          current.status === 200 &&
            equivalent(
              current.body.head.scope,
              manifest.history.initialHead.scope,
            ),
        );
        result = await upload(
          host,
          manifest.projects.primary,
          fixtures[0],
          manifest.mappings.primary,
          100,
        );
        check(result.metadata.sequence === "12");
        const original = await request(host, originalRevisionUrl);
        check(
          original.status === 200 &&
            same(Buffer.from(JSON.stringify(original.body)), originalBytes),
        );
      } catch (error) {
        first = error;
      } finally {
        try {
          await stopHost("mutation");
        } catch (error) {
          if (!first) first = error;
          else
            state.cleanupFailures.push({
              phase: "append-host",
              category: error.category ?? "cleanupFailure",
            });
        }
        try {
          await settleWorkers(true);
        } catch (error) {
          if (!first) first = error;
          else
            state.cleanupFailures.push({
              phase: "append-workers",
              category: error.category ?? "cleanupFailure",
            });
        }
      }
      if (first) throw first;
      const after = snapshot();
      // Existing immutable row objects and all old physical inode/byte/mode identities survive.
      for (const table of tables.filter(
        (table) =>
          ![
            "mappings",
            "revision_ingestions",
            "revision_ingestion_attempts",
          ].includes(table),
      )) {
        for (const row of old.rows[table])
          check(
            after.rows[table].some((value) => equivalent(value, row)),
            "productFailure",
          );
      }
      for (const item of old.physical)
        check(
          after.physical.some((value) => equivalent(value, item)),
          "productFailure",
        );
      check(
        after.rows.mapping_revisions.length ===
          old.rows.mapping_revisions.length + 1 &&
          after.rows.mappings.length === old.rows.mappings.length &&
          after.rows.private_object_publications.length ===
            old.rows.private_object_publications.length + 1 &&
          equivalent(old.credentials, after.credentials) &&
          equivalent(old.journal, after.journal),
        "productFailure",
      );
      verifyInputBodies();
      appended = true;
      return result.metadata;
    }

    const actions = [
      "api-stop",
      "api-start",
      "restart-profile",
      "revoke-project",
      "revoke-org",
      "append-primary-revision",
      "pg-normal-restart",
      "shutdown",
    ];
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
      const withProfile =
        item.action === "api-start" || item.action === "restart-profile";
      check(
        equivalent(
          Object.keys(item).sort(),
          (withProfile
            ? ["requestNonce", "fixtureId", "action", "profile"]
            : ["requestNonce", "fixtureId", "action"]
          ).sort(),
        ),
        "invalidControl",
      );
      guid(item.requestNonce);
      check(
        item.fixtureId === fx.fixtureId &&
          actions.includes(item.action) &&
          (!withProfile || profiles.includes(item.profile)),
        "invalidControl",
      );
      // All accepted properties are scalars; accepted envelopes have depth1 (the bound is3).
      return item;
    }
    return {
      profiles,
      setup,
      validateControl,
      maximum(action) {
        return {
          "api-stop": 15000,
          "api-start": 45000,
          "restart-profile": 60000,
          "revoke-project": 10000,
          "revoke-org": 10000,
          "append-primary-revision": 90000,
          "pg-normal-restart": 90000,
          shutdown: 120000,
        }[action];
      },
      async dispatch(item) {
        let response;
        switch (item.action) {
          case "api-stop":
            response = {
              outcome: (await stopHost("read")) ? "stopped" : "alreadyStopped",
            };
            break;
          case "api-start":
            check(!fx.readHost && !fx.mutationHost);
            {
              const host = await startHost("read", { profile: item.profile });
              response = {
                outcome: "ready",
                profile: item.profile,
                contextStatus: host.contextStatus,
              };
            }
            break;
          case "restart-profile":
            await stopHost("read");
            {
              const host = await startHost("read", { profile: item.profile });
              response = {
                outcome: "ready",
                profile: item.profile,
                contextStatus: host.contextStatus,
              };
            }
            break;
          case "revoke-project":
            revoke("project");
            response = { outcome: "revoked" };
            break;
          case "revoke-org":
            revoke("organization");
            response = { outcome: "revoked" };
            break;
          case "append-primary-revision":
            response = {
              outcome: "appended",
              appendedRevision: await appendPrimary(),
            };
            break;
          case "pg-normal-restart":
            await normalPgRestart();
            response = { outcome: "restarted" };
            break;
        }
        return response;
      },
    };
  },
});
