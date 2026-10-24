// F actual append qualification only. Source bytes, keys, query, tokens and SQL remain private.
import { runBrowserFixture } from "../browser-fixture/runtime.mjs";
import {
  readFileSync,
  lstatSync,
  openSync,
  closeSync,
  constants,
  fstatSync,
  readSync,
} from "node:fs";
import { join, dirname } from "node:path";

await runBrowserFixture({
  kind: "native-revision-append",
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
    [
      "engines/ferrule-host/tests/fixtures/invalid-function.json",
      "646595d5def08a5c9f2a1e009d811361730eaa6024326b915ae14e97016fc38e",
      988,
    ],
  ],
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
      snapshot,
      tables,
      jsonSql,
      state,
      save,
      storageRoot,
      captureAppendPublisher,
      changeAppendPublisherRole,
      normalPgRestart,
      verifyInputBodies,
    } = fx;
    const profiles = ["administrator", "publisher", "runner", "viewer"],
      modes = ["append", "featureOff"];
    const order = [
      "valid",
      "decodedInvalid",
      "winner",
      "loser",
      "renewed",
      "lost",
      "restored",
    ];
    const samples = Object.fromEntries(
      order.map((sample) => [
        sample,
        {
          profile: sample === "decodedInvalid" ? "administrator" : "publisher",
          bytes:
            sample === "decodedInvalid" ? fixtures[2].bytes : fixtures[1].bytes,
          filename: {
            valid: "append + % 精確😀.native.json",
            decodedInvalid: "append-invalid.native.json",
            winner: "winner.native.json",
            loser: "loser.native.json",
            renewed: "reviewed-new.native.json",
            lost: "lost-ack.native.json",
            restored: "restored-original.native.json",
          }[sample],
          phase: sample === "loser" ? "headConflict" : "committed",
          admission:
            sample === "decodedInvalid"
              ? "eligible_decoded_invalid"
              : "eligible_valid",
          backendStatus:
            sample === "decodedInvalid" ? "invalid_mapping" : "succeeded",
          valid: sample !== "decodedInvalid",
        },
      ]),
    );
    const advanced = {
      profile: "publisher",
      bytes: fixtures[0].bytes,
      filename: "browser-100.native.json",
      phase: "committed",
      admission: "eligible_valid",
      backendStatus: "succeeded",
      valid: true,
    };
    let project = null,
      primary = null,
      protectedRevision = null,
      initializedMarker = null;
    let baseline = null,
      baselineSql = null,
      baselineWithoutMappingSql = null,
      baselineWorkers = null,
      baselineMetadata = null;
    let currentProfile = "publisher",
      currentMode = "append",
      publisherRole = "publisher",
      edited = false,
      advancedHead = false;
    const captured = new Map();
    const scoped = (row) =>
      row.organization_id === project.organizationId &&
      row.project_id === project.projectId;
    const mine = (row) => scoped(row) && row.id === primary.scope.mappingId;
    const mappingScope = (scope) => ({
      organizationId: scope.organizationId,
      projectId: scope.projectId,
      mappingId: scope.mappingId,
    });
    const without = (row, names) =>
      Object.fromEntries(
        Object.entries(row).filter(([key]) => !names.includes(key)),
      );
    function observe(kind, data) {
      state.observations.push({ kind, ...data });
      save();
    }
    function fullSqlRows(exclusions = {}) {
      const value = jsonSql(
        "SELECT to_json(encode(sha256(convert_to(jsonb_build_object(" +
          tables
            .map(
              (table) =>
                "'" +
                table +
                "',(SELECT coalesce(jsonb_agg(to_jsonb(r) ORDER BY to_jsonb(r)::text),'[]'::jsonb) FROM weft." +
                table +
                " r" +
                (exclusions[table]
                  ? " WHERE NOT (" + exclusions[table] + ")"
                  : "") +
                ")",
            )
            .join(",") +
          ")::text,'UTF8')),'hex'))",
      );
      check(typeof value === "string" && /^[a-f0-9]{64}$/.test(value));
      return value;
    }
    function targetPredicate() {
      return (
        "r.organization_id='" +
        guid(project.organizationId) +
        "' AND r.project_id='" +
        guid(project.projectId) +
        "' AND r.id='" +
        guid(primary.scope.mappingId) +
        "'"
      );
    }
    function mapping(current) {
      const rows = current.rows.mappings.filter(mine);
      check(rows.length === 1);
      return rows[0];
    }
    function counts(current) {
      return {
        mappings: current.rows.mappings.length,
        revisions: current.rows.mapping_revisions.length,
        ingestions: current.rows.revision_ingestions.length,
        ingestionAttempts: current.rows.revision_ingestion_attempts.length,
        nativePublications: current.rows.private_object_publications.filter(
          (row) => row.object_class === "native",
        ).length,
        workersCreated: fx.workerCreates(),
        reportAttempts: current.rows.revision_derivation_attempts.length,
        reportResults: current.rows.revision_derivation_attempts.filter(
          (row) => row.result_usable,
        ).length,
      };
    }
    function acceptInitial(current) {
      baseline = current;
      baselineSql = fullSqlRows();
      baselineWithoutMappingSql = fullSqlRows({ mappings: targetPredicate() });
      baselineWorkers = fx.workerCreates();
    }
    function stable(current) {
      check(
        baseline &&
          equivalent(current, baseline) &&
          fullSqlRows() === baselineSql &&
          fx.workerCreates() === baselineWorkers,
        "productFailure",
      );
    }
    function acceptDelta(current, acceptedSql, acceptedWithout) {
      check(
        equivalent(current, snapshot()) &&
          fullSqlRows() === acceptedSql &&
          fullSqlRows({ mappings: targetPredicate() }) === acceptedWithout,
        "productFailure",
      );
      baseline = current;
      baselineSql = acceptedSql;
      baselineWithoutMappingSql = acceptedWithout;
      baselineWorkers = fx.workerCreates();
    }
    async function readMetadata(host) {
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
          equivalent(reply.body.scope, mappingScope(primary.scope)) &&
          typeof reply.body.name === "string" &&
          typeof reply.body.description === "string" &&
          typeof reply.body.version === "string" &&
          /^[1-9][0-9]{0,18}$/.test(reply.body.version) &&
          BigInt(reply.body.version) <= 9223372036854775807n &&
          reply.headers.etag ===
            '"metadata-v1:' +
              primary.scope.organizationId +
              ":" +
              primary.scope.projectId +
              ":" +
              primary.scope.mappingId +
              ":" +
              reply.body.version +
              '"',
        "productFailure",
      );
      return { body: reply.body, etag: reply.headers.etag };
    }
    async function checkpoint() {
      await settleWorkers(true);
      const current = snapshot();
      stable(current);
      verifyInputBodies();
      observe("append-read-checkpoint", {
        fullRowsAclJournalCredentialsFilesAndWorkersUnchanged: true,
        counts: counts(current),
      });
      return { outcome: "observed", counts: counts(current) };
    }
    async function startRead(profile, mode) {
      check(profiles.includes(profile) && modes.includes(mode));
      const host = await startHost("read", {
        profile,
        uploadOnly: mode === "append",
      });
      const reply = await request(host, mappingPath(primary.scope)),
        role = profile === "publisher" ? publisherRole : profile;
      check(
        reply.status === 200 &&
          equivalent(reply.body.scope, mappingScope(primary.scope)) &&
          reply.body.access.role === role &&
          reply.body.access.actions.uploadRevisions ===
            (mode === "append" &&
              (role === "administrator" || role === "publisher")) &&
          reply.body.access.actions.createMappings === false &&
          reply.body.access.actions.editMetadata === false,
        "productFailure",
      );
      currentProfile = profile;
      currentMode = mode;
      return host;
    }
    function captureMarker() {
      const path = join(storageRoot, ".weft-private-storage"),
        beforePath = lstatSync(path, { bigint: true });
      const same = (a, b) =>
        a.isFile() &&
        b.isFile() &&
        a.size === b.size &&
        a.mode === b.mode &&
        a.ino === b.ino &&
        a.dev === b.dev &&
        a.uid === b.uid &&
        a.nlink === b.nlink;
      const fd = openSync(
        path,
        constants.O_RDONLY | constants.O_NOFOLLOW | constants.O_NONBLOCK,
      );
      try {
        const before = fstatSync(fd, { bigint: true });
        check(
          initializedMarker === null &&
            before.size === 90n &&
            (before.mode & 0o7777n) === 0o400n &&
            before.uid === BigInt(process.geteuid()) &&
            before.nlink === 1n &&
            same(beforePath, before),
        );
        const buffer = Buffer.alloc(91);
        check(readSync(fd, buffer, 0, buffer.length, 0) === 90);
        const bytes = buffer.subarray(0, 90),
          match =
            /^weft-private-storage-v2\n([0-9a-f]{32})\n([0-9a-f]{32})\n$/.exec(
              bytes.toString("ascii"),
            );
        check(
          match &&
            match[1] !== "0".repeat(32) &&
            match[2] !== "0".repeat(32) &&
            bytes.equals(
              Buffer.from(
                "weft-private-storage-v2\n" + match[1] + "\n" + match[2] + "\n",
                "ascii",
              ),
            ) &&
            same(before, fstatSync(fd, { bigint: true })) &&
            same(before, lstatSync(path, { bigint: true })),
        );
        initializedMarker = { rootId: match[1], controlId: match[2] };
      } finally {
        closeSync(fd);
      }
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
          reply.body.result?.generation === "1",
        "productFailure",
      );
      if (operation === "validation") check(reply.body.report.isValid === true);
      return guid(reply.body.result.derivationId);
    }
    async function withMutation(options, work) {
      check(!fx.mutationHost);
      let first = null,
        result;
      try {
        result = await work(await startHost("mutation", options));
      } catch (error) {
        first = error;
      } finally {
        for (const [phase, settle] of [
          ["append-control-host", () => stopHost("mutation")],
          ["append-control-workers", () => settleWorkers(true)],
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
      return result;
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
      captureMarker();
      let projectionDerivationId, validationDerivationId;
      await withMutation(
        { profile: "administrator", writer: true, reportMode: "full" },
        async (host) => {
          project = await createProject(
            host,
            "Native revision append browser project",
          );
          primary = await upload(host, project, fixtures[0], null, 0);
          protectedRevision = await upload(host, project, fixtures[1], null, 1);
          for (const profile of ["publisher", "runner", "viewer"])
            provision(
              ids[profile],
              ids.organization,
              project.projectId,
              profile,
            );
          projectionDerivationId = await setupReport(host, "projection");
          validationDerivationId = await setupReport(host, "validation");
        },
      );
      captureAppendPublisher(project.projectId);
      const readHost = await startRead("publisher", "append");
      baselineMetadata = await readMetadata(readHost);
      const current = snapshot();
      check(
        current.rows.mappings.length === 2 &&
          current.rows.mapping_revisions.length === 2 &&
          current.rows.revision_ingestions.length === 2 &&
          current.rows.revision_ingestion_attempts.length === 2 &&
          current.rows.private_object_publications.length === 4 &&
          current.rows.revision_derivations.length === 2 &&
          current.rows.revision_derivation_attempts.length === 2 &&
          current.rows.revision_derivation_attempts.every(
            (row) => row.result_usable && row.completion_kind === "usable",
          ) &&
          current.rows.private_object_retirements.length === 0 &&
          current.rows.private_object_cleanup_attempts.length === 0 &&
          fx.workerCreates() === 4,
        "productFailure",
      );
      acceptInitial(current);
      observe("append-owner-ready", {
        genuineTwoNativeAndTwoReportSetup: true,
        readHostHasRestrictedIngestionOnly: true,
        counts: counts(current),
      });
      return {
        schemaVersion: "weft.native-revision-append-browser-fixture.v1",
        fixtureId,
        source: { commit: cfg.source.commit, tree: cfg.source.tree },
        readOrigin: cfg.readOrigin,
        initialReadProfile: "publisher",
        organizationId: ids.organization,
        profiles: Object.fromEntries(
          profiles.map((profile) => [
            profile,
            {
              actorPrincipalId: ids[profile],
              allowedOrganizationId: ids.organization,
              primaryRole: profile,
            },
          ]),
        ),
        project: { scope: project },
        mapping: {
          scope: mappingScope(primary.scope),
          initialRevision: {
            scope: primary.scope,
            sequence: "1",
            admission: "valid",
          },
        },
        protected: { revision: protectedRevision.scope },
        reports: { projectionDerivationId, validationDerivationId },
      };
    }
    function oneNew(previous, current) {
      const added = current.filter(
        (row) => !previous.some((old) => equivalent(old, row)),
      );
      check(
        added.length === 1 &&
          current.length === previous.length + 1 &&
          previous.every((old) => current.some((row) => equivalent(row, old))),
        "productFailure",
      );
      return added[0];
    }
    function deviceParts(value) {
      const device = BigInt(value);
      return {
        major: String(((device >> 8n) & 0xfffn) | ((device >> 32n) & ~0xfffn)),
        minor: String((device & 0xffn) | ((device >> 12n) & ~0xffn)),
      };
    }
    function directoryIdentity(path) {
      const stat = lstatSync(path, { bigint: true });
      check(
        stat.isDirectory() &&
          !stat.isSymbolicLink() &&
          (stat.mode & 0o777n) === 0o700n &&
          stat.uid === BigInt(process.getuid()),
      );
      const fd = openSync(path, "r");
      try {
        const match = /^mnt_id:\s+([0-9]+)$/m.exec(
          readFileSync("/proc/self/fdinfo/" + fd, "utf8"),
        );
        check(match);
        return {
          inode: String(stat.ino),
          mountId: match[1],
          ...deviceParts(stat.dev),
        };
      } finally {
        closeSync(fd);
      }
    }
    async function captureAppend(
      sampleName,
      operationId,
      sample = samples[sampleName],
    ) {
      check(
        baseline &&
          !fx.mutationHost &&
          fx.readHost &&
          currentMode === "append" &&
          !captured.has(sampleName),
        "stateMismatch",
      );
      if (sampleName !== "advance")
        check(
          edited &&
            order[captured.size - (advancedHead ? 1 : 0)] === sampleName &&
            fx.readHost.profile === sample.profile &&
            (sample.profile !== "publisher" || publisherRole === "publisher"),
        );
      await settleWorkers(true);
      check(
        equivalent(await readMetadata(fx.readHost), baselineMetadata),
        "productFailure",
      );
      const current = snapshot(),
        old = baseline,
        oldMapping = mapping(old),
        newMapping = mapping(current),
        added = {};
      for (const table of [
        "revision_ingestions",
        "revision_ingestion_attempts",
        "private_object_publications",
      ])
        added[table] = oneNew(old.rows[table], current.rows[table]);
      const ingestion = added.revision_ingestions,
        attempt = added.revision_ingestion_attempts,
        publication = added.private_object_publications,
        committed = sample.phase === "committed";
      if (committed)
        added.mapping_revisions = oneNew(
          old.rows.mapping_revisions,
          current.rows.mapping_revisions,
        );
      const revision = added.mapping_revisions,
        nativeDigest = sha(sample.bytes),
        expectedHead =
          sampleName === "loser"
            ? captured.get("winner").expectedHead
            : oldMapping.head_revision_id;
      for (const id of [
        operationId,
        ingestion.operation_id,
        ingestion.future_mapping_id,
        ingestion.future_revision_id,
        ingestion.native_object_id,
        ingestion.idempotency_key,
        publication.publication_id,
        publication.temp_id,
        publication.root_id,
        publication.control_namespace_id,
        attempt.claimant_instance_id,
        attempt.claim_token,
      ])
        guid(id);
      check(
        scoped(ingestion) &&
          ingestion.operation_id === operationId &&
          ingestion.actor_principal_id === ids[sample.profile] &&
          ingestion.operation_kind === "append" &&
          ingestion.target_mapping_id === primary.scope.mappingId &&
          ingestion.future_mapping_id === primary.scope.mappingId &&
          ingestion.head_condition === "equal" &&
          ingestion.expected_head_revision_id === expectedHead &&
          ingestion.create_name === null &&
          ingestion.create_description === null &&
          ingestion.display_filename === sample.filename &&
          ingestion.backend_id === "ferrule" &&
          ingestion.native_class === "native" &&
          ingestion.native_digest === nativeDigest &&
          String(ingestion.native_byte_count) === String(sample.bytes.length) &&
          ingestion.logical_entry_document === "entry.native.json" &&
          ingestion.dependency_manifest_id === "weft.dependencies.empty.v1" &&
          String(ingestion.fingerprint_version) === "1" &&
          /^[a-f0-9]{64}$/.test(ingestion.intent_fingerprint) &&
          ingestion.admission_policy_id === "weft.native-admission.v1" &&
          String(ingestion.effective_native_byte_limit) === "4194304" &&
          ingestion.engine_revision ===
            "576012aa17c403d9c3fcb8659ed4d3a47ba06864" &&
          ingestion.backend_build === cfg.workerImage &&
          ingestion.adapter_build === "weft.ferrule-adapter.v3" &&
          String(ingestion.claim_count) === "1" &&
          String(ingestion.lease_generation) === "1" &&
          String(ingestion.verified_stage_generation) === "1" &&
          ingestion.verified_stage_at !== null &&
          ingestion.phase ===
            (sample.phase === "headConflict"
              ? "head_conflict"
              : sample.phase) &&
          ingestion.completed_at !== null &&
          ingestion.last_error === null &&
          ingestion.lease_owner_id === null &&
          ingestion.lease_token === null &&
          ingestion.lease_expires_at === null,
        "productFailure",
      );
      check(
        scoped(attempt) &&
          attempt.operation_id === operationId &&
          String(attempt.generation) === "1" &&
          attempt.completion_kind === "validation" &&
          attempt.backend_status === sample.backendStatus &&
          attempt.admission_classification === sample.admission &&
          attempt.is_valid === sample.valid &&
          attempt.provenance_confirmed === true &&
          attempt.provenance_organization_id === project.organizationId &&
          attempt.provenance_project_id === project.projectId &&
          attempt.provenance_mapping_id === primary.scope.mappingId &&
          attempt.provenance_revision_id === ingestion.future_revision_id &&
          attempt.provenance_native_digest === nativeDigest &&
          attempt.provenance_backend_id === ingestion.backend_id &&
          attempt.provenance_engine_revision === ingestion.engine_revision &&
          attempt.provenance_backend_build === ingestion.backend_build &&
          attempt.provenance_adapter_build === ingestion.adapter_build &&
          attempt.started_at !== null &&
          attempt.verified_stage_at !== null &&
          attempt.finished_at !== null &&
          attempt.last_error === null &&
          attempt.reused_attempt_generation === null &&
          attempt.reused_admission === null &&
          attempt.evidence_version === "weft.ingestion-validation.v1" &&
          attempt.evidence &&
          typeof attempt.evidence === "object" &&
          !Array.isArray(attempt.evidence) &&
          Array.isArray(attempt.evidence.diagnostics) &&
          Number.isSafeInteger(attempt.total_issue_count) &&
          Number.isSafeInteger(attempt.emitted_issue_count) &&
          attempt.total_issue_count >= attempt.emitted_issue_count &&
          attempt.emitted_issue_count >= 0,
        "productFailure",
      );
      if (sampleName === "decodedInvalid")
        check(attempt.total_issue_count > 0 && attempt.emitted_issue_count > 0);
      if (committed) {
        check(
          scoped(revision) &&
            revision.mapping_id === primary.scope.mappingId &&
            revision.id === ingestion.future_revision_id &&
            String(revision.sequence) ===
              String(BigInt(oldMapping.head_sequence) + 1n) &&
            revision.creator_principal_id === ids[sample.profile] &&
            revision.native_object_id === ingestion.native_object_id &&
            revision.native_class === "native" &&
            revision.native_digest === nativeDigest &&
            String(revision.native_byte_count) ===
              String(sample.bytes.length) &&
            revision.backend_id === "ferrule" &&
            revision.logical_entry_document ===
              ingestion.logical_entry_document &&
            revision.dependency_manifest_id ===
              ingestion.dependency_manifest_id &&
            revision.ingestion_operation_id === operationId &&
            String(revision.initial_attempt_generation) === "1" &&
            revision.initial_admission === sample.admission &&
            ingestion.committed_revision_id === revision.id &&
            String(ingestion.eligible_attempt_generation) === "1" &&
            ingestion.eligible_admission === sample.admission &&
            revision.committed_at === ingestion.completed_at &&
            newMapping.head_revision_id === revision.id &&
            String(newMapping.head_sequence) === String(revision.sequence) &&
            equivalent(
              without(oldMapping, [
                "head_revision_id",
                "head_sequence",
                "updated_at",
              ]),
              without(newMapping, [
                "head_revision_id",
                "head_sequence",
                "updated_at",
              ]),
            ),
          "productFailure",
        );
        check(
          jsonSql(
            "SELECT to_json('" +
              newMapping.updated_at +
              "'::timestamptz >= '" +
              oldMapping.updated_at +
              "'::timestamptz)",
          ) === true,
        );
      } else {
        check(
          ingestion.committed_revision_id === null &&
            String(ingestion.eligible_attempt_generation) === "1" &&
            ingestion.eligible_admission === sample.admission &&
            !current.rows.mapping_revisions.some(
              (row) =>
                scoped(row) &&
                row.mapping_id === primary.scope.mappingId &&
                row.id === ingestion.future_revision_id,
            ) &&
            equivalent(oldMapping, newMapping),
          "productFailure",
        );
      }
      check(
        scoped(publication) &&
          publication.mapping_id === primary.scope.mappingId &&
          publication.revision_id === ingestion.future_revision_id &&
          publication.object_class === "native" &&
          publication.object_id === ingestion.native_object_id &&
          publication.native_object_id === ingestion.native_object_id &&
          publication.owner_kind === "native_ingestion" &&
          publication.actor_principal_id === ids[sample.profile] &&
          publication.ingestion_operation_id === operationId &&
          publication.cache_entry_id === null &&
          publication.derivation_id === null &&
          String(publication.owner_generation) === "1" &&
          publication.owner_instance_id === attempt.claimant_instance_id &&
          publication.owner_token === attempt.claim_token &&
          publication.bound_at !== null &&
          publication.owner_lease_expires_at !== null &&
          publication.root_id.replaceAll("-", "") ===
            initializedMarker.rootId &&
          publication.control_namespace_id.replaceAll("-", "") ===
            initializedMarker.controlId &&
          // The fixture configures the filesystem store, whose label this is.
          publication.storage_kind === "filesystem" &&
          publication.format === "weft.private-publication.linux.v2" &&
          /^[a-f0-9]{64}$/.test(publication.absence_record_digest) &&
          /^[a-f0-9]{64}$/.test(publication.bound_record_digest),
        "productFailure",
      );
      const compact = (value) => guid(value).replaceAll("-", ""),
        parts = [
          "objects",
          "native",
          ...[
            project.organizationId,
            project.projectId,
            primary.scope.mappingId,
            ingestion.future_revision_id,
          ].map(compact),
        ],
        blob = join(...parts, compact(ingestion.native_object_id) + ".blob"),
        control = join(
          ".weft-private-control",
          initializedMarker.controlId,
          ...parts,
          compact(ingestion.native_object_id),
        ),
        temporary = join(control, "publications", compact(publication.temp_id));
      const allowed = new Map([
        [blob, 0o400],
        [join(control, "object.gate"), 0o600],
        [join(temporary, "temp.gate"), 0o600],
        ...["absence", "reservation", "bound", "prepared"].map((name) => [
          join(temporary, name),
          0o400,
        ]),
      ]);
      const fresh = current.physical.filter(
          (file) => !old.physical.some((before) => before.path === file.path),
        ),
        payload = fresh.find((file) => file.path === blob);
      check(
        fresh.length === allowed.size &&
          fresh.every(
            (file) =>
              allowed.get(file.path) === file.mode &&
              file.links === "1" &&
              file.userId === String(process.getuid()),
          ) &&
          old.physical.every((before) =>
            current.physical.some((file) => equivalent(file, before)),
          ) &&
          payload?.sha256 === nativeDigest &&
          payload.bytes === sample.bytes.length &&
          fresh.find((file) => file.path === join(temporary, "absence"))
            ?.sha256 === publication.absence_record_digest &&
          fresh.find((file) => file.path === join(temporary, "bound"))
            ?.sha256 === publication.bound_record_digest &&
          fresh
            .filter((file) => file.mode === 0o600)
            .every((file) => file.bytes === 0) &&
          fresh
            .filter((file) => file.path !== blob && file.mode === 0o400)
            .every((file) => file.bytes > 0) &&
          readFileSync(join(storageRoot, blob)).equals(sample.bytes),
        "productFailure",
      );
      const precise = jsonSql(
        "SELECT json_build_object('expiryMatches',p.owner_lease_expires_at=a.started_at+interval '120 seconds'," +
          "'inode',p.file_inode::text,'major',p.file_device_major::text,'minor',p.file_device_minor::text,'mount',p.file_mount_id::text," +
          "'directoryInode',p.directory_inode::text,'directoryMajor',p.directory_device_major::text,'directoryMinor',p.directory_device_minor::text,'directoryMount',p.directory_mount_id::text) FROM weft.private_object_publications p JOIN weft.revision_ingestion_attempts a" +
          " ON a.organization_id=p.organization_id AND a.project_id=p.project_id AND a.operation_id=p.ingestion_operation_id AND a.generation=p.owner_generation" +
          " WHERE p.organization_id='" +
          guid(project.organizationId) +
          "' AND p.project_id='" +
          guid(project.projectId) +
          "' AND p.publication_id='" +
          guid(publication.publication_id) +
          "' AND p.ingestion_operation_id='" +
          guid(operationId) +
          "'",
      );
      const device = deviceParts(payload.device),
        parent = directoryIdentity(dirname(join(storageRoot, blob)));
      check(
        precise.expiryMatches === true &&
          precise.inode === payload.inode &&
          precise.mount === payload.mountId &&
          precise.major === device.major &&
          precise.minor === device.minor &&
          precise.directoryInode === parent.inode &&
          precise.directoryMajor === parent.major &&
          precise.directoryMinor === parent.minor &&
          precise.directoryMount === parent.mountId,
        "productFailure",
      );
      const predicate =
          "r.organization_id='" +
          guid(project.organizationId) +
          "' AND r.project_id='" +
          guid(project.projectId) +
          "'",
        exclusions = {
          mappings: targetPredicate(),
          revision_ingestions:
            predicate + " AND r.operation_id='" + guid(operationId) + "'",
          revision_ingestion_attempts:
            predicate +
            " AND r.operation_id='" +
            guid(operationId) +
            "' AND r.generation=1",
          private_object_publications:
            predicate +
            " AND r.publication_id='" +
            guid(publication.publication_id) +
            "'",
        };
      if (committed)
        exclusions.mapping_revisions =
          predicate +
          " AND r.mapping_id='" +
          guid(primary.scope.mappingId) +
          "' AND r.id='" +
          guid(revision.id) +
          "'";
      const reduced = {
        ...current,
        rows: {
          ...current.rows,
          mappings: current.rows.mappings.filter((row) => !mine(row)),
        },
        physical: current.physical.filter((file) => !allowed.has(file.path)),
      };
      for (const [table, addedRow] of Object.entries(added))
        reduced.rows[table] = current.rows[table].filter(
          (row) => row !== addedRow,
        );
      const acceptedSql = fullSqlRows(),
        acceptedWithout = fullSqlRows({ mappings: targetPredicate() });
      check(
        equivalent(reduced, {
          ...old,
          rows: {
            ...old.rows,
            mappings: old.rows.mappings.filter((row) => !mine(row)),
          },
        }) &&
          fullSqlRows(exclusions) === baselineWithoutMappingSql &&
          fx.workerCreates() === baselineWorkers + 1,
        "productFailure",
      );
      acceptDelta(current, acceptedSql, acceptedWithout);
      captured.set(sampleName, { operationId, expectedHead });
      verifyInputBodies();
      observe("actual-browser-native-append-captured", {
        sample: sampleName,
        phase: sample.phase,
        exactNativeBytesAndRegistryIdentity: true,
        exactOneActualValidationWorker: true,
        metadataAndPriorRowsReportsAclJournalCredentialsFilesPreserved: true,
        counts: counts(current),
      });
      return {
        outcome: "captured",
        sample: sampleName,
        operationId,
        phase: sample.phase,
        exactNativeBytes: true,
        counts: counts(current),
      };
    }
    async function metadataEdit() {
      check(!edited && captured.size === 0);
      const old = snapshot();
      stable(old);
      const before = mapping(old),
        oldWorkers = fx.workerCreates();
      const editedMetadata = await withMutation(
        { profile: "administrator", metadataEdits: true },
        async (host) => {
          const path = mappingPath(primary.scope) + "/metadata",
            read = await request(host, path);
          check(
            read.status === 200 &&
              read.headers.etag ===
                '"metadata-v1:' +
                  primary.scope.organizationId +
                  ":" +
                  primary.scope.projectId +
                  ":" +
                  primary.scope.mappingId +
                  ":" +
                  String(before.version) +
                  '"',
          );
          const body = Buffer.from(
            JSON.stringify({
              name: "F metadata independence 😀",
              description:
                "A genuine D edit between native head review and append.",
            }),
          );
          const reply = await request(host, path, {
            method: "PATCH",
            unsafe: true,
            body,
            media: "application/vnd.weft.mapping-metadata-edit.v1+json",
            ifMatch: read.headers.etag,
          });
          check(
            reply.status === 200 &&
              reply.body.name === "F metadata independence 😀" &&
              reply.body.description ===
                "A genuine D edit between native head review and append." &&
              String(reply.body.version) ===
                String(BigInt(before.version) + 1n),
            "productFailure",
          );
          const observed = await readMetadata(host);
          check(
            equivalent(observed.body, reply.body) &&
              observed.etag === reply.headers.etag,
          );
          return observed;
        },
      );
      const current = snapshot(),
        actual = mapping(current),
        acceptedSql = fullSqlRows(),
        acceptedWithout = fullSqlRows({ mappings: targetPredicate() });
      check(
        actual.name === "F metadata independence 😀" &&
          actual.description ===
            "A genuine D edit between native head review and append." &&
          String(actual.version) === String(BigInt(before.version) + 1n) &&
          equivalent(
            without(actual, ["name", "description", "version", "updated_at"]),
            without(before, ["name", "description", "version", "updated_at"]),
          ) &&
          equivalent(
            {
              ...current,
              rows: {
                ...current.rows,
                mappings: current.rows.mappings.filter((row) => !mine(row)),
              },
            },
            {
              ...old,
              rows: {
                ...old.rows,
                mappings: old.rows.mappings.filter((row) => !mine(row)),
              },
            },
          ) &&
          acceptedWithout === baselineWithoutMappingSql &&
          fx.workerCreates() === oldWorkers,
        "productFailure",
      );
      acceptDelta(current, acceptedSql, acceptedWithout);
      baselineMetadata = editedMetadata;
      edited = true;
      observe("genuine-metadata-edit-between-native-review-and-append", {
        nativeHeadAndAllOtherFactsPreserved: true,
        noNativeWorker: true,
      });
      return { outcome: "edited", counts: counts(current) };
    }
    async function advanceHead() {
      check(!advancedHead && captured.size === 6 && captured.has("lost"));
      await checkpoint();
      const revision = await withMutation(
        { profile: "publisher", writer: true },
        (host) => upload(host, project, fixtures[0], primary.scope, 100),
      );
      const ingestion = jsonSql(
        "SELECT to_json(ingestion_operation_id::text) FROM weft.mapping_revisions WHERE organization_id='" +
          guid(project.organizationId) +
          "' AND project_id='" +
          guid(project.projectId) +
          "' AND mapping_id='" +
          guid(primary.scope.mappingId) +
          "' AND id='" +
          guid(revision.scope.revisionId) +
          "'",
      );
      await captureAppend("advance", guid(ingestion), advanced);
      advancedHead = true;
      return {
        outcome: "appended",
        head: {
          scope: revision.scope,
          sequence: revision.metadata.sequence,
          admission: revision.metadata.admission,
        },
        counts: counts(baseline),
      };
    }
    async function publisherChange(role) {
      check(role !== publisherRole && ["publisher", "viewer"].includes(role));
      await settleWorkers(true);
      const old = snapshot();
      stable(old);
      const isMember = (row) =>
          scoped(row) && row.principal_id === ids.publisher,
        previous = old.rows.project_memberships.filter(isMember);
      check(
        previous.length === 1 &&
          previous[0].role === publisherRole &&
          previous[0].active === true &&
          Number.isSafeInteger(previous[0].version),
      );
      const predicate =
          "r.organization_id='" +
          guid(project.organizationId) +
          "' AND r.project_id='" +
          guid(project.projectId) +
          "' AND r.principal_id='" +
          guid(ids.publisher) +
          "'",
        beforeSql = fullSqlRows({ project_memberships: predicate });
      changeAppendPublisherRole(role);
      const current = snapshot(),
        member = current.rows.project_memberships.filter(isMember),
        acceptedSql = fullSqlRows(),
        acceptedWithout = fullSqlRows({ mappings: targetPredicate() });
      check(
        member.length === 1 &&
          equivalent(member[0], {
            ...previous[0],
            role,
            version: previous[0].version + 1,
          }) &&
          equivalent(
            {
              ...old,
              rows: {
                ...old.rows,
                project_memberships: old.rows.project_memberships.filter(
                  (row) => !isMember(row),
                ),
              },
            },
            {
              ...current,
              rows: {
                ...current.rows,
                project_memberships: current.rows.project_memberships.filter(
                  (row) => !isMember(row),
                ),
              },
            },
          ) &&
          beforeSql === fullSqlRows({ project_memberships: predicate }) &&
          fx.workerCreates() === baselineWorkers,
        "productFailure",
      );
      acceptDelta(current, acceptedSql, acceptedWithout);
      publisherRole = role;
      observe("append-publisher-role", {
        role,
        exactOneCapturedMembershipDelta: true,
      });
    }
    const shapes = {
      "api-stop": [],
      "api-start": ["profile", "mode"],
      "restart-profile": ["profile", "mode"],
      checkpoint: [],
      "capture-append": ["sample", "operationId"],
      "metadata-edit": [],
      "advance-head": [],
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
          typeof item.action === "string" &&
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
      if (item.action === "api-start" || item.action === "restart-profile")
        check(
          profiles.includes(item.profile) && modes.includes(item.mode),
          "invalidControl",
        );
      if (item.action === "capture-append") {
        check(
          typeof item.sample === "string" &&
            Object.hasOwn(samples, item.sample),
          "invalidControl",
        );
        guid(item.operationId);
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
          "capture-append": 15000,
          "metadata-edit": 60000,
          "advance-head": 90000,
          "revoke-publisher": 10000,
          "restore-publisher": 10000,
          "pg-normal-restart": 90000,
          shutdown: 120000,
        }[action];
      },
      async beforeCleanup() {
        if (baseline !== null) await checkpoint();
      },
      async dispatch(item) {
        if (item.action === "capture-append")
          return await captureAppend(item.sample, item.operationId);
        await checkpoint();
        switch (item.action) {
          case "checkpoint":
            return { outcome: "observed", counts: counts(baseline) };
          case "metadata-edit":
            return await metadataEdit();
          case "advance-head":
            return await advanceHead();
          case "api-stop": {
            const stopped = await stopHost("read");
            await checkpoint();
            return { outcome: stopped ? "stopped" : "alreadyStopped" };
          }
          case "api-start":
          case "restart-profile": {
            check(!fx.mutationHost);
            if (item.action === "restart-profile") await stopHost("read");
            else check(!fx.readHost);
            const host = await startRead(item.profile, item.mode);
            await checkpoint();
            return {
              outcome: "ready",
              profile: item.profile,
              mode: item.mode,
              contextStatus: host.contextStatus,
            };
          }
          case "revoke-publisher":
            await publisherChange("viewer");
            return { outcome: "revoked" };
          case "restore-publisher":
            await publisherChange("publisher");
            return { outcome: "restored" };
          case "pg-normal-restart": {
            const profile = currentProfile,
              mode = currentMode;
            await stopHost("read");
            await settleWorkers(true);
            await normalPgRestart();
            await startRead(profile, mode);
            await checkpoint();
            return { outcome: "restarted" };
          }
        }
        throw new Failure("invalidControl");
      },
    };
  },
});
