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
