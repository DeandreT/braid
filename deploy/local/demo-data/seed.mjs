// Seeds a local Weft instance with the demo catalog through the product API, as the
// Development user would through the browser: the same session cookie, request token and
// origin checks. Every create uses an idempotency key or resource ID derived from
// WEFT_SEED_NAMESPACE and the resource's name, so a rerun replays instead of duplicating.
// Run it through deploy/local/dev-seed.sh, which supplies the instance's settings.
import fs from "node:fs";
import http from "node:http";
import path from "node:path";
import { conversations, ferruleExamples, projects } from "./catalog.mjs";
import { base64Url, stableId } from "./native.mjs";

const origin = required("WEFT_SEED_ORIGIN");
const organization = required("WEFT_SEED_ORGANIZATION_ID");
const defaultProject = required("WEFT_SEED_PROJECT_ID");
const namespace = process.env.WEFT_SEED_NAMESPACE || "weft-demo-v1";
const examples = process.env.WEFT_SEED_FERRULE_EXAMPLES || "";
const skipChat = process.env.WEFT_SEED_CHAT === "0";
// A fixed clock keeps regression cases byte-identical on every rerun.
const caseClock = "2026-11-04T12:00:00Z";

function required(name) {
  const value = process.env[name];
  if (!value)
    throw new Error(`${name} is required; run deploy/local/dev-seed.sh.`);
  return value;
}

const id = (name) => stableId(namespace, name);
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const tally = {};
const failures = [];
let backendDown = false;
const count = (kind, outcome) => {
  tally[kind] ??= {};
  tally[kind][outcome] = (tally[kind][outcome] ?? 0) + 1;
};
const log = (message) => console.log(message);

// ---- HTTP: one Development session with its cookies and request token.
const jar = new Map();
let token = "";
// node:http rather than fetch, which refuses some ports a local instance may use (such as 4190).
function send(method, url, headers, body) {
  return new Promise((resolve, reject) => {
    const request = http.request(
      origin + url,
      { method, headers },
      (response) => {
        const chunks = [];
        response.on("data", (chunk) => chunks.push(chunk));
        response.on("end", () =>
          resolve({ response, bytes: Buffer.concat(chunks) }),
        );
      },
    );
    request.on("error", reject);
    request.end(body);
  });
}
async function api(
  method,
  url,
  {
    body,
    type,
    headers = {},
    accept = [200, 201, 202],
    raw = false,
    retries = 30,
  } = {},
) {
  for (let attempt = 1; ; attempt++) {
    const { response, bytes } = await send(
      method,
      url,
      {
        cookie: [...jar].map(([name, value]) => `${name}=${value}`).join("; "),
        ...(method !== "GET" && {
          origin,
          "x-xsrf-token": token,
          "content-length": body?.length ?? 0,
        }),
        ...(type && { "content-type": type }),
        ...headers,
      },
      body,
    );
    for (const cookie of response.headers["set-cookie"] ?? []) {
      const pair = cookie.split(";")[0];
      jar.set(
        pair.slice(0, pair.indexOf("=")),
        pair.slice(pair.indexOf("=") + 1),
      );
    }
    let data = raw ? bytes : bytes.toString("utf8");
    if (!raw)
      try {
        data = data ? JSON.parse(data) : null;
      } catch {}
    // Admission gates (two concurrent uploads, rate limits) refuse rather than queue.
    if (
      (response.statusCode === 503 || response.statusCode === 429) &&
      attempt < retries
    ) {
      if (attempt === 3)
        log(
          `  … ${method} ${url.split("?")[0]} is busy (${JSON.stringify(data)?.slice(0, 200)}); retrying`,
        );
      await sleep(Math.min(2000 * attempt, 15000));
      continue;
    }
    if (!accept.includes(response.statusCode)) {
      const detail =
        typeof data === "string"
          ? data.slice(0, 300)
          : JSON.stringify(data)?.slice(0, 300);
      throw new Error(
        `${method} ${url.split("?")[0]} returned ${response.statusCode}: ${detail}`,
      );
    }
    return { status: response.statusCode, data, etag: response.headers.etag };
  }
}
const json = (method, url, value, type = "application/json", extra = {}) =>
  api(method, url, {
    body: Buffer.from(JSON.stringify(value)),
    type,
    ...extra,
  });

async function poll(read, done, { seconds = 300, interval = 2000 } = {}) {
  const deadline = Date.now() + seconds * 1000;
  for (;;) {
    const value = await read();
    if (done(value)) return value;
    if (Date.now() > deadline)
      throw new Error("timed out waiting for completion");
    await sleep(interval);
  }
}

// Runs one seeding step; a failure is recorded and the rest of the catalog continues.
async function step(kind, label, action) {
  try {
    const outcome = await action();
    count(kind, outcome ?? "ok");
    return outcome;
  } catch (error) {
    count(kind, "failed");
    failures.push(`${kind} ${label}: ${error.message}`);
    log(`  ! ${kind} ${label}: ${error.message}`);
    return undefined;
  }
}

// ---- Catalog writes.
const projectPath = (project) =>
  `/api/v1/organizations/${organization}/projects/${project}`;
const mappingPath = (project, mapping) =>
  `${projectPath(project)}/mappings/${mapping}`;
const revisionPath = (project, mapping, revision) =>
  `${mappingPath(project, mapping)}/revisions/${revision}`;

async function createProject(project) {
  const reply = await json(
    "POST",
    `/api/v1/organizations/${organization}/projects`,
    { name: project.name, description: project.description },
    "application/vnd.weft.project-create.v1+json",
    { headers: { "idempotency-key": id(`project:${project.slug}`) } },
  );
  project.id = reply.data.receipt.project.projectId;
  return reply.data.outcome;
}

// Uploads one native revision (creating the mapping with its first) and waits for its admission.
async function upload(projectId, url, key, native, query) {
  // An ingestion has eight claims in total, so a stuck backend gets few retries and stops the run.
  const submit = () =>
    api("POST", `${url}?${new URLSearchParams(query)}`, {
      body: Buffer.from(
        typeof native === "string" ? native : JSON.stringify(native, null, 2),
      ),
      type: "application/octet-stream",
      headers: { "idempotency-key": key },
      accept: [200, 202, 503],
      retries: 3,
    }).then((reply) => reply.data);
  let result = await submit();
  if (result.outcome === "inProgress") {
    const receipt = `${projectPath(projectId)}/revision-ingestions/${result.receipt.operationId}`;
    await poll(
      () => api("GET", receipt).then((value) => value.data),
      (value) => !["reserved", "staged", "ready"].includes(value.phase),
    );
    result = await submit();
  }
  if (result.outcome === "retryableFailure") {
    backendDown = true;
    throw new Error("the Ferrule backend is unavailable");
  }
  if (!["committedValid", "committedDecodedInvalid"].includes(result.outcome))
    throw new Error(
      `ingestion ${result.outcome} ${result.rejection ?? result.failure ?? ""}`.trim(),
    );
  return {
    mappingId: result.receipt.futureMapping.mappingId,
    revisionId: result.receipt.futureRevision.revisionId,
    admission: result.receipt.committedAdmission,
  };
}

async function ensureReports(projectId, mappingId, revisionId) {
  for (const report of ["projection", "validation"])
    await step("report", `${report} ${revisionId}`, async () => {
      const url = `${revisionPath(projectId, mappingId, revisionId)}/reports/${report}/ensure`;
      const value = await poll(
        () =>
          api("POST", url, { accept: [200, 202] }).then((reply) => reply.data),
        // A retryable failure (a timeout on a busy host) names its next attempt; keep ensuring until then.
        (data) =>
          data.state === "available" ||
          (data.state === "failed" && !data.nextAttemptAtUtc),
        { seconds: 300, interval: 5000 },
      ).catch(() => ({ state: "failed", status: "still retrying" }));
      return `${report} ${value.state === "available" ? value.status : `failed (${value.status})`}`;
    });
}

const base64 = (text) => Buffer.from(text).toString("base64");
async function preview(projectId, mappingId, revisionId, sample, key) {
  const submitted = await json(
    "POST",
    `${revisionPath(projectId, mappingId, revisionId)}/previews`,
    {
      target: { kind: "all", name: null },
      parameters: sample.parameters ?? [],
      inputs: [
        { content: base64(sample.input), name: null },
        ...(sample.named ?? []).map((input) => ({
          content: base64(input.content),
          name: input.name,
        })),
      ],
      ...(sample.document && { profile: "weft.ferrule-document-preview.v1" }),
    },
    "application/json",
    { headers: { "idempotency-key": key } },
  );
  const run = await poll(
    () =>
      api(
        "GET",
        `/api/v1/organizations/${organization}/previews/${submitted.data.run.runId}`,
      ).then((reply) => reply.data),
    (value) =>
      value.state === "Completed" ||
      value.state === "Canceled" ||
      value.state === "completed" ||
      value.state === "canceled",
  );
  const outputs = [];
  if (run.outcome?.toLowerCase() === "succeeded")
    for (const output of run.outputs) {
      const bytes = await api(
        "GET",
        `/api/v1/organizations/${organization}/previews/${run.runId}/outputs/${output.ordinal}`,
        {
          raw: true,
        },
      ).then((reply) => reply.data);
      outputs.push({
        name: output.ordinal === 0 ? null : output.targetName,
        mediaType: output.mediaType,
        bytes,
      });
    }
  return { run, outputs };
}

// A stale baseline: the same output with its first text value changed, as if recorded before a fix.
function outdated(bytes) {
  const text = bytes.toString("utf8");
  const changed = text.replace(/: "([^"]*)"/, ': "$1 (old)"');
  return Buffer.from(changed === text ? `${text} ` : changed);
}

async function seedMapping(project, spec, environments) {
  const label = `${project.slug}/${spec.slug}`;
  log(`- ${project.name}: ${spec.name}`);
  const revisions = [];
  const ingest = async (revision) => {
    const native = spec.native(revision);
    const filename = `${spec.slug}.native.json`;
    const result = await step("revision", `${label}@${revision}`, async () => {
      const uploaded =
        revision === 1
          ? await upload(
              project.id,
              `${projectPath(project.id)}/mappings`,
              id(`mapping:${label}`),
              native,
              {
                name: spec.name,
                backendId: "ferrule",
                description: spec.description,
                originalFilename: spec.originalFilename ?? filename,
              },
            )
          : await upload(
              project.id,
              `${mappingPath(project.id, spec.mappingId)}/revisions`,
              id(`revision:${label}@${revision}`),
              native,
              {
                originalFilename: filename,
                head: "any",
              },
            );
      spec.mappingId = uploaded.mappingId;
      revisions[revision - 1] = uploaded.revisionId;
      return uploaded.admission;
    });
    if (result)
      await ensureReports(project.id, spec.mappingId, revisions[revision - 1]);
    return result;
  };
  if (!(await ingest(1))) return;
  const capabilities = (
    await api(
      "GET",
      `${mappingPath(project.id, spec.mappingId)}/regression-cases/capabilities`,
    )
  ).data;

  const revisionCount = spec.revisions ?? 1;
  const head = () => revisions[revisionCount - 1];
  const runSamples = async (revision, purpose) => {
    const results = [];
    for (const [index, sample] of (spec.samples ?? []).entries())
      await step("preview", `${label}@${revision} ${sample.name}`, async () => {
        const key = id(`preview:${label}@${revision}:${index}:${purpose}`);
        results[index] = await preview(
          project.id,
          spec.mappingId,
          revisions[revision - 1],
          sample,
          key,
        );
        return String(
          results[index].run.outcome ?? results[index].run.state,
        ).toLowerCase();
      });
    return results;
  };
  const firstOutputs = await runSamples(1, "baseline");
  const cases = [];
  const putCase = async (
    definition,
    revision,
    outputs,
    version = "1",
    name = definition.name,
  ) => {
    const sample = spec.samples[definition.sample];
    const output = outputs[definition.sample];
    if (!output?.outputs?.length)
      throw new Error("its preview produced no output to use as the baseline");
    const document = sample.document;
    const edi = output.outputs[0].mediaType !== "application/json";
    const caseId = id(`case:${label}:${definition.name}`);
    const body = {
      name,
      revisionId: revisions[revision - 1],
      runtime: document ? capabilities.documentRuntime : capabilities.runtime,
      currentDateTime: caseClock,
      target: { kind: "all", name: null },
      limits: capabilities.limits,
      primaryBase64Url: base64Url(sample.input),
      namedInputs: (sample.named ?? []).map((input) => ({
        name: input.name,
        bytesBase64Url: base64Url(input.content),
      })),
      parameters: sample.parameters ?? [],
      comparisonPolicy: edi
        ? "weft.regression.edi-segments.v1"
        : "weft.regression.exact-bytes.v1",
      expected: output.outputs.map((item) => ({
        name: item.name,
        bytesBase64Url: base64Url(
          definition.stale ? outdated(item.bytes) : item.bytes,
        ),
        ...(document && { mediaType: item.mediaType }),
      })),
      ...(edi && {
        comparisonOptions: {
          compareSeparators: false,
          ignoreControlNumbers: true,
          ignoreDates: true,
        },
      }),
    };
    const reply = await json(
      "PUT",
      `${mappingPath(project.id, spec.mappingId)}/regression-cases/${caseId}/versions/${version}`,
      body,
      "application/vnd.weft.regression-case.v1+json",
    );
    cases.push({ ...definition, name, caseId, version, revision });
    return reply.status === 201 ? "created" : "replayed";
  };
  for (const definition of (spec.cases ?? []).filter(
    (item) => item.revision === 1,
  ))
    await step("regression case", `${label} ${definition.name}`, () =>
      putCase(definition, 1, firstOutputs),
    );

  for (let revision = 2; revision <= revisionCount; revision++) {
    if (!(await ingest(revision))) return;
    if (spec.cases?.some((item) => item.revision === revision)) {
      const outputs = await runSamples(revision, "baseline");
      for (const definition of spec.cases.filter(
        (item) => item.revision === revision,
      ))
        await step("regression case", `${label} ${definition.name}`, () =>
          putCase(definition, revision, outputs),
        );
    }
  }
  if (spec.initialName && spec.name !== spec.initialName)
    await rename(project, spec);

  const headOutputs =
    revisionCount > 1 ? await runSamples(revisionCount, "head") : firstOutputs;
  for (const definition of (spec.cases ?? []).filter(
    (item) => item.revision === "head",
  ))
    await step("regression case", `${label} ${definition.name}`, () =>
      putCase(definition, revisionCount, headOutputs),
    );
  // Re-baseline the first-revision cases on the head: their history then has two versions.
  for (const definition of (spec.cases ?? []).filter(
    (item) => item.revision === 1 && revisionCount > 1,
  ))
    await step("regression case", `${label} ${definition.name} v2`, () =>
      putCase(
        definition,
        revisionCount,
        headOutputs,
        "2",
        definition.name.replace(/ \(.*\)$/, ""),
      ),
    );
  spec.headRevisionId = head();

  let evidence;
  for (const item of cases) {
    const runId = id(`regression-run:${label}:${item.name}:${item.version}`);
    const outcome = await step(
      "regression run",
      `${label} ${item.name}`,
      async () => {
        await json(
          "PUT",
          `${mappingPath(project.id, spec.mappingId)}/regression-runs/${runId}`,
          { caseId: item.caseId, version: item.version },
          "application/vnd.weft.regression-run.v1+json",
        );
        const run = await poll(
          () =>
            api(
              "GET",
              `${mappingPath(project.id, spec.mappingId)}/regression-runs/${runId}`,
            ).then((reply) => reply.data),
          (value) => !["queued", "running"].includes(value.run.state),
          { seconds: 300 },
        );
        // passed, failed or notEvaluated; otherwise the run's own end state.
        return run.comparison?.outcome ?? run.run.state;
      },
    );
    if (outcome === "passed" && item.revision === revisionCount && !evidence)
      evidence = runId;
  }

  if (cases.length && revisionCount > 1)
    await step("regression suite recheck", label, () =>
      json(
        "PUT",
        `${projectPath(project.id)}/regression-suites/${id(`suite:${label}`)}`,
        {
          mappingId: spec.mappingId,
          revisionId: head(),
        },
      ).then(() => "saved"),
    );

  if (spec.compile && evidence) {
    const document = spec.samples[0]?.document;
    const environment = document ? environments.document : environments.json;
    if (environment)
      await step("build", label, async () => {
        const buildId = id(`build:${label}`);
        const approvalId = id(`build-approval:${label}`);
        const approval = await json(
          "PUT",
          `${mappingPath(project.id, spec.mappingId)}/build-approvals/${approvalId}`,
          {
            buildId,
            revisionId: head(),
            environmentId: environment,
            environmentVersion: "1",
            regressionEvidenceId: evidence,
          },
          "application/vnd.weft.build-approval.v1+json",
          { accept: [200, 201, 412] },
        );
        // The project's "require passing regression" gate refuses a head whose suite has a failing case.
        if (approval.status === 412)
          return project.automation?.requirePassingRegression
            ? "refused by the regression gate"
            : "refused (preconditionFailed)";
        await json(
          "PUT",
          `${mappingPath(project.id, spec.mappingId)}/build-runs/${buildId}`,
          { approvalId },
          "application/vnd.weft.build-run.v1+json",
        );
        const run = await poll(
          () =>
            api(
              "GET",
              `${mappingPath(project.id, spec.mappingId)}/build-runs/${buildId}`,
            ).then((reply) => reply.data),
          (value) => !["queued", "running"].includes(value.state),
          { seconds: 900, interval: 5000 },
        );
        // succeeded, or failed with the compiler's outcome (such as unsupported), exhausted, ...
        return run.state === "failed" ? `failed (${run.outcome})` : run.state;
      });
  }
}

async function rename(project, spec) {
  await step("metadata edit", `${project.slug}/${spec.slug}`, async () => {
    const url = `${mappingPath(project.id, spec.mappingId)}/metadata`;
    const current = await api("GET", url);
    if (current.data.name === spec.name) return "unchanged";
    await json(
      "PATCH",
      url,
      { name: spec.name, description: spec.description },
      "application/vnd.weft.mapping-metadata-edit.v1+json",
      {
        headers: { "if-match": current.etag },
      },
    );
    return "edited";
  });
}

async function seedProjectSettings(project) {
  await step("notification subscription", project.slug, async () => {
    const options = await api(
      "GET",
      `${projectPath(project.id)}/notification-options`,
    );
    const events = options.data.events?.map((event) => event.id ?? event) ?? [];
    await json("PUT", `${projectPath(project.id)}/notification-subscription`, {
      events,
      inApp: true,
      email: false,
      webhooks: [],
    });
    return "saved";
  });
  if (project.automation)
    await step("automation policy", project.slug, () =>
      json(
        "PUT",
        `${projectPath(project.id)}/automation`,
        project.automation,
      ).then(() => "saved"),
    );
}

async function disableMappingAutomation(project) {
  for (const slug of project.disableAutomation ?? []) {
    const spec = project.mappings.find((item) => item.slug === slug);
    if (spec?.mappingId)
      await step("mapping automation", `${project.slug}/${slug}`, () =>
        json("PUT", `${mappingPath(project.id, spec.mappingId)}/automation`, {
          enabled: false,
        }).then(() => "disabled"),
      );
  }
}

async function buildEnvironments(project) {
  const environments = {};
  await step("build environment", project.slug, async () => {
    const capabilities = (
      await api(
        "GET",
        `${projectPath(project.id)}/build-environment-capabilities`,
      )
    ).data;
    for (const [kind, profile] of [
      ["json", capabilities.profile],
      ["document", capabilities.documentProfile],
    ]) {
      if (!profile) continue;
      const environmentId = id(`build-environment:${project.slug}:${kind}`);
      await json(
        "PUT",
        `${projectPath(project.id)}/build-environments/${environmentId}/versions/1`,
        { profileId: profile.id, profileVersion: profile.version },
        "application/vnd.weft.build-environment.v1+json",
      );
      environments[kind] = environmentId;
    }
    return "saved";
  });
  return environments;
}

async function chat(projectsBySlug) {
  const turns = [];
  for (const item of conversations) {
    await step("chat conversation", item.slug, async () => {
      const conversationId = id(`chat:${item.slug}`);
      const turnId = id(`chat-turn:${item.slug}:1`);
      let base = `/api/v1/organizations/${organization}/chat`;
      let focus = null;
      if (item.mapping) {
        const project = projectsBySlug[item.project];
        const spec = project?.mappings.find(
          (value) => value.slug === item.mapping,
        );
        if (!spec?.headRevisionId)
          throw new Error("its mapping was not seeded");
        base = `${revisionPath(project.id, spec.mappingId, spec.headRevisionId)}/chat`;
        focus = spec;
      }
      const availability = (await api("GET", base)).data;
      // Workspace availability wraps each profile with its payload policy.
      const profile =
        availability.profiles?.[0]?.profile ?? availability.profiles?.[0];
      if (!profile) throw new Error("no chat profile is available");
      await json("POST", `${base}/conversations`, { conversationId });
      await json("POST", `${base}/conversations/${conversationId}/turns`, {
        turnId,
        text: item.text,
        profileId: profile.profileId,
        profileVersion: profile.version,
        selectors: [],
      });
      turns.push({
        item,
        url: `${base}/conversations/${conversationId}`,
        focus,
      });
      return "asked";
    });
  }
  // The local model answers on CPU; wait a bounded time so the history shows replies.
  for (const turn of turns)
    await step("chat reply", turn.item.slug, async () => {
      const conversation = await poll(
        () => api("GET", turn.url).then((reply) => reply.data),
        (value) => {
          const states = JSON.stringify(
            value.turns?.map((item) => item.state) ?? [],
          );
          return !/pending|running|queued|streaming/i.test(states);
        },
        { seconds: 240, interval: 5000 },
      ).catch(() => null);
      return conversation ? "answered" : "still running";
    });
}

// ---- Main.
const context = await api("GET", "/api/v1/catalog/context");
token = context.data.requestToken;
if (context.data.allowedOrganizationId !== organization)
  throw new Error("The session is for a different organization.");
log(`Seeding ${origin} (namespace ${namespace})`);

const local = {
  slug: "local",
  name: "Local project",
  id: defaultProject,
  mappings: [],
};
if (examples) {
  for (const example of ferruleExamples) {
    const file = path.join(examples, example.file);
    if (!fs.existsSync(file)) {
      failures.push(
        `ferrule example ${example.file}: not found under ${examples}`,
      );
      continue;
    }
    const native = fs.readFileSync(file, "utf8");
    const input =
      example.input &&
      fs.readFileSync(path.join(examples, example.input), "utf8");
    local.mappings.push({
      ...example,
      native: () => native,
      samples: input ? [{ name: "Example input", input }] : [],
      cases: input
        ? [{ name: "Example input", sample: 0, revision: "head" }]
        : [],
      compile: Boolean(input),
    });
  }
}

const all = [local, ...projects];
for (const project of projects)
  await step("project", project.slug, () => createProject(project));
for (const project of all) {
  if (!project.id || backendDown) continue;
  // Settings first, so the regression gate applies to builds and the seeded activity notifies.
  if (project !== local) await seedProjectSettings(project);
  const environments = project.mappings.some((spec) => spec.compile)
    ? await buildEnvironments(project)
    : {};
  for (const spec of project.mappings)
    if (!backendDown) await seedMapping(project, spec, environments);
  await disableMappingAutomation(project);
}
if (!skipChat)
  await chat(Object.fromEntries(all.map((project) => [project.slug, project])));

log("\nSeeded:");
for (const [kind, outcomes] of Object.entries(tally))
  log(
    `  ${kind}: ${Object.entries(outcomes)
      .map(([outcome, total]) => `${total} ${outcome}`)
      .join(", ")}`,
  );
if (failures.length) {
  log(`\n${failures.length} step(s) failed:`);
  for (const failure of failures) log(`  ${failure}`);
  if (backendDown)
    log(
      "\nThe server's Ferrule backend stopped answering, so the rest of the catalog was skipped. Restart the server unit and run the seed again; it resumes where it stopped.",
    );
  process.exitCode = 1;
}
