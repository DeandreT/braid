// Actual browser uploads only. Private wire material never enters the owner protocol.
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
  kind: "native-mapping-upload",
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
      canonical,
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
      request,
      snapshot,
      tables,
      jsonSql,
      state,
      save,
      storageRoot,
      captureUploadPublisher,
      changeUploadPublisherRole,
      normalPgRestart,
      verifyInputBodies,
    } = fx;
    const profiles = ["administrator", "publisher", "runner", "viewer"];
    const modes = ["upload", "featureOff"];
    const marker = Buffer.from('"version": 2', "ascii");
    const source = fixtures[1].bytes,
      offset = source.indexOf(marker);
    check(offset >= 0 && offset === source.lastIndexOf(marker));
    const unsupported = Buffer.concat([
      source.subarray(0, offset),
      Buffer.from('"version": 999', "ascii"),
      source.subarray(offset + marker.length),
    ]);
    const malformed = Buffer.from("{not native JSON", "utf8");
    check(
      unsupported.length === 1568 &&
        sha(unsupported) ===
          "7c5ccd2bd23dd3b1e4c9b7e491ca352daec79642965ad40b28154851770e3585" &&
        malformed.length === 16 &&
        sha(malformed) ===
          "d7cb2eb2bce4003424a58a730a02aced4561946de703e4a74dd28073b1731f30",
    );
    // Exact accepted matrix22876da6: no browser-supplied file/query/metadata here.
    const samples = {
      legacy: {
        profile: "administrator",
        bytes: fixtures[0].bytes,
        name: "E legacy 😀",
        description: "Exact legacy bytes + % & =\nline two 😀",
        filename: "legacy + %.native.json",
        phase: "committed",
        admission: "eligible_valid",
        status: "succeeded",
        valid: true,
      },
      versioned: {
        profile: "publisher",
        bytes: fixtures[1].bytes,
        name: "E versioned 9007199254740993",
        description:
          "Versioned integer 9007199254740993; bits 3ff0000000000000 / 8000000000000000",
        filename: "versioned-精確😀.native.json",
        phase: "committed",
        admission: "eligible_valid",
        status: "succeeded",
        valid: true,
      },
      decodedInvalid: {
        profile: "publisher",
        bytes: fixtures[2].bytes,
        name: "E decoded invalid",
        description:
          "Decoded native input retained with actual validation issues.",
        filename: "invalid-function.native.json",
        phase: "committed",
        admission: "eligible_decoded_invalid",
        status: "invalid_mapping",
        valid: false,
      },
      decodeRejected: {
        profile: "publisher",
        bytes: malformed,
        name: "E decode rejection",
        description: "Intentionally malformed native bytes; no mapping commit.",
        filename: "malformed.native.json",
        phase: "rejected",
        admission: "rejected_native_decode",
        status: "invalid_mapping",
        valid: null,
      },
      unsupportedRejected: {
        profile: "publisher",
        bytes: unsupported,
        name: "E unsupported version",
        description:
          "Authored unsupported envelope version; no mapping commit.",
        filename: "unsupported-version.native.json",
        phase: "rejected",
        admission: "rejected_unsupported",
        status: "unsupported_artifact",
        valid: null,
      },
    };
    let project = null,
      protectedProject = null,
      protectedRevision = null,
      initializedMarker = null;
    let baseline = null,
      baselineSql = null,
      baselineWorkers = null;
    let currentProfile = "publisher",
      currentMode = "upload",
      publisherRole = "publisher";
    const captured = new Map();
    function observe(kind, fields) {
      state.observations.push({ kind, ...fields });
      save();
    }
    function scoped(row) {
      return (
        row.organization_id === project.organizationId &&
        row.project_id === project.projectId
      );
    }
    function fullSqlRows(exclusions = {}) {
      // Fixed source table set, fixed scoped predicates only; PostgreSQL hashes full
      // precision JSONB before Node sees it (including unsigned inode/time values).
      const query =
        "SELECT to_json(encode(sha256(convert_to(jsonb_build_object(" +
        tables
          .map(
            (table) =>
              "'" +
              table +
              "',(SELECT coalesce(jsonb_agg(to_jsonb(r) ORDER BY to_jsonb(r)::text),'[]'::jsonb) FROM braid." +
              table +
              " r" +
              (exclusions[table]
                ? " WHERE NOT (" + exclusions[table] + ")"
                : "") +
              ")",
          )
          .join(",") +
        ")::text,'UTF8')),'hex'))";
      const value = jsonSql(query);
      check(typeof value === "string" && /^[a-f0-9]{64}$/.test(value));
      return value;
    }
    function stable(current) {
      check(
        baseline !== null &&
          equivalent(current, baseline) &&
          fullSqlRows() === baselineSql &&
          fx.workerCreates() === baselineWorkers,
        "productFailure",
      );
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
      };
    }
    function advance(current) {
      baseline = current;
      baselineSql = fullSqlRows();
      baselineWorkers = fx.workerCreates();
    }
    async function checkpoint() {
      await settleWorkers(true);
      const current = snapshot();
      stable(current);
      verifyInputBodies();
      observe("upload-read-checkpoint", {
        fullRowsAclJournalCredentialsFilesAndWorkersUnchanged: true,
      });
      return { outcome: "observed", counts: counts(current) };
    }
    async function verifyReadHost(host) {
      const reply = await request(host, projectPath(project));
      const role = host.profile === "publisher" ? publisherRole : host.profile;
      check(
        reply.status === 200 &&
          equivalent(reply.body.scope, project) &&
          reply.body.access.role === role,
        "productFailure",
      );
      const canUpload =
        host.uploadOnly && (role === "administrator" || role === "publisher");
      check(
        reply.body.access.actions.createMappings === canUpload &&
          reply.body.access.actions.uploadRevisions === false,
        "productFailure",
      );
    }
    async function startRead(profile, mode) {
      check(profiles.includes(profile) && modes.includes(mode));
      const host = await startHost("read", {
        profile,
        uploadOnly: mode === "upload",
      });
      await verifyReadHost(host);
      currentProfile = profile;
      currentMode = mode;
      return host;
    }
    function captureInitializedMarker() {
      check(
        initializedMarker === null && typeof process.geteuid === "function",
        "productFailure",
      );
      const path = join(storageRoot, ".braid-private-storage"),
        pathBefore = lstatSync(path, { bigint: true });
      const sameIdentity = (left, right) =>
        left.isFile() &&
        right.isFile() &&
        left.size === right.size &&
        left.mode === right.mode &&
        left.ino === right.ino &&
        left.dev === right.dev &&
        left.uid === right.uid &&
        left.nlink === right.nlink;
      const fd = openSync(
        path,
        constants.O_RDONLY | constants.O_NOFOLLOW | constants.O_NONBLOCK,
      );
      try {
        const before = fstatSync(fd, { bigint: true });
        check(
          before.isFile() &&
            before.size === 90n &&
            (before.mode & 0o7777n) === 0o400n &&
            before.uid === BigInt(process.geteuid()) &&
            before.nlink === 1n &&
            sameIdentity(pathBefore, before),
          "productFailure",
        );
        const buffer = Buffer.alloc(91);
        check(
          readSync(fd, buffer, 0, buffer.length, 0) === 90,
          "productFailure",
        );
        const bytes = buffer.subarray(0, 90),
          text = bytes.toString("latin1");
        const match =
          /^braid-private-storage-v2\n([0-9a-f]{32})\n([0-9a-f]{32})\n$/.exec(
            text,
          );
        check(
          match &&
            match[1] !== "0".repeat(32) &&
            match[2] !== "0".repeat(32) &&
            bytes.equals(
              Buffer.from(
                "braid-private-storage-v2\n" +
                  match[1] +
                  "\n" +
                  match[2] +
                  "\n",
                "ascii",
              ),
            ),
          "productFailure",
        );
        const mount = /^mnt_id:\s+([0-9]+)$/m.exec(
          readFileSync("/proc/self/fdinfo/" + fd, "utf8"),
        );
        check(
          mount &&
            sameIdentity(before, fstatSync(fd, { bigint: true })) &&
            sameIdentity(before, lstatSync(path, { bigint: true })),
          "productFailure",
        );
        const asId = (value) =>
          guid(
            value.slice(0, 8) +
              "-" +
              value.slice(8, 12) +
              "-" +
              value.slice(12, 16) +
              "-" +
              value.slice(16, 20) +
              "-" +
              value.slice(20),
          );
        const identity = {
          inode: String(before.ino),
          device: String(before.dev),
          mountId: mount[1],
          mode: Number(before.mode & 0o777n),
          userId: String(before.uid),
          links: String(before.nlink),
        };
        initializedMarker = {
          rootId: asId(match[1]),
          controlId: asId(match[2]),
          identity,
        };
      } finally {
        closeSync(fd);
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
      captureInitializedMarker();
      let first = null;
      try {
        const host = await startHost("mutation", {
          profile: "administrator",
          writer: true,
        });
        project = await createProject(host, "Native browser upload target");
        protectedProject = await createProject(
          host,
          "Protected native browser sibling",
        );
        protectedRevision = await upload(
          host,
          protectedProject,
          fixtures[0],
          null,
          0,
        );
        check(protectedRevision.metadata.sequence === "1");
        for (const profile of ["publisher", "runner", "viewer"])
          provision(ids[profile], ids.organization, project.projectId, profile);
      } catch (error) {
        first = error;
      } finally {
        for (const [phase, action] of [
          ["upload-setup-host", () => stopHost("mutation")],
          ["upload-setup-workers", () => settleWorkers(true)],
        ]) {
          try {
            await action();
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
      captureUploadPublisher(project.projectId);
      await startRead("publisher", "upload");
      const current = snapshot();
      check(
        current.rows.mappings.length === 1 &&
          current.rows.mapping_revisions.length === 1 &&
          current.rows.revision_ingestions.length === 1 &&
          current.rows.revision_ingestion_attempts.length === 1 &&
          current.rows.private_object_publications.length === 1 &&
          current.rows.revision_derivations.length === 0 &&
          current.rows.revision_derivation_attempts.length === 0 &&
          current.rows.private_object_retirements.length === 0 &&
          current.rows.private_object_cleanup_attempts.length === 0 &&
          fx.workerCreates() === 1,
        "productFailure",
      );
      advance(current);
      return {
        schemaVersion: "braid.native-mapping-upload-browser-fixture.v1",
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
        protected: {
          project: protectedProject,
          revision: protectedRevision.scope,
        },
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
          Number(stat.mode & 0o777n) === 0o700 &&
          stat.uid === BigInt(process.getuid()),
        "productFailure",
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
    async function captureUpload(item) {
      check(
        baseline !== null &&
          !fx.mutationHost &&
          fx.readHost &&
          !captured.has(item.sample) &&
          ![...captured.values()].includes(item.operationId),
        "stateMismatch",
      );
      const sample = samples[item.sample],
        originalActor = ids[sample.profile];
      check(
        fx.readHost.profile === sample.profile &&
          currentMode === "upload" &&
          (sample.profile !== "publisher" || publisherRole === "publisher"),
        "stateMismatch",
      );
      await settleWorkers(true);
      const current = snapshot(),
        old = baseline,
        added = {};
      for (const table of [
        "revision_ingestions",
        "revision_ingestion_attempts",
        "private_object_publications",
      ])
        added[table] = oneNew(old.rows[table], current.rows[table]);
      const ingestion = added.revision_ingestions,
        attempt = added.revision_ingestion_attempts,
        publication = added.private_object_publications;
      const committed = sample.phase === "committed";
      if (committed)
        for (const table of ["mappings", "mapping_revisions"])
          added[table] = oneNew(old.rows[table], current.rows[table]);
      const mapping = added.mappings,
        revision = added.mapping_revisions;
      for (const value of [
        ingestion.operation_id,
        ingestion.future_mapping_id,
        ingestion.future_revision_id,
        ingestion.native_object_id,
        publication.publication_id,
        publication.temp_id,
        publication.root_id,
        attempt.claimant_instance_id,
        attempt.claim_token,
      ])
        guid(value);
      guid(ingestion.idempotency_key);
      const nativeDigest = sha(sample.bytes);
      check(
        scoped(ingestion) &&
          ingestion.operation_id === item.operationId &&
          ingestion.actor_principal_id === originalActor &&
          ingestion.operation_kind === "create" &&
          ingestion.target_mapping_id === null &&
          ingestion.head_condition === "any" &&
          ingestion.expected_head_revision_id === null &&
          ingestion.create_name === sample.name &&
          ingestion.create_description === sample.description &&
          ingestion.display_filename === sample.filename &&
          ingestion.backend_id === "ferrule" &&
          ingestion.native_class === "native" &&
          ingestion.native_digest === nativeDigest &&
          String(ingestion.native_byte_count) === String(sample.bytes.length) &&
          ingestion.logical_entry_document === "entry.native.json" &&
          ingestion.dependency_manifest_id === "braid.dependencies.empty.v1" &&
          String(ingestion.fingerprint_version) === "1" &&
          /^[a-f0-9]{64}$/.test(ingestion.intent_fingerprint) &&
          ingestion.admission_policy_id === "braid.native-admission.v1" &&
          String(ingestion.effective_native_byte_limit) === "4194304" &&
          ingestion.engine_revision ===
            "238369bf0071ee8ea097d8c54fa5061f433807fb" &&
          ingestion.backend_build === cfg.workerImage &&
          ingestion.adapter_build === "braid.ferrule-adapter.v3" &&
          String(ingestion.claim_count) === "1" &&
          String(ingestion.lease_generation) === "1" &&
          String(ingestion.verified_stage_generation) === "1" &&
          ingestion.verified_stage_at !== null &&
          ingestion.phase === sample.phase &&
          ingestion.completed_at !== null &&
          ingestion.last_error === null &&
          ingestion.lease_owner_id === null &&
          ingestion.lease_token === null &&
          ingestion.lease_expires_at === null,
        "productFailure",
      );
      check(
        scoped(attempt) &&
          attempt.operation_id === item.operationId &&
          String(attempt.generation) === "1" &&
          attempt.completion_kind === "validation" &&
          attempt.backend_status === sample.status &&
          attempt.admission_classification === sample.admission &&
          attempt.is_valid === sample.valid &&
          attempt.provenance_confirmed === true &&
          attempt.provenance_organization_id === project.organizationId &&
          attempt.provenance_project_id === project.projectId &&
          attempt.provenance_mapping_id === ingestion.future_mapping_id &&
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
          attempt.evidence_version === "braid.ingestion-validation.v1" &&
          attempt.evidence &&
          typeof attempt.evidence === "object" &&
          !Array.isArray(attempt.evidence) &&
          Number.isSafeInteger(attempt.total_issue_count) &&
          Number.isSafeInteger(attempt.emitted_issue_count) &&
          attempt.total_issue_count >= attempt.emitted_issue_count &&
          attempt.emitted_issue_count >= 0 &&
          Array.isArray(attempt.evidence.diagnostics),
        "productFailure",
      );
      if (item.sample === "decodedInvalid")
        check(attempt.total_issue_count > 0 && attempt.emitted_issue_count > 0);
      if (
        item.sample === "decodeRejected" ||
        item.sample === "unsupportedRejected"
      )
        check(
          attempt.evidence.diagnostics.some(
            (item) =>
              item.code ===
              (sample.admission === "rejected_native_decode"
                ? "ferrule.nativeDecodeFailed"
                : "ferrule.unsupportedNativeVersion"),
          ),
          "productFailure",
        );
      if (committed) {
        check(
          scoped(mapping) &&
            scoped(revision) &&
            mapping.id === ingestion.future_mapping_id &&
            mapping.name === sample.name &&
            mapping.description === sample.description &&
            mapping.backend_id === "ferrule" &&
            mapping.creator_principal_id === originalActor &&
            mapping.owner_principal_id === originalActor &&
            String(mapping.version) === "1" &&
            mapping.head_revision_id === ingestion.future_revision_id &&
            String(mapping.head_sequence) === "1" &&
            revision.mapping_id === mapping.id &&
            revision.id === ingestion.future_revision_id &&
            String(revision.sequence) === "1" &&
            revision.creator_principal_id === originalActor &&
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
            revision.ingestion_operation_id === item.operationId &&
            String(revision.initial_attempt_generation) === "1" &&
            revision.initial_admission === sample.admission &&
            ingestion.committed_revision_id === revision.id &&
            String(ingestion.eligible_attempt_generation) === "1" &&
            ingestion.eligible_admission === sample.admission &&
            mapping.created_at === mapping.updated_at &&
            mapping.created_at === revision.committed_at &&
            revision.committed_at === ingestion.completed_at,
          "productFailure",
        );
      } else {
        check(
          ingestion.committed_revision_id === null &&
            ingestion.eligible_attempt_generation === null &&
            ingestion.eligible_admission === null &&
            !current.rows.mappings.some(
              (row) => scoped(row) && row.id === ingestion.future_mapping_id,
            ) &&
            !current.rows.mapping_revisions.some(
              (row) =>
                scoped(row) &&
                row.mapping_id === ingestion.future_mapping_id &&
                row.id === ingestion.future_revision_id,
            ),
          "productFailure",
        );
      }
      check(
        scoped(publication) &&
          publication.mapping_id === ingestion.future_mapping_id &&
          publication.revision_id === ingestion.future_revision_id &&
          publication.object_class === "native" &&
          publication.object_id === ingestion.native_object_id &&
          publication.native_object_id === ingestion.native_object_id &&
          publication.owner_kind === "native_ingestion" &&
          publication.actor_principal_id === originalActor &&
          publication.ingestion_operation_id === item.operationId &&
          publication.cache_entry_id === null &&
          publication.derivation_id === null &&
          String(publication.owner_generation) === "1" &&
          publication.owner_instance_id === attempt.claimant_instance_id &&
          publication.owner_token === attempt.claim_token &&
          publication.bound_at !== null &&
          publication.owner_lease_expires_at !== null &&
          publication.root_id === initializedMarker.rootId &&
          publication.control_namespace_id === initializedMarker.controlId &&
          // The fixture configures the filesystem store, whose label this is.
          publication.storage_kind === "filesystem" &&
          publication.format === "braid.private-publication.linux.v2" &&
          /^[a-f0-9]{64}$/.test(publication.absence_record_digest) &&
          /^[a-f0-9]{64}$/.test(publication.bound_record_digest),
        "productFailure",
      );
      const compact = (value) => guid(value).replaceAll("-", "");
      const parts = [
        "objects",
        "native",
        ...[
          project.organizationId,
          project.projectId,
          ingestion.future_mapping_id,
          ingestion.future_revision_id,
        ].map(compact),
      ];
      const blob = join(
        ...parts,
        compact(ingestion.native_object_id) + ".blob",
      );
      const control = join(
        ".braid-private-control",
        compact(initializedMarker.controlId),
        ...parts,
        compact(ingestion.native_object_id),
      );
      const temporary = join(
        control,
        "publications",
        compact(publication.temp_id),
      );
      const allowed = new Map([
        [blob, 0o400],
        [join(control, "object.gate"), 0o600],
        [join(temporary, "temp.gate"), 0o600],
        ...["absence", "reservation", "bound", "prepared"].map((name) => [
          join(temporary, name),
          0o400,
        ]),
      ]);
      const freshFiles = current.physical.filter(
        (file) => !old.physical.some((before) => before.path === file.path),
      );
      check(
        freshFiles.length === 7 &&
          freshFiles.every(
            (file) =>
              allowed.get(file.path) === file.mode &&
              file.links === "1" &&
              file.userId === String(process.getuid()),
          ) &&
          old.physical.every((before) =>
            current.physical.some((file) => equivalent(file, before)),
          ),
        "productFailure",
      );
      const payload = freshFiles.find((file) => file.path === blob),
        absence = freshFiles.find(
          (file) => file.path === join(temporary, "absence"),
        ),
        bound = freshFiles.find(
          (file) => file.path === join(temporary, "bound"),
        );
      check(
        payload &&
          payload.sha256 === nativeDigest &&
          payload.bytes === sample.bytes.length &&
          absence.sha256 === publication.absence_record_digest &&
          bound.sha256 === publication.bound_record_digest &&
          freshFiles
            .filter((file) => file.mode === 0o600)
            .every((file) => file.bytes === 0) &&
          freshFiles
            .filter((file) => file.path !== blob && file.mode === 0o400)
            .every((file) => file.bytes > 0) &&
          readFileSync(join(storageRoot, blob)).equals(sample.bytes),
        "productFailure",
      );
      const precise = jsonSql(
        "SELECT json_build_object('expiryMatches',p.owner_lease_expires_at=a.started_at+interval '120 seconds'," +
          "'inode',p.file_inode::text,'major',p.file_device_major::text,'minor',p.file_device_minor::text,'mount',p.file_mount_id::text," +
          "'directoryInode',p.directory_inode::text,'directoryMajor',p.directory_device_major::text,'directoryMinor',p.directory_device_minor::text," +
          "'directoryMount',p.directory_mount_id::text) FROM braid.private_object_publications p JOIN braid.revision_ingestion_attempts a" +
          " ON a.organization_id=p.organization_id AND a.project_id=p.project_id AND a.operation_id=p.ingestion_operation_id AND a.generation=p.owner_generation" +
          " WHERE p.organization_id='" +
          guid(project.organizationId) +
          "' AND p.project_id='" +
          guid(project.projectId) +
          "' AND p.publication_id='" +
          guid(publication.publication_id) +
          "' AND p.ingestion_operation_id='" +
          guid(item.operationId) +
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
        "'";
      const exclusions = {
        revision_ingestions:
          predicate + " AND r.operation_id='" + guid(item.operationId) + "'",
        revision_ingestion_attempts:
          predicate +
          " AND r.operation_id='" +
          guid(item.operationId) +
          "' AND r.generation=1",
        private_object_publications:
          predicate +
          " AND r.publication_id='" +
          guid(publication.publication_id) +
          "'",
      };
      if (committed) {
        exclusions.mappings =
          predicate + " AND r.id='" + guid(mapping.id) + "'";
        exclusions.mapping_revisions =
          predicate +
          " AND r.mapping_id='" +
          guid(mapping.id) +
          "' AND r.id='" +
          guid(revision.id) +
          "'";
      }
      const reduced = {
        ...current,
        rows: { ...current.rows },
        physical: current.physical.filter((file) => !allowed.has(file.path)),
      };
      for (const [table, addedRow] of Object.entries(added))
        reduced.rows[table] = current.rows[table].filter(
          (row) => row !== addedRow,
        );
      // Bind the full precision state BEFORE subtracting. A later change cannot
      // become a newly adopted baseline merely because it followed subtraction.
      const acceptedSql = fullSqlRows();
      check(
        equivalent(reduced, old) &&
          fullSqlRows(exclusions) === baselineSql &&
          fx.workerCreates() === baselineWorkers + 1,
        "productFailure",
      );
      check(
        equivalent(current, snapshot()) && acceptedSql === fullSqlRows(),
        "productFailure",
      );
      baseline = current;
      baselineSql = acceptedSql;
      baselineWorkers = fx.workerCreates();
      captured.set(item.sample, item.operationId);
      verifyInputBodies();
      observe("actual-browser-native-upload-captured", {
        sample: item.sample,
        phase: sample.phase,
        exactNativeBytesAndRegistryIdentity: true,
        exactOneActualWorker: true,
        allPriorRowsAclJournalCredentialsAndFilesPreserved: true,
      });
      return {
        outcome: "captured",
        sample: item.sample,
        operationId: item.operationId,
        phase: sample.phase,
        exactNativeBytes: true,
        counts: counts(current),
      };
    }
    async function publisherChange(role) {
      check(role !== publisherRole && ["publisher", "viewer"].includes(role));
      await settleWorkers(true);
      const old = snapshot();
      stable(old);
      const mine = (row) => scoped(row) && row.principal_id === ids.publisher;
      const previous = old.rows.project_memberships.filter(mine);
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
        "'";
      const beforeSql = fullSqlRows({ project_memberships: predicate });
      changeUploadPublisherRole(role);
      const current = snapshot(),
        member = current.rows.project_memberships.filter(mine),
        acceptedSql = fullSqlRows();
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
          ) &&
          beforeSql === fullSqlRows({ project_memberships: predicate }) &&
          fx.workerCreates() === baselineWorkers,
        "productFailure",
      );
      check(equivalent(current, snapshot()) && acceptedSql === fullSqlRows());
      baseline = current;
      baselineSql = acceptedSql;
      baselineWorkers = fx.workerCreates();
      publisherRole = role;
      observe("upload-publisher-role", {
        role,
        exactOneCapturedMembershipDelta: true,
      });
    }
    const shapes = {
      "api-stop": [],
      "api-start": ["profile", "mode"],
      "restart-profile": ["profile", "mode"],
      checkpoint: [],
      "capture-upload": ["sample", "operationId"],
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
      if (item.action === "api-start" || item.action === "restart-profile")
        check(
          profiles.includes(item.profile) && modes.includes(item.mode),
          "invalidControl",
        );
      if (item.action === "capture-upload") {
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
          "capture-upload": 15000,
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
        // capture follows an actual HTTP mutation; every other control admits only
        // the already-trusted full state. No fresh snapshot is silently adopted.
        if (item.action === "capture-upload") return await captureUpload(item);
        await checkpoint();
        switch (item.action) {
          case "checkpoint":
            return { outcome: "observed", counts: counts(baseline) };
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
