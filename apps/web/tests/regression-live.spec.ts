import { test, expect, type Page } from "@playwright/test";
import {
  spawn,
  execFileSync,
  type ChildProcessWithoutNullStreams,
} from "node:child_process";
import { readFileSync } from "node:fs";
import { createHash, randomUUID } from "node:crypto";
import type { components } from "../src/api/generated";

type Scope = components["schemas"]["CatalogRevisionScopeV1"];
interface Binding {
  fixtureId: string;
  config: string;
  manifest: string;
  readOrigin: string;
  node: string;
  dotnet: string;
  operator: string;
  privateRoot: string;
}
interface Manifest {
  schemaVersion: string;
  fixtureId: string;
  readOrigin: string;
  revision: Scope;
}
type SavedCase = Pick<
  components["schemas"]["RegressionCaseSavedV1"],
  "caseId" | "version"
>;
type Ack = Record<string, unknown>;

/** Native browser, Linux API/worker; the pipe carries only closed controls and scoped metadata. */
class Owner {
  private child: ChildProcessWithoutNullStreams | null = null;
  private buffer = "";
  private queued: Ack[] = [];
  private pending: {
    resolve: (value: Ack) => void;
    reject: (error: Error) => void;
  } | null = null;
  private closed = false;
  private exitCode: number | null = null;
  private stderrBytes = 0;
  manifest!: Manifest;
  binding!: Binding;
  async start(): Promise<void> {
    this.binding = JSON.parse(
      readFileSync(process.env.WEFT_REGRESSION_BINDINGS!, "utf8"),
    ) as Binding;
    const b = this.binding;
    const path =
      b.dotnet.slice(0, b.dotnet.lastIndexOf("/")) +
      ":" +
      b.node.slice(0, b.node.lastIndexOf("/")) +
      ":/usr/bin:/bin";
    this.child = spawn(
      "wsl.exe",
      [
        "--distribution",
        "Example-Build-Linux",
        "--user",
        "root",
        "--exec",
        "/usr/bin/env",
        "PATH=" + path,
        "DOTNET_ROOT=" + b.dotnet.slice(0, b.dotnet.lastIndexOf("/")),
        "DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=false",
        "HOME=" + b.privateRoot,
        "TMPDIR=" + b.privateRoot,
        "WEFT_REGRESSION_BROWSER_MANIFEST=" + b.manifest,
        "WEFT_REGRESSION_BASE_URL=" + b.readOrigin,
        "/usr/bin/python3",
        "-I",
        "-B",
        b.operator.replace(/operator\.mjs$/, "run_linux.py"),
        b.config,
        b.node,
        b.operator,
      ],
      { stdio: "pipe", windowsHide: true },
    );
    this.child.stdout.on("data", (bytes: Buffer) => {
      this.buffer += bytes.toString("utf8");
      if (this.buffer.length > 8192) {
        this.fail();
        return;
      }
      while (this.buffer.includes("\n")) {
        const end = this.buffer.indexOf("\n");
        const line = this.buffer.slice(0, end);
        this.buffer = this.buffer.slice(end + 1);
        try {
          const value = JSON.parse(line) as Ack;
          if (this.pending) {
            const pending = this.pending;
            this.pending = null;
            pending.resolve(value);
          } else this.queued.push(value);
        } catch {
          this.fail();
        }
      }
    });
    this.child.stderr.on("data", (bytes: Buffer) => {
      this.stderrBytes += bytes.length;
      if (this.stderrBytes > 1048576) this.fail();
    });
    this.child.on("error", () => this.fail());
    this.child.on("close", (code) => {
      this.closed = true;
      this.exitCode = code;
      this.fail();
    });
    const ready = await this.next(300000);
    expect(ready["schemaVersion"]).toBe("weft.regression-browser-ready.v1");
    expect(ready["fixtureId"]).toBe(b.fixtureId);
    const bytes = execFileSync(
      "wsl.exe",
      [
        "--distribution",
        "Example-Build-Linux",
        "--user",
        "root",
        "--exec",
        "/bin/cat",
        b.manifest,
      ],
      { maxBuffer: 65536 },
    );
    expect(createHash("sha256").update(bytes).digest("hex")).toBe(
      ready["manifestSha256"],
    );
    this.manifest = JSON.parse(bytes.toString("utf8")) as Manifest;
    expect(this.manifest.schemaVersion).toBe(
      "weft.regression-browser-fixture.v1",
    );
    expect(this.manifest.fixtureId).toBe(b.fixtureId);
    expect(this.manifest.readOrigin).toBe(b.readOrigin);
    for (const id of Object.values(this.manifest.revision))
      expect(id).toMatch(/^[0-9a-f-]{36}$/);
  }
  async control(
    action: string,
    extra: Record<string, string> = {},
  ): Promise<Ack> {
    const requestNonce = randomUUID();
    this.child!.stdin.write(
      JSON.stringify({
        fixtureId: this.binding.fixtureId,
        requestNonce,
        action,
        ...extra,
      }) + "\n",
    );
    const ack = await this.next(
      action === "shutdown" || action === "restart" ? 130000 : 80000,
    );
    expect(ack["requestNonce"]).toBe(requestNonce);
    expect(ack["action"]).toBe(action);
    expect(ack["schemaVersion"]).toBe("weft.regression-browser-control.v1");
    expect(ack["outcome"]).not.toBe("failed");
    expect(ack["outcome"]).not.toBe("refused");
    return ack;
  }
  async stop(): Promise<void> {
    if (!this.child) return;
    if (this.closed)
      throw new Error(
        "The regression owner exited before its shutdown was acknowledged.",
      );
    try {
      expect((await this.control("shutdown"))["outcome"]).toBe("closed");
    } finally {
      this.child.stdin.end();
    }
    const child = this.child;
    await new Promise<void>((resolve, reject) => {
      if (this.closed) {
        this.exitCode === 0
          ? resolve()
          : reject(new Error("The regression owner failed cleanup."));
        return;
      }
      const timer = setTimeout(
        () => reject(new Error("The regression owner did not settle.")),
        20000,
      );
      child.once("close", (code) => {
        clearTimeout(timer);
        code === 0
          ? resolve()
          : reject(new Error("The regression owner failed cleanup."));
      });
    });
  }
  private fail(): void {
    this.pending?.reject(new Error("The real regression owner failed."));
    this.pending = null;
  }
  private next(milliseconds: number): Promise<Ack> {
    const value = this.queued.shift();
    if (value) return Promise.resolve(value);
    return new Promise((resolve, reject) => {
      if (this.closed || this.pending) {
        reject(new Error("Invalid regression owner state."));
        return;
      }
      const timer = setTimeout(() => {
        this.pending = null;
        reject(new Error("The regression owner deadline elapsed."));
      }, milliseconds);
      this.pending = {
        resolve: (value) => {
          clearTimeout(timer);
          resolve(value);
        },
        reject: (error) => {
          clearTimeout(timer);
          reject(error);
        },
      };
    });
  }
}

const owner = new Owner();
const input = Buffer.from(
  '{"code":"A1","a.b":"dot","nested":{"b":"x"}}',
  "utf8",
);
// Independently authored output with the documented native JSON pretty formatting and final LF.
const expected = Buffer.from(
  '{\n  "code": "A1",\n  "dotted": "dot",\n  "nestedOutput": {\n    "b": "x"\n  }\n}\n',
  "utf8",
);
const region = (page: Page) =>
  page.getByRole("region", { name: "Regression tests", exact: true });
function revisionPath(): string {
  const s = owner.manifest.revision;
  return `${owner.binding.readOrigin}/catalog/organizations/${s.organizationId}/projects/${s.projectId}/mappings/${s.mappingId}/revisions/${s.revisionId}`;
}
async function open(page: Page): Promise<void> {
  await page.goto(revisionPath());
  await region(page)
    .getByRole("button", { name: "Load regression cases", exact: true })
    .click();
  await expect(
    region(page).getByRole("button", { name: "Refresh cases", exact: true }),
  ).toBeEnabled();
}
async function publish(
  page: Page,
  name: string,
  primary = input,
  output = expected,
  version = "1",
): Promise<SavedCase> {
  await region(page)
    .getByRole("button", {
      name: version === "1" ? "New test case" : "Edit as next version",
      exact: true,
    })
    .click();
  await region(page).getByLabel("Fixture name", { exact: true }).fill(name);
  await region(page)
    .getByLabel("Fixed clock (UTC or offset)", { exact: true })
    .fill("2026-10-20T12:00:00.123456789-07:00");
  const files = region(page).locator('input[type="file"]');
  await files.nth(0).setInputFiles({
    name: "primary.json",
    mimeType: "application/json",
    buffer: primary,
  });
  await files.nth(1).setInputFiles({
    name: "expected.json",
    mimeType: "application/json",
    buffer: output,
  });
  const publish = region(page).getByRole("button", {
    name: "Publish fixture version",
    exact: true,
  });
  await expect(publish).toBeEnabled();
  const receipt = page.waitForResponse(
    (response) =>
      response.request().method() === "PUT" &&
      /\/regression-cases\/[a-f0-9-]{36}\/versions\/[1-9][0-9]*$/.test(
        new URL(response.url()).pathname,
      ),
  );
  await publish.click();
  const response = await receipt;
  expect(response.status()).toBe(201);
  const saved = (await response.json()) as SavedCase;
  expect(saved.version).toBe(version);
  await expect(
    region(page).getByRole("heading", {
      name: name + " · Version " + version,
      exact: true,
    }),
  ).toBeVisible();
  return { caseId: saved.caseId, version: saved.version };
}
async function submit(page: Page): Promise<string> {
  const receipt = page.waitForResponse(
    (response) =>
      response.request().method() === "PUT" &&
      /\/regression-runs\/[a-f0-9-]{36}$/.test(
        new URL(response.url()).pathname,
      ),
  );
  await region(page)
    .getByRole("button", { name: "Run saved version", exact: true })
    .click();
  const response = await receipt;
  expect(response.status()).toBe(201);
  return ((await response.json()) as { runId: string }).runId;
}
async function run(
  page: Page,
  comparison: "passed" | "failed" | "notEvaluated",
  expectedCase?: SavedCase,
): Promise<string> {
  const id = await submit(page);
  await owner.control("capture-run", {
    sample: comparison,
    runId: id,
    ...expectedCase,
  });
  await expect(
    region(page)
      .locator("code")
      .filter({ hasText: new RegExp("^" + id + "$") }),
  ).toBeVisible();
  await expect(region(page)).toContainText(
    "Comparison: " +
      (comparison === "notEvaluated"
        ? "Not Evaluated"
        : comparison === "passed"
          ? "Passed"
          : "Failed"),
  );
  return id;
}

test.beforeAll(async () => {
  test.setTimeout(320000);
  await owner.start();
});
test.afterAll(async () => {
  test.setTimeout(150000);
  await owner.stop();
});

test("actual saved cases execute independently, survive restart and respect access revocation", async ({
  page,
}) => {
  test.setTimeout(300000);
  await open(page);
  await publish(page, "Valid before failure");
  const first = await run(page, "passed");
  const malformedCase = await publish(
    page,
    "Malformed input",
    Buffer.from("{"),
  );
  const malformed = await run(page, "notEvaluated");
  await publish(page, "Valid after failure");
  const last = await run(page, "passed");
  await publish(
    page,
    "Wrong expected bytes",
    input,
    expected.subarray(0, expected.length - 1),
  );
  await run(page, "failed");

  await owner.control("pause-worker");
  const canceled = await submit(page);
  await region(page)
    .getByRole("button", { name: "Cancel run", exact: true })
    .click();
  await expect(region(page)).toContainText("Canceled · Attempt 0 of 3");
  await owner.control("capture-run", { sample: "canceled", runId: canceled });
  await owner.control("resume-worker");
  await region(page)
    .getByRole("button", { name: "Refresh cases", exact: true })
    .click();
  await region(page)
    .getByRole("button", { name: "Open Malformed input", exact: true })
    .click();
  const corrected = await publish(
    page,
    "Corrected input",
    input,
    expected,
    "2",
  );
  expect(corrected).toEqual({ caseId: malformedCase.caseId, version: "2" });
  await run(page, "passed", corrected);

  const before = await owner.control("checkpoint");
  expect(before["runs"]).toBe(6);
  expect(before["caseVersions"]).toBe(5);
  expect(before["capturedRuns"]).toBe(6);
  await owner.control("restart");
  await open(page);
  await region(page)
    .getByRole("button", { name: "Load run history", exact: true })
    .click();
  await region(page)
    .getByRole("button", { name: "Open run " + malformed, exact: true })
    .click();
  await expect(region(page)).toContainText("Comparison: Not Evaluated");
  await region(page)
    .getByRole("button", { name: "Open run " + canceled, exact: true })
    .click();
  await expect(region(page)).toContainText("Canceled · Attempt 0 of 3");
  await region(page)
    .getByRole("button", { name: "Open run " + first, exact: true })
    .click();
  await expect(region(page)).toContainText("Comparison: Passed");
  await region(page)
    .getByRole("button", { name: "View actual primary", exact: true })
    .click();
  await expect(region(page).locator("pre")).toHaveText(expected.toString());

  await owner.control("revoke-publisher");
  await region(page)
    .getByRole("button", { name: "Refresh result", exact: true })
    .click();
  await expect(region(page).getByRole("alert")).toBeVisible();
  await expect(region(page).locator("pre")).toHaveCount(0);
  await expect(
    region(page).getByRole("button", { name: /^Download actual/ }),
  ).toHaveCount(0);
  await owner.control("restore-publisher");
  await owner.control("restart-profile", { profile: "viewer" });
  await open(page);
  await expect(region(page)).toContainText("Metadata only");
  await expect(
    region(page).getByRole("button", { name: "New test case", exact: true }),
  ).toHaveCount(0);
  await expect(
    region(page).getByRole("button", { name: "Load run history", exact: true }),
  ).toHaveCount(0);
  await owner.control("restart-profile", { profile: "publisher" });
  await open(page);
  await region(page)
    .getByRole("button", { name: "Load run history", exact: true })
    .click();
  await region(page)
    .getByRole("button", { name: "Open run " + last, exact: true })
    .click();
  await expect(region(page)).toContainText("Comparison: Passed");
  const after = await owner.control("checkpoint");
  expect(after["runs"]).toBe(6);
  expect(after["caseVersions"]).toBe(5);
  expect(after["workerCreates"]).toBe(before["workerCreates"]);
});
