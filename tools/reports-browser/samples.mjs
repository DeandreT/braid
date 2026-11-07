// Self-authored, source-admitted native artifacts only. No caller-provided paths or bytes.
// Preserve the first five indices: revision append deliberately uses exact at index 1.
export const reportSampleArtifacts = Object.freeze(
  [
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
      "engines/ferrule-host/tests/fixtures/projection-partial.json",
      "fb01b40491fa38c35e65463d63c8d057db975e12b80254fa03ca4a4f289d152e",
      2478,
    ],
    [
      "engines/ferrule-host/tests/fixtures/projection-join-context.json",
      "308ad4c997a12597bf80309eff9ff2a676f110eab7b6f9c218db75517259547d",
      1701,
    ],
    [
      "dotnet/tests/Weft.Tests/Fixtures/validation-cycle.json",
      "54514537939302ceda435e4403da8051935f55c9e684dc2f9dadfb05145f52c2",
      526,
    ],
    [
      "engines/ferrule-host/tests/fixtures/projection-basic.json",
      "b89a104bc196aa83ecc0b028865ce211eaa1ea15d7cb7de80e0800e31e12cbb2",
      6261,
    ],
    [
      "engines/ferrule-host/tests/fixtures/projection-controls.json",
      "49ab8c57239e61876c125d0c3469d20babb41e50cf3b26a2aa98f78eb8e9ba19",
      8612,
    ],
    [
      "engines/ferrule-host/tests/fixtures/projection-functions.json",
      "898dc06ca138da7c50786ddb223f9f11d0770927cab0b8906c900796c6344b5e",
      2308,
    ],
    [
      "engines/ferrule-host/tests/fixtures/projection-generated-source-context.json",
      "34a41f42abeba2bd28caf051063839d8827a3ee99c33fb1759964e7a950136c1",
      776,
    ],
  ].map((artifact) => Object.freeze(artifact)),
);

export const reportSampleFixtureIndices = Object.freeze({
  basic: 0,
  exact: 1,
  partial: 2,
  ambiguous: 3,
  invalid: 4,
  uncached: 0,
  pending: 0,
  conditions: 5,
  controls: 6,
  functions: 7,
  generated: 8,
  namedInputs: 6,
});
export const reportSampleNames = Object.freeze(
  Object.keys(reportSampleFixtureIndices),
);

export function reportSampleFixtureIndex(sample) {
  if (
    typeof sample !== "string" ||
    !Object.hasOwn(reportSampleFixtureIndices, sample)
  )
    throw new Error("Unknown pinned reports sample");
  return reportSampleFixtureIndices[sample];
}
