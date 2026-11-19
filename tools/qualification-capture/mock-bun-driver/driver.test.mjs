import { expect, test } from "bun:test";
import {
  openSync,
  closeSync,
  writeFileSync,
  mkdtempSync,
  rmSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { admit, fits, createSupervisor } from "./driver.mjs";

test("regular-file descriptor, exact pin and narrow-field boundaries", () => {
  const fd = openSync(new URL("./mock.abi", import.meta.url), "r");
  try {
    expect(admit(fd)).toHaveLength(64);
  } finally {
    closeSync(fd);
  }
  expect(() => admit(fd)).toThrow();
  for (const n of [-1, 1.5, 2 ** 26, NaN]) expect(fits(n)).toBe(false);
  for (const n of [0, 2 ** 26 - 1]) expect(fits(n)).toBe(true);
  const dir = mkdtempSync(join(tmpdir(), "mock-abi-"));
  try {
    const path = join(dir, "altered");
    writeFileSync(path, "BRAID-MOCK-ABI-v1:u32be->u32be\n");
    const altered = openSync(path, "r");
    try {
      expect(() => admit(altered)).toThrow("mockArtifactMismatch");
    } finally {
      closeSync(altered);
    }
  } finally {
    rmSync(dir, { recursive: true });
  }
});

test("worker roundtrip uses a fixed four-byte representation", async () => {
  expect(await createSupervisor()()).toBe(42);
});

test("main owns cancellation and deadline while the worker withholds its reply", async () => {
  const controller = new AbortController();
  const query = createSupervisor();
  const pending = query({ signal: controller.signal, hold: true });
  await expect(query()).rejects.toThrow("slotUsed");
  setTimeout(() => controller.abort(), 10);
  await expect(pending).rejects.toThrow("cancelled");
  await expect(query()).rejects.toThrow("slotUsed");
  const timed = createSupervisor();
  await expect(timed({ hold: true, timeout: 20 })).rejects.toThrow("deadline");
  await expect(timed()).rejects.toThrow("slotUsed");
});
