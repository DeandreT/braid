import { EventEmitter } from "node:events";
import { expect, test, type Page, type Request } from "@playwright/test";
import { readHostedState } from "./hosted-read.fixture";

function fixture() {
  const events = new EventEmitter();
  const page = events as unknown as Page;
  const request = (method: string) =>
    events.emit("request", { method: () => method } as Request);
  const clean = () => expect(events.listenerCount("request")).toBe(0);
  return { page, request, clean };
}

test("hosted read succeeds without invoking recovery", async () => {
  const { page, request, clean } = fixture();
  await readHostedState(
    page,
    async () => {
      request("GET");
    },
    {
      recover: async () => {
        throw new Error("Must not recover a successful read.");
      },
    },
  );
  clean();
});

test("hosted read retries a recognized failure with the same retained identity", async () => {
  const { page, request, clean } = fixture();
  const identity = "original-case-version";
  const reads: string[] = [];
  let recoveries = 0,
    recorded = 0;
  await readHostedState(
    page,
    async () => {
      reads.push(identity);
      request("GET");
      if (reads.length === 1) throw new Error("Access read unavailable.");
    },
    {
      recover: async () => {
        recoveries++;
        request("GET");
        return true;
      },
      onRecovery: () => {
        recorded++;
      },
    },
  );
  expect(reads).toEqual([identity, identity]);
  expect([recoveries, recorded]).toEqual([1, 1]);
  clean();
});

test("hosted read does not retry an unrecognized failure", async () => {
  const { page, clean } = fixture();
  let reads = 0;
  await expect(
    readHostedState(
      page,
      async () => {
        reads++;
        throw new Error("Different failure.");
      },
      { recover: async () => false },
    ),
  ).rejects.toThrow("Different failure.");
  expect(reads).toBe(1);
  clean();
});

for (const method of ["POST", "PUT", "PATCH", "DELETE"]) {
  test(`hosted read never retries after observing ${method}`, async () => {
    const { page, request, clean } = fixture();
    let reads = 0,
      recoveries = 0;
    await expect(
      readHostedState(
        page,
        async () => {
          reads++;
          request(method);
          throw new Error("Mutation started.");
        },
        {
          recover: async () => {
            recoveries++;
            return true;
          },
        },
      ),
    ).rejects.toThrow("Mutation started.");
    expect([reads, recoveries]).toEqual([1, 0]);
    clean();
  });
}

test("hosted read checks for mutation activity during asynchronous recovery", async () => {
  const { page, request, clean } = fixture();
  let reads = 0;
  await expect(
    readHostedState(
      page,
      async () => {
        reads++;
        throw new Error("Read unavailable.");
      },
      {
        recover: async () => {
          await Promise.resolve();
          request("PUT");
          return true;
        },
      },
    ),
  ).rejects.toThrow("Read unavailable.");
  expect(reads).toBe(1);
  clean();
});

test("hosted read refuses a mutation even when its action succeeds", async () => {
  const { page, request, clean } = fixture();
  await expect(
    readHostedState(
      page,
      async () => {
        request("POST");
      },
      {
        recover: async () => true,
      },
    ),
  ).rejects.toThrow("unexpectedly sent a mutation");
  clean();
});

test("hosted read bounds recovery and removes its listener", async () => {
  const { page, clean } = fixture();
  let reads = 0,
    recoveries = 0;
  await expect(
    readHostedState(
      page,
      async () => {
        reads++;
        throw new Error("Read unavailable.");
      },
      {
        recover: async () => {
          recoveries++;
          return true;
        },
      },
    ),
  ).rejects.toThrow("Read unavailable.");
  expect([reads, recoveries]).toEqual([4, 3]);
  clean();
});

test("hosted read propagates recovery failure without another read", async () => {
  const { page, clean } = fixture();
  let reads = 0;
  await expect(
    readHostedState(
      page,
      async () => {
        reads++;
        throw new Error("Read unavailable.");
      },
      {
        recover: async () => {
          throw new Error("Recovery failed.");
        },
      },
    ),
  ).rejects.toThrow("Recovery failed.");
  expect(reads).toBe(1);
  clean();
});

test("hosted read can disable recovery explicitly", async () => {
  const { page, clean } = fixture();
  await expect(
    readHostedState(
      page,
      async () => {
        throw new Error("Read unavailable.");
      },
      {
        maximumRecoveries: 0,
        recover: async () => {
          throw new Error("Must not recover.");
        },
      },
    ),
  ).rejects.toThrow("Read unavailable.");
  clean();
});
