// Creates the private Blob container in the acceptance stack's Azurite emulator, the step a
// deployment template performs for a real account. Uses the emulator's published development
// account (Shared Key); no public access is requested. An existing container is left unchanged.
//   blob-container.mjs http://azurite:10000/devstoreaccount1 CONTAINER
import { createHmac } from "node:crypto";

const [endpoint, container] = process.argv.slice(2);
if (
  !endpoint ||
  !/^[a-z0-9](?:[a-z0-9]|-(?=[a-z0-9])){2,62}$/.test(container ?? "")
)
  throw new Error("Usage: blob-container.mjs ENDPOINT CONTAINER");
const account = "devstoreaccount1";
// The Azurite emulator's published development key; it protects no deployed data.
const key = Buffer.from(
  "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==",
  "base64",
);
const url = new URL(
  `${endpoint.replace(/\/$/, "")}/${container}?restype=container`,
);
const headers = {
  "x-ms-date": new Date().toUTCString(),
  "x-ms-version": "2021-08-06",
};
const canonicalHeaders = Object.keys(headers)
  .sort()
  .map((name) => `${name}:${headers[name]}\n`)
  .join("");
const resource = `/${account}${url.pathname}\nrestype:container`;
const stringToSign = `PUT\n\n\n\n\n\n\n\n\n\n\n\n${canonicalHeaders}${resource}`;
const signature = createHmac("sha256", key)
  .update(stringToSign, "utf8")
  .digest("base64");

for (let attempt = 1; ; attempt++) {
  try {
    const response = await fetch(url, {
      method: "PUT",
      headers: {
        ...headers,
        Authorization: `SharedKey ${account}:${signature}`,
        "Content-Length": "0",
      },
    });
    if (response.status === 201 || response.status === 409) {
      process.stdout.write(
        response.status === 201
          ? "BlobContainerCreated\n"
          : "BlobContainerRetained\n",
      );
      process.exit(0);
    }
    throw new Error(`Container creation returned ${response.status}.`);
  } catch (error) {
    if (attempt === 30) throw error;
    await new Promise((resolve) => setTimeout(resolve, 1000));
  }
}
