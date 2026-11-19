import { readFile } from "node:fs/promises";
import openapiTS from "openapi-typescript";

// OpenAPI documents of hosts without a generated client. Each host's build writes its
// document; this check keeps every document a valid OpenAPI 3.1 description whose
// references resolve, whose operations are identified and answered, and whose
// security schemes are the ones the host enforces.
const specifications = [
  {
    file: "braid-endpoint-v1.json",
    schemes: { machineToken: "oauth2" },
  },
  {
    file: "ferrule-service-v1.json",
    schemes: { pinnedClientCertificate: "mutualTLS" },
  },
];
const methods = ["get", "put", "post", "delete", "options", "head", "patch"];

if (process.argv.length > 2) {
  console.error(
    "Usage: bun tools/contracts-generator/check-specifications.mjs",
  );
  process.exit(1);
}

const failures = [];
for (const { file, schemes } of specifications) {
  const url = new URL(`../../contracts/${file}`, import.meta.url);
  const fail = (message) => failures.push(`contracts/${file}: ${message}`);
  const document = JSON.parse(await readFile(url, "utf8"));
  if (!/^3\.1\.\d+$/.test(document.openapi ?? "")) {
    fail(`expected OpenAPI 3.1, found ${document.openapi}`);
  }
  try {
    // Parses the document and resolves every reference.
    await openapiTS(url, { silent: true });
  } catch (error) {
    fail(`invalid OpenAPI document: ${error.message}`);
  }

  const declared = document.components?.securitySchemes ?? {};
  for (const [name, type] of Object.entries(schemes)) {
    if (declared[name]?.type !== type) {
      fail(`security scheme ${name} must be ${type}`);
    }
  }
  for (const name of Object.keys(declared)) {
    if (!(name in schemes)) fail(`unexpected security scheme ${name}`);
  }

  const operationIds = new Set();
  for (const [path, item] of Object.entries(document.paths ?? {})) {
    const templated = [...path.matchAll(/\{([^}]+)\}/g)].map(
      (match) => match[1],
    );
    for (const method of methods.filter((name) => name in item)) {
      const operation = item[method];
      const where = `${method.toUpperCase()} ${path}`;
      if (!operation.operationId || operationIds.has(operation.operationId)) {
        fail(`${where} needs a unique operationId`);
      }
      operationIds.add(operation.operationId);
      const parameters = (operation.parameters ?? []).filter(
        (parameter) => parameter.in === "path",
      );
      for (const name of templated) {
        if (!parameters.some((parameter) => parameter.name === name)) {
          fail(`${where} does not define path parameter ${name}`);
        }
      }
      const responses = Object.entries(operation.responses ?? {});
      if (responses.length === 0) fail(`${where} has no responses`);
      for (const [status, response] of responses) {
        if (!/^[1-5]\d\d$/.test(status) || !response.description) {
          fail(`${where} ${status} needs an explicit status and description`);
        }
      }
      for (const requirement of operation.security ?? document.security ?? []) {
        for (const name of Object.keys(requirement)) {
          if (!(name in declared))
            fail(`${where} requires unknown scheme ${name}`);
        }
      }
    }
  }
}

if (failures.length > 0) {
  for (const failure of failures) console.error(failure);
  process.exitCode = 1;
} else {
  console.log(
    `OpenAPI documents are valid: ${specifications.map(({ file }) => `contracts/${file}`).join(", ")}.`,
  );
}
