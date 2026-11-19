// Actual HTTP-created saved-report scenario. Private state never crosses the owner pipe.
import { runBrowserFixture } from "../browser-fixture/runtime.mjs";
import { reportSetupDiagnostic } from "../browser-fixture/safe-diagnostics.mjs";
import { spawn } from "node:child_process";
import { randomUUID } from "node:crypto";
import {
  readFileSync,
  openSync,
  closeSync,
  writeSync,
  fsyncSync,
  chmodSync,
  renameSync,
  existsSync,
} from "node:fs";
import { join } from "node:path";
import { setTimeout as pause } from "node:timers/promises";
import {
  reportSampleArtifacts,
  reportSampleFixtureIndex,
  reportSampleNames,
} from "./samples.mjs";

await runBrowserFixture({
  kind: "reports",
  entrypoint: import.meta.url,
  fixtures: reportSampleArtifacts,
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
      mappingPath,
      request,
      rows,
      snapshot,
      jsonSql,
      captureMembership,
      verifyInputBodies,
      state,
      save,
      fileIdentity,
      storageRoot,
      ownedChild,
      processIdentity,
      revoke,
      normalPgRestart,
      budget,
    } = fx;
    const profiles = ["administrator", "viewer", "runner", "publisher"];
    const sampleNames = reportSampleNames;
    const modes = ["full", "readOnlyNoWorker", "profileUnavailable"];
    const records = {},
      faults = new Map();
    let project = null,
      appended = false,
      pending = null,
      previousCheckpoint = null;
    const scopeSql = (scope) =>
      Object.entries({
        organization_id: scope.organizationId,
        project_id: scope.projectId,
        mapping_id: scope.mappingId,
        revision_id: scope.revisionId,
      })
        .map(([key, value]) => `${key}='${guid(value)}'`)
        .join(" AND ");
    const reportPath = (sample, operation) => {
      check(
        sampleNames.includes(sample) &&
          ["projection", "validation"].includes(operation),
        "invalidControl",
      );
      return records[sample].revisionUrl + "/reports/" + operation;
    };
    function observation(kind, value) {
      state.observations.push({ kind, ...value });
      save();
    }
    function protectedNative(old, after) {
      for (const name of [
        "mapping_revisions",
        "revision_ingestions",
        "revision_ingestion_attempts",
      ])
        check(equivalent(old.rows[name], after.rows[name]), "productFailure");
      check(
        equivalent(old.credentials, after.credentials) &&
          equivalent(old.journal, after.journal),
      );
      for (const file of old.physical.filter((file) =>
        file.path.startsWith("objects/native/"),
      ))
        check(
          after.physical.some((other) => equivalent(file, other)),
          "productFailure",
        );
      check(
        after.rows.private_object_retirements.length === 0 &&
          after.rows.private_object_cleanup_attempts.length === 0,
      );
    }
    async function setup() {
      for (const role of profiles) provision(ids[role], ids.organization);
      tool([
        "set-project-creation-capability",
        ids.organization,
        ids.administrator,
        "true",
      ]);
      initializeStorage();
      const host = await startHost("mutation", {
        actor: ids.administrator,
        writer: true,
      });
      project = await createProject(host, "Saved report browser project");
      for (let index = 0; index < sampleNames.length; index++)
        records[sampleNames[index]] = await upload(
          host,
          project,
          fixtures[reportSampleFixtureIndex(sampleNames[index])],
          null,
          index,
        );
      for (const role of ["viewer", "runner", "publisher"])
        provision(ids[role], ids.organization, project.projectId, role);
      await stopHost("mutation");
      await settleWorkers(true);
      captureMembership(project);
      const initial = rows();
      check(
        initial.projects.length === 1 &&
          initial.mappings.length === sampleNames.length &&
          initial.mapping_revisions.length === sampleNames.length &&
          initial.revision_derivations.length === 0 &&
          initial.revision_derivation_attempts.length === 0 &&
          initial.private_object_publications.length === sampleNames.length &&
          initial.private_object_retirements.length === 0 &&
          initial.private_object_cleanup_attempts.length === 0,
      );
      for (const revision of initial.mapping_revisions) {
        const published = initial.private_object_publications.filter(
          (row) =>
            row.revision_id === revision.id &&
            row.object_id === revision.native_object_id &&
            row.organization_id === ids.organization &&
            row.project_id === project.projectId,
        );
        check(
          published.length === 1 &&
            published[0].owner_kind === "native_ingestion" &&
            published[0].bound_at !== null,
        );
      }
      await startHost("read", { profile: "viewer", reportMode: "full" });
      return {
        schemaVersion: "braid.reports-browser-fixture.v1",
        fixtureId,
        source: { commit: cfg.source.commit, tree: cfg.source.tree },
        readOrigin: cfg.readOrigin,
        initialReadProfile: "viewer",
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
        samples: Object.fromEntries(
          sampleNames.map((name) => [
            name,
            { revision: records[name].metadata },
          ]),
        ),
      };
    }
    async function withMutation(action) {
      check(!fx.mutationHost && !pending);
      let first, result;
      try {
        result = await action(
          await startHost("mutation", {
            profile: "administrator",
            reportMode: "full",
          }),
        );
      } catch (error) {
        first = error;
      } finally {
        for (const [phase, settle] of [
          ["report-mutation-host", () => stopHost("mutation")],
          ["report-mutation-workers", () => settleWorkers(false)],
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
    function result(reply, sample, operation) {
      // Keep fixed contract categories, never report contents or transport tokens,
      // when a real derivation fails before the browser can read its result.
      state.observations.push(reportSetupDiagnostic(operation, reply));
      save();
      check(
        reply.status === 200 &&
          reply.body.schemaVersion === "braid.catalog-revision-report.v1" &&
          reply.body.operation === operation &&
          equivalent(reply.body.revision, records[sample].scope) &&
          reply.body.state === "available" &&
          reply.body.readOutcome === "observed" &&
          reply.body.result !== null &&
          reply.body.report !== null,
        "productFailure",
      );
      const value = reply.body.result;
      guid(value.derivationId);
      check(
        /^[1-9][0-9]*$/.test(value.generation) &&
          Object.keys(value).sort().join("|") ===
            ["derivationId", "generation", "startedAtUtc", "finishedAtUtc"]
              .sort()
              .join("|"),
      );
      return value;
    }
    async function derive(sample, operation, repairDerivationId) {
      const before = snapshot();
      const value = await withMutation(async (host) => {
        const path =
          reportPath(sample, operation) +
          "/ensure" +
          (repairDerivationId
            ? "?repairDerivationId=" + guid(repairDerivationId)
            : "");
        return result(
          await request(host, path, { method: "POST", unsafe: true }),
          sample,
          operation,
        );
      });
      protectedNative(before, snapshot());
      return value;
    }
    function selected(sample, operation, derivationId) {
      const scope = records[sample].scope;
      const matches = jsonSql(
        "SELECT coalesce(json_agg(row_to_json(a)),'[]'::json) FROM braid.revision_derivation_attempts a WHERE " +
          scopeSql(scope) +
          " AND derivation_id='" +
          guid(derivationId) +
          "' AND result_usable AND completion_kind='usable' AND EXISTS (" +
          "SELECT 1 FROM braid.revision_derivations k WHERE k.organization_id=a.organization_id AND k.project_id=a.project_id " +
          "AND k.mapping_id=a.mapping_id AND k.revision_id=a.revision_id AND k.cache_entry_id=a.cache_entry_id " +
          "AND k.operation_kind='" +
          operation +
          "' AND k.selected_derivation_id=a.derivation_id)",
      );
      check(matches.length === 1);
      return matches[0];
    }
    function faultKey(item) {
      return item.sample + ":" + item.operation + ":" + item.derivationId;
    }
    function faultReport(item) {
      check(!fx.mutationHost && !pending && !faults.has(faultKey(item)));
      const attempt = selected(item.sample, item.operation, item.derivationId);
      const publications = rows().private_object_publications.filter(
        (row) =>
          row.organization_id === attempt.organization_id &&
          row.project_id === attempt.project_id &&
          row.mapping_id === attempt.mapping_id &&
          row.revision_id === attempt.revision_id &&
          row.object_class === "derived" &&
          row.object_id === attempt.candidate_object_id &&
          row.derivation_id === attempt.derivation_id,
      );
      check(
        publications.length === 1 &&
          publications[0].owner_kind === "report_attempt" &&
          publications[0].bound_at !== null &&
          /^[a-f0-9]{64}$/.test(publications[0].bound_record_digest),
      );
      const path =
        join(
          storageRoot,
          "objects/derived",
          ...[
            attempt.organization_id,
            attempt.project_id,
            attempt.mapping_id,
            attempt.revision_id,
            guid(attempt.candidate_object_id),
          ].map((value) => value.replaceAll("-", "")),
        ) + ".blob";
      const original = readFileSync(path),
        identity = fileIdentity(path);
      check(
        identity.mode === 0o400 &&
          identity.links === "1" &&
          sha(original) === attempt.derived_digest &&
          String(original.length) === String(attempt.derived_byte_count) &&
          original.length > 0,
      );
      previousCheckpoint = null;
      const old = snapshot(),
        quarantine = path + ".browser-fault-" + randomUUID();
      check(!existsSync(quarantine));
      const saved = { path, quarantine, original, identity, fault: item.fault };
      // All browser requests have settled before this serialized exact-object physical fixture control.
      // No metadata, selected pointer, lifecycle clock, storage codec or receipt is rewritten.
      faults.set(faultKey(item), saved);
      if (item.fault === "missing") {
        renameSync(path, quarantine);
        check(
          equivalent(fileIdentity(quarantine), identity) &&
            readFileSync(quarantine).equals(original),
        );
      } else {
        chmodSync(path, 0o600);
        const fd = openSync(path, "r+");
        try {
          check(writeSync(fd, Buffer.from([original[0] ^ 1]), 0, 1, 0) === 1);
          fsyncSync(fd);
        } finally {
          closeSync(fd);
          chmodSync(path, 0o400);
        }
        check(
          equivalent(fileIdentity(path), identity) &&
            sha(readFileSync(path)) !== attempt.derived_digest,
        );
      }
      check(equivalent(old.rows, rows()));
      observation("exact-report-object-fault", {
        sample: item.sample,
        operation: item.operation,
        derivationId: item.derivationId,
        fault: item.fault,
      });
    }
    function restoreReport(item) {
      const key = faultKey(item),
        saved = faults.get(key);
      check(saved && !fx.mutationHost && !pending);
      previousCheckpoint = null;
      const oldRows = rows();
      if (saved.fault === "missing") {
        check(!existsSync(saved.path));
        renameSync(saved.quarantine, saved.path);
      } else {
        chmodSync(saved.path, 0o600);
        const fd = openSync(saved.path, "r+");
        try {
          check(
            writeSync(fd, saved.original, 0, saved.original.length, 0) ===
              saved.original.length,
          );
          fsyncSync(fd);
        } finally {
          closeSync(fd);
          chmodSync(saved.path, 0o400);
        }
      }
      check(
        equivalent(fileIdentity(saved.path), saved.identity) &&
          readFileSync(saved.path).equals(saved.original) &&
          equivalent(oldRows, rows()),
      );
      faults.delete(key);
      observation("exact-original-report-restored", {
        sample: item.sample,
        operation: item.operation,
        derivationId: item.derivationId,
      });
    }
    async function appendPrimary() {
      check(
        !appended &&
          !fx.orgRevoked &&
          !fx.projectRevoked &&
          !fx.mutationHost &&
          !pending,
      );
      previousCheckpoint = null;
      const before = snapshot(),
        original = records.basic;
      let first, next;
      try {
        const host = await startHost("mutation", {
          profile: "publisher",
          writer: true,
        });
        const head = await request(host, mappingPath(original.scope));
        check(
          head.status === 200 &&
            equivalent(head.body.head, {
              scope: original.scope,
              sequence: original.metadata.sequence,
              admission: original.metadata.admission,
            }),
        );
        next = await upload(host, project, fixtures[1], original.scope, 100);
        check(
          next.metadata.sequence === "2" &&
            next.metadata.nativeDigest.value !==
              original.metadata.nativeDigest.value &&
            equivalent(
              (await request(host, original.revisionUrl)).body,
              original.metadata,
            ),
        );
      } catch (error) {
        first = error;
      } finally {
        for (const [phase, settle] of [
          ["append-host", () => stopHost("mutation")],
          ["append-workers", () => settleWorkers(true)],
        ]) {
          try {
            await settle();
          } catch (error) {
            if (!first) first = error;
            else
              state.cleanupFailures.push({
                phase,
                category: error.category ?? "cleanupFailure",
              });
          }
        }
      }
      if (first) throw first;
      const after = snapshot();
      for (const name of [
        "mapping_revisions",
        "revision_derivations",
        "revision_derivation_attempts",
        "private_object_publications",
      ])
        for (const row of before.rows[name])
          check(after.rows[name].some((other) => equivalent(row, other)));
      for (const file of before.physical)
        check(after.physical.some((other) => equivalent(file, other)));
      check(
        after.rows.mapping_revisions.length ===
          before.rows.mapping_revisions.length + 1 &&
          after.rows.private_object_publications.length ===
            before.rows.private_object_publications.length + 1 &&
          equivalent(before.credentials, after.credentials) &&
          equivalent(before.journal, after.journal),
      );
      appended = true;
      verifyInputBodies();
      return next.metadata;
    }
    const pendingRows = () =>
      jsonSql(
        "SELECT coalesce(json_agg(row_to_json(a)),'[]'::json) FROM braid.revision_derivation_attempts a WHERE " +
          scopeSql(records.pending.scope),
      );
    // This independent watchdog cannot be delayed by the owner's synchronous SQL child.
    // Its sole authority is SIGCONT of the captured same PID/start ticks; no credentials or environment dump.
    const watchdogSource = `import {readFileSync} from 'node:fs';
      const pid=Number(process.argv[1]),start=process.argv[2];
      const same=()=>{try{const s=readFileSync('/proc/'+pid+'/stat','utf8');return s.slice(s.lastIndexOf(')')+2).split(' ')[19]===start}catch{return false}};
      let done=false,timer=null,armed=null;
      const finish=(automatic)=>{if(done)return;done=true;clearTimeout(timer);let resumed=false;if(same()){process.kill(pid,'SIGCONT');resumed=true}process.stdout.write(JSON.stringify({automatic,resumed,elapsedMilliseconds:armed===null?0:Date.now()-armed})+'\\n');process.stdin.destroy()};
      process.stdin.once('data',()=>{armed=Date.now();timer=setTimeout(()=>finish(true),2500);process.stdout.write('{"ready":true}\\n');process.stdin.once('data',()=>finish(false));});
      process.stdin.once('end',()=>finish(true));setTimeout(()=>{if(armed===null)finish(true)},1000);`;
    async function pendingStart() {
      check(
        pending === null &&
          !fx.mutationHost &&
          !fx.projectRevoked &&
          !fx.orgRevoked,
      );
      check(pendingRows().length === 0);
      const host = await startHost("mutation", {
        profile: "administrator",
        reportMode: "full",
      });
      const operation = request(
        host,
        reportPath("pending", "projection") + "/ensure",
        { method: "POST", unsafe: true },
      ).then(
        (reply) => ({ reply }),
        (error) => ({ error }),
      );
      pending = {
        host,
        operation,
        watch: null,
        watcherClosed: null,
        watcherResult: null,
        stopped: false,
        timedOut: false,
      };
      const end = Date.now() + budget(2000);
      let observed;
      while (true) {
        observed = pendingRows();
        check(
          observed.length <= 1 &&
            !observed.some((row) => row.finished_at !== null),
          "stateMismatch",
        );
        if (observed.length === 1) break;
        check(Date.now() < end, "deadline");
        await pause(10);
      }
      const identity = ownedChild(host);
      const watcher = spawn(
        process.execPath,
        ["--eval", watchdogSource, String(host.child.pid), identity.startTicks],
        { stdio: ["pipe", "pipe", "ignore"], env: { PATH: process.env.PATH } },
      );
      pending.watch = watcher;
      const own = pending;
      let output = "",
        lines = 0,
        readyResolve,
        readyReject;
      const ready = new Promise((fulfil, reject) => {
        readyResolve = fulfil;
        readyReject = reject;
      });
      own.watcherClosed = new Promise((fulfil, reject) => {
        watcher.once("error", (error) => {
          readyReject(error);
          reject(error);
        });
        watcher.once("close", (code, signal) => {
          try {
            check(
              code === 0 && signal === null && lines === 2 && output === "",
            );
            fulfil();
          } catch (error) {
            readyReject(error);
            reject(error);
          }
        });
      });
      // Attach the rejection handler now; failure is propagated during deterministic settlement.
      own.watcherClosed.catch(() => {});
      watcher.stdin.on("error", () => {});
      watcher.stdout.on("data", (bytes) => {
        try {
          output += bytes.toString("utf8");
          check(output.length <= 256);
          while (output.includes("\n")) {
            const end = output.indexOf("\n"),
              line = JSON.parse(output.slice(0, end));
            output = output.slice(end + 1);
            lines++;
            if (lines === 1) {
              check(equivalent(line, { ready: true }));
              readyResolve();
            } else {
              check(
                lines === 2 &&
                  typeof line.automatic === "boolean" &&
                  typeof line.resumed === "boolean" &&
                  Number.isSafeInteger(line.elapsedMilliseconds) &&
                  line.elapsedMilliseconds >= 0,
              );
              own.watcherResult = line;
            }
          }
        } catch (error) {
          readyReject(error);
          watcher.kill("SIGTERM");
        }
      });
      const watchdogIdentity = processIdentity(watcher.pid);
      own.watchIdentity = {
        pid: watchdogIdentity.pid,
        startTicks: watchdogIdentity.startTicks,
      };
      watcher.stdin.write("arm\n");
      await Promise.race([
        ready,
        pause(1500).then(() => {
          throw new Failure("deadline");
        }),
      ]);
      // The watchdog is armed before suspension, so owner failure cannot strand a stopped API.
      check(own.watcherResult === null);
      own.stoppedAt = Date.now();
      process.kill(host.child.pid, "SIGSTOP");
      pending.stopped = true;
      const stopEnd = Date.now() + 250;
      while (true) {
        ownedChild(host);
        const stat = readFileSync("/proc/" + host.child.pid + "/stat", "utf8");
        if (stat.slice(stat.lastIndexOf(")") + 2).split(" ")[0] === "T") break;
        check(Date.now() < stopEnd, "deadline");
        await pause(5);
      }
      const rechecked = pendingRows();
      check(
        rechecked.length === 1 &&
          rechecked[0].finished_at === null &&
          rechecked[0].completion_kind === null &&
          rechecked[0].derivation_id === observed[0].derivation_id &&
          !rechecked[0].result_usable,
      );
      ownedChild(host);
      const finalStat = readFileSync(
        "/proc/" + host.child.pid + "/stat",
        "utf8",
      );
      check(
        finalStat.slice(finalStat.lastIndexOf(")") + 2).split(" ")[0] === "T" &&
          own.watcherResult === null &&
          Date.now() - own.stoppedAt < 2500,
        "deadline",
      );
      observation("actual-pending-owned-api-paused", {
        sample: "pending",
        operation: "projection",
        committedPendingObserved: true,
        sameOwnedPidStopped: true,
        configuredAutomaticResumeMilliseconds: 2500,
      });
    }
    async function resumePending(requireExplicit) {
      if (!pending) return null;
      const own = pending;
      let first = null,
        value = null;
      const preserve = (phase, error) => {
        if (!first) first = error;
        else {
          state.cleanupFailures.push({
            phase,
            category: error.category ?? "cleanupFailure",
          });
          save();
        }
      };
      // Resume before inspecting watchdog/product results. Failure never skips the remaining owned settlement.
      try {
        if (own.stopped) {
          ownedChild(own.host);
          process.kill(own.host.child.pid, "SIGCONT");
          own.stopped = false;
          own.explicitResumedAt = Date.now();
        }
      } catch (error) {
        preserve("pending-resume", error);
      }
      try {
        if (own.watch && !own.watch.stdin.destroyed)
          own.watch.stdin.end("release\n");
        if (own.watcherClosed)
          await Promise.race([
            own.watcherClosed,
            pause(4000).then(() => {
              throw new Failure("deadline");
            }),
          ]);
      } catch (error) {
        preserve("pending-watchdog", error);
        // Only this still-identical owned watchdog may be terminated. Always wait/reap it.
        try {
          if (
            own.watch &&
            own.watch.exitCode === null &&
            own.watch.signalCode === null
          ) {
            const actual = processIdentity(own.watch.pid);
            check(
              actual.startTicks === own.watchIdentity.startTicks,
              "ownershipChanged",
            );
            own.watch.kill("SIGTERM");
            await Promise.race([
              own.watcherClosed.catch(() => {}),
              pause(1000).then(() => {
                throw new Failure("deadline");
              }),
            ]);
          }
        } catch (secondary) {
          preserve("pending-watchdog-reap", secondary);
          try {
            if (
              own.watch &&
              own.watch.exitCode === null &&
              own.watch.signalCode === null
            ) {
              check(
                processIdentity(own.watch.pid).startTicks ===
                  own.watchIdentity.startTicks,
                "ownershipChanged",
              );
              own.watch.kill("SIGKILL");
              await Promise.race([
                own.watcherClosed.catch(() => {}),
                pause(1000).then(() => {
                  throw new Failure("deadline");
                }),
              ]);
            }
          } catch (finalFailure) {
            preserve("pending-watchdog-kill-reap", finalFailure);
          }
        }
      }
      try {
        const completed = await own.operation;
        if (completed.error) throw completed.error;
        value = result(completed.reply, "pending", "projection");
        check(
          own.watcherResult?.resumed === true &&
            own.watcherResult.elapsedMilliseconds <= 3000,
          "deadline",
        );
        if (requireExplicit)
          check(
            own.watcherResult.automatic === false &&
              own.explicitResumedAt - own.stoppedAt <= 3000,
            "deadline",
          );
      } catch (error) {
        preserve("pending-original-operation", error);
      }
      pending = null;
      for (const [phase, settle] of [
        ["pending-host", () => stopHost("mutation")],
        ["pending-workers", () => settleWorkers(false)],
      ]) {
        try {
          await settle();
        } catch (error) {
          preserve(phase, error);
        }
      }
      observation("actual-pending-original-operation-terminal", {
        sample: "pending",
        operation: "projection",
        watchdog: own.watcherResult,
        explicitResumeElapsedMilliseconds:
          own.explicitResumedAt === undefined
            ? null
            : own.explicitResumedAt - own.stoppedAt,
        originalEnsureSucceeded: value !== null,
        succeeded: first === null,
      });
      if (first) throw first;
      return value;
    }
    const shapes = {
      "api-stop": [],
      "api-start": ["profile", "mode"],
      "restart-profile": ["profile", "mode"],
      checkpoint: [],
      "append-primary-revision": [],
      "pg-normal-restart": [],
      "fault-report": ["sample", "operation", "derivationId", "fault"],
      "restore-report": ["sample", "operation", "derivationId"],
      "derive-by-http": ["sample", "operation"],
      "pending-start": [],
      "pending-release": [],
      "revoke-project": [],
      "revoke-org": [],
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
      check(Object.hasOwn(shapes, item.action), "invalidControl");
      const optionalRepair =
        item.action === "derive-by-http" &&
        Object.hasOwn(item, "repairDerivationId");
      check(
        equivalent(
          Object.keys(item).sort(),
          [
            "requestNonce",
            "fixtureId",
            "action",
            ...shapes[item.action],
            ...(optionalRepair ? ["repairDerivationId"] : []),
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
      if (Object.hasOwn(item, "sample"))
        check(
          sampleNames.includes(item.sample) &&
            ["projection", "validation"].includes(item.operation),
          "invalidControl",
        );
      if (Object.hasOwn(item, "derivationId")) guid(item.derivationId);
      if (optionalRepair) guid(item.repairDerivationId);
      if (Object.hasOwn(item, "fault"))
        check(["missing", "integrity"].includes(item.fault), "invalidControl");
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
          "append-primary-revision": 90000,
          "pg-normal-restart": 90000,
          "fault-report": 15000,
          "restore-report": 15000,
          "derive-by-http": 60000,
          "pending-start": 45000,
          "pending-release": 60000,
          "revoke-project": 10000,
          "revoke-org": 10000,
        }[action];
      },
      async beforeCleanup() {
        if (pending) await resumePending(false);
      },
      async dispatch(item) {
        check(!pending || item.action === "pending-release", "stateMismatch");
        switch (item.action) {
          case "api-stop":
            return {
              outcome: (await stopHost("read")) ? "stopped" : "alreadyStopped",
            };
          case "api-start":
          case "restart-profile": {
            if (item.action === "restart-profile") await stopHost("read");
            const host = await startHost("read", {
              profile: item.profile,
              reportMode: item.mode,
            });
            return {
              outcome: "ready",
              profile: item.profile,
              mode: item.mode,
              contextStatus: host.contextStatus,
            };
          }
          case "checkpoint": {
            await settleWorkers(false);
            const current = snapshot();
            const effects = {
              reportAttempts: current.rows.revision_derivation_attempts.length,
              reportResults: current.rows.revision_derivation_attempts.filter(
                (row) => row.result_usable,
              ).length,
              workerCreates: fx.workerCreates(),
            };
            const unchanged =
              previousCheckpoint !== null &&
              equivalent(previousCheckpoint.effects, effects);
            if (unchanged)
              check(
                equivalent(previousCheckpoint.snapshot, current),
                "productFailure",
              );
            previousCheckpoint = { effects, snapshot: current };
            observation("durable-report-read-checkpoint", {
              effects,
              fullSnapshotUnchangedSinceEqualEffects: unchanged,
              rowsSha256: sha(Buffer.from(JSON.stringify(current.rows))),
              physicalSha256: sha(
                Buffer.from(JSON.stringify(current.physical)),
              ),
              credentialsSha256: sha(
                Buffer.from(JSON.stringify(current.credentials)),
              ),
              journalSha256: sha(Buffer.from(JSON.stringify(current.journal))),
            });
            return { outcome: "observed", effects };
          }
          case "append-primary-revision":
            return {
              outcome: "appended",
              appendedRevision: await appendPrimary(),
            };
          case "pg-normal-restart": {
            check(!fx.mutationHost && !pending);
            const profile = fx.readHost?.profile,
              mode = fx.readHost?.reportMode;
            await stopHost("read");
            await settleWorkers(false);
            await normalPgRestart();
            if (profile) await startHost("read", { profile, reportMode: mode });
            return { outcome: "restarted" };
          }
          case "fault-report":
            await settleWorkers(false);
            faultReport(item);
            return {
              outcome: "faulted",
              sample: item.sample,
              operation: item.operation,
              derivationId: item.derivationId,
              fault: item.fault,
            };
          case "restore-report":
            await settleWorkers(false);
            restoreReport(item);
            return {
              outcome: "restored",
              sample: item.sample,
              operation: item.operation,
              derivationId: item.derivationId,
            };
          case "derive-by-http":
            return {
              outcome: "derived",
              sample: item.sample,
              operation: item.operation,
              result: await derive(
                item.sample,
                item.operation,
                item.repairDerivationId,
              ),
            };
          case "pending-start":
            await pendingStart();
            return {
              outcome: "pending",
              sample: "pending",
              operation: "projection",
            };
          case "pending-release":
            return {
              outcome: "derived",
              sample: "pending",
              operation: "projection",
              result: await resumePending(true),
            };
          case "revoke-project":
            previousCheckpoint = null;
            revoke("project");
            return { outcome: "revoked" };
          case "revoke-org":
            previousCheckpoint = null;
            revoke("organization");
            return { outcome: "revoked" };
          default:
            throw new Failure("invalidControl");
        }
      },
    };
  },
});
