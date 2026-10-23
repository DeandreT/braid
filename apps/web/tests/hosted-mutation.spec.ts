import { EventEmitter } from "node:events";
import {
  expect,
  test,
  type Page,
  type Request,
  type Response,
} from "@playwright/test";
import { confirmHostedMutation } from "./hosted-mutation.fixture";

const url = "https://fixture.invalid/build-environments/new/versions/1";
const suffix = /\/build-environments\/[^/]+\/versions\/1$/;
const request = { method: () => "PUT", url: () => url } as Request;
const response = {
  request: () => request,
  url: () => url,
  status: () => 201,
} as Response;

function pageEvents() {
  const events = new EventEmitter();
  // The helper only observes Page events; no browser or API is simulated here.
  const page = events as unknown as Page;
  const clean = () => {
    for (const name of ["request", "response", "close"])
      expect(events.listenerCount(name)).toBe(0);
  };
  return { events, page, clean };
}

test("hosted mutation recovers an unsent control and confirms only once", async () => {
  const { events, page, clean } = pageEvents();
  let actions = 0,
    recoveries = 0,
    confirmations = 0;
  const result = await confirmHostedMutation(
    page,
    suffix,
    async () => {
      if (++actions === 1)
        throw new Error("Control hidden after admission changed.");
    },
    {
      recoverUnsentAction: async () => {
        recoveries++;
        return true;
      },
      confirm: async () => {
        confirmations++;
        events.emit("request", request);
        events.emit("response", response);
      },
    },
  );
  expect(result).toBe(response);
  expect([actions, recoveries, confirmations]).toEqual([2, 1, 1]);
  clean();
});

test("hosted mutation never retries an action after the request started", async () => {
  const { events, page, clean } = pageEvents();
  let actions = 0,
    recoveries = 0;
  await expect(
    confirmHostedMutation(
      page,
      suffix,
      async () => {
        actions++;
        events.emit("request", request);
        throw new Error("Action outcome unknown.");
      },
      {
        recoverUnsentAction: async () => {
          recoveries++;
          return true;
        },
        confirm: async () => {
          throw new Error("Must not confirm again.");
        },
      },
    ),
  ).rejects.toThrow("Action outcome unknown.");
  expect([actions, recoveries]).toEqual([1, 0]);
  clean();
});

test("hosted mutation does not retry a missing response", async () => {
  const { events, page, clean } = pageEvents();
  let actions = 0,
    recoveries = 0;
  await expect(
    confirmHostedMutation(
      page,
      suffix,
      async () => {
        actions++;
      },
      {
        recoverUnsentAction: async () => {
          recoveries++;
          return true;
        },
        confirm: async () => {
          events.emit("request", request);
        },
        responseTimeoutMs: 10,
      },
    ),
  ).rejects.toThrow("No matching mutation response");
  expect([actions, recoveries]).toEqual([1, 0]);
  clean();
});

test("hosted mutation treats an observed response as a started request", async () => {
  const { events, page, clean } = pageEvents();
  let recoveries = 0;
  await expect(
    confirmHostedMutation(
      page,
      suffix,
      async () => {
        events.emit("response", response);
        throw new Error("Control action failed after a response.");
      },
      {
        recoverUnsentAction: async () => {
          recoveries++;
          return true;
        },
        confirm: async () => {
          throw new Error("Must not confirm again.");
        },
      },
    ),
  ).rejects.toThrow("Control action failed after a response.");
  expect(recoveries).toBe(0);
  clean();
});

test("hosted mutation rechecks request activity after asynchronous recovery", async () => {
  const { events, page, clean } = pageEvents();
  let actions = 0;
  await expect(
    confirmHostedMutation(
      page,
      suffix,
      async () => {
        actions++;
        throw new Error("Control became unavailable.");
      },
      {
        recoverUnsentAction: async () => {
          events.emit("request", request);
          return true;
        },
        confirm: async () => {
          throw new Error("Must not confirm again.");
        },
      },
    ),
  ).rejects.toThrow("Control became unavailable.");
  expect(actions).toBe(1);
  clean();
});

test("hosted mutation bounds unsent recovery and removes every listener", async () => {
  const { page, clean } = pageEvents();
  let actions = 0,
    recoveries = 0;
  await expect(
    confirmHostedMutation(
      page,
      suffix,
      async () => {
        actions++;
        throw new Error("Still hidden.");
      },
      {
        recoverUnsentAction: async () => {
          recoveries++;
          return true;
        },
        confirm: async () => {
          throw new Error("Must not confirm.");
        },
      },
    ),
  ).rejects.toThrow("Still hidden.");
  expect([actions, recoveries]).toEqual([3, 2]);
  clean();
});

test("hosted mutation cannot turn a refused recovery into a retry", async () => {
  const { page, clean } = pageEvents();
  let actions = 0;
  await expect(
    confirmHostedMutation(
      page,
      suffix,
      async () => {
        actions++;
        throw new Error("Admission refused.");
      },
      {
        recoverUnsentAction: async () => false,
        confirm: async () => {
          throw new Error("Must not confirm.");
        },
      },
    ),
  ).rejects.toThrow("Admission refused.");
  expect(actions).toBe(1);
  clean();
});
