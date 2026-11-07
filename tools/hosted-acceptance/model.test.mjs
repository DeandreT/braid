import assert from "node:assert/strict";
import test from "node:test";
import { createModelFixture } from "./model.mjs";

test("the provider fixture requests a real tool before giving its bounded answer", async (t) => {
  const server = createModelFixture();
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  t.after(() => new Promise((resolve) => server.close(resolve)));
  const origin = `http://127.0.0.1:${server.address().port}`;
  const send = async (body) => {
    const response = await fetch(`${origin}/api/chat`, {
      method: "POST",
      body: JSON.stringify(body),
    });
    assert.equal(response.status, 200);
    return (await response.text()).trim().split("\n").map(JSON.parse);
  };
  const body = {
    model: "fixture-model",
    messages: [{ role: "user", content: "Where does order come from?" }],
    tools: [{ function: { name: "search_mapping" } }],
  };
  const first = await send(body);
  assert.equal(first.length, 1);
  assert.equal(first[0].done, true);
  assert.equal(first[0].message.tool_calls[0].function.name, "search_mapping");
  assert.deepEqual(first[0].message.tool_calls[0].function.arguments, {
    query: "order",
    limit: 4,
  });
  body.messages.push({
    role: "tool",
    content: "The application owns this result.",
  });
  const answer = await send(body);
  assert.equal(answer.at(-1).done, true);
  assert.equal(answer[0].model, "fixture-model");
  assert.equal(answer[0].message.tool_calls, undefined);
  assert.match(answer[0].message.content, /verified citations/);
});

test("malformed provider requests are refused", async (t) => {
  const server = createModelFixture();
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  t.after(() => new Promise((resolve) => server.close(resolve)));
  const response = await fetch(
    `http://127.0.0.1:${server.address().port}/api/chat`,
    {
      method: "POST",
      body: "{}",
    },
  );
  assert.equal(response.status, 400);
});

test("workspace search calls only the offered catalog tool", async (t) => {
  const server = createModelFixture();
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  t.after(() => new Promise((resolve) => server.close(resolve)));
  const response = await fetch(
    `http://127.0.0.1:${server.address().port}/api/chat`,
    {
      method: "POST",
      body: JSON.stringify({
        model: "fixture-model",
        messages: [{ role: "user", content: "Find the order mappings." }],
        tools: [
          { function: { name: "search_catalog" } },
          { function: { name: "search_mapping" } },
        ],
      }),
    },
  );
  const answer = JSON.parse((await response.text()).trim());
  assert.equal(answer.message.tool_calls[0].function.name, "search_catalog");
  assert.deepEqual(answer.message.tool_calls[0].function.arguments, {
    query: "order",
    limit: 4,
  });
});

test("payload requests are refused unless the payload tool is offered and acknowledge actual UTF-8 bytes", async (t) => {
  const observed = [];
  const server = createModelFixture((entry) => observed.push(entry));
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  t.after(() => new Promise((resolve) => server.close(resolve)));
  const runId = "01234567-89ab-cdef-0123-456789abcdef";
  const send = async (tools, messages) => {
    const response = await fetch(
      `http://127.0.0.1:${server.address().port}/api/chat`,
      {
        method: "POST",
        body: JSON.stringify({ model: "fixture-model", tools, messages }),
      },
    );
    assert.equal(response.status, 200);
    return (await response.text()).trim().split("\n").map(JSON.parse);
  };
  const messages = [{ role: "user", content: `Read preview input ${runId}.` }];
  const denied = await send(
    [{ function: { name: "search_catalog" } }],
    messages,
  );
  assert.equal(denied[0].message.content, "Payload tools were not offered.");
  assert.equal(denied[0].message.tool_calls, undefined);
  assert.deepEqual(observed.at(-1), {
    previewRequest: true,
    offeredTools: ["search_catalog"],
    afterToolReply: false,
    decision: "payload-not-offered",
  });
  const tools = [{ function: { name: "get_preview_input" } }];
  const called = await send(tools, messages);
  assert.deepEqual(called[0].message.tool_calls[0].function, {
    name: "get_preview_input",
    arguments: { runId, ordinal: 0 },
  });
  assert.equal(observed.at(-1).decision, "call:get_preview_input");
  messages.push({
    role: "tool",
    content: JSON.stringify({
      outcome: "available",
      data: { text: "é", sharedBytes: 2 },
    }),
  });
  const read = await send(tools, messages);
  assert.equal(
    read[0].message.content,
    "Received 2 sample bytes through the authorized tool.",
  );
  assert.equal(observed.at(-1).afterToolReply, true);
  assert.equal(observed.at(-1).decision, "inspect-payload-result");
  assert.ok(!JSON.stringify(observed).includes("é"));
  messages.at(-1).content = JSON.stringify({ outcome: "notPermitted" });
  assert.equal(
    (await send(tools, messages))[0].message.content,
    "Sample data was not available.",
  );
});
