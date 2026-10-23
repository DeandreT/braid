// Explicit local provider fixture. It never manufactures application data or citations:
// the application executes search_mapping and verifies its own returned citation targets.
import http from "node:http";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";

const model = process.env.WEFT_ACCEPTANCE_CHAT_MODEL ?? "weft-fixture";
export const createModelFixture = () =>
  http.createServer(async (request, response) => {
    if (request.method === "GET" && request.url === "/api/tags") {
      response.writeHead(200, { "content-type": "application/json" });
      response.end(JSON.stringify({ models: [{ name: model }] }));
      return;
    }
    if (request.method !== "POST" || request.url !== "/api/chat") {
      response.writeHead(404).end();
      return;
    }
    let count = 0;
    const chunks = [];
    try {
      for await (const chunk of request) {
        count += chunk.length;
        if (count > 2 * 1024 * 1024) throw new Error("body bound");
        chunks.push(chunk);
      }
      const body = JSON.parse(Buffer.concat(chunks).toString("utf8"));
      if (!Array.isArray(body.messages) || typeof body.model !== "string")
        throw new Error("request shape");
      console.log('POST "/api/chat"');
      response.writeHead(200, {
        "content-type": "application/x-ndjson",
        "cache-control": "no-store",
      });
      const send = (message, done = false) => {
        if (response.destroyed) return;
        response.write(
          JSON.stringify({
            model: body.model,
            message,
            done,
            ...(done
              ? { done_reason: "stop", prompt_eval_count: 1, eval_count: 1 }
              : {}),
          }) + "\n",
        );
      };
      const lastUser = body.messages.findLast((item) => item.role === "user");
      const lastTool = body.messages.findLastIndex(
        (item) => item.role === "tool",
      );
      const lastUserIndex = body.messages.findLastIndex(
        (item) => item.role === "user",
      );
      if (
        lastTool < lastUserIndex &&
        body.tools?.some((item) => item.function?.name === "search_mapping")
      ) {
        send(
          {
            role: "assistant",
            content: "",
            tool_calls: [
              {
                function: {
                  name: "search_mapping",
                  arguments: { query: "order", limit: 4 },
                },
              },
            ],
          },
          true,
        );
      } else {
        const slow = lastUser?.content?.includes("Describe every output field");
        const parts = slow
          ? Array(60).fill("Reviewing the retained mapping evidence. ")
          : [
              "The mapping search completed. Review the verified citations below.",
            ];
        for (const content of parts) {
          if (response.destroyed) break;
          send({ role: "assistant", content });
          if (slow)
            await new Promise((resolve) => {
              const done = () => {
                clearTimeout(timer);
                response.removeListener("close", done);
                resolve();
              };
              const timer = setTimeout(done, 500);
              response.once("close", done);
            });
        }
        send({ role: "assistant", content: "" }, true);
      }
      response.end();
    } catch {
      if (!response.headersSent) response.writeHead(400).end();
      else response.destroy();
    }
  });
if (
  process.argv[1] &&
  resolve(process.argv[1]) === fileURLToPath(import.meta.url)
) {
  const server = createModelFixture();
  server.listen(11434, "0.0.0.0");
  process.once("SIGTERM", () => server.close());
}
