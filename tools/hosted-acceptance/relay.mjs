// Byte relays for the acceptance stack; they never parse or log what they carry.
//   relay.mjs tcp LISTEN=TARGET [LISTEN=TARGET ...]
//     A loopback forwarder in a consumer's network namespace, for endpoints that the
//     product admits only on loopback (the Azurite emulator account, a plain-HTTP model).
//   relay.mjs tls LISTEN=TARGET
//     The platform ingress in front of the edge: terminates TLS for the public host with
//     BRAID_ACCEPTANCE_INGRESS_PEM (HTTP/1.1 only) and forwards to the edge listener.
import { connect, createServer } from "node:net";
import { createServer as createTlsServer } from "node:tls";

const [mode, ...routes] = process.argv.slice(2);
if (
  !["tcp", "tls"].includes(mode) ||
  routes.length === 0 ||
  (mode === "tls" && routes.length !== 1)
)
  throw new Error("Usage: relay.mjs tcp|tls LISTEN=TARGET ...");

function endpoint(text) {
  const separator = text.lastIndexOf(":");
  const port = Number(text.slice(separator + 1));
  if (separator < 1 || !Number.isInteger(port) || port < 1 || port > 65535)
    throw new Error("Endpoints are HOST:PORT.");
  return { host: text.slice(0, separator), port };
}

function pipe(incoming, target) {
  const outgoing = connect(target);
  const close = () => {
    incoming.destroy();
    outgoing.destroy();
  };
  incoming.on("error", close);
  outgoing.on("error", close);
  incoming.pipe(outgoing);
  outgoing.pipe(incoming);
}

let tls;
if (mode === "tls") {
  const bundle = process.env.BRAID_ACCEPTANCE_INGRESS_PEM;
  delete process.env.BRAID_ACCEPTANCE_INGRESS_PEM;
  if (!bundle)
    throw new Error("The TLS relay requires BRAID_ACCEPTANCE_INGRESS_PEM.");
  tls = {
    key: bundle.match(
      /-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]+?-----END [A-Z ]*PRIVATE KEY-----/,
    )[0],
    cert: bundle
      .match(/-----BEGIN CERTIFICATE-----[\s\S]+?-----END CERTIFICATE-----/g)
      .join("\n"),
    ALPNProtocols: ["http/1.1"],
  };
}

const servers = routes.map((route) => {
  const [listen, target] = route.split("=").map(endpoint);
  const server =
    mode === "tls"
      ? createTlsServer(tls, (socket) => pipe(socket, target))
      : createServer((socket) => pipe(socket, target));
  // Failed handshakes and resets are the client's problem; the relay keeps serving.
  server.on("tlsClientError", (_, socket) => socket.destroy());
  server.listen(listen.port, listen.host);
  return server;
});
process.stdout.write(`relay ${mode} listening\n`);
for (const signal of ["SIGTERM", "SIGINT"])
  process.on(signal, () => {
    for (const server of servers) server.close();
    process.exit(0);
  });
