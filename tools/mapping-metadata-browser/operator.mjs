// Actual metadata-only editor scenario. All PATCHes originate in the browser.
import { runBrowserFixture } from "../browser-fixture/runtime.mjs";
import { join } from "node:path";

await runBrowserFixture({
  kind: "mapping-metadata",
  entrypoint: import.meta.url,
  fixtures: [
    [
      "engines/ferrule-host/tests/fixtures/valid-basic.json",
      "4220877674cba63c004226d487f5a80042369aab30350a964dc5fff5df48dc26",
      2191,
    ],
    [
      "engines/ferrule-host/tests/fixtures/versioned-exact.json",
      "b9ac8045a2b3fba94520043cdb5f1a0be7dde34aa2ecd577fafce9168dd440e7",
      1566,
    ],
  ],
  create(fx) {
    const {
      Failure,
      check,
      sha,
      equivalent,
      guid,
      hash,
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
      mappingPath,
      request,
      snapshot,
      rows,
      sql,
      jsonSql,
      state,
      save,
      captureMetadataPublisher,
      changeMetadataPublisherRole,
      normalPgRestart,
      verifyInputBodies,
    } = fx;
    const profiles = ["administrator", "publisher", "runner", "viewer"],
      modes = ["edit", "featureOff"];
    let project = null,
      primary = null,
      unrelated = null,
      baseline = null,
      appended = false,
      publisherRole = "publisher";
    const scopeOnly = (value) => ({
      organizationId: value.organizationId,
      projectId: value.projectId,
      mappingId: value.mappingId,
    });
    const primaryRow = (row) =>
      row.organization_id === ids.organization &&
      row.project_id === project.projectId &&
      row.id === primary.scope.mappingId;
    function version(value) {
      check(
        typeof value === "string" &&
          /^[1-9][0-9]{0,18}$/.test(value) &&
          BigInt(value) <= 9223372036854775807n,
        "invalidControl",
      );
      return value;
    }
    function rowVersion(value) {
      check(Number.isSafeInteger(value) && value > 0);
      return version(String(value));
    }
    const changedText = (a, b) =>
      a.name !== b.name || a.description !== b.description;
    const without = (row, names) =>
      Object.fromEntries(
        Object.entries(row).filter(([key]) => !names.includes(key)),
      );
    function observe(kind, data) {
      state.observations.push({ kind, ...data });
      save();
    }
    function stable(current) {
      check(
        baseline !== null && equivalent(current, baseline),
        "productFailure",
      );
    }
    function currentMapping(current) {
      const values = current.rows.mappings.filter(primaryRow);
      check(values.length === 1);
      return values[0];
    }
    function counters(current) {
      return {
        revisions: current.rows.mapping_revisions.length,
        reportAttempts: current.rows.revision_derivation_attempts.length,
        reportResults: current.rows.revision_derivation_attempts.filter(
          (row) => row.result_usable,
        ).length,
        workersCreated: fx.workerCreates(),
      };
    }
    function timestampNotBefore(value, previous) {
      const syntax =
        /^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,6})?(?:Z|[+-][0-9]{2}:[0-9]{2})$/;
      check(
        typeof value === "string" &&
          typeof previous === "string" &&
          syntax.test(value) &&
          syntax.test(previous),
      );
      // Use PostgreSQL's full timestamp precision, not JavaScript millisecond rounding.
      check(
        sql(
          "SELECT '" +
            value +
            "'::timestamptz >= '" +
            previous +
            "'::timestamptz",
        ) === "t",
        "productFailure",
      );
    }
    async function metadata(host) {
      const reply = await request(
        host,
        mappingPath(primary.scope) + "/metadata",
      );
      check(
        reply.status === 200 &&
          reply.headers["cache-control"] === "no-store" &&
          equivalent(
            Object.keys(reply.body).sort(),
            ["scope", "name", "description", "version"].sort(),
          ) &&
          equivalent(reply.body.scope, scopeOnly(primary.scope)) &&
          typeof reply.body.name === "string" &&
          typeof reply.body.description === "string",
      );
      const v = version(reply.body.version),
        scope = primary.scope;
      check(
        reply.headers.etag ===
          `"metadata-v1:${scope.organizationId}:${scope.projectId}:${scope.mappingId}:${v}"`,
      );
      return { body: reply.body, etag: reply.headers.etag };
    }
    async function open(profile, mode) {
      check(profiles.includes(profile) && modes.includes(mode));
      return await startHost("read", {
        profile,
        metadataEdits: mode === "edit",
      });
    }
    async function setupReport(host, operation) {
      const reply = await request(
        host,
        primary.revisionUrl + "/reports/" + operation + "/ensure",
        { method: "POST", unsafe: true },
      );
      check(
        reply.status === 200 &&
          reply.body.schemaVersion === "weft.catalog-revision-report.v1" &&
          reply.body.operation === operation &&
          equivalent(reply.body.revision, primary.scope) &&
          reply.body.state === "available" &&
          reply.body.readOutcome === "observed" &&
          reply.body.status === "succeeded" &&
          reply.body.report !== null &&
          reply.body.result !== null,
      );
      guid(reply.body.result.derivationId);
      check(reply.body.result.generation === "1");
      if (operation === "validation") check(reply.body.report.isValid === true);
      return reply.body.result.derivationId;
    }
    function verifyPublications(current) {
      check(
        current.rows.private_object_retirements.length === 0 &&
          current.rows.private_object_cleanup_attempts.length === 0,
      );
      for (const row of current.rows.private_object_publications)
        check(
          row.bound_at !== null &&
            /^[a-f0-9]{64}$/.test(row.bound_record_digest) &&
            row.format === "weft.private-publication.linux.v2",
        );
      for (const revision of current.rows.mapping_revisions) {
        const pubs = current.rows.private_object_publications.filter(
          (row) =>
            row.organization_id === revision.organization_id &&
            row.project_id === revision.project_id &&
            row.mapping_id === revision.mapping_id &&
            row.revision_id === revision.id &&
            row.object_id === revision.native_object_id &&
            row.owner_kind === "native_ingestion",
        );
        check(pubs.length === 1);
      }
      for (const attempt of current.rows.revision_derivation_attempts)
        if (attempt.result_usable) {
          const pubs = current.rows.private_object_publications.filter(
            (row) =>
              row.derivation_id === attempt.derivation_id &&
              row.object_id === attempt.candidate_object_id &&
              row.owner_kind === "report_attempt",
          );
          check(pubs.length === 1);
        }
    }
    async function setup() {
      for (const profile of profiles) provision(ids[profile], ids.organization);
      tool([
        "set-project-creation-capability",
        ids.organization,
        ids.administrator,
        "true",
      ]);
      initializeStorage();
      const host = await startHost("mutation", {
        profile: "administrator",
        writer: true,
        reportMode: "full",
      });
      project = await createProject(host, "Metadata editor browser project");
      primary = await upload(host, project, fixtures[0], null, 0);
      unrelated = await upload(host, project, fixtures[1], null, 1);
      for (const role of ["publisher", "runner", "viewer"])
        provision(ids[role], ids.organization, project.projectId, role);
      const projectionDerivationId = await setupReport(host, "projection"),
        validationDerivationId = await setupReport(host, "validation");
      await stopHost("mutation");
      await settleWorkers(true);
      captureMetadataPublisher(project.projectId);
      const editor = await open("publisher", "edit"),
        initialMetadata = await metadata(editor);
      baseline = snapshot();
      verifyPublications(baseline);
      const mapping = currentMapping(baseline);
      check(
        equivalent(initialMetadata.body, {
          scope: scopeOnly(primary.scope),
          name: mapping.name,
          description: mapping.description,
          version: rowVersion(mapping.version),
        }) &&
          baseline.rows.projects.length === 1 &&
          baseline.rows.mappings.length === 2 &&
          baseline.rows.mapping_revisions.length === 2 &&
          baseline.rows.revision_ingestions.length === 2 &&
          baseline.rows.revision_ingestion_attempts.length === 2 &&
          baseline.rows.revision_derivations.length === 2 &&
          baseline.rows.revision_derivation_attempts.length === 2 &&
          baseline.rows.revision_derivation_attempts.every(
            (row) => row.result_usable && row.completion_kind === "usable",
          ) &&
          baseline.rows.private_object_publications.length === 4,
      );
      observe("metadata-editor-ready", {
        actualNativeAndReportSetup: true,
        editorHasNoNativeOrStorageConfiguration: true,
        counts: counters(baseline),
        grantsSha256: sha(Buffer.from(JSON.stringify(baseline.grants))),
      });
      return {
        schemaVersion: "weft.mapping-metadata-browser-fixture.v1",
        fixtureId,
        source: { commit: cfg.source.commit, tree: cfg.source.tree },
        readOrigin: cfg.readOrigin,
        initialReadProfile: "publisher",
        organizationId: ids.organization,
        profiles: Object.fromEntries(
          profiles.map((role) => [
            role,
            {
              actorPrincipalId: ids[role],
              allowedOrganizationId: ids.organization,
              primaryRole: role,
            },
          ]),
        ),
        project: { scope: project },
        primary: {
          scope: scopeOnly(primary.scope),
          initialRevision: primary.metadata,
        },
        unrelated: { scope: scopeOnly(unrelated.scope) },
        reports: { projectionDerivationId, validationDerivationId },
      };
    }
    async function checkpoint() {
      await settleWorkers(true);
      const current = snapshot();
      stable(current);
      const mapping = currentMapping(current),
        counts = counters(current);
      observe("metadata-read-checkpoint", {
        fullTrustedSnapshotUnchanged: true,
        counts,
        rowsSha256: sha(Buffer.from(JSON.stringify(current.rows))),
        physicalSha256: sha(Buffer.from(JSON.stringify(current.physical))),
        grantsSha256: sha(Buffer.from(JSON.stringify(current.grants))),
      });
      return {
        outcome: "observed",
        state: {
          version: rowVersion(mapping.version),
          headRevisionId: mapping.head_revision_id,
          headSequence: rowVersion(mapping.head_sequence),
        },
        counts,
      };
    }
    async function captureEdit(item) {
      check(baseline !== null && !fx.mutationHost);
      await settleWorkers(true);
      const current = snapshot(),
        old = currentMapping(baseline),
        actual = currentMapping(current);
      check(
        rowVersion(old.version) === item.previousVersion &&
          rowVersion(actual.version) === item.version &&
          sha(Buffer.from(actual.name, "utf8")) === item.nameSha256 &&
          sha(Buffer.from(actual.description, "utf8")) ===
            item.descriptionSha256,
      );
      const changed = changedText(old, actual);
      if (!changed) check(equivalent(current, baseline), "productFailure");
      else {
        check(
          BigInt(item.version) === BigInt(item.previousVersion) + 1n &&
            equivalent(
              without(old, ["name", "description", "version", "updated_at"]),
              without(actual, ["name", "description", "version", "updated_at"]),
            ),
        );
        timestampNotBefore(actual.updated_at, old.updated_at);
        check(
          equivalent(
            {
              ...baseline,
              rows: {
                ...baseline.rows,
                mappings: baseline.rows.mappings.filter(
                  (row) => !primaryRow(row),
                ),
              },
            },
            {
              ...current,
              rows: {
                ...current.rows,
                mappings: current.rows.mappings.filter(
                  (row) => !primaryRow(row),
                ),
              },
            },
          ),
          "productFailure",
        );
      }
      check(equivalent(current, snapshot()), "productFailure");
      baseline = current;
      observe("actual-browser-metadata-edit-captured", {
        version: item.version,
        changed,
        headHistoryReportsFilesRolesGrantsJournalPreserved: true,
      });
      return { outcome: "captured", version: item.version, changed };
    }
    async function publisherChange(role) {
      check(role !== publisherRole && ["publisher", "viewer"].includes(role));
      await settleWorkers(true);
      const old = snapshot();
      stable(old);
      const mine = (row) =>
        row.organization_id === ids.organization &&
        row.project_id === project.projectId &&
        row.principal_id === ids.publisher;
      const previous = old.rows.project_memberships.filter(mine);
      check(
        previous.length === 1 &&
          previous[0].role === publisherRole &&
          Number.isSafeInteger(previous[0].version),
      );
      const expected = {
        ...previous[0],
        role,
        version: previous[0].version + 1,
      };
      changeMetadataPublisherRole(role);
      const current = snapshot(),
        actual = current.rows.project_memberships.filter(mine);
      check(
        actual.length === 1 &&
          equivalent(actual[0], expected) &&
          equivalent(
            {
              ...baseline,
              rows: {
                ...baseline.rows,
                project_memberships: baseline.rows.project_memberships.filter(
                  (row) => !mine(row),
                ),
              },
            },
            {
              ...current,
              rows: {
                ...current.rows,
                project_memberships: current.rows.project_memberships.filter(
                  (row) => !mine(row),
                ),
              },
            },
          ),
        "productFailure",
      );
      baseline = current;
      publisherRole = role;
      observe("metadata-publisher-role", {
        role,
        onlyExpectedMembershipChanged: true,
      });
    }
    function oneNew(previous, current) {
      const added = current.filter(
        (row) => !previous.some((old) => equivalent(old, row)),
      );
      check(
        added.length === 1 &&
          current.length === previous.length + 1 &&
          previous.every((old) => current.some((row) => equivalent(row, old))),
      );
      return added[0];
    }
    async function appendPrimary() {
      check(
        !appended &&
          !fx.mutationHost &&
          fx.readHost &&
          publisherRole === "publisher",
      );
      const old = snapshot();
      stable(old);
      const oldMapping = currentMapping(old),
        oldMetadata = await metadata(fx.readHost),
        oldWorkers = fx.workerCreates();
      let result,
        first = null;
      try {
        const host = await startHost("mutation", {
          profile: "publisher",
          writer: true,
        });
        result = await upload(host, project, fixtures[1], primary.scope, 100);
        check(
          result.metadata.nativeDigest.value !==
            primary.metadata.nativeDigest.value &&
            BigInt(result.metadata.sequence) ===
              BigInt(rowVersion(oldMapping.head_sequence)) + 1n,
        );
      } catch (error) {
        first = error;
      } finally {
        for (const [phase, settle] of [
          ["metadata-append-host", () => stopHost("mutation")],
          ["metadata-append-workers", () => settleWorkers(true)],
        ]) {
          try {
            await settle();
          } catch (error) {
            if (!first) first = error;
            else {
              state.cleanupFailures.push({
                phase,
                category: error.category ?? "cleanupFailure",
              });
              save();
            }
          }
        }
      }
      if (first) throw first;
      const current = snapshot(),
        mapping = currentMapping(current),
        newMetadata = await metadata(fx.readHost);
      check(
        equivalent(oldMetadata, newMetadata) &&
          fx.workerCreates() === oldWorkers + 1 &&
          mapping.head_revision_id === result.scope.revisionId &&
          BigInt(rowVersion(mapping.head_sequence)) ===
            BigInt(rowVersion(oldMapping.head_sequence)) + 1n &&
          equivalent(
            without(mapping, [
              "head_revision_id",
              "head_sequence",
              "updated_at",
            ]),
            without(oldMapping, [
              "head_revision_id",
              "head_sequence",
              "updated_at",
            ]),
          ),
      );
      timestampNotBefore(mapping.updated_at, oldMapping.updated_at);
      const added = {};
      for (const table of [
        "mapping_revisions",
        "revision_ingestions",
        "revision_ingestion_attempts",
        "private_object_publications",
      ])
        added[table] = oneNew(old.rows[table], current.rows[table]);
      const revision = added.mapping_revisions,
        ingestion = added.revision_ingestions,
        attempt = added.revision_ingestion_attempts,
        pub = added.private_object_publications;
      check(
        revision.organization_id === ids.organization &&
          revision.project_id === project.projectId &&
          revision.mapping_id === primary.scope.mappingId &&
          revision.id === result.scope.revisionId &&
          revision.native_digest === fixtures[1].sha256 &&
          String(revision.native_byte_count) ===
            String(fixtures[1].bytes.length) &&
          revision.creator_principal_id === ids.publisher &&
          ingestion.organization_id === ids.organization &&
          ingestion.project_id === project.projectId &&
          ingestion.actor_principal_id === ids.publisher &&
          ingestion.operation_kind === "append" &&
          ingestion.target_mapping_id === primary.scope.mappingId &&
          ingestion.future_mapping_id === primary.scope.mappingId &&
          ingestion.future_revision_id === revision.id &&
          ingestion.committed_revision_id === revision.id &&
          ingestion.native_object_id === revision.native_object_id &&
          ingestion.phase === "committed" &&
          ingestion.head_condition === "equal" &&
          ingestion.expected_head_revision_id === oldMapping.head_revision_id &&
          String(ingestion.claim_count) === "1" &&
          String(ingestion.lease_generation) === "1" &&
          attempt.organization_id === ids.organization &&
          attempt.project_id === project.projectId &&
          attempt.operation_id === ingestion.operation_id &&
          String(attempt.generation) === "1" &&
          attempt.completion_kind === "validation" &&
          attempt.backend_status === "succeeded" &&
          attempt.admission_classification === "eligible_valid" &&
          attempt.provenance_confirmed === true &&
          attempt.provenance_revision_id === revision.id &&
          attempt.provenance_native_digest === fixtures[1].sha256 &&
          pub.organization_id === ids.organization &&
          pub.project_id === project.projectId &&
          pub.mapping_id === primary.scope.mappingId &&
          pub.revision_id === revision.id &&
          pub.object_class === "native" &&
          pub.object_id === revision.native_object_id &&
          pub.native_object_id === revision.native_object_id &&
          pub.owner_kind === "native_ingestion" &&
          pub.actor_principal_id === ids.publisher &&
          pub.ingestion_operation_id === ingestion.operation_id &&
          String(pub.owner_generation) === "1" &&
          pub.owner_instance_id === attempt.claimant_instance_id &&
          pub.owner_token === attempt.claim_token,
      );
      verifyPublications(current);
      const compact = (value) => guid(value).replaceAll("-", "");
      const parts = [
        "objects",
        "native",
        ...[
          result.scope.organizationId,
          result.scope.projectId,
          result.scope.mappingId,
          result.scope.revisionId,
        ].map(compact),
      ];
      const blob = join(...parts, compact(revision.native_object_id) + ".blob"),
        control = join(
          ".weft-private-control",
          ...parts,
          compact(revision.native_object_id),
        );
      const temp = join(control, "publications", compact(pub.temp_id));
      const allowed = new Map([
        [blob, 0o400],
        [join(control, "object.gate"), 0o600],
        [join(temp, "temp.gate"), 0o600],
        ...["absence", "reservation", "bound", "prepared"].map((name) => [
          join(temp, name),
          0o400,
        ]),
      ]);
      const newFiles = current.physical.filter(
        (file) => !old.physical.some((previous) => previous.path === file.path),
      );
      check(
        newFiles.length === allowed.size &&
          newFiles.every(
            (file) =>
              allowed.get(file.path) === file.mode && file.links === "1",
          ) &&
          old.physical.every((previous) =>
            current.physical.some((file) => equivalent(file, previous)),
          ),
      );
      const payload = newFiles.find((file) => file.path === blob),
        absence = newFiles.find((file) => file.path === join(temp, "absence")),
        bound = newFiles.find((file) => file.path === join(temp, "bound"));
      check(
        payload.sha256 === fixtures[1].sha256 &&
          payload.bytes === fixtures[1].bytes.length &&
          absence.sha256 === pub.absence_record_digest &&
          bound.sha256 === pub.bound_record_digest,
      );
      const inode = jsonSql(
        "SELECT to_json(file_inode::text) FROM weft.private_object_publications WHERE publication_id='" +
          guid(pub.publication_id) +
          "' AND organization_id='" +
          ids.organization +
          "' AND project_id='" +
          project.projectId +
          "' AND revision_id='" +
          guid(revision.id) +
          "'",
      );
      check(payload.inode === String(inode));
      const reduced = {
        ...current,
        physical: current.physical.filter((file) => !allowed.has(file.path)),
        rows: {
          ...current.rows,
          mappings: current.rows.mappings.filter((row) => !primaryRow(row)),
        },
      };
      for (const table of Object.keys(added))
        reduced.rows[table] = current.rows[table].filter(
          (row) => row !== added[table],
        );
      check(
        equivalent(reduced, {
          ...old,
          rows: {
            ...old.rows,
            mappings: old.rows.mappings.filter((row) => !primaryRow(row)),
          },
        }),
        "productFailure",
      );
      check(equivalent(current, snapshot()));
      baseline = current;
      appended = true;
      verifyInputBodies();
      observe("actual-native-append", {
        metadataRepresentationAndTagUnchanged: true,
        allOldFactsFilesAndReportsPreserved: true,
        oneActualAdditionalWorker: true,
      });
      return result.metadata;
    }
    const shapes = {
      "api-stop": [],
      "api-start": ["profile", "mode"],
      "restart-profile": ["profile", "mode"],
      checkpoint: [],
      "capture-edit": [
        "previousVersion",
        "version",
        "nameSha256",
        "descriptionSha256",
      ],
      "append-primary-revision": [],
      "revoke-publisher": [],
      "restore-publisher": [],
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
          Object.getPrototypeOf(item) === Object.prototype &&
          Object.hasOwn(shapes, item.action),
        "invalidControl",
      );
      check(
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
        check(
          profiles.includes(item.profile) && modes.includes(item.mode),
          "invalidControl",
        );
      if (item.action === "capture-edit") {
        version(item.previousVersion);
        version(item.version);
        hash(item.nameSha256);
        hash(item.descriptionSha256);
      }
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
          checkpoint: 15000,
          "capture-edit": 15000,
          "append-primary-revision": 90000,
          "revoke-publisher": 10000,
          "restore-publisher": 10000,
          "pg-normal-restart": 90000,
        }[action];
      },
      beforeCleanup() {
        if (baseline !== null) stable(snapshot());
      },
      async dispatch(item) {
        switch (item.action) {
          case "api-stop":
            stable(snapshot());
            return {
              outcome: (await stopHost("read")) ? "stopped" : "alreadyStopped",
            };
          case "api-start":
          case "restart-profile": {
            stable(snapshot());
            if (item.action === "restart-profile") await stopHost("read");
            const host = await open(item.profile, item.mode);
            stable(snapshot());
            return {
              outcome: "ready",
              profile: item.profile,
              mode: item.mode,
              contextStatus: host.contextStatus,
            };
          }
          case "checkpoint":
            return await checkpoint();
          case "capture-edit":
            return await captureEdit(item);
          case "append-primary-revision":
            return {
              outcome: "appended",
              appendedRevision: await appendPrimary(),
            };
          case "revoke-publisher":
            await publisherChange("viewer");
            return { outcome: "revoked" };
          case "restore-publisher":
            await publisherChange("publisher");
            return { outcome: "restored" };
          case "pg-normal-restart": {
            const old = snapshot();
            stable(old);
            check(!fx.mutationHost);
            const profile = fx.readHost?.profile,
              mode = fx.readHost?.metadataEdits ? "edit" : "featureOff";
            await stopHost("read");
            await settleWorkers(true);
            await normalPgRestart();
            if (profile) await open(profile, mode);
            stable(snapshot());
            return { outcome: "restarted" };
          }
          default:
            throw new Failure("invalidControl");
        }
      },
    };
  },
});
