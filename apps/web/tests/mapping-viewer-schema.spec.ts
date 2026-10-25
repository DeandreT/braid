import { expect, test } from "@playwright/test";
import {
  reportSampleArtifacts,
  reportSampleFixtureIndex,
  reportSampleNames,
} from "../../../tools/reports-browser/samples.mjs";
import { controlledData } from "./revision-reports-controlled.fixture";
import {
  parseReportsManifest,
  type ReportsBrowserManifest,
  type Sample,
} from "./revision-reports-real.fixture";

const origin = "http://127.0.0.1:4187";
function manifest(): ReportsBrowserManifest {
  const revision = controlledData().revision;
  const samples = Object.fromEntries(
    reportSampleNames.map((label, index) => {
      const [, digest, byteCount] =
        reportSampleArtifacts[reportSampleFixtureIndex(label)];
      return [
        label,
        {
          revision: {
            ...structuredClone(revision),
            nativeDigest: { algorithm: "sha256", value: digest },
            nativeByteCount: byteCount,
            scope: {
              ...revision.scope,
              mappingId: `30000000-0000-4000-8000-${String(index + 1).padStart(12, "0")}`,
              revisionId: `40000000-0000-4000-8000-${String(index + 1).padStart(12, "0")}`,
            },
          },
        },
      ];
    }),
  ) as Record<Sample, { revision: typeof revision }>;
  return {
    schemaVersion: "weft.reports-browser-fixture.v1",
    fixtureId: "10000000-0000-4000-8000-000000000001",
    source: { commit: "a".repeat(40), tree: "b".repeat(40) },
    readOrigin: origin,
    initialReadProfile: "viewer",
    organizationId: revision.scope.organizationId,
    project: {
      scope: {
        organizationId: revision.scope.organizationId,
        projectId: revision.scope.projectId,
      },
    },
    profiles: Object.fromEntries(
      ["viewer", "runner", "publisher", "administrator"].map((role, index) => [
        role,
        {
          primaryRole: role,
          actorPrincipalId: `20000000-0000-4000-8000-${String(index + 1).padStart(12, "0")}`,
          allowedOrganizationId: revision.scope.organizationId,
        },
      ]),
    ) as ReportsBrowserManifest["profiles"],
    samples,
  };
}
test("mapping viewer owner schema admits the exact twelve typed pinned native samples", () => {
  const value = manifest();
  expect(parseReportsManifest(value, origin)).toEqual(value);
  expect(Object.keys(value.samples)).toEqual(reportSampleNames);
});
test("mapping viewer owner schema rejects omitted, extra and inherited sample labels", () => {
  for (const changed of [
    Object.fromEntries(
      Object.entries(manifest().samples).filter(
        ([name]) => name !== "conditions",
      ),
    ),
    { ...manifest().samples, arbitrary: manifest().samples.basic },
    Object.create(manifest().samples),
  ])
    expect(() =>
      parseReportsManifest({ ...manifest(), samples: changed }, origin),
    ).toThrow(/unexpected public envelope properties/);
});
test("mapping viewer owner schema binds labels to exact native digests and byte counts", () => {
  for (const label of reportSampleNames) {
    const value = manifest();
    value.samples[label].revision.nativeDigest.value = "0".repeat(64);
    expect(() => parseReportsManifest(value, origin)).toThrow(
      /differs from pinned artifact\/project\/backend/,
    );
    const wrongSize = manifest();
    wrongSize.samples[label].revision.nativeByteCount++;
    expect(() => parseReportsManifest(wrongSize, origin)).toThrow(
      /differs from pinned artifact\/project\/backend/,
    );
  }
  const original = manifest();
  const swapped = {
    ...original,
    samples: {
      ...original.samples,
      conditions: original.samples.generated,
      generated: original.samples.conditions,
    },
  };
  expect(() => parseReportsManifest(swapped, origin)).toThrow(
    /differs from pinned artifact\/project\/backend/,
  );
});
test("mapping viewer owner schema refuses shared mapping/revision identities and origin or backend substitution", () => {
  const sharedMapping = manifest();
  sharedMapping.samples.namedInputs.revision.scope.mappingId =
    sharedMapping.samples.controls.revision.scope.mappingId;
  expect(() => parseReportsManifest(sharedMapping, origin)).toThrow(
    /separately HTTP-created mappings/,
  );
  const sharedRevision = manifest();
  sharedRevision.samples.namedInputs.revision.scope.revisionId =
    sharedRevision.samples.controls.revision.scope.revisionId;
  expect(() => parseReportsManifest(sharedRevision, origin)).toThrow(
    /distinct HTTP-created revisions/,
  );
  const wrongBackend = manifest();
  wrongBackend.samples.functions.revision.backendId = "weft.synthetic";
  expect(() => parseReportsManifest(wrongBackend, origin)).toThrow(
    /differs from pinned artifact\/project\/backend/,
  );
  expect(() =>
    parseReportsManifest(manifest(), "http://127.0.0.1:4188"),
  ).toThrow(/identity\/origin differs/);
});
